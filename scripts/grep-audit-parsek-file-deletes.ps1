# Parsek-file delete grep gate: only the collector deletes Parsek's own files.
#
# Why: design-load-restores-the-save.md 4.5. A recording sidecar, a rewind point file or a
# rewind save may still be named by an older save in the folder, so nothing but
# ParsekFileCollector (which reads every save first) may delete a file under
# Parsek/Recordings, Parsek/RewindPoints or Parsek/Saves. SidecarFileCommitBatch owns the
# staged writes and the retained versions.
#
# Scan root is Source/Parsek. Matching is plain SUBSTRING (case-sensitive) on RAW source
# lines, comments included, one hit per line, like the sibling gates. A file must be
# listed in scripts/parsek-file-deletes-allowlist.txt; an entry with a count pins the
# file's exact hit count, so a new delete in an already-listed file still reds the gate.
#
# Exit 0: every hit is in a listed file and every pinned count matches.
# Exit 1: an unlisted hit or a pin mismatch; details printed to stdout.
# Exit 2: script misconfiguration (missing source root or allowlist, bad entry).
#
# Keep in step with the managed fallback in Source/Parsek.Tests/GrepAuditTests.cs.

param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$sourceRoot = Join-Path $RepoRoot "Source/Parsek"
$allowlistPath = Join-Path $PSScriptRoot "parsek-file-deletes-allowlist.txt"

if (-not (Test-Path $sourceRoot)) {
    Write-Error "grep-audit: source root not found: $sourceRoot"
    exit 2
}
if (-not (Test-Path $allowlistPath)) {
    Write-Error "grep-audit: allowlist not found: $allowlistPath"
    exit 2
}

$ownerFiles = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
$pinnedFiles = [System.Collections.Generic.Dictionary[string,int]]::new([System.StringComparer]::OrdinalIgnoreCase)
$allowedPrefixes = New-Object System.Collections.Generic.List[string]
foreach ($rawLine in Get-Content $allowlistPath) {
    $line = $rawLine.Trim()
    if ($line.Length -eq 0) { continue }
    if ($line.StartsWith('#')) { continue }
    $parts = $line -split '\s+'
    $path = $parts[0] -replace '\\', '/'
    if ($parts.Length -gt 2) {
        Write-Error "grep-audit: bad allowlist entry: $line"
        exit 2
    }
    if ($path.EndsWith('/')) {
        $allowedPrefixes.Add($path)
    } elseif ($parts.Length -eq 2) {
        $n = 0
        if (-not [int]::TryParse($parts[1], [ref]$n)) {
            Write-Error "grep-audit: bad allowlist count: $line"
            exit 2
        }
        $pinnedFiles[$path] = $n
    } else {
        [void]$ownerFiles.Add($path)
    }
}

$patterns = @(
    'File.Delete(',
    'File.Move(',
    'File.Replace(',
    'Directory.Delete('
)

$repoRootFull = (Resolve-Path $RepoRoot).Path -replace '\\', '/'
$violations = New-Object System.Collections.Generic.List[string]
$hitsByFile = @{}
$hitsTotal = 0

$files = Get-ChildItem -Path $sourceRoot -Recurse -Filter *.cs -File
foreach ($file in $files) {
    $fullPath = $file.FullName -replace '\\', '/'
    $rel = $fullPath
    if ($rel.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $rel.Substring($repoRootFull.Length).TrimStart('/')
    }

    $isPrefixed = $false
    foreach ($p in $allowedPrefixes) {
        if ($rel.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) { $isPrefixed = $true; break }
    }

    $hitLines = New-Object System.Collections.Generic.List[string]
    $lines = @(Get-Content -LiteralPath $file.FullName)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $text = $lines[$i]
        foreach ($pat in $patterns) {
            if ($text -clike "*$pat*") {
                $lineNum = $i + 1
                $trimmed = $text.Trim()
                $hitLines.Add("${rel}:${lineNum}: ${trimmed}")
                break
            }
        }
    }
    if ($hitLines.Count -eq 0) { continue }
    $hitsTotal += $hitLines.Count
    $hitsByFile[$rel] = $hitLines.Count

    if ($isPrefixed -or $ownerFiles.Contains($rel)) { continue }
    if ($pinnedFiles.ContainsKey($rel)) {
        $pin = $pinnedFiles[$rel]
        if ($pin -ne $hitLines.Count) {
            $found = $hitLines.Count
            $violations.Add("${rel}: pinned ${pin} delete-family hit(s), found ${found}")
            foreach ($h in $hitLines) { $violations.Add("  $h") }
        }
        continue
    }
    foreach ($h in $hitLines) { $violations.Add($h) }
}

foreach ($pinned in $pinnedFiles.Keys) {
    if (-not $hitsByFile.ContainsKey($pinned)) {
        $pin = $pinnedFiles[$pinned]
        $violations.Add("${pinned}: pinned ${pin} delete-family hit(s), found 0 (stale entry)")
    }
}

if ($violations.Count -gt 0) {
    Write-Host "grep-audit: Parsek-file delete gate failed (total pattern hits: $hitsTotal)."
    Write-Host "Only ParsekFileCollector deletes files under Parsek/Recordings, Parsek/RewindPoints or Parsek/Saves (design-load-restores-the-save.md 4.5). Route the delete through the collector, or re-read the site and update its pin in scripts/parsek-file-deletes-allowlist.txt."
    foreach ($v in $violations) {
        Write-Host $v
    }
    exit 1
}

Write-Host "grep-audit: OK ($hitsTotal delete-family hit(s), all in allowlisted files with matching pins)"
exit 0
