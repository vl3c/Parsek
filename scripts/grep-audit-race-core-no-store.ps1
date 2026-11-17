# Race gate "No store in the rules" (multiplayer design 15.4, plan R1-06): no file of the
# RaceCore module names a type whose name ends in `Store`.
#
# Why: RaceCore is the rules module, headless and above nothing but the floor. The module map
# cannot give this alone, because MilestoneStore and GroupHierarchyStore are floor types the
# map allows. A rule that needs a store's data takes it as plain rows from RaceExport or
# RaceWorld.
#
# Scan: the shared race scan (race-source-scan.ps1), RaceCore files only: comments stripped,
# literal contents masked, interpolation holes kept as code; a `using X =` alias and a
# backslash in code are refused. Any identifier ending in `Store` is a hit, qualified or not.
# Managed mirror: RaceCoreNoStoreViolations in Source/Parsek.Tests/GrepAuditTests.Race.cs.
#
# Exit 0: clean. Exit 1: violations, printed as "<file>:<line>: [kind] <name>".
# Exit 2: misconfiguration (no RaceCore file, no controller file, a broken parse, a file the
# lexer cannot model).

param(
    [string]$RepoRoot,
    [int]$MinTypeReferences = 24
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

. (Join-Path $PSScriptRoot 'race-source-scan.ps1')

$storePattern = '(?<![A-Za-z0-9_])([A-Z][A-Za-z0-9_]*Store)(?![A-Za-z0-9_])'
$storeRe = [regex]::new($storePattern)

try {
    $scan = Get-RaceScan $RepoRoot
}
catch {
    [Console]::Error.WriteLine("grep-audit: $($_.Exception.Message)")
    exit 2
}
$problem = Get-RaceScanVacuity $scan 'race-core-no-store' $MinTypeReferences $true
if ($problem) {
    [Console]::Error.WriteLine("grep-audit: $problem")
    exit 2
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($file in $scan.Files) {
    if ($file.Module -cne 'RaceCore') { continue }
    for ($i = 0; $i -lt $file.Lines.Length; $i++) {
        if (Test-RaceStructural $file $i $violations) { continue }
        foreach ($m in $storeRe.Matches($file.Lines[$i])) {
            $violations.Add($file.Rel + ':' + ($i + 1) + ': [store] ' + $m.Groups[1].Value)
        }
    }
}
$violations.Sort([System.StringComparer]::Ordinal)

if ($violations.Count -gt 0) {
    Write-Host "grep-audit: $($violations.Count) store reference(s), alias(es) or escape(s) in RaceCore."
    Write-Host "RaceCore is the rules module (design 15.4): it names no store. Hand the rule plain rows from RaceExport or RaceWorld."
    foreach ($v in $violations) { Write-Host $v }
    exit 1
}

Write-Host "grep-audit: OK (RaceCore names no store; $($scan.Files.Count) race file(s), $($scan.TypeReferences) type reference(s) scanned)"
exit 0
