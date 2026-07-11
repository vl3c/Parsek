# Historical-saves regression floor (design doc
# docs/dev/design-autotest-findings-baseline.md "Plumbing"). Loops the known
# historical saves that are permanently RED on baked-in true positives (the five
# immutable INV2-NO-DOUBLE-COVER saves + any other triaged save carrying an
# authored baseline.cfg) and runs each in Apply mode. Green means "no new damage
# on any listed save"; a red is a genuinely NEW finding on top of the
# already-baselined findings, and the offending save is named.
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
# new (non-baselined) finding. Exit 1: a run failed to execute.
param(
    [string[]]$Saves,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

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

$analyzeScript = Join-Path $PSScriptRoot "analyze-recordings.ps1"
$reds = New-Object System.Collections.Generic.List[string]
$errors = New-Object System.Collections.Generic.List[string]
$firstRun = $true

foreach ($save in $Saves) {
    if ([string]::IsNullOrWhiteSpace($save)) { continue }
    if (-not (Test-Path $save -PathType Container)) {
        Write-Host "SKIP (missing): $save"
        continue
    }

    Write-Host "=== Historical save: $save ==="

    # Build once (first save), reuse the build for the rest.
    $extra = @()
    if ($NoBuild -or (-not $firstRun)) { $extra += "-NoBuild" }
    $firstRun = $false

    & $analyzeScript -SaveDir $save -UseBaseline -FailOnRed @extra
    $code = $LASTEXITCODE

    if ($code -eq 0) {
        Write-Host "GREEN: $save"
    } elseif ($code -eq 2) {
        Write-Host "RED (new finding): $save"
        $reds.Add($save)
    } else {
        Write-Host "ERROR (exit $code): $save"
        $errors.Add($save)
    }
}

Write-Host ""
Write-Host "=== Historical floor summary ==="
Write-Host "reds:   $($reds.Count)"
Write-Host "errors: $($errors.Count)"

if ($errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Host "  error: $e" }
    exit 1
}
if ($reds.Count -gt 0) {
    foreach ($r in $reds) { Write-Host "  new finding: $r" }
    exit 2
}

Write-Host "All historical saves green under Apply."
exit 0
