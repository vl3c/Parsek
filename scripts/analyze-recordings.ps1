# Offline recording analyzer CLI driver (module M-A1). Mirrors
# validate-ksp-log.ps1: it resolves the target save directory, sets the env
# vars the Manual analyzer test reads, drives that single xUnit test via
# `dotnet test`, surfaces the two report files, and propagates the exit code.
# This keeps ONE execution path (the xUnit host) for every run mode, so the
# harness and a human get identical verdicts.
#
# Usage:
#   scripts/analyze-recordings.ps1 -SaveDir <path-to-save-dir> [-ResultsDir <dir>] [-NoBuild]
param(
    [Parameter(Mandatory = $true)]
    [string]$SaveDir,
    [string]$ResultsDir,
    [switch]$NoBuild
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

# Surface the two report files (named after the save directory leaf).
$saveName = Split-Path $resolvedSaveDir -Leaf
$jsonPath = Join-Path $resolvedResultsDir "$saveName.analysis.json"
$txtPath = Join-Path $resolvedResultsDir "$saveName.analysis.txt"

if (Test-Path $jsonPath) {
    Write-Host "Machine report: $jsonPath"
} else {
    Write-Host "Machine report was not written (expected: $jsonPath)"
}
if (Test-Path $txtPath) {
    Write-Host "Human report:   $txtPath"
}

if ($testExit -ne 0) {
    exit $testExit
}

Write-Host "Analyzer run complete."
