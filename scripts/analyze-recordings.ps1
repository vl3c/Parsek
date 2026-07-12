# Offline recording analyzer CLI driver (module M-A1 + the per-save baseline
# follow-on). Mirrors validate-ksp-log.ps1: it resolves the target save
# directory, sets the env vars the Manual analyzer test reads, drives that single
# xUnit test via `dotnet test`, surfaces the two report files, echoes the human
# report header line, and propagates the exit code. This keeps ONE execution path
# (the xUnit host) for every run mode, so the harness and a human get identical
# verdicts.
#
# Usage:
#   scripts/analyze-recordings.ps1 -SaveDir <path> [-ResultsDir <dir>] [-NoBuild]
#       [-FailOnRed] [-UseBaseline] [-WriteBaseline] [-KeepStaleBaselineEntries]
#
# Modes:
#   (default)        analyze in Ignore; no baseline consulted.
#   -UseBaseline     analyze in Apply; matching findings are accepted (baselined)
#                    and do not count toward RED. Triage / historical floor.
#   -WriteBaseline   author <save>/analysis/baseline.cfg from the TRUE (unfiltered)
#                    findings (FAIL/WARN only). Authoring action, does NOT gate.
#                    Refuses if PARSEK_ANALYZER_BASELINE_MODE=forbid is declared in
#                    the environment. -KeepStaleBaselineEntries retains momentarily
#                    unmatched entries instead of pruning them.
#
# -FailOnRed exits nonzero when the report is red. RED is read from the terminal
# RED=<0|1> header token (the emitter's single reduction of the non-baselined
# FAIL/STALE splits). It is NEVER recomputed from the raw FAIL=/STALE= tokens,
# which still count baselined findings and would spuriously red an all-baselined
# save.
param(
    [Parameter(Mandatory = $true)]
    [string]$SaveDir,
    [string]$ResultsDir,
    [switch]$NoBuild,
    [switch]$FailOnRed,
    [switch]$UseBaseline,
    [switch]$WriteBaseline,
    [switch]$KeepStaleBaselineEntries
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

# Pick the single Manual test to drive.
if ($WriteBaseline) {
    $testFilter = "FullyQualifiedName~OfflineAnalyzerTests.Manual_WriteBaselineForEnvSave"
} else {
    $testFilter = "FullyQualifiedName~OfflineAnalyzerTests.Manual_AnalyzeEnvSave_WritesReports"
}

$testExit = 1
$modeWasSet = $false
$keepStaleWasSet = $false
try {
    # Hand the paths to the Manual analyzer test through the environment.
    $env:PARSEK_ANALYZER_SAVE = $resolvedSaveDir
    $env:PARSEK_ANALYZER_RESULTS = $resolvedResultsDir

    if ($WriteBaseline) {
        # Authoring: do NOT set BASELINE_MODE, so a caller's ambient forbid
        # declaration reaches the test and blocks the write per design.
        if ($KeepStaleBaselineEntries) {
            $env:PARSEK_ANALYZER_KEEP_STALE = "1"
            $keepStaleWasSet = $true
        }
        Write-Host "Writing baseline for save '$resolvedSaveDir'"
    } else {
        # Analyze: set the mode explicitly so an ambient env value cannot change it.
        if ($UseBaseline) {
            $env:PARSEK_ANALYZER_BASELINE_MODE = "apply"
        } else {
            $env:PARSEK_ANALYZER_BASELINE_MODE = "ignore"
        }
        $modeWasSet = $true
        Write-Host "Analyzing save '$resolvedSaveDir' (mode=$($env:PARSEK_ANALYZER_BASELINE_MODE))"
        Write-Host "Writing reports to '$resolvedResultsDir'"
    }

    $testArgs = @(
        "test",
        (Join-Path $repoRoot "Source/Parsek.Tests/Parsek.Tests.csproj"),
        "--filter", $testFilter,
        "-v", "minimal"
    )
    if ($NoBuild) {
        $testArgs += "--no-build"
    }

    dotnet @testArgs
    $testExit = $LASTEXITCODE
}
finally {
    # Never leak the analyzer env vars into the caller's session / a later run.
    Remove-Item Env:\PARSEK_ANALYZER_SAVE -ErrorAction SilentlyContinue
    Remove-Item Env:\PARSEK_ANALYZER_RESULTS -ErrorAction SilentlyContinue
    if ($modeWasSet) { Remove-Item Env:\PARSEK_ANALYZER_BASELINE_MODE -ErrorAction SilentlyContinue }
    if ($keepStaleWasSet) { Remove-Item Env:\PARSEK_ANALYZER_KEEP_STALE -ErrorAction SilentlyContinue }
}

# The -WriteBaseline flow authors a file and does not gate; report + exit.
if ($WriteBaseline) {
    $baselinePath = Join-Path (Join-Path $resolvedSaveDir "analysis") "baseline.cfg"
    if (Test-Path $baselinePath) {
        Write-Host "Baseline written: $baselinePath"
    } else {
        Write-Host "Baseline was not written (expected: $baselinePath)"
    }
    if ($testExit -ne 0) { exit $testExit }
    Write-Host "Baseline authoring complete."
    exit 0
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

# Echo the human-report header line (the one-line verdict summary) and read the
# terminal RED=<0|1> token as the SOLE gate source.
$reportIsRed = $false
if (Test-Path $txtPath) {
    Write-Host "Human report:   $txtPath"
    $header = (Get-Content -LiteralPath $txtPath -TotalCount 1)
    if ($header) {
        Write-Host $header
        # Anchor to end-of-line: RED= is the LAST token on the header, so a save
        # leaf that itself contains a literal "RED=0" earlier in the line cannot
        # false-green the gate. Trailing whitespace is tolerated.
        $redMatch = [regex]::Match($header, 'RED=(\d+)\s*$')
        if ($redMatch.Success) {
            $reportIsRed = ([int]$redMatch.Groups[1].Value) -ne 0
        } else {
            # A report from before the RED= token existed: fail loud rather than
            # silently pass a possibly-red run.
            Write-Host "Header carries no RED= token; treating as red (stale report format)."
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
