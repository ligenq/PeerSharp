[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TorrentPath,
    [Parameter(Mandatory)][string]$Interface,
    [string]$OutputDirectory = 'C:\temp\peersharp-live-audit',
    [string]$WiresharkDirectory = 'C:\Program Files\Wireshark',
    [ValidateSet('Tcp', 'Utp')][string]$Transport = 'Tcp',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path -Parent $PSScriptRoot
$torrent = (Resolve-Path -LiteralPath $TorrentPath).Path
$run = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run -Force | Out-Null
if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'tests\PeerSharp.Tests\PeerSharp.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Live audit build failed.' }
}
$savedRoot = $env:PEERSHARP_PACKET_AUDIT_DIR
$savedTorrent = $env:PEERSHARP_SOAK_TORRENT
$savedTransport = $env:PEERSHARP_PACKET_AUDIT_TRANSPORT
$capture = $null
$runner = $null
$auditMutex = [Threading.Mutex]::new($false, 'Local\PeerSharpTrafficAudit')
try { $auditOwnsMutex = $auditMutex.WaitOne(0) }
catch [Threading.AbandonedMutexException] { $auditOwnsMutex = $true }
if (-not $auditOwnsMutex) {
    $auditMutex.Dispose()
    throw 'Another traffic audit is running. Run captures sequentially.'
}
try {
    foreach ($tool in @('dumpcap.exe', 'tshark.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $WiresharkDirectory $tool))) { throw "Capture tool not found: $tool" }
    }
    $env:PEERSHARP_PACKET_AUDIT_DIR = $run
    $env:PEERSHARP_SOAK_TORRENT = $torrent
    $env:PEERSHARP_PACKET_AUDIT_TRANSPORT = $Transport
    $runnerPath = Join-Path $repo 'tests\PeerSharp.Tests\bin\Release\net10.0\PeerSharp.Tests.dll'
    $runner = Start-Process -FilePath 'dotnet' -ArgumentList @(
        ('"' + $runnerPath + '"'), '-noLogo', '-class', '*RealSwarmPacketAuditTests',
        '-result-xml', ('"' + (Join-Path $run 'tests.xml') + '"')
    ) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'tests.log') -RedirectStandardError (Join-Path $run 'tests-error.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(100)
    while (-not (Test-Path -LiteralPath (Join-Path $run 'ready.json'))) {
        if ($runner.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw "Discovery failed; inspect $run" }
        Start-Sleep -Milliseconds 200
    }
    $filter = Get-Content -LiteralPath (Join-Path $run 'capture-filter.txt') -Raw
    $captureLog = Join-Path $run 'capture.log'
    $capture = Start-Process -FilePath (Join-Path $WiresharkDirectory 'dumpcap.exe') -ArgumentList @(
        '-i', ('"' + $Interface + '"'), '-f', ('"' + $filter + '"'), '-a', 'duration:105',
        '-a', 'filesize:200000', '-w', ('"' + (Join-Path $run 'traffic.pcapng') + '"'), '-q'
    ) -WindowStyle Hidden -PassThru -RedirectStandardError $captureLog
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $captureLog) -or -not (Select-String -LiteralPath $captureLog -Pattern '^File:' -Quiet)) {
        if ($capture.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw "Capture failed; inspect $captureLog" }
        Start-Sleep -Milliseconds 50
    }
    Set-Content -LiteralPath (Join-Path $run 'capture-ready') -Value ([DateTime]::UtcNow.ToString('O'))
    while (-not $runner.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -gt $deadline.AddMinutes(4)) { throw 'Download runner exceeded its time bound.' }
    }
    $runner.Refresh()
    while (-not $capture.WaitForExit(1000)) { }
    $capture.Refresh()
    Write-Host "Real-download artifacts: $run"
    if ($runner.ExitCode -ne 0 -or $capture.ExitCode -ne 0) { throw "Audit failed; inspect $run" }
    [xml]$testReport = Get-Content -LiteralPath (Join-Path $run 'tests.xml') -Raw
    if ([int]$testReport.assemblies.assembly.passed -ne 1) { throw 'The live audit did not run its download test.' }
    $captureText = Get-Content -LiteralPath $captureLog -Raw
    $packetMatch = [regex]::Match($captureText, 'Packets captured:\s*(\d+)')
    $dropMatch = [regex]::Match($captureText, ':\s*\d+/(\d+)\s*\(pcap:')
    $coversDownload = (Get-Item -LiteralPath (Join-Path $run 'download-stopped')).LastWriteTimeUtc -le $capture.ExitTime.ToUniversalTime()
    $captureHealthy = $packetMatch.Success -and [long]$packetMatch.Groups[1].Value -gt 0 -and
        $dropMatch.Success -and [long]$dropMatch.Groups[1].Value -eq 0 -and $coversDownload
    [ordered]@{
        captureHealthy = $captureHealthy; interface = $Interface; filter = $filter; transport = $Transport
        packets = if ($packetMatch.Success) { [long]$packetMatch.Groups[1].Value } else { $null }
        dropped = if ($dropMatch.Success) { [long]$dropMatch.Groups[1].Value } else { $null }
        coversDownload = $coversDownload
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'capture-summary.json')
    if (-not $captureHealthy) { throw "Capture coverage/health check failed; inspect $run" }
}
finally {
    $env:PEERSHARP_PACKET_AUDIT_DIR = $savedRoot
    $env:PEERSHARP_SOAK_TORRENT = $savedTorrent
    $env:PEERSHARP_PACKET_AUDIT_TRANSPORT = $savedTransport
    if ($runner -and -not $runner.HasExited) { $runner.Kill() }
    if ($capture -and -not $capture.HasExited) { $capture.Kill() }
    if ($runner) { $runner.Dispose() }
    if ($capture) { $capture.Dispose() }
    $auditMutex.ReleaseMutex()
    $auditMutex.Dispose()
}
