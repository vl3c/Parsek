# Parsek GUI State Gallery - Design Document

*Design specification for photographing every enumerated GUI state by driving the real IMGUI draw code over MOCKED data, in one boot, so a GUI change can be iterated against before/after renders in the browser.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies how the GUI census gets from ~166 photographed states to ~450, and the owner-facing iteration loop built on top of that.*

**Status:** DESIGN - nothing implemented. This PR is docs-only.
**Out of scope:** the mirror page itself (`docs/dev/design-gui-mirror.md` owns it; this doc only specifies what the gallery adds to it), the dump format (`docs/dev/design-gui-tree-dump.md`), the measured window inventory (`docs/dev/design-gui-inventory.md`), the existing seam op vocabulary (`docs/dev/design-autotest-command-seam.md`), hover / tooltip capture (refuted instrument, `TestCommandUiPointer.cs:92-102`), and `harness/tools/gui_mirror_fidelity.py` (assumed to exist; this doc names where it plugs into the loop).
**Related docs:** `design-gui-mirror.md`, `design-gui-tree-dump.md`, `design-gui-inventory.md`, `design-gui-kerbals-window.md`, `design-autotest-command-seam.md`, `design-autotest-harness-core.md`, `autotest-status.md`.

---

## 1. Introduction

### 1.1 The problem

The census is honest and incomplete. A harness flight drives the real game through the M-A2 command seam, and for each state saves a PNG plus a Harmony-recorded IMGUI control tree, so every rect and every string on the mirror was drawn by the build under test. What it cannot do is reach a state that no fixture save plus op sequence reaches.

The 2026-09-21 coverage audit enumerated **~480 visibly distinct states** across 14 windows, 21 dialogs and 7 overlays, of which **~166 are captured**. The gap is not spread evenly. It is the product's entire failure surface:

| Family | States | Captured | Why flying there is expensive or impossible |
| --- | --- | --- | --- |
| Logistics hold clauses | 17 | 0 | each needs a route that failed for one specific reason at one specific cycle |
| Logistics reject reasons | 12 | 0 | needs a career whose trees are ineligible in twelve different ways |
| Logistics route statuses | 9 | 3 | `EndpointLost`, `DestinationFull`, `WaitingForFunds`, `SourceChanged` are multi-hour careers |
| Career divergence banner | 1 | 0 | needs a rewind that moves committed actions into the future |
| Kerbals `Lost` / `Retired` and the stand-in chain forms | ~6 | 0 in the current design | needs a fatal flight, or a displaced stand-in after its owner returns |
| Timeline supersede, rewind-armed, live Re-Fly | ~6 | 0 | needs a live Re-Fly session mid-flight |
| Test Runner failed row, running state | 2 | 0 | needs a deliberately failing test and a non-blocking run |
| Structure terminal statuses | 10 | 4 | one fixture per ending |
| Marker skip buckets | 11 | 3 | 8 have never fired program-wide in any collected log |

A reviewer judging the UI from the mirror today sees a product that never fails.

Two facts bound any answer. Fixtures are expensive (each is a flight, a harvest and a committed save). And roughly 60 of the missing strings are hover text, refuted at the strongest available setting - Unity only updates `Input.mousePosition` while receiving input, and four pointer steps with a stolen foreground still read `tooltip=-` (`TestCommandUiPointer.cs:92-102`). Hover is a separate problem and is excluded here.

### 1.2 What the owner asked for

> "have an accurate mirror of all GUI states (mocked) so that we can iterate and improve the GUI in an automatic way, with my input given after seeing comparison renders in the browser"

So the deliverable is not "more captures". It is a **loop**: edit draw code, run a gallery, regenerate the mirror, read before/after in a browser, comment per state, iterate. Coverage is the precondition; the loop is the product. Section 12 is the loop.

### 1.3 The non-negotiable, restated as a rule the design must obey

**The mirror never invents pixels or layout. Whatever produces the rects and the text must be the real IMGUI draw code of the build under test. Mocking is allowed at the DATA level only.**

The sharp consequence: a mock must enter the system UPSTREAM of the draw call and DOWNSTREAM of the game state. Nothing may fabricate a rect, a style, a colour or a string that the draw code did not compute - which rules out a hand-written HTML re-implementation and rules out any stub GUI layer whose text measurement is not KSP's own font (section 5.C).

### 1.4 What the player sees

Nothing. Every surface here is automation-only, armed by `PARSEK_TEST_COMMANDS=1` and inert when unset (`InGameTests/AutorunHooks.cs`), and the gallery runs on the provisioned harness instance. No new window, no badge, no tooltip, no screen message for a standing condition.

---

## 2. Design Philosophy

1. **The draw code is the only renderer.** A state is photographed by making the real window draw it. Every option is judged first on whether the pixels came from `LogisticsWindowUI.DrawRouteRow` and its siblings, and only then on cost.
2. **Mock the model, never the game.** A mock is a view model handed to a draw path. It never writes `RecordingStore`, `Ledger`, `MissionStore`, `RouteStore`, a save file or a sidecar. The blast radius of a bug in this feature is one screenshot, never one career.
3. **A mocked capture is labelled as mocked in the artifact, forever.** The owner must never have to remember which half of the mirror is real. Provenance travels inside the dump, not in a filename convention a person can copy by hand.
4. **The catalogue compiles.** Synthetic states are declared in C# against the real model types, beside the presentation code, so a renamed field or a new enum member is a build error rather than a silently skipped state.
5. **Completeness is mechanical.** "Every reason string has a state" is proven by a test that runs the catalogue through the real pure helpers and diffs the produced strings against the source's own literals. It is never a list a person maintains.
6. **Restore is unconditional and structural.** A mock lives only in UI-layer fields, so no save can capture it even if restore fails; restore then runs anyway on success, refusal, exception, scene change and quit.
7. **One boot, many states.** Measured, the unit of cost is the boot (45-65 s), not the capture (0.1 s screenshot + 0.09 s dump). The design therefore optimises for iterating a long catalogue inside a single flight, not for adding lanes.

---

## 3. Terminology

| Term | Definition |
| --- | --- |
| State | One visibly distinct draw of one window, as the coverage audit enumerates it. The unit of coverage. |
| State id | The stable key of a catalogue state: `<window>.<family>.<variant>`, e.g. `logistics.hold.escrow-short`. It becomes the capture label and the mirror's state token. |
| Catalogue | The compiled set of synthetic view models, one per state id, declared in C# beside the presentation types and shipped in `Parsek.dll` because the in-game applier needs it. |
| Gallery lane | A harness scenario that boots once and walks the catalogue, taking a capture pair per state. |
| Mock scope | The interval between applying a mock and restoring it. At most one is live at a time, for one window. |
| Real-save capture | Today's census capture: a fixture save plus seam ops. Unchanged by this design. |
| Mocked capture | A capture taken inside a mock scope. Carries a `mock` block in its dump. |
| Injection point | The one member on a window whose value the applier replaces so a synthetic model drives that window. |
| Live cell | A cell whose text the draw method reads from a live static rather than from the model, so a mock does not control it. Enumerated per window in section 6. |

---

## 4. Scenarios

The design is answerable only against concrete cases. These five are the acceptance set.

**S1. The reason string nobody has seen.** `logistics.hold.escrow-short` replaces a green delivery cell with `Held: escrow short by 1,240 funds ...`, truncated to 60 characters by `LogisticsHoldPresentation.TruncateForCell` and re-tinted yellow. Today: 0 of 17 hold clauses photographed. Under the gallery: one catalogue entry, one capture, and the truncation is measured off the real `GUI.Label` rect rather than argued about.

**S2. The window whose whole point has no picture.** `career.banner.divergent` draws `Career mode - UT 414 (timeline ends at UT 9310)` plus the split `Active now (N)` / `Pending in timeline (K)` layout. Reachable today only by rewinding a dense career.

**S3. A layout change the owner wants to judge.** The owner asks for wider Rewards cells in Milestones. The loop must show him the same 34 Career states before and after, side by side, with the node counts and worst header-to-cell dx measured off both dumps.

**S4. The state that must stay real.** The Missions window's row identity is an index into the raw committed list by explicit design (`RecordingsTableUI.cs:1749-1755`). Mocking that store in memory is exactly what philosophy 2 forbids. So the design must reach those states another way, or say plainly that it does not.

**S5. A mock that must not survive.** A gallery run is killed mid-state by a KSP crash. The next boot must find no mocked data anywhere, and no save may contain any. This has to be true by construction, not by a finally block.

---

## 5. Options considered

### 5.A In-game state gallery seam (mock the view model) - RECOMMENDED

An automation-only op swaps a window's view model or row source for a catalogue entry, the applier waits for one drawn frame, the lane captures the pair, and the op restores.

**Evidence that this fits the existing seam rather than inventing a new mechanism.** `UiAction` already has sixteen ops (`open close tab complexity rect describe pointer find expand target picker dialog playback raise dismiss run`, `TestCommandUiAction.cs:596-604`), three of which MUTATE UI state for the duration of a capture (`op=expand`, `op=playback state=`, and `op=raise`, which deliberately leaves a modal standing to be photographed). It already has a two-phase settle contract - initiate, then confirm after one drawn frame (`ParsekTestCommandAddon.UiAction.cs:385-395`, `SetExecResult(PendingVerdict, null, null)`). It already refuses an op when the game forbids it (`complexity-refused-gloops-recording`, `TestCommandUiAction.cs:339`). It already has a pure/applier split with source-gate tests over the applier's own tables (`GuiCensusApplierSourceGateTests`, which derives the applier's window tables from its comment-stripped source). A mock op is the same shape with a different payload.

**Evidence that the windows are injectable.** Two windows are settable TODAY with zero production change: `KerbalsWindowUI.CachedViewModelForTesting` (`UI/KerbalsWindowUI.cs:132-136`) and `CareerStateWindowUI.CachedVMForTesting` (`UI/CareerStateWindowUI.cs:209-213`). Career State is the reference case: everything from the VM read at `:1384` downward is a pure function of `vm` - a grep for `RecordingStore|EffectiveState|Ledger|FlightGlobals|HighLogic|Planetarium|ContractSystem|StrategySystem` over `:1384-2072` returns nothing. Kerbals is the same: over the whole draw path `:595-916` the only non-VM reads are chrome (`parentUI.GetTableRowStyle()`, `GUI.skin.label`). And all seventeen Logistics presentation helpers are pure and Unity-free, so the strings a mock produces are produced by the same code the game runs.

**Cost.** Entirely per-window, and it depends on how much of each window's pixels come from a model versus from live statics read inline inside the draw method. Section 6 is that audit, window by window, with the live cells named.

### 5.B Synthetic SAVE fixtures - RECOMMENDED as the narrow complement

Extend `Source/Parsek.Tests/Generators/` (RecordingBuilder, VesselSnapshotBuilder, ScenarioWriter) and the injector presets so a generated save already contains recordings, trees, missions, routes, ledger rows and kerbal statuses in the wanted state.

**What B reaches that A cannot.** Exactly the windows whose row model does not exist and whose injection point is therefore the store itself:

* The **Missions window** rebuilds `missionViewCache`, `compositionCache`, `vesselRowsCache`, `foreignLinksCache`, `journeyLegsCache`, `dockPartnerTextCache` and `summaryFactsCache` on the first lookup of **every new frame**, keyed on `Time.frameCount` (`UI/MissionsWindowUI.cs:1137-1148`). No mock can live in any of those caches by construction. The derived views are built from a live `RecordingTree` inside `GetMissionView` (`:1135-1181`).
* The **Recordings tab** has no row model at all: the draw iterates live `Recording` objects by index into `RecordingStore.CommittedRecordings` (`UI/RecordingsTableUI.cs:1756`), with an `[ERS-exempt]` comment at `:1749-1755` naming that as deliberate and a `TODO(phase 6+)` to move to id-keyed rows. Around twenty store predicates are then called per row (`CanFastForward` `:2969/:3484/:3586/:4489`, `CanRewind` `:2986/:3514/:3601/:4506`, `GetRewindRecording` `:3543/:4046/:4718`, `EffectiveState.IsRewindRetired` `:4085/:6496`), several of which walk the store rather than just the row.

For these two, the only cheap injection point is the store - which philosophy 2 forbids in memory. A **save** the generators authored is the legitimate form of the same mock: it is data, it is reproducible, it is committed, and the game loads it through its ordinary path. So B owns the store-shaped windows and A owns the model-shaped ones.

**What A reaches that B cannot.** Anything with no representation on disk: a Test Runner mid-run, a failing test row, a Logistics hold computed from a live capacity probe, a candidate cost, a Spawn Control row inside the warp radius, a Career VM in divergence without actually rewinding. Roughly two thirds of the enumerated gap.

**What the generators can and cannot author today.** This decides how much of B is buildable. `ScenarioWriter.BuildScenarioNode` (`Source/Parsek.Tests/Generators/ScenarioWriter.cs:287-347`) is exhaustive, and `InjectIntoSave` (`:375-389`) string-inserts that one node before `\tFLIGHTSTATE`, so it structurally cannot reach `GAME/PARAMETERS` or `GAME/ROSTER` or any other `SCENARIO`:

| Save surface | Authorable today | Evidence |
| --- | --- | --- |
| `RECORDING_TREE` / recordings / sidecars | **yes**, richly (~45 fluent mutators) | `RecordingBuilder.cs:753/:787`, `ScenarioWriter.cs:294`, sidecars `:521-609` |
| `REWIND_POINTS` + the RP quicksave `.sfs` | **yes**, production-shaped | `ScenarioWriter.cs:341-343`, `:633` -> `BuildRewindPointQuicksave:671` |
| `CREW_REPLACEMENTS` (legacy) | yes, but it is a MIGRATION source only on load | `:298-303`; `KerbalsModule.LoadSlots:2367-2405` |
| `GROUP_HIERARCHY` | yes - parenting only | `:326-331` |
| `MILESTONE_STATE`, `milestoneEpoch`, GameState events | yes | `:314-322`, `:558-587` |
| `KERBAL_SLOTS` (the current kerbal format) | **no** - zero hits in the generators | production writer `KerbalsModule.cs:2282-2308` |
| `HIDDEN_GROUPS` / group visibility | **no** - only the hierarchy half is written | production `GroupHierarchyStore.cs:432-448` |
| `MISSION` entries (archived, renamed, link selections) | **no** - defaults are derived on load instead | production `MissionStore.cs:900-916`; defaults `ParsekScenario.cs:4237` |
| `ROUTES` / `DORMANT_ROUTES` (committed routes) | **no** - `RouteFixtureBuilder` returns an in-memory `Route`, never a save | production `Logistics/RouteStore.cs:22-27`; builder `RouteFixtureBuilder.cs:262-321` |
| Ledger rows / career `GameAction` | **no** - not a `.sfs` node at all; external `Parsek/GameState/ledger.pgld` | `RecordingPaths.cs:115-118`, `GameActions/LedgerOrchestrator.cs:3852-3855` |
| `RECORDING_SUPERSEDES`, `LEDGER_TOMBSTONES`, rewind retirements, session markers | **no** - the codecs ship, the writer has no entry point | `RecordingSupersedeRelation.cs:40-44`, `GameActions/LedgerTombstone.cs:44-49` |
| `ParsekSettings` | **no, structurally** - a `GameParameters.CustomParameterNode` under the save's own `PARAMETERS`, outside the splice point | `ParsekSettings.cs:17/:46-54` |

Five of those six absences are a mechanical `ScenarioWriter` extension over a codec that already ships (`MISSION`, `ROUTE`, `KERBAL_SLOTS`, `HIDDEN_GROUPS`, supersede and tombstone `ENTRY` rows each have a `Save`/`SaveInto` in production). **Settings are the one structural absence, and the live seam already owns them** (`SetSetting` + `SettingWhitelist`), which is itself an argument for A.

**The standing caution on B.** `SAVE-AUTHORED-PROGRESS-NODE-DOES-NOT-RESTORE` (`docs/dev/todo-and-known-bugs.md:11643`, measured 2026-08-20 on run `2026-08-20_2217`): a `Progress` node spliced into a *file-constructed* career save was never `Load`ed, which silently retired the spliced contract and cost a 463 s flight that otherwise read green. The same splice onto `career-earned-pad`, which is **derived from a real KSP-written save**, loads all nine contracts. So the rule for any B work is: **splice onto a harvested save, never construct a stock scenario surface from scratch.** There are 59 committed save fixtures to splice onto.

**Cost per state.** B is the more expensive per state: a generator surface plus a preset plus a save on disk, and every state in the same save competes for the same window (two rows cannot both be the only row). A is one C# builder method per state.

**Determinism.** B is stronger: a committed fixture is bytes. A is deterministic too but depends on pinning the caches (section 7.4) and on the live cells of section 6 being genuinely inert in the capture scene.

### 5.C Headless IMGUI - REFUTED, plainly

Running the draw code outside KSP is not achievable at layout fidelity. Four independent blocks, each measured rather than argued:

1. **The GUI entry points refuse to run outside a GUI pass, and the depth flag cannot even be read headlessly.** `GUIUtility.guiDepth` is an `[MethodImpl(InternalCall)]` extern; `GUIUtility.CheckOnGUI` throws "You can only call GUI functions from inside OnGUI" when it is `<= 0`. The project already documents that `Delegate.CreateDelegate` over that ECall is refused in the xUnit host ("ECall methods must be packaged into a system module") and that a headless host makes `ReadGuiDepth` return `GuiDepthUnavailable` (`design-gui-tree-dump.md:80-95`). So a headless host is not merely unblessed, it is the case the recorder has a documented fallback for.
2. **No layout without a real `GUISkin`.** Every rect in a Parsek window is computed by `GUILayout`'s two-pass layout against styles from `GUI.skin` / `HighLogic.UISkin`, which are Unity assets loaded from KSP's resources. A hand-built `GUIStyle` is not the shipped skin, and the mod's own tests never construct one: they reason about `GUI.skin` metrics as constants and pin source text instead (`TableRowInsetAlignmentTests.cs:197-269`, `CareerStateWindowUITests.cs:1557`).
3. **No text measurement without the native font.** Widths come from `GUIStyle.CalcSize` through Unity's native `TextGenerator` and the font KSP loaded. The mirror already proves how sensitive this is: it had to calibrate to Arial 13px against measured ink extents of 136 / 144 / 87 px, because "a font that is 8% wide overflows cells the game fits" (`design-gui-mirror.md:49-58`). A stub layer would be exactly that error, at every cell, with no photo to catch it.
4. **Unity batch mode is not the same renderer either.** Even `-batchmode` inside Unity would need KSP's skin, fonts, screen metrics and `ClickThruBlocker` (the mod's window host is `ClickThruBlocker.GUILayoutWindow`), i.e. it would need KSP - at which point it is the harness instance with extra steps.

Verdict: **C is refused.** A stub GUI layer that records "the same tree" would produce a tree whose rects are the stub's arithmetic, not the game's, and the one thing this design may not do is put invented geometry on the mirror. What stays valuable headlessly is what is already there: the pure presentation helpers are unit-testable (all seventeen Logistics helpers are Unity-free), and section 11 uses exactly that to make the catalogue's completeness mechanical.

### 5.D Better ideas found in the code

Three, all adopted into the recommendation:

**D1. A batch-owner verb, not 900 spec steps.** Driving ~300 states from the TOML at 3 steps each is ~900 steps and about 6 minutes of pure harness channel latency. The seam already has the batch-owner shape for exactly this: `RunTests` owns an in-game batch and emits one `BATCH_COMPLETE v1 total=N passed=... skipped=S` line the spec pins (`TestCommandRunTests.cs:34-48`). Measured latency per seam op is ~0.24 s (`uiaction complexity` 18:54:30.527 -> `open` :30.768 -> `open` :30.788 -> `rect` :31.025 in the GUI-5 log), against 0.086 s for a whole `DumpGuiTree` (recv :31.779 -> ok :31.865) and `elapsed=0.1s` for a screenshot. So a `GalleryRun` verb that loops in-game removes roughly 60 percent of the wall time and shrinks the spec to about twenty steps.

**D2. Reuse `WindowRectForTesting`, which every window already has.** `StructureListWindowUI.cs:36`, `SpawnControlUI.cs:27`, `TestRunnerUI.cs:27`, `SettingsWindowUI.cs:253`, `TestRunnerShortcut.cs:148`, `KerbalsWindowUI.cs:45`, `CareerStateWindowUI.cs:36`, `GloopsRecorderUI.cs:27`. `op=rect` already drives it to enlarge a window so one capture shows more rows. The gallery needs no new sizing surface.

**D3. Two title-lookup seams already exist in Career State.** `ContractTitleLookupForTesting` (`:181`) and `StrategyTitleLookupForTesting` (`:186`) are consulted before `ContractSystem.Instance` / `StrategySystem.Instance` (`:1110`, `:1151`). That is the exact shape the other windows' live cells need, and it is a precedent to copy rather than a pattern to invent.

---

## 6. Per-window injectability audit

Every claim below is from the source at `origin/main` commit `a5c47c7e1`. "Live cells" is the important column: those are cells a mock does NOT control, so a catalogue state either avoids them or the phase adds a seam for them.

| # | Window | Model type (where) | Built where / pure? | Consumed where | Injection point | Cache key, does a mock survive? | Live cells under a mock | Est. lines | States unlocked (audit table) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | **Kerbals** | `KerbalsViewModel` (`UI/KerbalsWindowUI.cs:204-209`) over POCO rows (`:229-244`, `KerbalsPresentation.cs:99-169`); zero live objects | `GatherViewModel` `:1026-1056` (impure); pure composer `BuildViewModel` `:1201-1211` | `DrawKerbalsWindow` `:595`, rows `:723/:879` | `CachedViewModelForTesting` `:132` - **settable today** | `cachedVM` `:123`; nulled by `InvalidateCache` `:259` and eight stock `GameEvents` `:288-295` -> **no**, needs a pin | **none** over `:595-916` (chrome only) | 0 + ~5 pin | 4.8: `Lost`, `Retired`, `as <stand-in>`, `Reserved for X until`, `(displaced)`, `|--` glyph: ~15 |
| 2 | **Career State** | `CareerStateViewModel` `UI/CareerStateWindowUI.cs:225-237` + 4 tab VMs + 4 row structs; POCO (2 KSP enums) | `Build` `:363` **pure over 7 args**; 2 pre-seamed title lookups `:181/:186` | `DrawCareerStateWindow` `:1343`, tabs `:1460/:1576/:1681/:1732` | `CachedVMForTesting` `:209` - **settable today** | `ShouldRebuildCachedVM` `:893-923`, incl. UT-text compare `:917` -> **no**, rebuilt every game-second; needs a pin | `HighLogic.CurrentGame` `:1351`, `Planetarium.GetUniversalTime()` `:1368`, both ABOVE the VM read | ~10-20 | 4.7: divergence banner, `Active (N>0)`, pending folds, Flow cell, facility L>1, `(pending)`/`(closing)`: ~23 |
| 3 | **Structure List** | `StructureStep` (`MissionStructureList.cs:31-40`), POCO | `Rebuild` `:131-149`, called **only** from `OpenForMission` `:112` / `OpenForRoute` `:123`, never per frame | `DrawWindow` `:235-306`, rows `:285-297` | the `steps` field `:53` (+ `mode`/`title` `:50-52`); needs one `OpenWithSteps(...)` | **no invalidation key at all** -> **yes, survives indefinitely** | `KSPUtil.PrintDateCompact` in `FormatTime` `:319` only | ~8-12 | 4.6: 6 of 10 terminal statuses, `EVA`/`Boarded`, `Crashed`/`Overheated`/`Broke up`, route pickup forms: ~16 |
| 4 | **Settings** | none by design: binds `ParsekSettings.Current` at `UI/SettingsWindowUI.cs:324` | n/a | `DrawSettingsWindow` `:320` + 6 section draws | `ParsekSettings.CurrentOverrideForTesting` (`ParsekSettings.cs:406`) - **settable today**; the 5 statics below need separate handles | none -> **yes** | `ParsekUI.AppliedUiComplexityMode` `:341`; `UiSurfaceVisibility.IsVisible` `:356/:389/:404/:412` (decides which sections exist); `parentUI.Flight.IsGloopsRecording` `:491/:513`; `RewindPointDiskUsage` `:700-704`; `RecordingStore.CommittedRecordings.Count` `:760` + `MilestoneStore.Milestones.Count` `:761` (the two Wipe labels + enablement `:763/:778`) | 0 for values, ~15-25 for the 5 handles | 4.9: greyed Wipe pair + reasons, density Low/High, 5 diagnostics toggles, Basic-disabled + hint: ~12 |
| 5 | **Timeline** | `List<TimelineEntry>` (`Timeline/TimelineEntry.cs:73-99`); POCO apart from `UnityEngine.Color` (a plain struct) | `TimelineBuilder.Build` (`Timeline/TimelineBuilder.cs:36-42`) pure; call site impure (`UI/TimelineWindowUI.cs:474-486`, ERS `:479`, ELS `:482`) | `DrawEntryList` `:1098`, `DrawEntryRow` `:1275` | `cachedTimeline` `:101` (private, needs an accessor) + `recordingById` `:185` + `committedIndexById` `:193` | `ShouldRebuildTimeline` `:771-775` = `dirty \|\| missing \|\| archiveChanged`, **no UT trigger** -> **yes**, while nothing calls `InvalidateCache` `:366` | `Planetarium` `:1108/:933/:570`; a **second unthrottled** `ComputeERS()` in `DrawTimeRangeFilterBar` `:931` (slider bounds); per-row ghost reads `flight.HasActiveGhost` `:1340` .. `DescribeWatchFocusForLogs` `:1360`; `GetRewindSaveFileName` `:1617/:1788`; `CanFastForwardAtUT` `:1776`; **`File.Exists`** `:1815-1820`; `ResolveUnfinishedFlightRewindRoute` `:1208/:1509`; and row buttons need real `Recording`s in `recordingById` or **no W/R/FF/GoTo/Fly/Seal cell draws at all** | ~40-70 entries only; ~120-160 with row actions | 4.2: strikethrough supersede, archived marker, source toggles OFF, custom range, `W*`, disabled `W`, `FF`, dim future rows, `T-`: ~28 |
| 6 | **Group Picker** | `GroupPickerTreeModel` (`UI/GroupPickerPresentation.cs:5-23`), strings only | `BuildTreeModel` `:121-173` pure over 4 args; called **inside the draw** every frame (`GroupPickerUI.cs:197-201`) | `DrawGroupPopupContents` `:239`, `DrawGroupPopupNode` `:310-354` | the `treeModel` local `:197` -> hoist to a nullable field; plus one seeder for `groupPopupOpen/Group/Checked/Expanded` `:74-82` | none (rebuilt per frame) -> **yes**, once the override is checked each frame | `Screen.width/height` `:210-211`; `parentUI.KnownEmptyGroups` `:272` (click only) | ~20-30 | 4.4: collapsed node, chain-opened, `CycleInvalid` pruning, depth >= 2, multi-selection: ~9 |
| 7 | **Real Spawn Control** | `NearbySpawnCandidate` (`SelectiveSpawnUI.cs:11-22`) + pure `SpawnCandidateRowPresentation` (`SpawnControlPresentation.cs:21-39`, built by pure `BuildRowPresentation` `:88-92`) | `ParsekFlight.CollectNearbySpawnCandidates` `:27256`; exposed getter-only `NearbySpawnCandidates` `:511` | `DrawSpawnControlWindow` `UI/SpawnControlUI.cs:234`, rows `:290` | nullable override read at `:241` (+ `:142`, `:287`) **and** a UT override for `:240`; injecting `cachedSortedCandidates` `:44` does not work (gate `:269-282`) | `cachedSortedCandidates` keyed on count + `ProximityCheckGeneration` + sort -> **yes** for a constant-count list, if the generation is frozen | **`Planetarium.GetUniversalTime()` `:240`** (feeds every countdown `:305/:333`, `BuildRowPresentation` `:308`); `NearbySpawnRadius`/`MaxRelativeSpeed` `:309-310` (green tint `:319`, warp enable `:352`); auto-close gate `:142-153` force-closes outside FLIGHT or on zero candidates | ~25-40 | 4.10: multiple rows, `ConditionsMet` green + enabled warp, `Departing -> X`, `Departs T-`: ~8 |
| 8 | **Test Runner** x2 | `InGameTestInfo` (`InGameTests/InGameTestRunner.cs:30-60`) - mutable class carrying `MethodInfo`/`Type`, but the draw never dereferences them (`UI/TestRunnerUI.cs:284-401`) | `DiscoverTests` `:315-345` (reflects the assembly); grouped by `RebuildTestGroupCache` `:230-245` (pure over `cachedTestGroups`) | `DrawTestRunnerWindow` `:427`, `DrawTestCategoryList` `:284` | the `testRunner` field `:41` (getter-only `RunnerForTesting` `:190`); a list-only mock needs a settable runner, a running/failed mock needs an interface seam because `IsRunning/Passed/Failed/Skipped` are `private set` `:277-280`. Global twin: `TestRunnerShortcut.cs:26`, `:474`, `:487-489` | `cachedTestGroups` `:43`, rebuilt on `null \|\| (wasRunning && !running)` `:516-518` -> **yes** at runner level, **no** if injected into the group cache | `HighLogic.LoadedScene` `:347` (greys/dims every FLIGHT row), `:483`, `:486`; runner counters `:440/:463/:474-479` | ~10 list-only; ~40-60 with the interface | 4.12: RUNNING, failed row, skipped/dimmed/`[single]`, FLIGHT scene, global results: ~9 |
| 9 | **Logistics** | **no window VM.** Four row sources: `Route` (`Logistics/Route.cs:40`, POCO), `RouteCandidate` / `RouteNearMiss` (`RouteCandidateFinder.cs:13/:35`, both carry a live `RecordingTree`), and the private `RouteLegibility` struct (`UI/LogisticsWindowUI.cs:253-346`) which IS the de-facto pure row VM. **All 17 presentation helpers are pure and Unity-free** | 3 impure wall-clock refreshers: `GetCandidates` `:2992`, `GetNearMisses` `:3042`, `RefreshLegibilityCacheIfDue` `:3068` (reads ELS `:3290/:3350/:3456`, `HighLogic.CurrentGame.Mode` `:3277`, `FlightGlobals` `:3543`, live `LiveDeliveryCapacityProbe` `:3391`) | `DrawWindow` `:592`, `DrawRouteRow` `:1072`, `DrawCandidateRow` `:1578` - the row draws themselves are clean, every cell from `route.*` + `GetLegibility(route)` `:1089` | **five**: `RouteStore.CommittedRoutes` at `:598` (extract), `cachedCandidates` `:166`, `cachedNearMisses` `:195`, `legibilityCache` `:214`, `RouteStore.DormantRoutes` at `:781` | wall clock x3 (`:2993`, `:3043`, `:3071`), and **`legibilityCache.Clear()` `:3076`** every 1 s -> **no**, needs the refreshers pinned; L2 sort caches `:234-237`/`:3609-3611` | `RouteStore.TryGetRoute` `:1809`, `RecordingStore.CommittedTrees` `:2060`, `RecordingStore.TryResolveRecordingDisplayInfo` `:2289` (all expanded-row detail text), `Planetarium` via `TryGetCurrentUT` `:3751` | ~180-280 | 4.5: 17 holds, 12 rejects, 6 statuses, 2 badges, `Recent cycles:`, capacity, `Pause`, dormant section, candidates: **~70** |
| 10 | **Missions window** | three pure layers (`MissionVesselRow` `MissionVesselRows.cs:21-37`, `MissionSummaryFacts` `MissionPresentation.cs:116-136`, chapters, periodicity) but the draw's inputs are live `Mission` + `RecordingTree` | `GetMissionView` `UI/MissionsWindowUI.cs:1135-1181` from a live tree, once per tree per frame | the whole file | **not in this file**: `RecordingStore.CommittedTrees` + `MissionStore.Missions` | every derived cache keyed on **`Time.frameCount`** `:1137-1148` -> **no mock can live in any of them** | ~40 inline static reads incl. `MissionStore.HideArchived` `:952/:1032/:4483` (decides how many blocks draw), ERS `:739`, `Planetarium` x8 | 300-600 to make it VM-driven | via option B only |
| 11 | **Recordings tab** | **none.** Live `Recording` objects by index (`UI/RecordingsTableUI.cs:1756`, `[ERS-exempt]` `:1749-1755`) | n/a | the whole file | **not in this file**: `RecordingStore.CommittedRecordings` | `sortedIndices` keyed on `RecordingStore.StateVersion` `:425-427/:4890` -> only if `StateVersion` is stable | ~20 store predicates per row (`CanFastForward`, `CanRewind`, `GetRewindRecording`, `IsRewindRetired`, `GetSegmentPhaseLabel`, `Planetarium` `:1759`) | 400-800 for a row model | via option B only |
| 12 | **Gloops Recorder** | recorder state read live | n/a | `UI/GloopsRecorderUI.cs:105` | n/a - the seam already has `GloopsStart/Stop/Preview` verbs (`TestCommandGloopsVerbs.cs`) which reach all 3 missing states on a real host | n/a | n/a | 0 | 4.11: 3, already reachable; and the launcher is retired in both modes (`UiComplexityMode.cs:140-143`), so P3 in practice |

**Reading of the table.** Nine of the twelve surfaces are injectable for well under 300 lines each; two (Kerbals, Career State) and one static (Settings values) are settable with no production change at all. Two windows (Missions, Recordings) are store-shaped and belong to option B. One (Gloops) needs nothing. The single largest prize is Logistics: ~70 states for ~180-280 lines, and its helpers being pure is what makes the completeness guard of section 11 possible.

---

## 7. Detailed design

### 7.1 Data model

```
GuiMockState                      - one catalogue entry, immutable
  Id: string                      - "<window>.<family>.<variant>", ASCII, lowercase, dots and dashes only
  Window: string                  - a TestCommandUiAction window token (main/timeline/missions/...)
  Scene: TestCommandScene?        - null = any scene the window draws in; else the scene it requires
  Mode: UiComplexityMode?         - null = capture in both modes; else pin one
  RectW, RectH: int               - the size the gallery gives the window before capturing (D2)
  Build: Func<GuiMockPayload>     - builds the synthetic model; called once per apply
  Covers: string[]                - the branch keys this state claims (section 11)
  Note: string                    - one line, why this state exists; goes into the log line

GuiMockPayload                    - the union the applier hands to one window
  Kerbals: KerbalsViewModel?
  Career: CareerStateViewModel?
  Timeline: List<TimelineEntry>?
  TimelineRecordings: List<Recording>?      - seeds recordingById so row buttons draw
  Structure: GuiMockStructure?              - steps + title + TargetMode
  Settings: ParsekSettings?
  SettingsEnv: GuiMockSettingsEnv?          - the 5 Settings live-cell handles
  Logistics: GuiMockLogistics?              - routes + candidates + nearMisses + legibility + dormant
  SpawnControl: GuiMockSpawnControl?        - candidates + pinned UT
  TestRunner: GuiMockTestRunner?            - rows + counters + IsRunning
  GroupPicker: GroupPickerTreeModel?        - plus the popup selection seed

GuiMockSession                    - the live scope, one at a time, process-lifetime static
  StateId: string
  Window: string
  AppliedFrame: int
  AppliedUtc: string              - InvariantCulture, for the log and the dump
  Restore: Action                 - captured closures that put every touched member back
```

Nothing here is serialized. `GuiMockSession` is a static on an automation-only type, not a field of `ParsekScenario`, so `OnSave` has nothing to write even if a session is live (S5).

### 7.2 Where the catalogue lives

`Source/Parsek/UI/Gallery/`, inside `Parsek.dll` because the in-game applier consumes it:

```
GuiMockCatalogue.cs          - the registry: All, ById, ForWindow, plus the arming gate
GuiMockPayload.cs            - the union + the small per-window carrier types
GuiMockKerbalsStates.cs      - builders returning real KerbalsViewModel
GuiMockCareerStates.cs       - real CareerStateViewModel
GuiMockTimelineStates.cs     - real List<TimelineEntry>
GuiMockStructureStates.cs    - real List<StructureStep>
GuiMockLogisticsStates.cs    - real Route / RouteLegibility / RouteCandidate
GuiMockSettingsStates.cs
GuiMockSpawnControlStates.cs
GuiMockTestRunnerStates.cs
GuiMockGroupPickerStates.cs
```

Why C# beside the presentation models rather than TOML or JSON: the builders construct the **real** types, so a renamed field, a changed struct shape or a new required member is a compile error in the same build that changed the window. A data file would carry the rename silently and the gallery would photograph a stale state while claiming coverage. The cost is that the catalogue ships in the player's DLL; it is inert (nothing calls it unless the seam is armed) and it is text plus a few hundred object literals, which is far cheaper than the risk of a data file drifting.

A builder never fabricates a rendered string where a pure helper exists. `logistics.hold.escrow-short` builds the hold INPUTS and calls `LogisticsHoldPresentation` to render the clause, exactly as the game does. This is what makes section 11's guard meaningful and what keeps a catalogue state from teaching the owner about a string the product cannot produce.

### 7.3 The seam: verb grammar

Two surfaces, one primitive and one loop over it. Both refuse unless `PARSEK_TEST_COMMANDS=1` armed the addon (`InGameTests/AutorunHooks.cs`), which is the same gate every other census op already sits behind.

**Primitive - a new `UiAction` op, as a PAIRED op in the `raise` / `dismiss` shape.** The seam has no saved-previous-value machinery anywhere and no automatic restore: a grep over the four UiAction files finds one hit, about a stale pending read (`ParsekTestCommandAddon.UiAction.cs:103`). The house pattern is instead an explicit pair plus an unconditional clear helper on every exit path - `op=raise` deliberately leaves a modal standing so a capture can photograph it (`TestCommandUiAction.cs:74-84`), and `ReleaseRaisedDialogInputLock` (`ParsekTestCommandAddon.UiRaiseDismiss.cs:407-419`) releases on EVERY exit rather than the happy one, keyed per row via `UiRaisableDialog.OwnsInputLock` "rather than a blanket clear every Parsek lock, which would reach locks this op did not set". The gallery copies that exactly: a paired apply/clear, a per-window restore closure, and no blanket "rebuild everything".

```
UiAction op=mock window=<token> state=<stateId>      - apply; two-phase, settles on a DRAWN frame
UiAction op=mock window=<token> state=none           - clear (the paired op)
UiAction op=mock describe=true                       - report the catalogue: count, windows, ids
```

`UiAction` is already absent from `TestCommandVerbs.NonMutatingVerbs` because `op=complexity` persists a setting (`TestCommandVerbs.cs:327-330`), so adding a state-mutating op needs no change there - and the reasoning is worth keeping: `op=complexity` has no seam restore at all, the LANE restores it (`GUI-1-census-ksc.toml:332-338`), which is the same discipline a gallery lane follows for the mode it leaves behind.

**Wiring checklist for the new op**, because the token is declared four co-equal times plus twice in the harness: the `UiActionOp` enum member (`TestCommandUiAction.cs:8-94`), the `...OpToken` const (`:223-240`), the `TryParseOp` case (`:616-648`) and its reverse `OpToken` (`:651-673`), the `ValidOpNames` entry (`:596-604`), `OpNeedsWindow` and `OpIsTwoPhase` (`:762-801`), the `TestCommandDispatcher` interface row and scene requirement, and `hlib.UIACTION_OP_VALUES` + `UIACTION_OPS_NEEDING_WINDOW` (`harness/lib/hlib.py:2120-2151`, mirrored not derived so a typo is caught before a boot). Arg keys are declared as constants in the newer style (`TestCommandUiState.cs:87-105`), not as literals. One `ResolveWindowHandle`-shaped row per window carries the data-source swap accessor (`ParsekTestCommandAddon.UiAction.cs:850-977`), for the reason its own header gives: "a mapping error (the kerbals arm reaching the career window's field) lived in ONE of eight arms while the other seven stayed right, and the whole xUnit suite passed either way".

OK payload, in the established `k=v` shape and InvariantCulture throughout:

```
uiaction mock window=logistics state=logistics.hold.escrow-short applied=true
         covers=2 frame=184122 mode=advanced
```

Refusal reasons, kebab-case like every existing token (`TestCommandUiAction.cs:280-444`):

| reason | when |
| --- | --- |
| `mock-arg-missing` | no `state=` and no `describe=` |
| `mock-state-unknown` | `state=` names no catalogue id; message carries `window=<w> ids=<n>` |
| `mock-window-unsupported` | the window has no injection seam in this build; message names the supported set |
| `mock-state-window-mismatch` | the id's window is not the `window=` arg |
| `mock-refused-scene` | the state declares a scene the game is not in |
| `mock-refused-recording` | a Gloops or flight recording is active (mirrors `complexity-refused-gloops-recording`) |
| `mock-refused-session-live` | a mock is already live for another window; one at a time, by design |
| `mock-not-applied` | after the settle frame the read-back says the window did not draw the mocked model |
| `mock-restore-failed` | restore threw; logged as Error, the window is force-closed, the session is dropped |

Each token is an `internal const string ...Reason` on the pure half with a doc comment classifying it PRE-CALL / POST-CALL / POST-SETTLE, which is how every existing family is declared (`TestCommandUiAction.cs:276-444`, `TestCommandUiState.cs:189-290`), and the valid set is echoed in the refusal `msg` the way `op-arg-invalid` echoes `valid=<ValidOpNames>`.

**`mock-not-applied` is the load-bearing one, and its read-back must be draw-produced.** Reading back the field the applier just wrote is the vacuous read-back the `rect` op was first written with and had to be fixed for: comparing the written value with itself left `RectAppliedWithinTolerance` unable to fire on any input (`TestCommandUiAction.cs:196-200`, `ParsekTestCommandAddon.UiAction.cs:469-473`). Only the DRAW witnesses a data swap, so the settle uses the `op=find` mechanism - `GuiTreeRecorder.ArmForNextRepaint(label, writeToDisk: false)` (`GuiTreeRecorder.cs:339-353`) - and asserts the mocked rows appear in the tree the frame produced. This is also why `SettleChecksHostShowUi` is narrowed to `open` and `rect` alone (`:774-785`) and `op=mock` does not join them: its settle proves a drawn frame by construction.

Answering OK without that proof is exactly the failure that produced the four stale and four empty-hover labels the mirror carries today: a capture that reads as coverage and is not.

**Loop - a new batch-owner verb** (D1).

```
GalleryRun catalogue=<all|window token|comma list of ids>
           mode=<advanced|basic|both>
           prefix=<label prefix, default "mock">
           [limit=N]
```

It walks the selected states in catalogue order and, per state: apply -> wait one drawn frame -> `CaptureScreenshot` + `DumpGuiTree` under the derived label -> restore -> log one line. It blocks like `op=run` does (`run-not-finished` has the same shape) and then emits one summary line the spec pins:

```
GALLERY_COMPLETE v1 total=312 captured=308 skipped=4 failed=0 catalogue=gui-mock/1 wall=214.8s
```

`skipped` counts states whose declared scene or mode did not match the run, each already named by its own Info line; `failed` is any state that refused or did not apply. A spec pins `total=` exactly and regexes the rest, the same interim-pin convention `IngameBatchWiringGroupTests.INTERIM_PIN_IDS` already uses for a never-flown spec.

### 7.4 Making the mock survive the caches

Section 6 shows three different cache behaviours. One mechanism covers all of them: a single process-lifetime `GuiMockSession` that the window's rebuild predicate consults.

| Window | What the phase adds |
| --- | --- |
| Career State | `ShouldRebuildCachedVM` `:893` returns false while a session owns `career`; the UT-text compare at `:917` is what would otherwise clobber the VM within one game-second |
| Kerbals | `InvalidateCache` `:259` and `OnLiveCrewStateChanged` `:361` become no-ops while a session owns `kerbals` (eight stock `GameEvents` feed them, `onVesselChange` among them) |
| Timeline | `ShouldRebuildTimeline` `:771` returns false; `InvalidateCache` `:366` is suppressed |
| Logistics | the three wall-clock refreshers `:2993/:3043/:3071` return early, and `legibilityCache.Clear()` `:3076` is skipped |
| Spawn Control | the generation/count gate `:269-282` is satisfied naturally by a constant-count list; the UT read `:240` goes through the session's pinned UT |
| Structure List, Settings, Group Picker | nothing: no invalidation key to fight |

The suppression is one predicate read per rebuild site, `GuiMockSession.Owns("<window>")`, which is false in every player build because the session can only be created by the armed seam. A unit test asserts that each suppressed site is inert when no session exists, and a source gate asserts the set of suppressed sites equals the set the applier claims to support (the `GuiCensusApplierSourceGateTests` shape, over comment-stripped source).

### 7.5 Crash safety: why a mock can never reach a save

Four layers, in order of strength.

1. **Structural (the only one that has to work).** Every injected member is a UI-layer field: `cachedVM`, `cachedTimeline`, `steps`, `legibilityCache`, `cachedCandidates`, a nullable override field, or `ParsekSettings.CurrentOverrideForTesting`. None of them is read by `ParsekScenario.OnSave` (`:1165`), by any sidecar writer, or by `RecordingStore` / `Ledger` / `MissionStore` / `RouteStore`. A save taken mid-mock writes the real game. This is what makes S5 true by construction rather than by a finally block. A grep gate enforces it: the gallery applier's write-set must be confined to the injection members the catalogue declares, and no gallery file may appear in `scripts/ers-els-audit-allowlist.txt` (it reads no committed recordings at all; where it needs effective state it routes through `EffectiveState.ComputeERS/ComputeELS` like every other consumer).
2. **The seam refuses to save.** `SaveGame` answers `save-refused-gui-mock` while a session is live. A lane that wants a save ends the session first. This is a lane-hygiene guard, not a data guard.
3. **Every exit clears.** The addon already subscribes `onGameSceneLoadRequested` and `onLevelWasLoaded` (`ParsekTestCommandAddon.cs:246-247`): both clear the session. `FlushAndQuit` clears. An exception anywhere inside apply, draw-settle or capture runs restore and answers `ui-action-threw` / `mock-not-applied`. `GalleryRun` restores in a `finally` per state, so one bad state cannot poison the next.
4. **A process that dies mid-mock leaves nothing**, because of (1). `ParsekScenario.OnLoad` logs one Warn if it ever finds a session static set at load (it cannot, across processes) - cheap, and it makes the claim observable rather than assumed.

Every one of these transitions is logged (section 10).

### 7.6 Labels and how a mocked capture announces itself

**Label.** `<prefix>-<window>-<stateTail>-<mode>`, where `stateTail` is the state id minus its window prefix with dots turned into dashes:

```
logistics.hold.escrow-short   ->  mock-logistics-hold-escrow-short-advanced
career.banner.divergent       ->  mock-career-banner-divergent-advanced
kerbals.roster.lost           ->  mock-kerbals-roster-lost-advanced
```

This is already the mirror's grammar `<host>-<window>[-tab][-state]-<mode>` (`design-gui-mirror.md:122-140`), with `mock` as the host, so `parse_label` files it under the right window. The tab, where a state pins one, sits between window and state exactly as it does today.

Three hard constraints on the generated label, all of which the catalogue must enforce at build time rather than discover in a run:

* **96 characters and `^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9-])?$`** (`hlib.py:2370-2371`, mirrored by `TestCommandCaptureScreenshot.IsValidLabel:155-178`, which is deliberately stricter than the recorder's own `SanitizeLabel` so an accepted label survives it unchanged). A state id of `<window>.<family>.<variant>` plus the `mock-` host and the `-advanced` mode fits comfortably; a unit cell pins that every catalogue id does.
* **Nothing validates label uniqueness within a run.** The 12 existing lanes happen to be unique and no check enforces it; `captured.overwrote` is not a duplicate detector (it fires 303 of 439 times because KSP's `Screenshots/` persists between runs in the same instance). A 300-label gallery must assert uniqueness itself - the same unit cell.
* **The PNG's pixel dimensions must equal the dump's own `screen` exactly, or the mirror samples no colours at all for that capture** (`gui_mirror.py:1246-1257`). So a gallery capture must not change `superSize` or the screen metrics mid-run.

**Provenance in the artifact, not in the name.** A new additive block in the dump header (`GuiTreeCaptureHeader`, `GuiTreeJson.cs:23-51`; writer `:70-127`):

```json
"mock": {"stateId": "logistics.hold.escrow-short",
         "window": "logistics",
         "catalogue": "gui-mock/1",
         "states": 312,
         "covers": ["RouteStatus.EndpointLost", "hold.escrow-short"]}
```

Absent means a real-save capture. The key is additive and the schema id stays `parsek-gui-tree/1`: no key is renamed, no layout changes, and an older reader ignores it - the same additive-is-not-a-bump reasoning the recording schema uses. Two existing source-sync cells must learn the key in the same commit (`harness/lib/test_gui_tree_view.py`'s three cells over `GuiTreeJson.cs`), or the offline viewer degrades silently instead of failing.

**Why not the label alone.** The mirror derives a capture's dataset from `fixture.saveTemplate` in the spec (`gui_mirror.py:1130-1148`). A gallery lane HAS a saveTemplate - it needs a loaded game - so without the dump block its mocked captures would be filed under a real fixture's name and pair against real captures in Compare. That is the one lie this page must not tell.

**In the mirror.** Four changes, all owned by `design-gui-mirror.md`:

* `scan_shots_dir` (`gui_mirror.py:1087-1127`) reads the dump's `mock` block and sets `fixture = "mock"` plus a new `mocked` key on the capture record (`:1323-1343`; `_page_model` forwards the record whole at `:2705`, so the key reaches the page untouched). Because `fixture` is already part of `key_of` (`:287-295`), a mocked capture then **cannot** pair with a real one in Compare - the isolation is structural, not cosmetic.
* every mocked tile and every mocked Compare row carries a `MOCKED DATA` badge, and the rail shows the mocked count beside the real one per window.
* `gui-mirror-index.json` gains `mockedCaptureCount` at the top level and `mocked: true` on the state rows (`build_index:2712-2731`), so "how much of this is real" is answerable without opening the page.
* **the default dataset must stop being derived** while a mock dataset exists. Today it is "the fixture that photographed the most DIFFERENT windows at the Space Center" (`design-gui-mirror.md:168-172`), and a 300-state gallery would win that contest and silently become the page the owner opens. The gallery phase pins the default to a real fixture and offers `mock` as an explicit choice.

### 7.7 The lane, and what it costs

One spec, `GUI-13-gallery-mock`, tier `operator` (run on request, like every GUI census lane), on a committed fixture rather than the operator-local `c1-gui` - the gallery needs a loaded game, not a dense one, and a committed fixture makes the lane reproducible:

```toml
[driver]
kind = "seam"
steps = [
  { cmd = "LoadGame",   args = { save = "${runSave}", name = "persistent", scene = "spacecenter" }, expect = "OK", budget = 300 },
  { cmd = "SetSetting", args = { name = "verboseLogging", value = "true" }, expect = "OK" },
  { cmd = "UiAction",   args = { op = "complexity", mode = "advanced" }, expect = "OK" },
  { cmd = "UiAction",   args = { op = "open", window = "main" }, expect = "OK" },
  { cmd = "UiAction",   args = { op = "mock", describe = "true" }, expect = "OK" },
  { cmd = "GalleryRun", args = { catalogue = "all", mode = "both", prefix = "mock" }, expect = "OK", budget = 900 },
]
```

A FLIGHT half (`GUI-14-gallery-mock-flight`) runs the states whose `Scene` is FLIGHT: Spawn Control, the Timeline Watch column, the Settings Gloops-locked pair, the in-flight Logistics window the census has never photographed.

**Measured cost.** Derived from 39 collected `KSP.log`s across 42 GUI census run dirs (30 PASS), in the `Parsek-gui-census-*` and `Parsek-gui-playback-scope` results trees; `harness/results` in the main checkout is empty.

Per-command wall cost, from 2374 `recv id=` to next-`recv` deltas:

| command | n | median | p90 |
| --- | --- | --- | --- |
| `UiAction` | 1319 | **0.252 s** | 0.258 s |
| `CaptureScreenshot` | 439 | **0.252 s** | 0.257 s |
| `DumpGuiTree` | 439 | **0.250 s** | 0.263 s |
| `LoadGame` | 38 | 6.23 s | 7.10 s |

**A screenshot, a tree dump and a UI click all cost the same 0.25 s, because the cost is the harness poll, not the game.** `POLL_INTERVAL_SECONDS = 0.25` (`harness/run.py:146`, consumed in the drive loop at `:1779`); 2125 of 2197 deltas for those three verbs land in the single 0.25 s bin, with sub-poll minima of 0.116 s (`UiAction`) and 0.094 s (`DumpGuiTree`) showing the real in-game work is roughly 0.1 s. Fixed per boot: **25.0 s** boot (log init to first recv, n=30) + **6.2 s** `LoadGame` + **21.1 s** teardown (quit, snapshot, 13 verifiers, harvest) = **52.3 s**, independently confirmed by `wallSeconds` minus in-scene UT span holding at a median 53.0 s across lanes whose UT span ranges 6.6-27.4 s.

Density, pooled over the 30 PASS runs: **345 capture pairs, 2.98 `UiAction`s per pair, 5.42 commands per pair** including the amortised preamble. Leanest lane GUI-5 at 4.78 commands/pair, heaviest GUI-9 at 7.60.

Estimates for ~300 states in ONE boot:

| shape | commands/state | drive | total | per state |
| --- | --- | --- | --- | --- |
| lean (GUI-5 measured) | 4.78 | 361 s | **414 s / 6.9 min** | 1.38 s |
| pooled (measured) | 5.42 | 410 s | **462 s / 7.7 min** | 1.54 s |
| heavy (GUI-9 measured) | 7.60 | 575 s | 627 s / 10.4 min | 2.09 s |
| `GalleryRun`, no poll per state | 0 seam commands | ~180 s | **~230 s / 3.9 min** | ~0.6 s |

So **7 to 10 minutes step-driven, or about 4 minutes through the batch verb** - against 840 s (14.0 min) for today's entire 134-pair corpus across 12 boots, where 628 s of that is 12 payments of the fixed 52.3 s. Consolidating into one boot is a **4.1x per-state improvement, almost entirely boot amortisation rather than a faster capture.** `GUI-1` already carries `budgetSeconds = 1500`, so no budget needs raising.

**One hard blocker, and it must land before any gallery lane flies.** `ARTIFACT_MAX_SCREENSHOTS = 64` (`harness/lib/hlib.py:9496`), enforced in the harvest planner at `:9569` and `:9588-9592`. A capture pair is TWO files (`.png` and `.gui.json` are both in `ARTIFACT_SHOTS_SUFFIXES`, `:9495`), so **a run harvests at most 32 states today**, and a 300-state run would drop 536 of its 600 files into `skipped_over_cap` - reported as `artifacts.screenshotsSkipped` but easy to miss, and every current run reads 0 because GUI-1's 45 files sit just under the cap (which `GUI-1-census-ksc.toml:83-87` calls out). Raising the count cap is the single highest-leverage change in this design. The byte cap is not the binding one but is close at scale: `ARTIFACT_MAX_SCREENSHOT_BYTES = 256 MB` (`:9497`) against measured sizes (PNG median 194 KB / mean 353 KB / p90 660 KB; `.gui.json` median 14.8 KB / mean 74.3 KB / max 2.44 MB) puts 300 states at 63 MB median, 128 MB mean and 257 MB at p90 - i.e. exactly at the cap in the worst case, so the phase raises both and `ARTIFACT_SHOTS_MAX_TOTAL_BYTES = 2 GB` (`:9608-9609`) becomes the thing to watch across retained runs.

Without the cap raise the gallery must split across runs, re-paying 52.3 s each time: 32 states/run = 10 runs = 15.5 min; 64 = 11.2 min; 150 = 8.6 min; 300 = 7.7 min.

One unmeasured cost to note honestly: every existing lane does **exactly one** `LoadGame`, so a gallery that needs KSC and FLIGHT states in one boot has no data point for the second scene load. Presumably ~6.2 s; no run proves it. The plan therefore splits KSC and FLIGHT into two lanes rather than betting on it.

---

## 8. Behavior, state by state

### 8.1 Apply

1. Parse (pure, in `TestCommandUiAction`): op, window, state id. Reject on the section 7.3 table.
2. Gate: armed seam, scene matches the state's `Scene`, no recording active, no session live.
3. Resolve the catalogue entry, call `Build()` once. A throwing builder is `ui-action-threw` and no session is created.
4. Ensure the window is open and sized (`op=open` / `WindowRectForTesting` semantics, reused unchanged).
5. Install: set the injection member(s), capture the previous values into `Restore`, create the `GuiMockSession`.
6. Hold the command head for one drawn frame (the existing two-phase contract).
7. Read back: the injection member is still the installed object AND the window drew nodes this frame. Else restore and answer `mock-not-applied`.
8. Answer OK with the payload of section 7.3.

### 8.2 Capture

The lane (or `GalleryRun`) takes `CaptureScreenshot` + `DumpGuiTree` under the same label, unchanged from today, except that `GuiTreeRecorder` reads the live session and writes the `mock` block into the header.

### 8.3 Restore

Runs `Restore`, clears the session, logs. Then the window's own invalidation is un-suppressed, so the next frame rebuilds the real model. The applier does NOT force a rebuild itself: letting the window's own predicate do it is what proves the suppression was the only thing holding the mock.

### 8.4 Edge cases

| # | Case | Behavior |
| --- | --- | --- |
| E1 | `op=mock` on a window with no seam yet | `mock-window-unsupported`, message names the supported set. Phases add windows; the refusal names what this build has. |
| E2 | Two mocks at once | `mock-refused-session-live`. One at a time: two windows' suppressions interacting is a state nobody can reason about, and the gallery has no need for it. |
| E3 | Complexity switch while a mock is live | allowed only if the state does not pin a mode; `op=complexity` is already refused during a Gloops recording (`ParsekTestCommandAddon.UiAction.cs:748-759`) and the session is orthogonal to it. Switching to Basic force-closes gated windows (`ParsekUI.BuildGatedWindowCloseSet:530-571`), so a Basic capture of Kerbals or Career is not a state (audit section 3 proves 6 of 9 such index rows are unreachable) and the catalogue must not declare one. |
| E4 | The window self-closes under the mock | Spawn Control does this on zero candidates (`SpawnControlUI.cs:142-153`). A catalogue state for that window must supply a non-empty list; the read-back catches the rest as `mock-not-applied`. |
| E5 | A stock `GameEvent` fires mid-mock | the suppression holds (7.4). The event still reaches the real subsystems; only the window's cache rebuild is deferred. |
| E6 | Scene change mid-mock | session cleared on `onGameSceneLoadRequested`; any in-flight `GalleryRun` stops and reports `failed` for the interrupted state. |
| E7 | A save happens mid-mock | writes the real game (7.5 layer 1). The seam's own `SaveGame` refuses (layer 2) so a lane cannot do it by accident. |
| E8 | Restore throws | Error, force-close the window, drop the session, `mock-restore-failed`. The next state's apply starts from a closed window, which is a clean state. |
| E9 | A catalogue state references a model field that no longer exists | build error. That is the whole reason the catalogue is C#. |
| E10 | A state's live cells make the capture ambiguous | the catalogue state declares its live cells in `Note`, and the phase that adds the window's seam either pins them or the state is not admitted. A Spawn Control state without a pinned UT is not admitted, because every countdown cell would read from the wall clock. |

---

## 9. What does not change

* No player-facing surface. No window, no badge, no tooltip, no screen message.
* The real-save census is untouched: every existing GUI lane, label and capture keeps working, and the mirror keeps showing them as it does today. The gallery ADDS a dataset; it replaces nothing.
* No recording, ledger, mission, route or save format changes. The one serialization touch is an additive key in the GUI-tree dump, which is not game data.
* `RecordingStore`, `Ledger`, `MissionStore`, `RouteStore` and every sidecar writer are not called by any gallery code path.
* The mirror's guarantee that it types no window text of its own is unaffected: mocked captures are dumps and PNGs like any other.
* Hover states remain uncaptured and out of scope.

---

## 10. Diagnostic logging

Subsystem tag `[GuiMock]` for the session and applier, reusing `[TestCommands]` for the command echo (so a lane's log reads as one conversation) and `[GuiTree]` for the dump. Every transition below is logged; a reader of `KSP.log` must be able to reconstruct which states were photographed, which were refused and why, and that every session was restored.

| Level | Event | Must carry |
| --- | --- | --- |
| Info | `mock apply` | `state=<id> window=<w> mode=<m> scene=<s> covers=<n> frame=<f>` |
| Info | `mock applied` (after the settle frame) | `state=<id> nodes=<n> readback=ok` |
| Info | `mock restored` | `state=<id> window=<w> heldFrames=<n>` |
| Info | `mock describe` | `catalogue=gui-mock/1 states=<n> windows=<list>` |
| Warn | `mock rejected` | `reason=<token>` plus the arg that caused it - one line per refusal, the shape `uiaction rejected reason=... ` already uses |
| Warn | `mock cleared by scene change` | `state=<id> scene=<from>-><to>` |
| Error | `mock restore failed` | `state=<id> window=<w> exception=<type>` |
| Info | `gallery state` (one per state) | `i=<k>/<n> state=<id> label=<label> verdict=<captured\|skipped\|failed> reason=<token or -> wall=<s>` |
| Info | `GALLERY_COMPLETE v1` | `total= captured= skipped= failed= catalogue= wall=` |
| Verbose | `mock suppression` | `site=<career-vm\|kerbals-invalidate\|timeline-rebuild\|logistics-legibility\|...> owner=<window>` - one-shot per site per session, not per frame |

Numbers in payloads and log lines are formatted with `CultureInfo.InvariantCulture`: the `GALLERY_COMPLETE` line and the `k=v` payloads are parsed by the harness, which is the M-A2 response-grammar rule.

Rate limiting: the suppression line is one-shot per site per session (`ParsekLog.Verbose`), never per frame. No gallery line fires from inside a draw pass.

---

## 11. The completeness guard

The catalogue must not quietly fall behind the draw code. The rule:

> Every enum value and every distinct reason clause that a draw branch switches on must be produced by at least one catalogue state.

**There is no Roslyn in this test assembly** - `Parsek.Tests.csproj:9-21` carries `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Moq` and `coverlet.msbuild` and nothing else, and a repo-wide grep for `CSharpSyntaxTree|Microsoft.CodeAnalysis` returns exactly one hit: the comment in `Source/Parsek.Tests/SourceScanText.cs:16` saying there is none. So the house substitute is that file, already consumed by 27 test classes: a length-preserving comment stripper (`StripCSharpComments:35`), a literal masker (`MaskStringLiteralContents:112`), a brace walker (`OpenBlockStack:174`, `BraceMatchedBlock:195` which THROWS on imbalance rather than passing), `EnclosingMethodBodyStart:216`, and `IfConditions:229` - the last being the "read as a branch condition, not merely mentioned" primitive this guard needs. The worked consumer to imitate is `MissionsWindowLoopGateTests` (scan at `:191-222`), including its three properties: never-drawn is a FAILURE not a skip, unparseable structure is a FAILURE not a pass, and it carries mutation-verified anti-vacuity decoys (`:91-107`) proving the scan reds when the gate exists only in a comment.

Two mechanisms, neither a regex over raw source.

**11.1 Enums: reflection, no source reading at all.** For each enum a draw branch switches on - `RouteStatus`, `TerminalState`, `RosterStatus`, `ChainMemberStatus`, `TimelineEntryType`, `TargetMode`, `StructureStep.Kind`, `TestStatus`, `SignificanceTier` - the test reflects `Enum.GetValues` and asserts every member appears in some state's `Covers`, **naming the missing member and where to add it**, asserting the covering ids are distinct, and ending on a hardcoded count floor so the loop cannot go vacuous. That is the exact shape of `MapRenderTracerCoverageTests.cs:62-89` (which does this for log tokens) and `RouteDormantUiTests.cs:249-270` (which pins a 9-entry `RouteStatus` matrix against `Enum.GetValues(...).Length`). The set of enums under the guard is pinned in the test and a source gate asserts the pinned list equals the enums the supported windows' draw methods actually switch on, read through `SourceScanText`.

**11.2 Reason strings: run the catalogue through the real helpers.** This is the better half. Every Logistics presentation helper is pure and Unity-free, so xUnit can execute them. The test:

1. builds every catalogue state for the window,
2. runs the real helper over each (`LogisticsHoldPresentation`, `LogisticsRejectPresentation`, `RouteCreationFormatters`, `LogisticsDeliveryPresentation`, ...) and collects the PRODUCED string set,
3. extracts the DECLARED literal set from those helpers' own source through `SourceScanText.StripCommentsAndMaskLiterals` (length-preserving, so an index into the prepared text is the same index in the file and the failure can name a real line),
4. asserts every declared literal is produced by at least one state, naming the missing clause and its file:line.

It also carries its own anti-vacuity cells, which the precedents make non-optional: one over a SYNTHETIC source proving the scan reds when the stripper is removed (the shape of `test_the_op_needs_window_parse_is_not_vacuous`, `harness/lib/test_hlib.py:14905-14917`, which exists because the real method's doc comment names the very members the parse must exclude), and one count floor proving the helper walk found anything at all (the shape of `test_the_source_tree_is_actually_readable`, `:3613-3621`).

This is strictly stronger than a hand-maintained map: it proves the catalogue can actually make the game emit that string, not merely that somebody wrote the string into a table. It is also the guard that makes the 17 hold clauses and 12 reject reasons real coverage rather than 29 rows in a doc.

**11.3 A product improvement the guard earns.** Step (3) is fragile against interpolated clauses (a `$"..."` literal is stored split at its holes). The phase that lands the Logistics states therefore extracts each hold and reject clause into a named `internal const string` format, which makes the reason vocabulary greppable for debugging as well - a small refactor with no behavior change, and it is the difference between a mechanical guard and a best-effort one. Open question 3 asks for the go-ahead.

**11.4 Two more cheap cells.** (a) every catalogue `Id` is unique, matches `^[a-z0-9]+(\.[a-z0-9-]+)+$` and starts with a real window token from `TestCommandUiAction`'s window table; (b) every state's `Window` is one the applier claims to support, derived from the applier's comment-stripped source - the `GuiCensusApplierSourceGateTests` shape, so a state naming a window with no seam reds locally instead of burning a flight.

---

## 12. The iteration loop, end to end

What is automatic and what needs the owner is marked on each step.

**0. One-time.** `python harness/missions/bootstrap_venv.py` in the worktree if the lane needs it; the gallery lane itself needs no venv.

**1. Baseline gallery (AGENT).** On `origin/main`, in a sibling worktree:

```bash
cd Parsek-<branch>/Source/Parsek && dotnet build          # build the DLL under test
cd ../../harness && python provision/provision.py --profile stock-minimal
# verify the AUTOMATION DLL carries the change (CLAUDE.md "Verify the deployed DLL")
python run.py --id GUI-13-gallery-mock
```

Artifacts: `results/<runId>_GUI-13-gallery-mock_shots/` with ~300 PNG + `.gui.json` pairs and the run's `KSP.log`. Keep the runId; it is the BEFORE.

**2. Change the draw code (AGENT, on a branch).** Ordinary work in the worktree. No catalogue change unless the change adds a branch - in which case the guard of section 11 demands a state and says which.

**3. After gallery (AGENT).** Rebuild, re-provision, re-run the same lane. Second runId is the AFTER. Both runs are the same fixture, the same catalogue and the same labels, which is what makes Compare pair them: the key is `(fixture, window, tab, state, mode, scene)` and BEFORE/AFTER are earliest/latest `capturedUtc` (`design-gui-mirror.md:181-186`).

**4. Regenerate the mirror (AGENT).**

```bash
python harness/tools/gui_mirror.py \
  --shots "<...>/results/<beforeRunId>_GUI-13-gallery-mock_shots" \
  --shots "<...>/results/<afterRunId>_GUI-13-gallery-mock_shots" \
  --shots "<...>/results/*_GUI-*_shots" \
  --repo . --out gui-mirror.html --index gui-mirror-index.json
```

The real-save census dirs come along, so the owner sees the mocked dataset and the real one on the same page, badged. Compare is scoped per window by the rail, which is how ~300 rows stay readable.

**5. Fidelity check (AGENT, gating).**

```bash
python harness/tools/gui_mirror_fidelity.py --page gui-mirror.html \
  --shots "<...>/results/<afterRunId>_GUI-13-gallery-mock_shots" --report fidelity.json
```

A page whose rendering disagrees with the frames is not shown to the owner: the point of the loop is that he judges the game's pixels, and an unfaithful page would have him judging the generator's. This is the one step whose failure stops the loop rather than annotating it.

**6. Owner review (OWNER).** He opens `gui-mirror.html`, picks the window, reads Compare. Each row shows BEFORE and AFTER drawn from their own dumps, the CHANGELOG / todo / merge notes already attached, the measured node and column deltas, and the `MOCKED DATA` badge where it applies.

**7. Feedback capture (OWNER types, AGENT consumes).** The mirror is a static file and must stay one. The lightest thing that works, and the proposal:

* every Compare row and every state tile gets a one-line **notes** textarea, persisted in `localStorage` keyed by `(runPair, window, tab, state, mode)`, so closing the tab does not lose comments;
* a page-level **Export notes** button serialises every non-empty note to a JSON blob AND a markdown table in a `<pre>`, with a copy-to-clipboard button;
* the owner pastes that blob into chat. The agent reads it as `{stateId, verdict, note}` rows and turns them into the next iteration's task list.

No server, no upload, no dependency: `localStorage` plus a `<pre>` is the whole mechanism, and it degrades to "read the page, type in chat" if he prefers. Open question 5 asks whether he wants a file download instead.

**8. Iterate (AGENT).** Fix, then step 2. The AFTER of iteration N is the BEFORE of N+1 automatically, because Compare keys on time rather than on a pinned baseline.

**One window at a time (owner ruling 2026-09-21).** The gallery and the mirror cover every window at once, but the ANALYSIS does not: the improvement discussion and the comparison of browser renders run one window at a time, iteratively. A round is: pick one window, show its states and one proposal, take the owner's notes, build, re-run the gallery, re-render, compare, and repeat until he is done with that window; only then does the next window start. Steps 2 to 8 above are therefore always scoped to a single window token, and a draw-code branch never mixes changes to two windows.

**Who does what.** Automatic: build, provision, DLL verification, the gallery run, mirror regeneration, the fidelity gate, extracting the notes blob into tasks. Owner: the judgement in step 6 and the words in step 7. That is the whole of his involvement, which is what he asked for.

---

## 13. Test plan

**Unit (xUnit, `Source/Parsek.Tests/`).**

* `GuiMockCatalogueTests` - id grammar, uniqueness, window tokens real, every `Build()` returns a non-null payload of the declared window's type, no builder throws, no builder reads a live static (asserted by running the whole catalogue under a harness with no `HighLogic`, the way the existing pure-presentation tests run).
* `GuiMockCompletenessTests` - section 11.1 and 11.2, one cell per enum and one per helper, each naming the missing member or clause with its file:line.
* `GuiMockSessionTests` - one session at a time; restore puts every member back; restore runs on exception; `Owns()` is false with no session; the suppression predicates are inert when no session exists.
* `TestCommandUiActionTests` additions - the `op=mock` parse table and every refusal token, including `mock-state-window-mismatch` and the `describe` form.
* `GuiMockApplierSourceGateTests` - the applier's supported-window set, its suppression-site set and its injection-member set derived from comment-stripped source and compared against the pure side's tables (the existing `GuiCensusApplierSourceGateTests` pattern).
* `GuiTreeJsonTests` addition - the `mock` block round-trips, is absent when no session, and every number in it is InvariantCulture.
* Log-assertion cells (`ParsekLog.TestSinkForTesting`, the `RewindLoggingTests.cs` shape) for `mock apply` / `applied` / `restored` / `rejected` / `GALLERY_COMPLETE`, so a silent removal of any of them reds.

**Harness (Python, `harness/lib/`).**

* `test_hlib` cells for the `GALLERY_COMPLETE v1` parse, and `GUI-13`'s interim pin in `INGAME`-style pinning until its first flight.
* a `test_gui_mirror` cell that a mocked dump files under `fixture=mock` and never pairs with a real capture in Compare, plus one asserting the badge is emitted.
* the three `test_gui_tree_view.py` source-sync cells extended to the new key, in the same commit as the C# change.

**In-game (`Source/Parsek/InGameTests/`).**

* one `GuiMock` category cell per supported window: apply a known state, assert the window drew the mocked row set (via the same tree the recorder builds), restore, assert the real model is back. That is the only place the suppression-plus-draw path can be proven, and it is what a `dotnet test` cannot reach.
* one cell asserting a `SaveGame` attempt during a live session refuses, and one asserting a scene change clears the session.

**Live proof.** The lane itself: `GUI-13` green with `failed=0`, and the mirror showing the mocked dataset badged next to the real one. Until it flies, the spec pins `total=` exactly and regexes the rest.

---

## 14. Phased build plan

Sizes are production plus tests, rounded. "States" is against the audit's own tables.

| Phase | Scope | Size | States unlocked | Gate to the next phase |
| --- | --- | --- | --- | --- |
| **P0** | this doc, the mirror pointer, the todo entry | docs only | 0 | owner answers section 16 |
| **P1** | the spine: `GuiMockState` / `GuiMockPayload` / `GuiMockCatalogue`, the `op=mock` primitive with its full refusal table, the `GuiMockSession` + suppression predicate, the dump `mock` block, the mirror's `fixture=mock` + badge + index fields, the completeness guard for enums. Windows: **Kerbals** (0 lines of injection), **Career State** (~15), **Structure List** (~10) | ~700-900 | ~45 (Kerbals 15, Career 23, Structure 16 minus overlap) | `op=mock` green in-game for three windows; a mocked capture badged in the mirror |
| **P1b** | **the harvest cap**: raise `ARTIFACT_MAX_SCREENSHOTS` (and re-check `ARTIFACT_MAX_SCREENSHOT_BYTES`) in `harness/lib/hlib.py:9496-9497` with its `test_hlib` cells. Without this a run harvests 32 states and drops the rest into `skipped_over_cap` | ~50 | unblocks every later phase | a 300-file harvest with `screenshotsSkipped = 0` |
| **P2** | `GalleryRun` batch verb + `GUI-13-gallery-mock` (KSC) and `GUI-14-gallery-mock-flight` lanes + the mirror's notes textarea, Export-notes blob and pinned default dataset | ~450-550 | 0 new, but this is the phase that makes the loop exist | one full lane under 8 minutes; the owner completes one round of step 6-7 |
| **P3** | **Logistics**: extract `RouteLegibility` (`:253-346`) into a public row model, five injection seams (`:598`, `cachedCandidates`, `cachedNearMisses`, `legibilityCache`, `:781`), pin the three wall-clock refreshers, the three detail-panel resolver seams, reason-clause constants (11.3), and the string half of the completeness guard | ~600-800 | **~70** - the single largest win in the program | 17 holds + 12 rejects + 6 statuses + both badges photographed |
| **P4** | **Timeline** entries (~60) and then row actions (~120) with synthetic `Recording` seeding; the `ComputeERS` slider-bounds seam at `:931` | ~350-450 | ~28 | supersede strikethrough and the archived marker photographed |
| **P5** | **Settings** 5 live-cell handles, **Test Runner** interface seam (running / failed / skipped), **Spawn Control** candidate + UT override + auto-close bypass, **Group Picker** model override | ~450-550 | ~35 | - |
| **P6** | option B: five mechanical `ScenarioWriter` entry points over codecs that already ship (`MISSION`, `ROUTE` + `DORMANT_ROUTES`, `KERBAL_SLOTS`, `HIDDEN_GROUPS`, supersede + tombstone `ENTRY`) plus injector presets, spliced onto HARVESTED saves per the `SAVE-AUTHORED-PROGRESS-NODE-DOES-NOT-RESTORE` caution, for **Missions** and **Recordings**. Ledger rows stay derived on load; settings stay seam-only (structurally out of reach) | ~400-600 | ~60 (sort states still need an `op=sort`) | - |
| **P7** | housekeeping the audit already earned: retire the 4 stale and 4 empty-hover labels, index the one missing real state (`bdk-kerbals-roster-standin-chain-advanced`), mark the three mislabelled captures | ~100 | corrects ~8 false-coverage rows | - |

P1 and P2 together are the minimum viable loop: three windows, ~45 states, one lane, before/after in the browser with notes coming back. P3 is where the coverage argument is won.

---

## 15. Risks

| Risk | Mitigation |
| --- | --- |
| A mocked state teaches the owner about a picture the product cannot produce | every builder feeds the real pure helper rather than writing the rendered string; section 11.2's guard proves the string is reachable; where a real capture of the same state exists the mirror shows both and Compare measures them |
| A live cell (section 6) silently comes from the wall clock or the live store, so the owner judges a half-mocked picture | each state names its live cells in `Note`; states whose live cells are load-bearing are not admitted until the phase pins them (E10); the `Planetarium` reads in Spawn Control `:240` and Timeline `:1108` are exactly this case |
| A mock reaches a save | structural: nothing injected is read by any writer (7.5 layer 1), plus a grep gate on the applier's write-set, plus the `SaveGame` refusal |
| The suppression predicates leak into player builds | the session can only be created by the armed seam; a unit cell asserts each site is inert with no session; a source gate asserts the site set equals the applier's claim |
| Catalogue rot: a new draw branch with no state | section 11, which reds in the same build |
| The dump's new key breaks the offline viewer or the fidelity tool | additive key, absent means real, and the three `test_gui_tree_view.py` source-sync cells are updated in the same commit |
| ~300 mocked captures swamp the mirror's 16 MB budget | the generator already refuses over budget and `--no-photos` cuts about two thirds; the gallery page can also be generated per window group |
| The harvest silently drops most of a gallery run | P1b raises the count cap; a `test_hlib` cell pins the new value, and the lane asserts `screenshotsSkipped = 0` |
| The mock dataset silently becomes the mirror's default view | the default stops being derived (7.6); pinned to a real fixture with `mock` as an explicit choice |
| Two labels collide inside one 300-state run, and one capture overwrites the other | nothing in the harness validates within-run uniqueness, so the catalogue unit cell does (7.6) |
| A gallery capture changes `superSize` or the screen metrics and the mirror silently samples no colours | the lane sets neither; the PNG-vs-`screen` equality is the mirror's own precondition (`gui_mirror.py:1246-1257`) |
| A gallery lane re-provisions the automation instance under a sibling session | unchanged from today: the machine lock, and gallery lanes are operator-tier, run on request only |
| Logistics' 180-280 lines land on a hot window | the five seams are `??` reads and three early returns; no per-frame cost when no session exists, which a unit cell pins |

---

## 16. Owner rulings (2026-09-21)

All seven were put to the owner as yes/no questions and ALL SEVEN were answered YES on 2026-09-21 ("yes to all 7, do as you think is best"). They are kept in question form below so the reasoning stays readable; each is now a decision, and P6 (question 4) is therefore in scope.

1. **Same page?** Mocked captures live in the SAME mirror as real ones, badged `MOCKED DATA` and never paired with a real capture in Compare - rather than a separate page. Yes/no.
2. **One capture per apply?** A mock stays live across a small sweep (e.g. all four Timeline tabs) rather than re-applying per capture. Cheaper, slightly more state to reason about. Yes/no.
3. **Reason constants?** Extract the 17 Logistics hold clauses and 12 reject clauses into named `internal const string` formats (no behavior change) so the completeness guard is mechanical rather than best-effort. Yes/no.
4. **Ship P6?** Build the generator surfaces so the Missions and Recordings windows get mocked states too - or accept that those two stay real-save-only (their row identity is an index into the live store by design). Yes/no.
5. **Notes as a clipboard blob?** Your per-state comments come back as a JSON/markdown blob you copy out of the page and paste into chat, with no server. Yes/no - if no, a downloaded file instead.
6. **Operator tier only?** The gallery lanes stay `tier = "operator"`, run on request, never in daily/nightly. Yes/no.
7. **Retire the false-coverage labels?** In P1, drop the 4 stale and 4 empty-hover labels from the mirror so they stop reading as coverage. Yes/no.

---

## 17. Code layout

| Concept | Implementation |
| --- | --- |
| Catalogue and its entries | `Source/Parsek/UI/Gallery/GuiMock*.cs` (new) |
| The `op=mock` parse and refusal tokens | `Source/Parsek/TestCommands/TestCommandUiAction.cs` (extend), pure |
| The applier and the session | `Source/Parsek/TestCommands/ParsekTestCommandAddon.UiMock.cs` (new), `GuiMockSession.cs` (new) |
| The batch verb | `Source/Parsek/TestCommands/TestCommandGalleryRun.cs` + `ParsekTestCommandAddon.GalleryRun.cs` (new) |
| Provenance in the dump | `Source/Parsek/GuiTreeJson.cs` (header + writer), `GuiTreeRecorder.cs:1568-1583` (populate) |
| Cache suppression | one predicate read at each site named in 7.4 |
| Mirror integration | `harness/tools/gui_mirror.py` (`scan_shots_dir`, the badge, `build_index`) |
| Harvest cap | `harness/lib/hlib.py:9496-9497` (raise), plus its `test_hlib` cells |
| Lane | `harness/scenarios/GUI-13-gallery-mock.toml`, `GUI-14-gallery-mock-flight.toml` (new) |
| Guards | `Source/Parsek.Tests/GuiMock*Tests.cs` (new), `harness/lib/test_gui_tree_view.py` + `test_hlib.py` (extend) |
