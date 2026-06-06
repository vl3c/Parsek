# Phase 8e S4 (map-render cutover): lock the deletion of the `mapRenderDirectorDrive`
# setting / gate. The modular Director render pipeline is now UNCONDITIONAL: the
# setting, its persistence, its UI, and every `&& ...mapRenderDirectorDrive` gate
# clause were removed. The per-leg DECISION predicates
# (IsDirectorTracedPathActive / IsDirectorDriveActive / IsDirectorTracking) and the
# kept no-conic / suppressed-icon FLOOR mechanism (ghostsWithSuppressedIcon / the
# directorDriveActive local) SURVIVE - they are NOT this token. This audit enforces
# that NO file under Source/Parsek/ resurrects the deleted `mapRenderDirectorDrive`
# identifier (field, key, gate-clause read, doc-comment, or anything else).
#
# Exit 0: zero matches of `mapRenderDirectorDrive` under Source/Parsek/.
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

# The single forbidden token. Plain substring (case-sensitive) so a comment, a
# field, a key, or a method name all count as a resurrected reference. NOTE: this
# is the exact literal `mapRenderDirectorDrive`; the surviving DECISION predicates
# (IsDirectorTracedPathActive / IsDirectorDriveActive) and the `directorDriveActive`
# local do NOT contain this substring and are therefore never flagged.
$pattern = "mapRenderDirectorDrive"

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
        if ($text -clike "*$pattern*") {
            $lineNum = $i + 1
            $trimmed = $text.Trim()
            $violations.Add("${rel}:${lineNum}: ${trimmed}")
        }
    }
}

if ($violations.Count -gt 0) {
    $vcount = $violations.Count
    Write-Host "grep-audit: $vcount resurrected '$pattern' reference(s) found under Source/Parsek/"
    foreach ($v in $violations) {
        Write-Host $v
    }
    exit 1
}

Write-Host "grep-audit: OK (zero '$pattern' references under Source/Parsek/)"
exit 0
