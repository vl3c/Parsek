# Phase 8 of Basic / Advanced UI mode (design §13.4): enforce the section 9
# visibility-only invariant mechanically. The UI complexity-mode vocabulary
# (UiComplexityMode / UiSurfaceVisibility / UiSurface / uiComplexityMode) may
# appear ONLY in UI draw paths, the two deferred-apply Update hosts, and the
# settings layer. Any other file naming the mode is recording, playback,
# logistics, ledger or rewind code becoming mode-dependent, which the feature
# explicitly forbids.
#
# Scan root is Source/Parsek, so Source/Parsek.Tests is out of scope for free.
# Source/Parsek/InGameTests is IN the scan root and is allowlisted explicitly.
#
# Matching is plain SUBSTRING (case-sensitive), not word-boundary: an
# identifier such as ApplyPendingUiComplexityModeIfAny carries the vocabulary
# even though \b would not fire around it, and an identifier smuggling the mode
# into a recorder file is exactly the rot this gate exists to stop.
#
# Exit 0: every hit's file is allowlisted.
# Exit 1: at least one unapproved mode reference exists; offending sites are
#         printed to stdout in "<file>:<line>: <match>" format.
# Exit 2: script misconfiguration (missing source root or allowlist).

param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$sourceRoot = Join-Path $RepoRoot "Source/Parsek"
$allowlistPath = Join-Path $PSScriptRoot "ui-complexity-mode-audit-allowlist.txt"

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

# The mode vocabulary per design §13.4. UiSurfaceVisibility subsumes UiSurface
# as a substring; both are listed so the enforced vocabulary reads verbatim
# against the doc.
$patterns = @(
    'UiComplexityMode',
    'UiSurfaceVisibility',
    'UiSurface',
    'uiComplexityMode'
)

$violations = New-Object System.Collections.Generic.List[string]
$hitsTotal = 0

$repoRootFull = (Resolve-Path $RepoRoot).Path -replace '\\', '/'

$files = Get-ChildItem -Path $sourceRoot -Recurse -Filter *.cs -File
foreach ($file in $files) {
    $fullPath = $file.FullName -replace '\\', '/'
    # Relative to repo root so allowlist entries can be written as e.g.
    # Source/Parsek/ParsekUI.cs
    $rel = $fullPath
    if ($rel.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $rel.Substring($repoRootFull.Length).TrimStart('/')
    }

    $lines = Get-Content -LiteralPath $file.FullName
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $text = $lines[$i]
        foreach ($pat in $patterns) {
            if ($text -clike "*$pat*") {
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
    Write-Host "grep-audit: $vcount unapproved UI complexity-mode reference(s) found (total pattern hits: $hitsTotal)"
    foreach ($v in $violations) {
        Write-Host $v
    }
    exit 1
}

Write-Host "grep-audit: OK ($hitsTotal pattern hit(s), all in allowlisted files)"
exit 0
