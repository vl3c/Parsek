# Race gate "No writer of the own model" (multiplayer design 15.4, plan R1-06 / OV-11, the ONE
# no-writer gate): no race file names a store of the save or a persistence writer of the save,
# except a file the allowlist names for an entry, with its reason.
#
# The denylist and the allowlist are one data file, scripts/race-no-writer-allowlist.txt; its
# header states the line grammar, why the stores are denied whole, and the two writes design
# 15.4 names (RaceSites, RaceLooks), reserved for the tasks that write them.
#
# Scan: the shared race scan (race-source-scan.ps1) over every race file (RaceCore, RaceExport,
# RaceWorld, Controllers/ParsekRace*.cs): comments stripped, literal contents masked,
# interpolation holes kept as code; a `using X =` alias and a backslash in code are refused.
# Managed mirror: RaceNoWriterViolations in Source/Parsek.Tests/GrepAuditTests.Race.cs.
#
# Exit 0: clean. Exit 1: violations, printed as "<file>:<line>: [kind] <name>" (a denied name,
# an alias, an escape, a malformed allowlist line, an allow line naming no deny entry or with
# no hit left). Exit 2: misconfiguration (no allowlist, an empty denylist, no race file, no
# controller file, a broken parse, a file the lexer cannot model).

param(
    [string]$RepoRoot,
    [int]$MinTypeReferences = 24
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

. (Join-Path $PSScriptRoot 'race-source-scan.ps1')

$source = 'scripts/race-no-writer-allowlist.txt'
$allowPath = [System.IO.Path]::Combine((Resolve-Path -LiteralPath $RepoRoot).Path, 'scripts', 'race-no-writer-allowlist.txt')
if (-not [System.IO.File]::Exists($allowPath)) {
    [Console]::Error.WriteLine("grep-audit: race-no-writer: allowlist not found: $allowPath")
    exit 2
}

$identRe = [regex]::new('^[A-Za-z_][A-Za-z0-9_]*$')
$deny = [System.Collections.Generic.List[object]]::new()
$allow = [System.Collections.Generic.List[object]]::new()
$violations = [System.Collections.Generic.List[string]]::new()
$allowLines = [System.IO.File]::ReadAllLines($allowPath)
for ($n = 0; $n -lt $allowLines.Length; $n++) {
    $entry = $allowLines[$n].Trim()
    if ($entry.Length -eq 0 -or $entry.StartsWith('#', [System.StringComparison]::Ordinal)) { continue }
    $parts = $entry.Split([char[]]@([char]' ', [char]9), [System.StringSplitOptions]::RemoveEmptyEntries)
    $ok = $parts.Length -ge 4
    if ($ok -and $parts[0] -ceq 'deny' -and ($parts[1] -ceq 'type' -or $parts[1] -ceq 'member') -and $identRe.IsMatch($parts[2])) {
        if ($parts[1] -ceq 'member') { $key = '.' + $parts[2] } else { $key = $parts[2] }
        $deny.Add([pscustomobject]@{ Kind = $parts[1]; Name = $parts[2]; Key = $key })
        continue
    }
    if ($ok -and $parts[0] -ceq 'allow' -and $parts[1].StartsWith('Source/Parsek/', [System.StringComparison]::Ordinal) `
            -and $identRe.IsMatch($parts[2].TrimStart([char]'.')) -and $parts[2].LastIndexOf([char]'.') -le 0) {
        $allow.Add([pscustomobject]@{ File = $parts[1]; Key = $parts[2]; Line = $n + 1 })
        continue
    }
    $violations.Add($source + ':' + ($n + 1) + ': [bad-line] ' + $parts[0])
}
if ($deny.Count -eq 0) {
    [Console]::Error.WriteLine("grep-audit: race-no-writer: the allowlist denies nothing; this gate is vacuous")
    exit 2
}

try {
    $scan = Get-RaceScan $RepoRoot
}
catch {
    [Console]::Error.WriteLine("grep-audit: $($_.Exception.Message)")
    exit 2
}
$problem = Get-RaceScanVacuity $scan 'race-no-writer' $MinTypeReferences $false
if ($problem) {
    [Console]::Error.WriteLine("grep-audit: $problem")
    exit 2
}

$denyKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($d in $deny) { [void]$denyKeys.Add($d.Key) }
$allowed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($a in $allow) { [void]$allowed.Add($a.File + ' ' + $a.Key) }
$used = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$patterns = [System.Collections.Generic.List[object]]::new()
foreach ($d in $deny) {
    if ($d.Kind -ceq 'member') {
        $re = [regex]::new('\.\s*@?' + $d.Name + '(?![A-Za-z0-9_])')
    }
    else {
        $re = [regex]::new('(?<![A-Za-z0-9_])' + $d.Name + '(?![A-Za-z0-9_])')
    }
    $patterns.Add([pscustomobject]@{ Key = $d.Key; Re = $re })
}

foreach ($file in $scan.Files) {
    for ($i = 0; $i -lt $file.Lines.Length; $i++) {
        if (Test-RaceStructural $file $i $violations) { continue }
        foreach ($p in $patterns) {
            $hits = $p.Re.Matches($file.Lines[$i]).Count
            if ($hits -eq 0) { continue }
            $pair = $file.Rel + ' ' + $p.Key
            if ($allowed.Contains($pair)) {
                [void]$used.Add($pair)
                continue
            }
            for ($h = 0; $h -lt $hits; $h++) {
                $violations.Add($file.Rel + ':' + ($i + 1) + ': [writer] ' + $p.Key)
            }
        }
    }
}
foreach ($a in $allow) {
    if (-not $denyKeys.Contains($a.Key)) {
        $violations.Add($source + ':' + $a.Line + ': [bad-allow] ' + $a.Key)
    }
    elseif (-not $used.Contains($a.File + ' ' + $a.Key)) {
        $violations.Add($source + ':' + $a.Line + ': [stale-allow] ' + $a.Key)
    }
}
$violations.Sort([System.StringComparer]::Ordinal)

if ($violations.Count -gt 0) {
    Write-Host "grep-audit: $($violations.Count) writer reference(s) or allowlist problem(s) in the race files."
    Write-Host "No race file names a store of the save or a persistence writer of the save (design 15.4). A file that must name one gets an 'allow' line with its reason in $source."
    foreach ($v in $violations) { Write-Host $v }
    exit 1
}

Write-Host "grep-audit: OK (no race file names a writer of the own model; $($deny.Count) denied name(s), $($allow.Count) allow line(s), $($scan.Files.Count) race file(s), $($scan.TypeReferences) type reference(s) scanned)"
exit 0
