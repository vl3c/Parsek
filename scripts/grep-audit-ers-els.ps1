# Phase 3 of Rewind-to-Staging (design §11.7): enforce that no file outside
# the approved allowlist references RecordingStore.CommittedRecordings or
# Ledger.Actions directly. All other consumers must route through
# EffectiveState.ComputeERS() / ComputeELS().
#
# Exit 0: every hit's file is allowlisted.
# Exit 1: at least one unapproved raw-access site exists; offending sites are
#         printed to stdout in "<file>:<line>: <match>" format.

param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$sourceRoot = Join-Path $RepoRoot "Source/Parsek"
$allowlistPath = Join-Path $PSScriptRoot "ers-els-audit-allowlist.txt"

if (-not (Test-Path $sourceRoot)) {
    Write-Error "grep-audit: source root not found: $sourceRoot"
    exit 2
}
if (-not (Test-Path $allowlistPath)) {
    Write-Error "grep-audit: allowlist not found: $allowlistPath"
    exit 2
}

# Load allowlist, strip comments + whitespace, normalize separators.
# Entries ending in "/" are directory prefixes (every file underneath allowed).
# Other entries are exact file matches (repo-relative, forward slashes).
$allowedFiles = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
$allowedPrefixes = New-Object System.Collections.Generic.List[string]
foreach ($rawLine in Get-Content $allowlistPath) {
    $line = $rawLine.Trim()
    if ($line.Length -eq 0) { continue }
    if ($line.StartsWith('#')) { continue }
    $normalized = $line -replace '\\', '/'
    if ($normalized.EndsWith('/')) {
        $allowedPrefixes.Add($normalized)
    } else {
        [void]$allowedFiles.Add($normalized)
    }
}

function Test-Allowed([string]$rel) {
    if ($allowedFiles.Contains($rel)) { return $true }
    foreach ($p in $allowedPrefixes) {
        if ($rel.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

# Patterns per design §11.7:
#   - \.CommittedRecordings\b  -> RecordingStore.CommittedRecordings raw reads
#   - \bLedger\.Actions\b       -> GameActions.Ledger.Actions raw reads
$patterns = @(
    '\.CommittedRecordings\b',
    '\bLedger\.Actions\b'
)

$violations = New-Object System.Collections.Generic.List[string]
$hitsTotal = 0

$sourceRootFull = (Resolve-Path $sourceRoot).Path -replace '\\', '/'
$repoRootFull = (Resolve-Path $RepoRoot).Path -replace '\\', '/'

$files = Get-ChildItem -Path $sourceRoot -Recurse -Filter *.cs -File
foreach ($file in $files) {
    $fullPath = $file.FullName -replace '\\', '/'
    # Relative to repo root so allowlist entries can be written as e.g.
    # Source/Parsek/RecordingStore.cs
    $rel = $fullPath
    if ($rel.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $rel.Substring($repoRootFull.Length).TrimStart('/')
    }

    $lines = Get-Content -LiteralPath $file.FullName
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $text = $lines[$i]
        foreach ($pat in $patterns) {
            if ($text -match $pat) {
                $hitsTotal++
                if (-not (Test-Allowed $rel)) {
                    $lineNum = $i + 1
                    $trimmed = $text.Trim()
                    $violations.Add("${rel}:${lineNum}: ${trimmed}")
                }
                break
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $vcount = $violations.Count
    Write-Host "grep-audit: $vcount unapproved raw-access site(s) found (total pattern hits: $hitsTotal)"
    foreach ($v in $violations) {
        Write-Host $v
    }
    exit 1
}

Write-Host "grep-audit: OK ($hitsTotal pattern hit(s), all in allowlisted files)"
exit 0
