param(
    [Parameter(Mandatory = $true)]
    [string[]]$Reports,

    [int]$Top = 30,

    [int]$MinLines = 25,

    [ValidateRange(0, 100)]
    [double]$MinimumLinePercent = 0,

    [ValidateRange(0, 100)]
    [double]$MinimumBranchPercent = 0
)

$reportSummaries = foreach ($report in $Reports) {
    [xml]$xml = Get-Content -LiteralPath $report
    [pscustomobject]@{
        Report = $report
        LinePercent = [math]::Round([double]$xml.coverage.'line-rate' * 100, 2)
        BranchPercent = [math]::Round([double]$xml.coverage.'branch-rate' * 100, 2)
    }
}

$items = foreach ($report in $Reports) {
    [xml]$xml = Get-Content -LiteralPath $report
    $assembly = $xml.coverage.packages.package.name
    foreach ($class in $xml.coverage.packages.package.classes.class) {
        $lines = @($class.lines.line)
        $valid = $lines.Count
        $covered = @($lines | Where-Object { [int]$_.hits -gt 0 }).Count
        [pscustomobject]@{
            Assembly = $assembly
            Class = $class.name
            File = $class.filename
            Coverage = [math]::Round(($covered / [math]::Max($valid, 1)) * 100, 1)
            LinesValid = $valid
            LinesCovered = $covered
            LinesMissed = $valid - $covered
        }
    }
}

Write-Host "Coverage gates"
$reportSummaries | Format-Table -AutoSize

$failedReports = @($reportSummaries | Where-Object {
    $_.LinePercent -lt $MinimumLinePercent -or $_.BranchPercent -lt $MinimumBranchPercent
})
if ($failedReports.Count -gt 0) {
    throw "Coverage fell below the required $MinimumLinePercent% line / $MinimumBranchPercent% branch floor: $($failedReports.Report -join ', ')"
}

Write-Host "Overall"
$items |
    Group-Object Assembly |
    ForEach-Object {
        $valid = ($_.Group | Measure-Object LinesValid -Sum).Sum
        $covered = ($_.Group | Measure-Object LinesCovered -Sum).Sum
        [pscustomobject]@{
            Assembly = $_.Name
            Coverage = [math]::Round(($covered / [math]::Max($valid, 1)) * 100, 2)
            LinesCovered = $covered
            LinesValid = $valid
        }
    } |
    Format-Table -AutoSize

Write-Host "Top missed files"
$items |
    Group-Object Assembly, File |
    ForEach-Object {
        $valid = ($_.Group | Measure-Object LinesValid -Sum).Sum
        $covered = ($_.Group | Measure-Object LinesCovered -Sum).Sum
        $parts = $_.Name -split ', ', 2
        [pscustomobject]@{
            Assembly = $parts[0]
            File = $parts[1]
            Coverage = [math]::Round(($covered / [math]::Max($valid, 1)) * 100, 1)
            LinesValid = $valid
            LinesMissed = $valid - $covered
        }
    } |
    Where-Object { $_.LinesValid -ge $MinLines } |
    Sort-Object LinesMissed -Descending |
    Select-Object -First $Top |
    Format-Table -AutoSize

Write-Host "Untested classes"
$items |
    Where-Object { $_.LinesValid -ge $MinLines -and $_.Coverage -eq 0 } |
    Sort-Object LinesValid -Descending |
    Select-Object -First $Top |
    Format-Table -AutoSize
