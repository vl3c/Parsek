# GUI-state-gallery write-set gate: the mock applier and the compiled catalogue
# must touch UI-LAYER state and nothing else.
#
# Why: the crash-safety argument of the gallery (docs/dev/design-gui-state-gallery.md
# section 7.5, layer 1) is STRUCTURAL rather than procedural. A mocked view model
# reaches no save because every member the applier writes is a UI-layer field that
# ParsekScenario.OnSave, the sidecar writers, RecordingStore, the Ledger,
# MissionStore and RouteStore never read. That claim is only worth making while it
# stays mechanically true, so this gate fails the build the moment a gallery file
# names a store, a persistence writer or a file-system API.
#
# Scan set (INVERTED from the sibling allowlist gates: those scan the whole tree and
# allow named files, this one scans named files and allows nothing):
#   Source/Parsek/UI/Gallery/**/*.cs
#   Source/Parsek/TestCommands/ParsekTestCommandAddon.UiMock.cs
#   Source/Parsek/TestCommands/TestCommandUiMock.cs
#
# Matching runs over COMMENT-STRIPPED lines, and that is the deciding detail: these
# files' own headers explain at length which stores they do NOT touch, so a scan
# over raw text would fire on the very prose that documents the rule and would have
# to be silenced by rewording the explanation away. Line comments (// and ///) are
# removed; the gallery files use no block comments and the gate asserts that (a
# /* */ opener anywhere in the scan set is a misconfiguration, exit 2, because the
# line-based stripper cannot see inside one).
#
# Exit 0: no forbidden reference (zero is the expected, healthy state).
# Exit 1: at least one; offending sites print as "<file>:<line>: <match>".
# Exit 2: script misconfiguration (missing file, or a block comment in scope).

param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$sourceRoot = Join-Path $RepoRoot "Source/Parsek"
$galleryDir = Join-Path $sourceRoot "UI/Gallery"

if (-not (Test-Path $galleryDir)) {
    Write-Error "grep-audit: gallery directory not found (this gate is vacuous): $galleryDir"
    exit 2
}

$scanFiles = New-Object System.Collections.Generic.List[string]
foreach ($f in (Get-ChildItem -Path $galleryDir -Recurse -Filter *.cs -File)) {
    $scanFiles.Add($f.FullName)
}
foreach ($rel in @(
        "TestCommands/ParsekTestCommandAddon.UiMock.cs",
        "TestCommands/TestCommandUiMock.cs")) {
    $p = Join-Path $sourceRoot $rel
    if (-not (Test-Path $p)) {
        Write-Error "grep-audit: scan-set file not found (this gate is vacuous): $p"
        exit 2
    }
    $scanFiles.Add((Resolve-Path $p).Path)
}

if ($scanFiles.Count -lt 5) {
    Write-Error "grep-audit: only $($scanFiles.Count) gallery file(s) in the scan set; the layout moved and this gate is vacuous."
    exit 2
}

# The forbidden vocabulary: every store, persistence writer and file-system API a
# gallery file could reach for. Keep in step with the managed fallback in
# Source/Parsek.Tests/GrepAuditTests.cs.
#
# Deliberately ABSENT, and each for a stated reason:
#   KerbalsModule.      - the catalogue constructs KerbalSlot / KerbalReservation
#                         value objects and calls the PURE static
#                         ResolveActiveChainIndex. Neither reads the live module.
#   ContractsModule / StrategiesModule / FacilitiesModule / MilestonesModule
#                       - the Career builders construct FRESH instances to hand to
#                         CareerStateWindowUI.Build, which reads them only for slot
#                         helpers. LedgerOrchestrator.* (the accessor that would
#                         reach the LIVE ones) IS forbidden below.
$patterns = @(
    'RecordingStore.',
    'RecordingGroupStore.',
    'GroupHierarchyStore.',
    'MissionStore.',
    'RouteStore.',
    'MilestoneStore.',
    'LedgerOrchestrator.',
    'EffectiveState.',
    'GamePersistence.',
    'ParsekScenario.',
    'ParsekSettings.',
    'FileIOUtils.',
    'ConfigNode',
    'File.Write',
    'File.Delete',
    'File.Copy',
    'File.Move',
    'Directory.Create',
    'Directory.Delete',
    'RecordingPaths.'
)

$violations = New-Object System.Collections.Generic.List[string]
$repoRootFull = (Resolve-Path $RepoRoot).Path -replace '\\', '/'

foreach ($full in $scanFiles) {
    $fullPath = $full -replace '\\', '/'
    $rel = $fullPath
    if ($rel.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $rel.Substring($repoRootFull.Length).TrimStart('/')
    }

    $lines = Get-Content -LiteralPath $full
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $raw = $lines[$i]
        if ($raw -clike '*/[*]*') {
            Write-Error "grep-audit: block comment in ${rel}:$($i + 1); the line-based comment stripper cannot see inside one. Use line comments in the gallery files."
            exit 2
        }
        # Strip the line comment BEFORE matching (see the header).
        $cut = $raw.IndexOf('//')
        $code = if ($cut -ge 0) { $raw.Substring(0, $cut) } else { $raw }
        if ($code.Trim().Length -eq 0) { continue }
        foreach ($pat in $patterns) {
            if ($code -clike "*$pat*") {
                $violations.Add("${rel}:$($i + 1): $($code.Trim())")
                break
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "grep-audit: $($violations.Count) forbidden store / persistence reference(s) in the GUI-state-gallery write set."
    Write-Host "A mocked view model must reach NO save: every member the applier writes is a UI-layer field no writer reads (docs/dev/design-gui-state-gallery.md section 7.5, layer 1). If a gallery state genuinely needs effective state, route it through EffectiveState.ComputeERS/ComputeELS in a NON-gallery file and hand the result in."
    foreach ($v in $violations) { Write-Host $v }
    exit 1
}

Write-Host "grep-audit: OK (GUI-state-gallery write set is UI-only across $($scanFiles.Count) file(s))"
exit 0
