# Parsek GUI inventory - the shipped surface, and what the backend exposes through it

Measured 2026-09-11 against commit `4eb427e9e`. All `file:line` references are relative to
`Source/Parsek/` unless they start with `harness/`, `docs/` or `scripts/`.

## 1. Purpose and scope

This document is the structural map of Parsek's player-facing surface as it exists today:
which windows there are, what each one draws, which control reaches which backend, what the
Basic/Advanced gate actually hides, and which of the backend's 580 catalogued capabilities a
player can reach at all.

It is a MEASUREMENT, not a proposal. Nothing here recommends a redesign, a new surface or a
reworked flow; the analysis phase comes after, and it starts from this file. The one forward
looking section is section 7 (coverage plan), which is tooling work: how to photograph the
surfaces the census could not reach.

Load this before touching any file under `UI/`, `ParsekUI.cs`, the dialog producers, or the
overlay layer. The two raw inventories it condenses are committed beside it and hold the full
per-surface detail:

| Research note | Holds |
|---|---|
| `docs/dev/research/gui-inventory-2026-09-11.md` | 5725 lines: every surface with its (a) kind, (b) class + draw method, (c) scenes + gate, (d) structure, (e) controls and backends, (f) state variants and pictures, (g) sizing. Appendix 1 the gate keys, appendix 2 all 95 screen messages, appendix 3 the tooltip-only information, appendix 4 the unphotographed variants with the cheapest route to a picture, appendix 5 a gate cross-check |
| `docs/dev/research/gui-feature-exposure-2026-09-11.md` | 1155 lines: 580 capability rows across 9 subsystems, each classed EXPOSED / AUTOMATIC / HIDDEN / PROMISED-NOT-DELIVERED / DEAD, plus consolidated lists A (hidden), B (promised-not-delivered, P1-P22), C (dead, D1-D18) and D (UI information with no backend truth) |

Adjacent design authorities, which own their own slices and are not restated here:
`design-ui-basic-advanced.md` (the complexity-mode decision), `design-gui-tree-dump.md` (the
recorder), `design-autotest-command-seam.md` (the verbs), `design-map-ts-render-architecture.md`
(the map/TS render layer), `docs/user-guide.md` (the shipped player documentation).

Every count below is the size of a program-wide grep, not a hand tally. The six that carry the
structure:

| grep over `Source/Parsek` | result |
|---|---|
| `ClickThruBlocker.GUILayoutWindow(` / `GUILayout.Window(` / `GUI.Window(` | 16 sites: 15 player-facing hosts drawing **14 distinct windows**, 1 test-only probe at `InGameTests/GuiTreeDumpImguiTest.cs:484` |
| `PopupDialog.SpawnPopupDialog(` excluding `InGameTests/` + `TestCommands/` | **21** modal dialogs |
| `void OnGUI()` in production | **6** hosts: `ParsekFlight.cs:2087`, `ParsekKSC.cs:227`, `ParsekTrackingStation.cs:350`, `CurrencyReservationOverlay.cs:87`, `OverlayBadge.cs:122`, `InGameTests/TestRunnerShortcut.cs:177` |
| IMGUI draw calls outside `UI/` | 7 files: `CurrencyReservationOverlay.cs`, `MapMarkerRenderer.cs`, `OverlayBadge.cs`, `ParsekFlight.cs`, `ParsekKSC.cs`, `ParsekUI.cs`, `WatchModeController.cs` |
| `UiSurfaceVisibility.IsVisible(` | **12** call sites in 5 files; 4 of the 14 enum keys have none |
| `ParsekLog.ScreenMessage` / `ScreenMessages.PostScreenMessage` | 107 raw, **95** real producers |

## 2. How the picture was taken

**The tooling.** Two operator-tier harness lanes drove one career save through the M-A2
command seam: `GUI-1-census-ksc` (SPACECENTER, 23 labels) and `GUI-2-census-flight` (FLIGHT,
4 labels), whose results are `harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/` and
`harness/results/2026-09-11_0551_GUI-2-census-flight_shots/` (PNG plus `.gui.json` per label,
plus `gui-tree-index.html`). Three verbs did the work, and their vocabulary is the ceiling on
what a census can reach: `UiAction` with `op=complexity|open|close|rect|tab|describe`
(`TestCommands/TestCommandUiAction.cs:330-430`), `CaptureScreenshot label=` (framebuffer PNG,
`TestCommands/ParsekTestCommandAddon.CaptureScreenshot.cs:50`), and `DumpGuiTree label=`
(`parsek-gui-tree/1` control tree). The tree dump is one Repaint pass seen through 17 patched
IMGUI funnels (`GuiTreeFunnels.cs:10-32`), all of them reached from the single `GUI.DoWindow`
patch that catches every `GUI.Window` overload, `GUILayout.Window` and
`ClickThruBlocker.GUILayoutWindow` (`Patches/GuiTreeRecorderPatches.cs:224-227`, binding at
`GuiTreeFunnels.cs:112-118`); both runs report `patched=17/17`, `recordFaults=0`,
`droppedOverCap=0`. The mechanism, its counters and its limits are owned by
`design-gui-tree-dump.md`; the verb contracts by `design-autotest-command-seam.md`. There is
NO verb that clicks a control, expands a row, selects a row, or moves the pointer - which is
why the window table names three deliberate exclusions (`TestCommands/TestCommandUiAction.cs:369-380`:
`GroupPickerUI` and the Logistics link picker are popups over a selection nothing can arm,
`TestRunnerShortcut` is a separate MonoBehaviour with no accessor).

**The host.** Both lanes ran the operator-local fixture `fixtures/local-saves/c1-gui`
(`harness/scenarios/GUI-1-census-ksc.toml:34-44`), chosen for density: a 42 MB long-lived
career with 704 recording sidecars and 12 vessels. Screen 1280x720.

**What each capture layer can and cannot see.** The blind spots are structural, not accidental.

| layer | example | in the `.gui.json`? | in the PNG? |
|---|---|---|---|
| IMGUI inside a `GUI.Window` callback | every window and tab in section 3 | YES | yes |
| IMGUI outside any window | watch overlay, ghost map markers, currency tooltip, in-world ghost labels | NO | yes, if drawn |
| uGUI (`PopupDialog`, `RawImage` badges, the stock toolbar button) | all 21 dialogs, the R&D / Astronaut / Mission Control badges, the ApplicationLauncher button | NO | yes, if raised |
| hover-dependent IMGUI inside a window | the `TooltipEchoBox` strip's TEXT, `DisabledHoverEcho` reasons | node captured, always EMPTY | only with a parked pointer |

Colour is a third blind spot: the tree carries no colour field, so a tint (the red/cyan
Logistics launcher, the amber pending rows, the phase colours) is PNG-only evidence.

**Coverage.** The two runs produced 27 labels; one (`parsek-guitree-probe`) is the recorder's
own self-test window, leaving **26 player-facing captures**.

| kind | photographed | not photographed |
|---|---|---|
| windows | **10 of 14** | Logistics round-trip link picker, Real Spawn Control, the global Ctrl+Shift+T Test Runner, the Group picker |
| tabs | **12 of 12** | none |
| modal dialogs | **0 of 21** | all 21 |
| overlays / markers / badges | **0 of 7** | all 7 |
| tooltip surfaces in a USEFUL state | **0 of 2** | the echo strip is in every window capture but always empty; `DisabledHoverEcho` paints nothing by design |
| screen messages | **0 of 95** | all 95 |

So the census covers **22 of 105** countable surfaces (windows + tabs + dialogs +
overlays/markers/badges + tooltip surfaces + the toolbar button), and of the screen-message
and in-window-section layers only what happened to be on screen. Of the 26 captures, **8
photograph an essentially empty surface** by node count: `ksc-kerbals-roster-advanced` (9),
`ksc-structure-advanced` (3), `ksc-timeline-refly-advanced` (39), `ksc-timeline-basic` (39),
`ksc-career-contracts-advanced` (17), `ksc-career-strategies-advanced` (17),
`ksc-logistics-advanced` (58) and `ksc-logistics-basic` (58); and the Recordings tab's 415
nodes are 16 collapsed group headers with zero leaf rows.

Three causes account for every gap, and only the first is a fixture problem: the fixture had no
data of that shape (Career contracts, Kerbals roster, Logistics routes, STASH); the state needs
a CLICK and no verb clicks (every expanded row, every detail panel, the Group picker, the link
picker, populated Structure); or the surface is outside the recorder's reach by construction
(all 21 dialogs, all 7 overlays, every populated tooltip). Section 6 is organised by those
three, with the cheapest route per target.

## 3. The structure

### 3.0 Window index

The 14 distinct IMGUI windows, in the main window's own button order - the order
`TestCommands/TestCommandUiAction.cs:361-364` pins for the same reason.

| # | title | class + host line | scenes | seam token | census labels |
|---|---|---|---|---|---|
| 1 | `Parsek` (main) | `ParsekUI.cs:743`; hosts `ParsekFlight.cs:2118`, `ParsekKSC.cs:245` | FLIGHT, SPACECENTER | `main` | `ksc-main-basic/advanced`, `flight-main-basic/advanced` |
| 2 | `Parsek - Missions` | `UI/RecordingsTableUI.cs:651` | FLIGHT, SPACECENTER | `missions` (tabs `missions`, `recordings`) | 4 labels (3.2) |
| 3 | `Parsek - Timeline` | `UI/TimelineWindowUI.cs:281` | FLIGHT, SPACECENTER | `timeline` (tabs `overview`, `details`, `rewindff`, `refly`) | 5 labels (3.3) |
| 4 | `Parsek - Kerbals` | `UI/KerbalsWindowUI.cs:205` | FLIGHT, SPACECENTER | `kerbals` (tabs `roster`, `outcomes`) | 2 labels |
| 5 | `Parsek - Career State` | `UI/CareerStateWindowUI.cs:1211` | FLIGHT, SPACECENTER | `career` (4 tabs) | 4 labels |
| 6 | `Parsek - Logistics` | `UI/LogisticsWindowUI.cs:444` | FLIGHT, SPACECENTER | `logistics` | `ksc-logistics-advanced/basic` |
| 7 | Logistics round-trip link picker | `UI/LogisticsWindowUI.cs:1762` | as its host | excluded `TestCommandUiAction.cs:374-377` | NONE |
| 8 | `Parsek - Structure` | `UI/StructureListWindowUI.cs:174` | FLIGHT, SPACECENTER | `structure` | `ksc-structure-advanced` (empty chrome) |
| 9 | `Parsek - Settings` | `UI/SettingsWindowUI.cs:128` | FLIGHT, SPACECENTER | `settings` | `ksc-settings-advanced/basic` |
| 10 | `Real Spawn Control` | `UI/SpawnControlUI.cs:162` | FLIGHT only | `spawncontrol` | NONE |
| 11 | `Gloops Flight Recorder` | `UI/GloopsRecorderUI.cs:94` | FLIGHT only | `gloops` | `flight-gloops-advanced` |
| 12 | `Parsek - Test Runner` (Settings-launched) | `UI/TestRunnerUI.cs:122` | FLIGHT, SPACECENTER | `testrunner` | `ksc-testrunner-advanced` (4217 nodes) |
| 13 | `Parsek - Test Runner` (global Ctrl+Shift+T) | `InGameTests/TestRunnerShortcut.cs:204` | ANY scene | excluded `TestCommandUiAction.cs:378-380` | NONE |
| 14 | `Set Parent Group` / `Manage Groups` | `UI/GroupPickerUI.cs:224` | as its host | excluded `TestCommandUiAction.cs:369-373` | NONE |

Two asymmetries in that table are mechanical facts, not presentation choices:

- Window 13 is the ONLY one that calls raw `GUILayout.Window` instead of
  `ClickThruBlocker.GUILayoutWindow` (`InGameTests/TestRunnerShortcut.cs:204`), so it is the
  only Parsek window with no click-through protection; it compensates with its own
  `windowRect.Contains(Event.current.mousePosition)` input lock at `:213`.
- The TRACKING STATION hosts no Parsek window at all. `ParsekTrackingStation.cs:350` has an
  `OnGUI`, and its whole body is the pause gate plus `DrawAtmosphericMarkers()`; the source
  says so in place at `:394-395`, and `UiAction` answers `REJECTED ui-host-unavailable` there.

House layout rules that hold across every window: the footer order is tooltip echo strip, then
`Close` as the last content row, then the resize handle and `GUI.DragWindow()`; and window
BODIES are never complexity-gated - only launchers, content draw sites and the mode-change
close handler are (`UI/UiComplexityMode.cs:124-126`, rationale `ParsekUI.cs:296-300`).

### 3.1 Parsek (main window)

Purpose: the mod's single entry point. Its body IS the launcher column; there is no row model.

Hosts and gates: FLIGHT + MAPVIEW (`ParsekFlight.cs:2087`, gated `!PauseMenuGate.IsPauseMenuOpen()`
then `showUI` then a non-null opaque style) and SPACECENTER (`ParsekKSC.cs:227`, same three gates
in the opposite order). `showUI` is written only by the toolbar handlers
(`ParsekFlight.cs:1352-1353`, `ParsekKSC.cs:155-156`) and the Close handler. No `UiSurface` key
gates the window itself. The flight host draws 10 sub-windows (`:2132-2141`), the KSC host 8
(`ParsekKSC.cs:254-261`) - Spawn Control and Gloops are flight-only.

Contents, top-down (`ParsekUI.cs:745-986`): flight status block (FLIGHT only, `:747`), the
launcher column, the supply-route banner when armed, the tooltip echo strip (`:977`), a
version label and `Close` (`:979-986`).

| launcher | tooltip | line | gate | action |
|---|---|---|---|---|
| `Real Spawn Control ({N})` | `Turn a recorded craft passing nearby into a real vessel.` | `:775` | `InFlight && IsVisible(MainButtonSpawnControl)` `:770` | toggles `spawnControlUI.IsOpen` `:784`; greyed when `NearbySpawnCandidates.Count == 0`, reason `No recorded craft is passing nearby` (`:142`) carried by `DisabledHoverEcho` `:780` |
| `Timeline` | `Every recorded flight and career event on one clock.` | `:794` | none | `timelineUI.IsOpen` `:798` |
| `Missions` | `Your missions, and the recordings they are built from.` | `:806` | none | `ToggleRecordingsWindow()` `:809` |
| `Logistics` | `Supply routes that repeat a delivery you already flew.` | `:885` | none | `logisticsUI.IsOpen` `:889`; tinted red when `LogisticsButtonState.AnyRouteHardBroken` (`UI/LogisticsButtonState.cs:27`), else cyan while a route prompt pends (`:874-884`, broken outranks hint) |
| `Kerbals` | `Who is reserved, flying or retired in your timeline.` | `:914` | `IsVisible(MainButtonKerbals)` `:904` | `kerbalsUI.IsOpen` `:918` |
| `Career` | `Contracts, strategies and buildings along the timeline.` | `:925` | `IsVisible(MainButtonCareer)` `:906` | `careerStateUI.IsOpen` `:929` |
| `Gloops Flight Recorder` | `Record a ghost-only flight that your career ignores.` | `:943` | `IsVisible(MainButtonGloops)` `:941` - **never true** (`UI/UiComplexityMode.cs:140-143`, short-circuit `:162`) | would flip `gloopsUI.IsOpen` `:947` |
| `Settings` | `Recording, looping, ghost and diagnostic options.` | `:955` | none | `ToggleSettingsWindow()` `:958` |

Basic vs Advanced: Basic drops Real Spawn Control, Kerbals and Career; the `Space(10f)` that
opens the Kerbals/Career group is gated with it (`:908-909`) so Basic shows one gap, not two.
Census-verified sets: KSC Basic 4 buttons, KSC Advanced 6, FLIGHT Basic 4, FLIGHT Advanced 7.

Flight status block (`ParsekUI.DrawFlightStatus` `:995`): five labels, `State:` from
`GetStatusText()` (`:2699`) with four values `Idle` / `RECORDING` / `PREVIEWING` /
`Ready (has recording)`, `Recorded Points:`, an optional `Duration:` line when points exist,
and `Active Ghosts:`. Only the `Idle` / zero-points / zero-ghosts variant has a picture
(both flight labels).

State variants without a picture: the RouteRunPrompt banner (`:817-855`, `Open Logistics` /
`Dismiss`), either Logistics tint, an enabled Real Spawn Control, any non-`Idle` status, and a
populated tooltip strip.

### 3.2 Parsek - Missions (chrome, Missions tab, Recordings tab)

Purpose: two views of the same recorded data - missions as whole units, and the raw
per-recording table. Chrome and the Recordings tab live in `UI/RecordingsTableUI.cs`; the
Missions tab body in `UI/MissionsWindowUI.cs`.

Chrome (`UI/RecordingsTableUI.cs:615-655`): title `Parsek - Missions`, a two-entry
`GUILayout.Toolbar` (`:1551`) drawn only when `VisibleTabCount(complexity) > 0` (`:1549`),
tab content, then the per-tab bottom bar. Default tab `TabMissions`, transient. Input lock
`Parsek_RecordingsWindow` on CAMERACONTROLS while the mouse is inside (`:673-684`).

Basic/Advanced: the single `IsVisible` call in these files is `:189` inside `VisibleTabCount`,
which returns 2 in Advanced and **0** in Basic - deliberately zero, not one, because a
one-entry toolbar is noise and the title already carries the identity (`:174-186`). Three
consumers: `ClampTabIndexForMode` (`:202`), the tab-bar draw guard (`:1549`), and a content
dispatch pin (`:1562`). So Basic hides the Recordings tab button and with it the whole table,
the Info / New Group footer buttons, and every route to the group picker - which is why
`CloseGroupPickerForModeChange` (`:231`) exists.

**Missions tab.** Row model: one row per physical vessel or EVA kerbal, built by
`MissionVesselRowBuilder.Build` (`MissionVesselRows.cs:59`); depth is SEPARATION LINEAGE only,
never time (`:118-123`); roster atoms are not rows (`:99-101`).

| # | header | width const | value on a mission header row | on a vessel row |
|---|---|---|---|---|
| 1 | (blank enable slot) | `ColW_Enable` 20 | blank | blank |
| 2 | `#` (sortable) | `ColW_Index` 30 | per-TREE index | include checkbox (Advanced) or blank |
| 3 | `Missions and vessels` (sortable) | expand | mission title button | connector + caret + name + `EventPhrase` |
| 4 | `Next launch` | `ColW_TMinus` 105 | countdown | countdown on the launch row only |
| 5 | `Start time` (sortable) | `ColW_StartTime` 120 | - | `PrintDateCompact` |
| 6 | `Start event` | `ColW_StartEvent` 110 | - | event word, dock-partner named when resolvable (`:1937`) |
| 7 | `End event` | `ColW_EndEvent` 85 | - | terminal word |
| 8 | `End time` | `ColW_EndTime` 120 | - | date |
| 9 | `Re-Fly` | `ColW_ReFly` 90 | - | Fly / Seal cell (`UI/RecordingsTableUI.cs:3411`) |
| 10 | `Archive` + global toggle | `ColW_Archive` 80 | per-mission checkbox | margin-0 spacer |

Mission header bar controls (`UI/MissionsWindowUI.cs:2447`), with their gates:

| control | backend | disabled / hidden |
|---|---|---|
| title double-click | `CommitMissionRename` (`:3859`) -> `MissionGroupLink.RenameMissionGroup` (`MissionGroupLink.cs:57`) for an original, `MissionStore.RenameMission` for a clone | a group-name collision refuses the whole rename, Warn-only (`:3879`) |
| `Log` | `ParsekUI.OpenStructureWindowForMission` (`ParsekUI.cs:1057`) | never |
| `Clone` | `MissionStore.Clone` | HIDDEN in Basic (`:2505-2509`) |
| `Delete` | `MissionStore.Delete` | greyed by `CanDelete`; reason is the constant `A flight always keeps its first mission` (`:2908`). Kept in Basic |
| `Warp to...` | confirm dialog then a forward jump (`:2993`) | `ShouldEnableWarpToWindow` (`:3055`); four ordered reasons (`:2930`). Not Basic-gated |
| `Loop` + toggle | `CommitMissionLoopToggle` (`:2756`) -> `MissionStore.SetLoopEnabled` | HIDDEN in Basic; greyed when `RouteTreeGuard.RouteBindingFor(treeId)` |
| loop-period cell | `CommitMissionLoopPeriod` (`:4208`), four states (locked / auto / manual / editing) at `:3995` | HIDDEN in Basic; an open edit is DROPPED uncommitted on a Basic switch (`:772-784`) |
| `Watch` / `W*` | `flight.EnterWatchMode` / `ExitWatchMode` | two reasons at `:2918`. Not Basic-gated |
| `Rewind` / `Forward` | `UI/RecordingsTableUI.cs:3363` over the mission root recording | greys on `CanRewind` / `CanFastForward` with the store's reason |
| Archive checkbox | writes `mission.Archived` | never |

The rest of the tab: expanded per-vessel interval rows (Advanced only, `:1547`), chapter group
header rows whose tri-state toggle is the one interval-writing control Basic does NOT hide
(`:1885`), `Docked partner:` rows plus the foreign partner-journey staircase (`:2032`), and the
`Events (N)` digest foldout with its `Go to` cross-link (`:2256`).

Pictures: `ksc-missions-missions-advanced` (922 nodes, 18 missions, every Loop off, every
period `10` / `sec`), `ksc-missions-basic` (813 - no tab bar, no Clone, no Loop, no period, no
row checkboxes), `flight-missions-missions-advanced` (922, structurally identical). No picture:
loop ON with a phase-locked read-only period, `Looped by route`, `Forward` instead of `Rewind`,
`W*`, inline rename, `(partial)` / dimmed rows, `Docked with <partner>` in the Start event
cell, Fly/Seal, expanded intervals, chapter headers, partner rows, an expanded digest.

**Recordings tab.** A fixed 21-cell header (`:1196`) outside the scroll view, then a body of
four row kinds. Columns, left to right, with the shared header/body width constants: merged
[toggle + `#`] 58 (`ColW_Enable` 20 + `ColW_Index` 30 + 8), `Name` expand, `Phase` 90,
`Site` 90, `Launch` 110, `Duration` 80, then the Info-only block `MaxAlt` 65 / `MaxSpd` 65 /
`Dist` 65 / `Pts` 35 / `Start` 120 / `End` 120 (drawn only when `showExpandedStats`, `:1262`),
`Status` 120, `Group` 60, `Loop` 60, `Period` 90, `Watch` 50 (flight only, `:1346`),
`Rewind` 60, `Re-Fly` 90, `Archive` 80, plus a scrollbar-width spacer (`:1384`). Every header
cell is forced to `ColHeaderHeight = 32` (`:73`). Six headers are sortable (`#`, Name, Phase,
Site, Launch, Duration, Status); `SortColumn.LaunchTime` ascending is the default (`:135`).

Row kinds and what each blanks: GROUP HEADER (`DrawGroupTree` `:2399`, Period and Re-Fly always
blank), CHAIN BLOCK and GROUPED BLOCK (`:4038` / `:4048` into `DrawRecordingBlock` `:4057`;
Phase, Period, Watch, Re-Fly, Archive blank), VIRTUAL STASH group (`:3036`; Group, Loop, Period,
Watch, Rewind, Re-Fly blank), and the RECORDING leaf (`:1820`, all columns). There is no
separate detail row: expansion is a caret that renders CHILD rows of the same kinds, with
box-drawing connectors (`:2283`) and per-level indents.

Notable control semantics in the body:

| control | backend | gate |
|---|---|---|
| per-row Enable toggle | `rec.PlaybackEnabled` | never disabled. See finding P1 |
| header select-all Enable | writes every committed recording (`:1225`) | never; ignores the filters, and the tooltip says so |
| Loop toggle (row) | `rec.LoopPlayback` + `ApplyAutoLoopRange` (`:2057`) | blank when `ShouldSuppressRowLoopUi` (`:5544`); greyed and write-blocked when route-bound (`:2049`) |
| Loop toggle (header / folder) | `BulkSetLoopPlayback(..., applyAutoRange: false)` (`:1336`, `:2643`) | same route gate; deliberately does NOT re-narrow the window (`:5616-5630`) |
| Loop toggle (chain / block) | `BulkSetLoopPlayback(..., applyAutoRange: TRUE)` (`:4207`) | same glyph, different semantics, by design |
| `Archive` header toggle | `GroupHierarchyStore.HideActive` (`:1377`) | the one header toggle that writes NOTHING to a recording; it is a view filter |
| per-row Archive toggle | `rec.Hidden` + `NotifyTimelineOfArchiveChange` | REFUSED with a Warn + ScreenMessage when the row is an Unfinished Flight (`:2158-2167`) |
| folder Archive toggle | writes every descendant `Hidden` (`:2860`) | never. See finding P17 |
| `G` | `groupPicker.OpenForRecording / ForChain / ForRecordings / ForGroup` | never disabled; adds later rejected by `CanAddToUserGroup` (`UI/GroupPickerUI.cs:24`) |
| `X` on a row | `DeleteGhostOnlyRecording` (`:4570`), NO confirmation | only when `rec.IsGhostOnly && Mode != TrackingStation` (`:1982`, `:4611`) |
| `X` on a folder | `ShowDisbandGroupConfirmation` (`:4399`) | only for a non-permanent group |
| `W` / `W*` | `flight.EnterWatchMode(ri)` | `IsWatchButtonEnabled` (`:947`); column hidden outside flight |
| `FF` / `R` | `ShowFastForwardConfirmation` (`:4496`) / `ShowRewindConfirmation` (`:4450`) | `RecordingStore.CanFastForward` / `CanRewind`, refusal as tooltip; `R` is suppressed entirely on an unfinished-flight row (`:3754`) |
| `Fly` + `Seal` / `Stash` + `Seal` | `RewindInvoker.ShowDialog` / `UnfinishedFlightSealHandler.ShowConfirmation` / `UnfinishedFlightStashHandler.TryStash` | `ResolveReFlyColumnAction` (`:3416`, `:3420`) |
| footer `Info >` / `Info <` | flips `showExpandedStats`; forces the window to 1813 px on expand and back to 1355 on collapse (`:1418-1423`) | only when the list is non-empty |

Pictures: `ksc-missions-recordings-advanced` (415 nodes, 16 collapsed group rows, `Status`
values `past` / `Destroyed` / `Splashed` / `Landed` / `Orbiting`, all 16 folder `R` buttons
enabled with resolved targets). No picture: every leaf row, every chain block, STASH, the
Info-expanded columns, the Watch column, route-bound greyed Loop toggles, any inline rename,
the time-range filter strip.

**Group picker** (window 14, `UI/GroupPickerUI.cs:193-307`): title `Set Parent Group` in
group-parent mode else `Manage Groups`; a `(None / Root level)` toggle, a recursive checkbox
tree that SKIPS any group in `treeModel.CycleInvalid` (`:315`), a new-group text field plus
`+`, then `OK` / `Cancel`. `ApplyGroupPopupChanges` (`:356`) has four exclusive branches
(group-parent, chain all-or-nothing, multi-recording per-recording, single recording). Removes
are never gated. No picture; no verb opens it.

### 3.3 Parsek - Timeline

Purpose: every recorded flight and career event on one clock, and the only access to rewind,
fast-forward and warp-to-time - which is why its launcher is deliberately kept in Basic
(`UI/UiComplexityMode.cs:171`).

Hosts: `ParsekFlight.cs:2133` and `ParsekKSC.cs:255`; not the Tracking Station. Input lock
`Parsek_TimelineWindow` (`:297-308`).

Structure (`:426-517`): a two-row filter bar, a time-range preset row with optional custom
sliders, the entry scroll view with a "now" divider, a warp row, the single-line echo strip,
`Close`, resize handle, drag.

Row model: one row per `TimelineEntry` surviving `IsEntryVisible` (`:1162`); the list is built
by `TimelineBuilder.Build` from `EffectiveState.ComputeERS()` + `ComputeELS()` +
`MilestoneStore.Milestones` (`:455-462`) and sorted by UT only. **There are no sort controls
and no header row** - chronological is the only order. Cells: a 160 px time label
(`TimeColumnWidth` `:70`), a 14 px gutter spacer, an expanding description label, then zero or
more right-aligned action buttons. Row colour is one of six cached styles (`:1269-1277`):
strikethrough when `!IsEffective`, dim when future, blue when `IsPlayerAction`, else green /
red / white.

Four mutually exclusive tier views (`TimelineTierFilterMode`, `:33-39`, default `Overview`):

| mode | row predicate | effect on the three source toggles |
|---|---|---|
| Overview | drops every `SignificanceTier.T2` row (`:1189`) | live |
| Details | no tier drop | live |
| Rewind/FF | keeps only rows where `HasActionableRewindOrFastForwardButton` (`:1576`) | forces Recordings on and DISABLES all three (`:803`) |
| Re-Fly | keeps only rows where `HasActionableFlyOrSealButton` (`:1587`) | same |

Both action tiers hide rows whose button would be GREYED, not merely absent.

Row actions: `W` / `W*` 40 px (flight only, `:1314`), `FF` 40 (future rows, `:1364`), `R` 40
(past rows, `:1381`), `Fly` + `Seal` 40 on `UnfinishedFlightSeparation` rows (`:1478-1519`;
Seal stays live when Fly is greyed, `:1681`), and `GoTo` 48 gated by
`IsVisible(UiSurface.TabMissions, ...)` (`:1265`) - the only `IsVisible` call in the file, and
it hides nothing today. FF and R are mutually exclusive on one row, so a `RecordingStart` row
carries at most W + one of FF/R + GoTo.

The rewind gate is five preconditions evaluated in this order, and the failing one's string
becomes both the tooltip and the disabled-hover echo: `Rewind already in progress`
(`RecordingStore.cs:5233`), `No rewind save available` (`:5240`), `Stop recording before
rewinding` (`:5247`), `Merge or discard pending tree first` (`:5254`), `Rewind save file
missing` (`:5219`). Precondition 2 also removes the button, so only 1, 3, 4 and 5 are reachable
as greyed states. The FF gate is a six-condition near-mirror (`RecordingStore.cs:5281-5340`).

Warp row: a 186 px `Warp to time` button plus four 36 px Y/D/H/M fields (`:563-582`); the plan
comes from `WarpToTimeMath.DecideWarpPlan` (`:547`) and the confirmation is a stock dialog
(`WarpToTimeController.cs:223`). In flight the warp NEVER executes in place - it is deferred to
the next Space Center arrival (`WarpToTimeController.cs:133-143`) so the scene-exit merge
dialog handles the live recording first.

The `Archived` toggle (`:876`) writes the INVERSE of `GroupHierarchyStore.HideActive`, the same
single flag the Recordings tab's Archive header checkbox writes - and because that tab is
Basic-hidden, this toggle is **the only archive control a Basic player can reach** (`:870-874`).

Pictures: `ksc-timeline-overview-advanced` (338 nodes, 22 `R` + 22 `GoTo`),
`ksc-timeline-details-advanced` (1154, 22 `R` + 34 `GoTo`), `ksc-timeline-rewindff-advanced`
(152, exactly the 22 actionable rows), `ksc-timeline-refly-advanced` (39, empty list - the save
has no resolvable `UnfinishedFlightSeparation`), `ksc-timeline-basic` (39). The Basic capture is
a Re-Fly view photographed in Basic: the tier mode is a plain instance field with no reset, the
Advanced pass ended on `tab=refly`, and the Basic pass issued no `op=tab` (`KSP.log:14869`,
`:15684`, `:15707`). It proves the Basic chrome is mode-identical; it does not show Basic rows.
No picture: any flight-scene state (so the whole Watch button), `FF`, `Fly` / `Seal`, the custom
sliders, any non-default preset, `Archived` ON, the countdown label, every disabled tooltip.

### 3.4 Parsek - Kerbals

Purpose: read-only. Who fills each crew slot now, and how every recorded flight a kerbal took
ended. It has no reserve / unreserve / swap / clear control and mutates nothing but two
transient fold sets (`UI/KerbalsWindowUI.cs:64`, `:70`); `CrewReservationManager` is not
referenced by the file at all.

Hosts: `ParsekFlight.cs:2134`, `ParsekKSC.cs:256`. Basic-HIDDEN at the launcher
(`ParsekUI.cs:904`, decision `UI/UiComplexityMode.cs:181`), and an Advanced -> Basic switch
force-closes it (`ParsekUI.cs:498-503`), so it has **no Basic picture by construction**.

Both tabs are two-level indented OUTLINES, not column tables: there is no `GUILayout.Width`
call in any row and no user-selectable sort.

| tab | row model | sort | empty state |
|---|---|---|---|
| `Roster State` | owner header `"{Name} [{Trait}] - {status}  ({N})"` (`:504`, `:522`), status `deceased` / `reserved` / `reserved until UT n` / `active`; expanded chain-member subrows `"    \- {Name} ({tag})"` with tag `active`/`retired`/`displaced` (`:592`, `:525`); tail section `Unlinked Retired (N)` (`:481`) | owner name, ordinal ascending (`:795`) | `No reserved crew, stand-ins, or retired kerbals.` (`:352`) |
| `Mission Outcomes` | per-kerbal fold header (folded adds `FormatKerbalSummary`, `:701`); per-recording subrow `"{name} - {Dead\|Recovered\|Aboard\|Unknown} at UT {n}"` (`:739`) | kerbal name then `EndUT` (`:894-899`) | `No committed crew history yet.` (`:364`) |

Controls: the tab toolbar, the owner fold button (drawn as a plain LABEL when the chain has no
expandable entries, `:445-450`), the per-kerbal fold, the per-recording row button (a cross-link
into `TimelineWindowUI.ScrollToRecording`, `:653`), `Close`, resize handle.

Pictures: `ksc-kerbals-roster-advanced` is **9 nodes** - one collapsed owner row
(`> Jebediah Kerman [Pilot] - deceased  (1)`), and it is NOT a picture of a populated roster.
`ksc-kerbals-outcomes-advanced` (24 nodes) is the better one: one unfolded kerbal with 15
subrows covering all four end states. No picture: an expanded chain, the orphan tail, the
reserved / active statuses, either empty state, a folded outcomes header, the flight scene.

### 3.5 Parsek - Career State

Purpose: read-only. What the career holds now beside what the recorded timeline still commits.
Nothing in it writes to contracts, strategies, facilities or milestones.

Hosts: `ParsekFlight.cs:2135`, `ParsekKSC.cs:257`. Basic-HIDDEN (`ParsekUI.cs:906`,
`UI/UiComplexityMode.cs:182`), force-closed on the switch (`ParsekUI.cs:492-497`).

Structure: a one-line mode banner (`:1401`, three forms - `Career mode - UT n` with an optional
`  (timeline ends at UT n)` divergence suffix, `Science mode - contracts and strategies
unavailable`, `Sandbox mode - career state is not tracked`), a four-entry tab toolbar, a scroll
view, the SINGLE-line echo strip, `Close`. A null-game fallback (`:1319-1333`) replaces the
entire body with one label plus `Close`.

| tab | columns (header = row width const) | sort | section header |
|---|---|---|---|
| Contracts | `Contract` 240, `Accepted UT` 90, `Deadline UT` 90, `Status` 70 | `AcceptUT` then id (`:1017`) | `Mission Control L{n} - slots a/b now, c/d at timeline end` (`:1442`) |
| Strategies | `Strategy` 220, `Activated UT` 90, `Flow` 140, `Status` 70 | `ActivateUT` then id (`:1024`) | `Administration L{n} - slots ...` (`:1548`) |
| Facilities | `Facility` 200, `Level` 120, `Status` 180 | NONE - fixed `FACILITY_DISPLAY_ORDER` (`:133-144`), all nine always shown | the bare word `Facilities` (`:1647`) |
| Milestones | `Credited UT` 90, `Milestone` 200, `Rewards` 180, `Status` 70 | `CreditedUT` then id (`:1031`) | `Milestones (n credited / m at timeline end)` (`:1699`) |

Contracts and Strategies each have two layouts chosen by a row-equality predicate (`:1446`,
`:1552`): COLLAPSED `Active (n)`, or SPLIT `Active now (n)` plus a `Pending in timeline (n)`
disclosure. Facilities is the only tab with no pending group and no Science-mode branch -
facilities and milestones ARE tracked in Science mode (`ModeHasVisibleTimelineState` `:972`),
contracts and strategies are CAREER-only (`:952`, `:957`). `ColW_PendingTag` (70, `:86`) is
shared by three tabs even though it is declared under the Contracts comment block.

Pictures: `ksc-career-contracts-advanced` (17 nodes, zero rows),
`ksc-career-strategies-advanced` (17, zero rows), `ksc-career-facilities-advanced` (50, all
nine rows at L1 with an empty Status column), `ksc-career-milestones-advanced` (143, 25
credited rows - the best-populated capture in the whole census, and the one that exposes the
`Rewards` overflow named in section 6). No picture: the split layout and its pending rows,
`(closing)`, `--` deadlines, populated strategies, upgraded or destroyed facilities, pending
milestones, the divergence suffix, Science or Sandbox mode, the null-game fallback.

### 3.6 Parsek - Logistics

Purpose: supply routes derived from flights already flown. Hosts `ParsekFlight.cs:2136`,
`ParsekKSC.cs:258`; not the Tracking Station.

Complexity: `UI/LogisticsWindowUI.cs` contains **zero** `IsVisible` calls, and
`UiSurface.MainButtonLogistics` has no call site anywhere. The window is identical in both
modes not because the gate says keep but because nothing asks - the two census trees are
byte-identical at 58 nodes each.

Six bubbles inside one scroll view (`:541-554`). Only THREE are caret disclosures with a count
badge - Dormant Routes (`:675`), `Recently committed trees not yet eligible` (`:777`) and
`Dismissed` (`:844`); Active, Paused and Candidates are drawn unconditionally with a plain
centred title (`:544-545`). Per-ROW expand is separate (`:979`, `:1487`).

Route table (Active and Paused share `DrawRouteSortableHeader` `:929` and `DrawRouteRow` `:968`;
both sections share one sort state, `:118-119`):

| # | column | width | value |
|---|---|---|---|
| 1 | `#` | 30 | display position; not sortable |
| 2 | Name | expand | caret + `route.Name` |
| 3 | Origin | 95 | `FormatOrigin` `:3664` |
| 4 | Destination | 180 | cached `leg.DestinationText`; coords in the tooltip |
| 5 | Interval | 150 | inline `[-] field [+] Nx` stepper (`:1163`); an EMPTY cell of the same width while Send-Once-armed (`:1012`) |
| 6 | Cyc | 80 | `"3"` or `"3 / 1 skipped"` (`:3738`) |
| 7 | Next | 135 | bare `T-` countdown or `-` (`:3586`) |
| 8 | Status | 240 | hold text or status reason, colour by `StatusStyleFor` `:3813` |
| 9 | Delivery | 120 | Delivering / Flying, not delivering / New (not yet run) / Paused |
| 10 | Actions | 190 | Pause 58 + X 22 (Active); Send Once 79 + Activate 64 + X 22 (Paused); or a 160 px disabled armed button |

The fixed columns total 1220 px, which is why the window's own `MinWindowWidth` is 1410.
There is no Transit column: it lives only in the Interval cell's `Nx` tooltip and the expanded
detail line (`:1023-1025`).

Candidates table (`:905`, rows `:1474`): `#` 30, Name expand, Origin 95, Destination 180,
`Would deliver` 260, `Transit` 80, Actions 190 (`Create Route` 100 + `Dismiss` 70). Its empty
state is a full sentence, not `(none)`: `No eligible Supply Runs. Fly a one-way transport that
docks, transfers cargo to the destination, and undocks, then commit and seal the recording.`
(`:743`).

The expanded route detail panel (`:1554-1670`) is where most of the window's real controls
live: `Rename`, `Log (Route)`, `Log (Mission)`, `Link round-trip...` / `Unlink`, cadence and
priority steppers, `Re-scan for endpoint`, plus up to fourteen conditional readout lines
(hold, partial, capacity, countdown branch, five recent-cycle flow lines, cost/run, source
recordings). The candidate detail panel (`:2300`) is three or four lines with NO controls.

The link picker (window 7, `:1741-1851`) is its own `GUILayoutWindow`, armed only from an
expanded route row's `Link round-trip...` button; it is a declared census exclusion
(`TestCommands/TestCommandUiAction.cs:373-376`).

Pictures: `ksc-logistics-advanced` and `ksc-logistics-basic` (58 nodes each, identical) show
the whole chrome, three section headers, two route sort headers, both `(none)` rows, the
candidate empty sentence, and the collapsed near-miss disclosure reading `(18)`. The census
forced the window to 1280 px - BELOW its own 1410 minimum, which is legal because `op=rect`
writes the field directly and only a resize DRAG clamps (`:436`) - so the shipped picture shows
a squeezed layout with a horizontal scrollbar and a Name column collapsed to 60 px. No picture:
any populated route or candidate row, any of the three disclosures expanded, any detail panel,
the link picker, any of the four dialogs.

### 3.7 Parsek - Structure (the Log)

Purpose: a read-only step list for one mission or one route. No sorting, no filters, no per-row
controls (`UI/StructureListWindowUI.cs:14-16`).

Opened by `OpenForMission` (`:83`, from the Missions tab `Log` button) or `OpenForRoute`
(`:94`, from the Logistics detail panel); one reusable instance, retargeted and rebuilt on each
open (`:107`). No complexity gate.

Columns: `#` 28, `Time` 110, `Event` expand, `Status` 95, `Location` 185, `Vessel` 140, plus a
reserved scrollbar gutter. Empty state is a single label (`Nothing to show.` for a mission
target, `Nothing to show (source recording unavailable).` for a route) plus `Close`, then an
early return that suppresses the header, the scroll view and the resize handle (`:223-232`).

Picture: `ksc-structure-advanced`, 3 nodes - window, label, button. That is the empty MISSION
wording, because `op=open window=structure` raises `IsOpen` without ever calling
`OpenForMission` / `OpenForRoute`, so `targetId` stays null and `Rebuild` never runs. The census
spec files that as a follow-up rather than faking it
(`TestCommands/TestCommandUiAction.cs:414-418`).

### 3.8 Parsek - Settings

Purpose: the nine settings that still have a control, the complexity toggle itself, and the
two destructive wipes. Six sections drawn top to bottom in one pass; no tabs, no scroll view.

Hosts: `ParsekFlight.cs:2138`, `ParsekKSC.cs:260`. `UiSurface.MainButtonSettings` is visible in
both modes by design - it hosts the mode toggle - and is deliberately left unwrapped at the
draw site (`UI/UiComplexityMode.cs:174`).

| # | section | gate | controls |
|---|---|---|---|
| 1 | Interface | always | `Basic` / `Advanced` buttons (`:488-493`) plus a hint label; `Basic` is greyed while a Gloops recording runs (`:475`, predicate `:505`) |
| 2 | Looping | `SettingsSectionLooping` (`:378`) | `Auto-launch every` label, a 45 px value field, a 40 px unit button |
| 3 | Ghosts | always | ghost-audio slider 0..1 with a 35 px percent label, and the ` Show supply route paths on map` toggle |
| 4 | Diagnostics | `SettingsSectionDiagnostics` (`:393`) | five tracing toggles, `In-Game Test Runner`, `Run Diagnostics Report`, and the rewind-point disk readout |
| 5 | Recorder Sample Density | `SettingsSectionSampleDensity` (`:401`) | `Low` / `Medium` / `High` plus a summary label |
| 6 | Data Management | always | `Wipe All Recordings (N)` (`:746`) and `Wipe All Game Actions (N)` (`:756`), each greyed at zero with a disabled-hover reason |

Footer: `Defaults` (`:416`, resets 9 values from `UI/SettingsWindowPresentation.cs:55-66`) and
`Close`. Each hidden section's trailing `Space` lives INSIDE its gate (`:381`, `:396`, `:404`),
so Basic shows no double gap.

Settings that no longer have a control at all (2026-08-27 simplification): `autoRecordOnLaunch`
/ `autoRecordOnEva` / `autoRecordOnFirstModificationAfterSwitch` and `autoMerge` are hidden
fields clamped `true` on load, `forceFaithfulLoopPlayback` clamped `false`
(`ParsekSettings.cs:38-42`, `:71`, `:251`, clamps `:290-297`); `autoBackupExistingSaves`,
`showCommittedFutureOverlays`, `blockCommittedActions` and `transitedBodyRotationModeIndex` were
deleted outright (`ParsekSettings.cs:93-99`, `:233-238`).

Pictures: `ksc-settings-advanced` (39 nodes) and `ksc-settings-basic` (19). The 20-node
difference is exactly the three Basic-hidden sections, diffed node for node: 5 Looping + 9
Diagnostics + 6 Sample Density. Nothing else differs but the two mode buttons' widths (the
selected one renders with `GUI.skin.box`). No picture: the null-settings fallback, a greyed
`Basic`, a mid-edit auto-loop field, either wipe button greyed.

### 3.9 Real Spawn Control

Purpose: turn a recorded craft passing nearby into a real vessel. FLIGHT only, Basic-hidden
(`ParsekUI.cs:771`, `UI/UiComplexityMode.cs:180`).

Columns (`UI/SpawnControlUI.cs:56-60`, header `:242`): `Craft` expand, `Dist` 55, `Rel Speed`
70, `Spawns at` 100, `In T-` 95, `State` 110 (not sortable), and an unheaded 118 px warp cell.
Five headers sort; default is `Distance` ascending. Row model is one `NearbySpawnCandidate`
wrapped by the pure `SpawnCandidateRowPresentation` (`UI/SpawnControlPresentation.cs:21-39`).

Two envelopes, and the window exists to show the difference: the INNER gate that enables the
warp button is 250 m / 2 m/s (`ParsekFlight.cs:423`, `:429`), the OUTER "show in the list"
bound is 1000 m / 50 m/s (`:434-435`). A candidate between them is listed with a disabled warp
button and no green tint. The three disabled reasons in priority order
(`UI/SpawnControlPresentation.cs:71-81`) are `Too far away to spawn`, `Passing too fast to
spawn`, `This pass has already happened`, and they reach the player only through
`DisabledHoverEcho` because the row buttons carry no `GUIContent` at all (`:332-335`).

Self-close: `ResolveAutoCloseReason(inFlight, hasFlight, candidateCount)` (`:88-100`) shuts the
window on its FIRST draw when any of `not-in-flight` / `flight-null` / `zero-candidates` fires.

**No capture exists in either run, by design.** `GUI-2-census-flight.toml:152` declares
`op=open window=spawncontrol` with `expect = "ERROR"` and nothing follows it; the seam answered
`window-self-closed frames=1` because the census host's sub-orbital probe has zero nearby
candidates, which is the normal state. Tracked as
`GUI-CENSUS-SPAWN-CONTROL-NEEDS-A-CANDIDATE-HOST` (`GUI-2-census-flight.toml:147-151`).

### 3.10 Gloops Flight Recorder

Purpose: record a ghost-only flight the career ignores. FLIGHT only
(`ParsekUI.cs:1742-1744`).

**Its launcher is RETIRED in BOTH modes.** `UiSurfaceVisibility.IsRetired` returns true for
`MainButtonGloops` (`UI/UiComplexityMode.cs:140-143`) and `IsVisible` short-circuits before
consulting the mode (`:162-163`), so the block at `ParsekUI.cs:941-952` never draws, in
Advanced either. The window BODY is not gated, so it still draws when its flag is set - and the
only remaining writer of that flag is the harness seam
(`TestCommands/ParsekTestCommandAddon.UiAction.cs:659-665`). The cost of that state, and why
un-retiring it is not a one-line change, is finding 5.2 "Is Gloops retired".

Contents: a FIXED button order so positions never shift between states (`:226-228`) - primary
(`Stop Recording` / `Start New Recording` / `Start Recording`), `Preview` / `Stop Preview`,
`Discard Recording` - then a three-block status ladder selected by
`SelectStatusBlock(isRecording, hasLastRecording)` (`:172-177`) on a snapshot RE-READ after the
button handlers (`:305-310`, the #446 NRE guard), then `Close`.

Picture: `flight-gloops-advanced`, 8 nodes, the `Empty` block with Preview and Discard both
greyed. No picture: the `Recording` or `Saved` blocks, `Stop Preview`.

### 3.11 The two Test Runner windows

They share a row model, status icons, colours, category labels and `Run` / `Run+` / play
semantics (both call into `InGameTests/TestRunnerPresentation.cs` and the same
`InGameTestRunner` API). Two row kinds, no columns, no sort keys (categories are ordinal-sorted):
a category header `"{arrow} {category} ({passed}/{total})"` plus `Run` 40 and `Run+` 44, and a
test row with a 20 px status icon, an expanding name label with `[isolated]` / `[single]` /
`(NNNms)` suffixes, and a 24 px play button - followed by an ALWAYS-rendered error row that
collapses to `Height(0)` when empty, because a conditional begin/end would desync the
Layout/Repaint control count (`UI/TestRunnerUI.cs:334-346`).

| difference | global (Ctrl+Shift+T) | Settings-launched |
|---|---|---|
| search / filter bar | absent | present (`UI/TestRunnerUI.cs:446-460`) |
| footer labels | two (`InGameTests/TestRunnerShortcut.cs:551`, `:553`) | one (`UI/TestRunnerUI.cs:498`) |
| window host | raw `GUILayout.Window` | `ClickThruBlocker` |
| input lock | CAMERACONTROLS + six editor types + `KSC_ALL` (`:217-222`) | CAMERACONTROLS only |
| open-flag accessor | NONE - private field, so no `op=open` reaches it | `IsOpen` + `WindowRectForTesting` |
| complexity | never gated; draws in every scene but LOADING (`:180`) | launcher lives inside the Basic-hidden Diagnostics section, and the close handler shuts an open instance (`ParsekUI.cs:516-521`) |
| extra responsibility | hosts the M-A3 autorun hooks (`:136-152`, `:635-710`, `:728`, `:787`) | none |

Picture: `ksc-testrunner-advanced` only - 4217 nodes, idle, 113 category headers, 624 play rows,
zero collapsed categories, summary `idle | 0 passed  0 failed  0 skipped  (624 total)`. The
global window has no picture anywhere and is unreachable from the seam.

### 3.12 Dialogs (21)

All are stock `PopupDialog` / `MultiOptionDialog` on `HighLogic.UISkin`, so all 21 are
invisible to `DumpGuiTree` and **none has a picture**. Only one pins an explicit width (08.6,
420 px); eight use the no-width ctor and inherit KSP's 300 px default; the two cursor-anchored
menus use 160 and 180.

| dialog title | spawn site | trigger | buttons |
|---|---|---|---|
| `Confirm: Merge to Timeline` / `Confirm: Commit to Timeline` (post-transition) | `MergeDialog.cs:274` | a pending tree surviving into FLIGHT (`ParsekFlight.Finalization.cs:45`) or the deferred non-FLIGHT coroutine (`ParsekScenario.cs:6114`) | 2 or 3: merge label, optional `Merge & Seal`, `Discard` (`:240`) |
| `Confirm: Merge to Timeline` / `Re-Fly attempt - leaving flight` (pre-transition) | `MergeDialog.cs:468` | the `HighLogic.LoadScene` prefix blocking a flight exit (`SceneExitInterceptor.cs:763`, `:796`, `:843`) | 1 (journal active), 2 or 3 (`:419-461`) |
| `Pending switch-segment recording` | `MergeDialog.cs:726` | `MapContextMenuOptions.FocusObject.OnSelect` prefix, cases A and B (`Patches/MapFocusObjectOnSelectPatch.cs:503`, `:547`) | 2: `Merge`, `Discard`. No Cancel by design; Esc is defeated by a re-spawn (`:747-784`) |
| `Revert during Re-Fly` | `ReFlyRevertDialog.cs:236` | `RevertInterceptor.Prefix` on stock Revert while a Re-Fly marker lives (`RevertInterceptor.cs:176`) | 2 (journal active) or 3: `Retry from Rewind Point`, `Discard Re-Fly`, `Continue Flying` |
| `Action Blocked` | `CommittedActionDialog.cs:31` | four stock-UI Harmony hooks: contract accept, facility upgrade, kerbal hire, tech research | 1: `OK` |
| `Confirm: Re-Fly` | `RewindInvoker.cs:509` | the Recordings-table and Timeline `Fly` buttons | 2: `Fly`, `Cancel` |
| `Save failed` | `SceneExitInterceptor.cs:585` | a throwing `SaveGame` on a flight -> main-menu exit | 1: `OK` |
| ghost icon context menu (title = vessel name) | `Patches/GhostVesselLoadPatch.cs:324` | left-click on a ghost orbit node in map view | 3: `Focus`, `Set As Target`, one dynamic third |
| `Ghost` (Tracking Station popup) | `ParsekTrackingStation.cs:1276` | TS ghost selection, driven from `UpdateSelectedGhostPopup` (`:1154`) | 1: `Warp to Spawn` or a duration-suffixed label |
| `Confirm: Seal Unfinished Flight` | `UnfinishedFlightSealHandler.cs:214` | the Seal button in the Re-Fly column | 2: `Seal Permanently`, `Cancel` |
| `Confirm: Warp to Time` | `WarpToTimeController.cs:223` | the Timeline `Warp to time` button | 2: `Warp`, `Cancel` |
| `Confirm: Wipe Recordings` | `ParsekUI.cs:1505` | Settings Data Management (`UI/SettingsWindowUI.cs:753`) | 2: `Wipe All`, `Cancel` |
| `Confirm: Wipe Game Actions` | `ParsekUI.cs:1538` | Settings Data Management (`UI/SettingsWindowUI.cs:762`) | 2: `Wipe All`, `Cancel` |
| `Confirm: Disband Group` | `UI/RecordingsTableUI.cs:4431` | `X` on a non-permanent folder | 2: `Disband Group`, `Cancel` |
| `Confirm: Rewind` | `UI/RecordingsTableUI.cs:4474` | `R` on a row, folder or block | 2 |
| `Confirm: Fast-Forward` | `UI/RecordingsTableUI.cs:4510` | `FF` on a row, folder or block | 2 |
| `Confirm: Warp to Launch` | `UI/MissionsWindowUI.cs:3006` | an enabled mission `Warp to...` | 2: `Warp`, `Cancel` |
| `Confirm: Delete Route` | `UI/LogisticsWindowUI.cs:2582` | `X` on an Active or Paused row | 2 |
| `Confirm: Delete Dormant Route` | `UI/LogisticsWindowUI.cs:2623` | Delete in the Dormant disclosure | 2 |
| `Create Supply Route?` (window-raised) | `UI/LogisticsWindowUI.cs:2726` | `Create Route` on a candidate row | 3: `Create Paused`, `Create and Activate`, `Cancel` |
| `Create Supply Route?` (post-commit) | `UI/RouteCreationDialog.cs:312` | `MergeDialog.OnTreeCommitted` - test-only callers today (finding D4) | 2: `Create Route`, `Cancel` |

Two seam facts that shape any future dialog lane: `AnswerMergeDialog` finds the live popup by
`MergeDialog.DialogName` and picks buttons by ORDER (`TestCommands/ParsekTestCommandAddon.cs:2536`,
`:2544-2577`), and it is HARD-GATED on `ActiveReFlySessionMarker != null` (`:2258-2260`), so it
can answer the merge dialog only in its Re-Fly variant. Three dialogs bypass themselves under
test through hooks (`CommittedActionDialog.cs:12`, `ReFlyRevertDialog.cs:40`/`:49`/`:57`,
`SceneExitInterceptor.ShowDialogForTesting`), so an in-game test never renders them.

### 3.13 Overlays, markers and badges (7)

None is inside a `GUI.Window`, and the badges are uGUI, so **none is in any `.gui.json` and
none has a picture**.

| surface | draw site | scene | interaction | what only it carries |
|---|---|---|---|---|
| Watch Mode overlay | `WatchModeController.cs:1054`, called `ParsekFlight.cs:2103` | FLIGHT | none (pure output) | the ghost-to-vessel distance and the `[Horizon]` / `[Free]` camera mode; a fixed 300x50 box centred in the LEFT half of the screen (`:1094-1096`) |
| Currency reservation tooltip | `CurrencyReservationOverlay.cs:87`, paint `:129` | SPACECENTER + FLIGHT | hover only | the Total / Reserved split and the `Short by` overdraw; the stock bar shows only Available. Reputation is deliberately undecorated (`:22-24`). Unconditional since the 2026-08-27 simplification (`:89-90`) |
| Stock-UI badges | `StockUiOverlayController.cs:49`, renderer `OverlayBadge.cs:7` | SPACECENTER (R&D, Astronaut Complex, Mission Control) | hover only | six tint kinds and five tooltip texts naming which recording committed a tech node, which contract a committed future claims, which slot holds an applicant, and which roster entry is a stand-in |
| Ghost map markers (flight map) | `MapMarkerRenderer.cs:208` via `ParsekUI.cs:1901` | FLIGHT map | LEFT click falls through to stock (no handler is supplied, `ParsekUI.cs:2763`); RIGHT click toggles a sticky label | a marker for a ghost with no ProtoVessel; eight distinct skip conditions decide absence (`ParsekUI.cs:2021-2296`) |
| Ghost map markers (Tracking Station) | `ParsekTrackingStation.cs:364` | TRACKSTATION | LEFT click opens the ghost popup; RIGHT click pins | the same, plus a click-block while the popup is open (`:380-388`) |
| In-world ghost labels | `ParsekFlight.cs:27052`, call `:2106` | FLIGHT | none | a 250x40 two-line label per chain ghost: `Ghost -- spawn abandoned` / `spawn blocked` / `chain terminated` / `spawns at UT=n` (`SpawnWarningUI.cs:130-147`) |
| Logistics launcher tint | `ParsekUI.cs:874-884` | wherever the main window draws | none | the ONLY at-a-glance broken-route signal |

Two tooltip-infrastructure surfaces sit behind all of the above. `TooltipEchoBox`
(`UI/TooltipEchoBox.cs:215`) is the permanently visible one- or two-line help strip in 11
windows; it draws exactly one `Space` plus one `Label` per pass so the control count and the
window height never move on hover (`:233-250`, probe `:78-79`), and it marquee-scrolls
overflowing text at 60 px/s rather than clipping (`UI/TooltipMarquee.cs:20`). Three windows have
NO strip: `GroupPickerUI`, `StructureListWindowUI`, and the link picker. `DisabledHoverEcho`
(`UI/DisabledHoverEcho.cs:92`) paints an invisible zero-layout `GUI.Label` over a DISABLED
control's rect on Repaint so its reason reaches that strip; it exists because in KSP 1.12.5's
Unity build only `GUI.DoLabel` and `GUI.DoButtonGrid` publish a tooltip from managed code and
neither reads `GUI.enabled` (`:17-27`), and `GUI.Label(Rect, ...)` reserves no layout slot
(`:29-37`). At most one carrier paints per frame (`:39-43`).

### 3.14 Screen messages (95 producers)

The whole non-window notification budget, grouped by trigger class. Full per-site table with
exact text: research note appendix 2.

| trigger class | producers | notes |
|---|---|---|
| Recorder lifecycle (start / stop / block / auto-start) | ~17 across `FlightRecorder.cs` and `ParsekFlight.cs` | the densest notified area in the mod |
| Recorder refusals | 4 (`ParsekFlight.cs:8539`, `:8550`, `:8560`, `:13373`) | four named refusals reach the screen |
| Commit / discard / unstash | 7 across `ParsekFlight.cs` and `ParsekScenario.cs` | auto-commit IS announced |
| Merge dialog tail | 7 (`MergeDialog.Commit.cs:153`-`:389`) | `:258` explicitly pushes the player to `KSP.log` |
| Re-Fly / switch discard | 5 (`MergeDialog.ReFlyDiscard.cs`) | |
| Stock switch-to interception | 6 (`Patches/MapFocusObjectOnSelectPatch.cs`) | |
| Ghost click refusals (map + TS) | 10 (`Patches/GhostVesselLoadPatch.cs`, `Patches/GhostTrackingStationPatch.cs`) | the main REFUSAL surface that is not log-only |
| Warp / fast-forward | 10 (`ParsekFlight.cs` 3, `WarpToTimeController.cs` 7) | |
| Watch mode | 3 (`WatchModeController.cs:1839`, `:1858`, `:2390`) | only the 300 km entry refusal posts; every auto-exit is silent |
| Logistics | 9 (`RouteOrchestrator.cs` 4, `RouteRunPrompt.cs`, `RouteEndpointTransfer.cs`, `RouteStore.cs`, `UI/LogisticsWindowUI.cs` 2) | |
| Missions tab | 3 (`UI/MissionsWindowUI.cs:1084`, `:2746`, `:3038`) | |
| Recordings table / group picker | 4 | |
| Spawning / proximity / delete / wipe | 7 | the proximity toast drops its "open that window" call to action in Basic (`ParsekUI.cs:261`) |
| Tracking Station | 2 (`ParsekTrackingStation.cs:1990`, `:2011`) | |
| Save lifecycle / pre-Parsek backup | 5 | backup success is silent, failure is not |
| Ledger | **1** (`GameActions/KspStatePatcher.cs:3514`, latched once per session) | the only player-visible signal that the reconstructed ledger disagreed with the live pool |
| Gloops | 7 (`ParsekFlight.cs:17014`-`:17499`) | all seven are player-unreachable: every trigger starts in the retired window or the seam |
| Playback seam / test runner / the sink itself | 4 | |

A separate 46 sites use `InfoRateLimited` / `WarnRateLimited` - STANDING conditions (route
refusals, playback skips, anchor failures) that by construction reach `KSP.log` and nowhere
else. See finding "46 rate-limited refusals" in section 6.

### 3.15 Tooltip-only information

Information that exists ONLY in a tooltip - no label, no column, no message. Research note
appendix 3 lists all of it with per-site citations; the load-bearing groups are:

| group | example | why it matters |
|---|---|---|
| every disabled-control reason | `No recorded craft is passing nearby` (`ParsekUI.cs:142`), `A supply route already drives one of these flights` (`UI/RecordingsTableUI.cs:1018`), the five rewind refusals, the three spawn refusals, `A warp is already running` (`UI/TimelineWindowUI.cs:1558`) | the control itself is only greyed; the reason has no other surface |
| the meaning of one- and two-letter buttons | `W`, `FF`, `R`, `G`, `X`, `Fly`, `Seal`, `Stash`, `GoTo` | every glyph's meaning is hover-only |
| scope statements on bulk controls | the two header select-alls IGNORE the active filters (`UI/RecordingsTableUI.cs:1220`, `:1309`) | the visible position implies otherwise |
| filter-vs-write distinctions | the `Archive` header toggle is a FILTER and archives nothing (`:1366`) | it sits where a select-all would |
| numeric constants | the 300 km watch range (`:1348`), the launch-to-launch period definition and its overlap consequence (`:1342`), the interval grammar `30m / 2h / 1d` (`Logistics/LogisticsIntervalPresentation.cs:24`) | no label carries any of them |
| status-word definitions | `static` and `stationary` (`UI/RecordingsTableUI.cs:4738`, `:4740`), the STASH group's entire meaning (`UI/UnfinishedFlightsGroup.cs:42`), `(pending)` / `(closing)` (`UI/CareerStateWindowUI.cs:1519`, shared by four tables) | the word alone is not self-describing |
| cross-window side effects | clearing the time filter also resets the Timeline sliders (`UI/RecordingsTableUI.cs:1510`); `Info >` widens the window (`:1415`) | the click changes something off-screen |
| full values the cell truncates | the untruncated hold clause (`UI/LogisticsWindowUI.cs:1074`), the full crew roster and span dates (`UI/MissionsWindowUI.cs:2700`), endpoint coordinates (`:1003`) | the cell shows a capped form |
| the Gloops tooltips | `Record a ghost-only flight that your career ignores.` (`ParsekUI.cs:945`) | reaches nobody: the launcher is retired |

`GUIContent` tooltips do not exist at all on `PopupDialog` surfaces (grep over the nine dialog
files returns zero two-arg `GUIContent` constructions), and a `GUILayout.TextField` takes no
`GUIContent`, which is why the Missions period cell's state is carried by the unit button
beside it (`UI/MissionsWindowUI.cs:4151-4171`).

### 3.16 The UiSurface gate

One pure decision point, `UiSurfaceVisibility.IsVisible(UiSurface, UiComplexityMode)`
(`UI/UiComplexityMode.cs:156`), with an exhaustive switch that THROWS on an undecided value
(`:191-197`); retirement outranks the mode and short-circuits first (`:162-163`). The mode is
frame-latched: `SetUiComplexityMode` sets a PENDING value and `ApplyPendingUiComplexityModeIfAny`
latches it from `Update()`, so Layout and Repaint of one frame can never disagree about the
control count (`ParsekUI.cs:294-297`, `:246`).

| key | declared | Basic | enforcement | what Basic actually hides |
|---|---|---|---|---|
| `MainButtonSpawnControl` | `:47` | HIDE (`:180`) | `ParsekUI.cs:771`, `:262` | the launcher, and the proximity toast's call to action |
| `MainButtonTimeline` | `:50` | KEEP (`:171`) | **none** | nothing |
| `MainButtonRecordings` | `:53` | KEEP (`:172`) | **none** | nothing |
| `MainButtonLogistics` | `:56` | KEEP (`:173`) | **none** | nothing |
| `MainButtonKerbals` | `:59` | HIDE (`:181`) | `ParsekUI.cs:904` | the Kerbals launcher |
| `MainButtonCareer` | `:62` | HIDE (`:182`) | `ParsekUI.cs:906` | the Career launcher |
| `MainButtonGloops` | `:69` | RETIRED in both (`:140-143`) | `ParsekUI.cs:941` | the Gloops launcher, in Advanced too |
| `MainButtonSettings` | `:72` | KEEP (`:174`) | **none** | nothing |
| `TabRecordings` | `:75` | HIDE (`:183`) | `UI/RecordingsTableUI.cs:189` | the Recordings tab and its whole body |
| `TabMissions` | `:78` | KEEP (`:175`) | `UI/TimelineWindowUI.cs:1265` | nothing today; the GoTo button is gated by its TARGET's key by design (`:35-41`) |
| `MissionsLoopControls` | `:95` | HIDE (`:184`) | `UI/MissionsWindowUI.cs:735` (+7 consumers) | the Loop toggle, the period cell, Clone, and every interval / partner checkbox. NOT the route label, TTL column, `Warp to...` or Watch (`:89-93`) |
| `SettingsSectionLooping` | `:110` | HIDE (`:185`) | `UI/SettingsWindowUI.cs:345`, `:378` | the Looping section |
| `SettingsSectionDiagnostics` | `:113` | HIDE (`:186`) | `UI/SettingsWindowUI.cs:393` | the Diagnostics section, and with it the only reopen path to `TestRunnerUI` |
| `SettingsSectionSampleDensity` | `:116` | HIDE (`:187`) | `UI/SettingsWindowUI.cs:401` | the sample-density section |

Four keys have zero enforcement points, so Basic and Advanced agree on them by accident rather
than by enforcement; flipping any of the four to `visibleInBasic = false` would change nothing
on screen. Separately, `UiSurfaceVisibility.HiddenSurfaces` (`UI/UiComplexityMode.cs:214`) has
no production consumer: its only reference outside its own file is a doc comment at
`ParsekUI.cs:471` explaining why the real close set is the hand-written
`BuildGatedWindowCloseSet` (`ParsekUI.cs:488-529`). That set carries six targets - CareerState,
Kerbals, GloopsRecorder, SpawnControl, TestRunner (maps to no `UiSurface`; its launcher lives
in the hidden Diagnostics section) and GroupPicker (maps to no `UiSurface`; a reachability rule,
not a lock rule, `ParsekUI.cs:479-482`) - and deliberately omits the Missions, Structure,
Timeline, Logistics and Settings windows (`ParsekUI.cs:484-487`).

## 4. Backend exposure per subsystem

580 capability rows, tallied mechanically from the Class column of the research note:
**EXPOSED 265** (plus 7 EXPOSED-in-Advanced-only), **AUTOMATIC 156**, **HIDDEN 117**, **DEAD
21**, **PROMISED-NOT-DELIVERED 7** as its own row class (most such findings live in the
per-section bullet lists instead; list B is the complete set of 22), **NOT PRESENT 4**. Counts
below fold the Advanced-only rows into EXPOSED. The named items in each table are the
subsystem's entries from consolidated lists A-D; the research note carries the full rows.

| # | subsystem | rows | EXP | AUTO | HID | DEAD | P-N-D |
|---|---|---|---|---|---|---|---|
| 0 | UI shell: toolbar, main window, shortcuts, stock-UI overlays | 28 | 23 | 3 | 1 | 1 | - |
| 0b | the notification budget | (prose) | - | - | - | - | - |
| 1 | Recording lifecycle and store | 83 | 28 | 41 | 5 | 4 | - |
| 2 | Rewind, Re-Fly and supersede | 65 | 33 | 11 | 17 | 4 | - |
| 3 | Missions | 57 | 40 | 9 | 7 | 1 | - |
| 4 | Logistics and supply routes | 67 | 41 | 11 | 13 | 1 | 1 |
| 5 | Career ledger, game actions, kerbals, crew reservation | 90 | 28 | 41 | 17 | 3 | 1 |
| 6 | Ghost playback, watch mode, map/TS presence, spawn control, Gloops | 91 | 27 | 17 | 39 | 4 | 4 |
| 7 | Settings, diagnostics, test runner, automation seams | 99 | 52 | 23 | 18 | 3 | 1 |

The named items below are the complete HIDDEN / PROMISED-NOT-DELIVERED / DEAD set from the
research note's consolidated lists A-D, one line each, grouped by subsystem. The note carries
the full rows; the per-subsystem row counts are in the table above.

**0. UI shell** (1 HIDDEN, 1 DEAD, 2 promised)

| id | item | file:line |
|---|---|---|
| H1 | No Parsek window or toolbar button in the TRACKING STATION, while the guide advertises that missions and routes loop there (`docs/user-guide.md:160`) | `ParsekTrackingStation.cs:26`, `:350`; the only `AddToAllToolbars` calls are `ParsekFlight.cs:1351`, `ParsekKSC.cs:153` |
| D1 | The main-window Gloops launcher block never draws in either mode | `ParsekUI.cs:941-951`, gate `UI/UiComplexityMode.cs:140-143` |
| P7 | The stock Difficulty screen draws 5 Parsek settings whose edits the sidecar silently reverts | `ParsekSettings.cs:77`-`:100`, revert `ParsekSettingsPersistence.cs:229-272` |
| P18 | The Settings launcher tooltip advertises four topics, three of which can be absent | `ParsekUI.cs:957` |
| - | The `W` cycle key is real and undocumented (the Controls table omits it) | `ParsekFlight.cs:13410` vs `docs/user-guide.md:6-12` |

**1. Recording lifecycle and store** (5 HIDDEN, 4 DEAD, 2 promised)

| id | item | file:line |
|---|---|---|
| H26 | `LoopStartUT` / `LoopEndUT` / `LoopAnchorVesselId` are written only by `ApplyAutoLoopRange`, so the same Loop checkbox produces a different loop WINDOW depending on where it was clicked | `Recording.cs:63-66`, `UI/RecordingsTableUI.cs:5735`, `:1338`, `:2643` |
| H27 | Deleting a single non-ghost-only recording has no player path; the only one is the all-or-nothing wipe | `RecordingStore.cs:4287`, `ParsekFlight.cs:19787` |
| D9 | Four store operations with no production caller, three destructive | `RecordingStore.cs:3813`, `:3782`, `:5079`, `:4522` |
| P9 | Rename refusals (group, re-parent) discard the typed name with a Warn | `UI/RecordingsTableUI.cs:4346`, `:4353`, `:4364`, `GroupHierarchyStore.cs:150` |
| P17 | Group hide-all writes `Hidden` over every descendant with no Unfinished-Flight check | `UI/RecordingsTableUI.cs:2859` vs the per-row guard `:2164-2172` |

**2. Rewind, Re-Fly and supersede** (17 HIDDEN, 4 DEAD, 2 promised)

| id | item | file:line |
|---|---|---|
| H17 | Rewind read-back divergence is named in code as "possible silent career corruption", logs a Warn and proceeds; the abort switch is hardcoded false | `KspStatePatcher.cs:3732`, `RewindReadbackGuard.cs:116` |
| H18 | Ledger tombstones retired by a Re-Fly merge reach no UI at all; the merge dialog names no career consequence | `SupersedeCommit.cs:2302`, counts `:2553`, `:2619` |
| H21 | Load-time sweeps delete provisionals and RP quicksaves and force-seal slots, with a Verbose log as the only record | `LoadTimeSweep.cs:53`, `:234`, `:298`, `:359`; `RewindPointReaper.cs:79` |
| H28 | A player inside a Re-Fly session has no indicator anywhere; same for `SwitchSegmentSession` and `StockActionIntentMarker` | `ReFlySessionMarker.cs:32`, `SwitchSegmentSession.cs:85`, `StockActionIntentMarker.cs:136` |
| H29 | `MergeState`, supersede rows and the 11-phase journal are named to the player exactly twice, both as refusals | `MergeJournal.cs:66-84`, `SupersedeCommit.cs:176`, `:839` |
| D8 | Legacy `SupersedeCommit` public surfaces kept alive only by their in-game tests | `SupersedeCommit.cs:123`, `:773`, `:699` |
| P11 | `Merged, but could not seal - seal it from the Timeline window` names a control that cannot exist in that state | `MergeDialog.Commit.cs:371`, `:389` vs `UI/TimelineWindowUI.cs:1481`, `:1597` |
| P12 | Timeline `R` on a non-launch row rewinds the PARENT launch; the Recordings table suppresses this deliberately | `UI/TimelineWindowUI.cs:1385`, gate `:1555` vs `UI/RecordingsTableUI.cs:3741` |

**3. Missions** (7 HIDDEN, 1 DEAD, 2 promised, 3 no-backend-truth)

| id | item | file:line |
|---|---|---|
| H22 | Selection reconcile and one-loop-per-tree normalisation rewrite player choices on load with only a log line | `MissionStore.ReconcileSelections:133`, `NormalizeOneLoopPerTree:751`, route force-clear `Logistics/RouteTreeGuard.cs:162` |
| D6 | `MissionSelection` (whole file) and `Mission.ExcludedThroughLineHeadIds`: persisted, cloned, stale-dropped, folded into the change signature, never written | `MissionSelection.cs:23`, `:41`; `Mission.cs:18` |
| D14 | `DrawCompositionNode` as a mission-row renderer, reachable only inside an included foreign partner journey (off by default) | `UI/MissionsWindowUI.cs:1578`, `:1682`; `Mission.cs:40` |
| D16 | `BuildPeriodStateTooltip(locked: true)` cannot fire | `MissionPresentation.cs:651-652`, sole call `UI/MissionsWindowUI.cs:4156` |
| P10 | The mission `Log` is titled with the mission but built from the TREE, so two clones with different include sets render byte-identical logs | title `UI/MissionsWindowUI.cs:2492`, build `UI/StructureListWindowUI.cs:110-117` |
| P21 | The chapter tri-state toggle carries no tooltip and escapes the Basic gate | `UI/MissionsWindowUI.cs:1885`, marker `:1920` |
| - | `MissionDeleteDisabledReason()` takes no arguments and returns a constant | `UI/MissionsWindowUI.cs:2908` |
| - | Header summary, `Events (N)`, chapter titles, Start/End cells and the `#` index are all keyed by TREE id, so every clone shows identical values | `UI/MissionsWindowUI.cs:1255`, `:2272`, `:1770`, `:1515-1520`, `:2467` |

**4. Logistics and supply routes** (13 HIDDEN, 1 DEAD, 4 promised, 1 no-backend-truth)

| id | item | file:line |
|---|---|---|
| H23 | Cargo escrow, route contention reservations and the whole `RouteModule` walk state; escrow is cleared wholesale on every scene switch | `Logistics/RouteStore.cs:819-1049`, `GameActions/RouteModule.cs:47-103`, `ParsekScenario.cs:8354` |
| H24 | `Route.CostManifest` / `InventoryCostManifest` - what a route DEBITS per cycle - appear only retroactively in the flow lines | `Logistics/Route.cs:89`, `:92` |
| H25 | Eleven of the 46 mod-wide rate-limited refusal sites are here, so a Pause / Activate / Send Once / Create click that does nothing looks like one that worked | `Logistics/RouteOrchestrator.cs:115`, `:129`, `:181`, `:186`, `:306`, `:311`, `:1746`; `RouteAnalysisEngine.cs:842`; `RouteEndpointResolver.cs:293`; `RouteStore.cs:640`; `UI/LogisticsWindowUI.cs:1301`, `:2876` |
| D4 | The whole post-commit `RouteCreationDialog`; its live `DismissIfOpen` can only no-op | `UI/RouteCreationDialog.cs:81`, `:136`, `:147`, `:241`, `:448` |
| P3 | The Interval field accepts `30m` / `2h` / `1d`, then ceil-snaps to `N x transit` and overwrites the typed value; on a windowed-basis route it is dead input | `UI/LogisticsWindowUI.cs:1197`, `Logistics/RouteCadence.cs:100`, `:199` |
| P19 | The Candidates empty sentence draws over a full near-miss list | `UI/LogisticsWindowUI.cs:743` |
| P20 | Four cells read `route.Stops[0]` only while `RouteBuilder` builds multi-stop routes | `UI/LogisticsWindowUI.cs:1564`, `:1002`, `:1871`, `:3275`; `Logistics/RouteBuilder.cs:353` |
| - | The `Next` countdown cell renders identically for three different branches; only the expanded detail line disambiguates | `UI/LogisticsCountdownPresentation.cs:163` |

**5. Career ledger, game actions, kerbals, crew reservation** (17 HIDDEN, 3 DEAD, 3 promised, 1 no-backend-truth)

| id | item | file:line |
|---|---|---|
| H16 | The recalc-and-patch pipeline rewrites funds, science, reputation, the tech tree, facility levels, progress nodes and the live `ContractSystem` with exactly ONE player-facing message in the subsystem | `GameActions/LedgerOrchestrator.cs:1837`+ (14 sites), `GameActions/KspStatePatcher.cs:61`, message `:3514` |
| H19 | Crew-reservation swap refusals and VAB seat clears are Info/Verbose only, so the player ends up with the wrong kerbal seated and no cause given | `CrewReservationManager.cs:273-296`, `:946`; `Patches/CrewAutoAssignPatch.cs:246`, `:258` |
| H20 | Stock kerbal dismissal is refused silently, while its four sibling committed-action blocks all raise a dialog | `Patches/KerbalDismissalPatch.cs:27`, `:37` |
| D7 | `CanAffordFundsSpending` has no production caller; `Patches/FacilityUpgradePatch.cs:36` states funds affordability is deliberately unchecked while science IS enforced | `GameActions/LedgerOrchestrator.cs:6500`, `Patches/TechResearchPatch.cs:78` |
| D13 | `GameActionDisplay.GetCategory` (zero references anywhere) and `KerbalsModule.IsKerbalAvailable` are orphaned | `GameActions/GameActionDisplay.cs:17`, `KerbalsModule.cs:1033` |
| P5 | `Wipe All Game Actions (N)` and its dialog name the ledger; `MilestoneStore.ClearAll` clears milestones only and leaves `Ledger.Actions` untouched | `UI/SettingsWindowUI.cs:758`, `ParsekUI.cs:1543`, `MilestoneStore.ClearAll:509` |
| P6 | The Kerbals Outcomes GoTo tooltip promises a Timeline scroll that only happens if the Timeline is already open | `UI/KerbalsWindowUI.cs:642`, `:693`, `UI/TimelineWindowUI.cs:322`, consumed `:1091` |
| P15 | Funds `Reserved` / `Short by` is advisory while science is enforced, behind identical-looking tooltips | `CurrencyReservationOverlay.cs:184`, `:213` |
| - | The rewind-point disk readout renders `0 B, 0 files` on a FAILED scan, with no error indication | `RewindPointDiskUsage.cs:190` -> `UI/SettingsWindowUI.cs:684` |

**6. Ghost playback, watch mode, map/TS presence, spawn control, Gloops** (39 HIDDEN, 4 DEAD, 4 promised) - the most hidden subsystem by a wide margin, 39 HIDDEN against 27 EXPOSED

| id | item | file:line |
|---|---|---|
| H2 | The whole Gloops recorder, its 7 screen messages and its recordings group are reachable only through the seam | `UI/GloopsRecorderUI.cs`, verbs `ParsekFlight.cs:16978`+, retirement `UI/UiComplexityMode.cs:140-143` |
| H10 | The 20-clone overlap cap silently RAISES the typed loop period while the Period cell keeps showing the typed value | `ParsekConfig.cs:150`, `:168`; `GhostPlaybackLogic.WarpLoopPolicy.cs:469`; verdict `GhostPlaybackEngine.cs:976` |
| H11 | Render zones, warp-hide and loop-spawn thresholds: ghosts stop drawing at 120 km, stop spawning past 50 km, lose part-event visuals past 10 km, lose FX past 10x warp and vanish past 50x. Every one reads as a bug | `ParsekConfig.cs:41`, `:71`, `:73-74`, `:327`, `:333`; `RenderingZoneManager.cs:31`, `:62`, `:76` |
| H12 | 20 playback skip reasons; one has a control | `GhostPlaybackEvents.cs:5-56`, counters `:110-133`, log `GhostPlaybackEngine.cs:893-914` |
| H13 | `historical-not-replayed`: a recording the player only moved forward past is completely inert - no ghost, no map icon, no terminal spawn | `PlaybackScopeTracker.cs:34` |
| H14 | 15 + 6 spawn refusals, and `TerminalOrbitSpawnSafety` can latch a recording permanently unspawnable | `GhostPlaybackLogic.cs:8031-8215`; `VesselSpawner.cs:1921`+; `TerminalOrbitSpawnSafety.cs:273`, `:296` |
| H15 | Every watch-mode auto-exit and 4 of 5 entry refusals are silent; only the 300 km case posts | `WatchModeController.cs:2672`, `:2695`, `:2773`, `:3652`, `:3730`, `:1788`, `:1800`, `:1816`, message `:1839` |
| D2 | `SpawnWarningUI.ShouldShowWarning` / `FormatWarningText` and `SpawnCollisionDetector.CheckWarningProximity` are dead, so the pre-spawn collision warning does not exist in game | `SpawnWarningUI.cs:23`, `:40`; `SpawnCollisionDetector.cs:973` |
| D3 | `GhostCommNetRelay` is dead while a live Harmony patch cites it as the justification for destroying each ghost's `CommNetVessel` | `GhostCommNetRelay.cs:35`, `:207`, `:239`, `:389`; `Patches/GhostVesselLoadPatch.cs:446`, `:459` |
| D10 | `IsOverlapPerInstanceGateOn()` returns a constant; the documented OFF branch cannot execute | `GhostMapPresence.cs:76`, branch `:11416-11433` |
| D11 | `UIMode.TrackingStation` is never constructed, so `CanOfferGhostOnlyDelete`'s TS branch is dead | `UI/RecordingsTableUI.cs:4613`, `ParsekUI.cs:11` |
| P1 | The per-recording playback checkbox: the map icon, orbit line, TS row and the KSC terminal-vessel spawn all ignore `PlaybackEnabled` (bug #433) | tooltip `UI/RecordingsTableUI.cs:1849`; `GhostMapPresence*.cs` / `ParsekTrackingStation.cs` never read it; `ParsekKSC.cs:373-396` |
| P2 | The Period cell shows the typed value, not the cadence being flown | header tooltip `UI/RecordingsTableUI.cs:1341`, engine `GhostPlaybackLogic.WarpLoopPolicy.cs:469` |
| P4 | `Warp to Spawn` performs a time jump only; whether a vessel appears is then decided by 15 silent refusals, and an invalid jump is itself silent | `UI/SpawnControlPresentation.cs:104`, `ParsekFlight.WarpToRecordingEnd:27367`, `:27384-27389` |
| P13 | The Gloops idle label says `Ghost-only - loops by default` while the code sets `LoopPlayback = false`; the class doc says the opposite of the label | `UI/GloopsRecorderUI.cs:333`, `ParsekFlight.cs:17090`, doc `:8-10` |

**7. Settings, diagnostics, test runner, automation seams** (18 HIDDEN, 3 DEAD, 3 promised)

| id | item | file:line |
|---|---|---|
| H3 | The whole `Diagnostics/` subsystem's output (1,680 lines - storage breakdown, playback budget, ghost tier counts, memory/LOD) reaches the player only as `KSP.log` text, and in Basic even the button is gone | `Diagnostics/DiagnosticsComputation.cs:544`, button `UI/SettingsWindowUI.cs:671`, roadmap claim `docs/roadmap.md:188-199` |
| H4 | 36 command-seam verbs and 5 `PARSEK_*` env hooks, three with no player twin (`CaptureScreenshot`, `DumpGuiTree`, `ExportRenderManifest`) | `TestCommands/TestCommandVerbs.cs:56-233`, `InGameTests/TestRunnerShortcut.cs:69`-`:80` |
| H5 | `autoRecordOnLaunch` / `autoRecordOnEva` / `autoRecordOnFirstModificationAfterSwitch`: drawn nowhere, clamped `true` on every load, still documented as Settings toggles | `ParsekSettings.cs:38`, `:40`, `:42`, clamp `:293-295`; `docs/user-guide.md:320`, `:324` |
| H6 | `autoMerge`: clamped `true`, and the guide tells the player to turn it off | `ParsekSettings.cs:71`, clamp `:296`; `docs/user-guide.md:57` |
| H7 | `forceFaithfulLoopPlayback`: decides whether a looped interplanetary mission replays verbatim or re-aims, with no player control | `ParsekSettings.cs:251`, clamp `:297` |
| H8 | `LandingBodyAlignmentMode` pinned Loose as an `internal const`; `Drop` / `Tight` reachable only from tests | `ParsekSettings.cs:237` |
| H9 | In Basic - the DEFAULT for a new install - the loop period, recorder fidelity and verbose logging have no in-game control, and there is no Kerbals or Career window either | `UI/UiComplexityMode.cs:185-187`, `:256`, `:181-182` |
| D12 | Five reserved seam verbs answering `not-implemented-v1`; three documented as never to be implemented | `TestCommands/TestCommandVerbs.cs:248-255` |
| D15 | The Basic-disabled-while-Gloops-recording guard and its hint string, which no player can produce | `UI/SettingsWindowUI.cs:505`, `:514` |
| D17 | The legacy sampling-threshold migration, which only fires for pre-preset config keys nothing has written since | `ParsekSettings.cs:395`, `:425-445` |
| P8 | `Defaults` resets 9 values and skips `ghostAudioVolume` and `uiComplexityMode`, both drawn in the same window | `UI/SettingsWindowUI.cs:416`, defaults `UI/SettingsWindowPresentation.cs:55-66`, skipped `:591`, `:488` |
| P14 | Five shipped user-guide claims with no code behind them: the Timeline `L` toggle, the Timeline footer counts, the Ghosts settings table, `Show ghosts in Tracking Station`, and pin-on-click | `docs/user-guide.md:111`, `:115`, `:336`; pinning is RIGHT-click (`MapMarkerRenderer.cs:349`) |
| P16 | The Settings-launched Test Runner claims Ctrl+Shift+T toggles it; the shortcut opens a SECOND identically titled window | `UI/TestRunnerUI.cs:498` vs `InGameTests/TestRunnerShortcut.cs:162-171` |

## 5. Findings register

Item ids are the research note's list B (`P*`) and list C (`D*`); `I` marks a finding recorded
in the inventory's own "surprising things" and "honourable mentions" sections. The triage is the
supervisor's 2026-09-11 ruling.

### 5.1 Fixed on branch `gui-fixes-1`

Each lands with a CHANGELOG entry under the current version; the entry names are given so the
reviewer can match doc to diff. No fix approach is restated here.

| id | item | CHANGELOG entry it carries |
|---|---|---|
| P5 | `Wipe All Game Actions (N)` and its confirm text name the ledger; the handler clears milestones only | Fixed: the milestone wipe button and dialog say what they delete |
| P8 | Settings `Defaults` skips `ghostAudioVolume` and `uiComplexityMode` | Fixed: Defaults resets ghost audio; the button tooltip scopes the claim |
| P16 | The Settings-launched Test Runner claims Ctrl+Shift+T toggles it | Fixed: Test Runner footer text names the window the shortcut opens |
| P17 | Group hide-all bypasses the per-row Unfinished-Flight refusal (and its mirror, the unhide path) | Fixed: folder Archive honours the Unfinished-Flight guard |
| P18 | The Settings launcher tooltip advertises the retired Recording section | Fixed: Settings launcher tooltip names the sections that draw |
| P21 | The chapter tri-state toggle has no tooltip and escapes the Basic gate | Fixed: chapter include toggle is gated and described like its siblings |
| P22 | `CommitTreeFlight` and `RouteCreationDialog` docstrings name removed entry points | Fixed: corrected stale entry-point docstrings |
| P14 | `docs/user-guide.md` claims with no code behind them (Timeline `L`, footer counts, the Ghosts settings table, "Show ghosts in Tracking Station", the auto-record / autoMerge toggles) | Docs: user guide matches the shipped settings and Timeline controls |
| P6 | Kerbals Outcomes GoTo does nothing unless the Timeline is already open | Fixed: Kerbals outcome cross-link opens the Timeline like its sibling |
| P12 | Timeline `R` on a non-launch row rewinds the PARENT launch | Fixed: Timeline suppresses Rewind on non-launch rows |
| I | Milestones `Rewards` overflows its 180 px pin (two three-part cells render at height 36 in a 21 px grid, `ksc-career-milestones-advanced.gui.json`) | Fixed: Milestones Rewards column fits its content |
| I | Four `UiSurface` keys have zero `IsVisible` call sites; `HiddenSurfaces` has no consumer | Fixed: the four main-window launchers are wired through the gate |
| I | `basicUiMode` is a dead field with a false comment (`UI/MissionsWindowUI.cs:281`) | Fixed: removed the dead basicUiMode field |
| D2 | `SpawnWarningUI.ShouldShowWarning` / `FormatWarningText` + `SpawnCollisionDetector.CheckWarningProximity` are dead | Fixed: pre-spawn collision warning wired into Spawn Control, or removed |
| D4 | `RouteCreationDialog` (whole modal, test-only callers) | Removed: the unreachable post-commit route dialog |
| D9 | Four `RecordingStore` operations with no production caller, three destructive | Removed: unused RecordingStore mutators |
| D10 | `IsOverlapPerInstanceGateOn()` returns a constant; the documented OFF branch is unreachable | Removed: the overlap per-instance rollback switch |
| D11 | `UIMode.TrackingStation` is never constructed | Removed: the unreachable Tracking Station UI mode |
| D13 | Four orphaned helpers (`GameActionDisplay.GetCategory`, `KerbalsModule.IsKerbalAvailable`, `MissionIntervalSelection.IsVesselIncluded`, `WatchModeController.FinalizeAutomaticExitForTesting`) | Removed: orphaned UI and module helpers |
| D16 | `BuildPeriodStateTooltip(locked: true)` cannot fire | Removed: the unreachable locked-period tooltip branch |
| D17 | The legacy sampling-threshold migration | Removed: the pre-preset sampling migration |
| D-list | `MissionDeleteDisabledReason()` returns a constant; the rewind-point disk readout shows `0 B, 0 files` on a FAILED scan | Fixed: mission delete reason and rewind-point disk readout tell the truth |

Not touched here by ruling: the pre-switch dialog sharing the `ParsekMerge` dialog name
(`MergeDialog.cs:730` vs the matcher at `TestCommands/ParsekTestCommandAddon.cs:2536`) is being
fixed on the sibling branch `gui-census-ops`; and `DrawRecordingTooltip` is held for a decision
(5.2).

### 5.2 Filed for decision

These change no behavior on `gui-fixes-1`. Each needs an operator answer before it can be
scoped; the five below carry the weight.

**Should the Tracking Station have a Parsek control surface at all?** Ghosts get full TS
presence - list rows, orbit lines, markers, a selection popup, targeting - and the scene hosts
no Parsek window, no toolbar button and no launcher; `ParsekTrackingStation.cs:390-391` states
the consequence in place, and `UIMode.TrackingStation` exists as an enum member nothing
constructs. Missions and routes are advertised as looping "in flight, the Space Center, and the
Tracking Station" (`docs/user-guide.md:160`), so the TS is the one scene where a player sees the
most ghosts and can do the least with them. The no-new-UI-surfaces rule applies with full force,
which makes this a question rather than a task: is the answer (a) nothing, and the user guide's
sentence is the thing that changes; (b) wording inside the existing TS ghost popup
(`ParsekTrackingStation.cs:1276`), which is the only Parsek surface the scene already has; or
(c) a real window, which would be the first new surface in the mod's history and needs an
explicit exception?

**What should the per-recording playback checkbox mean?** Its tooltip says "Unticked, the
flight stays recorded but no ghost appears" (`UI/RecordingsTableUI.cs:1849`), and
`PlaybackEnabled` is read by `ParsekKSC.cs`, `ParsekFlight.cs`, `GhostPlaybackLogic*.cs` and
`RecordingOptimizer.cs` only. `GhostMapPresence*.cs` and `ParsekTrackingStation.cs` never read
it, so the map icon, the orbit line and the TS list row survive an unticked box, and
`ParsekKSC.cs:373-396` still spawns the terminal vessel (bug #433). The proposal on file is to
make the checkbox mean what its tooltip already says - no ghost anywhere - which needs
`GhostMapPresence` and `ParsekKSC` changes plus a harness lane to prove it. The alternative is
to narrow the tooltip to "no flight-scene ghost" and leave the three other surfaces alone. Which
is the intended contract?

**Should a silent ledger rewrite stay silent?** The recalc-and-patch pipeline rewrites funds,
science, reputation, the tech tree, facility levels, progress nodes and the live
`ContractSystem` (14 call sites from `LedgerOrchestrator.cs:1837`, applied by
`KspStatePatcher.cs:61`), and the entire subsystem has exactly one player-facing message - the
drawdown clamp toast at `KspStatePatcher.cs:3514`, latched once per session, after which every
further divergence is Warn-log only. Alongside it sit two unarmed safety gates:
`RewindReadbackGuard.AbortRewindPatchOnDivergence` is hardcoded `false` at
`RewindReadbackGuard.cs:116` while the code names the failure mode as "possible silent career
corruption" (`KspStatePatcher.cs:3732`), and `LedgerOrchestrator.CanAffordFundsSpending:6500`
has no production caller while `Patches/FacilityUpgradePatch.cs:36` states that funds
affordability is deliberately unchecked (science IS enforced at
`Patches/TechResearchPatch.cs:78`). Three separate decisions: arm the abort or delete it; arm
the funds gate or delete it and accept the asymmetry with science; and decide whether a
career-altering rewrite deserves more than one latched toast - remembering that the answer
"nothing" is the house default and that the only sanctioned channels are an existing tooltip or
a one-shot message for an event that actually changed something.

**What happens to the five hidden clamped settings, and to the user guide that still documents
them?** `autoRecordOnLaunch` / `autoRecordOnEva` / `autoRecordOnFirstModificationAfterSwitch`
and `autoMerge` are clamped `true` and `forceFaithfulLoopPlayback` clamped `false` on every load
(`ParsekSettings.cs:290-297`), by the 2026-08-27 simplification, and the shipped guide still
tells the player to change three of them (`docs/user-guide.md:320`, `:324`, `:57`). The guide
half is being fixed under P14. The remaining question is the fields: do they stay as
harness-pinned hidden fields (the status quo, with `TestCommands/SettingWhitelist.cs` as the
only writer), or does the clamp become the value and the field disappear? Attached to the same
answer: the stock Difficulty-Settings screen draws five other Parsek settings whose edits are
silently reverted by the sidecar (P7, `ParsekSettingsPersistence.cs:229-272`) - hide them from
the stock screen, or honour them? And `D5`: the deferred post-transition Merge/Discard dialog is
unreachable in shipping play because `autoMerge` is clamped, yet the guide describes it
(`docs/user-guide.md:45-61`) and the harness still needs it - retire the dialog or keep it as a
harness-only path?

**Is Gloops retired, or is it coming back?** Today it is in the worst of both states: the
launcher is retired in BOTH modes (`UI/UiComplexityMode.cs:140-143`), so the window, its 377
lines, its three controls, its seven screen messages, its recordings group and every downstream
Gloops branch are exercised by nothing but the harness seam - while the code, the seam verb, the
tests and a census label (`flight-gloops-advanced`) all still carry it. Three dead findings hang
off that state: D1 (the launcher block), D15 (a Basic-disabled guard and a player-facing hint
string no player can produce), and P13 (the idle label says "loops by default" while
`ParsekFlight.cs:17090` sets `LoopPlayback = false`, and the class doc says the opposite of the
label). Re-offering it is not a one-line change - `MainButtonGloops` has no `case` in the Basic
switch, so flipping `IsRetired` throws until a Basic decision is added. Delete the subsystem, or
un-retire it with a Basic decision?

One line each for the rest:

| id | the decision needed |
|---|---|
| P2 | The 20-clone cap silently raises the loop period while the Period cell shows the typed value: show the effective period with a tooltip, or leave the cap invisible? |
| P3 | The route Interval field snaps to `N x transit` and overwrites the typed value: show the snapped value and why in the existing tooltip, or accept the silent rewrite? |
| P4 | `Warp to Spawn` has 15 silent refusals behind it: put the refusal reason in the existing disabled-hover echo, or rename the button to the action it performs? |
| P9 | Rename refusals (mission, group, re-parent) discard the typed name with a Warn: keep the field content and show the reason in the row tooltip, or leave it? |
| P10 | The mission `Log` is built from the tree and ignores the mission's include set, so clones render identical logs under different titles: is that the intended contract? |
| P11 | `Merged, but could not seal - seal it from the Timeline window` names a control that cannot exist in exactly that state: reword, or make the control exist? |
| P19 | The Candidates empty-state sentence tells the player to do what the near-miss list below shows they already did: reword, or suppress it when near-misses exist? |
| P20 | Four route cells read `Stops[0]` only while `RouteBuilder` builds multi-stop routes: is multi-stop display in scope, or should the labels be scoped to the first stop? |
| D3 | `GhostCommNetRelay` is dead while `GhostCommNetVesselPatch` cites it as the justification for destroying each ghost's `CommNetVessel`: do ghost relays contribute to CommNet or not? |
| D6 | `MissionSelection` and `Mission.ExcludedThroughLineHeadIds` are persisted but never written: removal touches the mission save schema, so does it go now or wait for a generation bump? |
| D8 | The legacy `SupersedeCommit` public surfaces are kept alive only by their in-game tests, which therefore prove something the product does not run: delete both, or re-point the tests? |
| D18 | The virtual Unfinished Flights hide-all branch is unreachable; P17's fix should make the ordinary path carry the same guard: verify after, then decide whether the branch stays |
| I | `DrawRecordingTooltip` (`UI/RecordingsTableUI.cs:5451`) is a fully unit-tested 78-line hover panel with no caller, and the SOLE production caller of `FormatResourceManifest` / `FormatInventoryManifest` / `FormatCrewManifest`: wire it back, or delete it and three green formatter test suites with it? |
| exposure | 46 `InfoRateLimited` / `WarnRateLimited` sites mean a Logistics Pause / Activate / Send Once / Create click can no-op invisibly: is a log-only refusal acceptable for a click the player made? |
| tooling | `StockUiOverlay` badges (R&D, Astronaut Complex, Mission Control) are invisible to both census instruments: is a uGUI capture path worth building? |

The eight empty-surface captures, the 21 unphotographed dialogs and the 7 unphotographed
overlays are covered by section 7 and carry no todo entry of their own.

## 6. Coverage plan

The spec for the next census lanes. Grouped by the three causes from section 2; within each
group the cheapest route is named, with the fixture and the verb steps. No TOML here - the lane
authoring is the implementing task's job.

### 6.1 The fixture lacked data of that shape (no new machinery)

These need only a different `saveTemplate` and the existing `open` / `rect` / `tab` /
`complexity` ops. They are the cheapest pictures in the whole plan.

| target | fixture | steps beyond the existing sequence |
|---|---|---|
| Missions / Recordings tab in FLIGHT (the Watch column) | the existing GUI-2 host | insert `op=tab window=missions tab=recordings` before the capture |
| Basic-mode Timeline ROWS | the existing GUI-1 host | insert `op=tab window=timeline tab=overview` before the Basic capture; today the Basic label inherits the Advanced pass's Re-Fly filter |
| Timeline in FLIGHT (any view) | the existing GUI-2 host | `op=open window=timeline` + `op=rect` + `op=tab tab=overview` + capture |
| Kerbals and Career in FLIGHT (all six tabs) | the existing GUI-2 host | both windows are already in the window table with driveable tabs |
| Timeline minimum-size rendering | the existing GUI-1 host | a second `op=rect` at 520x150 plus one capture |
| Logistics populated: Active, Paused, `New (not yet run)`, the Send-Once-armed row | `interbody-route-recorded` (Active + Paused, `completedCycles = 0`) or `depot-route-recorded` (Active, `pauseAfterCurrentCycle = True`) | `op=rect` to at least 1410x500 FIRST, or the Name column collapses as it did in the shipped capture |
| Logistics `Dismissed (N)` header | `interbody-route-recorded` (carries `DISMISSED_ROUTE_CANDIDATES` with two ids) | none |
| Logistics Candidates populated + run-cost suffix | `rover-route-recorded`, or `rover-route-career` for the Career + KSC cost | none |
| Mission header `Looped by route` + greyed Loop | `depot-route-recorded` / `interbody-route-recorded` / `rover-route-recorded` | none; the label draws with no click |
| Recordings route-bound greyed Loop toggles (header level) | same three | none; `AnyRecordingRouteBound` scans all committed |
| `Docked with <partner>` in the Start event cell; chapter header rows; `Docked partner:` rows | `bdock-recorded` (or `bdock-station-craft` + `bdock-station-pad`, or `bdock-forge-base`) | none; all three draw with no click |
| Vessel-row Fly / Seal | `refly-a-recorded` | none |
| Kerbals reserved / active owner statuses | `eva2-lko-crewed` | none |
| Kerbals and Career empty states | `fresh-career` | none |
| Missions empty state, Recordings `No recordings.` | `fresh-sandbox` or `fresh-career` | none |
| Career Contracts SPLIT layout, `Pending in timeline`, the banner divergence suffix | `career-contract-pad` | none; the pending group defaults EXPANDED |
| Career Strategies populated (the `Flow` cell) | `strategy-career` | none |
| Career Facilities upgraded rows | `career-earned-ksc` | none |
| Career Science-mode and Sandbox-mode banners and empty states | `fresh-science`, `fresh-sandbox` | none; the science lane alone buys four otherwise-dark code paths |
| Timeline `R` greyed | any recorded fixture | capture with a pending tree, or delete one `RewindPoints/<id>.sfs` from the staged save |
| Timeline `FF` and the countdown time label | an `injectedRecordings` preset whose recording `StartUT` is ahead of the save UT | one capture buys both |
| Timeline `Archived` ON and the `[archived]` row suffix | a staged save with one archived recording and `HideActive=false` | none |
| Real Spawn Control (a GUI-3 flight lane) | `bdock-recorded` / `bdock-station-craft` / `bdock-station-pad` - anything with a recorded craft inside 250 m at under 2 m/s | `op=open window=spawncontrol` now returns OK; then `op=rect` + capture + dump. Closes `GUI-CENSUS-SPAWN-CONTROL-NEEDS-A-CANDIDATE-HOST` |
| In-world ghost labels, flight status non-`Idle`, `Active Ghosts > 0` | `mun-landing-recorded` / `b2-lko-craft` / `b1-pad-craft` | PNG only for the labels; the status block needs `StartRecording` before the dump |
| Tracking Station scene (markers, the ghost popup) | any `*-recorded` fixture | `LoadGame` with `scene=TRACKSTATION` then capture; no `UiAction` is possible there, so the driver needs a branch that skips the `op=rect` it currently sequences before every label |

Fixtures named by the research note but not present in the tree, so a NEW FIXTURE is required:
a career with an orphaned retired stand-in (Kerbals `Unlinked Retired`), a career with a
destroyed facility, a career whose uncommitted timeline credits a milestone after the live UT
(Milestones `(pending)`), a save with a dormant route, and a save with a hard-broken or held
route.

### 6.2 The state needs a click (the `gui-census-ops` ops)

The sibling branch `gui-census-ops` is adding the pointer-and-click op family. Each op below is
named with the surfaces it unlocks, so the branch can be scoped against real targets rather than
guesses.

| op | unlocks |
|---|---|
| `pointer` (park the IMGUI mouse over a named control's rect for one Repaint) | EVERY populated `TooltipEchoBox` strip (11 windows), every `DisabledHoverEcho` reason (the five rewind refusals, the three spawn refusals, the two warp refusals, the four `Warp to...` reasons, the two Watch reasons, the route stepper floors, `Pick a route above...`, `This route was not built from a recorded mission`), marquee mode, and every hover-only marker label |
| `find` (resolve a control by label or `ref` from a prior `.gui.json`) | the addressing layer every other op needs; without it a click op can only take indices |
| `expand` (toggle a disclosure or caret by key) | all 21 Recordings-tab leaf-row cells with their phase colours, status words and tree connectors; chain and grouped blocks; STASH member rows; expanded per-vessel interval rows; the `Events (N)` digest and its `Go to`; the Logistics route and candidate detail panels (the largest un-photographed control cluster in the mod); the near-miss, dismissed and dormant disclosures; expanded Kerbals owner chains and folded outcome headers; the Career `Pending in timeline` fold |
| `target` (invoke a typed entry point rather than synthesising a click: `OpenStructureWindowForMission`, `OpenStructureWindowForRoute`, `ShowWipeRecordingsConfirmation`, `SetUiComplexityMode`-style setters for `showExpandedStats`, `TimeRangeFilterState`, `showCustomRange`, `sortColumn`, `foldedKerbals`) | the populated Structure window in both modes, the Info-expanded stats columns, the time-range filter indicator and every non-default Timeline preset, the custom sliders, every non-default sort direction, the folded Kerbals summary |
| `picker` (arm a popup over a row selection) | the Group picker in both its titles, and the Logistics link picker - the two windows the seam excludes today by name (`TestCommandUiAction.cs:369-377`) |
| `dialog` (raise a `PopupDialog` and HOLD it open for a capture, then answer it) | all 21 dialogs. Two prerequisites beyond the op: `AnswerMergeDialog` needs a surface-only mode (it finds and immediately invokes today, `ParsekTestCommandAddon.cs:2260`) and a path that does not require a live Re-Fly marker (`:2258-2260`); and the three test hooks that short-circuit a spawn must be left null |
| `rect` clamping (refuse or warn when a commanded rect is below the window's own minimum) | prevents a repeat of the shipped Logistics capture, which was forced to 1280 against a 1410 minimum and photographed a layout no player can produce |

Two targets stay out of reach even with the full family, and should be recorded as such rather
than chased: the paused-host variant (no verb raises the pause menu, and the expected picture is
"the window is absent") and the Settings null-game fallback (the seam waits for a loaded game
before running any `UiAction`, which is exactly the state the branch needs).

### 6.3 Outside the recorder by construction

These need a capture path, not a verb. All of them are PNG-only by nature.

| target | what is needed |
|---|---|
| all 21 dialogs, all 7 overlays, every populated tooltip | full-screen `CaptureScreenshot` paired with `pointer`, not a window-tree dump. `CaptureScreenshot` already grabs the framebuffer, so the missing half is only "raise it and hold it" |
| the stock-UI badges (R&D, Astronaut Complex, Mission Control) and their five tooltips | a verb that opens a stock facility screen plus `pointer`; the `c2` career fixture with a committed tech, contract and hire is the subject |
| the currency reservation tooltip (four text forms x two widgets) | `pointer` over a stock uGUI widget rect - the first pointer target that is not a Parsek control |
| ghost map markers in either scene | the existing `EnterMapView` verb (M-A7) plus a full-screen capture; the sticky variant additionally needs a synthesised right-click on a marker |
| any tint (Logistics red/cyan, phase colours, amber pending rows, the `(partial)` dim) | the tree carries no colour field, so every tint claim must be paired with a PNG |

## 7. Sizing facts

Minimums, defaults and the fixed `GUILayout.Width` pins, measured against the 1280x720 census
instance and a 1920x1080 player screen. "First-open" rects are seeded only when
`windowRect.width < 1`, so a seam-commanded `op=rect` is never overwritten.

| window | min W x H | first-open W x H | resize? | fits 1280x720 | fits 1920x1080 |
|---|---|---|---|---|---|
| main | none | 250 x content (flight `ParsekFlight.cs:962`, KSC `ParsekKSC.cs:23`) | NO handle | yes | yes |
| Missions | **1355** x 150 (`UI/RecordingsTableUI.cs:48`, `:49`, `:63`) | 1355 x main height (KSC 2x) | yes | **NO** - 75 px too wide at its own minimum | yes |
| Missions, Info expanded | same min; the footer forces width to **1813** (`:64`, `:1420`) | 1813 | yes | NO | yes, 107 px to spare |
| Timeline | 520 x 150 (`UI/TimelineWindowUI.cs:67`, `:68`) | 820 x max(600, main height) | yes | yes | yes |
| Kerbals | 280 x 150 (`UI/KerbalsWindowUI.cs:42-43`) | 410 x 400 (half of Career's 820, so the two sit side by side, `:44-46`) | yes | yes | yes |
| Career State | 520 x 200 (`UI/CareerStateWindowUI.cs:74-75`) | 820 x 400 | yes | yes | yes |
| Logistics | **1410** x 500 (`UI/LogisticsWindowUI.cs:390-391`) | 1556 x 500 | yes, clamped on DRAG only (`:435`) | **NO** - 130 px too wide at its minimum; the census forced 1280 and got a squeezed layout with a horizontal scrollbar | yes |
| Structure | 420 x 160 (`UI/StructureListWindowUI.cs:69-70`) | 820 x 320 | yes | yes | yes |
| Settings | none | 280 x 600 (the 600 is a guess; every first open requests a height fit, `:75-86`) | NO handle; height is fixed between fits | yes | yes |
| Real Spawn Control | 350 x 150 (`UI/SpawnControlUI.cs:74-75`) | 750 x 200 | yes | yes | yes |
| Gloops | none | 280 x 230 (`UI/GloopsRecorderUI.cs:48-49`) | NO handle | yes | yes |
| Test Runner (Settings) | 320 x 600 (`UI/TestRunnerUI.cs:70-73`) | 440 x 600 | yes | yes, 600 of 720 | yes |
| Test Runner (global) | 320 x 600 (`InGameTests/TestRunnerShortcut.cs:85-88`) | 440 x 600 at a FIXED screen position (20, 60), not anchored to the main window (`:195`) | yes | yes | yes |
| Group picker | 220 x 200 (`UI/GroupPickerUI.cs:88-89`) | 280 x 300, clamped to the screen at the click point (`:207-213`) | yes | yes | yes |
| Logistics link picker | 240 x 180 (`UI/LogisticsWindowUI.cs:329-330`) | 340 x 380, clamped at the arming mouse position | yes | yes | yes |

Two windows therefore cannot be drawn at their own minimum on the 1280-wide census instance:
Logistics (1410) and Missions (1355, or 1813 with Info expanded). `op=rect` writes the field
directly and only a resize DRAG clamps, so a commanded rect below the minimum is accepted
silently - that is what produced the shipped Logistics capture. Any future populated capture of
either window must size to at least the minimum first, which on a 1280 screen means the
instance itself has to grow.

Fixed content pins, for the same reason - a column constant that exceeds its own content is the
class of defect the Milestones `Rewards` overflow belongs to:

| window | pins |
|---|---|
| Recordings tab | `ColW_*` 20 / 30 / 90 / 90 / 110 / 80 / 65 / 65 / 65 / 35 / 120 / 120 / 120 / 60 / 60 / 90 / 50 / 60 / 90 / 80 (`UI/RecordingsTableUI.cs:52-75`, `:319-331`); `ColHeaderHeight` 32 (`:73`); body row height 29 measured |
| Missions tab | 20 / 30 / expand / 105 / 120 / 110 / 85 / 120 / 90 / 80 (`UI/MissionsWindowUI.cs:302-355`); `ColW_HeaderButton` 70 (`:314`); `CompositionRowMinHeight` 22 (`:351`) |
| Timeline | `TimeColumnWidth` 160, row action 40, `GoTo` 48, warp button 186, warp fields 36; the six filter/preset columns are responsive at `Max(93, (width - 50) / 6)` - 158 at the census width (`UI/TimelineWindowUI.cs:70-78`, `:417-424`, `:155`, `:564`) |
| Career State | Contracts 240/90/90/70, Strategies 220/90/140/**70**, Facilities 200/120/180, Milestones 90/200/**180**/70 (`UI/CareerStateWindowUI.cs:83-98`). `ColW_PendingTag` (70) is shared by three tabs; `ColW_Rewards` (180) demonstrably overflows |
| Kerbals | NONE - no `GUILayout.Width` call in any row; the two tabs are indented outlines |
| Logistics | routes 30 / expand / 95 / 180 / 150 / 80 / 135 / 240 / 120 / 190 = 1220 fixed; candidates 30 / expand / 95 / 180 / 260 / 80 / 190 = 835 fixed (`UI/LogisticsWindowUI.cs:343-372`). The `MinWindowWidth` comment at `:383-389` is stale: it still totals a 90 px Next column and claims about 1175 |
| Structure | 28 / 110 / expand / 95 / 185 / 140 (`UI/StructureListWindowUI.cs:62-67`) |
| Settings | 45 auto-loop field, 40 unit button, 85 audio label, 35 percent label |
| Real Spawn Control | expand / 55 / 70 / 100 / 95 / 110 / 118 (`UI/SpawnControlUI.cs:56-60`) |
| Test Runner | status icon 20, `Run` 40, `Run+` 44, play 24, search label 48, clear 24; `ErrorIndent` 40, `ErrorMaxWidth` 380 |
| overlays | watch box 300x50 pinned; currency tooltip 147 wide; badge 18x18 with a 320x54 tooltip; map icon 20 with 6 px click and 24 px toggle pads; ghost label 250x40 |

The tooltip echo strip is 23 px in single-line windows (Timeline, Logistics, Career State,
Missions, Real Spawn Control) and 38 px in two-line windows (main, Kerbals, Settings, Gloops,
both Test Runners); its height is pinned to a probe measurement so the window never changes
height on hover (`UI/TooltipEchoBox.cs:78-79`).
