# Race gate "the recorder ban" (multiplayer design 15.4, plan R1-06 / OV-12): no race file
# names the recorders, the recording store, the pending-tree slot or the tree commit and
# discard services.
#
# Why: RaceWorld may reference the Recording module (the sidecar codecs and the recording data
# types, inside the loader only), and that module also holds the recorders and the recording
# stores. RaceExport reads the own recordings through EffectiveState only. The other half of the
# rule, "Recording types only in the loader", needs the module map and is the architecture
# cell RaceLoaderFilesTests in scripts/arch/test_archview.py (scripts/race-loader-files-allowlist.txt).
#
# Scan: the shared race scan (race-source-scan.ps1) over every race file (RaceCore, RaceExport,
# RaceWorld, Controllers/ParsekRace*.cs): comments stripped, literal contents masked,
# interpolation holes kept as code; a `using X =` alias and a backslash in code are refused.
# Any identifier below is a hit, qualified or not. No allowlist.
# Managed mirror: RaceNoRecorderViolations in Source/Parsek.Tests/GrepAuditTests.Race.cs.
#
# Exit 0: clean. Exit 1: violations, printed as "<file>:<line>: [kind] <name>".
# Exit 2: misconfiguration (no race file, no controller file, a broken parse, a file the lexer
# cannot model).

param(
    [string]$RepoRoot,
    [int]$MinTypeReferences = 24
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

. (Join-Path $PSScriptRoot 'race-source-scan.ps1')

# Keep in step with RaceRecorderTypes in Source/Parsek.Tests/GrepAuditTests.Race.cs (a cell
# there asserts the two lists are equal).
$recorderTypes = @(
    'FlightRecorder', 'BackgroundRecorder', 'RecordingStore',
    'PendingTreeSlot', 'TreeCommitService', 'TreeDiscardService'
)
$recorderRe = [regex]::new('(?<![A-Za-z0-9_])(' + ($recorderTypes -join '|') + ')(?![A-Za-z0-9_])')

try {
    $scan = Get-RaceScan $RepoRoot
}
catch {
    [Console]::Error.WriteLine("grep-audit: $($_.Exception.Message)")
    exit 2
}
$problem = Get-RaceScanVacuity $scan 'race-no-recorder' $MinTypeReferences $false
if ($problem) {
    [Console]::Error.WriteLine("grep-audit: $problem")
    exit 2
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($file in $scan.Files) {
    for ($i = 0; $i -lt $file.Lines.Length; $i++) {
        if (Test-RaceStructural $file $i $violations) { continue }
        foreach ($m in $recorderRe.Matches($file.Lines[$i])) {
            $violations.Add($file.Rel + ':' + ($i + 1) + ': [recorder] ' + $m.Groups[1].Value)
        }
    }
}
$violations.Sort([System.StringComparer]::Ordinal)

if ($violations.Count -gt 0) {
    Write-Host "grep-audit: $($violations.Count) recorder reference(s), alias(es) or escape(s) in the race files."
    Write-Host "No race file names the recorders, the recording store, the pending-tree slot or the tree commit and discard services (design 15.4)."
    foreach ($v in $violations) { Write-Host $v }
    exit 1
}

Write-Host "grep-audit: OK (no race file names a recorder; $($scan.Files.Count) race file(s), $($scan.TypeReferences) type reference(s) scanned)"
exit 0
