[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidateRange(1, 100)][int]$Repeat = 1,
    [switch]$Extended,
    [switch]$NoBuild,
    [switch]$CapturePackets,
    [ValidateRange(5, 120)][int]$CaptureSeconds = 15,
    [string]$WiresharkDirectory = 'C:\Program Files\Wireshark'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($CapturePackets -and $Extended) {
    throw 'Run -CapturePackets and -Extended separately. Packet capture covers the six focused cases.'
}
$auditRepo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $auditRepo 'artifacts\traffic-audit' }
$auditRun = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) (
    'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $auditRun -Force | Out-Null
$auditOriginalOutput = $env:PEERSHARP_WIRE_AUDIT_DIR
$auditResults = @()
$auditMutex = [Threading.Mutex]::new($false, 'Local\PeerSharpTrafficAudit')
try { $auditOwnsMutex = $auditMutex.WaitOne(0) }
catch [Threading.AbandonedMutexException] { $auditOwnsMutex = $true }
if (-not $auditOwnsMutex) {
    $auditMutex.Dispose()
    throw 'Another traffic audit is running. Run audits sequentially to keep packet captures isolated.'
}
Push-Location $auditRepo
try {
    $auditDumpcap = Join-Path $WiresharkDirectory 'dumpcap.exe'
    $auditTshark = Join-Path $WiresharkDirectory 'tshark.exe'
    if ($CapturePackets) {
        foreach ($auditTool in @($auditDumpcap, $auditTshark)) {
            if (-not (Test-Path -LiteralPath $auditTool)) { throw "Capture tool not found: $auditTool" }
        }
        $auditInterfaces = & $auditDumpcap -D 2>&1
        if ($LASTEXITCODE -ne 0 -or -not ($auditInterfaces -match 'NPF_Loopback')) {
            throw 'Npcap loopback capture is unavailable. Install Npcap with loopback support.'
        }
    }
    if (-not $NoBuild) {
        & dotnet build tests\PeerSharp.Tests\PeerSharp.Tests.csproj -c Release 2>&1 |
            Tee-Object -FilePath (Join-Path $auditRun 'build.log') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Traffic audit build failed.' }
    }
    $auditRunner = Join-Path $auditRepo 'tests\PeerSharp.Tests\bin\Release\net10.0\PeerSharp.Tests.dll'
    if (-not (Test-Path -LiteralPath $auditRunner)) { throw "Test runner not found: $auditRunner" }
    for ($auditIteration = 1; $auditIteration -le $Repeat; $auditIteration++) {
        $auditIterationPath = Join-Path $auditRun "iteration-$auditIteration"
        New-Item -ItemType Directory -Path $auditIterationPath -Force | Out-Null
        $env:PEERSHARP_WIRE_AUDIT_DIR = $auditIterationPath
        $auditCapture = $null
        $auditPacketSummary = $null
        $auditCaptureEndedEarly = $false
        $auditPcap = Join-Path $auditIterationPath 'traffic.pcapng'
        $auditCaptureLog = Join-Path $auditIterationPath 'capture.log'
        if ($CapturePackets) {
            # Capture only the fixture's dedicated loopback address. Autostop also bounds capture
            # lifetime if this script is interrupted; no physical or VPN interface is monitored.
            $auditCapture = Start-Process -FilePath $auditDumpcap -ArgumentList @(
                '-i', '\Device\NPF_Loopback', '-f', '"tcp and host 127.0.0.42"',
                '-a', "duration:$CaptureSeconds", '-w', ('"' + $auditPcap + '"'), '-q'
            ) -WindowStyle Hidden -PassThru -RedirectStandardError $auditCaptureLog
            $auditReadyDeadline = [DateTime]::UtcNow.AddSeconds(10)
            while (-not (Test-Path -LiteralPath $auditCaptureLog) -or
                -not (Select-String -LiteralPath $auditCaptureLog -Pattern '^File:' -Quiet)) {
                if ($auditCapture.HasExited -or [DateTime]::UtcNow -ge $auditReadyDeadline) {
                    throw "Packet capture did not become ready; inspect $auditCaptureLog"
                }
                Start-Sleep -Milliseconds 50
            }
        }
        $auditXml = Join-Path $auditIterationPath 'tests.xml'
        $auditArguments = @($auditRunner, '-noLogo', '-result-xml', $auditXml, '-class', '*SyntheticPeerTrafficAuditTests')
        if ($Extended) { $auditArguments += @('-class', 'PeerSharp.Tests.Integration.Synthetic.*', '-class', '*WireSequenceTests') }
        & dotnet @auditArguments 2>&1 | Tee-Object -FilePath (Join-Path $auditIterationPath 'tests.log') | Out-Host
        $auditExit = $LASTEXITCODE
        if ($auditCapture) {
            $auditCaptureEndedEarly = $auditCapture.HasExited
            if (-not $auditCapture.WaitForExit(($CaptureSeconds + 5) * 1000)) {
                $auditCapture.Kill()
                throw "Packet capture did not stop; inspect $auditCaptureLog"
            }
            $auditCapture.Refresh()
            $auditFields = @('frame.number', 'tcp.stream', 'ip.src', 'tcp.srcport', 'ip.dst', 'tcp.dstport',
                'tcp.len', 'bittorrent.info_hash', 'bittorrent.reserved', 'bittorrent.msg.type',
                'bittorrent.piece.index', 'bittorrent.piece.begin', 'bittorrent.piece.length',
                'tcp.analysis.retransmission', 'tcp.analysis.lost_segment', 'tcp.flags.reset', '_ws.expert.message')
            $auditDecodeArguments = @('-r', $auditPcap, '-n', '-2', '-o', 'tcp.desegment_tcp_streams:TRUE',
                '-o', 'bittorrent.desegment:TRUE', '-T', 'fields', '-E', 'header=y', '-E', 'separator=/t', '-E', 'quote=d')
            foreach ($auditField in $auditFields) { $auditDecodeArguments += @('-e', $auditField) }
            $auditPacketLines = @(& $auditTshark @auditDecodeArguments 2> (Join-Path $auditIterationPath 'decode.log'))
            if ($LASTEXITCODE -ne 0) { throw 'TShark packet decoding failed.' }
            $auditPacketLines | Set-Content -LiteralPath (Join-Path $auditIterationPath 'packets.tsv')
            $auditPackets = @($auditPacketLines | ConvertFrom-Csv -Delimiter "`t")
            $auditMalformed = @(& $auditTshark -r $auditPcap -n -Y '_ws.malformed' -T fields -e frame.number)
            if ($LASTEXITCODE -ne 0) { throw 'TShark malformed-packet analysis failed.' }
            $auditDropped = $null
            $auditDropMatch = [regex]::Match((Get-Content -LiteralPath $auditCaptureLog -Raw), ':\s*\d+/(\d+)\s*\(pcap:')
            if ($auditDropMatch.Success) { $auditDropped = [int]$auditDropMatch.Groups[1].Value }
            $auditPacketSummary = [ordered]@{
                file = $auditPcap; filter = 'tcp and host 127.0.0.42'; packets = $auditPackets.Count
                streams = @($auditPackets.'tcp.stream' | Sort-Object -Unique).Count
                handshakes = @($auditPackets | Where-Object { $_.'bittorrent.info_hash' }).Count
                dropped = $auditDropped; endedBeforeTests = $auditCaptureEndedEarly
                malformedPackets = $auditMalformed; captureExitCode = $auditCapture.ExitCode
                retransmissions = @($auditPackets | Where-Object { $_.'tcp.analysis.retransmission' }).Count
                missingSegments = @($auditPackets | Where-Object { $_.'tcp.analysis.lost_segment' }).Count
                resets = @($auditPackets | Where-Object { $_.'tcp.flags.reset' -eq 'True' }).Count
            }
            $auditPacketSummary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $auditIterationPath 'packets-summary.json')
        }
        $auditTotal = 0
        if (Test-Path -LiteralPath $auditXml) {
            [xml]$auditReport = Get-Content -LiteralPath $auditXml -Raw
            $auditTotal = ($auditReport.assemblies.assembly | Measure-Object -Property total -Sum).Sum
        }
        # The runner can return zero when a filter selects nothing. That is not an audit.
        $auditIterationCaptures = @(Get-ChildItem -LiteralPath $auditIterationPath -Recurse -File -Filter traffic.json).Count
        $auditPacketPassed = -not $CapturePackets -or ($auditPacketSummary.captureExitCode -eq 0 -and
            -not $auditPacketSummary.endedBeforeTests -and $null -ne $auditPacketSummary.dropped -and
            $auditPacketSummary.dropped -eq 0 -and $auditPacketSummary.streams -eq $auditIterationCaptures -and
            $auditPacketSummary.handshakes -eq 2 * $auditIterationCaptures -and
            $auditPacketSummary.malformedPackets.Count -eq 0 -and $auditPacketSummary.missingSegments -eq 0)
        if (-not $auditPacketPassed) {
            Write-Warning ("Iteration ${auditIteration}: packet validation failed. Expected $auditIterationCaptures streams and " +
                "$(2 * $auditIterationCaptures) handshakes; captured $($auditPacketSummary.streams) streams and " +
                "$($auditPacketSummary.handshakes) handshakes. Inspect $auditIterationPath\packets-summary.json and capture.log.")
        }
        $auditResults += [pscustomobject]@{
            iteration = $auditIteration; exitCode = $auditExit; totalTests = $auditTotal
            captures = $auditIterationCaptures
            packetCapture = $auditPacketSummary
            passed = ($auditExit -eq 0 -and $auditTotal -gt 0 -and $auditIterationCaptures -gt 0 -and $auditPacketPassed)
        }
    }
    $auditCaptures = @(Get-ChildItem -LiteralPath $auditRun -Recurse -File -Filter traffic.json)
    [ordered]@{
        formatVersion = 2
        scope = if ($CapturePackets) { 'Generated plaintext TCP scenarios with Npcap loopback packet captures' }
            else { 'Isolated plaintext TCP scenarios using generated data; not a live packet capture' }
        captures = $auditCaptures.Count
        iterations = $auditResults
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $auditRun 'summary.json')
    Write-Host "Traffic audit artifacts: $auditRun"
    if (@($auditResults | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
}
finally {
    $env:PEERSHARP_WIRE_AUDIT_DIR = $auditOriginalOutput
    Pop-Location
    $auditMutex.ReleaseMutex()
    $auditMutex.Dispose()
}
