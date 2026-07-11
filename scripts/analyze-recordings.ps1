# Offline recording analyzer CLI driver (module M-A1). Mirrors
# validate-ksp-log.ps1: it resolves the target save directory, sets the env
# vars the Manual analyzer test reads, drives that single xUnit test via
# `dotnet test`, surfaces the two report files, echoes the human-report header
# line, and propagates the exit code. This keeps ONE execution path (the xUnit
# host) for every run mode, so the harness and a human get identical verdicts.
#
# Usage:
#   scripts/analyze-recordings.ps1 -SaveDir <path-to-save-dir> [-ResultsDir <dir>] [-NoBuild] [-FailOnRed]
#
# -FailOnRed exits nonzero when the report is red (any FAIL or any STALE), so a
# harness / CI step can gate on the analyzer verdict without parsing JSON.
param(
    [Parameter(Mandatory = $true)]
    [string]$SaveDir,
    [string]$ResultsDir,
    [switch]$NoBuild,
    [switch]$FailOnRed
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Resolve the save directory.
try {
    $resolvedSaveDir = (Resolve-Path $SaveDir -ErrorAction Stop).Path
} catch {
    Write-Error "Save directory not found: '$SaveDir'"
    exit 1
}
if (-not (Test-Path $resolvedSaveDir -PathType Container)) {
    Write-Error "Save path is not a directory: '$resolvedSaveDir'"
    exit 1
}

# Results directory: explicit arg, else an 'analysis' folder beside the save.
if ([string]::IsNullOrWhiteSpace($ResultsDir)) {
    $ResultsDir = Join-Path $resolvedSaveDir "analysis"
}
if (-not (Test-Path $ResultsDir)) {
    New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null
}
$resolvedResultsDir = (Resolve-Path $ResultsDir).Path

$testExit = 1
try {
    # Hand the paths to the Manual analyzer test through the environment.
    $env:PARSEK_ANALYZER_SAVE = $resolvedSaveDir
    $env:PARSEK_ANALYZER_RESULTS = $resolvedResultsDir

    $testArgs = @(
        "test",
        (Join-Path $repoRoot "Source/Parsek.Tests/Parsek.Tests.csproj"),
        "--filter", "FullyQualifiedName~OfflineAnalyzerTests.Manual_AnalyzeEnvSave_WritesReports",
        "-v", "minimal"
    )
    if ($NoBuild) {
        $testArgs += "--no-build"
    }

    Write-Host "Analyzing save '$resolvedSaveDir'"
    Write-Host "Writing reports to '$resolvedResultsDir'"
    dotnet @testArgs
    $testExit = $LASTEXITCODE
}
finally {
    # Never leak the analyzer env vars into the caller's session / a later run.
    Remove-Item Env:\PARSEK_ANALYZER_SAVE -ErrorAction SilentlyContinue
    Remove-Item Env:\PARSEK_ANALYZER_RESULTS -ErrorAction SilentlyContinue
}

# Surface the two report files (named after the save directory leaf).
$saveName = Split-Path $resolvedSaveDir -Leaf
$jsonPath = Join-Path $resolvedResultsDir "$saveName.analysis.json"
$txtPath = Join-Path $resolvedResultsDir "$saveName.analysis.txt"

if (Test-Path $jsonPath) {
    Write-Host "Machine report: $jsonPath"
} else {
    Write-Host "Machine report was not written (expected: $jsonPath)"
}

# Echo the human-report header line (the one-line verdict summary) so a caller
# sees the counts without opening the file.
$reportIsRed = $false
if (Test-Path $txtPath) {
    Write-Host "Human report:   $txtPath"
    $header = (Get-Content -LiteralPath $txtPath -TotalCount 1)
    if ($header) {
        Write-Host $header
        # Red = any FAIL or any STALE (mirrors AnalysisReport.IsRed).
        $failMatch = [regex]::Match($header, 'FAIL=(\d+)')
        $staleMatch = [regex]::Match($header, 'STALE=(\d+)')
        $failCount = if ($failMatch.Success) { [int]$failMatch.Groups[1].Value } else { 0 }
        $staleCount = if ($staleMatch.Success) { [int]$staleMatch.Groups[1].Value } else { 0 }
        if ($failCount -gt 0 -or $staleCount -gt 0) {
            $reportIsRed = $true
        }
    }
}

if ($testExit -ne 0) {
    exit $testExit
}

if ($FailOnRed -and $reportIsRed) {
    Write-Host "Analyzer verdict: RED (FailOnRed set) - exiting nonzero."
    exit 2
}

Write-Host "Analyzer run complete."
