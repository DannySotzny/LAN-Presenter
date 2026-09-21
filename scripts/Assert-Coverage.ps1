param(
    [Parameter(Mandatory = $true)]
    [string] $ResultsDirectory,

    [ValidateRange(0, 100)]
    [double] $Threshold = 80
)

$ErrorActionPreference = 'Stop'
$resolvedResults = (Resolve-Path -LiteralPath $ResultsDirectory).Path
$reports = @(Get-ChildItem -LiteralPath $resolvedResults -Recurse -Filter 'coverage.cobertura.xml')
if ($reports.Count -eq 0) {
    throw "No Cobertura coverage reports were found below '$resolvedResults'."
}

$sourceLines = @{}
foreach ($report in $reports) {
    [xml] $coverage = Get-Content -LiteralPath $report.FullName
    foreach ($package in $coverage.coverage.packages.package) {
        $packageName = [string] $package.name
        foreach ($class in $package.classes.class) {
            $fileName = ([string] $class.filename).Replace('/', '\')
            $projectMarker = $fileName.IndexOf('BeamerPresenter.', [StringComparison]::OrdinalIgnoreCase)
            $canonicalFileName = if ($projectMarker -ge 0) {
                $fileName.Substring($projectMarker)
            } else {
                "$packageName\$fileName"
            }

            foreach ($line in $class.lines.line) {
                $key = "$canonicalFileName|$($line.number)"
                $hits = [int] $line.hits
                if (!$sourceLines.ContainsKey($key) -or $hits -gt $sourceLines[$key]) {
                    $sourceLines[$key] = $hits
                }
            }
        }
    }
}

if ($sourceLines.Count -eq 0) {
    throw 'Coverage reports did not contain any product source lines.'
}

$coveredLines = @($sourceLines.Values | Where-Object { $_ -gt 0 }).Count
$lineCoverage = 100.0 * $coveredLines / $sourceLines.Count
Write-Host ('Product line coverage: {0:N2}% ({1}/{2} lines)' -f $lineCoverage, $coveredLines, $sourceLines.Count)
if ($lineCoverage -lt $Threshold) {
    throw ('Line coverage {0:N2}% is below the required {1:N2}%.' -f $lineCoverage, $Threshold)
}
