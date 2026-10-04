# Parsek GUI inventory - the shipped surface, and what the backend exposes through it

Measured 2026-09-11 against commit `4eb427e9e`. All `file:line` references are relative to
`Source/Parsek/` unless they start with `harness/`, `docs/` or `scripts/`.

STRUCTURAL CHANGE 2026-09-27 (owner decision): the Career State window is REMOVED - its class,
its launcher, its `UiSurface` key (`MainButtonCareer`), its seam token `career` and its gallery
states. So the window population is **13** (was 14), the main window's launchers are one fewer
(3.1), and the gate has **13** keys (3.16). Its one unique fact, how many contract / strategy
slots the committed future reserves, is the last sentence of the Timeline's `Contracts` and
`Strategies` button hovers (3.3). Section 3.5 is kept as a one-paragraph tombstone; the tallies
of section 2 and the coverage history elsewhere are the record of the flights that took them
and are not rewritten.

STRUCTURAL CHANGE 2026-09-26 (operator ruling: recordings are never player-deletable, because a
deletion breaks the timeline and the ledger). The Settings window's Data Management section is
GONE with both its buttons (`Wipe All Recordings (N)`, `Wipe All Milestones (N)`), their two
confirm dialogs (`Confirm: Wipe Recordings`, `Confirm: Wipe Milestones`) and their two
`All ... wiped` screen messages, so the dialog population is **19** (was 21) and the Settings
window draws five sections (3.8, 3.12). The Recordings table's `X` on ghost-only rows is gone
too (3.2, Recordings tab), and with it `ParsekFlight.DeleteRecording` and its `Recording '...'
deleted` screen message; a ghost-only row now draws only its `G`. The per-row Archive (Hidden)
checkbox is the only way a player stops seeing a recording. The coverage tallies below are the historical record of the
flights that took them and are not rewritten; where they count a wipe dialog, that row no
longer exists.

COVERAGE UPDATED 2026-09-11 (evening) off the wave-2 reading runs `2026-09-11_1548` / `_1551` /
`_1553` / `_1556` / `_1559` / `_1601`, all six PASS on attempt 1, 79 further captures. What
moved: section 2's tally (windows 10 -> 13 of 14, overlays 0 -> 2 of 7, nine empty-vs-populated
pairs where there were none, and the denominator corrected from a hand-sum), 3.0's window index,
3.13's overlay table, and section 6's per-row lane column - including four rows the runs
REFUTED. WHAT THIS UPDATE DOES NOT DO: re-measure the structural analysis. Sections 3 to 5 and 7 still
carry the 2026-09-11 reading against `4eb427e9e`, and wave 2 flew a LATER DLL (this branch, with
`gui-census-ops`' six ops, `gui-fixes-1`' four seam fixes and the `origin/main` merges between),
so a `file:line` below is the line at `4eb427e9e` and a re-measure is its own task. Where a
wave-2 capture contradicted a structural claim, the contradiction is recorded at the claim (3.13
and the section-3 preamble) rather than smoothed over.

COVERAGE UPDATED AGAIN 2026-09-15 off two flights of that day, both PASS on attempt 1 and both on
one pinned automation DLL (sha256
`ffa524672a4c4bde607556c226e20fa9a65f4cdb42abf717f556bdb6d5443ec9`, re-pinned before, between and
after the two flights and never moved). `GUI-10-census-dialogs` (`2026-09-15_1538`) took THE
FIRST PICTURES OF ANY PARSEK MODAL - section 2's dialog row moves 0 -> 6 of 21 and 3.12 gains a
measured correction to its own table, found while authoring the lane against the source.
`GUI-7-census-flight-recording` (`2026-09-15_1539`) is a MEASURED REFUTATION rather than a fix:
the hover row STAYS at 0 of 2, and what changed is that its cause is now positively stated
instead of open (the `pointer` row of 6.2). Sections 3 to 5 and 7 are still the 2026-09-11
reading against `4eb427e9e`; 3.12 alone carries re-derived line numbers, because that is where
the authoring pass hit them.

COVERAGE UPDATED AGAIN 2026-09-15 (late) off `GUI-12-census-testrunners` (run
`2026-09-15_2057`, PASS on attempt 1, 89 s wall, every verifier PASS or SKIPPED,
`expectations mismatches=0`, analyzer RED=0, deployed automation DLL sha256
`74d03cb94e233636534dd695f28d6f31e70feaf327ef38d88b851c8abb559369`, host the committed
`career-earned-ksc` fixture at SPACECENTER, 7 PNGs + 7 `.gui.json` dumps, every dump
`patched=17/17`). THIS IS THE LANE THAT CLOSES THE WINDOW CLASS: the global Ctrl+Shift+T
runner was the last Parsek window with no picture and no seam route, and it now has both, so
section 2's window row reads 14 of 14 and window 13 of 3.0 carries a token instead of an
exclusion. What moved: section 2's tally, its denominator and its exclusion sentence (three
deliberate exclusions -> two), rows 12 and 13 of 3.0 with the asymmetry bullet under them, the
whole of 3.11 - which alone carries re-derived line numbers for BOTH runner files, because
that is where this authoring pass hit them - and the two Test Runner rows of section 7's size
table. Sections 3 to 5 and the rest of 7 are still the 2026-09-11 reading against `4eb427e9e`.

COVERAGE NOT YET RE-MEASURED FOR WAVE 5, AND THIS PARAGRAPH SAYS SO ON PURPOSE. Eleven
new census lanes were AUTHORED AND FLOWN GREEN 2026-09-21 (GUI-13..GUI-23, twenty
flights on one pinned automation DLL - 18 PASS and 2 PARSEK-FAIL, both of them one lane's
own log-contract regex casing rather than a product failure, and every lane's final
verdict PASS on attempt 1) plus two
amendments to GUI-1 and GUI-6, off a read-only STATE-COVERAGE AUDIT that is a different
measurement from this file's: where this document enumerates SURFACES (windows, tabs, dialogs, overlays, gate keys)
and asks which are reachable, the audit enumerated visibly distinct STATES of those surfaces
and asked which were photographed. Its headline reading was that all 14 windows were
MODELLED - each had at least one capture, which is what section 2's 14 of 14 records - and
that none was COVERED, because what had been photographed was the product's RESTING state:
every in-progress, blocked, held, refused, superseded and authoring state was absent, and
roughly 60 hover / disabled-reason strings remain unreachable for the cause the `pointer` row
of 6.2 already states. Section 2's tally is therefore CORRECT AS WRITTEN and is not the number
the audit moves; a per-window state tally is a different axis and belongs with the audit, not
here. What DOES belong here and is recorded now, because it corrects claims this file makes:

- **Four census captures were STALE against HEAD** and are re-shot by the act of re-flying
  their lane. `ksc-settings-advanced` / `ksc-settings-basic` photographed a button reading
  `Wipe All Game Actions (N)` where HEAD draws `Wipe All Milestones (N)`;
  `ksc-career-milestones-advanced` photographed the Rewards column at 180 px with cells
  wrapping, where HEAD sets `ColW_Rewards = 320f` - widened BECAUSE of that dump, so
  `cek-career-milestones-advanced` already carries the 320; and the two GUI-1 Kerbals labels
  (`ksc-kerbals-roster-advanced`, `ksc-kerbals-outcomes-advanced`) photographed the
  PRE-REDESIGN single-line shape that the 2026-09-15 rebuild replaced with four-column
  tables. Nothing in any spec names the old labels, so re-flying is the whole fix. The
  Kerbals pair is KEPT rather than dropped: the lane photographs whatever HEAD draws, and
  those two are the census's only DENSE Kerbals pictures.
- **Two draw branches this file lists as surfaces are DEAD.** `SpawnControlUI`'s
  `No nearby craft to spawn.` branch can never execute - `DrawIfOpen` reads the same
  candidate list into `ResolveAutoCloseReason` a few lines earlier and closes the window on
  `zero-candidates` - and the un-indexed capture `flight-spawncontrol-advanced` is exactly
  that outcome: no Spawn Control window in the dump at all. And `MainButtonGloops` is RETIRED
  in BOTH complexity modes (`UI/UiComplexityMode.cs`, `IsRetired`), so no player can open the
  Gloops Recorder; its three states are photographed through the seam's own `IsOpen` write
  and are DIAGNOSTIC in practice. Both are filed with their source gates as todo
  `GUI-STATE-COVERAGE-RESIDUE-2026-09-21`.
- **The Timeline's grey `!IsEffective` row is neither a supersede's trace nor a
  tombstone's, and it is not a strike.** `TimelineEntry.IsEffective` is written only from
  `action.Effective` and from the milestone-compaction merge
  (`Timeline/TimelineBuilder.cs`) - never from a recording supersede, which produces no
  Timeline pixel at all. And the Timeline's grey `!IsEffective` row is neither a supersede's trace nor a tombstone's: the window is fed `EffectiveState.ComputeELS()` (`UI/TimelineWindowUI.cs:477-483`), so a tombstoned action is filtered out before the builder, and the only writers of `Effective = false` are `GameActions/ContractsModule.cs:399/408/417/428` (a duplicate / already-resolved contract completion) and `GameActions/MilestonesModule.cs:108` (a duplicate milestone), reset at `RecalculationEngine.cs:519`. It also is not a STRIKE - `timelineStrikethroughStyle` differs from the label style only by `normal.textColor = Color.gray` (`UI/TimelineWindowUI.cs:395-396`), so the state is a colour and therefore a PNG verdict. So it needs a duplicate-contract or
  duplicate-milestone host. Measured on GUI-19's first flight over the one committed
  fixture carrying a `RECORDING_SUPERSEDES` entry: it loaded, the Details tab drew one
  launch row, nothing was grey. Filed as
  GUI-CENSUS-TIMELINE-STRIKETHROUGH-IS-DUPLICATE-CREDIT-NOT-SUPERSEDE-OR-TOMBSTONE.
- (HISTORICAL: the Facilities tab was removed 2026-09-24; facility history is the Timeline's Career > Facilities view.) **The Career State window's Facilities tab CAN see upgrades**, which this file could not
  say before. Every capture across three fixtures read nine rows of `L1`, consistent both
  with "nothing was upgraded" and with "the window is blind". GUI-14 drove one
  `KscAction upgrade-facility` and the tab then read one row at `L2` (Tracking Station)
  beside eight at `L1`. A `FacilityUpgrade` is NOT a Milestones row, though: that tab
  still read `(no milestones credited)` on the same ledger in the same frame.
- **Three surfaces this file treats as reachable are not, from any committed host.** The
  Structure window's Route-mode `Origin: depot` step (all three committed routes read
  `isKscOrigin = True`); the Logistics capacity line `<dest> tanks full: ...` (gated on
  `RouteStatus == DestinationFull`, a status the loop dispatch path never assigns - it records
  a hold and transitions only to `Paused`); and the Career State window's populated Strategies
  rows (that tab reads Parsek's effective LEDGER, and `strategy-career` carries no Parsek
  footprint at all, so it draws `(no active strategies)` like every other host). The last
  of the three is reachable since 2026-09-24: `GUI-5-census-career-ksc` activates a strategy
  through `KscAction action=activate-strategy` and photographs the populated tab.

Sections 3 to 5 and 7 still carry the 2026-09-11 reading against `4eb427e9e`. A full
re-measure against the wave-5 captures is its own task; the per-lane reading lives in each
new spec's header and in `docs/dev/autotest-status.md`, the single status authority.

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
| `PopupDialog.SpawnPopupDialog(` excluding `InGameTests/` + `TestCommands/` | **19** modal dialogs (21 at `4eb427e9e`; the two Settings wipe confirmations were removed 2026-09-26) |
| `void OnGUI()` in production | **5** hosts: `ParsekFlight.cs:2087`, `ParsekKSC.cs:227`, `ParsekTrackingStation.cs:350`, `CurrencyReservationOverlay.cs:87`, `InGameTests/TestRunnerShortcut.cs:177` (`OverlayBadge.cs` was the sixth until 2026-09-25, when the stock-UI badges moved to stock mechanisms and the file was deleted) |
| IMGUI draw calls outside `UI/` | 6 files: `CurrencyReservationOverlay.cs`, `MapMarkerRenderer.cs`, `ParsekFlight.cs`, `ParsekKSC.cs`, `ParsekUI.cs`, `WatchModeController.cs` (`OverlayBadge.cs` deleted 2026-09-25) |
| `UiSurfaceVisibility.IsVisible(` | **12** call sites in 5 files; 4 of the 14 enum keys have none |
| `ParsekLog.ScreenMessage` / `ScreenMessages.PostScreenMessage` | 107 raw, **95** real producers at `4eb427e9e`; **92** since the two `All ... wiped` toasts left with the Settings wipes and `ParsekFlight.DeleteRecording`'s toast with it (2026-09-26) |

## 2. How the picture was taken

**The tooling.** Two operator-tier harness lanes drove one career save through the M-A2
command seam: `GUI-1-census-ksc` (SPACECENTER, 29 labels since the 2026-09-22 fold re-fly,
23 when this inventory was taken) and `GUI-2-census-flight` (FLIGHT,
4 labels), whose results are `harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/`,
`harness/results/2026-09-22_1631_GUI-1-census-ksc_shots/` and
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
why the window table names TWO deliberate exclusions (`TestCommands/TestCommandUiAction.cs:484-504`):
`GroupPickerUI` and the Logistics link picker, both popups over a SELECTION nothing in the seam
can arm, so raising either flag would photograph an empty picker. `TestRunnerShortcut` WAS the
third and is now the `testrunnerglobal` row: a separate MonoBehaviour needs an accessor, not a
driveable context, and one shipped with GUI-12 (3.11). Adding either survivor still costs a way
to drive its context first, which is why the source names them rather than leaving them absent.

**The host.** Both lanes ran the operator-local fixture `fixtures/local-saves/c1-gui`
(`harness/scenarios/GUI-1-census-ksc.toml:34-44`), chosen for density: a 42 MB long-lived
career with 704 recording sidecars and 12 vessels. Screen 1280x720.

**What each capture layer can and cannot see.** The blind spots are structural, not accidental.

| layer | example | in the `.gui.json`? | in the PNG? |
|---|---|---|---|
| IMGUI inside a `GUI.Window` callback | every window and tab in section 3 | YES | yes |
| IMGUI outside any window | watch overlay, ghost map markers, currency tooltip, in-world ghost labels | NO | yes, if drawn |
| uGUI (`PopupDialog`, stock controls Parsek annotates, the stock toolbar button) | all 21 dialogs, the R&D / Astronaut / Mission Control stock-control annotations, the ApplicationLauncher button | NO | yes, if raised |
| hover-dependent IMGUI inside a window | the `TooltipEchoBox` strip's TEXT, `DisabledHoverEcho` reasons | node captured, always EMPTY | only with a parked pointer |

Colour is a third blind spot: the tree carries no colour field, so a tint (the red/cyan
Logistics launcher, the amber pending rows, the phase colours) is PNG-only evidence.

**Coverage.** Wave 1 produced 27 labels; one (`parsek-guitree-probe`) is the recorder's own
self-test window, leaving **26 player-facing captures**. WAVE 2 FLEW ON 2026-09-11 and added
**79** more (14 + 16 + 17 + 12 + 8 + 12, every one a PNG plus a `<label>.gui.json` dump, all 79
dumps reporting `patched=17/17`), so the corpus is **105 player-facing captures** across eight
lanes. WAVE 3 FLEW ON 2026-09-15 and added **8** more - `GUI-10-census-dialogs`, run
`2026-09-15_1538`, PASS on ATTEMPT 1, 67 s wall, every verifier PASS or SKIPPED,
`expectations mismatches=0`, analyzer RED=0, 17 harvested files (8 PNG + 8 `<label>.gui.json`
dumps + `KSP.log`) - so the corpus is **113 player-facing captures** across nine lanes. SIX of
the eight are the first pictures of any Parsek modal; the other two
(`dlg-baseline-no-modal`, `dlg-teardown-no-modal`) bracket them and are the lane's own proof
that nothing stood over the frames before or after. WAVE 4 FLEW LATER THAT DAY and added **7**
more - `GUI-12-census-testrunners`, run `2026-09-15_2057`, PASS on ATTEMPT 1, 89 s wall, every
verifier PASS or SKIPPED, `expectations mismatches=0`, analyzer RED=0, 7 PNGs + 7
`<label>.gui.json` dumps, every dump `patched=17/17` - so the corpus is **120 player-facing
captures** across ten lanes. FOUR of the seven are the Settings-launched runner in four states
and THREE are the global Ctrl+Shift+T runner, which had no picture and no seam route before
this lane. The tally below is recomputed from the files, per class, and the earlier wave
columns are kept beside it so the movement is visible rather than asserted.

| kind | wave 1 | after wave 2 | after wave 3 | after wave 4 | still not photographed |
|---|---|---|---|---|---|
| windows | **10 of 14** | **13 of 14** | **13 of 14** | **14 of 14** | none - THE CLASS IS CLOSED. Wave 2 pays all three hosts the seam's window table excluded by name: `Link round-trip partner` (GUI-3), `Manage Groups` + `Set Parent Group` (GUI-3 and GUI-4, both titles each), and `Parsek - Real Spawn Control` (GUI-6, one candidate row). Wave 3 adds no window: its subject is uGUI. Wave 4 pays the last one, the global Ctrl+Shift+T Test Runner, whose open flag was a private field on a separate MonoBehaviour until GUI-12 gave it an accessor and the `testrunnerglobal` seam row (3.11); the same lane re-shoots the SETTINGS-launched runner in three further states, which is a state gain rather than a window gain |
| tabs | **12 of 12** | **12 of 12** | **12 of 12** | **12 of 12** | none. Wave 2 re-shoots them on hosts whose rows can be read off committed bytes, adds the FLIGHT form of eight (GUI-6 / GUI-7) and the EMPTY form of six (GUI-8). Neither runner window has tabs |
| modal dialogs | **0 of 21** | **0 of 21** | **6 of 21** | **6 of 21** | SINCE 2026-09-26 THE POPULATION IS 19: the two wipe confirmations below no longer exist, so four of the six photographed rows remain and `GUI-10-census-dialogs` raises four. The historical reading follows. 15 of the 21. SIX HAVE A PICTURE as of wave 3, each raised through its own production spawn site, reported standing by `op=dialog`, photographed, dumped and dismissed: `actionblocked` (`ParsekResourceBlock` / "Action Blocked" / `OK`), `savefailed` (`ParsekSceneExitSaveFailed` / "Save failed" / `OK`), `wiperecordings` (`ParsekWipeRecordingsConfirm` / "Confirm: Wipe Recordings" / `Wipe All`, `Cancel`), `wipemilestones` (`ParsekWipeMilestonesConfirm` / "Confirm: Wipe Milestones" / `Wipe All`, `Cancel`), `fastforward` (`ParsekFastForwardConfirm` / "Confirm: Fast-Forward" / `Fast-Forward`, `Cancel`) and `seal` (`ParsekUFSealDialog` / "Confirm: Seal Unfinished Flight" / `Seal Permanently`, `Cancel`). Each of the six `op=dialog` steps beside them read `open=true count=1` with that name, title and button list. THE SEVENTH RAISABLE ROW, `Confirm: Rewind`, answered the typed refusal `REJECTED dialog-target-unavailable popup=rewind detail=no-rewind-owner-among=21` on this host - its spawn site silently returns when `RecordingStore.GetRewindRecording` is null, and no recording in `bdock-recorded` carries a `rewindSaveFileName` - so a host with a rewind point is what would photograph it. THE REMAINING 14 STAY FILED WITH THEIR REASON, each needing state a pure in-process call cannot supply: the tree merge dialog (`ParsekMerge`; its spawn takes a `RecordingTree` and BOTH its buttons act on it, so a synthetic one's commit would write invented history), the pre-switch decision dialog (`ParsekPreSwitch`; needs a live `Vessel`, so FLIGHT only, and RE-SPAWNS ITSELF on any non-button teardown), the ghost icon context menu (`ParsekGhostIconMenu`; spawned inside a Harmony Prefix over a live ghost ProtoVessel in map view, so there is no method to call), the Tracking Station ghost popup (its host exists only in TRACKSTATION, which runs no ParsekUI, so every `UiAction` there answers `REJECTED ui-host-unavailable`), Re-Fly invoke (`ParsekRewindInvoke`; a RewindPoint with a child slot), Re-Fly revert (`ParsekReFlyRevert`; a live `ReFlySessionMarker`), `Confirm: Disband Group`, the three Logistics confirms (delete route, delete dormant route, create route - a live `Route` or `RouteCandidate`), and the remainder. See 6.2 |
| overlays / markers / badges | **0 of 7** | **2 of 7** | **2 of 7** | **2 of 7** | the Watch Mode overlay (GUI-6 `play-main-watchmode-advanced`) and the flight-map ghost markers (GUI-6 `play-mapview-ghostmarkers-advanced`, 243 `[GhostMap] Marker DRAWN` lines behind it) ARE photographed. The five still dark: the currency reservation tooltip, the stock-UI badges, the Tracking Station markers, the in-world ghost labels and the Logistics launcher TINT (zero `broken-state tint applied` lines in any lane, so only the untinted button has a picture) |
| tooltip surfaces in a USEFUL state | **0 of 2** | **0 of 2** | **0 of 2** | **0 of 2** | both, and THE CAUSE IS NO LONGER OPEN - it was MEASURED on 2026-09-15 (GUI-7 run `2026-09-15_1539`, PASS on attempt 1, 63 s, 17 harvested files), and the reading RETIRES both candidates the finding had pre-registered. A one-shot Verbose probe inside a Parsek `OnGUI` Repaint (`TooltipEchoStripLatch.SampleMousePositionProbe`, armed by every `op=pointer`) reports `Event.current.mousePosition` beside `Input.mousePosition` in the SAME pass: the EVENT position reads `eventLocal=-8.0,-8.0` -> `eventScreen=0.0,0.0` on every probe of the flight, while the POLLED position tracks the commanded point exactly (`input=133.0,558.0` -> `inputGuiY=162.0` against a commanded `133,161`; `input=133.0,523.0` -> `inputGuiY=197.0` against `133,196`). So Unity's polled position follows a warped cursor and the position IMGUI computes its hit test from never moves at all - it stays pinned at the screen origin, which is outside every control. Neither foreground nor a synthetic mouse event is the cause: both reach-further flags were CONFIRMED APPLIED on the same run and changed nothing (`fgOutcome=attached` on the first hover move, `fgOutcome=already` on the second, `fg=true` after both, the relative `SendInput` pair accepted on both), and both answers still read `tooltip=-`. See GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT and the `pointer` row of 6.2 |
| screen messages | **0 of 95** | **0 of 95** | **0 of 95** | **0 of 95** | all 95; zero `ScreenMessage` lines in any of the six wave-2 logs, and neither wave 3 nor wave 4 re-measures the class - one drives modals and the other windows, and neither is a screen message |
| empty-vs-populated PAIRS | **0** | **9** | **9** | **9** | Missions tab, Recordings tab, Timeline Overview, Timeline Re-Fly, Kerbals Roster, Kerbals Outcomes, Logistics, Career Milestones and the Structure window each now hold BOTH forms. Wave 1 could hold none: it flew one host. Wave 3 adds no surface pair: its `dlg-baseline-no-modal` / `dlg-teardown-no-modal` pair is a no-modal bracket around the six dialog captures, not the empty and populated forms of one surface. Wave 4 adds none either: its four Settings-launched states are one surface in four states, and the closest thing to a pair - the same rows before and after a real run - is a RESULTS pair rather than an empty-vs-populated one |

THE DENOMINATOR, re-derived rather than carried forward. The named classes sum to **57**
countable surfaces (14 windows + 12 tabs + 21 dialogs + 7 overlays/markers/badges + 2 tooltip
surfaces + 1 toolbar button), so wave 1's coverage was **22 of 57**, after wave 2 it was
**27 of 57** (13 + 12 + 0 + 2 + 0 + 0), after wave 3 it was **33 of 57**
(13 + 12 + 6 + 2 + 0 + 0) - the whole of that movement the dialog class - and after wave 4 it
is **34 of 57** (14 + 12 + 6 + 2 + 0 + 0), the one further surface being the global runner and
the window class now CLOSED. The two classes still at zero are the ones nothing has yet found a
path to.
THE "22 OF 105" THIS PARAGRAPH USED TO CARRY WAS A
HAND-SUM ERROR and is corrected here rather than repeated: 105 does not reconcile with the
class list it names under any reading (the classes give 57; adding appendix 5's ~38 in-window
sections gives 95), and the same figure is in the research note's appendix 5, which is left as
the historical record. The numerator was always right - it is 10 windows + 12 tabs. Screen
messages and in-window sections stay outside the denominator, covered only by whatever happened
to be on screen.

Of wave 1's 26 captures, **8 photograph an essentially empty surface** by node count:
`ksc-kerbals-roster-advanced` (9), `ksc-structure-advanced` (3), `ksc-timeline-refly-advanced`
(39), `ksc-timeline-basic` (39), `ksc-career-contracts-advanced` (17),
`ksc-career-strategies-advanced` (17), `ksc-logistics-advanced` (58) and `ksc-logistics-basic`
(58); and the Recordings tab's 415 nodes are 16 collapsed group headers with zero leaf rows.
Wave 2 answers SIX of those eight with a populated counterpart on a committed host
(`ksc-logistics-advanced` / `-basic` -> `ib-logistics-expanded-advanced` 190 nodes and
`ib-logistics-basic` 188; `ksc-structure-advanced` -> `ib-structure-route-advanced` 78 and
`ib-structure-mission-advanced` 316; `ksc-timeline-refly-advanced` ->
`bd-timeline-refly-advanced` 93; `ksc-timeline-basic` -> `bd-timeline-overview-basic` 156;
`ksc-kerbals-roster-advanced` -> `cek-kerbals-roster-advanced` 45). The two it does NOT answer
are `ksc-career-contracts-advanced` and `ksc-career-strategies-advanced`: no committed fixture
carries an ACCEPTED contract or a live `STRATEGY` node, so `cek-career-contracts-advanced` (53
nodes) and `cek-career-strategies-empty-advanced` (53) are second pictures of the same empty
form - a corrected reading rather than a new picture, see 6.1. The biggest bodies the wave
produced, for scale: `play-missions-missions-flight-advanced` at 11248 nodes over 243 injected
recordings, `play-timeline-overview-flight-advanced` at 2028, `ib-missions-recordings-expanded-
advanced` at 877.

Three causes account for every gap, and only the first is a fixture problem: the fixture had no
data of that shape (Career contracts, Kerbals roster, Logistics routes, STASH); the state needs
a CLICK and no verb clicks (every expanded row, every detail panel, the Group picker, the link
picker, populated Structure); or the surface is outside the recorder's reach by construction
(all 21 dialogs, all 7 overlays, every populated tooltip). Section 6 is organised by those
three, with the cheapest route per target. Wave 2 closed the whole of the first cause it had
fixtures for and most of the second; what it did NOT close is the third, and the hover finding
is why - see 6.3.

ONE BLIND-SPOT ROW OF THE TABLE ABOVE IS NARROWER THAN IT SAYS, measured on
`play-main-watchmode-advanced`. "IMGUI outside any window -> in the `.gui.json`? NO" holds for
the map markers and the in-world labels, but NOT for the Watch Mode overlay: its two labels are
ROOT-LEVEL nodes in that dump (`Watching: Part Showcase - Lights v1  (295 m) [Horizon]` at
`rect=[170,15,300,22]` and `[ ] return  |  V camera  |  W cycle` at `[170,37,300,18]`). The
recorder patches `GUI.DoLabel` and not only `GUI.DoWindow`, so IMGUI drawn outside a window is
captured when it goes through a patched funnel; what is absent is uGUI and anything drawn
through `GUI.DrawTextureWithTexCoords` (the markers' icons).

### 2.1 Known inconsistencies between the two source notes

The two research notes were written by different passes and disagree in seven places. This
document carries the values named below with that caveat; none has been reconciled against
the code yet, and each is a one-grep check for whoever next touches the area.

- Screen-message totals: the inventory counts 95 producers (107 raw minus 12 excluded); the
  exposure note counts 77 `ParsekLog.ScreenMessage` sites across 23 files. The difference is
  consistent with the remaining 18 being `ScreenMessages.PostScreenMessage` calls, but neither
  note states that split. This document uses 95 and attributes it to the combined grep.
- EXPOSED row count: a mechanical re-tally of the exposure note's Class column gives 272, its own
  header says 265 + 7 "EXPOSED-in-Advanced-only". Section 4 folds the two together. The Class
  column also carries three non-class values (`NOT PRESENT` x4, `n/a` x2, `(see next rows)` x1)
  that the note's tally absorbs silently.
- Per-area screen-message citations in the inventory's section 0b (`ParsekFlight.cs:8313`,
  `:8371`, `:10764`, `:13388`, `:13393`, `:12851`, `:4287`, `:12642`, `:3498`, `:2912`, and the
  Gloops sites `:17388` / `:17499`) do not appear in its own appendix 2, so one of the two lists
  is partial.
- Item P14 cites `docs/user-guide.md:336` for two different claims ("Show ghosts in Tracking
  Station" and "pin on click"); at most one is that line.
- The Missions warp-to-launch dialog is cited at `UI/MissionsWindowUI.cs:2993` in the body and
  `:3006` in the dialog tally; both may be real (method vs the `SpawnPopupDialog` call). The
  dialogs table uses `:3006`, the line the 21-site grep counted.
- Appendix 2 of the inventory prints `PParsekLog.ScreenMessage` (double P) in about 15 rows; a
  transcription artefact, not a code symbol.
- The census host is named `fixtures/local-saves/c1-gui` in the inventory header and in the
  spec (`GUI-1-census-ksc.toml:34-44`), but appendix 4 also spells sibling hosts as
  `harness/fixtures/saves/...`; only the local-saves form is the staged fixture.

## 3. The structure

Each window section below says what the window draws at HEAD: sections, columns, buttons and
their states, hovers, Basic vs Advanced, and empty states. Code is cited by type and method
rather than by line. A `Pictures:` line names census captures that show the surface; what the
census has and has not photographed is section 2's tally and section 6's per-row lane column,
not these lines.

**Shared style.** Every window draws the same few things the same way:

- One countdown, `ParsekTimeFormat.FormatCountdown`: `T- ` then the two largest units
  (`T- 2d 4h`, `T- 5m 30s`), `T+ ` once the moment is behind, ` (!)` appended when warned, in
  `ParsekUI.CountdownTextColor` (#ffcc66). The Missions summary, the Logistics `Next` cell, the
  Real Spawn Control `Spawns` column, the Timeline's first row after
  now and the Recordings Status of a flight still ahead all read it. The Log's `T+h:mm:ss`
  elapsed clock is a different thing (time since the mission's first event) and keeps its form.
- One cross-link spelling, `Go to` (the Missions partner rows, the Logistics route, the
  Timeline rows).
- One palette: status colours come from `ParsekUI.StatusColor` (green good / active, yellow
  caution, red broken / dead, grey inert, cyan informational or player-made, violet the
  Logistics Paused accent); muted text (second lines, detail lines, empty-list sentences) is
  `ParsekUI.MutedTextColor` (0.78 grey); dimmed rows are `ParsekUI.DimTextColor` (white at
  45%). Colours with a meaning of their own stay local: the Recordings phase legend, its ended
  grey (0.5) and static orange, the Real Spawn Control `Leaving` orange, the Timeline's grey
  `!IsEffective` rows and `now` label, and the 0.9 column-header grey.
- One empty-list voice: a single sentence in `ParsekUI.GetEmptyStateStyle` (muted grey, plain
  label), never `(none)`. A table that has headers keeps them and shows the sentence as its
  one body row (the Log and the Route History).
- Row actions sit in an Interact column at the Missions widths
  (`MissionsWindowUI.InteractButtonWidth` 100 single, `InteractPairButtonWidth` 48 pair,
  `InteractCellInset` 8): the Missions and Logistics Interact cells, the Logistics near-miss,
  dormant and hidden-mission rows, and every Timeline row action. Stock-style dialog buttons
  and window footers keep their own widths.

### 3.0 Window index

The 14 IMGUI windows Parsek draws, in the main window's own button order (the order
`TestCommands/TestCommandUiAction.cs` pins for the same reason). Row 5 is an unused number kept
so the rows other sections cite keep their numbers; the Route History is row 8b, the second
instance of row 8's class.

| # | title | class + host line | scenes | seam token | census labels |
|---|---|---|---|---|---|
| 1 | `Parsek` (main) | `ParsekUI.DrawWindow`; hosts `ParsekFlight.OnGUI`, `ParsekKSC.OnGUI` | FLIGHT, SPACECENTER | `main` | `ksc-main-basic/advanced`, `flight-main-basic/advanced` |
| 2 | `Parsek - Missions` | `UI/RecordingsTableUI.cs` (chrome and the Recordings tab), `UI/MissionsWindowUI.cs` (the Missions tab) | FLIGHT, SPACECENTER | `missions` (tabs `missions`, `recordings`) | 3.2 |
| 3 | `Parsek - Timeline` | `UI/TimelineWindowUI.cs` | FLIGHT, SPACECENTER | `timeline` (tabs `overview`, `details`, `rewindff`, `refly`, `contracts`, `strategies`, `facilities`, `milestones`, `tech`) | 3.3 |
| 4 | `Parsek - Kerbals` | `UI/KerbalsWindowUI.cs:205` | FLIGHT, SPACECENTER | `kerbals` (tabs `roster`, `outcomes`) | 2 labels |
| 5 | (unused number) | - | - | - | - |
| 6 | `Parsek - Logistics` | `UI/LogisticsWindowUI.cs:444` | FLIGHT, SPACECENTER | `logistics` | `ksc-logistics-advanced/basic` |
| 7 | Logistics round-trip link picker (`Link round-trip partner`) | `UI/LogisticsWindowUI.cs` (`DrawLinkPicker`) | as its host | excluded `TestCommandUiAction.cs:374-377`; reached by `op=picker picker=link` | `ib-logistics-linkpicker-advanced` (GUI-3, 198 nodes, `windows=4`) |
| 8 | `Parsek - Log: <mission>` (bare `Parsek - Log` untargeted) | `UI/StructureListWindowUI.cs` (`BuildWindowTitle`) | FLIGHT, SPACECENTER | `structure` (`op=target mission=`) | `ib-structure-route-log-advanced`, `ib-structure-mission-advanced` (GUI-3), `ksc-structure-advanced` (GUI-1) |
| 8b | `Parsek - Route History: <route>` | `UI/StructureListWindowUI.cs` (second instance, `RouteHistoryWindowIdKey`) | FLIGHT, SPACECENTER | `routehistory` (`op=target route=`) | `ib-routehistory-advanced` (GUI-3) |
| 9 | `Parsek - Settings` | `UI/SettingsWindowUI.cs:128` | FLIGHT, SPACECENTER | `settings` | `ksc-settings-advanced/basic` |
| 10 | `Parsek - Real Spawn Control` | `UI/SpawnControlUI.cs` | FLIGHT only | `spawncontrol` | `rsc-spawncontrol-before-warp` (RSC-1, one candidate row) |
| 11 | `Gloops Flight Recorder` | `UI/GloopsRecorderUI.cs` | FLIGHT only | `gloops` | `flight-gloops-advanced` |
| 12 | `Parsek - Test Runner` (Settings-launched) | `UI/TestRunnerUI.cs` | FLIGHT, SPACECENTER | `testrunner` | `ksc-testrunner-advanced`, and four GUI-12 states on `career-earned-ksc`: `cek-testrunner-idle-advanced`, `-collapsed-advanced`, `-category-advanced`, `-results-advanced` (3.11) |
| 13 | `Parsek - Test Runner` (global Ctrl+Shift+T) | `InGameTests/TestRunnerShortcut.cs` | ANY scene but LOADING | `testrunnerglobal` | three GUI-12 states: `cek-testrunnerglobal-idle-advanced`, `-collapsed-advanced`, `-category-advanced` (3.11) |
| 14 | `Set Parent Group` / `Manage Groups` | `UI/GroupPickerUI.cs` | as its host | excluded from the window table; reached by `op=picker picker=manage|setparent` | both titles on GUI-3 (`ib-missions-grouppicker-manage/setparent-advanced`) and GUI-4 (`bd-missions-grouppicker-manage/setparent-advanced`) |

Two asymmetries in that table are mechanical facts, not presentation choices:

- Window 13 is the ONLY one that calls raw `GUILayout.Window` instead of
  `ClickThruBlocker.GUILayoutWindow`, so it is the only Parsek window with no click-through
  protection; it compensates with its own `windowRect.Contains(Event.current.mousePosition)`
  input lock. It is also the only row drawn OUTSIDE both scene hosts' `showUI` gate - its draw
  is in its own `OnGUI` - which is why the seam exempts it, and only it besides `main`, from
  the hidden-host settle refusal (`TestCommandUiAction.WindowDrawsOutsideHostShowUi`).
- The TRACKING STATION hosts no Parsek window at all. `ParsekTrackingStation.OnGUI` is the
  pause gate plus `DrawAtmosphericMarkers()`, and `UiAction` answers
  `REJECTED ui-host-unavailable` there.

House layout rules that hold across every window: the footer order is tooltip echo strip, then
`Close` as the last content row, then the resize handle and `GUI.DragWindow()`; and window
BODIES are never complexity-gated - only launchers, content draw sites and the mode-change
close handler are (`UiSurfaceVisibility`, 3.16).

### 3.1 Parsek (main window)

Purpose: the mod's single entry point. Its body IS the launcher column; there is no row model.

Hosts and gates: FLIGHT + MAPVIEW (`ParsekFlight.OnGUI`: `!PauseMenuGate.IsPauseMenuOpen()`,
then `showUI`, then a non-null `ParsekUI.GetMainWindowStyle()`) and SPACECENTER
(`ParsekKSC.OnGUI`: the same three gates, `showUI` first). `showUI` is written only by the
stock toolbar button handlers and the window's `Close`. Both hosts draw the window at
`GUILayout.Width(250)` and reset its height to 0 each frame, so it is always exactly as tall as
its content; it has no resize handle. The title `Parsek` uses `GetMainWindowStyle()`, the
shared opaque window style, bold and 2 px larger than every sub-window title. No `UiSurface`
key gates the window itself. After it, the flight host draws nine sub-windows (Missions,
Timeline, Kerbals, Logistics, Log, Settings, Real Spawn Control, Gloops, Test Runner) and the
KSC host seven (the same minus Real Spawn Control and Gloops). The flight host also draws the
ghost map markers and the in-world ghost labels outside the `showUI` gate (3.13).

Contents, top-down (`ParsekUI.DrawWindow`): a 10 px gap, the launcher column, the Supply
Route candidate banner between `Missions` and `Logistics` while one is pending, the two-line
tooltip echo strip, then a footer row with the version label on the left and `Close` filling
the rest.

| launcher | hover | gate | action |
|---|---|---|---|
| `Real Spawn Control ({N})` | `Turn a recorded craft passing nearby into a real vessel.` | `InFlight && IsVisible(MainButtonSpawnControl)` | toggles the Real Spawn Control window; greyed when `NearbySpawnCandidates.Count == 0`, reason `No recorded craft is passing nearby` carried by `DisabledHoverEcho`; followed by its own gap |
| `Timeline` | `Every recorded flight and career event on one clock.` | `IsVisible(MainButtonTimeline)` | toggles the Timeline |
| `Missions` | `Your missions, and the recordings they are built from.` | `IsVisible(MainButtonRecordings)` | `ToggleRecordingsWindow()` |
| `Logistics` | `Supply routes that repeat a delivery you already flew.` | `IsVisible(MainButtonLogistics)` | toggles the Logistics window; tinted red when `LogisticsButtonState.AnyRouteHardBroken`, else cyan while a Supply Route candidate banner is pending (broken outranks the hint) |
| `Kerbals` | `Who is reserved, flying or retired on timeline.` | `IsVisible(MainButtonKerbals)` | toggles the Kerbals window; a gap opens the group only when the button draws |
| `Gloops Flight Recorder` | `Record a ghost-only flight that your career ignores.` | `InFlight && IsVisible(MainButtonGloops)` - **never true**: the key is retired in both modes (3.10, 3.16) | would toggle the Gloops window |
| `Settings` | `Interface, ghosts and data - plus more in Advanced.` | `IsVisible(MainButtonSettings)` | `ToggleSettingsWindow()` |

The Supply Route candidate banner (`RouteRunPrompt.HasPendingPrompt`): a boxed label
`Supply Route candidate:` over the candidate's name, then `Open Logistics` (opens the
Logistics window and clears the prompt) and `Dismiss` (hover `Drops the suggestion; undo it in
Logistics > Dismissed.`; dismisses the candidate). A prompt dismissed elsewhere is cleared on
a Layout pass only, so Layout and Repaint draw the same controls.

Basic vs Advanced: every launcher reads the frame-latched mode through its `UiSurface` key.
Basic drops `Real Spawn Control`, so both modes draw five buttons at the Space Center
(`Timeline`, `Missions`, `Logistics`, `Kerbals`, `Settings`), and in flight Advanced draws six
and Basic five. Each separator lives inside the block it separates, so a hidden button never
leaves a double gap.

Pictures: `ksc-main-basic` / `ksc-main-advanced` (GUI-1, `2026-10-01_1221`),
`bd-main-basic` / `bd-main-advanced` (GUI-4). No picture: the Supply Route candidate banner,
either Logistics tint, an enabled `Real Spawn Control`, a populated tooltip strip.

### 3.2 Parsek - Missions (chrome, Missions tab, Recordings tab)

Purpose: two views of the same recorded data - missions as whole units, and the raw
per-recording table. The chrome and the Recordings tab live in `UI/RecordingsTableUI.cs`; the
Missions tab body in `UI/MissionsWindowUI.cs`, drawn inside this window.

Chrome: title `Parsek - Missions`; a two-entry `GUILayout.Toolbar` (`Missions` hover `Your
flights grouped as whole missions - the higher-level view.`, `Recordings` hover `The raw table
of every recorded vessel, one row each.`) drawn only when `VisibleTabCount(complexity) > 0`;
the tab content; then the tab's bottom bar. The default tab is `Missions` (`TabMissions = 0`),
transient, never persisted. Input lock `Parsek_RecordingsWindow` on CAMERACONTROLS while the
mouse is inside. Minimum and first-open width 1355 (`DefaultWindowWidth`); on a narrower screen
the window is capped to the screen and the pinned header scrolls sideways with the rows
(`WideWindowScroll`, section 7). Bottom bar: the single-line tooltip echo strip, then
`New Group` (Recordings tab only; hover `Create an empty folder and name it. Put recordings in
it with the G button on each row.`) and `Close`, the resize handle and drag.

Basic/Advanced: the only `IsVisible` call in `RecordingsTableUI.cs` is inside
`VisibleTabCount`, which returns 2 in Advanced and **0** in Basic - zero, not one, because a
one-entry toolbar is noise and the title already carries the identity. Its consumers are
`ClampTabIndexForMode`, the tab-bar draw guard and the content dispatch, which is PINNED to
the Missions tab when no toolbar draws. So Basic hides the tab bar, the whole Recordings table,
`New Group` and every route to the group picker, which is why
`CloseGroupPickerForModeChange` exists. The Missions tab reads `MissionsLoopControls` (below).

**Missions tab.** Row model: one row per physical vessel or EVA kerbal, built by
`MissionVesselRowBuilder.Build` (`MissionVesselRows.cs`); depth is SEPARATION LINEAGE only,
never time; roster atoms are not rows. Vessel names come from `MissionVesselNaming`, shared
with the Log: another mission's vessel reads `X (mission 'Y')`, and two genuinely different
own vessels with one name are numbered `Kerbal X [2]` in the row, in every phrase piece that
names it and in the expanded `after undock: X left` line (a ship KSP re-pidded at an undock is
the same vessel and keeps its name). Empty state: `No missions recorded yet.`, one sentence in the
house muted grey (`ParsekUI.GetEmptyStateStyle`, the empty-list voice of every window).

The header row sits outside the scroll view and every header cell is 32 px tall
(`ColHeaderHeight`). Each mission is a two-line BAR whose lines lay out the same columns as
every row under them (`DrawMissionValueRow` / `DrawMissionActionLine`), so each heading
describes the first row under it:

| # | header | width const | mission bar line 1 | mission bar line 2 | vessel / interval / partner / chapter rows |
|---|---|---|---|---|---|
| 1 | (blank enable slot) | `ColW_Enable` 20 | blank | blank | blank |
| 2 | `#` (sortable) | `ColW_Index` 30 | per-TREE index | the collapse caret (down / right) | include checkbox (Advanced) or blank |
| 3 | `Missions and vessels` (sortable) | expand | bold title (double-click renames), then right-aligned (Advanced) `Clone` and `Warp to...`, then `Log` (`DrawMissionNameCell`) | the summary, then (Advanced) `Delete` and the loop row, right-aligned (`DrawMissionSummaryNameCell`) | connector + caret + name + `EventPhrase` |
| 4 | `Start time` (sortable) | `ColW_StartTime` 120 | the mission span's start date | blank | `PrintDateCompact` |
| 5 | `Start event` | `ColW_StartEvent` 110 | the first vessel row's start event (`MissionPresentation.MissionStartEventText`) | blank | event word |
| 6 | `End event` | `ColW_EndEvent` 85 | the primary vessel's outcome | blank | terminal word |
| 7 | `End time` | `ColW_EndTime` 120 | the mission span's end date | blank | date |
| 8 | `Interact` (centred plain label) | `ColW_Interact` 116 | `Watch` / `W*` | `Rewind` or `Forward` when one applies, else a reserved button-sized slot | `Stash` + `Seal` or `Fly` + `Seal` (vessel and interval rows), `Go to` (Docked partner rows), blank (chapter rows) |

`MissionsTabColumnSequenceTests` pins the column sequence of the header against both bar
lines, the vessel row, the interval row, the chapter header row and both Docked partner row
kinds, in both modes: no column is mode-gated.

The `#` header sits over the index column's contents: its merged [enable + index] cell
(`indexHeaderCellStyle`) has no left inset, so its index slot starts at the rows' index-cell x,
and `#` is left-aligned in a label-skin style (`indexHeaderLabelStyle`) like the index number
and the collapse caret below it, with the sort arrow trailing it. The inset moves to the cell's
right padding (`IndexHeaderRightPadding`), so the cell keeps its width and no other header moves
(`TheIndexHeaderSitsOverTheIndexColumnContents`).

The Interact column carries every per-row action, on one width system:
`InteractButtonWidth` 100 for a single button (`Watch` on line 1 and `Rewind` / `Forward` on
line 2 at the same x, `Go to`, and `Log` in the name cell), `InteractPairButtonWidth` 48 for
each half of a pair (`Fly` / `Stash` + `Seal`), `InteractButtonGap` 4, so
`2 * pair + gap == single`;
the column is the single plus an 8 px inset each side (`InteractCellInset`). The Re-Fly pair
is the Recordings tab's own cell drawn at that geometry
(`RecordingsTableUI.DrawReFlyColumnCell`).

The Advanced loop controls form a fixed 2x2 grid in the name cell, right-aligned beside `Log`:
column A (`LoopGridColumnAWidth` 70) holds `Clone` over `Delete`; column B
(`LoopGridColumnBWidth` 92) holds `Warp to...` on line 1 and, on line 2, `Loop` (28) + the
checkbox slot (22) + `every` (42), so `Loop` starts at Warp's left edge; under `Log` the value
field (56) + 4 + the unit button (40) fill the 100 px Log slot, so the period ends at Log's
right edge (`LoopCellWidth` 192 = column B + the Log slot). The line reads
`Loop [x] every [10] [sec]`. A locked period (phase-locked or re-aim) is one value-only label
in the Log slot (`Loop [x] every ~13d-19d`); its qualifier (`Mun window, varies`,
`Kerbin rot`) and the locked-state sentence ride its hover
(`MissionPresentation.BuildLockedPeriodTooltip`). Every control is fixed-width, has zero
horizontal margin and is centred on its 22 px line (the checkbox, field and unit button each in
a 22 px centring slot), so nothing moves between looping off and on, Auto and manual, or
locked. `Warp to...` draws in every Advanced state and is greyed with its reason when it
cannot act. A route-bound tree draws `Looped by route` across the whole loop row instead of
the toggle and period (the route owns the repeats; the hover `Looped by route: <name>` names
the route).
`MissionsTabColumnSequenceTests.TheLoopGridColumnsAreFixedAndLineUpAcrossBothLines` pins those
edges and the zero margins. Basic draws no grid: the summary takes the whole name cell, `Log`
sits right-aligned on line 1, and the route label sits right-aligned after the summary in
Basic's words, `Run by route` with the hover `Run on the schedule of route '<name>'.` (Basic
reads no loop word). The label's words follow the frame-latched mode
(`MissionsWindowUI.RouteBoundLabel` / `RouteBoundTooltip`).

The summary line (line 2) is the title's font size in a muted colour
(`MissionSummaryTextColor`) and wraps rather than clipping: body, duration, crew and outcome,
joined by middle dots (`MissionPresentation.SummarySeparator`). In Advanced a looping mission's summary also
carries the amber `Next launch T- <countdown>` (the house countdown,
`ParsekTimeFormat.FormatCountdown`: `T- `, then the two largest units, in
`ParsekUI.CountdownTextColor` #ffcc66); a WARNED countdown (station drift, an arrival
refusal, a launch outside its alignment tolerance) ends in `(!)` inside the same amber segment
(`MissionPresentation.SummaryCountdownWarningMarker`), and the warning's explanation rides the
summary's hover with the two state words' explanations. The marker's appearance and clearing
are logged once per transition (`next-launch countdown warned=`).

**Event cells never clip silently.** Every Start event / End event cell, and the mission bar's
value cells, draws through `DrawEventCell`: the cell shows the event word, and its hover
(`MissionPresentation.BuildEventCellTooltip`) is the fuller phrase when one exists - the
same-tree dock partner (`Docked with Depot Station Duna I`) or the dock-graph partner with its
mission - else the word itself when it measures wider than the cell, else nothing. The event
words are single short words (`Launch`, `Decoupled`, `Undocked`, `Docked`, `Boarded`, `EVA`,
`Broke off`, `Broke up`, `Placed`, `Switch`, and the terminal words up to `Disassembled`).

**Collapse caret.** `Mission.Collapsed` (saved as `collapsed`; a save's older per-mission
`archived` key loads into it) hides a mission's vessel, interval, chapter and partner rows;
the two-line bar stays. The toggle is a clickable caret glyph in the `#` column on line 2,
under the index number (`DrawMissionCollapseCaret`): a down caret (U+25BC) while the rows
show and a right caret (U+25B6) while they are hidden
(`MissionPresentation.MissionCollapseCaretGlyph`), hover `Collapse this mission` /
`Expand this mission`. It is a frameless Button in a label style (the click style of the
Recordings group carets and the Logistics section carets), and its hit area is the whole
`ColW_Index` cell (30 px) at least one row (22 px) tall. The draw loop reads the flag once
before the bar draws, so a click lands next frame. The Timeline GoTo cross-link QUEUES
expanding a collapsed target and applies
it on a Layout pass. Seam: `op=expand window=missions key=mission:<id>` drives it (expanded =
not collapsed); it is a NAMED-KEYS-ONLY set, so `key=all` / `key=none` skip it and its counts.
The Recordings tab's per-recording Archive (`rec.Hidden`) is a different mechanism.

Mission bar controls, with their gates:

| control | where | backend | disabled / hidden |
|---|---|---|---|
| title double-click | line 1 name cell | `CommitMissionRename` -> `MissionGroupLink.RenameMissionGroup` for an original, `MissionStore.RenameMission` for a clone | a group-name collision refuses the whole rename, Warn-only |
| `Log` | line 1 name cell, right-aligned (right of the Advanced loop grid) | `ParsekUI.OpenStructureWindowForMission` | never |
| `Watch` / `W*` | line 1 Interact, single width | `flight.EnterWatchMode` / `ExitWatchMode` | greyed with one of two reasons (`MissionWatchDisabledReason`: `Watching only works while you are flying`, `Nothing from this mission is flying right now`). Both modes |
| `Rewind` / `Forward` | line 2 Interact, single width, under Watch | `RecordingsTableUI.DrawMissionRewindForwardButton` over the mission root recording | greyed on `CanRewind` / `CanFastForward` with the store's reason; not drawn when neither applies (a button-sized rect is reserved instead, so line 2 keeps its height), and not drawn at all when the mission's root recording owns no launch save (`RecordingStore.GetRewindRecording` null), the same rule as the Recordings tab's blank Rewind cell. Both modes |
| collapse caret (down / right) | line 2 `#` cell, under the index | writes `Mission.Collapsed` | never. Both modes |
| `Clone` | loop grid column A, line 1 | `MissionStore.Clone` | HIDDEN in Basic |
| `Delete` | loop grid column A, line 2 | `MissionStore.Delete` | greyed by `CanDelete` (`A flight always keeps its first mission`); HIDDEN in Basic |
| `Warp to...` | loop grid column B, line 1 | confirm dialog, then an in-place forward jump to 15 s before the next relaunch | HIDDEN in Basic; in Advanced always drawn, greyed with the first failing reason in order (`MissionWarpToDisabledReason`): `Warping works in flight or at the Space Center`; `Turn Loop on to warp to the next launch` (on a route-bound tree `A supply route repeats this mission, not Loop`); `This mission does not repeat on a schedule yet`; `The next launch is not ahead of you` |
| `Loop` + toggle + `every` | loop row column B, line 2 | `CommitMissionLoopToggle` -> `MissionStore.SetLoopEnabled` | HIDDEN in Basic; not drawn on a route-bound tree |
| `Looped by route` (Advanced) / `Run by route` (Basic) | line 2: across the whole loop row (Advanced) or right-aligned after the summary (Basic) | label; hover `Looped by route: <name>` / `Run on the schedule of route '<name>'.` | only on a route-bound tree |
| loop-period cell | loop row, Log slot, line 2 | `CommitMissionLoopPeriod`, four states (locked / auto / manual / editing) | HIDDEN in Basic; not drawn on a route-bound tree; an open edit is DROPPED uncommitted on a Basic switch |

The rows under the bar: one row per vessel with a caret (Advanced) that opens its per-vessel
interval rows (`Kerbal X (pod x1, probe x1, crew x3)`, `after decouple: Kerbal X Probe left -
...`; Advanced only), chapter group header rows (their tri-state toggle, the `[~]` marker and
the dimming are Advanced-only), and `Docked partner:` rows naming the partner vessel and its
mission (`Docked partner: Kerbal X (mission 'Kerbal X #2') (loiter, 2h 25m - not recorded)`),
plus the foreign partner-journey rows an include pulls in (Advanced only). Every Docked
partner row carries `Go to` in the Interact column, which opens the partner's mission
(`DrawInteractGoTo` -> `ShowMissionForRecording`); a dock THIS mission recorded with another
mission's vessel draws a Docked partner row of its own (`DrawRecordedDockPartnerRow`, no
include toggle). `MissionEventDigest` is the source of the partner naming and the `Go to`
target. Vessel and interval rows' single-line data cells use
`compositionCellLabel.clipping = Overflow`, so descenders are not clipped in a 22 px row.

What `MissionsLoopControls` hides in Basic: the include checkboxes, the loop grid (`Clone`,
`Delete`, `Warp to...`, `Loop`, the period cell), the summary's `Next launch T- ...` piece, the
per-vessel interval rows, the vessel caret, the foreign partner-journey rows and the
loop-selection styling (dimmed excluded vessels, the `(partial)` suffix, the chapter header's
toggle, dimming and `[~]` marker). Basic keeps the title and rename, the summary, `Log`,
`Watch`, `Rewind` / `Forward`, the collapse caret, `Fly` / `Stash` / `Seal`, `Go to`, the
chapter headers, the Docked partner rows and the route label (`Run by route` in Basic).

Pictures: `ksc-missions-missions-advanced`, `ksc-missions-basic`,
`ksc-missions-missions-collapsed-advanced` and `ksc-missions-missions-expanded-advanced`
(GUI-1, `2026-10-01_1221`, the `c1-gui` career); `bd-missions-missions-collapsed-advanced`,
`bd-missions-missions-expanded-advanced` and `bd-missions-basic` (GUI-4, `2026-10-01_2044`,
`bdock-recorded`: two missions, Docked partner rows, `Fly` / `Stash` / `Seal`, greyed `Watch`
and `Warp to...`). No picture: loop ON with a locked period, `Looped by route`, `Forward`,
`W*`, an inline rename, `(partial)` / dimmed rows, a warned countdown.

**Recordings tab.** A pinned header row outside the scroll view (`DrawRecordingsTableHeader`),
then a body of four row kinds. Columns, left to right, with the shared header / body width
constants: merged [toggle + `#`] 58 (`ColW_Enable` 20 + `ColW_Index` 30 + 8), `Name` expand,
`Phase` 120, `Site` 80, `Launch` 110, `Duration` 70, `Status` 120, `Group` 60, `Loop` 60,
`Period` 90, `Watch` 50 (flight only), `Rewind` 60, `Re-Fly` 90, `Archive` 80. The table uses
the house table styles: the pinned header opens with `GetTableHeaderRowStyle()` (the scrollbar
gutter is its right padding), the four row kinds with `GetTableRowStyle()`, the list area with
`GetTableBodyBoxStyle()`, and the body labels are built on `GetTableCellStyle()`
(`TableRowInsetAlignmentTests`, including a four-row-kind column-sequence gate). Every header
cell is 32 px tall. Seven headers sort (`#`, `Name`, `Phase`, `Site`, `Launch`, `Duration`,
`Status`); `SortColumn.LaunchTime` ascending is the default. The Status cell's hover leads with
where the flight ended (`Ends: ...`), an EVA's `EVA from <vessel>` (`BuildStatusPlaceTooltip`)
and `Max altitude <a>, max speed <v>` (`BuildStatusStatsTooltip`); a leaf's Status shows its
terminal word for debris too, while a folder's (`GetGroupStatus`) ignores debris.

Above the table, while the shared time-range filter is active (`DrawTimeRangeFilterIndicator`):
`Filtered: <preset>` or `Filtered: <from> - <to>`, and `Clear` (hover: it also resets the
Timeline's range sliders). Empty state: `No recordings yet.` (muted grey). A row's Status is
the house countdown while the flight is ahead (`T- 2d 4h`, amber), `T+ 5m 3s` in the palette
green while it flies, then its ending word.

Row kinds and what each blanks: GROUP HEADER (`DrawGroupTree`; Period and Re-Fly always blank),
CHAIN BLOCK and GROUPED BLOCK (`DrawRecordingBlock`; Phase, Period, Watch, Re-Fly, Archive
blank), the virtual STASH group (the Unfinished Flights group, `UI/UnfinishedFlightsGroup.cs`;
Group, Loop, Period, Watch, Rewind, Re-Fly blank), and the RECORDING leaf (all columns). There
is no separate detail row: expansion is a caret that renders CHILD rows of the same kinds,
with box-drawing connectors and per-level indents.

Aggregate cells: a folder's `Duration` is the SPAN its descendants cover
(`GetGroupSpanDuration`: latest EndUT - earliest StartUT, dataless members skipped), the same
figure a chain block shows, and the folder / chain Duration sort keys use it; the STASH row's
Duration is blank. A subfolder's LABEL drops its parent's `parent + " / "` prefix when drawn
under that parent (`GroupPickerPresentation.DisplayLabelUnderParent`; the group picker tree
does the same), so a mission's auto subfolders read `Debris` / `Crew`. A leaf's `Period` cell
is BLANK while its Loop is off, with the loop-off reason as the blank cell's hover
(`LoopPeriodBlankCellTooltip`). A MISSION folder (a tree's auto-generated root group) draws no
block row for its tree-root vessel: that vessel's segments are its own direct child rows
(`FindRootVesselBlockIndex` / `FlattenAbsorbedBlock`), while every other vessel keeps its
block. The mission row then writes Loop to those absorbed segments WITH the auto loop range
and to its other loopable descendants WITHOUT (`SplitAbsorbedLoopWrite`), and its Group cell
is `G` (folder parent) + `S` (every segment of the launched vessel).

Notable control semantics in the body:

| control | backend | gate |
|---|---|---|
| per-row Enable toggle | `rec.PlaybackEnabled` | never disabled. See finding P1 |
| header select-all Enable | writes every committed recording | never; ignores the filters, and the hover says so |
| Loop toggle (row) | `rec.LoopPlayback` + `ApplyAutoLoopRange` | blank when `ShouldSuppressRowLoopUi`; greyed and write-blocked when route-bound |
| Loop toggle (header / folder) | `BulkSetLoopPlayback(..., applyAutoRange: false)`; a mission folder that absorbed its launched vessel's block writes those segments with `applyAutoRange: true` (`SplitAbsorbedLoopWrite`) | same route gate (over descendants plus absorbed segments); deliberately does NOT re-narrow the window for the rest |
| Loop toggle (chain / block) | `BulkSetLoopPlayback(..., applyAutoRange: true)` | same glyph, different semantics, by design |
| `Archive` header toggle | `GroupHierarchyStore.HideActive` | the one header toggle that writes NOTHING to a recording; it is a view filter |
| per-row Archive toggle | `rec.Hidden` + `NotifyTimelineOfArchiveChange` | a hide is REFUSED with a Warn + ScreenMessage when the row is an Unfinished Flight (`IsArchiveRefusedForUnfinishedFlight`); an un-hide never is |
| folder / block Archive toggle | writes every descendant `Hidden` | the same refusal when any member is an Unfinished Flight |
| `G` (+ `S` on a mission folder that absorbed its launched vessel's block) | `groupPicker.OpenForRecording / ForChain / ForRecordings / ForGroup`; `S` is `OpenForRecordings` over the absorbed segments | never disabled; adds later rejected by `CanAddToUserGroup` |
| `X` on a folder | `ShowDisbandGroupConfirmation` (hover: its recordings and subfolders move up to the parent, nothing is deleted) | only for a non-permanent group. A recording row has no `X`: recordings are never player-deletable |
| `W` / `W*` | `flight.EnterWatchMode(ri)` | `IsWatchButtonEnabled`; the column draws only in flight |
| `FF` / `R` | `ShowFastForwardConfirmation` / `ShowRewindConfirmation` | `RecordingStore.CanFastForward` / `CanRewind`, refusal as the hover; `R` is suppressed entirely on an unfinished-flight row. SHOWN ONCE: a row under a folder / block that DREW the same target (same committed index) draws a blank cell (`IsTimeTargetShownByEnclosingRow`, `ResolveChildEnclosingTimeTargets`); STASH resets the inherited targets, so its rows keep theirs |
| `Fly` + `Seal` / `Stash` + `Seal` | `RewindInvoker.ShowDialog` / `UnfinishedFlightSealHandler.ShowConfirmation` / `UnfinishedFlightStashHandler.TryStash` | `ResolveReFlyColumnAction` |

Pictures: `ksc-missions-recordings-advanced`, `ksc-missions-recordings-collapsed-advanced`,
`ksc-missions-recordings-chain-advanced` (one group folder open with one chain block expanded)
and `ksc-missions-recordings-expanded-advanced` (every folder, chain block and leaf row at
once), GUI-1 `2026-10-01_1221`; `bd-missions-recordings-collapsed-advanced` /
`-expanded-advanced`, GUI-4. No picture: STASH, the Watch column, route-bound greyed Loop
toggles, an inline rename, the time-range filter strip.

**Group picker** (window 14, `UI/GroupPickerUI.cs`): title `Set Parent Group` in group-parent
mode, else `Manage Groups`; a `(None / Root level)` toggle, a recursive checkbox tree that
SKIPS any group in `treeModel.CycleInvalid`, a new-group text field plus `+`, then `OK` /
`Cancel`. `ApplyGroupPopupChanges` has four exclusive branches (group-parent, chain
all-or-nothing, multi-recording per-recording, single recording). Removes are never gated. It
has no tooltip strip. It draws in the shared picker look (`UI/PickerWindowLayout.cs`, below).
Its heading (`GroupPickerPresentation.FormatHeading`) names what the choice is for:
`Parent of 'G':` in group-parent mode, `Groups for 'V':` for one recording, `Groups for N
recordings:` for a chain, a block or a multi-selection. The `(None / Root level)` toggle is the
first entry row; `+` is 48 px, `OK` / `Cancel` 100 px. Minimum 260 x 220, first opened at
320 x 360: next to the clicked `G` / `S` button, or centred over the Missions window when the
seam opens it. Pictures: see row 14 of 3.0.

**The shared picker look** (`UI/PickerWindowLayout.cs`, both pickers): the main windows'
5 px gap under the title bar (`ParsekUI.WindowContentTopGapPx`, the `GUILayout.Space` the
Missions, Logistics, Kerbals, Settings and Log windows open with), a heading in the
shared table section-header style (`ParsekUI.GetTableSectionHeaderStyle`), then the entries
inside the shared dark table body box (`GetTableBodyBoxStyle`, the Recordings tab's list-area
box) around a scroll view on `GetTableScrollViewStyle`, one row per entry at the Missions
tab's expanded sub-row spacing (a style-less `BeginHorizontal` at
`MissionsWindowUI.CompositionRowMinHeight`, 22 px, with no table-row vertical margin), group
names in `GetTableCellStyle`, and buttons at
the Missions Interact widths (100 px single, 48 px for the one-glyph `+`, 4 px gap). Placement
(`PickerWindowLayout.PlaceOnOpen`, pure): a click-opened picker opens to the right of the
click with its top level with it, or to its left when the right side has no room; a picker
opened with no click point (the census seam) is centred over its parent window; either way
the rect is clamped fully on screen. The first-open rect is placed BEFORE
`ParsekUI.HandleResizeDrag`, because the screen fit inside it widens an unplaced zero rect to
the window's minimum width at the screen origin and the placement would then never run. One
`Group picker placed ...` / `Logistics link picker placed ...` Verbose line per open. Pinned
by `PickerWindowLayoutTests`.

### 3.3 Parsek - Timeline

Purpose: every recorded flight and career event on one clock, and the only access to rewind,
fast-forward and warp-to-time - which is why its launcher is kept in Basic.

Hosts: `ParsekFlight` and `ParsekKSC`; not the Tracking Station. Input lock
`Parsek_TimelineWindow` on CAMERACONTROLS while the mouse is inside. First opened to the left of
the main window at 820 x max(600, main window height).

Structure, top-down (`DrawTimelineWindow`): the filter area (two or three rows, below), the From / To
sliders while `Custom` is lit, the entry scroll view with a "now" divider, the warp row, the
single-line echo strip, `Close`, the resize handle and drag. Minimum size 610 x 150
(`MinWindowWidth`): every filter row sits on one six-cell grid (`FilterRowCellWidth` over
`ComputeFilterCellWidth`, the same width for every row), the widest labels (`Recordings`,
`Strategies`, `Milestones`) fit the 93 px cell floor, and six floor cells plus margins and
chrome are 608 px.

Filter area (`DrawFilterBar` + `DrawTimeRangeFilterBar`). Every filter button has the grid
cell's width and every row is left-aligned, so a row of fewer than six buttons leaves its
unused cells empty on the right.

- Row 1: the one-at-a-time view group `Overview` / `Details` / `Rewind/FF` / `Re-Fly` /
  `Career` (four in Sandbox).
- Row 2: the selected view's context row (`ResolveContextRow`), drawn only when the view has
  one (`ShouldDrawContextRow`). The source toggles `Recordings` / `Actions` / `Events` under
  Overview and Details; NO row under Rewind/FF and Re-Fly (the sources are forced or inert
  there, so they are hidden rather than greyed), where the time-range row moves up directly
  under row 1 and never leaves an empty line; the category buttons `Contracts` / `Strategies` / `Facilities` /
  `Milestones` / `Tech` under Career (single-select; `Career` reopens the last one used). The
  Career cell and the category set read the GAME mode, never the UI complexity mode: Career
  mode draws all five, Science draws Facilities / Milestones / Tech, Sandbox draws no Career
  cell, and a category view the loaded mode does not show falls back to one it does. In Career
  mode the `Contracts` and `Strategies` hovers end with the slot counts
  (`CareerSlotSummary.FormatSlotSentence`, free first: `Only contract rows, past and future.
  Contract slots: 4 of 7 free now (2 active, 1 reserved for later).`, `no slot limit (2
  active)` at an unlimited building, the reserved clause dropped when nothing is reserved);
  contracts read the shared `ContractSlotReservation` forecast, strategies the peak walk; the
  counts rebuild on a ledger change, a rewind or a new game minute, and only while that row
  draws. Which row draws is latched on the Layout pass (`LatchContextRow`), so a view click
  changes the row from the next frame and the Layout and Repaint passes of one frame always
  emit the same controls.
- Last row, always drawn: `Last Day` / `Last 7d` / `Last 30d` / `This Year` / `All` / `Custom`,
  exactly one lit (`ResolveLitTimeRangeButton`; `All` by default), so the range in force is
  always visible. `Custom` shows the sliders (`SetCustomRangeSelected`; switched on over a
  preset it keeps that range without the preset's name, switched off it clears to `All`); a
  slider drag lights `Custom` (`ApplyCustomSliderRange`); a preset turns `Custom` off
  (`ApplyTimeRangePreset`). `Custom` is disabled while the data spans no range. Each slider
  prints its own value. The range is the shared time-range filter the Recordings tab also
  applies (3.2).

Row model: one row per `TimelineEntry` surviving `IsEntryVisible`; the list is built by
`TimelineBuilder.Build` from `EffectiveState.ComputeERS()` + `ComputeELS()` +
`MilestoneStore.Milestones` and sorted by UT only. **There are no sort controls and no header
row** - chronological is the only order. Cells: a 160 px time label (`TimeColumnWidth`), a
14 px gutter, an expanding description label, then zero or more right-aligned action buttons.
Row colour is one of the cached styles: grey when `!IsEffective`, dim when future, blue when
`IsPlayerAction`, else green / red / white (green, red and the player-action blue are the
palette's `Green` / `Red` / `Cyan`; dim is `ParsekUI.DimTextColor`). The "now" divider is a grey `-- <date> (now)`
label (drawn with box-drawing characters) followed by a rule; its hover is
`NowDividerTooltip` (`Rows below happen on their date and hold stock controls until then.`).
Empty state: `No timeline entries.`

Row hover (`TimelineRowHoverTracker`): the description label carries a tooltip that the
single-line echo strip shows, and only the row hovered on the last Repaint gets one: the row
index whose rect held the mouse is published at the end of the Repaint pass, the text is
memoized per `TimelineEntry` and rebuilt when the row crosses now, the memo clears on every
cache rebuild, and the label stays ONE control either way. The text is
`TimelineRowHover.Compose` of three parts: the walk's not-counted reason on a grey row
(`GameAction.NotCountedReason`, runtime only, stamped at the `ContractsModule` /
`MilestonesModule` / `FundsModule` repair sites that clear `Effective` and reset with it in
`RecalculationEngine.ResetDerivedFields`); on a future row, the stock control it holds
(`ReservationExplanation.ForTimelineRow`: the click-block predicates over
`CommittedFutureIndexCache.Current`, `Holds <control> until <row date>.`; a repair row never
claims one, a part purchase reads `StockUiPartPurchase.DecideLive`, a strategy names only the
button stock shows now - Activate while inactive, Deactivate while active - from
`StrategyReservationGate.ActiveStrategyIds`); then the row kind's details (contract accept:
deadline, advance, the rewards of the first counted completion
`TimelineBuilder.FindPairedContractComplete` stamps on `TimelineEntry.PairedContractComplete`,
the snapshot agent; contract end: flight, rep / science or penalty; launch: crew from the
snapshot, `ResolveLaunchEnd` over the same launch's chain and tree, the original mission's
name). Budget: `TooltipEchoBudgetTests.TimelineRowHovers_FitTheTimelineStrip` pins each
builder's worst case; a future accept (hold plus terms) is the one composition that runs into
the marquee.

Row text for career rows: a contract row (Accept / Complete / Fail / Cancel) names the
contract - its own `ContractTitle`, else the title of the same contract's accept in the ELS
(`GameActionDisplay.BuildContractAcceptIndex`), else the humanized contract type, else
`Contract <first id block>` (an accept outside the ledger: pre-Parsek or tombstoned). A
contract fail row reads `Expired: <name>` when it lands at or after the deadline of the accept
it closes (the latest accept of that id at or before it,
`GameActionDisplay.BuildContractAcceptHistory` / `FindAcceptForOutcome`), else `Fail: <name>`:
stock reports a deadline expiry with the same `onFailed` event, and this is the ledger's own
deadline test. A facility row names the facility through
`FacilityDisplayNames.ResolveBuildingDisplayName`, so a building-level destructible id reads as
its facility (`Launchpad destroyed`). The ledger keys a destruction or repair by ONE building,
so one facility event is several rows (a Runway repair is up to ten, one per destroyed
building, at one UT); `TimelineBuilder.CompactFacilityBuildingActions` folds rows of the same
type, facility, owner and effectiveness within 1 s into one, summing the repair cost.
Destruction and repair rows come from the moment stock fires `OnKSCStructureCollapsing` /
`OnKSCStructureRepairing` (a flight-tagged collapse at its commit, a KSC repair at once), so a
repair made at the Space Center is on the Timeline. The three source toggles route rows by
`TimelineWindowUI.ResolveSourceToggle`: every recording-sourced row (crew deaths included) is
Recordings; a ledger or legacy row is Actions when `TimelineEntryDisplay.IsPlayerAction`, else
Events (milestones, contract completions and failures, earnings, recoveries, destructions).
The toggle hovers are consts pinned to that routing by `TimelineCareerNamesTests`.

Nine mutually exclusive views (`TimelineTierFilterMode`, default `Overview`; the five category
members are appended after `ReFly` so the index stays 1:1 with the seam's tab tokens). The row
predicate is the pure `IsEntryVisibleInView`, and the time range applies in every view:

| mode | row predicate | effect on the three source toggles |
|---|---|---|
| Overview | drops every `SignificanceTier.T2` row | live |
| Details | no tier drop | live |
| Rewind/FF | keeps only rows where `HasActionableRewindOrFastForwardButton` | forces Recordings on; all three HIDDEN |
| Re-Fly | keeps only rows where `HasActionableFlyOrSealButton` | same |
| Contracts ... Tech | keeps only rows whose `TimelineEntry.CareerCategory` is that category, both tiers | ignored (hidden, values kept) |

Both action views hide rows whose button would be GREYED, not merely absent. The category and
the row's subject id (`CareerSubjectId`: contract, strategy, facility, milestone or tech node
id) are stamped by `TimelineBuilder` from the LEDGER ACTION TYPE through
`TimelineCareerCategories.Classify`, never from the display type: Contracts = accept /
complete / fail / cancel, Strategies = activate / deactivate, Facilities = upgrade /
destruction / repair, Milestones = milestone credits, Tech = a `ScienceSpending` that names a
node. The strategy currency-exchange science legs share the `ScienceSpending` display bucket
and are in no category. Recording, legacy and other ledger rows (earnings, hires, builds,
recoveries, seeds) are in none either. `ScrollToCareerSubject(category, subjectId)` opens the
window on the category's view and scrolls to the subject's first visible row (for a contract,
its accept row), the career twin of `ScrollToRecording`; it has no production caller today.

Row actions, each one Missions Interact pair button wide (`MissionsWindowUI.InteractPairButtonWidth`,
48 px): `W` / `W*` (flight only), `FF` (future rows), `R` (past rows), `Fly` + `Seal` on
`UnfinishedFlightSeparation` rows (`Seal` stays live when `Fly` is greyed), and `Go to` (hover
`Show this recording's mission`, greyed `This recording is not part of a mission`), which opens
the Missions tab on the recording's original mission
(`RecordingsTableUI.ShowMissionForRecording`). `Go to` is gated by
`IsVisible(UiSurface.TabMissions, ...)`, the only `IsVisible` call in the file, which hides
nothing today. FF and R are mutually exclusive on one row, so a `RecordingStart` row carries at
most W + one of FF / R + Go to. The first row after now shows its time as the house countdown
(`T- 5m 30s`) in the countdown amber; the empty state `No timeline entries.` is muted grey.

The rewind gate is five preconditions evaluated in this order, and the failing one's string
becomes both the hover and the disabled-hover echo: `Rewind already in progress`,
`No rewind save available`, `Stop recording before rewinding`, `Merge or discard pending tree
first`, `Rewind save file missing` (`RecordingStore.CanRewind`). Precondition 2 also removes
the button, so only 1, 3, 4 and 5 are reachable as greyed states. The FF gate is a
six-condition near-mirror (`RecordingStore.CanFastForward`).

Warp row: a 186 px `Warp to time` button (twice the filter-cell floor) plus four 36 px
Year / Day / Hour / Minute fields; the plan comes from `WarpToTimeMath.DecideWarpPlan`, the
button is greyed while no plan is actionable or a warp is pending (`WarpToTimeDisabledReason`),
and the confirmation is a stock dialog (`WarpToTimeController`). In flight the warp NEVER
executes in place - it is deferred to the next Space Center arrival so the scene-exit merge
dialog handles the live recording first.

No archive control: recordings the Recordings tab archived (`Recording.Hidden`) never
contribute rows (`TimelineBuilder.CollectRecordingEntries` skips them), whatever that tab's
Archive header filter says. Archive and un-archive live in the Recordings tab only
(`design-ui-basic-advanced.md` section 4.4).

Pictures: `ksc-timeline-overview-advanced`, `ksc-timeline-details-advanced`,
`ksc-timeline-rewindff-advanced`, `ksc-timeline-refly-advanced` and `ksc-timeline-basic`
(GUI-1, `2026-10-01_1221`); `bd-timeline-overview-advanced`, `-overview-basic`,
`-details-advanced`, `-rewindff-advanced`, `-refly-advanced` and `-refly-minsize-advanced`
(GUI-4); the Career views `ksc-timeline-contracts-advanced`, `-milestones-advanced`,
`-tech-advanced`, `-milestones-thisyear-advanced`, `-customlastday-advanced` and
`-minwidth-advanced` (GUI-24). The Contracts view shows the grey `!IsEffective` row (the host's
duplicate contract completions). No picture: any flight-scene state (so the Watch button), `FF`,
every disabled hover.

### 3.4 Parsek - Kerbals

The row model, the status vocabulary, the sizing and the `op=expand window=kerbals` seam row
live in their own authority: **`docs/dev/design-gui-kerbals-window.md`**. In short:

Purpose: read-only. What each kerbal is doing now, and how every recorded mission a kerbal
flew ended. No reserve / unreserve / swap / clear control; its only mutations are transient
fold states. Hosts: FLIGHT and SPACECENTER; drawn in both complexity modes and not in the
Basic close set.

Two tabs, each a COLUMN TABLE sharing one inset (`ParsekUI.GetTableRowStyle` /
`GetTableBodyBoxStyle`, header text and body text aligned through `GetTableCellStyle`):

- `Roster` (seam token `roster`): `Kerbal` 210 / `Status now` 220 / `Last flight` (expands),
  grouped by slot (each stand-in a row under the kerbal whose seat he covers), plain available
  kerbals with no recorded flight behind one fold row `Available, no recorded flights (N)`.
  Statuses `Lost` / `Lost until <date>`, `Retired`, `Reserved ...`, `Stand-in for <owner>`,
  `Assigned (<vessel>)` / `On EVA`, `Available`, with the hold, the release rule or the
  re-fly remedy in the status cell's hover. Every `Last flight` cell is a Timeline cross-link.
  Empty state `No kerbals in the roster.`
- `Flights` (seam token `outcomes`): per-kerbal fold groups headed `Name [Trait] - N
  missions: ...`, one row per MISSION: `Date` 130 (the launch) / `Mission` (expands, plus
  `(flown by <stand-in>)`) / `Outcome` 110 (`Recovered` / `Lost` / `Still aboard` /
  `Outcome unknown`). A click anywhere on a row scrolls the Timeline to the mission's last
  segment. Empty state `No recorded flights with crew yet.`

Size: first opened at 760 x 400, minimum 586 x 150, two-line tooltip strip.

Pictures: `ksc-kerbals-roster-advanced` / `ksc-kerbals-outcomes-advanced` (GUI-1,
`2026-10-01_1221`); the GUI-5, GUI-6, GUI-8 and GUI-11 labels listed in the Kerbals document's
section 8.

### 3.5 Contracts and strategies (no Parsek window)

Parsek has no window of its own for contracts and strategies. Stock Mission Control and
Administration carry Parsek's reservation annotations (3.13: active counts, deadlines, accept
dates, completion labels, greyed Accept / Activate with the reason), and the Timeline's Career
view (3.3) lists every dated contract and strategy event, past and future. The slots the
committed future reserves are the last sentence of the Timeline's `Contracts` /
`Strategies` button hovers in Career mode.

### 3.6 Parsek - Logistics

Purpose: supply routes derived from flights already flown. Hosts `ParsekFlight` and
`ParsekKSC`; not the Tracking Station.

Complexity: one gate key, `UiSurface.LogisticsRouteTuning` (design-ui-basic-advanced.md 4.6),
read once per pass into `drawTuning` at the top of `DrawWindow` through
`LogisticsRoutePresentation.ShowsRouteTuning`. The header, every row and every detail block read
that one bool, so a frame's Layout and Repaint passes draw the same columns.

**Sections.** One scroll view holds, top to bottom: Active Routes, Paused Routes, the Dormant
Routes disclosure (only when a route is dormant), Candidates. Each of the three main sections
has a title bar: ONE button across the width with the caret (U+25BC expanded / U+25B6 collapsed, the
route rows' glyphs), the centred title and its count (`Active Routes (2)`, `Paused Routes (1)`,
`Candidates (3)`), in a font 2 pt over the shared section header, and a 2 px accent bar along the
bottom of the header box in the section's `ParsekUI.StatusColor` slot
(`LogisticsRoutePresentation.SectionAccent`: green Active, soft violet `#b39ddb` Paused
(`StatusColorKind.Violet`), cyan Candidates; a bar rather than a coloured title, so a section's
start stays marked when its title has scrolled away). A click folds or unfolds the section on the next frame (the queued
toggle is applied after the draw); a folded section draws only its bar; the state is per
section, session-only, all expanded by default. Folding a section drops an open interval or
rename edit on a route inside it (discarded like Escape). Between Active and Paused, and before
Candidates, a 2 px grey rule (`ParsekUI.CreateRuleLineStyle`, the Timeline's "now" rule) sits
in the middle of a doubled gap. An empty route table reads one grey sentence (`No active
routes.` / `No paused routes.`).

**Table membership.** The Paused table holds Paused routes; the Active table holds everything
else: running, held, broken, Pause-armed, and a route armed by Send while it sends (through the
countdown to its window and while its run is in flight). When that run completes, the backend's
PauseAfterCurrentCycle returns the route to Paused and the row moves with it.

**Route table** (Active and Paused share `DrawRouteSortableHeader` and `DrawRouteRow`, one sort
state). Every row is two lines tall:

| # | column | width | Basic | Advanced | value |
|---|---|---|---|---|---|
| 1 | `#` | 30 | yes | yes | display position; not sortable |
| 2 | Route | expand | yes | yes | line 1 caret + `route.Name`; line 2 grey (`MissionsWindowUI.MissionSummaryTextColor`, same font) `KSC [U+2192] Depot Station Duna I` (the U+2192 arrow, the arrow the default route names use; a multi-stop route `Depot (+2 stops)`; an unresolved endpoint names the place, never coordinates). Hover: origin and destination with coordinates. Sorts by name |
| 3 | Delivers | 200 | yes | yes | per-run manifest, amount first like every Logistics cargo text (`257.8 LiquidFuel, 315.1 Oxidizer`; wraps), then what the route loads: `200.0 LiquidFuel; picks up 154.4 LiquidFuel at B`, or alone on a pure pickup route `picks up 154.4 LiquidFuel at B` (`LogisticsDeliveryPresentation.FormatRouteCargoCell`); hover the detail block's cargo line |
| 4 | Every | 150 | read-only `every 4.0d` / `every 2nd window` | inline `[-] field [+] Nx` stepper | a Send-armed route shows the read-only form in both modes |
| 5 | Runs | 80 | no | yes | `3` or `3, 1 held` |
| 6 | Next | 135 | yes | yes | the house countdown: amber `ParsekTimeFormat.FormatCountdown` (`T- 2d 4h`), ` (!)` when the last run was held; grey `-` when no run is scheduled (a Paused route). A Send-armed route counts down to its window, then to the arrival. Hover: the exact date |
| 7 | Status | 260 | yes | yes | ONE colour-coded word + short reason (`LogisticsRoutePresentation.ClassifyStatus`): `Delivering` green, `Scheduled` white, `Held: ...` yellow (in either table), `Paused` grey, `New` cyan, `Sending one run` / `Pausing after this run` cyan, `Broken: destination lost` / `origin lost` / `flight missing` / `flight changed` red. Hover: one dated sentence, never the raw enum |
| 8 | Interact | measured | yes | yes | a 2x2 grid, below |

**Interact grid.** Every cell is the same width: the widest grid label (`Activate`, `Pause`,
`Cancel`, `Delivering...`, `Pausing...`, `Send`, `Go to`, `Log`) measured in the skin's
pair-button style at style build, plus 2 px, never below the Missions pair half
(`LogisticsRoutePresentation.InteractPairWidth`). The column is two cells, the 4 px gap and the
two 8 px insets (`InteractColumnWidth`); its header cell is a centred `Interact` in the same
width. Each cell is exactly one button in every state.

| line | cell 1 | cell 2 |
|---|---|---|
| 1 | `Activate` (Paused table) / `Pause` (Active table) / live `Cancel` (Send-armed, before launch: `RouteOrchestrator.TryCancelSendOnce` clears the arm and returns the route to Paused with NO ledger row, so its Route History is unchanged; hover `Cancels the run before launch; nothing is spent.`) / greyed `Delivering...` (Send-armed, in flight; hover `Launched on <date>; arrives on <date>, then pauses again. A launched run cannot be called back.`) / greyed `Pausing...` (Pause-armed in flight) | `Send`: live only on an unarmed Paused route; greyed with its reason otherwise (`Already running on its schedule; pause it first to send a single run`, `Already sending one run`, `Already armed: ...`, or `Stopped: <reason>. Fix or delete the route first` on a broken route) |
| 2 | `Go to`: opens the Missions tab on the mission the route repeats, through `RecordingsTableUI.ShowMissionForRecording` / `MissionsWindowUI.RevealMissionForRecording` (the spelling every window uses); greyed `The mission this route was built from no longer exists` when no recording of its source tree is effective | `Log`: opens the route's Route History window (3.7) |

**Detail block** (`DrawRouteDetail`, the route's basic information). Basic:
`Delivers each run: <manifest> to <destination>.` (a route that picks cargo up names every
stop's cargo in visit order instead, a stop's pickup before its delivery: `Picks up each run:
154.4 LiquidFuel at B.`, `Picks up each run: 154.4 LiquidFuel at B, then delivers 200.0
LiquidFuel to A.`; `FormatRouteCargoLine`), the next run (`Next launch window on
<date>; arrives <duration> later.`), the dated hold (`Last run held on <date>: <clause>.`,
yellow; the date is `Route.LastHoldUT`, the LAST check, so it names the last held run, never
when the hold began) and partial-delivery lines, the capacity line and `Re-scan for endpoint`
of a broken route, `Last delivered on <date>: ... Delivered so far: ...` (from the route's
RouteCargoDelivered ledger rows), cost/run, `Built from mission 'X'.` and the round-trip note
when linked. Advanced adds the `Every:` and `Priority:` steppers, one fixed grid: the same 70 px
label column, 24 px `-` / `+` buttons in the same button style, and one value cell measured once
with the block's label style from the widest Every readout (`1x (every window)` up to
`99x (every 99th window)`, `99x (~9999.9d)`; `LogisticsRoutePresentation.StepperValueCellWidth`),
so both `-` buttons share a column and both `+` buttons another whatever the values; both rows
take one height, a slot-button line's (`StepperRowHeight`), whether or not the block's slot
column reaches them; a `-` at its
floor (1x / 0) is greyed with its reason on hover. Then `Flights used:` (names, a
repeated name numbered `Name [1]`, `Name [2]`) and the manual-looping clause after
`Built from mission 'X'.`. The route's runs are the Route History's, not the block's.

The block has its own Interact column under the row's: every detail line ends in one slot cell
of the column's width (`DrawDetailSlotCell`), and the first lines carry full-width singles
(two grid cells and the gap): `Rename` (line 1), `Delete` (line 2; its confirm dialog
`Confirm: Delete Route`), and in Advanced `Link round-trip...` / `Unlink` (line 3); every later
line keeps a same-width space. A block short of lines for its buttons gets one information line
(`Delivered so far: ...` or `Not run yet.`), never an empty line. While a route is renamed, its
name field leads the block without taking a slot and Rename greys with its reason.

**Candidates table** (on the route grid): `#` 30; Route, expanding, two lines in the route
rows' styles (the caret + default route name, over the grey `KSC [U+2192] Depot` line through
the same `FormatFromTo` arrow: the origin in the route rows' short form, the dock endpoint's
live vessel name or its place, never coordinates; hover `From KSC (funds) to Depot Mun
(surface) 1.00,2.00.`, cut with `...` to the strip); `Would deliver` 260 (wraps; the manifest, then `; picks up ... at
<place>` for a run that loads cargo, or that alone for a pure pickup run, plus the Career net
cost suffix); `Transit` 150 (read-only, under the route tables' Every column; hover `How long
one run takes, from launch to undock.`); Interact, the route tables' measured column under the
shared centred `Interact` header cell (`DrawInteractHeaderCell`): line 1 `Create route`, line 2
`Dismiss`, each a single as wide as two grid cells and the gap (the detail block's 100 px
singles), exactly two buttons on every row. The from/to text and the Would deliver text are
cached per candidate on the ~1 Hz refresh, and the candidate list, the near-miss list and its
groups refresh on a Layout pass only, so a frame's Layout and Repaint draw the same controls.
Empty state: `No supply runs to offer yet. Fly a cargo run that docks, transfers cargo and
undocks, then finish the mission.` The expanded candidate is the cost line and `Built from
mission 'X'.`. The create dialog names its ends as the candidate row does (`Origin: KSC
(Runway)`, a depot by its name, `harvested en route`; `Destination:` the live vessel's name, else
the place `Mun (surface)`, never coordinates), lists resources amount first (`97.6 LiquidFuel`)
and stored parts by their title (`EVA Parachute x2`, the internal name when PartLoader knows no
title), and adds a `Picks up:` block for a run that loads cargo.

Below the table, `Missions that cannot become routes yet (N)` (collapsed by default) draws ONE
line per reason (`LogisticsNearMissPresentation.Group`): a caret, the short reason, the count
and a preview of names up to 60 characters, `No dock was recorded (18): Kerbal X [1], Kerbal X
[2], Duna Supply 1 ...`, largest group first; the hover is `Missions: ` and every name, cut
with `, +N more` to the strip. A repeated name is numbered `Name [1]`, `Name [2]` across the
whole list. A group opens (key `nearmiss:group:<status>`, collapsed by default) to one row per
mission with its own `Dismiss` in the Interact column. Above the rows the full reason sentence
shows once only when every mission shares it AND it says more than the short reason
(`LogisticsNearMissPresentation.ReasonAddsToShort`: advice or a count; `No dock was recorded on
this flight, so there is nothing to repeat.` and the unidentified-vessel clause only restate
theirs and are left out). A group whose missions carry different details (amounts) shows each
row as `Name - <sentence>`, its hover the sentence cut with `...` to the strip. Dismiss acts on the one mission on its row; there is no dismiss-all. `Hidden
missions (N)` lists each dismissed mission (numbered the same way) with `Restore` in the
Interact column (one Interact single wide, like `Dismiss`); the Dormant Routes rows' `Delete`
sits the same way.

Hold clauses in the Status cell, its hover and the detail block name a stored part by its
title, through the resolver the Route History's held rows use
(`StructureListWindowUI.ResolvePartTitle`).

`MinWindowWidth` is 1410 in both modes, so a mode switch never resizes the window.

The link picker (window 7) is its own `GUILayoutWindow`, armed only from an expanded route's
`Link round-trip...` (Advanced only), reached by the seam's `op=picker picker=link`, which
refuses `picker-hidden-in-basic` in Basic. A switch to Basic closes it (the `LogisticsLinkPicker`
entry of the Basic close set). It draws in the shared picker look (3.2, `UI/PickerWindowLayout.cs`):
the `Link 'X' with:` heading in the table section-header style, one candidate toggle per table
row inside the dark body box, `Link` / `Cancel` at 100 px. It opens next to the clicked
`Link round-trip...` button (to its left, since that column sits at the window's right edge),
or centred over the Logistics window when the seam opens it.

Pictures: GUI-3 (`ib-logistics-collapsed-advanced`, `ib-logistics-expanded-advanced`,
`ib-logistics-linkpicker-advanced`, `ib-logistics-basic`), GUI-1 (`ksc-logistics-advanced`,
`ksc-logistics-basic`, `ksc-logistics-nearmiss-advanced`), GUI-13 (populated candidates),
GUI-20..23 (the hold states), GUI-26 (the create dialog).

### 3.7 Parsek - Log and Parsek - Route History (the log-table window)

One class, `UI/StructureListWindowUI.cs`, in two instances owned by `ParsekUI`, each with its
own window id (`WindowIdKey` / `RouteHistoryWindowIdKey`), rect, input lock and open state, so
the two can be open at once. Neither persists its rect across scenes; the Route History first
opens 40 px right of and below where the Mission Log first opens (`DefaultWindowRect`), so the
two never open stacked. Both are read-only, have
no complexity gate, are drawn by `ParsekUI.DrawStructureWindowIfOpen` and release their locks in
`ParsekUI.Cleanup`.

| instance | title | opened by | rows | Time cell | empty state | seam token |
|---|---|---|---|---|---|---|
| Mission Log | `Parsek - Log: <mission>` (bare `Parsek - Log` untargeted) | the Missions tab `Log` (`OpenForMission`) | one per mission event (`MissionStructureListBuilder`) | first row the date, later rows `T+h:mm:ss` since it | `This mission has no recorded flight.` | `structure` (`op=target mission=`) |
| Route History | `Parsek - Route History: <route>` | a route's Interact `Log` (`ParsekUI.OpenRouteHistoryWindow` -> `OpenForRoute`) | one per route ledger row in the Effective Ledger Set (`RouteHistoryBuilder`; a rewound or tombstoned run is absent) | every row its own date | `No runs yet.` | `routehistory` (`op=target route=`) |

Columns: `Time` 110, `Event` expand, `Location` 185, `Vessel` 160, plus a reserved scrollbar
gutter; first-open width 900. ONE dark body box holds the pinned header row and the
forced-vertical-bar scroll view of rows; the row labels are the shared table cell style with the
vertical padding dropped. A long Event cell is shortened and carried whole as its tooltip, read
in the single-line hover strip above `Close`. Empty state, the same in both instances: the
table keeps its column headers over one muted-grey body row (`This mission has no recorded
flight.` / `No runs yet.`), then the hover strip and `Close` at the bottom.

Mission Log rows: `Launch`, one `Staged: N pieces (<part title> xK, ...)` per recorded
separation, `Decoupled (<piece>)` / `Docked (<partner>)` / `Undocked (<piece>)` naming the other
vessel (another mission's vessel as `X (mission 'Y')`), and `End: <terminal word>`. Vessel names
follow the Missions rows (`MissionVesselNaming`, `Kerbal X [2]`). Location: `<body> orbit` for an
orbital ending, else body and biome where recorded, else the body, else `-`. It rebuilds while
open when the mission's include set, its name or the committed recordings move (a Layout-only
change signature).

Route History rows (N is the run's position in dispatch order):

| ledger row | Event | Location / Vessel |
|---|---|---|
| RouteDispatched | `Run N: Sent`, or `Run N: Sent once` for a run armed by Send, plus what the launch cost when it cost anything: `, cost 7,410 funds` (a Career KSC launch), `, cost 257.8 LiquidFuel, 315.1 Oxidizer` (cargo taken from an origin vessel), or both joined (`RouteHistoryBuilder.SentCostSuffix`) | the origin (`KSC` / `-` for a funds-paid launch) |
| RouteCargoPickedUp | `Run N: Picked up <amounts>` (`(the source was short)` when it was) | the pickup stop's place and vessel |
| RouteCargoDelivered | `Run N: Delivered <amounts>` (`40.0 of 150.0 LiquidFuel (110.0 did not fit)` when short); the row that finishes a run | the stop's place and vessel |
| RouteHeld | `Held: <reason>`, the Logistics window's hold sentence without its live-route advice (`- delivers when ...`, `- use Re-scan ...`, `- it may have moved ...`): `Held: origin is short 108.8 LiquidFuel`, `Held: destination has no room for LiquidFuel`, `Held: Depot B is short 20.0 Ore`, `Held: destination has no free inventory slot for stored part 'EVA Science Kit'` (stored parts by title, `StructureListWindowUI.ResolvePartTitle`; a linked-route wait names the partner's current name from the stored partner id, `RouteHistoryBuilder.HeldDetailForDisplay`); no run number; plain `Held` when the row's kind did not read back (`LogisticsHoldPresentation.DescribeHoldForHistory` / `FormatHistoryHeldRow`) | the origin for an origin-cargo or funds hold; a pickup source hold names that source's vessel (live name first); `-` otherwise |
| RoutePaused | `Paused` (a player Pause), `Paused after the run` (delivered, partly delivered, or delivered on a replayed crossing), `Paused after a held run`, `Stopped: flight missing` / `flight changed` | `-` |
| RouteResumed | `Activated` (player), `Resumed` (automatic) | `-` |
| RouteEndpointLost | `Stopped: destination lost` / `origin lost` | `-` |

Amounts read amount first in every row (`150.0 LiquidFuel, 40.0 Oxidizer`, resources in ordinal
order, then `N stored part(s)`), as the Sent cost, a short delivery and the Logistics window's
cargo text do. A Held row is written once per hold
episode and reason (logistics design section 6.7): a route held for a year on one reason shows
one row, a reason change (origin short, then destination full) a second, and the next `Sent`,
`Paused`, `Activated` or `Stopped` row ends the episode; nothing marks the release.

Debit rows (funds or origin cargo) are not shown as rows; each is folded into the Sent row of
its own run (same `RouteCycleId`, so interleaved runs never borrow each other's cost), and a debit
with no Sent row adds nothing. The funds part reads the debit's `RouteKscFundsCost`, which the
dispatch writes only for a Career KSC launch: Sandbox and Science never show funds, and a zero
cost is left out (never "free"). Funds format as the route's Cost/run line
(`LogisticsCostPresentation.FormatFunds`, grouped whole funds, InvariantCulture). Cargo shows
only for a debit taken from an origin vessel (the row carries its pid); a KSC launch's row lists
the cargo its funds bought, which is not a separate cost. Both come from the same ELS list as the
rows, so a rewound or tombstoned run's cost leaves with it. The
ledger carries a UT on every row, so each pickup and each delivery has its own time; no extra
field is stored. It rebuilds while open when the ledger version, the tombstone version or the
route's name move (`RouteHistorySignature`, read on Layout passes only). When its route no
longer exists (deleted, or removed by a rewind) it closes and logs `Route History window closed: route=...
no longer exists`. A Send taken back with Cancel before launch writes no ledger row, so it adds
no row here.

Pictures: Mission Log `ib-structure-route-log-advanced` (the Mun route's source mission) and
`ib-structure-mission-advanced` (GUI-3); Route History `ib-routehistory-advanced` (GUI-3, the
Mun route; this fixture's routes carry no runs of their own, so it shows the empty state).

### 3.8 Parsek - Settings

Purpose: the nine settings that still have a control and the complexity toggle itself. Five
sections drawn top to bottom in one pass; no tabs, no scroll view.

Hosts: `ParsekFlight`, `ParsekKSC`. `UiSurface.MainButtonSettings` is visible in both modes by
design - it hosts the mode toggle. First opened to the right of the main window at 280 px wide;
it has no resize handle, and its height is fitted to its content on first open and on every
mode change (`SettingsWindowUI.RequestHeightRemeasure`, `design-ui-basic-advanced.md` 7.2).
Without an active game it draws `Settings unavailable (no active game).` and `Close`.

| # | section | gate | controls |
|---|---|---|---|
| 1 | Interface | always | `Basic` / `Advanced` pressed toggles of one fixed equal width (`SettingsWindowPresentation.OptionCellWidth`) plus the hint `Basic hides power-user windows. Advanced is the full UI.`; `Basic` is greyed while a Gloops recording runs (`IsModeOptionDisabled`), and the hint then adds `Stop the Gloops recording first.` |
| 2 | Ghosts | always | `Ghost audio` label (85 px), a 0..1 slider and a 35 px percent label; the ` Show supply route paths on map` toggle |
| 3 | Looping | `SettingsSectionLooping` | `Auto-launch every` label, a 45 px value field, a 40 px unit button |
| 4 | Recorder Sample Density | `SettingsSectionSampleDensity` | `Low` / `Medium` / `High` pressed toggles of one fixed equal width, plus a summary label |
| 5 | Diagnostics | `SettingsSectionDiagnostics` | five toggles (` Verbose logging`, ` Ghost render tracing (Warning: huge logs)`, ` Map/TS render tracing (Warning: huge logs)`, ` Ledger apply tracing (Warning: huge logs)`, ` Write readable .txt recording copies`), `In-Game Test Runner`, `Run Diagnostics Report`, and the `Rewind points on disk: <size> (<n> files)` readout whose hover carries the live / crashed / stable / concluded counts |

Footer: the two-line tooltip echo strip, then `Defaults` (resets the values in
`SettingsWindowPresentation.BuildDefaults`, the Advanced-only ones included, never the
interface mode) and `Close`. Each hidden section's trailing gap lives INSIDE its gate, so
Basic shows no double gap. In Basic the window's click-away commit for the auto-launch field is
replaced by dropping an open edit, since the Looping section is not drawn.

Persistence: the interface mode, ghost audio, route paths, sample density, verbose logging, the
three tracers and the readable copies are install-wide (`ParsekSettingsPersistence`,
`GameData/Parsek/PluginData/settings.cfg`; the audio slider is written once per finished drag);
only the auto-launch period is per save. The readable copies default OFF for players, and the
harness settings baseline stamps them ON for every automation run.

Settings with no control: `autoRecordOnLaunch` / `autoRecordOnEva` /
`autoRecordOnFirstModificationAfterSwitch` and `autoMerge` are hidden fields clamped `true` on
load and `forceFaithfulLoopPlayback` is clamped `false`, unless an automation env hook is armed
(`ParsekScenario.OnLoad`); landing-body alignment is the compile-time
`ParsekSettings.LandingBodyAlignmentMode` (Loose).

Pictures: `ksc-settings-advanced` and `ksc-settings-basic` (GUI-1, `2026-10-01_1221`); the
difference between them is exactly the three Basic-hidden sections. No picture: the
null-settings fallback, a greyed `Basic`, a mid-edit auto-launch field.

### 3.9 Real Spawn Control

Purpose: turn a recorded craft passing nearby into a real vessel. FLIGHT only, Basic-hidden
(`MainButtonSpawnControl`). Title `Parsek - Real Spawn Control`; first opened 750 wide and as
tall as its rows need (`SpawnControlPresentation.FirstOpenHeight`: 158 px of chrome plus 33 px
a row, so 191 for one row, floored at the 150 minimum and capped at 400), minimum 350 x 150.

Columns (`UI/SpawnControlUI.cs`, header `DrawSpawnColumnHeader`): `Craft` expand, `Dist` 60
(`129 m`), `Speed` 70 (`0.1 m/s`, `-` before a second sample), `Spawns` 90 (the house
countdown in the countdown amber), `Spawn date` 110 (the exact date, `KSPUtil.PrintDateCompact`
with the time, e.g. `Y1, D06, 14:05`), `Status` 70 (one word, its reason on hover) and
`Actions` 100 (one `Warp` button). Five headers sort, each on its own key: `Craft`, `Dist`,
`Speed`, `Spawns` and `Status`; `Spawn date` shows the same moment as `Spawns` and is a plain
header, and so is `Actions`. The default sort is `Dist` ascending; `Status` sorts the warpable
rows first (Ready, Leaves, Too fast, Leaving, Passed) and soonest first within one word. ONE
dark body box holds the pinned header row and the scroll view of rows
(`DrawSpawnCandidateTable`), and the row labels use the shared table cell style
(`ParsekUI.GetTableCellStyle`). The row model is one `NearbySpawnCandidate` wrapped by the pure
`SpawnCandidateRowPresentation` (`UI/SpawnControlPresentation.cs`, `BuildRowPresentation`).

Both time cells show the moment the row's `Warp` acts on (`SelectiveSpawnUI.EffectiveWarpUT`):
the spawn for a craft that stays, the departure for a craft that leaves its orbit first. The
`Spawns` sort and `Warp to Next Spawn` read the same value, so a row sorts where its button goes.

Status words (`SpawnCandidateStatus`), in priority order, with the hover each carries:

| word | colour | Warp | hover |
|---|---|---|---|
| `Too fast` | plain | greyed | `Spawns only below 2 m/s relative speed; it is passing at 8.1 m/s` |
| `Leaves` | countdown amber | live | `Leaves this orbit on <date> for Mun; it does not spawn here`; the Warp hover is `Warps to just before <craft> leaves orbit on <date>; it does not spawn here.` |
| `Leaving` | orange | greyed | `Leaving this orbit now for Mun; it does not spawn here` |
| `Ready` | green | live | `Close and slow enough to spawn; it spawns here on <date>`; the Warp hover is `Warps to <date>, when <craft> spawns here.` |
| `Passed` | plain | greyed | `Its spawn time, <date>, has passed` |

A greyed `Warp` carries its row's Status reason through `DisabledHoverEcho`. The departure
destination is words, never a raw value (`SelectiveSpawnUI.FormatDepartureDestination` over
`DepartureKind`): `for Mun` (another body), `for a new orbit` (same body), `to land on Kerbin`
(Landed or Splashed terminal), `to come down on Kerbin` (Destroyed terminal). A surface
terminal is decided before any orbit comparison, and its body is the one the flight ended on
(`SelectiveSpawnUI.TerminalSurfaceBody`: terminal position, endpoint body, last point).

Which craft are listed: a ghost inside the spawn radius `ParsekFlight.NearbySpawnRadius`
(250 m) and under `MaxListRelativeSpeed` (50 m/s) (`SelectiveSpawnUI.IsListedCandidate`). A
ghost farther than 250 m cannot be spawned from here, so it is not listed at all; a listed
ghost faster than `MaxRelativeSpeed` (2 m/s) stays as a greyed `Too fast` row, because closing
the speed is what fixes it. The proximity scan samples speed out to `NearbySpawnTrackRadius`
(1000 m), so a ghost that closes inside 250 m is listed with its speed already known; the scan's
verbose summary counts the ghosts it hid by distance (`hidden beyond spawn radius 250m`). The
main window's `Real Spawn Control (N)` counts the same list.

Footer: the single-line tooltip echo strip, then `Warp to Next Spawn` (expands; greyed when no
row is inside both warp gates; its hover, `Warps to when <craft> spawns here, in 56s.` or
`Warps to just before <craft> leaves orbit in 2m 0s; it does not spawn here.`, is handed to
the strip explicitly, because the button draws below it) and `Close` (132 px), the resize
handle and drag.

Self-close: `ResolveAutoCloseReason(inFlight, hasFlight, candidateCount)` shuts the window on
its FIRST draw when any of `not-in-flight` / `flight-null` / `zero-candidates` fires. The empty
table shape (headers over one grey `No nearby craft to spawn.` row) is therefore unreachable
(todo `GUI-STATE-COVERAGE-RESIDUE-2026-09-21`).

Pictures: `rsc-spawncontrol-before-warp` (RSC-1, one `Ready` row at the first-open size). GUI-6's
host lists no row (its rover is outside 250 m), and that lane asserts the window's self-close.

### 3.10 Gloops Flight Recorder

Purpose: record a ghost-only flight the career ignores. FLIGHT only. First opened at 280 x 230,
no resize handle.

**Its launcher is RETIRED in BOTH modes.** `UiSurfaceVisibility.IsRetired` returns true for
`MainButtonGloops` and `IsVisible` short-circuits before consulting the mode, so the launcher
never draws, in Advanced either. The window BODY is not gated, so it still draws when its flag
is set - and the only remaining writer of that flag is the harness seam. The cost of that
state, and why un-retiring it is not a one-line change, is finding 5.2 "Is Gloops retired".

Contents: a FIXED button order so positions never shift between states - the primary button
(`Stop Recording` / `Start New Recording` / `Start Recording`, hover `Records a ghost-only
flight; your career never sees it.`), `Preview` / `Stop Preview` (greyed with no last take or
while recording), a gap, `Discard Recording` (greyed with nothing to discard) - then a
three-block status ladder selected by `SelectStatusBlock(isRecording, hasLastRecording)` on a
snapshot RE-READ after the button handlers (the #446 NRE guard): `Recording...` with `Points:`
and `Duration:`; `Saved: "<name>"` with `Points:` and `Duration:`; or `Vessel: <name>` with
`Ghost-only - loops by default`. Then the two-line tooltip strip and `Close`.

Picture: `flight-gloops-advanced`, the empty block with Preview and Discard greyed. No picture:
the `Recording` or `Saved` blocks, `Stop Preview`.

### 3.11 The two Test Runner windows

They share a row model, status icons, colours, category labels and `Run` / `Run+` / play
semantics (both call into `InGameTests/TestRunnerPresentation.cs` and the same
`InGameTestRunner` API). Top row: `Run All`, `Run All + Isolated` (hover: runs batch-safe plus
`[isolated]` FLIGHT tests, quickloading a baseline after each destructive test), `Reset` (hover:
clears the table AND the per-scene history the results file uses) - all three greyed while a
run is going - and `Cancel` (live only while running). Then a summary label
(`idle | 1 passed  0 failed  0 skipped  (624 total)`). Two row kinds, no columns, no sort keys
(categories are ordinal-sorted): a category header `"{arrow} {category} ({passed}/{total})"`
plus `Run` 40 and `Run+` 44, and a test row with a 20 px status icon, an expanding name label
with `[isolated]` / `[single]` / `(NNNms)` suffixes, and a 24 px play button - followed by an
ALWAYS-rendered error row that collapses to `Height(0)` when empty, because a conditional
begin / end would desync the Layout / Repaint control count. Both windows are first opened at
440 x 600 with a 320 x 600 minimum and a two-line tooltip strip.

| difference | global (Ctrl+Shift+T) | Settings-launched |
|---|---|---|
| search / filter bar | absent | present: `Search:` (48 px), a text field and a 24 px `x` clear button; an empty filter result reads `No categories or tests match "<query>".` |
| footer labels | two: `Results file auto-updates after each run. Multi-scene runs accumulate.` and `Ctrl+Shift+T to toggle from any scene` | one: `Ctrl+Shift+T opens a separate runner window, in any scene` |
| window host | raw `GUILayout.Window` at a FIXED screen position (20, 60) | `ClickThruBlocker`, anchored beside the main window |
| input lock | CAMERACONTROLS + six editor types + `KSC_ALL`, taken while the mouse is inside | CAMERACONTROLS only |
| open-flag accessor | `IsOpenForTesting` + `WindowRectForTesting`, reached through this MonoBehaviour's OWN singleton `Instance` rather than through `ParsekUI` | `IsOpen` + `WindowRectForTesting` |
| complexity | never gated; draws in every scene but LOADING | launcher lives inside the Basic-hidden Diagnostics section, and the Basic close set shuts an open instance |
| extra responsibility | hosts the M-A3 autorun hooks (`UpdateAutorun`, `FireAutorun`, the multi-category driver) | none |

IDLE MEANS EVERY CATEGORY EXPANDED for both, because each seeds its fold set from its own
discovery at lazy init; a full idle runner is the largest dump in the census. The measured
difference between the two windows is exactly 3 nodes at every comparable state - the
`Search:` label, the text field and the `x` button the global window does not draw.

Seam routes, all automation-only, all writing state a click already writes:

- `testrunnerglobal` is its own window-table row. It is the only row besides `main` exempt from
  the hidden-host settle refusal (`TestCommandUiAction.WindowDrawsOutsideHostShowUi`), because
  its draw is in its own `OnGUI` outside both scene hosts' `showUI` gate.
- `op=expand key=category:<name>` drives either window's fold set through ONE key prefix
  (`TestCommandUiState`), one set per window, with `window=` saying which.
- `op=run window=<runner> category=<name>` runs one in-game test category through THAT WINDOW'S
  OWN `InGameTestRunner` - the category header `Run` button's body, `ResetCategory` then
  `RunCategory` (`ParsekTestCommandAddon.UiRun`). It is NOT the `RunTests` verb: each runner
  window constructs its own runner with its own reflection discovery, so a `RunTests` batch
  drives a THIRD runner and leaves both windows' tables reading "not run". That runner is also
  read by the addon's safe-point gate (`IsBatchRunning` /
  `CommandRunnerIsRunningForGating`).

Pictures: `ksc-testrunner-advanced` (GUI-1); `cek-testrunner-idle-advanced`,
`-collapsed-advanced`, `-category-advanced`, `-results-advanced` and
`cek-testrunnerglobal-idle-advanced`, `-collapsed-advanced`, `-category-advanced` (GUI-12,
`career-earned-ksc`). No picture: a FAILED row (the red error row that expands out of its
`Height(0)` collapse), the `Run+` isolated path, and either window MID-RUN.

### 3.12 Dialogs (19)

All are stock `PopupDialog` / `MultiOptionDialog` on `HighLogic.UISkin`, so all are invisible
to `DumpGuiTree` (PNG only). The population is the 18 production spawn sites below
(`PopupDialog.SpawnPopupDialog(` outside `InGameTests/` and `TestCommands/`) plus the in-game
test runner's `Parsek Test Isolation` dialog (`ParsekTestRestoreDegraded`, one `OK`, spawned in
`InGameTests/InGameTestRunner.cs`). `Confirm: Re-Fly` pins an explicit 420 px width
(`RewindInvoker.AdvisoryDialogWidth`); the no-width constructor inherits KSP's 300 px default;
the two cursor-anchored ghost menus size themselves.

| dialog title | popup name | spawn site | trigger | buttons |
|---|---|---|---|---|
| `Confirm: Merge to Timeline` / `Confirm: Commit to Timeline` (post-transition) | `MergeDialog.DialogName` | `MergeDialog` (post-transition show) | a pending tree surviving into FLIGHT (`ParsekFlight` finalization) or the deferred non-FLIGHT coroutine (`ParsekScenario`) | 2 or 3: the merge label, `Merge & Seal` for a not-yet-sealable Re-Fly attempt, `Discard` |
| `Confirm: Merge to Timeline` / `Re-Fly attempt - leaving flight` (pre-transition) | `MergeDialog.DialogName` | `MergeDialog` (pre-transition show) | the `HighLogic.LoadScene` prefix blocking a flight exit (`SceneExitInterceptor`) | 1 (journal active), 2 or 3 |
| `Pending switch-segment recording` | - | `MergeDialog.ShowPreSwitchDecisionDialog` | `MapContextMenuOptions.FocusObject.OnSelect` prefix, cases A and B (`Patches/MapFocusObjectOnSelectPatch.cs`) | 2: `Merge`, `Discard`. No Cancel by design; Esc is defeated by a re-spawn |
| `Revert during Re-Fly` | - | `ReFlyRevertDialog` | `RevertInterceptor.Prefix` on stock Revert while a Re-Fly marker lives | 2 (journal active) or 3: `Retry from Rewind Point`, `Discard Re-Fly`, `Continue Flying` |
| `Action Blocked` | `ParsekResourceBlock` | `CommittedActionDialog` | the stock-control click-blocks (3.13) | 1: `OK` |
| `Confirm: Re-Fly` | `ParsekRewindInvoke` | `RewindInvoker` | the Recordings-table, Missions-tab and Timeline `Fly` buttons | 2: `Fly`, `Cancel` |
| `Save failed` | `ParsekSceneExitSaveFailed` | `SceneExitInterceptor` | a throwing `SaveGame` on a flight -> main-menu exit | 1: `OK` |
| ghost icon context menu (title = vessel name) | `ParsekGhostIconMenu` | `Patches/GhostVesselLoadPatch.cs` | left-click on a ghost orbit node in map view | 3: `Focus`, `Set As Target`, one dynamic third |
| `Ghost` (Tracking Station popup) | `ParsekTrackingStationGhostMenu` | `ParsekTrackingStation` | TS ghost selection (`UpdateSelectedGhostPopup`) | 1: `Warp to Spawn` or a duration-suffixed label |
| `Confirm: Seal Unfinished Flight` | - | `UnfinishedFlightSealHandler` | any `Seal` button | 2: `Seal Permanently`, `Cancel` |
| `Confirm: Warp to Time` | `ParsekWarpToTimeConfirm` | `WarpToTimeController` | the Timeline `Warp to time` button | 2: `Warp`, `Cancel` |
| `Confirm: Disband Group` | `ParsekDisbandGroupConfirm` | `RecordingsTableUI.ShowDisbandGroupConfirmation` | `X` on a non-permanent folder | 2: `Disband Group`, `Cancel` |
| `Confirm: Rewind` | `ParsekRewindConfirm` | `RecordingsTableUI.ShowRewindConfirmation` | `R` / `Rewind` on a row, folder, block or mission | 2: `Rewind`, `Cancel` |
| `Confirm: Fast-Forward` | `ParsekFastForwardConfirm` | `RecordingsTableUI.ShowFastForwardConfirmation` | `FF` / `Forward` on a row, folder, block or mission | 2: `Fast-Forward`, `Cancel` |
| `Confirm: Warp to Launch` | `ParsekMissionWarpToWindowConfirm` | `MissionsWindowUI.ShowMissionWarpToWindowConfirmation` | an enabled mission `Warp to...` | 2: `Warp`, `Cancel` |
| `Confirm: Delete Route` | `ParsekLogisticsDeleteRouteConfirm` | `LogisticsWindowUI` | Delete on an Active or Paused route | 2: `Delete`, `Cancel` |
| `Confirm: Delete Dormant Route` | `ParsekLogisticsDeleteDormantRouteConfirm` | `LogisticsWindowUI` | Delete in the Dormant disclosure | 2: `Delete`, `Cancel` |
| `Create Supply Route?` | `ParsekLogisticsCreateRouteConfirm` | `LogisticsWindowUI` | `Create Route` on a candidate row | 3: `Create Paused`, `Create and Activate`, `Cancel` |

Two seam facts that shape any dialog lane: `AnswerMergeDialog` finds the live popup by
`MergeDialog.DialogName` and picks buttons by ORDER, and it is HARD-GATED on
`ActiveReFlySessionMarker != null`, so it can answer the merge dialog only in its Re-Fly
variant. Three dialogs bypass themselves under test through hooks (`CommittedActionDialog`,
`ReFlyRevertDialog`, `SceneExitInterceptor.ShowDialogForTesting`), so an in-game test never
renders them. `GUI-10-census-dialogs` raises and photographs `Action Blocked`, `Save failed`,
`Confirm: Fast-Forward` and `Confirm: Seal Unfinished Flight` through their production spawn
sites (`op=dialog`); the rest need state section 6.2 lists.

### 3.13 Overlays, markers and badges (7)

None is inside a `GUI.Window` and the stock-screen annotations are uGUI, so most are in no
`.gui.json`. The exception is the Watch Mode overlay: its labels are ROOT-LEVEL nodes in the
dump (`Watching: Part Showcase - Lights v1  (295 m) [Horizon]`, `[ ] return  |  V camera  |  W
cycle` in `play-main-watchmode-advanced.gui.json`), because the recorder patches `GUI.DoLabel`
and not only `GUI.DoWindow`. IMGUI drawn outside a window is captured when it goes through one
of the 17 patched funnels; uGUI and anything drawn through `GUI.DrawTextureWithTexCoords` /
`GUI.DrawTexture` (the markers' icons) are not. Marker LABELS are separate from marker ICONS:
`MapMarkerRenderer.ShouldDrawLabel(sticky, hover) => sticky || hover`, so a capture with the
pointer parked off the map carries icons and no label text.

| surface | draw site | scene | interaction | what only it carries |
|---|---|---|---|---|
| Watch Mode overlay | `WatchModeController`, called from `ParsekFlight.OnGUI` | FLIGHT | none (pure output) | the ghost-to-vessel distance and the `[Horizon]` / `[Free]` camera mode; a fixed 300 x 50 box centred in the LEFT half of the screen |
| Currency reservation tooltip | `CurrencyReservationOverlay.OnGUI` | SPACECENTER + FLIGHT | hover only | the Total / Reserved split and the `Short by` overdraw on the stock funds / science widgets; the stock bar shows only Available. Reputation is deliberately undecorated. Unconditional |
| Stock-control annotations (the D1 exception) | Harmony patches: `Patches/RnDStockDecorationPatches.cs`, `PartPurchaseBlockPatches.cs`, `AstronautComplexDecorationPatches.cs`, `CrewDialogReservationPatches.cs`, `FlightCrewReservationPatches.cs`, `MissionControlStockUiPatches.cs`, `StrategyReservationPatch.cs`, `FacilityMenuDecorationPatch.cs` / `FacilityRepairBlock.cs`; refresh host `StockUiOverlayController`; marks from `StockUiDecorationQuery`, text from `ReservationExplanation` | R&D, Mission Control, Administration, the facility menus (SPACECENTER); the Astronaut Complex and the VAB / SPH crew dialog in any scene that opens them; the flight crew portraits and hatch dialog | the tint, row labels and greyed buttons are always visible; the explanation is in the stock hover tooltip, description or reason field | a tinted R&D node icon; greyed Research / part Purchase / hire / dismiss / Accept / Cancel / Activate / facility Upgrade and Repair buttons, and a held kerbal's EVA / Transfer in flight; the stock row label of a kerbal (`Reserved ...`, `Lost ...`, `Stand-in for <owner>`, retired stand-in) or of a contract (a committed completion on an Active contract); the reservation explanation appended to the stock tooltip, description or reason. Every click-blocked control carries a mark read from the same predicate, and a refused click raises `Action Blocked` with the same text. No Parsek-drawn box, badge or panel |
| Ghost map markers (flight map) | `MapMarkerRenderer` via `ParsekUI.DrawMapMarkers` | FLIGHT map | LEFT click falls through to stock; RIGHT click toggles a sticky label | a marker for a ghost with no ProtoVessel; `ParsekUI.DrawMapMarkers` decides absence through its skip conditions |
| Ghost map markers (Tracking Station) | `ParsekTrackingStation.DrawAtmosphericMarkers` | TRACKSTATION | LEFT click opens the `Ghost` popup; RIGHT click pins | the same, plus a click-block while the popup is open |
| In-world ghost labels | `ParsekFlight.DrawGhostLabels` | FLIGHT | none | a 250 x 40 two-line label per chain ghost: `Ghost -- spawn blocked` (also after an exhausted walkback) / `chain terminated` / `spawns at UT=n` (`SpawnWarningUI`) |
| Logistics launcher tint | `ParsekUI.DrawWindow` | wherever the main window draws | none | the ONLY at-a-glance broken-route signal (red), and the cyan pending-candidate hint |

Photographed: the Watch Mode overlay (`play-main-watchmode-advanced`) and the flight-map ghost
markers (`play-mapview-ghostmarkers-advanced`, PNG only). Not photographed: the currency
tooltip and the annotations' tooltip text (hover-only, and hover does not paint, 6.2), the
Tracking Station markers, the in-world ghost labels, the Logistics tint.

**The hover strip.** `TooltipEchoBox` (`UI/TooltipEchoBox.cs`) is the permanently visible one-
or two-line help strip in 11 windows: single-line in Missions, Timeline, Logistics, Log and Real
Spawn Control; two-line in the main window, Kerbals, Settings, Gloops and both Test Runners.
The group picker and the Logistics link picker have none. It draws exactly one `Space` plus one
`Label` per pass at a probe-measured fixed height (23 px single-line, 38 px two-line), so the
control count and the window height never move on hover, and it marquee-scrolls overflowing
text at 60 px/s rather than clipping (`UI/TooltipMarquee.cs`). It sits directly above the
window's `Close` row. `DisabledHoverEcho` (`UI/DisabledHoverEcho.cs`) paints an invisible
zero-layout `GUI.Label` over a DISABLED control's rect on Repaint so its reason reaches that
strip: in KSP 1.12.5's Unity build only `GUI.DoLabel` and `GUI.DoButtonGrid` publish a tooltip
from managed code and neither reads `GUI.enabled`, and `GUI.Label(Rect, ...)` reserves no layout
slot. At most one carrier paints per frame.

**The tooltip budget.** `TooltipEchoBudgetTests` holds every literal hover to what its own
window's strip shows without scrolling: `budget = floor(lines * (firstOpenWidth - 30) / 7)`
characters (`TooltipEchoBox.BudgetChars`; 30 px of chrome and padding, a pessimistic 7 px
average advance). The `StripWindows` rows are the main window 250 px (two lines), Settings 280
(two), Gloops 280 (two), Kerbals 760 (two), Timeline 820 (one), Missions / Recordings 1355
(one), Logistics 1556 (one), Real Spawn Control 750 (one), Log 900 (one) and both Test Runners
440 (two). The scan covers every `new GUIContent(label, tooltip)` whose tooltip is a literal or
a same-file `const string`; each row pins a minimum literal count so a parser regression cannot
make the gate vacuous; hard newlines are rejected; and each row's line count must match the
window's own `new TooltipEchoBox(...)`. Runtime-built hovers are pinned by cells on their pure
builders (the Timeline row hovers, the Career-mode slot sentences, the Logistics hold and cost
texts).

### 3.14 Screen messages (95 producers)

The whole non-window notification budget, grouped by trigger class: 91 producing call sites of
`ParsekLog.ScreenMessage` / `ScreenMessages.PostScreenMessage` outside `InGameTests/` and
`TestCommands/` (the sink itself and one forwarding wrapper excluded). Full per-site table with
exact text: research note appendix 2 (its 2026-09-11 reading).

| trigger class | producers | notes |
|---|---|---|
| Recorder lifecycle | 7 (`FlightRecorder` 4, `ParsekFlight` 1, `ParsekScenario` 2) | `Recording STARTED`, `Recording STOPPED: N points`, `Recording stopped - vessel changed`, `Cannot record while paused`, `Recording unstashed (revert)`, `Recording discarded - vessel idle on pad` |
| Commit / merge | 11 (`ParsekFlight` 3, `ParsekScenario` 1, `MergeDialog.Commit` 7) | auto-commit IS announced |
| Re-Fly / rewind | 10 (`MergeDialog.ReFlyDiscard` 5, `RecordingStore` 2, `ParsekFlight` 1, `RewindInvoker` 1, `RevertInterceptor` 1) | |
| Stock switch-to interception | 6 (`Patches/MapFocusObjectOnSelectPatch.cs`) | |
| Ghost click refusals (map + TS) | 10 (`Patches/GhostVesselLoadPatch.cs` 6, `Patches/GhostTrackingStationPatch.cs` 4) | the main REFUSAL surface that is not log-only |
| Warp / fast-forward | 12 (`WarpToTimeController` 7, `ParsekFlight` 3, `ParsekTrackingStation` 2) | |
| Watch mode | 3 (`WatchModeController`) | the 300 km entry refusal, `Your vessel continues unattended`, `Camera: Horizon Locked` |
| Logistics | 9 (`RouteOrchestrator` 4, `RouteStore`, `RouteRunPrompt`, `RouteEndpointTransfer`, `UI/LogisticsWindowUI.cs` 2) | |
| Missions tab | 3 (`UI/MissionsWindowUI.cs`) | the loop-moved notice, the double-clock advisory, the `Warp to...` landing |
| Recordings table / group picker | 6 (`UI/RecordingsTableUI.cs` 5, `UI/GroupPickerUI.cs` 1) | |
| Spawning / proximity | 3 (`VesselSpawner` 2, `ParsekFlight` 1) | the proximity toast drops its "open that window" call to action when Real Spawn Control is unreachable (`ParsekUI.IsSpawnControlReachable`) |
| Crew | 1 (`CrewReservationManager`) | posted only when a reserved kerbal is actually swapped out of a seat |
| Save lifecycle / pre-Parsek backup | 2 (`PreParsekBackup`) | |
| Ledger | **1** (`GameActions/KspStatePatcher.cs`, latched once per session) | the only player-visible signal that the reconstructed ledger disagreed with the live pool |
| Gloops | 6 (`ParsekFlight`) | all player-unreachable: every trigger starts in the retired window or the seam |
| Playback seam | 1 (`ParsekPlaybackPolicy`) | throttled |

Separately, the `InfoRateLimited` / `WarnRateLimited` sites report STANDING conditions (route
refusals, playback skips, anchor failures) that by construction reach `KSP.log` and nowhere
else.

### 3.15 Tooltip-only information

Information that exists ONLY in a hover - no label, no column, no message. Research note
appendix 3 lists it with per-site citations at its 2026-09-11 reading; the load-bearing groups:

| group | example | why it matters |
|---|---|---|
| every disabled-control reason | `No recorded craft is passing nearby` (main window), `A supply route already drives one of these flights` (Recordings tab), the five rewind refusals, the three spawn refusals, the four `Warp to...` refusals, `A warp is already running` (Timeline) | the control itself is only greyed; the reason has no other surface |
| the meaning of one- and two-letter buttons | `W`, `FF`, `R`, `G`, `S`, `X` | every glyph's meaning is hover-only |
| scope statements on bulk controls | the two Recordings-tab header select-alls IGNORE the active filters | the visible position implies otherwise |
| filter-vs-write distinctions | the Recordings tab's `Archive` header toggle is a FILTER and archives nothing | it sits where a select-all would |
| numeric constants | the 300 km watch range, the launch-to-launch period definition and its overlap consequence (Recordings `Period` header), the interval grammar `30m / 2h / 1d` (`Logistics/LogisticsIntervalPresentation.cs`) | no label carries any of them |
| status-word definitions | `static` and `stationary` (Recordings tab), the STASH group's entire meaning (`UI/UnfinishedFlightsGroup.cs`), a locked loop period's qualifier (`Mun window, varies`) | the word alone is not self-describing |
| cross-window side effects | the Recordings tab's `Clear` of the time filter also resets the Timeline sliders | the click changes something off-screen |
| full values a cell shortens | an event cell's dock partner (`Docked with <partner>`), the full crew roster and span dates of a mission, endpoint coordinates in Logistics, the untruncated hold clause | the cell shows a capped form |
| the Gloops hovers | `Record a ghost-only flight that your career ignores.` | reach nobody: the launcher is retired |

`GUIContent` tooltips do not exist on `PopupDialog` surfaces, and a `GUILayout.TextField` takes
no `GUIContent`, which is why the Missions period cell's state is carried by the unit button
beside it.

### 3.16 The UiSurface gate

One pure decision point, `UiSurfaceVisibility.IsVisible(UiSurface, UiComplexityMode)`
(`UI/UiComplexityMode.cs`), with an exhaustive switch that THROWS on an undecided value;
retirement (`IsRetired`) outranks the mode and short-circuits first. The mode is
frame-latched: `SetUiComplexityMode` sets a PENDING value and
`ApplyPendingUiComplexityModeIfAny` latches it from `Update()`, so Layout and Repaint of one
frame can never disagree about the control count. Every draw site reads
`ParsekUI.AppliedUiComplexityMode`, never the settings field.

| key | declared | Basic | enforcement | what Basic actually hides |
|---|---|---|---|---|
| `MainButtonSpawnControl` | `UiComplexityMode.cs` | HIDE | `ParsekUI.DrawWindow`, `ParsekUI.IsSpawnControlReachable` | the launcher, and the proximity toast's call to action |
| `MainButtonTimeline` | `UiComplexityMode.cs` | KEEP | `ParsekUI.DrawWindow` | nothing |
| `MainButtonRecordings` | `UiComplexityMode.cs` | KEEP | `ParsekUI.DrawWindow` | nothing (the `Missions` launcher) |
| `MainButtonLogistics` | `UiComplexityMode.cs` | KEEP | `ParsekUI.DrawWindow` | nothing |
| `MainButtonKerbals` | `UiComplexityMode.cs` | KEEP | `ParsekUI.DrawWindow` | nothing |
| `MainButtonGloops` | `UiComplexityMode.cs` | RETIRED in both (`IsRetired`) | `ParsekUI.DrawWindow` | the Gloops launcher, in Advanced too |
| `MainButtonSettings` | `UiComplexityMode.cs` | KEEP | `ParsekUI.DrawWindow` | nothing |
| `TabRecordings` | `UiComplexityMode.cs` | HIDE | `RecordingsTableUI.VisibleTabCount` | the tab bar, the Recordings tab and its whole body |
| `TabMissions` | `UiComplexityMode.cs` | KEEP | `TimelineWindowUI` (the `Go to` button) | nothing; `Go to` is gated by its TARGET's key, so re-pointing it at a hidden surface would hide it |
| `MissionsLoopControls` | `UiComplexityMode.cs` | HIDE | `MissionsWindowUI.ShowsLoopAuthoringControls` (read at each draw site) | the loop grid (`Clone`, `Delete`, `Warp to...`, `Loop`, the period cell), the summary's `Next launch` piece, the include checkboxes, the per-vessel interval rows, the foreign partner-journey rows and the loop-selection styling. NOT the route label (`Run by route` in Basic), `Watch`, `Rewind` / `Forward`, `Log`, the collapse caret, `Fly` / `Stash` / `Seal` or `Go to` |
| `SettingsSectionLooping` | `:110` | HIDE (`:185`) | `UI/SettingsWindowUI.cs:345`, `:378` | the Looping section |
| `LogisticsRouteTuning` | `UI/UiComplexityMode.cs` | HIDE | `UI/LogisticsRoutePresentation.cs` (`ShowsRouteTuning`, latched once per pass in `LogisticsWindowUI.DrawWindow`) | the Every stepper (Basic reads the interval), the Runs column, Priority, Link round-trip and its picker, Flights used, the manual-looping clause |
| `SettingsSectionDiagnostics` | `:113` | HIDE (`:186`) | `UI/SettingsWindowUI.cs:393` | the Diagnostics section, and with it the only reopen path to `TestRunnerUI` |
| `SettingsSectionSampleDensity` | `UiComplexityMode.cs` | HIDE | `SettingsWindowUI.DrawSettingsWindow` | the sample-density section |

Every key has at least one enforcement site (`UiComplexityModeTests.EverySurfaceKeyHasAtLeastOneEnforcementSite`),
and the `SettingsSectionLooping` gate is read twice in `SettingsWindowUI.DrawSettingsWindow`
(the section and its click-away commit). `UiSurfaceVisibility.HiddenSurfaces` feeds only the
mode-change log line (`ParsekUI.FormatHiddenSurfaces`) and is the subject of a doc comment at
`ParsekUI.cs:471` explaining why the real close set is the hand-written
`BuildGatedWindowCloseSet`. That set has five targets - GloopsRecorder, SpawnControl,
TestRunner (maps to no `UiSurface`; its launcher lives in the hidden Diagnostics section),
GroupPicker (a reachability rule, not a lock rule) and LogisticsLinkPicker (its opener is the
Advanced-only Link control; no lock) - and deliberately omits the Missions, Mission Log, Route
History, Timeline, Logistics and Settings windows.

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
| H1 | No Parsek window or toolbar button in the TRACKING STATION, while the guide advertised that missions and routes loop there (`docs/user-guide.md:160`). RULED 2026-10-01 (operator): option (a), no TS control surface; the user guide now says the TS shows ghost presence, orbit lines, targeting and the Warp to Spawn popup, and that every control lives in the Parsek window in Flight / KSC | `ParsekTrackingStation.cs:26`, `:350`; the only `AddToAllToolbars` calls are `ParsekFlight.cs:1351`, `ParsekKSC.cs:153` |
| D1 | The main-window Gloops launcher block never draws in either mode | `ParsekUI.cs:941-951`, gate `UI/UiComplexityMode.cs:140-143` |
| P7 | ~~The stock Difficulty screen draws 5 Parsek settings whose edits the sidecar silently reverts~~ FIXED 2026-09-14: `ParsekSettings.GameMode` returns `GameParameters.GameMode.NONE`, so the stock screen builds no Parsek section at all (see 5.2) | `ParsekSettings.cs:22-56` |
| P18 | The Settings launcher tooltip advertises four topics, three of which can be absent | `ParsekUI.cs:957` |
| - | The `W` cycle key is real and undocumented (the Controls table omits it) | `ParsekFlight.cs:13410` vs `docs/user-guide.md:6-12` |

**1. Recording lifecycle and store** (5 HIDDEN, 4 DEAD, 2 promised)

| id | item | file:line |
|---|---|---|
| H26 | `LoopStartUT` / `LoopEndUT` / `LoopAnchorVesselId` are written only by `ApplyAutoLoopRange`, so the same Loop checkbox produces a different loop WINDOW depending on where it was clicked | `Recording.cs:63-66`, `UI/RecordingsTableUI.cs:5735`, `:1338`, `:2643` |
| H27 | Deleting a single non-ghost-only recording has no player path; the only one is the all-or-nothing wipe | `RecordingStore.cs:4287`, `ParsekFlight.cs:19787`. BY DESIGN since 2026-09-26: no player deletion path exists at all (the wipe and the ghost-only `X` were removed, and `ParsekFlight.DeleteRecording` with them) |
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
| P10 | ~~The mission `Log` is titled with the mission but built from the TREE, so two clones with different include sets render byte-identical logs~~ FIXED 2026-10-01 (MISSION-LOG-REWORK): the Log opens on a Mission and drops the rows of its excluded intervals through `MissionIntervalSelection.IsIntervalIncluded`, the vessel rows' predicate, and rebuilds on a change signature | title `UI/MissionsWindowUI.cs:2492`, build `UI/StructureListWindowUI.cs:110-117` |
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
| P20 | ~~Four cells read `route.Stops[0]` only while `RouteBuilder` builds multi-stop routes~~ FIXED 2026-10-01: on a route with more than one stop, Delivers per cycle sums every stop's delivery, Destination names the first receiving stop plus `(+N stops)` with the stop list in its tooltip, the DestinationFull capacity line names the stop the capacity gate refuses, and the one Re-scan button re-scans every stop; single-stop text is unchanged (pure helpers in `UI/LogisticsDeliveryPresentation.cs`) | `UI/LogisticsWindowUI.cs`; `Logistics/RouteBuilder.cs:353` |
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
| H10 | The 20-clone overlap cap silently RAISES the typed loop period while the Period cell keeps showing the typed value. STALE + FIXED 2026-10-01: the manual-mode cell has shown the flown (cap-raised) period in amber since April; its hover now names the typed value and the 20-copy reason in the row's unit (`RecordingsTableUI.BuildLoopPeriodCellView`). An `auto` row still shows the shared Settings launch gap | `ParsekConfig.cs:150`, `:168`; `GhostPlaybackLogic.WarpLoopPolicy.cs:469`; verdict `GhostPlaybackEngine.cs:976` |
| H11 | Render zones, warp-hide and loop-spawn thresholds: ghosts stop drawing at 120 km, stop spawning past 50 km, lose part-event visuals past 10 km, lose FX past 10x warp and vanish past 50x. Every one reads as a bug | `ParsekConfig.cs:41`, `:71`, `:73-74`, `:327`, `:333`; `RenderingZoneManager.cs:31`, `:62`, `:76` |
| H12 | 20 playback skip reasons; one has a control | `GhostPlaybackEvents.cs:5-56`, counters `:110-133`, log `GhostPlaybackEngine.cs:893-914` |
| H13 | `historical-not-replayed`: a recording the player only moved forward past is completely inert - no ghost, no map icon, no terminal spawn | `PlaybackScopeTracker.cs:34` |
| H14 | 15 + 6 spawn refusals, and `TerminalOrbitSpawnSafety` can latch a recording permanently unspawnable | `GhostPlaybackLogic.cs:8031-8215`; `VesselSpawner.cs:1921`+; `TerminalOrbitSpawnSafety.cs:273`, `:296` |
| H15 | Every watch-mode auto-exit and 4 of 5 entry refusals are silent; only the 300 km case posts | `WatchModeController.cs:2672`, `:2695`, `:2773`, `:3652`, `:3730`, `:1788`, `:1800`, `:1816`, message `:1839` |
| D2 | `SpawnWarningUI.ShouldShowWarning` / `FormatWarningText` and `SpawnCollisionDetector.CheckWarningProximity` are dead, so the pre-spawn collision warning does not exist in game | `SpawnWarningUI.cs:23`, `:40`; `SpawnCollisionDetector.cs:973` |
| D3 | `GhostCommNetRelay` is dead while a live Harmony patch cites it as the justification for destroying each ghost's `CommNetVessel` | `GhostCommNetRelay.cs:35`, `:207`, `:239`, `:389`; `Patches/GhostVesselLoadPatch.cs:446`, `:459` |
| D10 | `IsOverlapPerInstanceGateOn()` returns a constant; the documented OFF branch cannot execute | `GhostMapPresence.cs:76`, branch `:11416-11433` |
| D11 | `UIMode.TrackingStation` is never constructed, so `CanOfferGhostOnlyDelete`'s TS branch is dead | `UI/RecordingsTableUI.cs:4613`, `ParsekUI.cs:11` |
| P1 | The per-recording playback checkbox: the map icon, orbit line, TS row and the KSC terminal-vessel spawn all ignore `PlaybackEnabled` (bug #433). RULED + FIXED 2026-09-14 on the RENDER surfaces (see 5.2); the KSC terminal-vessel spawn stays, by the same ruling | tooltip `UI/RecordingsTableUI.cs:1849`; `GhostMapPresence*.cs` / `ParsekTrackingStation.cs` never read it; `ParsekKSC.cs:373-396` |
| P2 | The Period cell shows the typed value, not the cadence being flown. FIXED 2026-10-01 (see H10): the read-out is the flown cadence, the hover says `Period raised from 5s to 6s to fit the overlap cap - at most 20 copies of this flight can play at once.` | header tooltip `UI/RecordingsTableUI.cs:1341`, engine `GhostPlaybackLogic.WarpLoopPolicy.cs:469` |
| P4 | Real Spawn Control's row `Warp` (on a `Ready` row) performs a time jump only; whether a vessel appears is then decided by 15 silent refusals, and an invalid jump is itself silent | `UI/SpawnControlPresentation.cs:104`, `ParsekFlight.WarpToRecordingEnd:27367`, `:27384-27389` |
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
| H9 | In Basic - the DEFAULT for a new install - the loop period, recorder fidelity and verbose logging have no in-game control, and there is no Career window either (Kerbals is visible in Basic since 2026-09-22) | `UI/UiComplexityMode.cs:185-187`, `:256`, `:181-182` |
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

**Should the Tracking Station have a Parsek control surface at all? ANSWERED 2026-10-01 (operator): (a) - no TS control surface, add no controls; the user guide was corrected to describe what the TS shows (presence, orbit lines, targeting, the Warp to Spawn popup) and where the controls live (the Parsek window in Flight / KSC).** The question as filed: Ghosts get full TS
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

**What should the per-recording playback checkbox mean? ANSWERED 2026-09-14: no ghost
anywhere, career effect untouched.** The question as filed: its tooltip said "Unticked, the
flight stays recorded but no ghost appears", and `PlaybackEnabled` was read by `ParsekKSC.cs`,
`ParsekFlight.cs`, `GhostPlaybackLogic*.cs` and `RecordingOptimizer.cs` only. `GhostMapPresence*.cs`
and `ParsekTrackingStation.cs` never read it, so the map icon, the orbit line and the TS list row
survived an unticked box, and `ParsekKSC.cs`'s past-end branch still spawned the terminal vessel
(bug #433). THE OPERATOR RULING took the first option and drew the line exactly where the filed
"Decision" paragraph asked: the checkbox now means no ghost on any RENDER surface - the world
ghost, the map icon, the stock orbit line, the Tracking Station row, the non-proto atmospheric
marker and the trajectory polyline - while the recording's CAREER effect is deliberately
unchanged, because ticking a box off is a view decision and rewriting what the career ends up
holding is a bigger promise than the tooltip makes. So the terminal-vessel spawn still fires for a
hidden recording, and bug #433's visibility-only reject stays exactly as it was. The tooltip now
names where the ghost disappears from ("no ghost appears anywhere - world, map or Tracking
Station"). Implementation and the four gated draw authorities:
`docs/dev/design-map-ts-render-architecture.md` Appendix A, "The playback tick box is a
whole-presence gate". Live proof: `harness/scenarios/GUI-9-playback-toggle-map-scope.toml`, which
exists because map and TS presence is the one surface a unit test cannot see.

**Should a silent ledger rewrite stay silent?** The recalc-and-patch pipeline rewrites funds,
science, reputation, the tech tree, facility levels, progress nodes and the live
`ContractSystem` (14 call sites from `LedgerOrchestrator.cs:1837`, applied by
`KspStatePatcher.cs:61`), and the entire subsystem has exactly one player-facing message - the
drawdown clamp toast at `KspStatePatcher.cs:3514`, latched once per session, after which every
further divergence is Warn-log only. Alongside it sat two unarmed safety gates. The
rewind read-back one is **decided (operator 2026-09-14): the guard stays warn-and-proceed and
the abort option is retired**, because the abort was not fail-closed (the guard clears in
`RewindInvoker.cs`'s `finally` while the ReFly marker keeps `authoritativeReduction=true`, so
the next unguarded recalc writes the same target), it would break a DESIGNED rewind (a Step-3b
resurrected-recovery retirement legitimately puts the target below both witnesses), it would
skip the whole economy/tech/contracts patch after the crew roster was already applied, and it
never fired in 57 armed rewinds across 665 collected logs; the field is deleted and the WARN
now states the measured meaning instead of "possible silent career corruption" (rationale:
`docs/dev/ledger-state-reconstruction-audit.md` §8 rec #1). Still open:
`LedgerOrchestrator.CanAffordFundsSpending:6500` has no production caller while
`Patches/FacilityUpgradePatch.cs:36` states that funds affordability is deliberately unchecked
(science IS enforced at `Patches/TechResearchPatch.cs:78`). Two decisions left: arm the funds
gate or delete it and accept the asymmetry with science; and decide whether a career-altering
rewrite deserves more than one latched toast - remembering that the answer
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
answer: ~~the stock Difficulty-Settings screen draws five other Parsek settings whose edits are
silently reverted by the sidecar (P7, `ParsekSettingsPersistence.cs:229-272`) - hide them from
the stock screen, or honour them?~~ ANSWERED 2026-09-14: hidden, and not just those five. The
ruling is that every Parsek setting is a runtime debugging or preference option belonging only
to Parsek's own Settings window, so `ParsekSettings.GameMode` now returns
`GameParameters.GameMode.NONE` (`ParsekSettings.cs:56`) and KSP's Difficulty Options screen
builds NO Parsek section - the six `CustomParameterUI` toggles and the two numeric controls
(`samplingDensity`, `ghostAudioVolume`) alike. Decompiled KSP 1.12.5 `DifficultyOptionsMenu`
skips a node when `(customParamNode.GameMode & currentGameModeFilter) == 0`, before it reads any
member or attribute, and the `listDictionary.Add(node.Section, ...)` that creates the section and
its tab sits inside that loop; dropping the attributes instead would have left an empty "Parsek"
tab, since a node that produced no controls gets a blank label. Storage is untouched
(`ParameterNode.Save` writes every public field regardless of `GameMode`), so existing saves keep
every key and the sidecar, Settings window and harness `SetSetting` seam all behave as before.
Pinned by `ParsekSettingsTests.StockDifficultyScreen_DrawsNoParsekSection`. This leaves the
hidden-clamped-fields half of the question open. And `D5`: the deferred post-transition Merge/Discard dialog is
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
| P2 | The 20-clone cap silently raises the loop period while the Period cell shows the typed value: show the effective period with a tooltip, or leave the cap invisible? DECIDED + FIXED 2026-10-01: show it, typed value and reason on the hover |
| P3 | The route Interval field snaps to `N x transit` and overwrites the typed value: show the snapped value and why in the existing tooltip, or accept the silent rewrite? |
| P4 | Real Spawn Control's row `Warp` has 15 silent refusals behind it: put the refusal reason in the existing disabled-hover echo, or rename the button to the action it performs? |
| P9 | Rename refusals (mission, group, re-parent) discard the typed name with a Warn: keep the field content and show the reason in the row tooltip, or leave it? |
| P10 | The mission `Log` is built from the tree and ignores the mission's include set, so clones render identical logs under different titles: is that the intended contract? DECIDED + FIXED 2026-10-01: no; the Log follows the mission's include set (MISSION-LOG-REWORK) |
| P11 | `Merged, but could not seal - seal it from the Timeline window` names a control that cannot exist in exactly that state: reword, or make the control exist? |
| P19 | The Candidates empty-state sentence tells the player to do what the near-miss list below shows they already did: reword, or suppress it when near-misses exist? |
| P20 | Four route cells read `Stops[0]` only while `RouteBuilder` builds multi-stop routes: is multi-stop display in scope, or should the labels be scoped to the first stop? |
| D3 | `GhostCommNetRelay` is dead while `GhostCommNetVesselPatch` cites it as the justification for destroying each ghost's `CommNetVessel`: do ghost relays contribute to CommNet or not? RULED A 2026-09-26: yes; rebuilt as `GhostCommNet` / `GhostCommNetManager` (design 15.6, todo GUI-D3) |
| D6 | `MissionSelection` and `Mission.ExcludedThroughLineHeadIds` are persisted but never written: removal touches the mission save schema, so does it go now or wait for a generation bump? |
| D8 | The legacy `SupersedeCommit` public surfaces are kept alive only by their in-game tests, which therefore prove something the product does not run: delete both, or re-point the tests? |
| D18 | The virtual Unfinished Flights hide-all branch is unreachable; P17's fix should make the ordinary path carry the same guard: verify after, then decide whether the branch stays |
| I | `DrawRecordingTooltip` (`UI/RecordingsTableUI.cs:5451`) is a fully unit-tested 78-line hover panel with no caller, and the SOLE production caller of `FormatResourceManifest` / `FormatInventoryManifest` / `FormatCrewManifest`: wire it back, or delete it and three green formatter test suites with it? |
| exposure | 46 `InfoRateLimited` / `WarnRateLimited` sites mean a Logistics Pause / Activate / Send Once / Create click can no-op invisibly: is a log-only refusal acceptable for a click the player made? |
| tooling | the `StockUiOverlay` stock-control annotations (R&D, Astronaut Complex, Mission Control; badges until 2026-09-25) are invisible to the GuiTree recorder (uGUI) and their explanation text is hover-only: is a uGUI capture path worth building? |

The eight empty-surface captures, the 21 unphotographed dialogs and the 7 unphotographed
overlays are covered by section 7 and carry no todo entry of their own.

## 6. Coverage plan

### 6.0 What the wave-6 seam ops unlock (2026-09-21)

The read-only state audit of 2026-09-21 enumerated about 380 visibly distinct window states
against about 150 captured. Five automation-only seam additions landed for the reachable
part of the gap. This table is what a spec author aims a lane at; the exact steps are in the
wave-6 lane plan, the grammar and refusals in
`docs/dev/design-autotest-command-seam.md` -> `#### UiAction`.

| seam addition | states it unlocks | window |
| --- | --- | --- |
| `op=state key=srcRecordings\|srcActions\|srcEvents` | the three source-OFF row-population branches, all reading `true` in every existing dump | Timeline |
| `op=state key=archived` | the Recordings tab's Archive header filter ON (archived rows listed there; zero hosts carry one today) | Missions |
| `op=state key=customRange` + `key=preset` | the Custom range (the window's only two sliders; between 2026-09-24 PR #1792 and the preset-row revert the key opened a Time fold), the `From:` / `To:` labels (zero hits), the four ranged presets and the active-range readout (that readout was removed 2026-09-25) | Timeline |
| `op=state key=scrollY` | the window's first scrolled PNG. Note the dump already carried below-fold content with full rects, so this buys the PICTURE, not the data | Timeline |
| `op=state key=expandedStats` | (REMOVED 2026-09-26 with the Recordings tab's Info toggle; the key no longer exists) | - |
| ~~`op=state key=archivedMissions`~~ (removed 2026-09-30; the archive filter became per-mission Collapse, `op=expand key=mission:<id>`) | whole missions dropping out; the only way to exercise `DisplayBlockRendersAnything` and the corner-connector precedence table | Missions (Missions tab) |
| `op=sort` | 20 Missions sort states, 16 Logistics, 10 Spawn Control - all zero captured, and each is materially different (the arrow moves AND the row order does, including group-vs-recording interleaving) | Missions, Logistics, Spawn Control |
| `op=select key=vessel:` | the greyed include-OFF row and the `" (partial)"` suffix, which test the non-cascading `ExcludedIntervalKeys` contract | Missions |
| `op=select key=link:` | the foreign partner-journey subtree - an entire recursive renderer with zero coverage | Missions |
| `op=edit field=recordingname\|groupname\|missiontitle` | three label-becomes-a-text-field layout changes | Missions |
| `op=raise popup=deleteroute\|deletedormantroute\|createroute` | the three Logistics modals, PNG-only (a `PopupDialog` is uGUI and contributes zero nodes to any dump) | Logistics |
| `op=run await=false` | the Test Runner's RUNNING control bar - Run All / Run+ / Reset disabled, Cancel enabled, a yellow Running row. The awaiting form can never photograph it, returning only once the batch has ended | Test Runner x2 |
| `RouteCommand action=link\|unlink\|set-cadence` | the `Unlink` button, the `Round-trip linked to 'X'` note, `WaitingForPartner`, and the `-` stepper enabled at a cadence of 2 or more | Logistics |
| `selectedIndex` in the dump | not a state: it makes every tab verdict READABLE from a dump instead of derived from the tab body, which is how every tab row in the audit had to be established | all tabbed windows |

TWO LANE RULES THE OPS CANNOT ENFORCE. `op=select` and `op=state key=archived` write state
that PERSISTS with the save (`Mission.ExcludedIntervalKeys` /
`IncludedForeignDockLinkIds` through the Mission codec; `GroupHierarchyStore.HideActive`
with the save), so a lane using either runs on a THROWAWAY staged copy of its fixture. And
every op in this family answers OK over a surface the current complexity mode is not
drawing, so a lane selects its tab and sets Advanced before it captures.

WHAT STAYS UNREACHABLE, so no lane should be aimed at it: about 60 hover / disabled-reason
strings (measured, not assumed - `focus=true nudge=true` delivered a real `WM_MOUSEMOVE`
and `GUI.tooltip` was still empty), the Gloops window's three in-progress states (its
launcher is retired in BOTH modes, so no player can open it), the map marker label and its
sticky icon alpha, and `popup=rewind` on the whole committed fixture set. Full reasoning:
`docs/dev/todo-and-known-bugs.md` -> `GUI-SEAM-WAVE6-RESIDUE-2026-09-21`.

### 6.0.1 The lanes flown against those ops (2026-09-22), and seven predictions the sources and the flights corrected

FOUR LANES CLAIM THE TABLE ABOVE, all FLOWN PASS 2026-09-22 on one pinned automation DLL
(`d3a4dbbfc23d9d1e9c6cd166075c53e769e3e89d8629b6cfcfb3b89fd0f5518e`), nine flights, every
lane's final verdict PASS on attempt 1 at 55-88 s: `GUI-24-census-timeline-filters`
(`fixtures/local-saves/c1-gui`, SPACECENTER - every Timeline row of the table above, and
NOT the Career `pending:` row, whose two steps that lane dropped - see below),
`GUI-25-census-missions-state-sort-edit` (`interbody-route-recorded`,
SPACECENTER - both archive keys, four `op=sort` states (the `expandedStats` step became the
Phase sort when the Info toggle was removed 2026-09-26), all three
`op=edit` editors, the Logistics sort, `popup=deleteroute` and the link + cadence pair),
`GUI-26-census-createroute-and-running-batch` (`rover-route-recorded`, SPACECENTER -
`popup=createroute` and `op=run await=false`) and `GUI-27-census-missions-include`
(`bdock-recorded`, SPACECENTER - `op=select` in all three forms). Their status rows are in
`autotest-status.md` under "The GUI census, wave 6".

FOUR ROWS OF THE 6.0 TABLE PREDICTED MORE THAN THE SOURCES DELIVER, corrected in place
rather than left to mislead the next author. Each was re-derived from the committed bytes
or from the applier, not from the op's name:

  * the **`key=archived`** row's archived ROWS, and the **`key=archivedMissions`**
    row's "whole missions dropping out", need a host with an archived recording or an
    archived mission. NO FIXTURE AND NOT THE OPERATOR'S OWN CAREER HAS EITHER: the `hidden`
    key is written only when true and appears ZERO times across all 59 fixture directories
    and in `c1/persistent.sfs`, and `archived` reads `False` in all 9 of its occurrences.
    Both keys therefore buy the FILTER CONTROL moving (readable in the dump, which records
    a toggle's own `value`) and nothing else, so `DisplayBlockRendersAnything` and the
    corner-connector precedence table stay uncovered.
  * the **`op=sort`** row's "10 Spawn Control" states are UNREACHABLE THROUGH THE SEAM.
    `op=sort` refuses a closed window, and `SpawnControlUI.DrawIfOpen` force-closes itself
    on its first draw with no nearby spawn candidate - which GUI-2 already measured and
    pins. The Missions and Logistics halves are unaffected and are what GUI-25 claims.
  * the **`op=select key=vessel:`** row's `" (partial)"` SUFFIX is not expressible by that
    op. It resolves a ROW (by `OwnerHeadId`, else by any one of that row's interval keys)
    and then applies `ApplyVesselInclusion` over ALL of that row's own keys, so
    `ClassifyInclusion` answers `All` or `None` and never `Partial`. The greyed include-OFF
    row IS bought, and GUI-27 additionally takes the mixed tab (one vessel excluded among
    included siblings), which is the shape a player produces.
  * the **`op=raise popup=deletedormantroute`** third of the Logistics-modal row has NO
    HOST: `RouteStore.DormantRoutes` is disjoint from `CommittedRoutes` and no committed
    fixture carries a dormant entry, so GUI-25 declares that raise as a REJECTED naming the
    reason. The other two modals are photographed, on two different hosts - a host with
    routes has no live candidate and a host with candidates has no routes.

AND THREE MORE THE FLIGHTS CORRECTED, each measured rather than reasoned, and each a run
that PASSED every pinned line over a picture showing the wrong thing:

  * the **`op=raise popup=...`** row's PNG-only note is right about the dump and silent
    about the thing that actually breaks the picture: a `PopupDialog` is uGUI and KSP's
    legacy IMGUI pass paints OVER it, so a full-width Parsek window over the screen centre
    HIDES the modal in the capture while `op=dialog` reports `open=true count=1`. Both
    Logistics modals were invisible in their first captures; both lanes now `op=close` the
    covering window before the raise.
  * the **`op=run await=false`** row promises the RUNNING control bar and delivers a RACE:
    `running=` is read at DISPATCH, and an eight-cell `TrajectoryMath` batch completed in
    149 ms before the screenshot landed, so the PNG read `idle | 8 passed` with `Cancel`
    greyed. A category whose batch outlives the seam's command poll is required;
    `Periodicity` (nine batch-eligible Lambert-solving cells) is the one this wave uses,
    and the re-flight reads `RUNNING | 2 passed 0 failed 4 skipped` with `Cancel` enabled.
  * the **`op=edit field=recordingname`** row needs a row the seam can DRAW, and
    `op=expand key=all` cannot open every block that hides one: the Recordings tab draws
    two collapsible block kinds and only `DrawChainBlock`'s `ChainId` is enumerated, while
    `DrawGroupedRecordingBlock` keys its block `"<groupName>::<identity>"`. `key=all`
    answered `changed=29 expanded=52 total=52` and a multi-member grouped block still drew
    collapsed, so the editor answered `edit-not-drawn`. Key such an edit to a single-member
    block, or add a `block:` expand prefix.

All seven are filed as `GUI-CENSUS-WAVE6-RESIDUE-2026-09-22` in
`docs/dev/todo-and-known-bugs.md`.

The spec for the next census lanes. Grouped by the three causes from section 2; within each
group the cheapest route is named, with the fixture and the verb steps. No TOML here - the lane
authoring is the implementing task's job.

WAVE 2 IS AUTHORED AND FLOWN (authored 2026-09-11 on branch `gui-census-lanes`, read the same
day), so the tables below carry a LANE column naming the spec and the capture label that pays
each row, or the reason nothing does. Six lanes, all on COMMITTED fixtures:
`GUI-3-census-logistics-routes`, `GUI-4-census-missions-docked`, `GUI-5-census-career-ksc`,
`GUI-6-census-flight-playback`, `GUI-7-census-flight-recording`, `GUI-8-census-empty-states`.
Their status rows are in `autotest-status.md` under "The GUI census, wave 2"; each spec's own
header names the rows below that it claims and the ones it cannot reach.

THE READING RUNS: `2026-09-11_1548` / `_1551` / `_1553` / `_1556` / `_1559` / `_1601`, in lane
order, ALL SIX PASS ON ATTEMPT 1 (80 / 67 / 66 / 74 / 59 / 61 s wall), 79 captures with 79
dumps, every dump `patched=17/17`, `recordFaults=0`, every step met, `expectations mismatches=0`
on all six. WHAT THE RUNS REFUTED, recorded here because a row that claims a picture the run
did not take is worse than a row that claims nothing:

  * the **Career Contracts** row's "nine live contracts" - `career-earned-ksc`'s nine
    `CONTRACT` nodes are all `state = Offered`, and the tab lists ACTIVE (accepted) contracts
    only, so `cek-career-contracts-advanced` reads `Active (0)` / `(no active contracts)`. THIS
    IS THE THIRD ROW OF THIS SECTION READ OFF A NODE COUNT RATHER THAN OFF THE `state =` VALUES
    (the facilities and strategies rows were the first two, corrected below), so the lesson is
    now mechanical: grep the VALUES, never the node names.
  * the **in-world ghost labels** third of the flight row - zero `SpawnWarningUI` producers
    fired on GUI-6 (no `spawn abandoned` / `spawn blocked` / `chain terminated` line anywhere in
    its log), so that surface still has no picture. `Active Ghosts > 0` and the two non-`Idle`
    status values DID land.
  * **both hover echoes** - the cursor lands and the hover does not paint, on all four
    captures. See the `pointer` row of 6.2 and
    GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT.
  * the **`Rewind/FF` filter mode** GUI-4 was chosen for - `bdock-recorded`'s three
    `REWIND_POINTS/POINT` entries are SEPARATION RewindPoints and they populate the `Re-Fly`
    view (`bd-timeline-refly-advanced`, two `Unfinished Flight:` rows with `Fly` / `Seal` /
    `GoTo`), not `Rewind/FF`, which needs `TimelineWindowUI.ShouldShowRewindButton` -> a
    recording owning a LAUNCH rewind save. That fixture has zero `rewindSaveFileName` keys, so
    `bd-timeline-rewindff-advanced` is the EMPTY form and no committed fixture populates that
    view.

WHAT THE RUNS CONFIRMED, beyond the rows: Real Spawn Control opens and holds on a candidate
host, all three excluded window hosts are reachable, `op=expand key=all` buys the largest
un-photographed cluster in one step, and the `op=dialog` negative holds six times over.

TWO ROWS OF THIS TABLE WERE WRONG and are corrected in place rather than left to mislead the
next author. Both were read off the fixture names rather than off the fixtures' bytes:
`career-earned-ksc` does NOT carry upgraded facilities (all ten
`ScenarioUpgradeableFacilities` entries read `lvl = 0`), and `strategy-career` does NOT carry
an active strategy (its `STRATEGIES` node is EMPTY by construction - it seeds `rep = 25` so
`L3`'s in-game cell can ACTIVATE `LeadershipInitiative` at run time, and that cell's `finally`
restores the pool). Both surfaces need a fixture that does not exist; they have moved to the
new-fixture list at the bottom of this section.

### 6.1 The fixture lacked data of that shape (no new machinery)

These need only a different `saveTemplate` and the existing `open` / `rect` / `tab` /
`complexity` ops. They are the cheapest pictures in the whole plan.

| target | fixture | steps beyond the existing sequence | lane / label |
|---|---|---|---|
| Missions / Recordings tab in FLIGHT (the Watch column) | the existing GUI-2 host | insert `op=tab window=missions tab=recordings` before the capture | GUI-6 `play-missions-recordings-flight-advanced` (243 injected rows, not GUI-2's host). PAID: the dump carries the `Watch` column between `Period` and `Rewind`, with the `G` / `W` buttons on the group header row over `Part Showcases (242)` + `Synthetic (1)` |
| Basic-mode Timeline ROWS | the existing GUI-1 host | insert `op=tab window=timeline tab=overview` before the Basic capture; today the Basic label inherits the Advanced pass's Re-Fly filter | GUI-4 `bd-timeline-overview-basic` and GUI-5 `cek-timeline-overview-basic`, both with the explicit `op=tab` this row asks for |
| Timeline in FLIGHT (any view) | the existing GUI-2 host | `op=open window=timeline` + `op=rect` + `op=tab tab=overview` + capture | GUI-6 `play-timeline-overview-flight-advanced`; GUI-7 `b1-timeline-overview-live-advanced` adds the same view over a tree the run itself recorded |
| Kerbals and Career in FLIGHT (all six tabs) | the existing GUI-2 host | both windows are already in the window table with driveable tabs | GUI-6 `play-kerbals-roster-flight-advanced` / `play-kerbals-outcomes-flight-advanced` / `play-career-contracts-sandbox-flight-advanced` (3 of the 6; the three remaining Career tabs in flight are unclaimed - they are the same classes GUI-5 shoots at KSC) |
| Timeline minimum-size rendering | the existing GUI-1 host | a second `op=rect` at 520x150 plus one capture | GUI-4 `bd-timeline-refly-minsize-advanced`, at the 520x150 floor (historical: the floor is 720 since the 2026-09-24 filter redesign; not re-flown) |
| Logistics populated: Active, Paused, `New (not yet run)`, the Send-Once-armed row | `interbody-route-recorded` (Active + Paused, `completedCycles = 0`) or `depot-route-recorded` (Active, `pauseAfterCurrentCycle = True`) | `op=rect` to at least 1410x500 FIRST, or the Name column collapses as it did in the shipped capture | GUI-3 `ib-logistics-collapsed-advanced` (both rows) and `ib-logistics-expanded-advanced` |
| Logistics `Dismissed (N)` header | `interbody-route-recorded` (carries `DISMISSED_ROUTE_CANDIDATES` with two ids) | none | GUI-3 `ib-logistics-collapsed-advanced` / `ib-logistics-expanded-advanced` |
| Logistics Candidates populated + run-cost suffix | `rover-route-recorded`, or `rover-route-career` for the Career + KSC cost | none | UNCLAIMED. `interbody-route-recorded` carries a DISMISSED list rather than live candidates, so GUI-3's expanded capture shows the candidate SECTION and not a populated one. Wants a `rover-route-recorded` lane of its own |
| Mission header `Looped by route` + greyed Loop | `depot-route-recorded` / `interbody-route-recorded` / `rover-route-recorded` | none; the label draws with no click | GUI-3 `ib-missions-missions-collapsed-advanced` / `ib-missions-missions-expanded-advanced` |
| Recordings route-bound greyed Loop toggles (header level) | same three | none; `AnyRecordingRouteBound` scans all committed | GUI-3 `ib-missions-recordings-expanded-advanced` |
| `Docked with <partner>` in the Start event cell's hover; chapter header rows; `Docked partner:` rows | `bdock-recorded` (or `bdock-station-craft` + `bdock-station-pad`, or `bdock-forge-base`) | none; all three draw with no click | GUI-4 `bd-missions-missions-expanded-advanced` / `bd-missions-recordings-expanded-advanced` |
| Vessel-row Fly / Seal | `refly-a-recorded` | none | UNCLAIMED. `refly-a-recorded` is a fourth `saveTemplate` and therefore a seventh lane; GUI-4's `bdock-recorded` corpus has RewindPoints but its rows are not the Unfinished-Flights shape this needs |
| Kerbals reserved / active owner statuses | `eva2-lko-crewed` | none | UNCLAIMED. `eva2-lko-crewed` is a fifth `saveTemplate`. GUI-5 shoots both Kerbals tabs on a career with a real roster, which is the row below this one rather than this one |
| Kerbals and Career empty states | `fresh-career` | none | GUI-8 `fs-kerbals-outcomes-empty-advanced`; the Career empty state (`No active contracts.` / `No active strategies.`) is GUI-5's `cek-career-*` pair since 2026-09-24, and GUI-8 no longer opens Career (see the science row below) |
| Missions empty state, Recordings `No recordings yet.` | `fresh-sandbox` or `fresh-career` | none | GUI-8 `fs-missions-missions-empty-advanced` / `fs-missions-recordings-empty-advanced` |
| Career Contracts SPLIT layout, `Pending in timeline`, the banner divergence suffix | `career-contract-pad` | none; the pending group defaults EXPANDED | UNCLAIMED, AND THE ROW'S OWN PREMISE WAS WRONG - corrected 2026-09-11 off the reading run. `career-earned-ksc`'s nine `CONTRACT` nodes are all `state = Offered`; the tab's `CurrentRows` come from the ACTIVE (accepted) snapshot (`UI/CareerStateWindowUI.cs:690-723`), so `cek-career-contracts-advanced` reads `Active (0)` / `(no active contracts)` under a `Mission Control L1 - slots 0/2 now, 0/2 at timeline end` header. NO committed fixture carries an ACCEPTED contract, so BOTH the populated list and the SPLIT layout now want `career-contract-pad` or a new fixture |
| Career Strategies populated (the `Flow` cell) | ~~`strategy-career`~~ - WRONG, corrected 2026-09-11 off the save's bytes: its `STRATEGIES` node is EMPTY by construction (the fixture seeds `rep = 25` so `L3`'s in-game cell can ACTIVATE a strategy at run time, and that cell's `finally` restores the pool). NO committed fixture carries a live `STRATEGY` node | a NEW fixture | IMPOSSIBLE AS WRITTEN - see the corrected fixture cell. GUI-5 `cek-career-strategies-empty-advanced` and GUI-8 `fs-career-strategies-science-advanced` shoot the two EMPTY forms instead, both labelled as such |
| Career Facilities upgraded rows | ~~`career-earned-ksc`~~ - WRONG, corrected 2026-09-11 off the save's bytes: all ten of its `ScenarioUpgradeableFacilities` entries read `lvl = 0`. That fixture is EARNED in its POOLS (funds 536558, sci 111.6, rep 2.0) and in its milestones, not in its buildings or its contracts | a NEW fixture | IMPOSSIBLE AS WRITTEN - see the corrected fixture cell. GUI-5 `cek-career-facilities-level0-advanced` shoots the all-level-0 form, labelled as such. WHAT THE PICTURE SHOWS, so the label is not misread: nine facility rows all reading `L1`, because the save's `lvl = 0` is the FIRST level and the window prints it one-based. The label names the save value, the picture names the display value, and they agree |
| Career Science-mode and Sandbox-mode banners and empty states | `fresh-science`, `fresh-sandbox` | none; the science lane alone buys four otherwise-dark code paths | Since 2026-09-24 the Career launcher is Career-only, so the Science form is unreachable and GUI-8 dropped its `fs-career-milestones-science-advanced` capture; its `fs-main-advanced` is the Science main window WITHOUT the launcher. GUI-6 `play-career-contracts-sandbox-flight-advanced` still photographs the seam-opened Sandbox banner in flight. A sandbox banner at the KSC is unclaimed |
| Timeline `R` greyed | any recorded fixture | capture with a pending tree, or delete one `RewindPoints/<id>.sfs` from the staged save | UNCLAIMED. Wants a STAGED save edit (delete one `RewindPoints/<id>.sfs`), which no lane does today - the harness stages a fixture verbatim |
| Timeline `FF` and the countdown time label | an `injectedRecordings` preset whose recording `StartUT` is ahead of the save UT | one capture buys both | UNCLAIMED. Wants a preset whose recording `StartUT` is ahead of the save clock; `part-showcase` starts at UT 50 and GUI-6 jumps PAST it to 55, so its Timeline is behind rather than ahead |
| Real Spawn Control (a GUI-3 flight lane) | `bdock-recorded` / `bdock-station-craft` / `bdock-station-pad` - anything with a recorded craft inside 250 m at under 2 m/s | `op=open window=spawncontrol` now returns OK; then `op=rect` + capture + dump. Closes `GUI-CENSUS-SPAWN-CONTROL-NEEDS-A-CANDIDATE-HOST` | PAID by RSC-1 `rsc-spawncontrol-before-warp` (the `spawn-control-target` preset: one probe 129 m from the pad, a `Ready` row at the first-open size). GUI-6 `play-spawncontrol-advanced` paid it first on LT-5's showcase host, whose only nearby ghost is a rover about 450 m out; since the Model 1 redesign hides craft beyond the 250 m spawn radius, GUI-6 asserts the window's self-close there instead |
| In-world ghost labels, flight status non-`Idle`, `Active Ghosts > 0` | `mun-landing-recorded` / `b2-lko-craft` / `b1-pad-craft` | PNG only for the labels; the status block needs `StartRecording` before the dump | TWO OF THREE PAID, the third refuted. GUI-6 `play-main-ghosts-advanced` shows `Active Ghosts: 156` (and 243 in the later captures of the same run), and GUI-7 `b1-main-recording-advanced` / `b1-main-ready-advanced` show `State: RECORDING` + `Recorded Points: 1` + `Duration: 0.0s` and `State: Ready (has recording)` (`PREVIEWING` needs the still-RESERVED `StopPlayback`). The status block itself was removed on 2026-09-22 (section 3.1), so these are historical captures. THE IN-WORLD LABELS DID NOT DRAW: no `SpawnWarningUI` producer fired on either flight lane (zero `spawn abandoned` / `spawn blocked` / `chain terminated` lines), and no root-level label other than the watch overlay's two appears in any of the 20 flight dumps. That surface still has no picture and needs a host where a ghost's spawn is actually abandoned or blocked |
| Tracking Station scene (markers, the ghost popup) | any `*-recorded` fixture | `LoadGame` with `scene=TRACKSTATION` then capture; no `UiAction` is possible there, so the driver needs a branch that skips the `op=rect` it currently sequences before every label | UNCLAIMED AND BLOCKED. `ParsekTrackingStation.OnGUI` draws MARKERS ONLY and hosts no Parsek window, so every `UiAction` there answers `REJECTED ui-host-unavailable` - a TS lane could take a full-screen PNG and could not even open `main` to make the surface visible. The driver branch this row asks for is necessary and not sufficient |

Fixtures named by the research note but not present in the tree, so a NEW FIXTURE is required:
a career with an orphaned retired stand-in (Kerbals `Unlinked Retired`), a career with a
destroyed facility, a career whose uncommitted timeline credits a milestone after the live UT
(Milestones `(pending)`), a save with a dormant route, and a save with a hard-broken or held
route. TWO MORE JOINED THAT LIST ON 2026-09-11, both moved here off the corrected rows above
rather than newly discovered: a career with an ACTIVE strategy (a live `STRATEGY` node), and a
career with an UPGRADED facility (any `ScenarioUpgradeableFacilities` entry above `lvl = 0`).

A THIRD CLASS is neither a missing fixture nor a missing verb but a missing STAGED EDIT: the
`Timeline R greyed` and `Timeline FF countdown` rows each want a save that differs from a
committed one by a single deletion or clock change. The harness stages a `saveTemplate`
VERBATIM, so today those need a committed fixture of their own; a per-lane staged-edit hook
would buy both at once and is the cheaper answer if a third ever appears.

### 6.2 The state needs a click (the `gui-census-ops` ops)

The sibling branch `gui-census-ops` is adding the pointer-and-click op family. Each op below is
named with the surfaces it unlocks, so the branch can be scoped against real targets rather than
guesses.

SHIPPED 2026-09-11, and wave 2 is the first consumer of every one of them. What each op
actually bought, against what this table predicted:

| op | first consumer | reading |
|---|---|---|
| `pointer` | GUI-7 `b1-main-disabledecho-spawncontrol-advanced` (no hover painted) and `b1-main-tooltip-timeline-advanced` (no hover painted); GUI-3 and GUI-5 take one main-window tooltip each (no hover painted) | MEASURED 2026-09-11: THE CURSOR LANDS AND THE HOVER DOES NOT PAINT. The addressing half is proven on all four captures - `op=find` resolved each control by text and `op=pointer` read its own move back within 1 px (`centre=133,109` -> `at=133,110`; `133,169` -> `133,170`; `133,161` -> `133,162`; `133,196` -> `133,197`) - and the painting half produced nothing: every hover dump's `roots` tree hashes IDENTICAL to its non-hover sibling, the `TooltipEchoBox` strip node is the same EMPTY label, and the PNG region covering the whole main window is pixel-identical. So NEITHER echo has a useful picture, and `DisabledHoverEcho`'s reason is unpainted for the same one cause rather than a second. THE CAUSE IS OPEN and the obvious guess is already in tension with the bytes: an unfocused game would not update `Input.mousePosition` at all by the op's own contract (`TestCommandUiPointer.cs:52-56`), and every move read back. What the evidence supports is POSITION vs EVENT - `SetCursorPos` warps the cursor without injecting input, Unity's frame sample follows a warp and the window's mouse EVENT stream does not, and IMGUI hover is computed while the event pump runs. The candidate fixes (an opt-in `focus=true` calling `SetForegroundWindow`, or a `SendInput` relative move that produces a real `WM_MOUSEMOVE`) both need a flight and both reach further outside the process; the second is the one the reasoning favours. Filed as GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT. **BOTH CANDIDATES FLEW ON 2026-09-15 AND BOTH ARE REFUTED - the finding now has a positive cause instead of two guesses.** `op=pointer` gained exactly those two opt-in flags (`focus=true`, a foreground ladder of already / plain `SetForegroundWindow` / documented `AttachThreadInput` retry; `nudge=true`, one relative `SendInput` (+1,0)/(-1,0) pair) plus five reported keys after `via=` (`focus= nudge= fgOutcome= fg= tooltip=`), both defaulting false, and GUI-7 flies both on both hover moves (run `2026-09-15_1539`, PASS on attempt 1, 63 s, 17 harvested files, same pinned DLL hash). EVERY MECHANISM CONFIRMED APPLIED AND THE HOVER STILL DID NOT PAINT: `fgOutcome=attached` on the first move (plain `SetForegroundWindow` refused, the `AttachThreadInput` retry took it) and `fgOutcome=already` on the second, `fg=true` after both, the relative pair accepted on both (`nudge=true sent a relative (+1,0)/(-1,0) SendInput pair`), and both answers read `tooltip=-`. THE DISCRIMINATOR the finding asked for is a new one-shot Verbose probe inside a Parsek `OnGUI` Repaint (`TooltipEchoStripLatch.SampleMousePositionProbe`, armed by every `op=pointer` and re-armed after the read-back lands): `Event.current.mousePosition` reads `eventLocal=-8.0,-8.0` -> `eventScreen=0.0,0.0` on EVERY probe of the flight, while `Input.mousePosition` in the SAME pass tracks the commanded point exactly (`input=133.0,558.0` -> `inputGuiY=162.0` against `133,161`; `input=133.0,523.0` -> `inputGuiY=197.0` against `133,196`). So the POLLED position follows a warped cursor and the position IMGUI computes its hit test from never moves - it stays pinned at the screen origin, outside every control. Mechanically confirmed twice on that run's own artefacts: the `roots` tree of `b1-main-tooltip-timeline-advanced.gui.json` and of `b1-main-disabledecho-spawncontrol-advanced.gui.json` both hash IDENTICAL to `b1-main-idle-advanced.gui.json` (sha256 of the canonicalised subtree, first 16 hex `dbf76cbc604a04a1` for all three), and a per-pixel comparison of the main-window region (8,8)-(258,708) against the idle capture differs ONLY at y >= 445 (2443 and 1703 pixels of 175000), which is the live flight status block BELOW both hovered rects - the hovered buttons themselves (`rect=18,150,230,21` for `Real Spawn Control (0)`, `rect=18,185,230,21` for `Timeline`) and the tooltip strip are pixel-identical, so there is no button highlight and no strip text. An earlier run `2026-09-15_1520` was PARSEK-FAIL on exactly the two `required` contracts that pinned a NON-EMPTY strip; those were removed as unsatisfiable and replaced by contracts pinning what the flags DID plus the probe measurement, which is what the amended lane passes on. THE FLAGS STAY IN as measured: the answer reports what they did, so the next candidate is compared against this reading rather than against a guess. A SECOND, NOT-AUTOMATION-ONLY piece of observability came with them: `TooltipEchoStripLatch` logs one `[Parsek][INFO][UI] tooltip echo strip text=<text> len= frame= distinct=` line per DISTINCT strip text, capped at 200 distinct texts per session with one line saying so - and ZERO such lines appeared in this flight, which is itself the reading. WHAT IT DID BUY: a HARNESS FIX - `validate_ui_action_step` parsed `x=` / `y=` as literals, so the documented `${stepN.cx}` chain (the whole reason `op=find` reports a centre) failed pre-launch validation and no census could hover anything. Fixed with `hlib.is_handle_ref`, pinned by `test_a_runtime_handle_is_a_legal_pointer_or_rect_coordinate` |
| `find` | every `pointer` step above | CONFIRMED, and it is the half of the pair that worked. The match LADDER is what makes a spec readable: `text=Real Spawn Control` missed the exact rung and PREFIX-matched the live `Real Spawn Control (0)` (`match=prefix matches=1 searched=13`), so a spec never has to guess the count a label carries; the three exact matches read `matches=1 searched=8` / `8` / `13`. GUI-7 pins `match=prefix` so that stays true |
| `expand` | GUI-3 `ib-logistics-expanded-advanced` and `ib-missions-missions-expanded-advanced`; GUI-4 adds the `key=none` mirror | CONFIRMED with numbers. `key=all` is the affordance that buys the pictures, exactly as predicted: one step over the Logistics window answered `changed=5 expanded=5 total=5` and one over the Missions window `changed=29 expanded=52 total=52` on GUI-3 (`changed=13 expanded=22 total=22` on GUI-4, mirrored by `key=none` -> `changed=22 expanded=0 total=22`). The dumps behind them are 190 and 767 nodes against 121 and 371 collapsed (GUI-4's Missions pair is 402 against 212, and its Recordings pair 513 against 111). The payload's `changed=` is what separates "already open" from "opened by this step" when the two look alike |
| `target` | GUI-3 `ib-structure-route-advanced` / `ib-structure-mission-advanced`; GUI-4 `bd-structure-mission-advanced` | CONFIRMED and closes GUI-CENSUS-STRUCTURE-WINDOW-HAS-NO-DRIVEABLE-TARGET for the KSC half: the echoes read `steps=4` / `steps=38` / `steps=31` and each window is titled after its subject (`Parsek - Route: KSC -> Mun`, `Parsek - Duna Supply 1`, `Parsek - Kerbal X #2`) instead of "Nothing to show.". NARROWER THAN THIS ROW PROPOSED: it shipped with the two Structure openers only, so `ShowWipeRecordingsConfirmation` and the setter family (`showExpandedStats`, `TimeRangeFilterState`, `showCustomRange`, `sortColumn`, `foldedKerbals`) are still unreachable and every surface this row attributed to them is still dark |
| `picker` | GUI-3 and GUI-4, both titles each, plus GUI-3's link picker | CONFIRMED: all three excluded window hosts are reachable through their own production openers, and each picker capture's dump carries `windows=4` with the popup's own title - `Manage Groups` (four group rows on GUI-4: `Kerbal X`, `Kerbal X / Debris`, `Kerbal X #2`, `Kerbal X #2 / Debris`), `Set Parent Group` (the `(None / Root level)` toggle plus the non-self rows), and `Link round-trip partner` (`Link 'Route: KSC -> Duna' with:` over a one-route choice). GUI-3's `setparent` also answered its OWN question: `group=Kerbal X #3` resolved, so `GroupPickerPresentation.BuildTreeModel` does publish the auto-generated root group under the name the recording carries. `recording=first` is what lets a committed spec name a row without naming a save-specific id |
| `dialog` | wave 2: every lane, as a NEGATIVE assertion. Wave 3: `GUI-10-census-dialogs`, beside each of the six raises | THE OP IS THE READ-ONLY HALF AND STILL IS - it reports a live popup and holds nothing - so the wave-2 reading stands as written: neither prerequisite this row named shipped with `gui-census-ops`, `ExitToSpaceCenter` REFUSES `dialog-required` rather than driving an exit into a modal, `AnswerMergeDialog` drives the re-fly conclusion AND invokes the button inside one call (`DriveReFlyConclusion` -> `TryInvokeMergeButton`) with no frame between, `SimulateStockSwitchClick` turns all three pre-switch cases into typed REJECTEDs, and all six wave-2 lanes' steps answered `open=false count=0 nbuttons=0 name=- title=- buttons=-` so nothing stood over any of the 79 captures. WHAT CHANGED ON 2026-09-15 IS THAT A DIFFERENT DOOR SHIPPED, not that those prerequisites were met: `UiAction op=raise popup=<name>` calls ONE dialog's own production spawn site and STOPS, leaving the modal standing for this op to report and `CaptureScreenshot` to photograph, and `op=dismiss popup=<name> [press=<button>]` takes it down afterwards. So "ALL 21 DIALOGS REMAIN UNPHOTOGRAPHED" is retired: `GUI-10-census-dialogs` (tier operator, host `fixtures/saves/bdock-recorded` at SPACECENTER, run `2026-09-15_1538`, PASS on ATTEMPT 1) photographed SIX, each one read back by this op as `open=true count=1` with its name, title and ordered button list - `actionblocked`, `savefailed`, `wiperecordings`, `wipemilestones`, `fastforward`, `seal`. The seventh raisable row is refused BY NAME on this host (`REJECTED dialog-target-unavailable popup=rewind detail=no-rewind-owner-among=21`) and 14 stay filed with a reason (section 2's dialog row names both sets). THE ONE-MODAL-AT-A-TIME GUARD FIRED LIVE: a second raise while one stood answered `REJECTED dialog-already-open popup=wiperecordings open=ParsekSceneExitSaveFailed count=1`, which is the `count > 1` state this op flags as a finding, prevented. NOTHING MUTATING WAS PRESSED: one harmless `OK` on `actionblocked` (`dismiss popup=actionblocked press=OK via=button`), the other five dismissed unpressed through `DismissPopup`, and the seam REFUSES `press=` on every mutating confirm (`press-not-allowed`) - proven by `recordings.count 21` holding and by all three of `All recordings wiped` / `All milestones wiped` / `Sealed slot=` being `forbidden` and absent. The `seal` row's `ControlTypes.All` input lock, which only its own button callbacks release, was released by the dismiss path (`uiaction dismiss popup=seal released the dialog's own ControlTypes.All input lock`, plus `Seal dialog input lock cleared`). AND THE DUMP IS NOT THE EVIDENCE HERE, STRUCTURALLY: a `PopupDialog` is uGUI while `GuiTreeRecorder` patches the IMGUI funnel, so a dialog CANNOT appear in a `.gui.json` at all - the PNG plus this op's own payload is the whole record |
| `rect` clamping | every wave-2 lane commands `missions` at 1355 and `logistics` at 1410 | shipped, and GUI-1's own reading run confirmed the width half (`rect=270,8,400,718`). Wave 2 names each floor outright so the spec says what the picture is, and its own echoes report the clamp explicitly - `op=rect window=spawncontrol` came back `rect=270,8,750,300 clamped=false minW=350 minH=150`, and the Timeline floor capture `bd-timeline-refly-minsize-advanced` produced a reading the row did not predict: the seam APPLIED the commanded 520x150 unclamped (`want=270,8,520,150 applied=270,8,520,150 clamped=false min=520,150`) and the window DREW at `270,8,606,245`. So `TimelineWindowUI`'s declared 520x150 is the resize-DRAG floor, not a size the window can occupy - GUILayout expands it to its content, and 606x245 is the smallest the Re-Fly view actually renders at. A "column set pinned wider than its own window" defect therefore cannot be produced at the declared floor for this window, and section 7's floor should be read as a minimum REQUEST rather than a minimum picture |

| op | unlocks |
|---|---|
| `pointer` (park the IMGUI mouse over a named control's rect for one Repaint) | PREDICTED: EVERY populated `TooltipEchoBox` strip (11 windows), every `DisabledHoverEcho` reason (the five rewind refusals, the three spawn refusals, the two warp refusals, the four `Warp to...` reasons, the two Watch reasons, the route stepper floors, `Pick a route above...`, `This route was not built from a recorded mission`), marquee mode, and every hover-only marker label. DELIVERED: NONE OF IT, and that is measured rather than pending - the op parks the OS CURSOR, not the IMGUI mouse, and IMGUI's hover did not follow it on any of wave 2's four hover captures. Everything in this cell stays unphotographed until GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT is closed; the hover-only marker label is the same miss one surface over (`MapMarkerRenderer.ShouldDrawLabel(sticky, hover)` had neither on `play-mapview-ghostmarkers-advanced`). STILL DELIVERED: NONE OF IT after the 2026-09-15 flight either, and the finding is no longer OPEN but REFUTED-WITH-A-CAUSE: the two opt-in flags `focus=true` / `nudge=true` were both applied and measured (`fgOutcome=attached` / `already`, `fg=true`, the `SendInput` pair accepted) and the strip still read `tooltip=-`, because `Event.current.mousePosition` inside Parsek's own Repaint stays at the screen origin (`eventScreen=0.0,0.0`) while `Input.mousePosition` follows the commanded point. Whatever closes this cell has to move the position IMGUI hit-tests against, which neither foreground nor a synthetic move does - see the `pointer` row above for the numbers |
| `find` (resolve a control by label or `ref` from a prior `.gui.json`) | the addressing layer every other op needs; without it a click op can only take indices |
| `expand` (toggle a disclosure or caret by key) | all 21 Recordings-tab leaf-row cells with their phase colours, status words and tree connectors; chain and grouped blocks; STASH member rows; expanded per-vessel interval rows; the `Events (N)` digest and its `Go to` (the digest is removed since Missions Model 1; the `Go to` is on the Docked partner rows); the Logistics route and candidate detail panels (the largest un-photographed control cluster in the mod); the near-miss, dismissed and dormant disclosures; expanded Kerbals owner chains and folded outcome headers; the Career `Pending in timeline` fold |
| `target` (invoke a typed entry point rather than synthesising a click: `OpenStructureWindowForMission`, `OpenStructureWindowForRoute` (retired 2026-10-01 with the route Log), `ShowWipeRecordingsConfirmation`, `SetUiComplexityMode`-style setters for `showExpandedStats`, `TimeRangeFilterState`, `showCustomRange`, `sortColumn`, `foldedKerbals`) | the populated Structure window in both modes, the Info-expanded stats columns, the time-range filter indicator and every non-default Timeline preset, the custom sliders, every non-default sort direction, the folded Kerbals summary |
| `picker` (arm a popup over a row selection) | the Group picker in both its titles, and the Logistics link picker - the two windows the seam excludes today by name (`TestCommandUiAction.cs:369-377`) |
| `dialog` (raise a `PopupDialog` and HOLD it open for a capture, then answer it) | PREDICTED: all 21 dialogs, behind two prerequisites - `AnswerMergeDialog` needing a surface-only mode (it finds and immediately invokes today, `ParsekTestCommandAddon.cs:2260`) and a path not requiring a live Re-Fly marker (`:2258-2260`), plus the three test hooks that short-circuit a spawn being left null. DELIVERED 2026-09-15, THROUGH A DIFFERENT DOOR and with neither prerequisite met: `op=dialog` stays read-only and the raising is a separate pair, `op=raise popup=<name>` / `op=dismiss popup=<name> [press=<button>]`, each calling ONE dialog's own production spawn site and leaving the modal standing. SIX of the 21 have a picture (`GUI-10-census-dialogs`), the seventh raisable one is refused by name on that host (`dialog-target-unavailable popup=rewind`), and 14 stay filed with the state each would need - section 2's dialog row names all three sets. The two prerequisites above therefore remain the route to the MERGE dialog specifically, not to the family |
| `rect` clamping (refuse or warn when a commanded rect is below the window's own minimum) | prevents a repeat of the shipped Logistics capture, which was forced to 1280 against a 1410 minimum and photographed a layout no player can produce |

Two targets stay out of reach even with the full family, and should be recorded as such rather
than chased: the paused-host variant (no verb raises the pause menu, and the expected picture is
"the window is absent") and the Settings null-game fallback (the seam waits for a loaded game
before running any `UiAction`, which is exactly the state the branch needs).

THREE MORE JOINED THAT LIST ON 2026-09-11, measured while authoring wave 2 rather than guessed:

  * ALL 21 DIALOGS, for the reason in the `dialog` row above. RETIRED 2026-09-15 AS A
    WHOLE-FAMILY CLAIM, and by a route this bullet did not name: not a surface-only
    `AnswerMergeDialog` mode (which still does not exist), but a new pair of ops that call a
    dialog's OWN production spawn site and stop - `op=raise` / `op=dismiss`. SIX of the 21 are
    photographed (`GUI-10-census-dialogs`, run `2026-09-15_1538`, PASS on attempt 1), the
    seventh raisable one is refused by name on that host, and 14 are still unreachable - but
    unreachable ONE AT A TIME, each for a stated missing piece of live state, rather than as a
    family. The two prerequisites above are now the route to the MERGE dialog specifically.
  * THE STICKY GHOST MARKER POPUP. `op=pointer` MOVES the cursor and there is no click op in
    the family, so the right-click section 6.3 names has no driver.
  * THE TRACKING STATION SCENE, which is worse than section 6.1's row suggests.
    `ParsekTrackingStation.OnGUI` draws MARKERS ONLY and hosts no Parsek window, so `UiAction`
    answers `REJECTED ui-host-unavailable` there by design - a TS lane could take a full-screen
    PNG and could not even open `main` to make the surface visible. The driver branch that row
    asks for is necessary and not sufficient.

### 6.3 Outside the recorder by construction

These need a capture path, not a verb. All of them are PNG-only by nature. Wave 2 PAYS the
ghost-map-marker row (`GUI-6-census-flight-playback`, label `play-mapview-ghostmarkers-advanced`:
`EnterMapView` then a framebuffer capture, exactly the pair this table names - 243
`[GhostMap] Marker DRAWN` lines stand behind that frame, 242 of them `PID-less marker rides its
own polyline`) and pays one row this table did not list, the WATCH MODE overlay
(`play-main-watchmode-advanced`, which turned out to be in the control tree as well as the PNG).

IT DOES NOT PAY THE TOOLTIP ROW. The `pointer`-plus-capture pair RAN on three lanes and four
captures (GUI-3, GUI-5, GUI-7) and the hover did not paint: the cursor landed within 1 px of
each resolved centre and every hover capture is byte-for-byte the un-hovered window, tree and
pixels both. So the tooltip third of the first row is MEASURED-AND-STILL-OPEN
(GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT), the dialog third is BLOCKED rather than
pending (see 6.2), and the currency-tooltip and badge rows - both hover-only - inherit the same
blocker before their own missing verb even matters. The remaining rows are untouched.

WAVE 3 (2026-09-15) PAYS THE DIALOG THIRD OF THAT FIRST ROW, six of 21, exactly as this table
describes the need: "raise it and hold it" is now two ops (`op=raise` / `op=dismiss`) and the
capture half was already there. The tooltip third is still unpaid, and no longer for an open
reason: the two candidate fixes flew, both applied, and the hover still did not paint because
the position IMGUI hit-tests against never moves (see the `pointer` row of 6.2). The currency
tooltip and the badges inherit THAT, which is a stronger blocker than a missing verb.

| target | what is needed |
|---|---|
| all 21 dialogs, all 7 overlays, every populated tooltip | full-screen `CaptureScreenshot` paired with `pointer`, not a window-tree dump. `CaptureScreenshot` already grabs the framebuffer, so the missing half is only "raise it and hold it" |
| the stock-UI annotations (R&D, Astronaut Complex, Mission Control) and their tooltip text | a verb that opens a stock facility screen plus `pointer`; the `c2` career fixture with a committed tech, contract and hire is the subject |
| the currency reservation tooltip (four text forms x two widgets) | `pointer` over a stock uGUI widget rect - the first pointer target that is not a Parsek control |
| ghost map markers in either scene | the existing `EnterMapView` verb (M-A7) plus a full-screen capture; the sticky variant additionally needs a synthesised right-click on a marker |
| any tint (Logistics red/cyan, phase colours, amber pending rows, the `(partial)` dim) | the tree carries no colour field, so every tint claim must be paired with a PNG |

## 7. Sizing facts

Minimums, defaults and the fixed `GUILayout.Width` pins, measured against the 1280x720 census
instance and a 1920x1080 player screen. "First-open" rects are seeded only when
`windowRect.width < 1`, so a seam-commanded `op=rect` is never overwritten.

| window | min W x H | first-open W x H | resize? | fits 1280x720 | fits 1920x1080 |
|---|---|---|---|---|---|
| main | none | 250 x content (both hosts draw it at `GUILayout.Width(250)` and reset its height each frame) | NO handle | yes | yes |
| Missions | **1355** x 150 (`RecordingsTableUI.MinWindowWidth` / `MinWindowHeight`) | 1355 x main height (KSC 2x) | yes | capped to 1280, both tabs scroll sideways (header with the rows) | yes, unchanged |
| Timeline | 610 x 150 (`TimelineWindowUI.MinWindowWidth` / `MinWindowHeight`) | 820 x max(600, main height) | yes | yes | yes |
| Kerbals | 586 x 150 (`KerbalsWindowUI.MinWindowWidth` / `MinWindowHeight`) | 760 x 400 | yes | yes | yes |
| Logistics | **1410** x 500 (`UI/LogisticsWindowUI.cs:390-391`) | 1556 x 500 | yes | capped to 1280, its section stack keeps its 1410 layout and scrolls sideways in its own scroll view | yes, unchanged |
| Structure | 420 x 160 (`UI/StructureListWindowUI.cs:69-70`) | 820 x 320 | yes | yes | yes |
| Settings | none | 280 x 600 (the 600 is replaced by a height fit on first open and on every mode change) | NO handle; height is fixed between fits | yes | yes |
| Real Spawn Control | 350 x 150 (`SpawnControlUI.MinWindowWidth` / `MinWindowHeight`) | 750 x rows (`SpawnControlPresentation.FirstOpenHeight`, 191 for one row) | yes | yes | yes |
| Gloops | none | 280 x 230 (`GloopsRecorderUI.DefaultWindowWidth` / `DefaultWindowHeight`) | NO handle | yes | yes |
| Test Runner (Settings) | 320 x 600 (`TestRunnerUI.MinWindowWidth` / `MinWindowHeight`) | 440 x 600 | yes | yes, 600 of 720 | yes |
| Test Runner (global) | 320 x 600 (`TestRunnerShortcut.MinWindowWidth` / `MinWindowHeight`) | 440 x 600 at a FIXED screen position (20, 60), not anchored to the main window | yes | yes | yes. GUI-12 commands it to 620x700 through `op=rect window=testrunnerglobal`, the same rect as its twin, so the two are comparable at a glance |
| Group picker | 260 x 220 (`GroupPickerUI.GroupPopupMinW` / `GroupPopupMinH`) | 320 x 360 (`GroupPopupDefaultW` / `GroupPopupDefaultH`), next to the click or centred over the Missions window, clamped on screen (`PickerWindowLayout.PlaceOnOpen`) | yes | yes | yes |
| Logistics link picker | 260 x 200 (`LogisticsWindowUI.LinkPickerMinW` / `LinkPickerMinH`) | 360 x 320 (`LinkPickerDefaultW` / `LinkPickerDefaultH`), next to the click or centred over the Logistics window, clamped on screen (`PickerWindowLayout.PlaceOnOpen`) | yes | yes | yes |

Two windows are laid out wider than a 1280 px screen: Logistics (1410) and Missions (1355).
Every other window's minimum and first-open default fit 1024 px. A window with a resize handle
that cannot fit the screen at its own width is fitted before each draw (`UI/WideWindowLayout.cs`) (`ParsekUI.HandleResizeDrag` -> `FitWindowToScreen`):
its width is capped to `Screen.width` (and raised back to `min(MinWindowWidth, Screen.width)`
once a wider screen allows), it is moved fully on-screen horizontally and top-on-screen
vertically, and its height is never touched. A window that fits is never moved, wherever the
player put it. A resize drag is never wider than the screen, and its floor becomes
`min(MinWindowWidth, Screen.width)`. A window capped below its natural width
(`MinWindowWidth`) scrolls its content sideways through `WideWindowScroll`: the Missions
window wraps its pinned header and its body scroll view in one horizontal scroll view laid
out at 1355 (the body's vertical bar then sits at the content's right end), and Logistics
gets a minimum content width inside its existing scroll view. On a screen the window fits,
neither draws anything. The windows with no resize handle (main, Settings, Gloops) are not
fitted. `op=rect` applies the same fit, so on a narrow screen a rect commanded at the
minimum reads back at the screen width with `clamped=true`; `op=state window=missions
key=scrollX` drives the Missions window's horizontal offset. Census lane:
`GUI-29-census-wide-windows-1280`.

Fixed content pins, for the same reason - a column constant that exceeds its own content is the
class of defect the Milestones `Rewards` overflow belongs to:

| window | pins |
|---|---|
| Recordings tab | `ColW_*` 20 / 30 / expand / 120 / 80 / 110 / 70 / 120 / 60 / 60 / 90 / 50 / 60 / 90 / 80 (header order; `UI/RecordingsTableUI.cs`); `ColHeaderHeight` 32; body row height 29 measured |
| Missions tab | 20 / 30 / expand / 120 / 110 / 85 / 120 / 116 (`ColW_*`; Interact = `InteractButtonWidth` 100 + 2 x `InteractCellInset` 8); Interact pair halves 48 + gap 4; loop grid column A 70 (`ColW_HeaderButton`), column B 92, Log slot 100, `LoopCellWidth` 192; `ColHeaderHeight` 32; `CompositionRowMinHeight` 22 |
| Timeline | `TimeColumnWidth` 160, every row action (`W`, `FF`, `R`, `Fly`, `Seal`, `Go to`) 48 (`MissionsWindowUI.InteractPairButtonWidth`), warp button 186, warp fields 36; the six filter / preset cells are responsive at `Max(93, (width - 50) / 6)` (`ComputeFilterCellWidth`) |
| Kerbals | Roster 210 / 220 / expand; Flights 130 / expand / 110 (`KerbalsWindowUI.ColW_*`) |
| Logistics | routes 30 / expand / 95 / 180 / 150 / 80 / 135 / 240 / 120 / 190 = 1220 fixed; candidates 30 / expand / 95 / 180 / 260 / 80 / 190 = 835 fixed (`UI/LogisticsWindowUI.cs:343-372`). The `MinWindowWidth` comment at `:383-389` is stale: it still totals a 90 px Next column and claims about 1175 |
| Structure | 28 / 110 / expand / 95 / 185 / 140 (`UI/StructureListWindowUI.cs:62-67`) |
| Settings | 45 auto-loop field, 40 unit button, 85 audio label, 35 percent label |
| Real Spawn Control | expand / 60 / 70 / 90 / 110 / 70 / 100 (`SpawnControlUI.SpawnColW_*`); `Close` 132 |
| Test Runner | status icon 20, `Run` 40, `Run+` 44, play 24, search label 48, clear 24; `ErrorIndent` 40, `ErrorMaxWidth` 380 |
| overlays | watch box 300x50 pinned; currency tooltip 147 wide; stock-control annotations draw no Parsek box; map icon 20 with 6 px click and 24 px toggle pads; ghost label 250x40 |

The tooltip echo strip is 23 px in single-line windows (Timeline, Logistics, Log, Missions,
Real Spawn Control) and 38 px in two-line windows (main, Kerbals, Settings, Gloops,
both Test Runners); its height is pinned to a probe measurement so the window never changes
height on hover (`TooltipEchoBox`'s probe lines).

**Table row inset.** A column-header row and its body rows share a left origin only when
both open with an EXPLICIT container style whose horizontal margin and padding are
`ParsekUI.TableRowHorizontalInsetPx` (0) and the body's list-area box carries no horizontal
margin or padding either (`ParsekUI.GetTableRowStyle` / `GetTableHeaderRowStyle` /
`GetTableBodyBoxStyle`): KSP's skin reports box / label / button / toggle / textField margin
L4/R4 and box padding L4/R4 (logged once per draw by `RecordingsTableUI` as
`Rec table skin margins`), Unity places a child at
`parentRect.x + max(parentStyle.padding.left, childStyle.margin.left)`, and a group opened
with NO style inherits its margin from its first child - so a plain `BeginHorizontal()`
header drawn outside a scroll view and plain body rows drawn inside one sit 4 px apart, 8 px
with a `GUI.skin.box` wrapper in between. A header pinned OUTSIDE the body scroll view
reserves the scrollbar gutter as its own right padding (`GetTableHeaderRowStyle`, paired with
`alwaysShowVertical: true` on the body) rather than as a trailing `GUILayout.Space` at each
call site, so the width a pinned header reserves is the width the scroll view actually claims
and the expanding column comes out equal on both halves.

**Cell TEXT inset, not just the cell rect.** Equal cell rects are not equal text: the column
header style is a box (padding L4/R4), so its text starts 4 px inside its rect, while a bare
`GUI.skin.label` starts at its rect's edge. A body cell under a boxed header therefore uses
`ParsekUI.GetTableCellStyle()` - a label whose horizontal padding is copied from the header
style by the pure `ComposeTableCellPadding` (vertical padding stays the label's) - and the
text delta is `ParsekUI.HeaderToCellTextDeltaPx` = 0. Built once per skin with a
`Table cell style built: colHdr.padding=... cell.padding=...` Verbose line, which is the
number a dump measurement adds to each rect's x. The Recordings tab, Kerbals, Real Spawn
Control and the Log window use it (pinned by `TableRowInsetAlignmentTests`), and so do the
group picker's entries (`PickerWindowLayoutTests`).

**The gutter itself is TWO skin terms, not the scrollbar width.** Both are read off the live
skin by `ParsekUI.VerticalScrollbarFootprintWidth()` + `TableCellHorizontalMarginPx()`,
combined by `VerticalScrollbarGutterWidth()` and printed in the same
`Rec table skin margins` line as `vScroll.fixedWidth` / `vScroll.margin` /
`vScroll.footprint` / `tableHeaderGutter`:

1. **The scroll view's real footprint, 16.** Decompiled `GUIScrollGroup.SetHorizontal`
   (`UnityEngine.IMGUIModule`, KSP 1.12.5) lays its content group out at
   `width - verticalScrollbar.fixedWidth - verticalScrollbar.margin.left`, so the bar costs
   `fixedWidth + margin.left` = 15 + 1 = 16 - one px more than the 15 the bar's own rect
   measures. Visible in every dump as the outer-vs-inner width step: Real Spawn Control
   730 -> 714, Structure 980 -> 964, Missions 1335 -> 1319.
2. **One cell margin, 4.** Decompiled `GUILayoutGroup.SetHorizontal` reduces a styled group's
   content width by `max(style.padding.right, lastChild.margin.right)` - a MAX, not a sum. A
   body row carries no right padding, so its content already ends one 4 px cell margin inside
   its group; the header's reserved padding REPLACES that margin rather than adding to it, so
   it has to cover both terms.

Gutter = 20. Reserving `fixedWidth` alone leaves every pinned header exactly 5 px wider than
its body (1 px of scrollbar margin plus the 4 px the MAX replaces).

A header that reserves the gutter as a trailing `GUILayout.Space` instead owes the same 20
when its body's list-area box spends a `padding.right` of its own - the Recordings tab, whose
rows measure x=14 width 1311 inside a 1319-wide box. The Missions tab's box spends none (rows
x=10 width 1319), so that header owes the bare 16 footprint; it keeps its own reservation
under GUI-MISSIONS-WINDOW-MERGED-FIRST-HEADER-CELL, whose remaining offset is structural.
