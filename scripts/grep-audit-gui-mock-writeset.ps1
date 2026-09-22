# GUI-state-gallery write-set gate: the mock applier and the compiled catalogue may
# reference only an ALLOWLISTED set of types, and may not alias any of them.
#
# Why: the crash-safety argument of the gallery (docs/dev/design-gui-state-gallery.md
# section 7.5, layer 1) is STRUCTURAL rather than procedural. A mocked view model reaches
# no save because every member the applier writes is a UI-layer field that
# ParsekScenario.OnSave, the sidecar writers, RecordingStore, the Ledger, MissionStore and
# RouteStore never read. That claim is only worth making while it stays mechanically true.
#
# WHY AN ALLOWLIST AND NOT A DENYLIST. The first version of this gate banned a list of
# store names and had holes a review walked straight through: `Ledger.AddActions(...)`,
# `CrewReservationManager.ClearReplacementsInternal()` and - worst - `RS.ResetForTesting()`
# behind `using RS = Parsek.RecordingStore;` all passed with exit 0. A denylist can only
# ban the writers somebody thought of. An allowlist inverts the burden: a gallery file that
# needs a new type says so here, in a review, with a reason.
#
# Scan set (INVERTED from the sibling allowlist gates: those scan the whole tree and allow
# named FILES, this one scans named files and allows named TYPES):
#   Source/Parsek/UI/Gallery/**/*.cs
#   Source/Parsek/TestCommands/ParsekTestCommandAddon.UiMock.cs
#   Source/Parsek/TestCommands/TestCommandUiMock.cs
#
# Matching runs over COMMENT-STRIPPED lines, and that is the deciding detail: these files'
# own headers explain at length which stores they do NOT touch, so a scan over raw text
# would fire on the very prose that documents the rule and would have to be silenced by
# deleting the explanation. Line comments (// and ///) are removed; the gallery files use
# no block comments and the gate asserts that (a /* opener anywhere in the scan set is a
# misconfiguration, exit 2, because the line-based stripper cannot see inside one).
#
# Exit 0: every referenced type is allowlisted and no alias exists.
# Exit 1: at least one is not; offending sites print as "<file>:<line>: <match>".
# Exit 2: script misconfiguration (missing file, block comment in scope, broken parse).

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

# THE ALLOWLIST: every type a gallery file may name, and why it is safe.
# Keep in step with GuiMockAllowedTypes in Source/Parsek.Tests/GrepAuditTests.cs; a cell
# there asserts the two lists are equal, so neither can drift.
#
# The rule for adding one: it must be a PURE presentation / model type or a PURE builder.
# Nothing that reads a store, writes a save, or holds live KSP state belongs here - where a
# gallery state needs effective state, it is handed in by a non-gallery caller.
$allowedTypes = @(
    # --- the gallery's own surface ---
    'GuiMockSession', 'GuiMockCatalogue', 'GuiMockState', 'GuiMockPayload',
    'GuiMockStructure', 'GuiMockWitness', 'GuiMockSuppressionSite',
    'GuiMockKerbalsStates', 'GuiMockCareerStates', 'GuiMockStructureStates',
    'MissionInputs', 'RosterInputs', 'RouteShape', 'FlightShape',

    # --- the seam halves this op is part of ---
    'TestCommandUiMock', 'TestCommandUiAction', 'TestCommandUiState',
    'TestCommandUiFind', 'TestCommandCaptureScreenshot', 'TestCommandDumpGuiTree',
    'TestCommandSaveGame', 'TestCommandScene', 'UiActionOp', 'UiActionRect',
    'UiWindowSpec', 'UiWindowHandle', 'UiActionPending', 'UiActionSettleOutcome',
    'GuiTreeDumpPollOutcome', 'ParsedCommand', 'DeferralBudget',
    'ParsekTestCommandAddon', 'MockIntent',

    # --- the windows the applier injects into, and their PURE model types ---
    'KerbalsWindowUI', 'KerbalsPresentation', 'CareerStateWindowUI',
    'StructureListWindowUI', 'ParsekUI', 'UiComplexityMode', 'UiSurface',
    'UiSurfaceVisibility',

    # --- pure presentation / model / builder types the states construct or call ---
    'KerbalsModule', 'KerbalEndState', 'GameAction', 'GameActionType',
    'StrategyResource', 'ContractsModule', 'StrategiesModule', 'FacilitiesModule',
    'MilestonesModule', 'Game',
    'Recording', 'RecordingTree', 'BranchPoint', 'BranchPointType', 'PartEvent',
    'PartEventType', 'TerminalState', 'StructureStep', 'StructureStepKind',
    'MissionStructure', 'MissionStructureBuilder', 'MissionStructureListBuilder',
    'MissionCompositionBuilder', 'StructureLocationFormatter',
    'Route', 'RouteStop', 'RouteEndpoint', 'RouteConnectionWindow',
    'RouteStructureListBuilder', 'RouteEndpointLocationFormatter',

    # --- the observability + capture surfaces (read-only, or write ONE artifact) ---
    'ParsekLog', 'GuiTreeRecorder', 'GuiTreeResult', 'GuiTreeNode', 'GuiTreeAssembler',

    # --- namespaces, BCL and Unity value types ---
    'System', 'Parsek', 'Logistics', 'Gallery', 'TestCommands', 'UI',
    'Action', 'Func', 'List', 'Dictionary', 'HashSet', 'IEnumerable',
    'IReadOnlyList', 'IReadOnlyCollection', 'IReadOnlyDictionary',
    'KeyValuePair', 'StringComparer', 'StringComparison', 'CultureInfo',
    'Exception', 'InvalidOperationException', 'ArgumentOutOfRangeException',
    'DateTime', 'Math', 'Enum', 'StringBuilder', 'Guid', 'Globalization',
    'Rect', 'Time', 'UnityEngine', 'Object'
)
$allowedSet = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
foreach ($t in $allowedTypes) { [void]$allowedSet.Add($t) }

$violations = New-Object System.Collections.Generic.List[string]
$referencesSeen = 0
$repoRootFull = (Resolve-Path $RepoRoot).Path -replace '\\', '/'

# A "<Type>." reference: an upper-case-initial identifier followed by a dot, not itself
# preceded by an identifier character or a dot (so `a.Foo.Bar` contributes nothing and
# `Foo.Bar` contributes `Foo`). That is the shape of every static call, every nested-type
# reference and every qualified name - the whole surface a gallery file could reach a
# store through.
$refRe = [regex]'(?<![A-Za-z0-9_.])([A-Z][A-Za-z0-9_]*)\s*\.'
# `using X = Y;` - banned outright. An alias defeats a name-based gate by construction,
# which is exactly how `RS.ResetForTesting()` got through the first version.
$aliasRe = [regex]'^\s*using\s+[A-Za-z_][A-Za-z0-9_]*\s*='
# A double-quoted string literal, escapes included. Its CONTENTS are masked away before
# the type scan (see the call site).
$literalRe = [regex]'"(?:[^"\\]|\\.)*"'

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
        # Strip the line comment BEFORE matching (see the header), then MASK STRING
        # LITERAL CONTENTS. The mask is not optional: a catalogue state's Covers keys are
        # literals like "RosterStatus.Lost", and without it every one of them reads as a
        # type reference - which is the same comments-read-as-code trap one layer in.
        $cut = $raw.IndexOf('//')
        $code = if ($cut -ge 0) { $raw.Substring(0, $cut) } else { $raw }
        $code = $literalRe.Replace($code, '""')
        if ($code.Trim().Length -eq 0) { continue }

        if ($aliasRe.IsMatch($code)) {
            $violations.Add("${rel}:$($i + 1): [alias] $($code.Trim())")
            continue
        }

        foreach ($m in $refRe.Matches($code)) {
            $name = $m.Groups[1].Value
            $referencesSeen++
            if (-not $allowedSet.Contains($name)) {
                $violations.Add("${rel}:$($i + 1): [type] $name -- $($code.Trim())")
            }
        }
    }
}

if ($referencesSeen -lt 100) {
    Write-Error "grep-audit: only $referencesSeen type reference(s) parsed across the gallery files; the parse broke and this gate is vacuous."
    exit 2
}

if ($violations.Count -gt 0) {
    Write-Host "grep-audit: $($violations.Count) un-allowlisted type reference(s) or alias(es) in the GUI-state-gallery write set (of $referencesSeen references scanned)."
    Write-Host "A mocked view model must reach NO save: every member the applier writes is a UI-layer field no writer reads (docs/dev/design-gui-state-gallery.md section 7.5, layer 1). Add a PURE type to the allowlist in this script AND to GuiMockAllowedTypes in Source/Parsek.Tests/GrepAuditTests.cs, with a reason - or hand the data in from a non-gallery caller. Aliases are banned outright: they defeat a name-based gate by construction."
    foreach ($v in $violations) { Write-Host $v }
    exit 1
}

Write-Host "grep-audit: OK (GUI-state-gallery write set references $referencesSeen allowlisted type(s) across $($scanFiles.Count) file(s), no aliases)"
exit 0
