param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('bencode', 'peer-message', 'torrent-metadata', 'dht-compact')]
    [string] $Target,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = Join-Path $PSScriptRoot "corpus/$Target"
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

Get-ChildItem -LiteralPath $sourceDirectory -File | ForEach-Object {
    $destination = Join-Path $resolvedOutput ($_.BaseName + '.seed')

    if ($Target -ne 'bencode') {
        $hex = (Get-Content -LiteralPath $_.FullName -Raw) -replace '\s', ''
        [System.IO.File]::WriteAllBytes($destination, [Convert]::FromHexString($hex))
    }
    else {
        $value = (Get-Content -LiteralPath $_.FullName -Raw).TrimEnd("`r", "`n")
        [System.IO.File]::WriteAllBytes($destination, [Text.Encoding]::ASCII.GetBytes($value))
    }
}

Write-Host "Prepared $Target seeds in $resolvedOutput"
