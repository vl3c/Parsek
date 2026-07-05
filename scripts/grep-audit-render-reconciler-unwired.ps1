# Phase 8 (map/TS render overhaul, migration plan section 10): lock the UNWIRING of the
# now-circular intent-vs-old-truth comparator. The end-of-frame MapRenderProbe call to
# GhostRenderReconciler.CheckIntentAgainstOldTruth was removed: once the spine drives the
# render, the OLD truth the probe reads is the spine's own consequence, so the comparison
# is circular / self-confirming. The Phase-0 recorded-vs-rendered RenderParityOracle (a
# DISTINCT axis since Phase 0) is now the SOLE acceptance oracle.
#
# This is an UNWIRING, NOT a deletion of the type: GhostRenderReconciler, its pure
# predicates, and the CheckIntentAgainstOldTruth METHOD are KEPT (exercised by the xUnit
# GhostRenderReconcilerTests). What must stay gone is any LIVE production CALL SITE.
#
# Forbidden token: the CALL form `.CheckIntentAgainstOldTruth(` (a leading dot + a trailing
# open-paren). That pattern matches an invocation
# (e.g. `GhostRenderReconciler.CheckIntentAgainstOldTruth(`) but NOT:
#   - the method DEFINITION (`internal static void CheckIntentAgainstOldTruth(` - no leading dot), nor
#   - a doc-comment cref (`<see cref="CheckIntentAgainstOldTruth"/>` - no trailing paren).
# So the kept method + its kept doc-comments are never flagged, while a resurrected live
# call site is. The xUnit GhostRenderReconcilerTests under Source/Parsek.Tests/ DO call the
# method, but this audit scans only Source/Parsek/ (production), so they are out of scope.
#
# Exit 0: zero `.CheckIntentAgainstOldTruth(` call sites under Source/Parsek/.
# Exit 1: at least one match exists; offending sites are printed to stdout in
#         "<file>:<line>: <match>" format.

param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$sourceRoot = Join-Path $RepoRoot "Source/Parsek"

if (-not (Test-Path $sourceRoot)) {
    Write-Error "grep-audit: source root not found: $sourceRoot"
    exit 2
}

# The single forbidden token: the CALL form (leading dot + trailing open-paren). Plain
# substring (case-sensitive). The method definition (no leading dot) and the doc-comment
# crefs (no trailing paren) do NOT contain this literal and are therefore never flagged.
$pattern = ".CheckIntentAgainstOldTruth("

$violations = New-Object System.Collections.Generic.List[string]

$repoRootFull = (Resolve-Path $RepoRoot).Path -replace '\\', '/'

$files = Get-ChildItem -Path $sourceRoot -Recurse -Filter *.cs -File
foreach ($file in $files) {
    $fullPath = $file.FullName -replace '\\', '/'
    $rel = $fullPath
    if ($rel.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $rel.Substring($repoRootFull.Length).TrimStart('/')
    }

    # @(...) forces an array so a single-line file (Get-Content returns a bare string) is still iterated by
    # LINE, not by character (the managed xUnit gate is already immune; this hardens the direct .ps1 run too).
    $lines = @(Get-Content -LiteralPath $file.FullName)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $text = $lines[$i]
        if ($text -clike "*$pattern*") {
            $lineNum = $i + 1
            $trimmed = $text.Trim()
            $violations.Add("${rel}:${lineNum}: ${trimmed}")
        }
    }
}

if ($violations.Count -gt 0) {
    $vcount = $violations.Count
    Write-Host "grep-audit: $vcount resurrected '$pattern' call site(s) found under Source/Parsek/"
    foreach ($v in $violations) {
        Write-Host $v
    }
    exit 1
}

Write-Host "grep-audit: OK (zero '$pattern' call sites under Source/Parsek/)"
exit 0
