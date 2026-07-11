# Historical-saves regression floor (design doc
# docs/dev/design-autotest-findings-baseline.md "Plumbing"). Drives the SINGLE
# Manual xUnit test OfflineAnalyzerTests.Manual_HistoricalSaves_GreenUnderApply
# over the known historical saves that are permanently RED on baked-in true
# positives (the five immutable INV2-NO-DOUBLE-COVER saves + any other triaged
# save carrying an authored baseline.cfg). Each listed save runs in Apply mode;
# green means "no new damage on any listed save", and a red is a genuinely NEW
# finding on top of the already-baselined findings.
#
# This exports the existing save dirs as a semicolon-separated
# PARSEK_ANALYZER_HISTORICAL_SAVES list and lets the ONE Manual test loop them, so
# there is a single xUnit execution path (matching analyze-recordings.ps1's design
# intent) rather than N per-save shell-outs. Per-save GREEN/RED is then reported
# from each save's own analysis/<leaf>.analysis.txt terminal RED= token (the same
# single gate source the test's verdict is built from).
#
# Each save MUST already carry <save>/analysis/baseline.cfg (author it once with
#   scripts/analyze-recordings.ps1 -SaveDir <save> -WriteBaseline
# after confirming its reds are known true positives).
#
# Usage:
#   scripts/analyze-historical-saves.ps1 -Saves <path1>,<path2>,...
#   scripts/analyze-historical-saves.ps1            # uses the built-in $DefaultSaves
#
# Exit 0: every listed save is green under Apply. Exit 2: at least one carried a
# new (non-baselined) finding. Exit 1: the test run failed to execute.
param(
    [string[]]$Saves,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# The dev-machine historical save directories. These are machine-specific and
# gitignored save-side artifacts, so fill this list in on the machine that owns
# the five known-red saves (or pass -Saves explicitly). Left empty by default so a
# fresh checkout does not fail on absent paths.
$DefaultSaves = @(
    # "C:/Users/vlad3/Documents/Code/Parsek/Kerbal Space Program/saves/<historical-save-1>",
    # "C:/Users/vlad3/Documents/Code/Parsek/Kerbal Space Program/saves/<historical-save-2>"
)

if (-not $Saves -or $Saves.Count -eq 0) {
    $Saves = $DefaultSaves
}

if (-not $Saves -or $Saves.Count -eq 0) {
    Write-Host 'No historical saves configured. Pass -Saves <path>,... or fill in $DefaultSaves.'
    Write-Host "(Each save must already carry analysis/baseline.cfg; author it with -WriteBaseline.)"
    exit 0
}

# Keep only the saves that actually exist as directories; a missing listed save is
# skipped (not a red), mirroring the Manual test's own per-save existence check.
$existing = New-Object System.Collections.Generic.List[string]
foreach ($save in $Saves) {
    if ([string]::IsNullOrWhiteSpace($save)) { continue }
    if (-not (Test-Path $save -PathType Container)) {
        Write-Host "SKIP (missing): $save"
        continue
    }
    $existing.Add((Resolve-Path $save).Path)
}

if ($existing.Count -eq 0) {
    Write-Host "No listed historical save exists on this machine; nothing to run."
    exit 0
}

Write-Host "=== Historical floor: driving Manual_HistoricalSaves_GreenUnderApply over $($existing.Count) save(s) ==="
foreach ($save in $existing) { Write-Host "  save: $save" }

$testExit = 1
try {
    # Hand the save list to the single Manual test through the environment; it loops
    # each save in Apply mode and asserts none carries a NEW (non-baselined) finding.
    $env:PARSEK_ANALYZER_HISTORICAL_SAVES = ($existing -join ";")

    $testArgs = @(
        "test",
        (Join-Path $repoRoot "Source/Parsek.Tests/Parsek.Tests.csproj"),
        "--filter", "FullyQualifiedName~OfflineAnalyzerTests.Manual_HistoricalSaves_GreenUnderApply",
        "-v", "minimal"
    )
    if ($NoBuild) {
        $testArgs += "--no-build"
    }

    dotnet @testArgs
    $testExit = $LASTEXITCODE
}
finally {
    # Never leak the list into the caller's session / a later run.
    Remove-Item Env:\PARSEK_ANALYZER_HISTORICAL_SAVES -ErrorAction SilentlyContinue
}

# Per-save verdict from each save's own report header terminal RED= token (the same
# single gate source the test's aggregate assert is built from). The test writes
# <save>/analysis/<leaf>.analysis.txt for every listed save it runs.
$reds = New-Object System.Collections.Generic.List[string]
$noReport = New-Object System.Collections.Generic.List[string]

Write-Host ""
Write-Host "=== Per-save verdicts ==="
foreach ($save in $existing) {
    $leaf = Split-Path $save -Leaf
    $txtPath = Join-Path (Join-Path $save "analysis") "$leaf.analysis.txt"
    if (-not (Test-Path $txtPath)) {
        Write-Host "NO REPORT: $save"
        $noReport.Add($save)
        continue
    }
    $header = (Get-Content -LiteralPath $txtPath -TotalCount 1)
    $redMatch = [regex]::Match($header, 'RED=(\d+)\s*$')
    if ($redMatch.Success) {
        if (([int]$redMatch.Groups[1].Value) -ne 0) {
            Write-Host "RED (new finding): $save"
            $reds.Add($save)
        } else {
            Write-Host "GREEN: $save"
        }
    } else {
        # A report without the terminal RED= token: fail loud rather than silently
        # pass a possibly-red run (mirrors analyze-recordings.ps1).
        Write-Host "RED (no RED= token, stale report format): $save"
        $reds.Add($save)
    }
}

Write-Host ""
Write-Host "=== Historical floor summary ==="
Write-Host "testExit: $testExit"
Write-Host "reds:     $($reds.Count)"
Write-Host "noReport: $($noReport.Count)"

if ($reds.Count -gt 0) {
    foreach ($r in $reds) { Write-Host "  new finding: $r" }
    exit 2
}
if ($testExit -ne 0) {
    # The test failed to execute (build error, host crash) with no attributable red.
    Write-Host "Manual test run failed to execute (exit $testExit)."
    exit 1
}

Write-Host "All historical saves green under Apply."
exit 0
