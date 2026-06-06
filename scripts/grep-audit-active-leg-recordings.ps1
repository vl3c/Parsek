# Phase 8e S3b (map-render cutover): lock the deletion of the legacy polyline
# ownership publish `activeLegRecordings`. The drew set
# (`drewNonOrbitalLegRecordings`) is the single ownership source now; the legacy
# set and its S3a deletion-safety gate were removed. This audit enforces that NO
# file under Source/Parsek/ resurrects the deleted identifier (field, publish,
# read, doc-comment, or anything else).
#
# Exit 0: zero matches of `activeLegRecordings` under Source/Parsek/.
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
# field, or a method name all count as a resurrected reference.
$pattern = "activeLegRecordings"

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
