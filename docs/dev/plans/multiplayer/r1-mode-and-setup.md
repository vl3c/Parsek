# Race mode, mark and setup - implementation plan

Status: PLAN (2026-11-14), not started. Design sections covered: 6.1, 6.2, 7.1, 7.2, 7.3
(slot assignment only), 7.4 (look assignment only), 15.1 to 15.4, 15.7, 15.8, 15.12, 15.14,
addition A7. Phase: R1 ("a race save that plays as single player").

Design: `docs/dev/design-competitive-multiplayer.md` (cited as "design").
Inventory: `docs/dev/research/multiplayer-base-refactoring-2026-11-14.md` (cited as
"inventory").

## 1. Goal and scope

R1 makes a race save exist. At the end of R1 a player can open Multiplayer from the main
menu, create a race in a shared folder, join it (also late, also "start over in my seat"),
continue a race save, and change the folder, name and look in Settings. The race save then
plays exactly like a single-player save: no sites, no rivals, no board. A single-player save
never constructs a race object.

Built here:

- The three modules `RaceCore`, `RaceExport`, `RaceWorld`: folder rules, layers, `RaceCore`
  as a headless module, `RaceWorld` as runtime-coupled, and the 71 forbidden edges of design
  15.4, all from the first commit (design 15.2, 15.4).
- The 15.4 grep-audit gates that R1's own files can trip, on one shared race source scanner
  (T-08 merged into R1-06, OV-10): no store in the rules, one no-writer gate (OV-11), the
  one recorder ban, `Recording` types only in the loader files of `RaceWorld` (an arch cell
  reading `scripts/race-loader-files-allowlist.txt`, OV-12), and the race log tag list
  (OV-57). The rival-store gate lands with the store's types (R3W-27, on R1-06's scanner).
- The race mark: `RaceLink` (design 6.1) in its own scenario node, held by the second
  scenario module `ParsekRaceScenario` (design 6.2), with child Save / Load hooks for the
  children later tasks add (OV-28).
- `RaceGate.IsRaceSave`, decided from the game's scenario node list before any scene is
  built (design 7.1, 15.8), with the one process-lifetime host `ParsekRaceBoot` whose one
  handler `OnSceneRequest` evaluates it at every scene request; R2-01 adds its
  `RaceSites.Reconcile` call there (OV-51, A4).
- The `LoadReconcilePolicy` row for the mark, the static lifetimes of every new static, and
  the `SaveFormat` registration of the mark and of the exchange files with one new frozen
  golden folder (design 15.7, 15.14).
- `race.cfg`, `player.cfg` and `local.cfg` codecs, the fixed timestamp format (design 6.5),
  the local race directory `saves/<save>/Parsek/Race/` (`IRaceFolder`, `RaceFolder`,
  `RaceTimestamp`, `RaceIds`, which R3E-01 extends, OV-22, OV-24), and a minimal
  `RaceSession` and `RaceRuntime` (one static, OV-26) built per scene from the mark and
  `local.cfg` (design 15.7).
- Slot and look assignment (design 7.3, 7.4): first free slot in 0, 1, 2, 3; first free
  look; the simultaneous-join clash rules; the fifth folder ignored; the unique display name;
  late join; "start over in my seat".
- The shared folder checks of design 7.2 and 6.3 (exists, writable, empty or same race, path
  budget) and the install checks (newer `raceFormat`, `dateUnit` other than DAY under
  R4-01's rule name and reason (OV-30), missing Making History for `alternateSites`, and an
  empty modified-Kerbin slot that R2-02 fills (OV-31)).
- Create, Join, Start over and Continue as one production setup core; Create and Join are
  reachable from the seam verb `RaceSetup action=create|join` and the session from
  `RaceCommand action=status` before any UI exists (the uncalled-method ratchet, design
  15.14; verb names and argument names are the test-and-harness ones, OV-01).
- The main menu Multiplayer entry `ParsekRaceMenu`, hidden behind a developer switch until
  R3's rivals and R4's board exist (question 1, assumed (a)), the setup dialog
  `RaceSetupDialog`, and the Multiplayer section of the Settings window (design 7.1, 7.2,
  15.14).
- Addition A7 (design 15.11): the shared "find a scenario node" helper, the log bundle and
  the test-batch reconcile leaving `Parsek/Race/` alone.
- The `.claude/CLAUDE.md` edit (and its `AGENTS.md` copy) recording the three owner UI
  exceptions, in the task that builds the first one (R1-14 owns it, OV-42).

Not built here: the sites, the terrain patches and scenery (R2: a joiner at slot 1, 2 or 3
plays at the stock KSC until R2 lands, which is still "plays as single player"); export,
import, the copy worker, the rival cache and own mirror contents, the post-sync clash
re-check (R3E-18, calling R1-09's rules); the board, the Space Race window, Sync now and the
sync status text (R4-09, in R1-18's Settings section); dressing kerbals in a look and the
look colors (R5); recorded opponents and the `kind` values RECORDED and AI (R8). R1 writes
and accepts only `kind = HUMAN`.

## 2. Starts after

- **P12-01, P12-02** (design 15.11): the list form of `[forbidden]` in both parsers
  (P12-01) and the Controllers rule covering `ParsekRace*` root files (P12-02). R1-01 needs
  both; every task adding a `ParsekRace*.cs` root file needs P12-02.
- **P7-01**: the floor id check with a fixed character set. R1-08 validates player and race
  ids with it. P7-02's public save-root accessor is not used (see section 9, item 11; R3E-01
  does not use it either).
- **P10-01**: the create-and-start core lifted out of `TestCommands`. R1-10 needs it, with a
  "before start" step (section 9, item 3).
- **Spike S6** (design section 12 table, line 1799 and 1803 to 1804): a scenario node of an
  unknown module type survives a save; cloning a main menu entry; creating and starting a
  save from a dialog. R1-03 rests on the first fact, R1-14 on the second, R1-10 and R1-15 on
  the third. The S6 list must also answer the VERIFY items of section 3.8.
- **Spike S4a**: whether the new `Game` is in `HighLogic.CurrentGame` at the scene load
  request. It gates R1-04's trigger (OV-51).
- **T-01** (test-and-harness): the headless `SaveWriterDriver` that R1-12's golden generator
  runs through (OV-15).
- **T-13** (test-and-harness): lane RACE-1, which carries the gate and join assertions of the
  dropped R1-13 (OV-02). R1-14 waits for it.
- Spike S8 (15.9) does not block R1; see section 9, item 9.
- Prerefactor P11-01 (window count derived in specs) only if the IMGUI fallback of R1-14 or
  R1-15 is used, because a fallback is a new Parsek window.

## 3. What the code looks like today

### 3.1 Scenario registration and the game-mode gate

- `ParsekScenario` is registered for every game:
  `[KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT,
  GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]`
  (`Source/Parsek/ParsekScenario.cs:30-32`). `OnSave` is at `ParsekScenario.cs:483`, `OnLoad`
  at `:1007`, `OnDestroy` at `:2410`.
- In an inert game mode or under the backup hold, `OnLoad` stashes the node verbatim and
  returns (`ParsekScenario.cs:1018-1020`, `ParsekGameModeGate.CheckInert` and
  `StashInertGameModeNode`); the stash copies the node as is
  (`ParsekScenario.InertPassthrough.cs:49`). `OnSave` checks the same gate (`:488`).
- `ParsekGameModeGate` (`Source/Parsek/ParsekGameModeGate.cs:35`) is placed in `Patches` by
  a name rule (`scripts/arch/modules.toml:186`). `IsActiveMode` (`:54`),
  `IsInertForCurrentGame` (`:123`), log-once `CheckInert` (`:156`), live read kept in a
  `NoInlining` core (`:239-252`), the backup hold (`:141`). This is the pattern the race gate
  copies: a pure decision plus a live read in a no-inline core.
- The main-menu return is hooked with an INSTANCE handler, because a static handler throws
  inside stock's event delegate (`ParsekScenario.SceneTransitions.cs:230`, handler `:247`,
  MAINMENU branch `:249`).
- Existing `KSPAddon` hosts that live across scenes: `ParsekToolbarRegistration.cs:6`,
  `MapRenderProbe.cs:46`, `PostFlightDialogLog.cs:243` (all `Startup.Instantly`, once);
  `CurrencyReservationOverlay.cs:26` (`EveryScene`). No addon runs at `Startup.MainMenu`
  today: nothing Parsek draws at the main menu.

### 3.2 Creating and loading a save from code

- `ParsekTestCommandAddon.NewGame.cs:253` `CreateAndStartNewGame`: deletes an existing save
  like stock overwrite (`:261`), builds parameters with
  `GameParameters.GetDefaultParameters(stockMode, ToStockPreset(p.Preset))` (`:279`), calls
  `GamePersistence.CreateNewGame(...)` into `HighLogic.CurrentGame` (`:280`), fires
  `GameEvents.onGameNewStart` (`:289`) and `HighLogic.CurrentGame.Start()` (`:296`). P10
  lifts this core into a production file.
- The seam's load route calls `GamePersistence.LoadGame(name, save, true, false)`
  (`ParsekTestCommandAddon.cs:2256`), fires `onGameStatePostLoad` like the stock load dialog
  (`:2310-2320`), and for SPACECENTER sets `HighLogic.CurrentGame`, runs
  `GamePersistence.UpdateScenarioModules`, `SaveGame(..., "persistent", ...)` and
  `game.Start()` (`:2380-2426`). Its comment states that `SpaceCenterMain.Start` re-reads
  `persistent.sfs` from disk, which is why the save is written before `Start`.
- `LoadReconcilePolicy.KnownLoadInitiators` (`Source/Parsek/LoadReconcilePolicy.cs:242-280`)
  lists the files allowed to call `GamePersistence.LoadGame(`; `ParsekTestCommandAddon.cs`
  is one of them.

### 3.3 The copies of "find the Parsek scenario node" (A7)

Over a `ProtoScenarioModule` list (six):

- `PlaybackScopeTracker.UnionFromScenarios` (`Source/Parsek/PlaybackScopeTracker.cs:229`)
- `RouteLoadReconcile.ReadSavedEventsFromScenarios` (`Logistics/RouteLoadReconcile.cs:738`)
- `RouteStore.ReadSavedRouteStateFloorUTFromScenarios` (`Logistics/RouteStore.cs:1205`)
- `RouteStore.ReadSavedCommittedRoutesFromScenarios` (`Logistics/RouteStore.cs:1253`)
- `RevertInterceptor.FindParsekScenarioProtoNode(Game)` (`RevertInterceptor.cs:853`, also
  called from `RewindLauncher.PersistentWrite.cs:101`)
- `LedgerHeldWindow.ReadSavedFromScenarios` (`GameActions/LedgerHeldWindow.cs:291`)

Over a GAME `ConfigNode` (three, plus one tooling copy):

- `PlaybackScopeTracker.UnionFromGameNode` (`PlaybackScopeTracker.cs:252`)
- `ParsekScenario.FindParsekScenarioNodeInGame` (`ParsekScenario.CommittedTreeCarry.cs:467`)
- `PreParsekBackup.HasParsekGameplayFootprint` (`PreParsekBackup.cs:286`), which walks every
  scenario node for "any populated Parsek node"
- `CareerSaveParser.FindScenario` (tooling, the ledger ground-truth parser), which is
  independent of production on purpose.

The scenario name literal is repeated at `SaveReferenceScanner.cs:84` and
`PreParsekBackup.cs:54`.

### 3.4 Folders under `Parsek/` that something sweeps whole

- `scripts/collect-logs.py:176-188` (`parsek_sidecar_dirs_to_copy`) returns every
  `Parsek/<dir>` beside the save; `:191` copies them into the bundle.
- `ParsekScenario.TestBatchReconcile.cs:170-195` deletes the live `Parsek/` folder and copies
  the batch snapshot back.
- `InGameTests/InGameTestRunner.cs:1965-1995` (`CaptureBatchFlightParsekSaveSnapshot`)
  copies the whole `Parsek/` folder at batch start, and `:2579`
  (`RestoreParsekSidecarsFromSnapshot`) swaps it back.
- The C# file collector does NOT sweep it: `ParsekFileCollector.cs:203,235,236` walk only
  `Recordings`, `RewindPoints` and `Saves`.
- `PreParsekBackup.MaybeBackupOnFirstColdContact` (`PreParsekBackup.cs:470`) takes no backup
  of a save that has a footprint (`:529`) or is brand new (`:535`); a race save created by
  the setup core is brand new at its first load.

### 3.5 Windows, dialogs and Settings

- `ParsekUiScale` (`Source/Parsek/UI/ParsekUiScale.cs:43`; `BeginHost` `:250/:255`, `Window`
  `:304`) scales IMGUI windows. `ParsekUiScaleTests.cs:257-312` gate it: only
  `ParsekUiScale` and `TestRunnerShortcut` call `GUILayout.Window`; every draw host opens a
  scale scope (`EachDrawHostOpensAScaleScope` list `:297-301`). Hosts: `ParsekKSC.cs:259`
  (`"KSC"`), `ParsekFlight.cs:1448` (`"Flight"`).
- `ParsekWindowSet` builds and holds the windows (`UI/ParsekWindowSet.cs:20,36,78`).
- Settings: `UI/SettingsWindowUI.cs` window key `:58`, tooltip budget comment (71
  characters, `TooltipEchoBudgetTests`) `:60`, `DrawIfOpen` `:94`, 280 x 600 window `:112`,
  `DrawSettingsWindow` `:349`, sections Interface, Ghosts, Looping, Sample density,
  Diagnostics gated by `UiSurfaceVisibility` (`:385-450`), `DrawInterfaceSettings` `:512`.
  A typed IMGUI field exists in Settings (the loop-period edit) and in
  `UI/LogisticsWindowUI.cs:524`.
- Stock `PopupDialog` is used by `MergeDialog.cs` (lock id `"ParsekMergeDialog"` `:41`),
  `CommittedActionDialog.cs`, `ReFlyRevertDialog.cs`. No Parsek dialog uses
  `DialogGUITextInput` today.
- Host interface precedent for a UI-side caller of entry-layer work:
  `ParsekFlightMergeFlow.cs:21` `IMergeFlowHost`, implemented by
  `ParsekFlight.MergeFlow.cs:13`.

### 3.6 Architecture map and ratchets

- `scripts/arch/modules.toml`: folder rules `:17-67`; Controllers prefix
  `^Parsek(Flight|KSC|TrackingStation|Scenario|Harmony|Playback)|^WatchMode` (`:322`), which
  does not match `ParsekRace*` (P12 fixes it); LogIO prefix
  `^ParsekLog|^FileIO|^ParsekProcess|^Suppression|^PreParsek` (`:326`); the Core kernel rule
  must stay last (`:477-479`); layers `:511-519`; `[headless.modules]` `:531-536` (Core,
  Trajectory, Missions, Timeline, Reaim); `[size].runtimeCoupled` `:560`; `[forbidden]` holds
  two edges (`:562-566`); `[allowed]` is empty.
- `scripts/arch/layers.py:71-77` declares a module from its rule name, so a module with no
  files exists; `:225-254` reds a forbidden spec naming an undeclared module.
- `scripts/arch/test_archview.py:4031-4091` pins the knot sizes `[435, 9, 4, 3, 2]`; `:4097`
  pins the Core kernel at 8 files; `:4179` requires an atlas summary for every module
  (`missingSummaries == []`, summaries in `scripts/arch/atlas.toml:51-110`).
- `scripts/arch/facts.toml:18-31` lists inert types (`ConfigNode`, `Game/Modes`, `KSPAddon`,
  `KSPScenario`, `ScenarioCreationOptions`, `GameScenes`); `:49` does not follow calls into
  `ParsekLog`. `ProtoScenarioModule`, `Game`, `HighLogic` are not inert: a headless method
  that names them is a leak.
- `scripts/arch/facts_pins.toml:75` bounds the uncalled production methods at 491. Tooling
  (`TestCommands`, `InGameTests`) is reachable from the roots, so a seam verb is a caller.
- `scripts/arch/lifetimes.toml:20-27` defines the lifetimes; the Cold roots are
  `ParsekScenario.OnAwake`, `OnLoad`, `ResetSaveScopedSessionState` and
  `ParsekSettings.OnLoad` (`:37-41`). `SaveScopedSessionResetTests.cs:25` holds the
  `InventoryTypes` list.

### 3.7 Save format and paths

- `Source/Parsek/SaveFormat.cs`: `CurrentSaveFormat` `:89`, rows `:92-117`, `All`
  `:118-122`, upgrade steps `:129`. `machine` files such as `settings.cfg`
  (`ParsekSettingsPersistence.cs:39`) have no row.
- `Fixtures/FormatGolden/README.md`: a `vN` folder is frozen; `v1/formats.txt` lists ten
  rows. `SaveFormatGoldenGateTests.cs:42-70` (`FindCoverageProblems`) reds when no golden
  folder carries each version from a row's baseline to its current.
- `RecordingPaths.SaveRootOverrideForTesting` `:65`, `ResolveSaveScopedPath` `:67`,
  `ValidateRecordingId` (asks the OS for invalid characters) `:302`, private
  `TryGetSaveContext` `:329`. `PreParsekBackup.SanitizeSaveName` `:181` uses
  `Path.GetInvalidFileNameChars` too.
- `LaunchSiteExclusionZones.cs:118` checks Making History with
  `Expansions.ExpansionsLoader.IsExpansionInstalled("MakingHistory")`.

### 3.8 Grep gates and seam verbs

- `GrepAuditTests.cs:46-70` runs the pwsh gates with the managed fallback;
  `:99-158` holds the NotVacuous proofs. `scripts/grep-audit-gui-mock-writeset.ps1` is the
  shape of the "no writer of the own model" gate (type allowlist, comment-stripped, alias
  ban).
- `scripts/grep-audit-parsek-file-deletes.ps1` with its allowlist pins every `File.Delete`
  site by file and count (the NewGame file is pinned at 4).
- Seam verbs register in `TestCommands/TestCommandVerbs.cs` (NewGame at `:397-405`) and are
  mirrored in `harness/lib/hlib.py` (`NEWGAME_*` at `:4571`).
- VERIFY in S6 (no KSP reference assemblies were available to decompile): that
  `ScenarioCreationOptions.None` neither adds nor removes the proto in
  `UpdateScenarioModules`; that a proto added to `game.scenarios` before `Game.Start` is
  instantiated in its target scenes; that `HighLogic.CurrentGame` is the game being entered
  when `onGameSceneLoadRequested` fires on every load path; `DialogGUITextInput` behavior in
  a `MultiOptionDialog` at the main menu; whether the stock difficulty panel can be opened
  from a mod dialog; the `TextProButton3D` clone of design line 1803; whether
  `FlightGlobals.Bodies` is populated at the main menu (for the Kerbin check, now R2-02's,
  OV-31).

## 4. Tasks

Every task: CHANGELOG and todo are touched only when it changes what a player sees. While
the Multiplayer entry is hidden behind the developer switch (question 1, assumed (a)), no
R1 task changes what a player sees, so R1 writes todo entries only; the CHANGELOG entry
lands with the change that turns the switch on. Log lines use the subsystem tag `Race`
(`[Parsek][INFO][Race] ...`), one of the race tags fixed in R1-06's tag list (OV-57); the
report-only tag map `scripts/arch/log_tags.json` is refreshed in the task that first logs.

### R1-01 Register the three race modules

- **Change:** `scripts/arch/modules.toml`: folder rules `RaceCore/` -> `RaceCore`,
  `RaceExport/` -> `RaceExport`, `RaceWorld/` -> `RaceWorld` beside the other folder rules
  (before the Core kernel rule); `[layers.modules]` model += `RaceCore`, features +=
  `RaceExport`, `RaceWorld`; `[headless.modules]` += `RaceCore` with its reason line;
  `[size].runtimeCoupled` += `RaceWorld`; the 71 edges of design 15.4 in P12-01's list form
  (13 modules -> 3, `RaceCore` -> 18, `RaceWorld` -> 9, `RaceExport` -> 5).
  `scripts/arch/atlas.toml`: one summary per new module. `docs/dev/arch/README.md`: the
  module table and the headless table gain the rows.
- **Proof:** the DLL-free arch suite (`python3 -m unittest discover -s scripts/arch -q`);
  CI's semantic steps after a Debug build (no edge crosses: no file exists yet). Knot sizes,
  layer pins, headless pins and the facts bound do not move; if a cell reds on a declared
  module with no files, merge this task into R1-03 instead of re-pinning around it.
- **Registers:** the files above only.
- **Size:** S. **Depends on:** P12-01, P12-02. **PC:** no.
- **Risk:** low; a zero-file module may trip a summary or atlas cell, which the fallback above
  answers.

### R1-02 One helper to find a scenario node (A7, part 3)

- **Change:** new root file `Source/Parsek/ScenarioNodeLookup.cs` (module LogIO: add
  `^ScenarioNode` to the LogIO prefix at `modules.toml:326`). Three internal static methods:
  `FindInProtoList(IList<ProtoScenarioModule>, string moduleName)` returning the module's
  data node or null; `FindInGameNode(ConfigNode game, string moduleName)` over `SCENARIO`
  children; `EnumerateScenarioNodes(ConfigNode game)`. One constant
  `ParsekScenarioName = "ParsekScenario"`, pinned to `nameof(ParsekScenario)` by a test.
  Replace the six proto-list copies and the three GAME-node copies of section 3.3, each
  keeping its null handling and its log text; `PreParsekBackup.HasParsekGameplayFootprint`
  keeps its "any populated node" rule over `EnumerateScenarioNodes`. `CareerSaveParser`
  keeps its own reader (its independence is the point of the ground-truth harness). Each of
  the three methods has production callers in this pull request (the replaced copies), so
  the uncalled-method ratchet does not move (A1).
- **Proof:** xUnit `ScenarioNodeLookupTests` (found, absent, two nodes of one name takes the
  first as today, a node with no `name`); the existing tests of every replaced method stay
  green unchanged (`RouteLoadReconcile*`, `LedgerHeldWindow*`, `PlaybackScopeTracker*`,
  `PreParsekBackup*`, `RevertInterceptor*`, `CommittedTreeCarry*`). No new log line.
- **Registers:** `modules.toml` prefix; atlas unchanged; re-pin the knot canary only if the
  text scan moves it (it should not: LogIO is below every caller).
- **Size:** S. **Depends on:** none; order: after P10-01 (both edit the LogIO prefix,
  task index C). **PC:** no.
- **Risk:** low; a behavior-neutral replacement, but a caller that relied on "last match"
  would change, so the test pins today's order per caller.

### R1-03 The race mark: `RaceLink` and `ParsekRaceScenario`

- **Change:**
  - `RaceCore/RaceLink.cs`: sealed immutable value with the fields of design 6.1 (`raceId`,
    `playerId`, `siteSlot`, `siteTableId`, `ladderId`, `dateUnit`, `calendar`,
    `alternateSites`, `gameMode`, `difficultyDigest`) plus `raceLinkFormat`; codec
    `RaceLinkCodec.TryRead(ConfigNode, out RaceLink, out string reason)`, invariant
    culture, unknown keys ignored on read. `RaceLinkCodec.Write(RaceLink, ConfigNode)` is
    specified here and lands with its first production caller, R1-10's before-start step
    (A1, the uncalled-method ratchet).
  - `RaceCore/RaceFormat.cs`: constants `LinkCurrent = 1` (the mark's `raceLinkFormat`) and
    `Current = 1` (the exchange `raceFormat`) (OV-16), `ScenarioModuleName =
    "ParsekRaceScenario"`, `DateUnitDay = "DAY"`, the table ids `"kerbin-equator-1"` and
    `"standard-1"`; a `Classify(int?)` returning the same five classes as
    `SaveFormat.Classify` (RaceCore may not name `SaveFormat`). R3E-01 and R3E-04 use this
    file; they add no second constant or classifier.
  - `RaceCore/RaceScenarioChild.cs`: interface `IRaceScenarioChild` (child node name,
    `Load(ConfigNode)`, `Save(ConfigNode)`), the child hooks of OV-28.
  - Root file `Source/Parsek/ParsekRaceScenario.cs` (Controllers via P12-02's rule):
    `[KSPScenario(ScenarioCreationOptions.None, FLIGHT, SPACECENTER, TRACKSTATION, EDITOR)]`
    (S6). Instance state only: the loaded node copy, the parsed link and a list of
    `IRaceScenarioChild` handlers built in `OnAwake` (empty in R1). `OnLoad` keeps the node
    verbatim, hands each child node whose name has a handler to that handler, parses the
    link, logs one line; when Parsek is inert (`ParsekGameModeGate.CheckInert`) it stashes
    and stops. `OnSave` writes the kept node back verbatim (newer or unknown keys and every
    child without a handler survive) and lets each handler write its own child. R3E-15
    (disclosure, `testTooling`) and R5-11 (`RACE_LOOK_DRESS`) add their children through the
    hooks, each with its `LoadReconcilePolicy` row in the task that adds it (OV-28). No code
    path in single player creates the module: only a proto in `game.scenarios`
    instantiates it.
- **Proof:** xUnit `RaceLinkCodecTests` (read cells here, the round trip with R1-10's `Write`; every
  missing required key gives its reason; `raceLinkFormat` newer, missing, older classify; `de-DE`
  culture pin; unknown key kept by the verbatim path); `RaceScenarioChildTests` (an unknown child
  round-trips byte-equal; a child with a handler reaches its `Load` and is written by its `Save`);
  `RaceFormatPinTests`, the one pin class for the race formats that R1-12 and R3E-04 add cells to
  (`ScenarioModuleName == nameof(ParsekRaceScenario)`; `Classify` agrees with `SaveFormat.Classify`
  on a table of inputs). In-game test `RaceModeTests.MarkSurvivesSaveLoad` (category `RaceMode`,
  SPACECENTER, hosted by T-29's RACE-9, A2: a proto added to a scratch game round-trips byte-equal).
  Log: `[Race] Mark loaded: raceId=.. playerId=.. slot=.. format=..` and `[Race] Mark held verbatim:
  reason=..`.
- **Registers:** `lifetimes.toml` needs nothing (no static); `headless_pins.toml` re-pinned
  if `RaceCore` shows a leak (it should show none); atlas file count; `log_tags.json`; the
  lane catalog for the new `RaceMode` category (`python harness/tools/lane_catalog.py`).
- **Size:** M. **Depends on:** R1-01, P12-02, S6. **PC:** yes, the in-game test.
- **Risk:** medium; if S6 shows stock drops a proto of a `None` module, the module needs
  another creation option, which the in-game test would show at once.

### R1-04 `RaceGate` and its host

- **Change:**
  - `RaceCore/RaceGate.cs`: `Decide(ConfigNode raceNode, bool parsekInert)` returning a
    `RaceGateDecision` (IsRaceSave, link, reason: `no-mark`, `inert`, `link-invalid:<r>`,
    `link-newer`, `race`). A static current decision with `Apply(decision)` and `Clear()` as
    the only writers (its own type; the cross-type static-write ratchet stays put); `Apply`
    and `Clear` return the transition from the previous decision (none, entered, left,
    switched race). `IsRaceSave` / `CurrentLink` readers, first called by the race
    scenario's cross-check below (A1). A newer or invalid link is "not a race save" and the
    node is still kept verbatim by R1-03.
  - Root file `Source/Parsek/ParsekRaceBoot.cs` (Controllers): `[KSPAddon(Startup.Instantly,
    true)]`, `DontDestroyOnLoad`, one instance handler `OnSceneRequest(GameScenes)` on
    `onGameSceneLoadRequested`, the one scene-request handler (OV-51, A4): R2 adds no
    second addon and there is no `ParsekRaceSitesHost`. On a request for any scene but
    MAINMENU it reads `HighLogic.CurrentGame.scenarios` through
    `ScenarioNodeLookup.FindInProtoList` (in a `NoInlining` core, the
    `ParsekGameModeGate.ReadLiveModeCore` pattern), asks
    `ParsekGameModeGate.IsInertForCurrentGame`, and applies the decision. On MAINMENU it
    clears. It logs the transition `Apply` / `Clear` returns, so leaving shows on any race
    to non-race change by any route (main menu, the in-game load, a harness `LoadGame` that
    skips the main menu), as do entering and race A to race B. R2-01 adds one call in this
    handler, on every request (the main menu included): the wanted sites state from the
    decision, then `RaceSites.Reconcile`. If S5 asks for the lazy copy build, R2-07 adds
    its scene-ready tick to this addon. R1 adds neither the call nor the tick and no empty
    hook method (A1).
  - `ParsekRaceScenario.OnLoad`: when `RaceGate.IsRaceSave`, cross-checks its parsed link
    against `RaceGate.CurrentLink` and warns on a mismatch.
- **Proof:** xUnit `RaceGateTests` (decision table over: no node, valid, invalid, newer, inert;
  Apply then Clear; race A to race B gives the switched transition; race to a single-player save
  without the main menu gives left); a single-player inertness pin `RaceInertTests`: the `v1` golden
  `persistent.sfs` GAME node decides `no-mark`, and the R1 code adds no `SCENARIO` node to it.
  In-game test in `RaceMode` (SPACECENTER, RACE-9): the gate is true after loading a race
  fixture save. (The single-player side is not an in-game cell: RACE-9 runs only in a race
  save, and T-13 pins `reason=no-mark` in a stock save.) Logs: `[Race] Gate: race save raceId=..
  slot=.. scene=..`, `[Race] Gate: not a race save reason=..` (once per game and scene, like
  `CheckInert`), `[Race] Gate: cleared at main menu`, `[Race] Gate: transition entered|left|switched
  from=.. to=..`.
- **Registers:** `lifetimes.toml`: `RaceGate`'s static is `process` ("recomputed at every
  scene request and cleared at every main menu return; never owned by a load");
  `headless_pins.toml` (RaceGate must show no leak); `log_tags.json`.
- **Size:** M. **Depends on:** R1-02, R1-03, S4a. **PC:** yes, the in-game tests.
- **Risk:** medium; if a stock load path sets `HighLogic.CurrentGame` after the scene
  request, the decision is stale for one scene. S4a answers this before the task starts
  (OV-51); the in-game tests on each path (Resume, quickload, revert) would show it, and
  `onGameStatePostLoad` or a prefix on `Game.Start` is the trigger then, the one handler
  moving with the gate and R2's reconcile call together.

### R1-05 The race mark's row in `LoadReconcilePolicy`

- **Change:** `LoadReconcilePolicy.cs`: new `LoadStateCategory.RaceLink` (`:59-103`);
  `Decide` returns `LoadReconcileAction.Save` for every load kind, with the reason "identity
  that never changes after creation; every load takes the loaded save's copy" (design 15.7).
- **Proof:** `LoadReconcilePolicyTests` (table shape covers the new category; `DecideEarly`
  consistency).
- **Registers:** none beyond the test.
- **Size:** S. **Depends on:** R1-03. **PC:** no.
- **Risk:** low; a table row with no behavior of its own.

### R1-06 The race grep-audit gates (design 15.4)

- **Change:** T-08 is merged into this task (OV-10): every race gate runs on one shared
  scanner, built here once and reused by the later race gates (R3W-27's rival-store gate).
  - `Source/Parsek.Tests/GrepAuditTests.Race.cs` (partial of the test class) with a managed
    `RaceSourceScan` over the files of `RaceCore/`, `RaceExport/`, `RaceWorld/` and the
    `ParsekRace*.cs` root files: comment-stripped, string literals masked, `using X =`
    aliases banned, type references matched with the `GuiMockTypeRef` regex shape.
  - `scripts/grep-audit-race-core-no-store.ps1`: no file of `RaceCore` names a type whose
    name ends in `Store`.
  - `scripts/grep-audit-race-no-writer.ps1` + allowlist: the ONE no-writer gate (OV-11; T-15
    is dropped). No race file names a mutating entry point of a store or a persistence
    writer of the save (`GamePersistence`, `ScenarioModule` saves, the store mutators listed
    in the allowlist header). The allowlist states the two writes design 15.4 names
    (`RaceLooks`, `RaceSites`) for R2 and R5 to fill.
  - `scripts/grep-audit-race-no-recorder.ps1`: the one recorder ban (OV-12): no race file
    names `FlightRecorder`, `BackgroundRecorder`, `RecordingStore`, `PendingTreeSlot`,
    `TreeCommitService`, `TreeDiscardService`.
  - "`Recording` types only in the loader": a DLL-free cell in `scripts/arch/test_archview.py`
    over the text scan's type-to-module map (a grep cannot know module membership; the
    `Recording` type list comes from `modules.toml`, not typed by hand). The loader files
    are read from `scripts/race-loader-files-allowlist.txt`, created here empty, one file
    per line with its reason; R3W-27 adds its loader entries to that file and writes no
    second script (OV-12; T-09 is dropped).
  - The race log tag list (OV-57): a constant `RaceLogTags` in `GrepAuditTests.Race.cs`,
    one plain tag per subsystem (A4; never a nested form like `[Race][RivalLane]`): `Race`
    (R1), `RaceSites` and `RaceLaunchGuard` (R2), `RaceExport` and `RaceSync` (R3E; every
    sync, import and poll line is `RaceSync`, never `RaceImport`), `RivalStore` and
    `RivalLane` (R3W), `RaceBoard` (R4, R6), `RaceLooks` (R5). A cell reds when a
    `ParsekLog` call in a race file uses a tag that is not on the list; a new tag joins the
    list in the task that first logs under it.
- **Proof:** for each gate: the gate fact on the tree; an anti-vacuity cell (the scan sees at
  least the race modules' file count and a minimum reference count); a planted-red cell (a
  temp copy with `MilestoneStore.` in a `RaceCore` file reds the no-store gate, a
  `RecordingStore.` line in a temp `RaceWorld` file reds the recorder ban and the loader
  cell); a mirror cell (the pwsh script and the managed scan agree on a fixed input). The
  no-writer gate also has T-15's cell: every persistence writer of a `save`-lifetime type
  in `scripts/arch/lifetimes.toml` is on its denylist. Mutation check by hand in the PR
  (plant one line, see red).
- **Registers:** `GrepAuditTests.Race.cs`; `scripts/race-loader-files-allowlist.txt`;
  `docs/dev/arch/README.md` names the cell; CLAUDE.md's gate list if it names the family.
- **Size:** M (it absorbs T-08 and T-15's cell). **Depends on:** R1-01, R1-03 (so the scan
  sees a race file). **PC:** no.
- **Risk:** low; a vacuous gate is the danger, closed by the anti-vacuity and planted-red
  cells.

### R1-07 Logs and test batches leave `Parsek/Race/` alone (A7, parts 1 and 2)

- **Change:** `scripts/collect-logs.py`: `parsek_sidecar_dirs_to_copy` skips `Race`, and a
  new step writes `race-listing.txt` (relative path, size, modified time; no contents).
  `InGameTestRunner.CaptureBatchFlightParsekSaveSnapshot` skips `Race/`;
  `RestoreParsekSidecarsFromSnapshot` and `ParsekScenario.TestBatchReconcile` keep the live
  `Race/` (move it aside before the delete, back after the copy). The folder name is one
  constant beside the other `Parsek/` subfolder names.
- **Proof:** harness `scripts` and `harness/lib` suites (`SnapshotSourceSyncTests` reads the
  collect-logs layout and must stay green); xUnit over the pure "which dirs" helpers of the
  runner and the reconcile with a temp folder holding `Race/`; log `[TestBatch] kept
  Parsek/Race/ (n files)`.
- **Registers:** `scripts/grep-audit-parsek-file-deletes` allowlist counts if a delete site
  moves; `CLAUDE.md` "Scripts" line for `collect-logs.py` (mentions the listing).
- **Size:** S. **Depends on:** none. **PC:** no.
- **Risk:** low; an in-game batch crash path is the one that cannot run headless, so the
  reconcile change keeps the existing code shape and only filters a directory name.

### R1-08 Read side: codecs, timestamps, local race directory, session

- **Change:** every member below has its first production or tooling caller in this pull
  request (the session, the race scenario, the `status` action); a member whose first caller
  comes later is specified here and lands with that caller, as an edit to this task's file
  (A1, the uncalled-method ratchet).
  - `RaceCore/RaceManifest.cs`: `RaceManifest`, `PlayerManifest`, `RaceLocal` values (design
    6.1) and their READ codecs over `ConfigNode` (`race.cfg`, `player.cfg`, `local.cfg`).
    `kind` other than HUMAN is refused (`kind-unsupported`). Callers here: the session
    reads `local.cfg` and the local `race.cfg` copy, and, when `local.cfg` is missing, the
    display name and look back from the local copy `Race/own/player.cfg` (design 15.7).
    `RaceLocal` also carries four optional, additive keys from the start (OV-27; section 9,
    item 6), held as plain readonly fields so the read path stores them and R1-10's writer
    writes each back when present, with no accessor method of their own: `lastSyncRealTime`
    (written by R3E-17), `lastStandingDigest` (R3E-04), `firstImportDone` (false written by
    R1-11's join, true by R3E-18) and the `SHOW` block (kept as the node read). A typed
    reader, setter or accessor lands with its first caller (R3W-13 the SHOW reader, R4-10
    the SHOW setter, R3E-04 / R3E-17 / R3E-18 their keys).
  - `RaceCore/RaceTimestamp.cs`: parse `yyyy-MM-ddTHH:mm:ssZ` (design 6.5), called by the
    manifest reads. `Format` lands with R1-10's writers; R3E-01 adds `NextStamp` (OV-24).
  - `RaceCore/RaceIds.cs`: `race_` / `p_` + 32 hex; validation through P7-01's floor check,
    called by the read codecs. R1-08 ships validation only; id generation lands with
    R1-10 (Create mints both ids). R3E-01 adds the 8-character short id with its
    collision check and collision lengthening (design 6.3, OV-24); no R1 task calls it.
  - `RaceCore/RacePorts.cs`: `IRaceFolder` under the LOCAL race directory (design 15.3).
    R1-08 ships `TryRead` (the session's reads); R1-10 adds `SafeWrite` (Create's local
    files). R3E-01 adds the rest, which no R1 task calls: `List`, `Move`, `Exists`, and
    `Delete` if it is its first caller (OV-22); there is no second folder port.
  - `RaceWorld/RaceFolder.cs`: the adapter over `FileIOUtils`, rooted at
    `<KSP root>/saves/<save>/Parsek/Race/`, computed in RaceWorld from
    `KSPUtil.ApplicationRootPath` and `HighLogic.SaveFolder` (section 9, item 11; OV-23).
  - `RaceWorld/RaceSession.cs` and `RaceWorld/RaceRuntime.cs` (R1 owns both, OV-26): the
    session holds the link, `RaceLocal` and the local `race.cfg` copy. The ONE static is
    `RaceSession.Current`, set only by the session's own `Attach` / `Detach`;
    `RaceRuntime` is the facade, and `RaceRuntime.Current` forwards
    (`RaceSession.Current?.Runtime`) with no backing static; its first caller is the
    `status` action below. R3W-08 and the R4 / R6 tasks
    extend these two types (the first caller of a facade member adds it: R4-09
    `CurrentBoard`, R4-10 the SHOW setter, R4-11 the watch stub, R6-06 `KnownParts` /
    `KnownBodies`, OV-39). `ParsekRaceScenario.OnLoad` builds and attaches the session
    when the gate says race; `OnDestroy` detaches. A missing `local.cfg` gives a session
    with no folder ("ask for the folder again", design 15.7).
  - Test type `Source/Parsek.Tests/Generators/InMemoryRaceFolder.cs`: the one in-memory
    `IRaceFolder`, reused by every later race test (OV-17).
  - Seam verb `RaceCommand`, action `status` only (OV-01; E.6): new
    `TestCommands/TestCommandRaceCommand.cs` with a `KnownActions` table (the `RouteCommand` shape)
    and its applier partial. Single-phase, read-only, no arguments: prints, through
    `RaceRuntime.Current`, the gate decision, the race mark (race id, player id, slot), look, folder
    state and last sync of the current session. T-12 adds `set-folder`, T-19 `sync`, `offline` and
    `online`.
- **Proof:** xUnit `RaceManifestCodecTests`, `PlayerManifestCodecTests`,
  `RaceLocalCodecTests` (each required key's refusal reason; unknown blocks ignored; the four
  optional keys absent and present; `raceFormat` newer refused; `de-DE` pin),
  `RaceTimestampTests` (parse, bad stamps refused; the round trip with R1-10's `Format`),
  `RaceIdsTests` (the fixed character set on both platforms), `RaceSessionTests` over
  `InMemoryRaceFolder` (`local.cfg` missing reads name and look from `own/player.cfg`);
  `TestCommandRaceCommandTests` (parse, unknown action refused); a harness source-sync cell
  for `KnownActions` (it is not a closed-arg row, so no other gate sees it). Logs: `[Race]
  Session: raceId=.. folder=<set|missing> name=.. look=..`.
- **Registers:** `TestCommandVerbs.ImplementedVerbs` + the `hlib.py` rows and their sync
  test; `design-autotest-command-seam.md` (known-verb row and the `status` contract);
  `lifetimes.toml`: `RaceSession.Current` is `scene` ("attached by the race scenario's
  OnLoad, detached in its OnDestroy"); `headless_pins.toml`; `log_tags.json`.
- **Size:** M. **Depends on:** R1-04, P7-01. **PC:** no (the session's in-game proof rides on
  T-13's lane RACE-1).
- **Risk:** medium; reading `local.cfg` on every scene's `OnLoad` is a small file read on
  the main thread from the LOCAL directory, which design 15.9 allows.

### R1-09 Seats, slots, looks and names (`RaceSlots`)

- **Change:** `RaceCore/RaceSlots.cs`, pure over a list of `PlayerManifest` values:
  `OrderPlayers` (by `joinedRealTime`, ties by `playerId`); `RacePlayers` (the first four;
  a fifth and later folder is ignored and reported); `FirstFreeSlot` (0, 1, 2, 3 order,
  design 7.3); `FreeLooks` and `FirstFreeLook` (design 7.4); `ResolveClashes` returning, for
  the local player, `SameSiteAs` (both keep it; told once) and a changed look when this
  player is the later joiner with a taken look; `IsNameTaken` (trimmed, case-insensitive,
  invariant). `StartOverSeat(playerId)` (keeps id, slot and look) is specified here and
  lands with its first caller, R1-11's `StartOver` (A1).
  - First caller (A1): `RaceCommand action=status` gains the session's shared folder: the
    seats (`OrderPlayers`, `RacePlayers`, a fifth folder reported), the free slot and free
    looks, the local player's clash outcome (`ResolveClashes`) and whether its display
    name is taken by another seat (`IsNameTaken`). To read the folder this task creates the
    read half of the shared-folder port, `IRaceSharedFolder` (`List`, `Read`) in
    `RaceCore/RacePorts.cs`, and its main-thread adapter `RaceWorld/RaceSharedFolder.cs`
    (section 9, item 9); R1-10 adds the write members and the checks.
- **Proof:** xUnit `RaceSlotsTests`: four joins in order give 0, 1, 2, 3; a seat taken out
  of order (slot 1 held) gives 0 then 2; two simultaneous joins on one slot both keep it and
  the later one gets the "same site" flag; two on one look: the later one gets the first free
  look; ties broken by player id; five folders: the latest is ignored; name clash rules;
  the start-over cell lands with R1-11; `status` output cells over `InMemoryRaceFolder`
  and a fake shared folder.
- **Registers:** the `status` contract text (seat lines); `headless_pins.toml`.
- **Size:** S to M. **Depends on:** R1-08. **PC:** no.
- **Risk:** low; pure rules. R3E-18 calls `ResolveClashes` again after each import.

### R1-10 Create a race (setup core, write side)

- **Change:**
  - Members specified earlier that Create is the first caller of (A1): WRITE codecs for
    `race.cfg`, `player.cfg`, `local.cfg` in `RaceCore/RaceManifest.cs` (the four optional
    `local.cfg` keys written back when present); `RaceLinkCodec.Write` (R1-03);
    `RaceTimestamp.Format` and the id generation of `RaceIds` (R1-08); `IRaceFolder.SafeWrite`
    and `RaceFolder`'s implementation (R1-08); the write members of R1-09's
    `IRaceSharedFolder` and `RaceSharedFolder`. `difficultyDigest` = SHA-256 of the
    `DIFFICULTY` node's invariant text (hex). `RaceCore/RaceModDiff.cs` (the joiner's
    informational diff) lands with its first caller, R1-11.
  - `RaceCore/RaceFolderCheck.cs`: pure checks of design 7.2 and 6.3 over
    `IRaceSharedFolder` (exists, writable, empty, holds a race with id X, longest
    expected path under 250 characters for both the shared layout and the local cache).
    R1-10 creates the path-length check with a conservative constant for the longest
    expected path, held in this R1 file; R3E-01 (lands later) replaces the constant with
    the computed longest expected path and updates this call site. R3E-16 replaces the
    port with `IRaceTransport` and moves this check and the adapter onto it in the same
    PR (OV-25).
  - `RaceCore/RaceCalendar.cs`: the minimal date rule under R4-01's name and reason
    (OV-30): `TryAcceptDateRule(dateUnit, calendar, out reason)` refuses a `dateUnit`
    other than `DAY` with `date-unit-not-day`; R1 does not check `calendar`. R4-01 extends
    this file (calendar rules, `calendar-unknown`, day arithmetic) and adds no second rule.
  - `RaceCore/RaceInstallCheck.cs`: newer `raceFormat`, the date rule above, Making History
    missing with `alternateSites`, and an empty modified-Kerbin slot (a check that always
    passes in R1; R2-02 fills it with `RaceSites.CheckKerbinStock`, OV-31). Each refusal has
    its exact reason string. Create runs the whole check over the manifest it builds, so
    every rule, the date rule and the slot included, has its caller in this task (A1).
  - `RaceWorld/RaceInstallFacts.cs`: the live reads
    (`ExpansionsLoader.IsExpansionInstalled`, the loaded mod list from `AssemblyLoader`,
    `GameSettings.KERBIN_TIME` for the calendar), each in a `NoInlining` core. The Kerbin
    reads are R2-02's (`RaceWorld/KerbinProbe.cs`).
  - Root file `Source/Parsek/ParsekRaceSetup.cs` (Controllers): `Create(RaceCreateRequest)`
    validates, writes `race.cfg` and the creator's `players/<id>/player.cfg` (safe-write),
    then calls P10-01's separate steps itself: `NewSaveStarter.CreateGame(saveFolder, mode,
    parameters, flagUrl)`; then its own before-start step on the returned game, which adds
    the `ParsekRaceScenario` proto with the link to `game.scenarios`, writes
    `Parsek/Race/local.cfg`, the local `race.cfg` copy and `Race/own/player.cfg`, and saves
    `persistent.sfs`; then `NewSaveStarter.StartGame(game)`. P10-01 has no callback
    parameter; the step is plain code between the two calls (`onGameNewStart` has fired by
    then, as in stock's order). The save name is `<race name> - <display name>`, sanitized
    and capped by the path budget; an existing save folder (through
    `NewSaveStarter.ResolveSaveDirectory`) is refused, so the delete step is never called
    (no overwrite, no new delete site).
  - Seam verb `RaceSetup`, action `create` (OV-01; T-11 is dropped and replaced by this task
    and R1-11): new `TestCommands/TestCommandRaceSetup.cs` (pure parse) and
    `ParsekTestCommandAddon.RaceSetup.cs` (applier). Two-phase like NewGame: quit to the
    main menu, then create through P10-01's core and `ParsekRaceSetup.Create`. Arguments
    (T-11's names): `action`, `race` (race name), `mode`, `preset`, `folder` (a lane passes
    T-03's `${raceShared}`), `name` (display name), `look`, `sites=on|off`
    (`alternateSites`), `endDate`, `noRetakes`, `as=<suffix>` (the created save's name, so a
    lane names it in advance). Refusal reasons are the setup dialog's (design 7.2) verbatim,
    so a lane can drive each. Budget 300 s. The verb marks nothing itself: R3E-08 marks the
    save as tool-used at seam arming (OV-33).
- **Proof:** xUnit `RaceFolderCheckTests`, `RaceCalendarTests` (the `date-unit-not-day`
  refusal; R4-01 adds its cells) and `RaceInstallCheckTests` (every refusal reason, path
  budget edges, the Kerbin slot passing), `RaceManifestWriteTests` (write then read gives
  the same value, the four optional `local.cfg` keys included; byte pin of a canonical
  `race.cfg`), the `RaceLinkCodecTests` and `RaceTimestampTests` round trips (their writers
  land here), `RaceSetupPlanTests` over the pure part of `ParsekRaceSetup` (request to
  files plan), `TestCommandRaceSetupTests` (every arg, every refusal) and a harness
  `RaceSetupSourceSyncTests` cell like `NewGameSourceSyncTests`. Harness lane RACE-1
  (T-13). Logs: `[Race] Create: raceId=.. folder=.. save=.. slot=0 look=..`, `[Race] Create
  refused: reason=..`.
- **Registers:** `TestCommandVerbs.ImplementedVerbs`; `design-autotest-command-seam.md`
  (known-verb row and a `#### RaceSetup` contract); the hlib rows beside the NewGame rows;
  the `lanecatalog.py` operation map; `TestCommands` placement is automatic (its folder
  rule); the delete gate (expect no change); `lifetimes.toml` (no statics expected);
  `headless_pins.toml` (`RaceCalendar`); `log_tags.json`.
- **Size:** M. **Depends on:** R1-08, R1-09, P10-01, S6. **PC:** yes, through RACE-1 (T-13).
- **Risk:** high; the first Parsek code that creates a save, and the race mark must be on
  disk before `SpaceCenterMain` reads `persistent.sfs`; RACE-1 asserts the gate line in the
  Space Center scene of the new save.

### R1-11 Join a race and start over in my seat

- **Change:** `ParsekRaceSetup.Join(RaceJoinRequest)`: reads `race.cfg` and the player
  folders through `IRaceSharedFolder`, runs the install checks, refuses a full race or a
  taken name, takes `FirstFreeSlot` and the chosen free look, writes the new
  `players/<p_id>/player.cfg` with `joinedRealTime`, creates the save from the manifest's
  `gameMode` and `DIFFICULTY` node (`GameParameters` from the node, S6) through the same
  P10-01 sequence as Create (`CreateGame`, its own before-start step, `StartGame`; no
  callback parameter); the `local.cfg` it writes carries `firstImportDone = false`
  (OV-27; R3E-18 sets it true). `StartOver(playerId)`: same, with the existing id, slot and
  look, and a fresh save name; it does not touch the shared folder's player files except
  `player.cfg`'s cosmetic fields. Seam verb `RaceSetup`, action `join` (OV-01), in the files
  R1-10 created: T-11's argument names `folder`, `name`, `look`, `as`, plus
  `startOverPlayerId` for start over (accepted departure, A3). First callers landing here
  (A1): `RaceSlots.StartOverSeat` (R1-09's file) and `RaceCore/RaceModDiff.cs` (the mod
  differences, listed as information).
- **Proof:** xUnit `RaceJoinPlanTests` (late join takes slot 1 then 2; the "no retakes"
  late-join note flag is set when the race has standings; full race; taken name; start
  over keeps the seat; mod differences listed; `firstImportDone = false` written);
  `RaceSlotsTests` gains the start-over cell, `RaceModDiffTests` the diff;
  `TestCommandRaceSetupTests` gains the join cells. Harness lane RACE-1 (T-13). Logs:
  `[Race] Join: raceId=.. playerId=.. slot=.. look=.. late=..`, `[Race] Join refused:
  reason=..`.
- **Registers:** the `join` rows in the seam contract and hlib; `log_tags.json`.
- **Size:** M. **Depends on:** R1-10. **PC:** yes, through RACE-1 (T-13).
- **Risk:** medium; two machines joining at once is only provable with two installs; the
  pure clash rules carry it here, and R3's import re-check finishes it.

### R1-12 Register the race formats with a golden folder

- **Change:** this task registers BOTH race rows, because R1-10 already writes `race.cfg`
  and `player.cfg` carrying `raceFormat` (OV-15, supervisor decision). `SaveFormat.cs`:
  rows `race-link` (baseline 1, current 1) and `race-exchange` (baseline 1, current 1),
  each with a literal constant; cells in `RaceFormatPinTests` (R1-03's class) pin them equal
  to `RaceFormat.LinkCurrent` and `RaceFormat.Current`. New frozen folder
  `Fixtures/FormatGolden/v2/`: what R1 writes, by a skipped generator test
  (`FormatGoldenV2GeneratorTests`, `PARSEK_WRITE_FORMAT_GOLDEN=v2`) that runs through T-01's
  `SaveWriterDriver` and the production writers: a race save (the fresh-career save plus a
  `ParsekRaceScenario` node holding the link), `race.cfg`, `own/player.cfg` and
  `Parsek/Race/local.cfg`; its `formats.txt` names both rows at 1 and `save=1`;
  `checksums.txt` extended. `FormatGoldenV2Tests` load it through the production readers.
  R3E-04 registers nothing and bumps nothing; it adds golden `v3` (a standing, its payload
  and an own mirror at `race-exchange = 1`) through the same driver.
- **Proof:** `SaveFormatTests`, `SaveFormatGoldenGateTests`, `FormatGoldenV2Tests`,
  `RaceFormatPinTests`.
- **Registers:** `Fixtures/FormatGolden/README.md` gains a `v2` section.
- **Size:** S. **Depends on:** R1-03, R1-10, T-01. Must land before R1-14. **PC:** no.
- **Risk:** low; the one trap is editing a frozen folder, which the gate refuses.

### R1-13 Harness lanes MP-1 and MP-2

Dropped: replaced by T-13 (OV-02).

### R1-14 Main menu entry, setup dialog shell and Continue

- **Change:**
  - Root file `Source/Parsek/ParsekRaceMenu.cs` (Controllers): `[KSPAddon(Startup.MainMenu,
    false)]`; clones a stock main-menu entry (S6) labelled Multiplayer after Scenarios and
    wires its tap to open the dialog; implements `IRaceSetupHost`.
  - `UI/RaceSetupDialog.cs`: a stock `PopupDialog` / `MultiOptionDialog` with three
    buttons (Create, Join, Continue) and the Continue page: one row per local race save
    (race name, own look, last sync). `IRaceSetupHost` is declared in the same file (the
    `IMergeFlowHost` precedent) so UI never names Controllers. Its members land with their
    first callers (A1): `Continue` here, `Create` with R1-15's page, `Join` and
    `StartOver` with R1-17's page, the folder-only mode with R1-18's Change button.
  - `RaceCore/RaceSaveListing.cs`: pure listing over directory facts (save folder name,
    parsed `local.cfg`, parsed local `race.cfg`); `RaceWorld/RaceSaveScanner.cs` reads
    `saves/*/Parsek/Race/` (the LOCAL directories only).
  - Continue loads through the load-and-start route (section 9, item 12):
    `ParsekRaceSetup.Continue(saveFolder)` calls the lifted route; its file joins
    `KnownLoadInitiators` if it holds the `LoadGame(` call.
  - `.claude/CLAUDE.md` "Hard rules": a fourth exception bullet naming the three owner
    exceptions (the main menu Multiplayer entry, the race setup dialog, the Space Race
    window; race saves only; owner rulings 2026-11-14, design section 0), then `cp
    .claude/CLAUDE.md AGENTS.md`. This task owns the edit (OV-42); R4-09 only checks that
    its window is named. `docs/dev/design-gui-inventory.md`: the entry and the dialog. A
    todo entry, no CHANGELOG entry while the entry is hidden (question 1, assumed (a)).
  - The developer switch of question 1 (assumed (a)): the entry is added only when the
    switch is on; it is off by default and stays off until R3's rivals and R4's board
    exist. Turning it on for players is a later change with its CHANGELOG entry.
- **Proof:** xUnit `RaceSaveListingTests`, `RaceSetupDialogModelTests` (pure page state);
  `AgentInstructionMirrorTests`; the menu entry (with the switch on) is a manual check on
  the owner's PC: no in-game category runs at the main menu, since every race category has
  one scene and one host lane (A2); RACE-1 (T-13)
  gains a "continue from the dialog" step through a `UiAction`-style seam only if one
  exists for stock dialogs; otherwise a manual check. Logs: `[Race] Menu entry added`,
  `[Race] Continue: save=.. raceId=..`.
- **Registers:** `CLAUDE.md` + `AGENTS.md` (and the umbrella copies by hand after merge, a
  docs change); `design-gui-inventory.md` (dialog count, screen messages);
  `ParsekUiScaleTests` only if the IMGUI fallback is used (a `"RaceMenu"` host in the
  `EachDrawHostOpensAScaleScope` list); `KnownLoadInitiators` and
  `LoadReconcileWiringGateTests` if a new file calls `LoadGame(`.
- **Size:** M. **Depends on:** R1-12, T-13, R1-16, S6. **PC:** yes.
- **Risk:** high; the first Parsek object at the main menu; a broken clone could break stock
  keyboard navigation, which is checked by hand on the owner's PC.

### R1-15 The Create page

- **Change:** `RaceSetupDialog` Create page: race name, game mode (three toggles), the stock
  difficulty panel (S6; fallback per question 2), the shared folder path field
  (`DialogGUITextInput`) with a Validate button and its result line, shared launch sites
  toggle (greyed with the reason when Making History is missing), optional end date, "no
  retakes", display name, look (four toggles), calendar line (question 3). The form state
  is a pure `RaceCore/RaceSetupForm.cs` (field values to a `RaceCreateRequest` plus the
  first refusal); Create calls `IRaceSetupHost.Create`, which calls `ParsekRaceSetup.Create`.
  Confirmations use the stock confirmation dialog (design section 0).
- **Proof:** xUnit `RaceSetupFormTests` (every field's refusal, the request built from a
  valid form); manual create on the owner's PC; RACE-1 (T-13) keeps proving the core.
  Adds `IRaceSetupHost.Create`, its first caller (A1).
- **Registers:** `design-gui-inventory.md` (the page); todo wording (no CHANGELOG while the
  entry is hidden).
- **Size:** M. **Depends on:** R1-14. **PC:** yes.
- **Risk:** medium; a text field in a stock dialog at the main menu is new to the mod (S6).

### R1-16 Lift the load-and-start route beside P10's core

- **Change:** move the seam's SPACECENTER load route (`ParsekTestCommandAddon.cs:2256`,
  `:2310-2320`, `:2380-2426`) into the production file P10-01 created, as
  `LoadAndStart(saveFolder)`; the seam verb calls it, logging unchanged.
  `KnownLoadInitiators` moves the `LoadGame(` count from `TestCommands/
  ParsekTestCommandAddon.cs` to that file.
- **Proof:** `LoadReconcileWiringGateTests`, `LoadReconcilePolicyTests`; every harness lane
  that loads a save through the seam (a flight tier on the owner's PC before merge).
- **Registers:** `KnownLoadInitiators`; the file-delete gate if a delete moves.
- **Size:** S. **Depends on:** P10-01. **PC:** yes (one tier run).
- **Risk:** medium; every harness load passes through this route.

### R1-17 The Join page and "start over in my seat"

- **Change:** `RaceSetupDialog` Join page: folder field and Validate; race summary (name,
  mode, end date, house rules, seats with look and name); display name field; look toggles
  with taken looks greyed; the site the player will get; the mod differences as
  information; the late-join "no retakes" note (one fixed sentence when the race's
  `noRetakes` is set; no list of rows, R6-09 is dropped); a "start over as" choice per existing
  player with the warning text of design 7.2, confirmed through the stock confirmation
  dialog. Calls `IRaceSetupHost.Join` / `StartOver`, which this task adds (their first
  caller, A1).
- **Proof:** xUnit `RaceJoinFormTests`; manual join on the owner's PC (a second install or
  a second save on one install, folder shared on disk).
- **Registers:** `design-gui-inventory.md`; todo wording (no CHANGELOG while the entry is
  hidden).
- **Size:** M. **Depends on:** R1-14. **PC:** yes.
- **Risk:** medium; the page shows other players' text, so names are length-capped and
  rendered as plain text.

### R1-18 The Multiplayer section in Settings

- **Change:** `UI/SettingsWindowUI.cs`: a Multiplayer section drawn only when
  `RaceGate.IsRaceSave` (no `UiSurface` key, design 15.14): the folder path shortened with
  the full path in the hover and a Change button that opens the dialog's folder field
  (`RaceSetupDialog` folder-only mode, added here with its first caller, A1); display name
  and look (any free one); a status line
  (folder reachable or the reason). The rules live in `RaceWorld/RacePlayerSettings.cs` over
  `RaceCore` checks: same race id accepted; empty folder accepted after the stock
  confirmation, writing `race.cfg` and `players/<id>/player.cfg` from the local copies
  (design 7.2 "move the race here"); another race or not writable refused. A name or look
  change rewrites `player.cfg` (shared) and `local.cfg`. R4-09 adds the Sync now button
  (calling `RequestSyncNow`) and the sync status text to this section (OV-40); rivals'
  times are R4.
- **Proof:** xUnit `RacePlayerSettingsTests` (the three folder cases, name clash, look
  taken); `TooltipEchoBudgetTests` (hover text within 71 characters); manual check in
  FLIGHT and SPACECENTER on the owner's PC. Logs: `[Race] Folder changed: from=.. to=..
  case=..`, `[Race] Look changed: ..`.
- **Registers:** `design-gui-inventory.md` (Settings section); todo (no CHANGELOG while the
  entry is hidden).
- **Size:** M. **Depends on:** R1-08, R1-09, R1-10, R1-14. **PC:** yes.
- **Risk:** medium; `SettingsWindowUI.cs` is shared by every Settings change, so this task
  keeps its additions in one draw method and one partial-free block.

## 5. Parallel tracks

| Track | Tasks, in order | Files it owns |
|-------|-----------------|---------------|
| A, arch | R1-01, then R1-06 (after R1-03) | `modules.toml`, `atlas.toml`, arch README, new `grep-audit-race-*` scripts, `GrepAuditTests.Race.cs`, `scripts/race-loader-files-allowlist.txt`, `test_archview.py` (the loader cell) |
| B, A7 | R1-02 (after P10-01); R1-07 (any time) | `ScenarioNodeLookup.cs`, the nine callers of section 3.3; `collect-logs.py`, `InGameTestRunner.cs`, `ParsekScenario.TestBatchReconcile.cs` |
| C, mark and gate | R1-03, R1-04, R1-05 | `RaceCore/RaceLink.cs`, `RaceFormat.cs`, `RaceScenarioChild.cs`, `RaceGate.cs`, `ParsekRaceScenario.cs`, `ParsekRaceBoot.cs`, `LoadReconcilePolicy.cs` |
| D, rules and setup | R1-08, R1-09, R1-10, R1-11, R1-12 | `RaceCore/RaceManifest.cs`, `RacePorts.cs`, `RaceTimestamp.cs`, `RaceIds.cs`, `RaceSlots.cs`, `RaceFolderCheck.cs`, `RaceCalendar.cs`, `RaceInstallCheck.cs`, `RaceWorld/*`, `ParsekRaceSetup.cs`, the `RaceCommand` and `RaceSetup` seam files, `InMemoryRaceFolder.cs`, `SaveFormat.cs`, golden v2 |
| E, load route | R1-16 (any time after P10-01) | P10-01's file, `ParsekTestCommandAddon.cs`, `LoadReconcilePolicy.cs` (initiators) |
| F, UI | R1-14, then R1-15, R1-17, R1-18 in parallel | `ParsekRaceMenu.cs`, `UI/RaceSetupDialog.cs`, `SettingsWindowUI.cs`, `CLAUDE.md`, `AGENTS.md`, `design-gui-inventory.md` |

Hot files that force an order:

- `scripts/arch/modules.toml`: R1-01, R1-02 (LogIO prefix), and P10-01 (LogIO prefix). Land
  R1-01 first or rebase; R1-02 lands after P10-01 (task index C).
- `LoadReconcilePolicy.cs`: R1-05 and R1-16. Independent rows; land in either order and
  rebase.
- `TestCommandVerbs.cs` and `harness/lib/hlib.py`: R1-08 (`RaceCommand action=status`),
  R1-10 (`RaceSetup action=create`), R1-11 (`action=join`), in that order; then T-12
  (`set-folder`) and T-19 (`sync`, `offline`, `online`) (OV-01, OV-48).
- `ParsekRaceBoot.cs`: R1-04 creates it; R2-01 adds the `RaceSites.Reconcile` call, R2-07
  the fallback tick if S5 asks for it.
- `RaceCore/RacePorts.cs`, `RaceManifest.cs`, `RaceSlots.cs`: later tasks of this plan
  (R1-09, R1-10, R1-11) and R3E-01 add members to them with their first callers (A1).
- `UI/RaceSetupDialog.cs`: R1-15 and R1-17 add pages to the shell of R1-14; each page is
  its own method, so both can be built at once and rebased.
- `SettingsWindowUI.cs`, `design-gui-inventory.md`, CHANGELOG, todo: shared with every other
  UI change in the repository; rebase at merge.
- `SaveFormat.cs` and `Fixtures/FormatGolden/checksums.txt`: R1-12 registers both race rows
  and writes `v2`; R3E-04 registers nothing and adds golden `v3` at `race-exchange = 1`
  (OV-15).

The earliest unblocking path is R1-01 and R1-02 at once, then R1-03, R1-04. Track B and
track E can start on the first day.

What other phases wait for (final ids; R3 per task, task index E.3.5):

| Phase | Waits for | Why |
|-------|-----------|-----|
| R2 (sites) | R2-01: R1-01, R1-03, R1-04; R2-02, R2-08: R1-10; R2-12: R1-10, R1-11 | modules; `siteSlot` and `siteTableId` in the link; the one handler `ParsekRaceBoot.OnSceneRequest`, where R2-01 adds the `RaceSites.Reconcile` call (every request, the main menu included) and R2-07 its fallback scene-ready tick, so R2 adds no second addon (OV-51, A4); the modified-Kerbin refusal slot R2-02 fills (OV-31); R2's in-game proof at slots 1 to 3 needs R1-11 |
| R3E (export, exchange) | R3E-01: R1-01, R1-03, R1-06, R1-07, R1-08, R1-10; R3E-04: R1-03, R1-08, R1-12; R3E-06: R1-04, R1-08; R3E-15: R1-03, R1-10; R3E-16: R1-08, R1-10; R3E-18: R1-08, R1-09 | modules and gates; `RaceFormat.Current` and the one pin class (OV-16); `IRaceFolder`, `RaceFolder`, `RaceTimestamp`, `RaceIds` to extend (OV-22, OV-24; no P7-02; R3E-01 adds the members R1 has no caller for, `List`, `Move`, `Exists`, `Delete` and the short id, A1); R1-10's path-budget constant, which R3E-01 replaces with the computed longest path; `RaceSession`; the `RaceLocal` keys it writes (OV-27); golden `v3` after `v2` (OV-15); the child hooks of the race scenario (OV-28); `IRaceSharedFolder` folded into `IRaceTransport` (OV-25); the clash rules re-run after import |
| R3W (rival world) | R3W-02, R3W-06: R1-01; R3W-08, R3W-13: R1-08; R3W-09: R1-10; R3W-27: R1-06 | modules; R3W-08 extends R1's `RaceSession` / `RaceRuntime` (OV-26); the SHOW block in `RaceLocal` (OV-27); the shared scanner and the loader allowlist file R3W-27 adds entries to (OV-12, OV-13) |
| R4, R6 (board, window) | R4-01: R1-01, R1-08, R1-10; R4-02: R1-01, R1-08, R1-10; R4-07: R1-08; R4-09: R1-04, R1-08, R1-14, R1-18; R4-10: R1-10 | modules; R4-01 extends R1-10's `TryAcceptDateRule` (OV-30); `RaceSession` (R4-07); the CLAUDE.md exception R1-14 owns (OV-42); the Settings section where R4-09 adds Sync now (OV-40); the SHOW setter on R1-08's codec key; facade members added on R1's `RaceRuntime` (OV-39) |
| R5 (looks) | R5-07: R1-01; R5-11: R1-03, R1-08, R1-09 | look assignment; `local.cfg`'s look; the child hook for `RACE_LOOK_DRESS` (OV-28). The starting crew is dressed at R5-11's first load pass, not in R1-10's before-start step (OV-54), so R5 does not wait for R1-10 or R1-11 |
| T&H | T-10: R1-04; T-12: R1-08; T-13: R1-10, R1-11; T-16: R1-10 | `RaceGate`; T-12 adds `set-folder` to R1-08's `RaceCommand`; RACE-1 drives R1's `RaceSetup` actions and carries the dropped R1-13's assertions (OV-02) |

## 6. Tests and proof

Unit (xUnit, headless, `Source/Parsek.Tests/`):

| Test class | Proves |
|------------|--------|
| `ScenarioNodeLookupTests` | the helper finds what each old copy found, in the same order |
| `RaceLinkCodecTests`, `RaceFormatPinTests` | the mark's shape, refusals, version classes, constants pinned to the C# names and to `SaveFormat` |
| `RaceScenarioChildTests` | unknown children of the race scenario node kept verbatim; a child with a handler loads and saves through it |
| `RaceGateTests`, `RaceInertTests` | the decision table; the left transition on any race to non-race change, switched on race A to race B; no mark in a single-player save means no race; R1 code adds nothing to a single-player save |
| `LoadReconcilePolicyTests` | the new row covers every load kind |
| `RaceManifestCodecTests`, `PlayerManifestCodecTests`, `RaceLocalCodecTests`, `RaceManifestWriteTests` | the exchange and local files read and write, invariant, with exact refusal reasons; the four optional `local.cfg` keys |
| `RaceTimestampTests`, `RaceIdsTests` | design 6.5 stamps; ids safe on Windows and Linux alike (the short id and its clash cells land with it in R3E-01) |
| `RaceSlotsTests` | design 7.3 and 7.4 assignment, simultaneous join, fifth folder, names, the `status` seat lines (R1-09); start over (R1-11) |
| `RaceFolderCheckTests`, `RaceCalendarTests`, `RaceInstallCheckTests` | every refusal of design 7.2; `date-unit-not-day` under R4-01's rule; the empty Kerbin slot; the 250-character budget |
| `RaceSetupPlanTests`, `RaceJoinPlanTests`, `RaceModDiffTests` | the files and save a create or join will write, before any I/O; the joiner's mod differences |
| `TestCommandRaceCommandTests`, `TestCommandRaceSetupTests` | the `status`, `create` and `join` actions parse with T-11 / T-12's argument names; every refusal |
| `RaceSaveListingTests`, `RaceSetupDialogModelTests`, `RaceSetupFormTests`, `RaceJoinFormTests`, `RacePlayerSettingsTests` | the dialog and Settings logic without drawing |
| `SaveFormatTests`, `SaveFormatGoldenGateTests`, `FormatGoldenV2Tests`, `RaceFormatPinTests` (R1-12's cells) | the two rows at 1, pinned to `RaceFormat.LinkCurrent` and `RaceFormat.Current`, and the frozen `v2` |
| `GrepAuditTests.Race.cs` | the shared `RaceSourceScan`; no store in `RaceCore`, the one no-writer gate (with the save-lifetime writers cell), the one recorder ban: each with anti-vacuity, planted-red and mirror cells; the race log tag list |
| arch suite | 71 edges declared; `RaceCore` headless with no leak; the loader-only cell reading `scripts/race-loader-files-allowlist.txt` |

Every class touching `ParsekLog` or a static uses `[Collection("Sequential")]` and captures
the log with the test sink (`RewindLoggingTests` pattern).

`InMemoryRaceFolder` (`Generators/`, R1-08) is the one in-memory `IRaceFolder`; later race
tests reuse it (OV-17). Harness: source-sync cells for `RaceCommand`'s `KnownActions` (R1-08)
and `RaceSetupSourceSyncTests` (R1-10).

In-game (category `RaceMode`, SPACECENTER, hosted only by T-29's RACE-9 (A2, OV-09); until
that lane pins it no committed spec pins it, so `CommittedBatchTallySourceSyncTests` stays
green):

- `MarkSurvivesSaveLoad` (R1-03), gate on and off across scenes (R1-04). R1 has no other
  in-game category: the menu entry (R1-14) is a manual check, since no race category runs at
  the main menu (A2).

Harness lanes (owner's PC): R1 owns none. T-13's RACE-1 (folder `${raceShared}` =
`<instance>/rs/<n>`, T-03) carries the dropped R1-13's assertions (OV-02): create, gate on in
the new save's Space Center, gate cleared at the main menu, a single-player fixture reads no
race mark (`reason=no-mark`), reloading the race save keeps the race (same `raceId`), and a
late join gets slot 1 and the first free look. One flight tier for R1-16.

Manual checks on the owner's PC: the cloned menu entry and keyboard navigation (R1-14); the
Create and Join pages including the difficulty panel (R1-15, R1-17); Settings in FLIGHT and
SPACECENTER (R1-18); a race save opened from the stock Resume list behaves like Continue.

What needs the owner's PC: R1-03, R1-04 (in-game tests), R1-10, R1-11 (through RACE-1),
R1-14 to R1-18.

## 7. Gates and registrations

- **Architecture map:** three folder rules, layers (`RaceCore` model; `RaceExport`,
  `RaceWorld` features), `RaceCore` in `[headless.modules]`, `RaceWorld` in
  `[size].runtimeCoupled`, 71 `[forbidden]` edges (R1-01); `^ScenarioNode` in the LogIO
  prefix (R1-02); root files `ParsekRaceScenario.cs`, `ParsekRaceBoot.cs`,
  `ParsekRaceSetup.cs`, `ParsekRaceMenu.cs` placed by P12-02's Controllers rule. Atlas
  summaries. Knot canary and layer pins: re-pin in the change that moves them, with the
  reason in the PR (none expected: race modules sit above the knot and are named only from
  entry and UI).
- **Headless pins:** `RaceCore` should carry no leak; every live read is in `RaceWorld` or
  Controllers. `headless_pins.toml` re-pinned when `RaceCore` first has files (R1-03).
- **Uncalled-method ratchet (A1):** no task adds a production member without a production
  or tooling caller in the same pull request: R1-02 (the replaced copies), R1-03 (stock
  calls `OnLoad` / `OnSave`), R1-04 (the boot addon's `OnSceneRequest`), R1-08 (the session
  and `RaceCommand action=status`), R1-09 (`status`'s seat lines), R1-10 and R1-11
  (`RaceSetup action=create`, `action=join`), R1-14 to R1-18 (the dialog and Settings). A
  member another task first calls lands there as an edit to this plan's file: writers,
  `RaceLinkCodec.Write`, `RaceTimestamp.Format`, id generation and `IRaceFolder.SafeWrite`
  with R1-10; `StartOverSeat` and `RaceModDiff` with R1-11; each `IRaceSetupHost` member
  with its page; the short id and `IRaceFolder.List` / `Move` / `Exists` / `Delete` with
  R3E-01; the typed `SHOW` reader and setter and every `RaceRuntime` facade member with their
  first callers in R3 to R6 (OV-39). R1 adds no empty hook: R2-01 adds the
  `RaceSites.Reconcile` call and R2-07 the scene-ready tick to `ParsekRaceBoot`.
- **Cross-type static writes:** `RaceGate` and `RaceSession` write their statics only
  through their own methods; `RaceRuntime.Current` forwards and has no static (OV-26).
- **Static lifetimes:** `RaceGate` decision `process`; `RaceSession.Current` `scene`;
  `ParsekRaceScenario` keeps instance state only, so the Cold roots do not change (design
  15.14).
- **Grep audits:** the race gates on one shared scanner (R1-06): no store in `RaceCore`,
  the one no-writer gate, the one recorder ban, the race log tag list; the loader rule is
  the arch cell reading `scripts/race-loader-files-allowlist.txt`; the file-delete gate
  unchanged (Create refuses an existing folder); the background-thread gate unchanged (no
  worker in R1).
- **Save format:** `race-link` and `race-exchange` rows, both at 1, with golden `v2`
  (R1-12); R3E-04 adds `v3` without a bump (OV-15); `local.cfg` is a machine file with no
  row (like `settings.cfg`), additive keys only.
- **Load policy:** `LoadStateCategory.RaceLink` (R1-05); each child of the race scenario
  node gets its own row in the task that adds it (R3E-15, R5-11; OV-28);
  `KnownLoadInitiators` follows R1-16 and R1-14.
- **Seam:** `RaceCommand action=status` (R1-08), `RaceSetup action=create` (R1-10) and
  `action=join` (R1-11) in `TestCommandVerbs.ImplementedVerbs`, the hlib tables, their
  source-sync cells and `design-autotest-command-seam.md`; T-12 and T-19 add the other
  `RaceCommand` actions.
- **GUI:** `design-gui-inventory.md` (entry, dialog, Settings section); `ParsekUiScaleTests`
  only for an IMGUI fallback; no `UiSurface` key; tooltip budget for the Settings hover;
  `GuiCensusSeamVerbTests` vocabularies unchanged (no new Parsek window unless a fallback
  is used, then P11-01).
- **Docs:** `.claude/CLAUDE.md` + `AGENTS.md` (R1-14: the UI exceptions; R1-07: the
  collect-logs line); todo entries per task, no CHANGELOG while the Multiplayer entry is
  hidden (question 1, assumed (a)); FormatGolden README (R1-12); arch README (R1-01,
  R1-06).

## 8. Risks

- **The mark is not on disk when the Space Center reads it.** Seen as a missing `Gate: race
  save` line in RACE-1 (T-13). Answer: the before-start step saves `persistent.sfs` after
  adding the proto (R1-10).
- **Stock drops the `None` scenario's proto or never instantiates it.** Seen in R1-03's
  in-game test and in S6. Answer: another creation option, or registering for the race
  saves' modes with an `OnLoad` that does nothing without a node.
- **A stale gate for one scene on some load path.** Seen in R1-04's in-game tests per load
  path; S4a answers it first (OV-51). Answer: evaluate also on `onGameStatePostLoad`.
- **Main menu clone breaks stock navigation.** Seen by hand on the owner's PC. Answer: the
  IMGUI fallback button under its own UI-scale host.
- **Freeze on a cloud placeholder or offline share during Validate or Join.** Seen by S8's
  numbers. Answer: the port of section 9 item 9; R3E-16 replaces it with `IRaceTransport`
  and R3 swaps in the worker if the owner allows it.
- **A single-player regression from A7.** Seen in the existing suites of the replaced
  methods and in every harness lane. Answer: R1-02 keeps each caller's search order and log
  text.
- **The uncalled bound rises.** Seen in CI's `test_ilanalysis`. Answer: the task order in
  section 7.
- **Path budget too tight for long user paths.** Seen as a Validate refusal. Answer: the
  refusal names the longest path and its length.

## 9. Corrections and gaps

1. **Seven copies.** The inventory (line 142) and design A7 say seven copies of "find the
   scenario node". Measured: six over a proto list and three over a GAME node in production,
   plus `CareerSaveParser` in tooling (section 3.3). The plan replaces nine and keeps the
   tooling copy.
2. **"File collector".** `ParsekFileCollector` never copies `Parsek/Race/`
   (`ParsekFileCollector.cs:203,235,236`). The sweeps that do are `collect-logs.py`, the
   test-batch reconcile, and the in-game runner's batch snapshot and restore
   (`InGameTestRunner.cs:1965-1995`, `:2579`), which A7 does not name. R1-07 covers all three.
3. **Before-start step.** The design does not say when the mark is written for a new save.
   `SpaceCenterMain` re-reads `persistent.sfs` (`ParsekTestCommandAddon.cs:2380-2426`), so the
   proto must be added and the save written before `Game.Start`. P10's core must offer that
   step; the plan assumes it does. Settled: the overlaps audit found that P10-01's three
   steps leave room for it, and it stays for other uses now that the starting crew is
   dressed at R5-11's first load pass (OV-54).
4. **The mark has no version.** Design 6.1 gives `RaceLink` no version key and 15.7 registers
   only `raceFormat`. The plan adds `raceLinkFormat` and a `race-link` row. Settled: the
   constants are `RaceFormat.LinkCurrent` and `RaceFormat.Current` (OV-16).
5. **Golden folder for a non-save format.** A new `SaveFormat` row needs a frozen folder
   covering it (`SaveFormatGoldenGateTests.cs:42-70`); the folders are saves. The plan
   writes one `v2` race save holding the mark and the local files. Settled: R1-12 registers
   both rows and writes `v2`; R3E-04 adds `v3` without a bump (OV-15, supervisor decision).
6. **`RaceLocal` is short.** Continue shows "last sync" and Settings rebuilds the shared files
   "from its local copies" (7.2), but `RaceLocal` has no last-sync key and no local copy of
   `race.cfg` or the own `player.cfg` is listed. The plan adds `lastSyncRealTime` (written by
   R3), `Race/race.cfg` and `Race/own/player.cfg`. Settled: R1-08's codec carries
   `lastSyncRealTime`, `lastStandingDigest`, `firstImportDone` and `SHOW` as optional keys;
   R3E-17, R3E-04, R1-11 / R3E-18 and R4-10 write them (OV-27).
7. **Calendar.** 7.2's Create list has no calendar, yet the manifest carries one (6.6). The
   plan takes the creator's stock setting; question 3.
8. **Who creates a save.** 15.4 has UI call the `RaceWorld` facade for create and join.
   Creating a save needs P10's core and `GamePersistence`, which are entry-layer work; a
   `RaceWorld` (features) call into Controllers is a backward edge. The plan puts the setup
   in `ParsekRaceSetup` (Controllers) behind an interface declared in UI. Settled: R1's
   reading (design-corrections DC-102).
9. **Setup touches the shared folder from a button.** 15.9 says the game never touches the
   shared folder from a Unity callback, but Validate, Create, Join and the Settings folder
   change must, and R1 has no copy worker. The plan uses a port with 15.9's main-thread
   fallback adapter; R3 replaces the adapter. Settled: R3E-16 folds `IRaceSharedFolder`
   into `IRaceTransport` and moves the adapter and `RaceFolderCheck` onto it (OV-25).
10. **UI-scale hosts.** 15.14 says the menu entry and the dialog need their own UI-scale
    host. Built from stock uGUI (a cloned button, `PopupDialog`) they follow the stock scale
    and need none; a host is needed only for the IMGUI fallbacks.
11. **Local race directory path.** P7 adds a save-root accessor on `RecordingPaths` for the
    local directory, but 15.4 limits `Recording` names inside `RaceWorld` to loader files.
    The plan computes the path in `RaceWorld` from `KSPUtil.ApplicationRootPath` and
    `HighLogic.SaveFolder`. Settled: R1's reading; R3E-01 uses R1-08's `RaceFolder` and not
    P7-02 (OV-23, supervisor order fixes).
12. **Continue needs a load route.** 15.14 says Continue is a load through a declared
    initiator, but the only production-quality load-and-start sequence is in the seam. The
    plan lifts it beside P10's core (R1-16) rather than writing a second copy.
13. **"Recording types only in the loader" is not a grep.** A grep cannot know which module
    a type belongs to; the plan makes it an arch-suite cell over the text scan's map.
    Settled: R1-06's arch cell owns the rule and reads the allowlist file
    `scripts/race-loader-files-allowlist.txt`; T-09 and R3W-27's second script are dropped
    (OV-12).
14. **`RaceWorld` is runtime-coupled** (15.2) but 15.14 does not list `[size].runtimeCoupled`;
    R1-01 adds it.
15. **The gate and inert modes.** The design does not say how the race gate composes with
    `ParsekGameModeGate`, which sits in `Patches` and cannot be named from `RaceCore` or
    `RaceWorld`. The host in Controllers passes the inert flag in; an inert Parsek means no
    race, with the mark kept verbatim.
16. **Path budget.** 6.3 fixes the local cache layout but not the shared folder's layout
    under `players/<id>/`. Settled: R1-10 creates the path-length check with a conservative
    constant in an R1 file; R3E-01 (lands later) replaces the constant with the computed
    longest expected path and updates R1-10's call site.

## 10. Questions for the owner

1. **When does the player see Multiplayer?** After R1 a race save plays as single player:
   no rivals, no board. Options: (a) the menu entry stays hidden behind a developer switch
   until R3's rivals and R4's board land; (b) it is visible from R1. Recommendation: (a), so
   no player creates a race that shows nothing yet.
   Assumed until the owner says otherwise: (a), the entry hidden behind a developer switch
   until R3 and R4 land (supervisor decision); R1-14 builds the switch.
2. **If the stock difficulty panel cannot be opened from the Multiplayer dialog** (S6),
   what does the creator get? Options: (a) the four stock presets (Easy, Normal, Moderate,
   Hard) only, with custom settings later; (b) wait for a custom panel before Create ships.
   Recommendation: (a); the manifest stores the full settings either way.
3. **Who chooses the length of a race day?** Options: (a) the creator's own stock calendar
   setting (Kerbin or Earth time) is used and shown in the dialog; (b) the dialog has a
   Kerbin day / Earth day choice. Recommendation: (a); it is one less field and the
   creator already chose it in stock settings.
