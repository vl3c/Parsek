# R0b tooling and small prerefactors (P12, P11, P7, P1, P10) - implementation plan

Status: PLAN (2026-11-14), not started. Covers design `design-competitive-multiplayer.md`
sections 15.11 (rows P1, P7, P10, P11, P12), 15.4 (the forbidden-edge list form, the id check
on the floor), 15.2 / 15.14 (the `ParsekRace*` placement rule), 7.3 and 7.7 (the launch-area
shape test, the home space center value), 6.3 (id safety), 7.2 (the create-and-start core).
Phase R0b, steps 1 and 2 of the analysis's order of work.

## 1. Goal and scope

Five small changes to single-player code and tooling that land before any race code. Each
is its own pull request (or two), lands on a green tree, and changes nothing a player sees.
The two small single-player fixes this plan once carried (the wound longitude and the partial
Making History read) were answered "fix both" by the owner (question 10.1, Q-04, 2026-11-14)
and moved out of this plan to the launch pad fixes work, which lands them on main first; P1-04
is dropped and P1-02 keeps only its behavior-neutral half.

Built here:

- **P12**: `[forbidden]` in `scripts/arch/modules.toml` accepts `"From -> A | B | C"`, read
  the same way by every consumer of the file; the Controllers placement rule covers
  `ParsekRace*` root files.
- **P11**: the 25 GUI census specs stop pinning `windows=11` (the count is derived from the
  window table, offline); the two `ParsekUI` constructors become one body.
- **P7**: the pure id check moves to a `Core` file with a fixed character set, and
  `RecordingPaths.ValidateRecordingId` keeps its name, its log lines and its callers by
  delegating to it; `RecordingPaths` gains a save-root accessor with a production caller.
- **P1**: a `HomeSpaceCenter` value in `Core` that the KSC pad / runway circles read; one
  pure launch-area shape test in `Core` (circle or strip, longitude wrapped) that the 50 m
  rule calls; the launch-site circle cache keyed on the home center's version and dropped at
  the main menu.
- **P10**: the create-and-start-a-save core leaves `TestCommands` for a production file in
  `LogIO`, called by the `NewGame` verb now and by R1-10 / R1-11's `ParsekRaceSetup`
  (Controllers) later; a source gate keeps `GamePersistence.CreateNewGame(` in that one file.

Not built here (each belongs to a later phase): the 71 forbidden edges themselves and the
three race modules (R1-01); the `HomeSpaceCenter` setter `Apply` / `RestoreStock` with its
lifetime tag and the `LaunchSiteOnlyGhost` cache key (R2-05, OV-53) and the runway end and
strip half-width numbers (R2, after spike S4); the race's launch areas, which are R2-01's
`SiteRow.LaunchAreas` (with the `LaunchArea.Strip` factory) read through
`LaunchAreaGeometry.Contains` (OV-41; used by R3W-12); the race setup dialog (R1); anything
about saves, sites or ghosts beyond the above.

## 2. Starts after

- Version 1.1.0 is released and the owner says go (brief; design section 14).
- No other plan of the multiplayer program. Every task here builds on `main`. One spike:
  P1-03 waits for spike S4a.
- The launch pad fixes are on main (owner answer to question 10.1, Q-04, 2026-11-14: fix
  both, in a separate work item outside this plan). That work fixes, in place and minimally,
  the longitude wrap in today's launch-area test and the partial first read of the Making
  History sites in the `LaunchSiteExclusionZones` cache, each with a unit test, a CHANGELOG
  line and a todo entry; it does not create `HomeSpaceCenter`, `LaunchArea` or
  `LaunchAreaGeometry`. P1-02 and P1-03 edit the same files (`SpawnCollisionDetector.cs`,
  `LaunchSiteExclusionZones.cs`), so both start after it is on main.
- The VERIFY before P1-03 is written is folded into spike S4a (OV-52): whether the runtime
  alt-site read includes the KSC's own `LaunchPad` and `Runway` spawn points (section 9,
  item 5). P1-03 is correct either way; only its comment and test wording change. If they
  are included, R2-05 filters or rebuilds them.

Tasks in other slices that depend on this one:

- P12-01: R1-01, R3E-01. P12-02: R1-01, R1-03, R2-01, R3E-01, and every task that adds a
  `ParsekRace*` root file (R1-03 `ParsekRaceScenario.cs`, R1-04 `ParsekRaceBoot.cs`, R1-10
  `ParsekRaceSetup.cs`, R1-14 `ParsekRaceMenu.cs`).
- P10-01: R1-10 (runs its before-start step between `CreateGame` and `StartGame`), R1-16
  (adds `LoadAndStart` to the same file); R1-11 through R1-10 (T-11 is dropped). R1-02
  orders after P10-01 on the shared LogIO line (E.5.1).
- P7-01: R1-08, R3E-01. P7-02: none outside this plan; it stands on its own production
  caller, `ParsekFileCollector` (OV-23; R3E-01 does not use it).
- P1-01 and P1-03: R2-05. P1-02: R2-01 (builds `SiteRow.LaunchAreas` and adds
  `LaunchArea.Strip`, A1) and R3W-12 (reads those rows through `LaunchAreaGeometry.Contains`,
  OV-41).
- P11-01 and P11-02: R4-09; R4-08 lands after P11-02 (`ParsekUI.cs`).

## 3. What the code looks like today

All read on 2026-11-14 at commit abf7de3.

### 3.1 P12: forbidden edges and the placement rules

- `scripts/arch/modules.toml:562-566` holds `[forbidden] edges = ["Missions -> Logistics",
  "Ghost -> GameActions"]`; the comment above it (`:542-544`) says both lists use
  `"From -> To"`. `[allowed] edges = []` (`:568-569`).
- One loader: `archview.load_rules` (`scripts/arch/archview.py:2142-2150`) returns the raw
  list as `forbidden`. Every consumer goes through it: `layers.py:398`, `metrics.py:475`,
  `archview.py:7151`, `test_semantic_layers.py:106` and `:327`, `test_archview.py:4193`,
  `test_metrics.py:765` (the others discard it: `semantic.py:464`, `ilfacts.py:873`,
  `ilanalysis.py:1365`, `logtags.py:587`).
- Two parsers of one spec:
  - `archview.parse_edge_spec` (`archview.py:6517-6527`): splits on `->`, needs exactly two
    non-empty parts. `"A -> B | C"` is NOT rejected: it returns `("A", "B | C")`. Its
    forbidden callers are `_atlas_upward_rows` (`:3892`), the history block of `run_check`
    (`:7017`) and the violation block (`:7029`), all of which `continue` on `ValueError` and
    find no edge for `"B | C"`, so a list spec there is silently inert. `metrics.py:176`
    counts it as one declared edge.
  - `layers.forbidden_problems` (`layers.py:225-251`): the gate. A list spec reds there as
    `names no module: B | C`.
  So the design's list form is refused by the gate and silently ignored by the report.
- Tests: `test_archview.CheckerHelperTests` (`test_archview.py:1506-1542`, five
  `parse_edge_spec` cells), `test_archview.py:3580`, `:3592`, `:3817` (the `WARN bad
  [forbidden] edge spec` line), `test_archview.test_no_forbidden_edge_is_crossed_on_the_text_scan`
  (`:4187-4197`), `test_semantic_layers.ForbiddenTests` (`:260-302`) and
  `test_the_forbidden_specs_name_real_modules` (`:104-108`), the IL cells at `:327-344`.
- Docs: `docs/dev/arch/README.md:768-773` and `:2006-2008` describe `"A -> B"` only.
- Placement: rules are first-match (`archview.assign_module`, `archview.py:84-106`). The
  Controllers rule `modules.toml:320-322` is
  `^Parsek(Flight|KSC|TrackingStation|Scenario|Harmony|Playback)|^WatchMode`. Run against
  today's rules, `ParsekRaceMenu.cs` and `ParsekRaceScenario.cs` match no rule (checked with
  `assign_module`). An unmatched root file is `unclassified`, and
  `test_archview.test_every_production_file_is_placed` (`:3976-3981`) reds on it.

### 3.2 P11: the window count and the two constructors

- `windows=11` appears 100 times in 25 specs, all GUI census lanes (GUI-1 to GUI-27 except
  16 and 23), each inside a `logContracts.required` regex of the form
  `uiaction describe scene=X complexity=Y windows=11 open=N openWindows=...`.
- The number is `TestCommandUiAction.Windows.Count`, a static table:
  `Source/Parsek/TestCommands/ParsekTestCommandAddon.UiAction.cs:974-1015` builds one row
  per table entry and logs `windows={rows.Count}`. It does not depend on the scene or the
  save. `hlib.UIACTION_WINDOW_VALUES` (`harness/lib/hlib.py:3154-3165`, 11 names) is held
  byte-equal and in order to the C# table by
  `GuiCensusSeamVerbTests.test_the_window_and_op_vocabularies_mirror_the_c_sharp_tables`
  (`harness/lib/test_hlib.py:19641-19670`).
- One cell already derives the count:
  `test_the_census_specs_pinned_describe_echoes_match_their_own_steps`
  (`test_hlib.py:21303-21384`) simulates GUI-1 and GUI-2 and compares
  `len(hlib.UIACTION_WINDOW_VALUES)` to the pinned literal (regex `windows=(\d+)` at
  `:21330-21331`).
- Required patterns are plain `re.search` over KSP.log (`hlib.py:9973-9978`; also
  `run.py:2795`). Spec files are read raw by many loaders (`run.py:607`,
  `tools/tier_runner.py:1118`, `tools/mutation_check.py:90`, `test_hlib.py:56-61`), so a
  placeholder token would need every loader to expand it.
- Precedent: the Gloops removal rewrote `windows=12` to `windows=11` in every census spec
  (`docs/dev/gloops-recorder-design.md:166`). `docs/dev/design-autotest-command-seam.md:2390`
  says "every `describe` reads `windows=11`".
- `ParsekUI` constructors: `Source/Parsek/ParsekUI.cs:127-147` (flight) and `:149-169`
  (Space Center) are identical except `this.flight = flight; this.mode = UIMode.Flight;`
  versus `this.flight = null; this.mode = mode;`. `mode` and `flight` are `readonly`
  (`:23`, `:26`). The only callers are `UI/ParsekWindowSet.cs:45` and `:51`.
- Source gates that read the constructors:
  `ParsekUIWindowSetCharacterizationTests.Constructor_BuildsTheWindowsFirst_BeforeGoingLive`
  (`Source/Parsek.Tests/ParsekUIWindowSetCharacterizationTests.cs:387-412`, a theory over
  both signatures) and
  `KerbalsWindowUITests.LiveCrewRefresh_ParsekUiSubscribesAndUnsubscribesWhereTheLedgerHookIs`
  (`Source/Parsek.Tests/KerbalsWindowUITests.cs:2122-2134`, counts 2 subscriptions). The
  runtime cell `BothConstructors_BuildTheSameTenWindows_EachItsOwnInstance` (`:125-139`)
  builds both.

### 3.3 P7: the id check and the save root

- `RecordingPaths` is in module `Recording` (prefix `^Recording`). The check is
  `RecordingPaths.ValidateRecordingId(string, RecordingIdValidationLogContext)`
  (`Source/Parsek/RecordingPaths.cs:284-314`): null or empty, then `/`, `\` or `..`, then
  any char of `Path.GetInvalidFileNameChars()` (`:302`). Each refusal logs on tag `Paths`
  (Warn in production, Verbose in the `Test` context, `:316-327`), with the texts `id is
  null or empty`, `contains invalid path sequence`, `contains invalid file-name char`.
- On .NET and Mono for Windows that set is 41 chars (`"`, `<`, `>`, `|`, NUL, chars 1 to
  31, `:`, `*`, `?`, `\`, `/`); on Mono for Linux and macOS it is NUL and `/`. So an id with
  `:` passes on Linux and fails on Windows.
- Calls: 28 in 13 production files (`RecordingSidecarStore.cs:33`, `:136`, `:933`, `:958`,
  `:1003`, `:1395`; `RecordingPaths.cs:28`, `:162`; `RewindPointAuthor.cs:129`;
  `RecordingFileJanitor.cs:51`; `DiscardSidecarReap.cs:238`;
  `RewindInvoker.QuicksaveScrub.cs:304`; `Analyzer/Rules/Inv9RewindPoint.cs:100`, `:111`;
  `TestCommands/ParsekTestCommandAddon.PartAction.cs:80`;
  `TestCommands/ParsekTestCommandAddon.ReadVesselResources.cs:18`;
  `InGameTests/InGameTestSidecarReaper.cs:136`; `InGameTests/SnapshotSidecarRuntimeTests.cs:58`,
  `:150`; `InGameTests/RuntimeTests.cs:7866-7879` (four), `:8384`;
  `InGameTests/StorageRuntimeTests.cs:49`, `:66`, `:84`, `:119`) and about 25 in
  `Source/Parsek.Tests` (`RecordingPathsLoggingTests.cs`, `SyntheticRecordingTests.cs:5807-5815`,
  `Analyzer/SaveDirectoryLoader.cs:347`, `TestCommandPartActionLabTests.cs:21`,
  `TestCommandReadVesselResourcesTests.cs:31`, and comments elsewhere).
- Ids produced today: `Recording.RecordingId` is `Guid.ToString("N")` (`Recording.cs:11`),
  continuation ids `ToString("D")` (`ParsekFlightSwitchContinuation.cs:662`, `:927`,
  `:1150`), prefixes `rec_`, `rp_`, `mj_` (`RewindInvoker.MarkerWrite.cs:616`,
  `RewindPoint.cs:27`, `MergeJournalOrchestrator.cs:224`). Every non-empty recording, tree,
  rewind point and parent id in `harness/fixtures` and `Source/Parsek.Tests/Fixtures` is in
  `[A-Za-z0-9_-]` (scanned).
- `RecordingPathsLoggingTests.GetInvalidFileNameCharForTesting` (`:72-84`) and
  `StorageRuntimeTests.cs:38-45` read `Path.GetInvalidFileNameChars()` themselves, so they
  test whatever the running OS says.
- Save root: `TryGetSaveContext` (`RecordingPaths.cs:329-371`) is private. The public
  resolvers append a relative path (`ResolveSaveScopedPath`, `:67-93`, refuses an empty
  one) or a fixed folder (`ResolveGameStateDirectory`, `:256-282`); the test seam is
  `SaveRootOverrideForTesting` (`:65`). At least eight files hand-build
  `saves/<save>`; the closest to an accessor is `ParsekFileCollector.ResolveSaveDirectory`
  (`Source/Parsek/ParsekFileCollector.cs:255-279`, same module `Recording`,
  `modules.toml:197`), whose last branch reads `KSPUtil.ApplicationRootPath` and
  `HighLogic.SaveFolder` in a try / catch.
- `Core` is the explicit file list `modules.toml:477-478` (eight files);
  `test_archview.py:4101` pins 8 and `:4103-4120` pins the list; the header comment
  `modules.toml:11-12` says "nine explicit files" and `README.md:1991` says eight. `Core` is
  headless (`modules.toml:532`); its leaks are pinned in `scripts/arch/headless_pins.toml:153-169`.

### 3.4 P1: the space center position and the launch-site circles

- Constants `SpawnCollisionDetector.KscPadLatitude` / `KscPadLongitude` /
  `KscRunwayLatitude` / `KscRunwayLongitude` (`Source/Parsek/SpawnCollisionDetector.cs:216-225`,
  the runway pair is the west threshold) and `DefaultKscExclusionRadiusMeters = 50.0`
  (`:232`). `SpawnCollisionDetector.cs` is module `Ghost` (`modules.toml:96`).
- Readers of the four constants: `IsWithinKscExclusionZone` (`:240-262`, which has no
  production caller; its callers are `Bug170Tests`, `SyntheticRecordingTests` and the
  in-game `RuntimeTests`) and `ClassifyKscExclusionZone` (`:271-285`, the live reader).
- `ClassifyKscExclusionZone` is called by `LaunchSiteExclusionZones.Classify`
  (`Source/Parsek/LaunchSiteExclusionZones.cs:177-201`), which then walks the non-KSC
  circles with `SpawnCollisionDetector.SurfaceDistance`. `Classify` is called by
  `SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement` (`:353-380`),
  `VesselSpawner.KscRetirement.cs:127-131` and `LaunchSiteOnlyGhost.cs:272-274` (which
  classifies every trajectory point of a recording).
- `SurfaceDistance` (`SpawnCollisionDetector.cs:385-394`) is flat-earth and does not wrap
  longitude. It has eight other production callers that are not launch-site rules
  (`CoexistingTreeSiblingSpawn.cs:209`, `:264`; `SpawnCollisionDetector.cs:526`, `:545`,
  `:817`; `VesselSpawner.EndOfRecording.cs:131`; `SwitchSegmentNoOpClassifier.cs:819`;
  `ParsekFlight.PadEvidence.cs:236`).
- Recorded longitudes are not always in [-180, 180]: 2,686 of 118,819 `lon =` values in the
  committed fixtures are outside it, down to -550, and two `.prec.txt` points read -434.4
  and -434.5 (that is -74.4 / -74.5, over the KSC). So a wrap changes results for real data.
- The cache: `LaunchSiteExclusionZones.cachedRuntimeCircles` (`:130`), filled by
  `GetAltSiteCircles` (`:216-252`) only when the runtime read returned at least one circle,
  and then kept for the process with any fallback rows merged in (`:240-251`). It is
  cleared only by `ResetForTesting` (`:320-325`). The runtime read keeps every stock site
  whose name passes `IsStockLaunchSiteName` (`:132-142`), and that list names `LaunchPad`
  and `Runway` (`:79-83`), so the cache very likely holds the KSC's own spawn points too
  (VERIFY, section 9 item 5).
- The launch pad fixes work changes two things above before P1-02 and P1-03 start: the
  launch-area test wraps the longitude, and a partial first read is no longer cached. The
  line numbers here are from before it.
- Lifetime: `cachedRuntimeCircles` is untagged and listed in
  `scripts/arch/lifetime_pins.toml:143`.
- Log wording the lanes pin is written elsewhere and does not move:
  `VesselSpawner.KscRetirement.cs:193-224` (`flight ended within KSC exclusion zone (pad)`,
  `Spawn RETIRED for #N`). EX-1 (`harness/scenarios/EX-1-ghost-extension-past-endut.toml:162`,
  `:174`, `:182`) requires the retirement line with `lat=-0\.097[0-9] lon=-74\.557[0-9]`;
  LF-1 (`LF-1-loop-first-run-real.toml:210-211`) and LF-2 (`:298-299`) forbid `Spawn
  RETIRED` for a rover parked 59.7 m from the runway threshold. MC-4 rolls out on the
  Desert pad (`MC-4-making-history-desert.toml:74`, `:91-93`). About 30 other specs list
  `Spawn RETIRED` or `exclusion zone` (spawn lanes; grep in section 6).
- Tests over these: `Bug170Tests.cs`, `KscPadRetirementTests.cs`,
  `LaunchSiteFollowupTests.cs` (the only cache tests, `:204-238`),
  `LaunchSiteOnlyGhostTests.cs`, `SyntheticRecordingTests.cs`, `EndOfRecordingWalkbackTests.cs`,
  in-game `RuntimeTests.cs`: 99 references to the constants and the radius in six files.
- The main-menu reset: `ParsekScenario.OnMainMenuTransition`
  (`Source/Parsek/ParsekScenario.SceneTransitions.cs:246-283`).

### 3.5 P10: creating and starting a save

- The verb is two-phase. `ParsekTestCommandAddon.NewGameImpl`
  (`Source/Parsek/TestCommands/ParsekTestCommandAddon.NewGame.cs:62-162`) gates, saves
  `persistent` and loads `MAINMENU`; `TryCompleteNewGame` (`:164-248`) polls; at the settled
  main menu `CreateAndStartNewGame` (`:253-303`) deletes `saves/<save>` like stock's
  overwrite keeping `Ships/` (`DeleteSaveLikeStockOverwrite`, `:308-333`, and
  `DeleteTreeLikeStock`, `:335-348`), logs `newgame create ...`, then runs
  `GameParameters.GetDefaultParameters`, `HighLogic.CurrentGame = GamePersistence.CreateNewGame(
  save, mode, parameters, flagUrl, GameScenes.SPACECENTER, EditorFacility.None)`,
  `GameEvents.onGameNewStart.Fire()`, logs `newgame created title=...`, and calls
  `HighLogic.CurrentGame.Start()`.
- The pure half is `TestCommands/TestCommandNewGame.cs` (`ShouldPreserveSaveEntry`
  `:360-361`, `ResolveFlagUrl` `:365-366`, `FormatCreateLine` `:487-494`), tested by
  `Source/Parsek.Tests/TestCommandNewGameTests.cs`.
- `harness/lib/test_hlib.py:18778-18868` `NewGameSourceSyncTests`; its cell
  `test_the_applier_runs_stocks_own_new_game_path` (`:18812-18826`) requires the applier
  file to contain the six stock calls and the order CreateNewGame, Fire, Start.
- Specs: `CS-0-new-career-game.toml:57-58` and `CS-0-new-science-game.toml:57-58` pin
  `newgame create ...` and `newgame complete ...`; `SWS-1-save-switch-through-main-menu.toml:124`
  pins `newgame created title='[^']*' saveFolder=cs-career-mun4-b `.
- `TestCommands` is a tooling module (`modules.toml:486`). No production file calls
  `GamePersistence.CreateNewGame` (only a comment in `ParsekGameModeGate.cs:18`).
- `LogIO` (floor) is `^ParsekLog|^FileIO|^ParsekProcess|^Suppression|^PreParsek`
  (`modules.toml:325-326`); `PreParsekBackup.cs` already copies save folders there.

## 4. Tasks

### P12-01 `[forbidden]` takes a list on the right side

- **Decision.** The list form, expanded in one place, not 71 hand-written lines. Writing the
  edges out costs no code, but a missing line is a silent hole no gate can see (a gate can
  only check the edges that are written), the design's four rules become 71 lines that a
  reviewer must diff against prose, and a future module joins by one token per list
  instead of one line per pair. The expansion is about 25 lines of Python, gated by tests
  that pin it per element, so the cost is small and lives in tooling only.
- **Change.**
  - `scripts/arch/archview.py`: add `expand_edge_specs(specs)` next to `parse_edge_spec`. For
    each spec: a string with exactly one `->`, a non-empty left side with no `|`, and a
    right side whose `|`-separated elements are all non-empty after `strip()` becomes one
    `"From -> X"` per element; anything else is passed through unchanged, so the existing
    checks red on it with their existing messages. Output keeps order and drops exact
    duplicates.
  - `archview.load_rules` (`:2142-2150`): `forbidden = expand_edge_specs(raw)`. `[allowed]`
    stays one-to-one (no list form there; say so in the comment).
  - `archview.parse_edge_spec`: also raise `ValueError` when either side contains `|`, so a
    list that reached it unexpanded can never pass as a module named `"B | C"`.
  - `scripts/arch/layers.py` `forbidden_problems`: run its input through
    `archview.expand_edge_specs` first (idempotent), so a direct caller with a list spec
    gets the same gate; the malformed message keeps the substring `is not` and names both
    forms (`"From -> To" or "From -> A | B"`).
  - `scripts/arch/modules.toml:542-544` comment and `docs/dev/arch/README.md:768-773`,
    `:2006-2008`: document the list form, that it expands to one edge per target, and that
    every element must name a module.
- **Proof.** New cells in `test_archview.CheckerHelperTests`: plain spec unchanged; list
  splits in order; whitespace ignored; duplicates dropped; malformed forms pass through
  (`"A -> B |"`, `"A | B -> C"`, `"A -> B -> C"`, `None`, `5`); `parse_edge_spec("A -> B | C")`
  raises; `load_rules` on a temporary modules.toml with a list spec returns the expanded
  list. New cells in `test_semantic_layers.ForbiddenTests`: a list spec whose second target
  is crossed reds naming `Ghost -> GameActions` only; a list with one unknown target reds
  `names no module` naming that element; `"Ghost -> GameActions |"` reds `is not`. One
  committed-file cell: every raw spec in `modules.toml` expands to exactly
  `count("|") + 1` edges. The existing suite stays green with the two committed edges:
  `python3 -m unittest discover -s scripts/arch -q` (DLL-free) and, after a Debug build,
  with `PARSEK_ARCH_IL` set.
- **Registers.** README sections above; no pins move (two edges before and after).
  No CHANGELOG (tooling only). No todo.
- **Size.** S. **Depends on.** Nothing. **PC.** No.
- **Risk.** Low: an expansion bug would drop edges silently, which the per-element cells
  and the committed-file count cell pin.

### P12-02 The Controllers rule places `ParsekRace*` root files

- **Change.** `scripts/arch/modules.toml:322`: add `Race` to the alternation, giving
  `^Parsek(Flight|KSC|TrackingStation|Scenario|Harmony|Playback|Race)|^WatchMode`. No earlier
  rule matches a `ParsekRace` name today (checked with `assign_module` over the live rules).
- **Proof.** A cell in `test_archview.py` beside `test_phase_two_placements` (`:4121`):
  `assign_module("ParsekRaceMenu.cs", rules)`, `("ParsekRaceScenario.cs", rules)`,
  `("ParsekRaceBoot.cs", rules)` and `("ParsekRaceSetup.cs", rules)` are `Controllers`, and
  `ParsekRaceX.cs` too. `test_committed_placement_report_matches_a_fresh_render`
  (`:4129`) is unaffected (no such file exists).
- **Registers.** None beyond the file. **Size.** S. **Depends on.** Nothing; may share the
  P12-01 branch (both edit `modules.toml` and `test_archview.py`). **PC.** No.
- **Risk.** Low: a rule for files that do not exist yet changes no placement.

### P11-01 Census specs stop pinning the window count

- **Decision.** Replace the literal with the regex `windows=\\d+` (TOML basic string, so the
  regex reads `windows=\d+`). A placeholder that a loader expands was rejected: specs are
  read raw by at least five loaders and every one would need it. The exact claim of each
  echo is `open=N openWindows=<names>`, which stays literal. The count itself is a static
  table length; it is already pinned equal to `hlib.UIACTION_WINDOW_VALUES` by
  `GuiCensusSeamVerbTests`, which is where "derived" lives.
- **Change.**
  - The 25 specs: every `windows=11` inside a `uiaction describe` pattern becomes
    `windows=\\d+`. Mechanical: one regex substitution limited to that line shape; grep
    after it for any remaining `windows=[0-9]`.
  - `harness/lib/test_hlib.py` `test_the_census_specs_pinned_describe_echoes_match_their_own_steps`
    (`:21303`): parse the window token as the literal `\d+` and compare (scene, mode, open,
    openWindows) only; a digit literal in the token reds with "pins the window count; write
    `windows=\\d+`".
  - New cell beside it, over every spec in `harness/scenarios/`: any `required` or
    `forbidden` pattern containing `uiaction describe` carries `windows=\d+` and no digit
    window count. A second assertion: `len(hlib.UIACTION_WINDOW_VALUES)` equals the row
    count of the C# table (reuse the parse in `GuiCensusSeamVerbTests:19641-19670`), so the
    derivation is stated where the specs no longer state it.
  - `docs/dev/design-autotest-command-seam.md:2390`: the specs do not pin the count; it is the
    table's length, held equal to hlib by `GuiCensusSeamVerbTests`.
- **Proof.** `cd harness && python -m unittest discover -s lib -q` (then `provision`,
  `missions/lib`). The change only widens a regex: every log line that matched `windows=11`
  matches `windows=\d+`, so no lane can turn red from it, and no flight is needed. A third
  cell renders one old line from each changed pattern (the pattern with `\d+` replaced by
  `11`, the other groups filled from the spec's own simulated state for GUI-1 and GUI-2)
  and asserts the new pattern matches it.
- **Registers.** `docs/dev/autotest-status.md`: one dated line in its log section (the 25
  census lanes no longer pin the window count; no status changes). No CHANGELOG.
- **Size.** S. **Depends on.** Nothing. **PC.** No.
- **Risk.** Low. What is lost: a stale automation DLL with a different window table would
  no longer red on this token (section 8).

### P11-02 One `ParsekUI` constructor body

- **Change.** `Source/Parsek/ParsekUI.cs:127-169`: a private constructor
  `ParsekUI(UIMode mode, ParsekFlight flight, Func<ParsekUI, IParsekUIWindows> buildWindows)`
  holds today's body once (`this.flight = flight; this.mode = mode;` then the unchanged
  sequence). The two internal constructors keep their signatures and become chains:
  `: this(UIMode.Flight, flight, buildWindows) { }` and `: this(mode, null, buildWindows) { }`.
  Constructor chaining is needed because both fields are `readonly`; a helper method cannot
  assign them. `ParsekWindowSet.BuildUI` (`UI/ParsekWindowSet.cs:43-52`) is unchanged.
- **Proof.**
  - `ParsekUIWindowSetCharacterizationTests.Constructor_BuildsTheWindowsFirst_BeforeGoingLive`
    becomes a fact over the private constructor's body (same ordered needles, with
    `"this.mode = "` kept), plus a new fact that each internal constructor's text is
    `: this(` with an empty body.
  - `KerbalsWindowUITests.LiveCrewRefresh_...` (`:2127-2133`): the expected counts become 1
    (one constructor subscribes); update its comment.
  - `BothConstructors_BuildTheSameTenWindows_EachItsOwnInstance` and
    `TwoInstances_ShareNoWindow` stay as they are and must pass unchanged.
  - Optional mechanical proof: `scripts/refactor/il_compare.py` on the two constructors
    before and after shows the same call sequence.
  - `scripts/cloud-test.sh` (xUnit), and one GUI census lane per scene on the owner's PC
    (GUI-8-census-empty-states, committed fixture, Space Center; GUI-7-census-flight-recording,
    committed fixture, flight).
- **Registers.** None (no player change, no new UI). No CHANGELOG.
- **Size.** S. **Depends on.** Nothing. **PC.** Yes, two census lanes (operator tier).
- **Risk.** Low: the windows are built in the same order by the same callback.

### P7-01 The id check on the floor with a fixed character set

- **Decision on the set.** The fixed set is today's Windows set: NUL, chars 1 to 31, `"`,
  `<`, `>`, `|`, `:`, `*`, `?`, `\`, `/` (41 chars), plus the existing `..` rule. On Windows
  nothing changes. On Linux and macOS an id containing one of the 39 newly listed chars is
  refused; no producer makes such an id and no committed fixture holds one (section 3.3). A
  stricter allowlist (`[A-Za-z0-9_-]`) was rejected for this step: it would also refuse ids
  that pass on Windows today (a space, a dot), which is a behavior change the prerefactor
  must not make. The race codec may add its own stricter rule for ids it creates.
- **Change.**
  - New `Source/Parsek/SafeFileId.cs` (module `Core`, floor, headless): `internal static
    class SafeFileId` with `internal enum SafeFileIdVerdict { Ok, NullOrEmpty, PathSequence,
    InvalidChar }`, `internal static SafeFileIdVerdict Check(string id)` (pure, no logging,
    no `System.IO.Path` call) and `internal static bool IsInvalidChar(char c)` over a
    `private static readonly bool[]` or `char[]` table built in the type initializer (never
    mutated, so not in the static-lifetime population). `Check` calls `IsInvalidChar`, and
    `ValidateRecordingId` calls `Check`, so both are reached from production in this pull
    request (uncalled-method ratchet, A1).
  - `RecordingPaths.ValidateRecordingId` (`RecordingPaths.cs:284-314`) calls
    `SafeFileId.Check` and maps each verdict to today's exact log text and level. The three
    messages and the `Paths` tag do not change. `RecordingIdValidationLogContext` stays in
    `RecordingPaths.cs`.
  - `scripts/arch/modules.toml:478`: add `|^SafeFileId[.]cs$` to the Core list; fix the
    header comment at `:11-12` ("nine") and the one at `:471` to the new count.
  - `test_archview.py:4101` (8 to 9) and the kernel list at `:4104-4120`;
    `docs/dev/arch/README.md:1991` ("eight explicit file names").
  - Comments that say a space is refused (`Bug278SnapshotPersistenceTests.cs:57`,
    `Bug280PersistFinalizedRecordingTests.cs:56`, `:112`) are wrong today; correct the
    wording (those tests fail for the missing save context, not for the id).
- **Proof.**
  - New `Source/Parsek.Tests/SafeFileIdTests.cs`: each verdict; a theory over all 41 chars
    (each refused inside `rec<c>id`); `:` refused (this cell fails on Linux CI before the
    change, which is the proof that the OS no longer decides); GUID `N` and `D` forms and
    `rec_`, `rp_`, `mj_` ids accepted; space, `.` and non-ASCII letters accepted (unchanged
    from Windows today).
  - `RecordingPathsLoggingTests`: replace `GetInvalidFileNameCharForTesting` (`:72-84`) by a
    fixed `:` so the cell is OS-independent; the three exact log lines stay pinned.
  - `SyntheticRecordingTests.RecordingPaths_ValidateRecordingId_RejectsInvalidIds`
    (`:5807-5815`) and every other caller's tests pass unchanged.
  - In-game `StorageRuntimeTests` (`:38-45`) keeps reading the platform set; it is a subset
    of the fixed set, so it still passes. No in-game test is added (no batch tally moves).
  - `scripts/cloud-test.sh`; the arch suite DLL-free, and with `PARSEK_ARCH_IL` after a
    Debug build (Core gains one headless type with no leak; `headless_pins.toml` needs no
    re-pin, its `[measured]` block is report-only).
- **Registers.** `modules.toml`, `test_archview.py`, README as above. todo: add and close an
  entry "recording id check depended on the operating system" (it is analysis section 10's
  finding). CHANGELOG: none (no player-visible effect).
- **Size.** S. **Depends on.** Nothing. **PC.** No (any daily lane exercises the sidecar
  paths; not required).
- **Risk.** Low: a refused id loses its sidecars, so the cells pin that every producer's
  shape still passes.

### P7-02 A save-root accessor on `RecordingPaths`

- **Change.** `RecordingPaths.ResolveSaveRoot()` (internal static, `string` or null):
  `SaveRootOverrideForTesting` first (`Path.GetFullPath(override)`), else
  `TryGetSaveContext("ResolveSaveRoot", "resolve-save-root-missing-context", null, ...,
  allowNullRelativePath: true)` and `Path.GetFullPath(Path.Combine(root, "saves",
  saveFolder))` in a try / catch that logs `Error` on tag `Paths` like the siblings. Its
  production caller lands in the same pull request (uncalled-method ratchet, A1): the KSP
  branch of
  `ParsekFileCollector.ResolveSaveDirectory` (`ParsekFileCollector.cs:268-278`) returns
  `RecordingPaths.ResolveSaveRoot()`; the two override branches above it stay first and
  unchanged. The other hand-built `saves/<save>` sites are not touched here. The race does
  not use this accessor: `RecordingPaths` is a `Recording` type, which `RaceWorld` may name
  only in its loader files, so R1-08 computes the local race directory itself (OV-23,
  DC-096); the collector is the accessor's one user.
- **Proof.** xUnit: with the override set, `ResolveSaveRoot` returns its full path; without
  a save context it returns null and logs the Verbose missing-context line once (rate
  limited); `ParsekFileCollector` tests that set either override keep passing. Behavior:
  identical paths; the only difference is one Verbose rate-limited line when the collector
  runs without a save context, where it was silent.
- **Registers.** None (same module, no new static). No CHANGELOG.
- **Size.** S. **Depends on.** P7-01 (same file; can share the branch). **PC.** No.
- **Risk.** Low: one call site, same values.

### P1-01 `HomeSpaceCenter` in `Core`, read by the KSC circles

- **Change.**
  - New `Source/Parsek/HomeSpaceCenter.cs` (module `Core`): `internal sealed class
    HomeSpaceCenter`, immutable, with `internal const double StockPadLatitude = -0.0972`,
    `StockPadLongitude = -74.5575`, `StockRunwayThresholdLatitude = -0.0502`,
    `StockRunwayThresholdLongitude = -74.7300` (the values of
    `SpawnCollisionDetector.cs:216-225`), readonly fields `PadLatitude`, `PadLongitude`,
    `RunwayThresholdLatitude`, `RunwayThresholdLongitude`, `Version` (int), a
    `static readonly HomeSpaceCenter Stock` (version 0) and `internal static HomeSpaceCenter
    Current => Stock;`. No setter and no mutable static in this task: a setter with no
    production caller would raise the uncalled-method bound (`facts_pins.toml:74-75`), and
    R2-05 adds `Apply` / `RestoreStock` on this type (never a field another type writes) with
    its caller (`RaceSites`), a `process` lifetime tag, and `Version` in
    `LaunchSiteOnlyGhost`'s cache key, which this plan does not add (OV-53).
    Uncalled-method ratchet (A1): the position values and `Version` are readonly fields, not
    properties, so no getter lands uncalled; the `Current` getter is reached from
    `ClassifyKscExclusionZone`, and the private constructor from `Stock`'s initializer.
    Confirm in the `ilanalysis.py` report before pushing that the type initializer counts as
    reached; if it does not, `Current` builds `Stock` lazily.
  - `SpawnCollisionDetector.cs:216-225`: the four constants stay as aliases
    (`internal const double KscPadLatitude = HomeSpaceCenter.StockPadLatitude;` and so on) so
    the 99 test references keep compiling and keep meaning "the stock position". The readers
    in `IsWithinKscExclusionZone` (`:243-244`) and `ClassifyKscExclusionZone` (`:278-281`)
    read `HomeSpaceCenter.Current` once per call into locals. Log text unchanged.
  - `modules.toml:478` Core list adds `|^HomeSpaceCenter[.]cs$`; Core count and kernel list
    in `test_archview.py:4101-4120`; README `:1991`; the two comments in `modules.toml`.
- **Proof.** New `HomeSpaceCenterTests`: `Current` is `Stock`; the stock fields equal the
  `SpawnCollisionDetector` constants; `Version` is 0. Existing `Bug170Tests`,
  `KscPadRetirementTests`, `LaunchSiteFollowupTests`, `LaunchSiteOnlyGhostTests`,
  `SyntheticRecordingTests` pass unchanged. Arch suite with IL: Ghost to Core is downward;
  no layer, knot or headless pin moves (HomeSpaceCenter names nothing).
- **Registers.** `modules.toml`, `test_archview.py`, README. No CHANGELOG, no todo.
- **Size.** S. **Depends on.** Nothing; rebases on P7-01 if that landed first (the same Core
  line and the same pinned count). **PC.** No.
- **Risk.** Low: same doubles, read through one more hop.

### P1-02 One launch-area shape test, called by the 50 m rule

- **Change.**
  - New `Source/Parsek/LaunchArea.cs` (module `Core`): `internal enum LaunchAreaKind
    { Circle, Strip }`; `internal readonly struct LaunchArea` (kind; centre or strip start
    latitude / longitude; strip end latitude / longitude; strip half-width in metres;
    margin in metres, which is the radius for a circle) with one internal all-fields
    constructor and the factory `Circle(lat, lon, radius)`, which calls it; and `internal
    static class LaunchAreaGeometry` with
    `double WrappedDeltaLongitudeDegrees(double lon1, double lon2)` (into (-180, 180]),
    `double SurfaceDistanceMeters(lat1, lon1, lat2, lon2, bodyRadius)` (the flat-earth
    formula of `SpawnCollisionDetector.cs:385-394` with the wrapped delta, exactly as the
    launch pad fixes left the launch-site test on main, in the same evaluation order, so
    every distance is bit-identical), and `bool Contains(LaunchArea area, double lat,
    double lon, double bodyRadius)`: a circle is `distance < margin` (strict, as today); a strip
    projects the point into the strip's local frame (east-north metres about the strip's
    start, longitude wrapped), takes the along-track and cross-track excess outside the
    rectangle and tests the excess length `< margin`. NaN inputs or a radius `<= 0` give
    false.
  - `SpawnCollisionDetector.ClassifyKscExclusionZone` and `IsWithinKscExclusionZone` build
    two circles from `HomeSpaceCenter.Current` and the radius argument and use
    `LaunchAreaGeometry` (the Verbose line of `IsWithinKscExclusionZone` keeps its fields,
    now from the wrapped distance). `LaunchSiteExclusionZones.Classify` (`:194-200`) uses
    `LaunchAreaGeometry.Contains(LaunchArea.Circle(c.Latitude, c.Longitude, radius), ...)`.
  - `SpawnCollisionDetector.SurfaceDistance` and its eight other callers are NOT changed.
  - Uncalled-method ratchet (second pass, A1): every method this task adds is reached from
    production in this pull request (`Circle` and the constructor from `Classify` and the
    detector, `WrappedDeltaLongitudeDegrees` and `SurfaceDistanceMeters` from `Contains`).
    The strip branch of `Contains` is code inside a reached method. The factory
    `LaunchArea.Strip(lat0, lon0, lat1, lon1, halfWidth, margin)` has no production caller
    here, so it lands with its first one: R2-01 adds it to `LaunchArea.cs` when it builds
    `SiteRow.LaunchAreas` (the runway strip). This task's strip tests build strips through
    the internal constructor.
- **Behavior.** None. The longitude wrap itself is already on main from the launch pad fixes
  work (owner answer to question 10.1, Q-04, 2026-11-14), so a flight parked on the pad or
  runway with a wound longitude (for example -434.5 instead of -74.5) is already retired.
  This task only extracts the shape test that the 50 m rule calls:
  `WrappedDeltaLongitudeDegrees` and `SurfaceDistanceMeters` take over the wrap and the
  distance as they read on main after that fix, with the same arithmetic in the same order,
  so every distance is bit-identical to main's.
- **Proof.** New `LaunchAreaTests`: wrapped delta (179 vs -179 is 2; -434.5 vs -74.5 is 0;
  exactly 180 stays 180); circle equals main's launch-area test on a grid of points, wound
  longitudes included (bit-equal distances); the 59.7 m rover of LF-1 / LF-2 is outside the
  runway circle and the EX-1 pad position is inside; the strip: inside along the centre
  line, outside past the end plus margin, the corner case, a strip across the date line. The
  wound-longitude retirement cell in `KscPadRetirementTests` (a recording ending at the pad
  with a wound longitude is retired) pins existing behavior: it is the launch pad fixes'
  cell if that work added it there, and a new pin otherwise. Existing retirement and
  launch-site-only cells unchanged. Lanes on the owner's PC: EX-1, LF-1, LF-2, MC-4, kept as
  the no-change proof of the extraction (same retirements, same pinned lines).
- **Registers.** `modules.toml` Core list (`|^LaunchArea[.]cs$`), `test_archview.py` count
  and list, README. No CHANGELOG and no todo entry (no behavior change; the wound-longitude
  entry belongs to the launch pad fixes work).
- **Size.** S to M. **Depends on.** P1-01; starts after the launch pad fixes are on main (the
  same `SpawnCollisionDetector.cs` and `LaunchSiteExclusionZones.cs`). **PC.** Yes (four
  lanes).
- **Risk.** Low: a move of arithmetic already on main, proven bit-equal by the grid cell.

### P1-03 The launch-site circle cache follows the home center and the main menu

- **Change.** `Source/Parsek/LaunchSiteExclusionZones.cs`:
  - A new `private static int cachedRuntimeCirclesHomeVersion`. `GetAltSiteCircles`
    (`:216-252`) drops `cachedRuntimeCircles` when `HomeSpaceCenter.Current.Version` differs
    from it, logging Verbose `launch-site circle cache dropped reason=home-version
    old=<v> new=<v>`; it records the version whenever it caches.
  - `internal static void InvalidateCachedCircles(string reason)` clears both statics and
    logs Verbose `launch-site circle cache dropped reason=<reason> had=<n>` (no line when
    already empty).
  - `ParsekScenario.OnMainMenuTransition` (`ParsekScenario.SceneTransitions.cs:248-283`)
    calls `LaunchSiteExclusionZones.InvalidateCachedCircles("main-menu")` inside the
    `MAINMENU` branch, beside the other RAM-cache resets. This is its production caller.
  - A test seam `RuntimeAltSiteReaderForTesting` (`Func<List<LaunchSiteCircle>>`, consulted
    before `ReadRuntimeAltSiteCirclesCore`) so the cache rules can be unit-tested; reset in
    `ResetForTesting`.
  - `scripts/arch/lifetimes.toml`: a `[[type]]` entry for `Parsek.LaunchSiteExclusionZones`
    tagging `cachedRuntimeCircles` and `cachedRuntimeCirclesHomeVersion` `process` ("a cache
    of stock site positions keyed on the home center's version, dropped at the main menu").
    Re-pin `scripts/arch/lifetime_pins.toml` (`python3 scripts/arch/lifetimes.py
    Source/Parsek/bin/Debug/Parsek.dll --write-pins` after a Debug build), which removes
    `:143` from `[untagged]`.
- **Behavior.** In single player the version never changes, so only the main-menu drop is
  new: the next game's first read re-reads the stock sites (one more `Launch-site
  retirement circles resolved` Info line per game loaded after a main-menu return). Same
  circles, same decisions.
- **Proof.** xUnit with the reader seam: a cached list survives repeated reads; a version
  change drops it (needs a test-only way to present another version: add
  `internal static Func<HomeSpaceCenter> CurrentOverrideForTesting` to `HomeSpaceCenter` in
  this task, tagged `test-hook` by the `*ForTesting` pattern, and have `Current` return it
  when set); `InvalidateCachedCircles` drops it and logs once; the log lines are pinned.
  `LoadReconcileWiringGateTests` (`:247-250`) and `InSessionHandoffWiringGateTests` read
  `OnMainMenuTransition` and must stay green. Lanes on the owner's PC: MC-4 (Desert pad,
  Making History) and SWS-1 (two saves through the main menu), plus EX-1.
- **Registers.** `lifetimes.toml`, `lifetime_pins.toml`. If the `HomeSpaceCenter` override
  makes `crossTypeStaticWrites` or `testHookWrittenByProduction` count a write, that is a
  defect in the plan (only tests write it): re-check with `ilanalysis.py` and `lifetimes.py`
  reports before pushing. No CHANGELOG (no player effect). todo: if the analysis finding
  "cache never reset" was filed, mark it done with this task.
- **Size.** S. **Depends on.** P1-01 (`Version`); spike S4a, which carries the VERIFY of
  section 2 (OV-52), for its comment and test wording; starts after the launch pad fixes are
  on main (the same `LaunchSiteExclusionZones.cs`; their rule of not caching a partial first
  read stays, and the version key and the main-menu drop sit beside it). P1-03 does not
  filter the KSC's own pad and runway out of the cache; if S4a finds them there, R2-05
  filters or rebuilds them.
  **PC.** Yes (three lanes).
- **Risk.** Low: same data re-read; the main-menu hook is additive.

### P1-04 Do not keep a partial first read for the process

Dropped: landed outside this plan by the launch pad fixes work, with its own unit test,
CHANGELOG line and todo entry (owner answer to question 10.1, Q-04, 2026-11-14).

### P10-01 The create-and-start core in production

- **Change.**
  - New `Source/Parsek/NewSaveStarter.cs`, module `LogIO` (floor), by adding
    `|^NewSaveStarter[.]cs$` to `modules.toml:326`. Its callers are the `NewGame` applier
    (`TestCommands`, tooling), R1-10 / R1-11's `ParsekRaceSetup` (Controllers, entry; the
    setup composes there, not in `RaceWorld`, DC-102) and R1-16's `LoadAndStart`, which
    joins this file. The floor is downward from every one of them, and it leaves
    `ResolveSaveDirectory` open to `RaceWorld` (features) without a backward pair (OV-23's
    alternative to R1-08's own computation). `internal static class NewSaveStarter`, tag
    `NewSave`, three steps so the verb keeps its log order and a caller can run its own
    step between create and start:
    - `DeleteResult DeleteSaveFolderLikeStockOverwrite(string saveDir, Func<string, bool>
      preserveTopLevelEntry)`: the bodies of `DeleteSaveLikeStockOverwrite` and
      `DeleteTreeLikeStock` (`NewGame.cs:308-348`) moved verbatim; returns files, dirs,
      preserved. Pure file I/O, no KSP type, so xUnit can drive it on a temp folder.
      Exceptions propagate (the verb maps them to `newgame-delete-failed` as today).
    - `Game CreateGame(string saveFolder, Game.Modes mode, GameParameters parameters, string
      flagUrl)`: `HighLogic.CurrentGame = GamePersistence.CreateNewGame(saveFolder, mode,
      parameters, flagUrl, GameScenes.SPACECENTER, EditorFacility.None)`; when non-null,
      `GameEvents.onGameNewStart.Fire()`; returns the game or null. Takes `GameParameters`,
      not a preset, so the race can pass the manifest's difficulty.
    - `void StartGame(Game game)`: `game.Start()`.
    - The before-start step: the three steps are separate calls, so a caller runs work
      between `CreateGame` and `StartGame` on the returned game. R1-10 (and R1-11, Join and
      Start over) adds the `ParsekRaceScenario` proto to `game.scenarios`, writes its local
      files and saves `persistent.sfs` there, because `SpaceCenterMain` re-reads that file
      (r1-mode-and-setup.md section 9, item 3). `onGameNewStart` has fired by then, as in
      stock's order. The verb runs no such step. No callback parameter is added.
    - `string ResolveSaveDirectory(string saveFolder)`: `saves/<saveFolder>` under
      `KSPUtil.ApplicationRootPath`. Every KSP read sits in a `[MethodImpl(NoInlining)]`
      core so a test of the delete step never JITs a game type.
    - Each step logs one Info line on `NewSave` (deleted counts; created game title, mode
      and folder; start requested). These are new lines next to the verb's unchanged ones.
  - `TestCommands/ParsekTestCommandAddon.NewGame.cs:253-348`: `CreateAndStartNewGame` takes
    its folder from `NewSaveStarter.ResolveSaveDirectory(p.Save)` (replacing its own
    `Path.Combine` at `:255-256`) and calls the three steps in the same order with the same
    log lines between them (`newgame create ...` after the delete, `newgame created
    title=...` after create, then start); the two delete helpers are removed from the
    applier. So every `NewSaveStarter` member is reached from the applier in this pull
    request (TestCommands tooling code is live for the uncalled-method ratchet, A1); R1-16's
    `LoadAndStart` lands in R1-16 with the verb that calls it. `GetDefaultParameters`, the
    flag lookup, `ToStockMode` / `ToStockPreset` and every pure decision stay in
    `TestCommands`.
  - Source gate (new xUnit `NewSaveStarterWiringTests`): over comment-stripped
    `Source/Parsek/**/*.cs`, `GamePersistence.CreateNewGame(` and
    `GameEvents.onGameNewStart.Fire(` appear only in `NewSaveStarter.cs`, once each, in that
    order before `.Start()`.
  - `harness/lib/test_hlib.py`
    `NewGameSourceSyncTests.test_the_applier_runs_stocks_own_new_game_path`
    (`:18812-18826`): read `SaveGame` / `LoadScene(GameScenes.MAINMENU)` /
    `GetDefaultParameters` from the applier, and `GamePersistence.CreateNewGame(` /
    `GameEvents.onGameNewStart.Fire();` / `game.Start();` with their order from
    `Source/Parsek/NewSaveStarter.cs`; and require the applier to call the three steps in
    order. Update the docstring list of files it reads.
  - `.claude/CLAUDE.md`: the "Thirty-three hlib test cells read OUTSIDE `harness/`" list
    gains `NewGameSourceSyncTests` reading `NewSaveStarter.cs`; then
    `cp .claude/CLAUDE.md AGENTS.md`.
  - `scripts/arch/log_tags.json`: refresh with `python3 scripts/arch/logtags.py
    Source/Parsek/bin/Debug/Parsek.dll --write` for the `NewSave` tag (report-only gate).
- **Proof.** xUnit: the delete step on a temp tree (files, nested dirs, a `Ships` folder
  kept, read-only attributes cleared, a missing folder is a no-op, counts returned); the
  wiring gate; `TestCommandNewGameTests` unchanged. hlib suite green. Lanes on the owner's
  PC: CS-0-new-career-game, CS-0-new-science-game, SWS-1-save-switch-through-main-menu
  (their pinned `newgame create`, `newgame created`, `newgame complete` lines are written by
  the verb exactly as before).
- **Registers.** `modules.toml` LogIO prefix; CLAUDE.md and AGENTS.md; `log_tags.json`;
  arch suite (LogIO stays a pure sink, `test_archview.test_logio_is_a_pure_sink`:
  `NewSaveStarter` names only `ParsekLog` inside its module). The uncalled-method ratchet:
  all four methods are reached from the `NewGame` applier (a `KSPAddon` root); confirm in
  the `ilanalysis.py` report before pushing. No CHANGELOG (no player effect). No change to
  `LoadReconcilePolicy.KnownLoadInitiators` (no `LoadGame` call moves).
- **Size.** S to M. **Depends on.** Nothing. **PC.** Yes (three lanes).
- **Risk.** Low: the same stock calls in the same order; the new log lines are additive.

## 5. Parallel tracks

Files each task touches:

| Task | Files |
|------|-------|
| P12-01 | `scripts/arch/archview.py`, `layers.py`, `modules.toml` (comment at :542), `test_archview.py` (CheckerHelperTests), `test_semantic_layers.py`, `docs/dev/arch/README.md` (:768, :2006) |
| P12-02 | `scripts/arch/modules.toml` (:322), `test_archview.py` (near :4121) |
| P11-01 | 25 `harness/scenarios/GUI-*.toml`, `harness/lib/test_hlib.py` (:21303 and new cells), `docs/dev/design-autotest-command-seam.md`, `docs/dev/autotest-status.md` |
| P11-02 | `Source/Parsek/ParsekUI.cs`, `ParsekUIWindowSetCharacterizationTests.cs`, `KerbalsWindowUITests.cs` |
| P7-01 | new `SafeFileId.cs`, `RecordingPaths.cs`, `modules.toml` (Core :478, comments :11, :471), `test_archview.py` (:4101-4120), README (:1991), `RecordingPathsLoggingTests.cs`, new `SafeFileIdTests.cs`, two Bug27x test comments, todo |
| P7-02 | `RecordingPaths.cs`, `ParsekFileCollector.cs`, an xUnit file |
| P1-01 | new `HomeSpaceCenter.cs`, `SpawnCollisionDetector.cs`, `modules.toml` (Core), `test_archview.py` (Core), README (:1991), new `HomeSpaceCenterTests.cs` |
| P1-02 | new `LaunchArea.cs`, `SpawnCollisionDetector.cs`, `LaunchSiteExclusionZones.cs`, `modules.toml` (Core), `test_archview.py` (Core), README, new `LaunchAreaTests.cs`, `KscPadRetirementTests.cs` |
| P1-03 | `LaunchSiteExclusionZones.cs`, `HomeSpaceCenter.cs`, `ParsekScenario.SceneTransitions.cs`, `lifetimes.toml`, `lifetime_pins.toml`, `LaunchSiteFollowupTests.cs` or a new test file |
| P1-04 | dropped: landed outside this plan by the launch pad fixes work |
| P10-01 | new `NewSaveStarter.cs`, `ParsekTestCommandAddon.NewGame.cs`, `modules.toml` (LogIO :326), `harness/lib/test_hlib.py` (:18812), new `NewSaveStarterTests.cs` and `NewSaveStarterWiringTests.cs`, `.claude/CLAUDE.md`, `AGENTS.md`, `scripts/arch/log_tags.json`, `scripts/parsek-file-deletes-allowlist.txt` |

Five tracks run at the same time, one agent each: **P12** (P12-01 then P12-02, one branch),
**P11** (P11-01 and P11-02 are independent of each other and may run as two agents),
**P7** (P7-01 then P7-02), **P1** (P1-01, P1-02, P1-03 in order; P1-02 and P1-03 start after
the launch pad fixes are on main), **P10**.

The task index (C.2) groups P12, P7-01, P7-02, P1-01 and P1-02 in its ARCH track, P10-01 in
SETUP and P11 in GUI; the merge order below holds either way.

Hot files that force an order (merge order; the neighbours' places follow task-index C.1 and
the wave-0 notes of E.5):

- `scripts/arch/modules.toml`: P12-01, P12-02, P7-01, P1-01, P1-02, P10-01, then R1-02 (the
  same LogIO line :326 as P10-01), then P2a-05 and the later rows of task-index C.1. R1-01
  adds its own module rules and may land at any time after P12-01, rebasing on the Core
  lines. Line 478 (the Core list) and `test_archview.py:4101-4120` (the Core count and
  list): P7-01, P1-01 and P1-02 each add a file there. Land P7-01 first; P1-01 and P1-02
  rebase and pin 10 and then 11. Different lines of `modules.toml` (P12 :322 and :542, P10
  :326) merge cleanly inside this plan.
- `scripts/arch/test_archview.py`: P12-01, P12-02, P7-01, P1-01, P1-02, then P2a-05
  and R1-06.
- `docs/dev/arch/README.md:1991`: P7-01 and P1-01 / P1-02 (the "eight explicit file names"
  count). Same order.
- `harness/lib/test_hlib.py` and `hlib.py`: P10-01 (near :18812), then P11-01 (near :21303),
  then T-03, T-04, T-07 (E.5.3). P10-01 and P11-01 are 2,500 lines apart; a textual merge is
  clean.
- `docs/dev/design-autotest-command-seam.md`: P11-01 before T-07 and the race verb tasks
  (C.1).
- `.claude/CLAUDE.md` and `AGENTS.md`: P10-01, then R1-07 (E.5.2) and the later rows of C.1;
  one mirror copy per PR, so re-check `AGENTS.md` equality after merging `origin/main`.
- `ParsekUI.cs`: P11-02, then R4-08 and R4-09.
- `scripts/arch/lifetimes.toml` and `lifetime_pins.toml`: P1-03 first of C.1's row; every
  later task re-pins after merging.
- `SpawnCollisionDetector.cs` and `LaunchSiteExclusionZones.cs`: the launch pad fixes work
  (outside this plan, already on main), then P1 only, in order (R2-05 edits
  `HomeSpaceCenter.cs` after P1-01 and P1-03).
- `RecordingPaths.cs`: P7 only, in order.

## 6. Tests and proof

| Task | Unit tests (kind, name) | Lanes (owner's PC) | Proves |
|------|-------------------------|--------------------|--------|
| P12-01 | Python `test_archview.CheckerHelperTests` (expand, strict parse), `test_semantic_layers.ForbiddenTests` (list spec crossed, unknown element, malformed), committed-file expansion count | none | the list form gates per element in both parsers and nothing else moved |
| P12-02 | Python `test_archview` placement cell | none | a future `ParsekRace*` root file lands in Controllers |
| P11-01 | Python `test_hlib` (simulation cell reworked, no-digit cell, derived-count cell, old-line-still-matches cell) | none needed (regex widening) | specs no longer pin the count; the count is derived offline |
| P11-02 | xUnit `ParsekUIWindowSetCharacterizationTests` (body order, chains), `KerbalsWindowUITests` count, unchanged runtime cells | GUI-8-census-empty-states, GUI-7-census-flight-recording | both scenes build the same windows in the same order |
| P7-01 | xUnit `SafeFileIdTests` (verdicts, 41-char theory, `:` on every OS, producer shapes), `RecordingPathsLoggingTests` (fixed char), existing callers | none required | the check is OS-independent and refuses nothing a producer makes |
| P7-02 | xUnit accessor cells, `ParsekFileCollector` tests | none | same paths, one production caller |
| P1-01 | xUnit `HomeSpaceCenterTests`, existing retirement tests | none required | the KSC circles read one source with the same numbers |
| P1-02 | xUnit `LaunchAreaTests` (wrap, circle equality, strip, date line), wound-longitude retirement cell (a pin of behavior already on main) | EX-1, LF-1, LF-2, MC-4 (no-change proof) | same retirements on every lane, wound longitudes included |
| P1-03 | xUnit cache cells with the reader seam and the version override | MC-4, SWS-1, EX-1 | cache follows the version and the main menu, same decisions |
| P1-04 | dropped (launch pad fixes work) | - | - |
| P10-01 | xUnit delete-step cells, `NewSaveStarterWiringTests`; Python `NewGameSourceSyncTests` | CS-0-new-career-game, CS-0-new-science-game, SWS-1 | the verb creates and starts a save exactly as before through the production core |

Every task also runs, before its PR: `scripts/cloud-test.sh` (or `dotnet test` on Windows),
the three harness suites, `python3 -m unittest discover -s scripts/arch -q`, and, for a C#
change, the IL half with `PARSEK_ARCH_IL=Source/Parsek/bin/Debug/Parsek.dll` after a Debug
build. The `Spawn RETIRED` / `exclusion zone` lanes beyond those named (about 30 specs, for
example SS-1, EX-2, the MINE and EVA spawn lanes) are covered by the next ordinary tier run;
P1-02 should not merge before at least the four named lanes are green on its DLL
(provisioned with `harness/provision/provision.py --profile stock-minimal`, the deployed DLL
checked for a P1-02 marker such as the `LaunchAreaGeometry` identifier in UTF-8).

No in-game test is added in this slice, so no `[InGameTest]` batch tally and no lane catalog
regeneration is triggered. If an implementer adds one, `CommittedBatchTallySourceSyncTests`
and `python harness/tools/lane_catalog.py` apply in the same commit.

## 7. Gates and registrations

- **Architecture map** (`scripts/arch/modules.toml`): P12-02 (Controllers alternation);
  P7-01, P1-01, P1-02 (three Core files: `SafeFileId.cs`, `HomeSpaceCenter.cs`,
  `LaunchArea.cs`); P10-01 (`NewSaveStarter.cs` in LogIO). Every new root file has its rule in
  the same commit (`test_every_production_file_is_placed`).
- **Layers and layer pins**: no new backward pair. Ghost, Controllers and Recording to Core,
  TestCommands to LogIO are downward or tooling. `layer_pins.toml` does not move.
- **Forbidden edges**: P12-01 changes the syntax only; the two committed edges stay.
- **Knot canaries**: the new types reference nothing (Core files) or only `ParsekLog`
  (`NewSaveStarter`), so they cannot join a cycle; the arch suite confirms.
- **Headless pins**: Core gains three headless types with no game reference, so no new leak;
  `headless_pins.toml` `[measured]` is report-only. LogIO is not headless.
- **Code facts** (`facts_pins.toml`): no new uncalled production method (each new method has a
  production caller in its own pull request; `LaunchArea.Strip` lands with R2-01); no
  cross-type static write; no method near complexity 45.
- **Static lifetimes**: P1-03 tags `cachedRuntimeCircles` and its version key `process` and
  re-pins `lifetime_pins.toml`; `HomeSpaceCenter.CurrentOverrideForTesting` and
  `LaunchSiteExclusionZones.RuntimeAltSiteReaderForTesting` take `test-hook` from the
  `*ForTesting` pattern. `SaveScopedSessionResetTests` is untouched.
- **Grep-audit gates**: one changes. P10-01 moves the four save-folder delete / move calls
  out of `ParsekTestCommandAddon.NewGame.cs`, so `scripts/parsek-file-deletes-allowlist.txt`
  (pinned per file by `GrepAuditTests`) replaces the entry
  `Source/Parsek/TestCommands/ParsekTestCommandAddon.NewGame.cs 4` with
  `Source/Parsek/NewSaveStarter.cs 4` and its one-line reason (the save-folder overwrite of
  a new game, also used by R1-10). P10-01 also adds a C# source gate
  (`NewSaveStarterWiringTests`) of its own.
- **Save format**: nothing persisted changes. No `SaveFormat.cs` edit, no golden folder.
- **Load policy**: no `GamePersistence.LoadGame(` call moves; `KnownLoadInitiators` unchanged.
- **GUI gates**: P11-02 changes no window; `design-gui-inventory.md` unchanged.
  `TooltipEchoBudgetTests` and `ParsekUiScaleTests` unaffected.
- **Harness source-sync**: P10-01 updates `NewGameSourceSyncTests`; P11-01 updates the census
  simulation cell. Both are named in CLAUDE.md's list of hlib cells reading outside
  `harness/` (P10-01 edits that list).
- **Docs**: `docs/dev/arch/README.md` (P12-01, P7-01, P1-01, P1-02);
  `design-autotest-command-seam.md` and `autotest-status.md` (P11-01); `.claude/CLAUDE.md` and
  `AGENTS.md` (P10-01); no CHANGELOG entry in this plan (the wound longitude and the partial
  cache read have theirs in the launch pad fixes work, which lands them on main first);
  `docs/dev/todo-and-known-bugs.md` entries for the id check (P7-01) and, if filed, the
  "cache never reset" finding (P1-03), each opened and closed in its task.

## 8. Risks

- **The extraction moves a retirement** (P1-02). Real recordings carry wound longitudes
  (section 3.4), and the wrap that handles them is already on main (launch pad fixes work);
  a slip in the moved arithmetic would change what retires. Noticed by: the bit-equal grid
  cell (wound longitudes included), the wound-longitude retirement pin, and any lane whose
  pinned `Spawn RETIRED` count or forbidden pattern changes on the four named lanes and the
  next tier run. Mitigation: the move keeps the arithmetic and its order; the other
  `SurfaceDistance` callers are not touched.
- **The list-form expansion drops an edge silently** (P12-01). Noticed by the per-element
  cells and the committed-file count cell.
- **A stale automation DLL is no longer caught by `windows=11`** (P11-01). The token was an
  accidental DLL check. Provisioning's VERIFY and the marker grep in CLAUDE.md are the real
  checks; an implementer of a window change still sees the census lanes' `openWindows=` lists
  fail if the table differs.
- **An id on a Linux or macOS install is refused after P7-01.** Only an id with one of the 39
  chars newly refused there; none is produced. Noticed by the in-game
  `StorageRuntimeTests` cell, which asserts every live id still passes, on the owner's save.
- **The version override of P1-03 counts as a production write** in `lifetimes.py` or
  `ilanalysis.py`. Noticed before the push by running both reports; fix by keeping the write
  in tests only.
- **`NewSaveStarter` JITs a game type in a unit test** (mono, P10-01). Noticed by
  `scripts/cloud-test.sh`; prevented by the `NoInlining` cores.
- **The main-menu re-read of P1-03 runs before `PSystemSetup` is ready.** It is lazy: the
  next `GetAltSiteCircles` call reads, and a read without the setup returns the fallback
  uncached, as today.

## 9. Corrections and gaps

1. **P11 "derive" is realized as "do not pin".** The design and the analysis say the specs
   derive the count. Spec files are read raw by at least five loaders, so a derived literal
   would need a substitution step in each. The plan writes `windows=\d+` in the specs and
   keeps the derivation in the hlib cells (the count equals the C# table's length, held by
   `GuiCensusSeamVerbTests`).
2. **The longitude wrap is not behavior-neutral** (design 15.11 lists P1 as "no behavior
   change"). Recorded longitudes outside [-180, 180] exist in committed fixtures, including
   points over the KSC stored as about -434.5. Settled (owner answer to 10.1, Q-04,
   2026-11-14): the wrap and the partial-read cache fix land on main as single-player fixes
   in the separate launch pad fixes work, each with a CHANGELOG line; P1 itself stays
   behavior-neutral.
3. **`HomeSpaceCenter` lands without the runway end, the strip half-width and a setter.** The
   design lists all three. The code has no stock numbers for the runway's east end or width
   (only the west threshold, `SpawnCollisionDetector.cs:221-225`), and a setter with no
   production caller raises the uncalled-method bound. R2 adds them, with the numbers from
   spike S4 and the setter's caller. Settled (OV-53): R2-05 adds `Apply` / `RestoreStock`
   with its lifetime tag and the `LaunchSiteOnlyGhost` cache key, explicitly.
4. **The fixed character set is not named by the design.** The plan uses today's Windows set
   (no change on Windows). A race-specific stricter id grammar, if wanted, is RaceCore's.
5. **The alt-site cache likely holds the KSC's own pad and runway.** Design 7.3 calls it the
   cache of "the other stock launch sites". `IsStockLaunchSiteName`
   (`LaunchSiteExclusionZones.cs:79-83`) admits `LaunchPad` and `Runway`, so the runtime read
   (`:254-318`) probably adds circles at the stock KSC spawn points, which in a race save at
   another site would still retire flights parked at the stock KSC. VERIFY from any
   collected KSP.log: `Launch-site retirement circles resolved: runtime=N` with N above the
   four Making History spawn points means they are included. R2 must then filter them or
   rebuild them from the moved site; P1-03's version key makes the cache drop when the home
   moves either way. Settled (OV-52, DC-036, DC-037): the VERIFY is folded into spike S4a,
   P1-03 keeps its version key, and if the pad and runway are cached R2-05 filters or
   rebuilds them.
6. **P10's module is not named by the design.** It must be on the floor (LogIO) so that
   `RaceWorld` (features) can call it; `Controllers` would create a features-to-entry
   backward pair. Settled (DC-102): the race setup composes in Controllers
   (`ParsekRaceSetup`, R1-10), so that reason falls away; `LogIO` stays because it is
   downward from every caller (the verb, the setup, R1-16's load route) and leaves
   `ResolveSaveDirectory` open to `RaceWorld` (OV-23).
7. **`IsWithinKscExclusionZone` has no production caller** (only tests and in-game tests).
   The analysis counts it among the readers; the live reader is `ClassifyKscExclusionZone`
   alone.
8. **Two test comments are wrong today**: `Bug278SnapshotPersistenceTests.cs:57` and
   `Bug280PersistFinalizedRecordingTests.cs:112` say an id with spaces is refused by
   `ValidateRecordingId`; a space is a valid file-name character on every OS. P7-01 corrects
   the comments.
9. **Counts drift**: 99 test references to the constants (the analysis says 64); 28 calls of
   the id check in production files plus about 25 in tests (the analysis says 32 callers);
   `modules.toml:11-12` says Core has nine files, it has eight. None changes a task.
10. **A list spec is silently inert in the report today.** `parse_edge_spec("A -> B | C")`
    returns `("A", "B | C")`, so `archview.py --check` and the atlas skip it without a
    warning; only `layers.forbidden_problems` reds. P12-01 makes `parse_edge_spec` refuse
    `|`.

## 10. Questions for the owner

1. **Two small single-player fixes ride with the launch-pad rule. Land them?** Today a
   flight that ends parked on the launch pad or runway is retired (it never becomes a real
   vessel). (a) If its stored longitude has wound past the date line (the game sometimes
   stores the KSC as -434.5 instead of -74.5), the rule misses it and the vessel spawns on the
   pad. (b) If the Making History sites were read before they were fully built, the game keeps
   approximate positions for them until KSP restarts. Options: **land both** (each with a
   CHANGELOG line; the race needs the same shape test anyway); land (a) only; land neither
   (P1 stays strictly neutral, the shape test waits for the race). Recommendation: land both.
   **Answered 2026-11-14 (Q-04): fix both, in a separate work item outside this plan.** The
   launch pad fixes work lands (a) and (b) on main, minimally and in place, each with a unit
   test, a CHANGELOG line and a todo entry. P1-04 is dropped; P1-02 and P1-03 start after
   that work is on main, and P1-02 is a behavior-neutral extraction.
