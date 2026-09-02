# Background-thread grep gate: Source/Parsek must start NO thread of its own.
#
# Why: KSP 1.12.5 pins the MAIN thread to CultureInfo.CreateSpecificCulture("en")
# in HighLogic.Awake, and that pin is per-thread. Parsek relies on it for the
# ~1400 culture-sensitive format specifiers inside ParsekLog.* calls (dot
# decimals in every ut= value, harness logContract regexes pinning \.); see the
# "InvariantCulture" rule under "KSP API & code gotchas" in .claude/CLAUDE.md.
# Code running on any other thread formats under the OS culture instead, so a
# background thread silently breaks every log line it writes on a ro-RO machine.
# Unity API access is main-thread-only as well, so a thread start in this mod is
# almost always a bug even before culture enters the picture.
#
# Scan root is Source/Parsek (Source/Parsek.Tests is out of scope for free;
# Source/Parsek/InGameTests IS in scope and stays gated).
#
# Matching is plain SUBSTRING (case-sensitive) on RAW source lines, comments
# included, like the sibling allowlist gates: a comment naming one of these
# tokens trips the gate loudly rather than being silently skipped. Reword the
# comment (name the API in prose, not as a call) instead of allowlisting a file.
# The allowlist exists only for the day a thread is deliberately introduced
# together with its own per-thread culture handling.
#
# Exit 0: no un-allowlisted hit (zero hits is the expected, healthy state).
# Exit 1: at least one thread-start site exists outside the allowlist;
#         offending sites are printed to stdout in "<file>:<line>: <match>".
# Exit 2: script misconfiguration (missing source root or allowlist).

param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$sourceRoot = Join-Path $RepoRoot "Source/Parsek"
$allowlistPath = Join-Path $PSScriptRoot "background-threads-audit-allowlist.txt"

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

# Every .NET Framework 4.7.2 way of putting work on another thread that this
# mod could plausibly reach. Blocking calls that stay on the caller's thread
# (Thread.Sleep, Interlocked.*, lock) are deliberately NOT listed: they do not
# move code off the pinned main thread. Keep this list in step with the managed
# fallback in Source/Parsek.Tests/GrepAuditTests.cs.
$patterns = @(
    'new Thread(',
    'Task.Run(',
    'Task.Factory.StartNew(',
    'new Task(',
    '.ContinueWith(',
    'ThreadPool.QueueUserWorkItem(',
    'ThreadPool.UnsafeQueueUserWorkItem(',
    'Parallel.',
    'System.Threading.Timer',
    'System.Timers.Timer',
    'new Timer('
)

$violations = New-Object System.Collections.Generic.List[string]
$hitsTotal = 0

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
        foreach ($pat in $patterns) {
            # -clike treats [ ] ? * as wildcards; the token set above contains
            # none of them, so a literal wrap is enough.
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
    Write-Host "grep-audit: $vcount background-thread start site(s) found in Source/Parsek (total pattern hits: $hitsTotal)."
    Write-Host "Parsek must run on KSP's main thread only: the CultureInfo.CreateSpecificCulture(""en"") pin from HighLogic.Awake is PER-THREAD, so code on any other thread formats ParsekLog output under the OS culture. See the InvariantCulture rule under 'KSP API & code gotchas' in .claude/CLAUDE.md."
    foreach ($v in $violations) {
        Write-Host $v
    }
    exit 1
}

Write-Host "grep-audit: OK ($hitsTotal background-thread pattern hit(s), all in allowlisted files)"
exit 0
