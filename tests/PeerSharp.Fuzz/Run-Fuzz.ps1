param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('bencode', 'peer-message', 'torrent-metadata', 'dht-compact')]
    [string] $Target,

    [string] $Configuration = 'Release',

    [string] $OutputDirectory = 'artifacts/fuzz-findings',

    [ValidateRange(0, [int]::MaxValue)]
    [int] $TimeLimitSeconds = 0
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $PSScriptRoot 'PeerSharp.Fuzz.csproj'
$corpus = Join-Path $repositoryRoot "artifacts/fuzz-corpus/$Target"
$findings = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$frameworkDirectory = Join-Path $PSScriptRoot "bin/$Configuration/net10.0"
$instrumentedAssembly = Join-Path $frameworkDirectory 'PeerSharp.dll'
$harness = Join-Path $frameworkDirectory 'PeerSharp.Fuzz.dll'

Push-Location $repositoryRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    dotnet build $project --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Fuzz harness build failed.' }

    & (Join-Path $PSScriptRoot 'Prepare-Corpus.ps1') -Target $Target -OutputDirectory $corpus

    dotnet tool run sharpfuzz $instrumentedAssembly
    if ($LASTEXITCODE -ne 0) { throw 'SharpFuzz instrumentation failed.' }

    [System.IO.Directory]::CreateDirectory($findings) | Out-Null
    $aflOptions = @('-i', $corpus, '-o', $findings, '-m', 'none')
    if ($TimeLimitSeconds -gt 0) {
        $aflOptions += @('-V', $TimeLimitSeconds.ToString([CultureInfo]::InvariantCulture))
    }
    # SharpFuzz instruments managed IL. The native dotnet launcher cannot contain AFL's signature.
    & env AFL_SKIP_BIN_CHECK=1 afl-fuzz @aflOptions -- dotnet $harness $Target
    if ($LASTEXITCODE -ne 0) { throw "afl-fuzz exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
