# Phase 5b (map-render cutover): lock the deletion of the typed-spine cutover flag and the
# legacy TracedPath side-channel. The typed PhaseChain spine is UNCONDITIONAL: the compile-time
# flag const, its class-level home, the in-game force seam, the flag-aware selector property,
# and the per-pid legacy side-channel dictionary were all removed. The surviving predicates
# (IsTracedPathOwnedThisFrame / IsDirectorTracedPathActiveFromIntent / IsDirectorDriveActive /
# IsDirectorTracking) do NOT contain any forbidden literal below and are never flagged. This
# audit enforces that NO file under Source/Parsek/ resurrects any of the deleted identifiers
# (field, const, gate-clause read, doc-comment, or anything else).
#
# Forbidden tokens (case-sensitive substrings):
#   MapRenderPhaseSpineDrive   - the removed compile-time cutover flag const
#   ForceSpineDriveForTesting  - the removed in-game force seam
#   PhaseSpineDriveActive      - the removed const-OR-seam selector property
#   tracedPathByPid            - the removed legacy TracedPath side-channel dictionary
#
# Exit 0: zero matches of any forbidden token under Source/Parsek/.
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

$patterns = @(
    "MapRenderPhaseSpineDrive",
    "ForceSpineDriveForTesting",
    "PhaseSpineDriveActive",
    "tracedPathByPid"
)

$violations = New-Object System.Collections.Generic.List[string]

$repoRootFull = (Resolve-Path $RepoRoot).Path -replace '\\', '/'

$files = Get-ChildItem -Path $sourceRoot -Recurse -Filter *.cs -File
foreach ($file in $files) {
    $fullPath = $file.FullName -replace '\\', '/'
    $rel = $fullPath
    if ($rel.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $rel.Substring($repoRootFull.Length).TrimStart('/')
    }

    $lines = Get-Content -LiteralPath $file.FullName
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $text = $lines[$i]
        foreach ($pattern in $patterns) {
            if ($text -clike "*$pattern*") {
                $lineNum = $i + 1
                $trimmed = $text.Trim()
                $violations.Add("${rel}:${lineNum}: [$pattern] ${trimmed}")
                break
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $vcount = $violations.Count
    Write-Host "grep-audit: $vcount resurrected phase-spine-drive reference(s) found under Source/Parsek/"
    foreach ($v in $violations) {
        Write-Host $v
    }
    exit 1
}

Write-Host "grep-audit: OK (zero phase-spine-drive / traced-path side-channel references under Source/Parsek/)"
exit 0
