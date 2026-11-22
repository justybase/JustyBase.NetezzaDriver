# Enforces the coverage gates from the Cobertura reports produced by
# `dotnet test --collect:"XPlat Code Coverage"`.
#
# Two gates are intentional:
#   * Protocol primitives: 70%, matching the Rust driver's line gate. These are
#     the byte-level decoders, parameter formatting and reader primitives that
#     can be fully exercised offline.
#   * Overall driver: a ratcheting floor. Bulk copy, external tables and parts
#     of the catalog/DDL surface require a live appliance and are exercised by
#     the conformance profile instead.
[CmdletBinding()]
param(
    [string]$ResultsDirectory = 'TestResults/coverage',
    [double]$OverallMinimum = 55,
    [double]$CoreMinimum = 70
)

$ErrorActionPreference = 'Stop'

$reports = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter coverage.cobertura.xml -File -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    throw "No coverage.cobertura.xml found under '$ResultsDirectory'."
}

# Merge reports by class + source line, keeping the highest hit count. This
# handles multi-target runs and repeated test invocations without double
# counting the same line.
$lines = @{}
foreach ($report in $reports) {
    [xml]$document = Get-Content -LiteralPath $report.FullName
    foreach ($package in $document.coverage.packages.package) {
        foreach ($class in $package.classes.class) {
            foreach ($method in $class.methods.method) {
                foreach ($line in $method.lines.line) {
                    $key = '{0}|{1}' -f $class.name, $line.number
                    $hits = [int]$line.hits
                    if (-not $lines.ContainsKey($key) -or $hits -gt $lines[$key].Hits) {
                        $lines[$key] = [pscustomobject]@{
                            Class = [string]$class.name
                            Hits  = $hits
                        }
                    }
                }
            }
        }
    }
}

if ($lines.Count -eq 0) {
    throw "Coverage reports under '$ResultsDirectory' contain no line data."
}

$coreClasses = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in @(
        'NzReadBuffer',
        'ProtocolLengthValidator',
        'ProtocolCountingStream',
        'DbosTupleDesc',
        'RowDescriptionMessage',
        'RowValue',
        'NzConnectionHelpers',
        'NzParameterHelper',
        'NzParameter',
        'NzParameterCollection',
        'NzCommand',
        'NzDataReader'
    )) {
    [void]$coreClasses.Add($name)
}

$overallTotal = 0
$overallCovered = 0
$coreTotal = 0
$coreCovered = 0

foreach ($entry in $lines.Values) {
    $shortName = ($entry.Class -split '\.')[-1]
    $isCore = $coreClasses.Contains($shortName) -or $entry.Class.Contains('.TypeConvertions.', [StringComparison]::Ordinal)

    $overallTotal++
    if ($entry.Hits -gt 0) { $overallCovered++ }

    if ($isCore) {
        $coreTotal++
        if ($entry.Hits -gt 0) { $coreCovered++ }
    }
}

function Format-Rate([int]$Covered, [int]$Total) {
    if ($Total -eq 0) { return 'n/a' }
    return '{0:P2}' -f ($Covered / $Total)
}

function Format-Line([string]$Label, [int]$Covered, [int]$Total, [double]$Minimum) {
    $rate = if ($Total -eq 0) { 100.0 } else { 100.0 * $Covered / $Total }
    $status = if ($rate -ge $Minimum) { 'PASS' } else { 'FAIL' }
    return '{0,-22} {1,8}  ({2}/{3})  minimum {4,5:P1}  {5}' -f `
        $Label, (Format-Rate $Covered $Total), $Covered, $Total, ($Minimum / 100.0), $status
}

$overallRate = if ($overallTotal -eq 0) { 100.0 } else { 100.0 * $overallCovered / $overallTotal }
$coreRate = if ($coreTotal -eq 0) { 100.0 } else { 100.0 * $coreCovered / $coreTotal }

$summary = @(
    (Format-Line 'Overall driver' $overallCovered $overallTotal $OverallMinimum),
    (Format-Line 'Protocol primitives' $coreCovered $coreTotal $CoreMinimum)
)
$summary | ForEach-Object { Write-Host $_ }

if ($env:GITHUB_STEP_SUMMARY) {
    @('## Coverage', '', '```', $summary, '```', '') | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}

if ($overallRate -lt $OverallMinimum -or $coreRate -lt $CoreMinimum) {
    exit 1
}
exit 0
