# Parsek GUI inventory - the shipped surface, and what the backend exposes through it

Measured 2026-09-11 against commit `4eb427e9e`. All `file:line` references are relative to
`Source/Parsek/` unless they start with `harness/`, `docs/` or `scripts/`.

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
- **The Career State window's Facilities tab CAN see upgrades**, which this file could not
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
  footprint at all, so it draws `(no active strategies)` like every other host).

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
| `PopupDialog.SpawnPopupDialog(` excluding `InGameTests/` + `TestCommands/` | **21** modal dialogs |
| `void OnGUI()` in production | **6** hosts: `ParsekFlight.cs:2087`, `ParsekKSC.cs:227`, `ParsekTrackingStation.cs:350`, `CurrencyReservationOverlay.cs:87`, `OverlayBadge.cs:122`, `InGameTests/TestRunnerShortcut.cs:177` |
| IMGUI draw calls outside `UI/` | 7 files: `CurrencyReservationOverlay.cs`, `MapMarkerRenderer.cs`, `OverlayBadge.cs`, `ParsekFlight.cs`, `ParsekKSC.cs`, `ParsekUI.cs`, `WatchModeController.cs` |
| `UiSurfaceVisibility.IsVisible(` | **12** call sites in 5 files; 4 of the 14 enum keys have none |
| `ParsekLog.ScreenMessage` / `ScreenMessages.PostScreenMessage` | 107 raw, **95** real producers |

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
| uGUI (`PopupDialog`, `RawImage` badges, the stock toolbar button) | all 21 dialogs, the R&D / Astronaut / Mission Control badges, the ApplicationLauncher button | NO | yes, if raised |
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
| modal dialogs | **0 of 21** | **0 of 21** | **6 of 21** | **6 of 21** | 15 of the 21. SIX HAVE A PICTURE as of wave 3, each raised through its own production spawn site, reported standing by `op=dialog`, photographed, dumped and dismissed: `actionblocked` (`ParsekResourceBlock` / "Action Blocked" / `OK`), `savefailed` (`ParsekSceneExitSaveFailed` / "Save failed" / `OK`), `wiperecordings` (`ParsekWipeRecordingsConfirm` / "Confirm: Wipe Recordings" / `Wipe All`, `Cancel`), `wipemilestones` (`ParsekWipeMilestonesConfirm` / "Confirm: Wipe Milestones" / `Wipe All`, `Cancel`), `fastforward` (`ParsekFastForwardConfirm` / "Confirm: Fast-Forward" / `Fast-Forward`, `Cancel`) and `seal` (`ParsekUFSealDialog` / "Confirm: Seal Unfinished Flight" / `Seal Permanently`, `Cancel`). Each of the six `op=dialog` steps beside them read `open=true count=1` with that name, title and button list. THE SEVENTH RAISABLE ROW, `Confirm: Rewind`, answered the typed refusal `REJECTED dialog-target-unavailable popup=rewind detail=no-rewind-owner-among=21` on this host - its spawn site silently returns when `RecordingStore.GetRewindRecording` is null, and no recording in `bdock-recorded` carries a `rewindSaveFileName` - so a host with a rewind point is what would photograph it. THE REMAINING 14 STAY FILED WITH THEIR REASON, each needing state a pure in-process call cannot supply: the tree merge dialog (`ParsekMerge`; its spawn takes a `RecordingTree` and BOTH its buttons act on it, so a synthetic one's commit would write invented history), the pre-switch decision dialog (`ParsekPreSwitch`; needs a live `Vessel`, so FLIGHT only, and RE-SPAWNS ITSELF on any non-button teardown), the ghost icon context menu (`ParsekGhostIconMenu`; spawned inside a Harmony Prefix over a live ghost ProtoVessel in map view, so there is no method to call), the Tracking Station ghost popup (its host exists only in TRACKSTATION, which runs no ParsekUI, so every `UiAction` there answers `REJECTED ui-host-unavailable`), Re-Fly invoke (`ParsekRewindInvoke`; a RewindPoint with a child slot), Re-Fly revert (`ParsekReFlyRevert`; a live `ReFlySessionMarker`), `Confirm: Disband Group`, the three Logistics confirms (delete route, delete dormant route, create route - a live `Route` or `RouteCandidate`), and the remainder. See 6.2 |
| overlays / markers / badges | **0 of 7** | **2 of 7** | **2 of 7** | **2 of 7** | the Watch Mode overlay (GUI-6 `play-main-watchmode-advanced`, also standing in `play-spawncontrol-advanced`) and the flight-map ghost markers (GUI-6 `play-mapview-ghostmarkers-advanced`, 243 `[GhostMap] Marker DRAWN` lines behind it) ARE photographed. The five still dark: the currency reservation tooltip, the stock-UI badges, the Tracking Station markers, the in-world ghost labels and the Logistics launcher TINT (zero `broken-state tint applied` lines in any lane, so only the untinted button has a picture) |
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

HOW TO READ THE PER-SECTION `Picture:` / `No picture:` LINES BELOW. They are the WAVE-1 reading
(GUI-1 + GUI-2, 26 captures), written before wave 2 flew, so each names what had a picture on
the morning of 2026-09-11. The window INDEX in 3.0 and the overlay table in 3.13 ARE updated to
the wave-2 reading; the per-variant lists inside 3.1 to 3.12 are deliberately not re-walked,
because the authority for what wave 2 photographed is section 2's tally plus section 6's per-row
lane column, and copying 79 labels into ten prose lists would give the next author two places to
disagree. Read a `No picture:` line below as "no picture in wave 1", then check section 6 for
whether wave 2 took one.

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
| 7 | Logistics round-trip link picker (`Link round-trip partner`) | `UI/LogisticsWindowUI.cs:1762` | as its host | excluded `TestCommandUiAction.cs:374-377`; reached by `op=picker picker=link` | `ib-logistics-linkpicker-advanced` (GUI-3, 198 nodes, `windows=4`) |
| 8 | `Parsek - Structure` | `UI/StructureListWindowUI.cs:174` | FLIGHT, SPACECENTER | `structure` | `ksc-structure-advanced` (empty chrome) |
| 9 | `Parsek - Settings` | `UI/SettingsWindowUI.cs:128` | FLIGHT, SPACECENTER | `settings` | `ksc-settings-advanced/basic` |
| 10 | `Real Spawn Control` | `UI/SpawnControlUI.cs:162` | FLIGHT only | `spawncontrol` | `play-spawncontrol-advanced` (GUI-6, 69 nodes, one candidate row) |
| 11 | `Gloops Flight Recorder` | `UI/GloopsRecorderUI.cs:94` | FLIGHT only | `gloops` | `flight-gloops-advanced` |
| 12 | `Parsek - Test Runner` (Settings-launched) | `UI/TestRunnerUI.cs:133` | FLIGHT, SPACECENTER | `testrunner` | `ksc-testrunner-advanced` (4217 nodes) plus four GUI-12 labels, all `cek-` on `career-earned-ksc`: `-testrunner-idle-advanced` (4217 in this window's subtree, the same figure GUI-1 read), `-testrunner-collapsed-advanced` (473), `-testrunner-category-advanced` (479) and `-testrunner-results-advanced` (479, one real pass in the table). Four states, not one picture (3.11) |
| 13 | `Parsek - Test Runner` (global Ctrl+Shift+T) | `InGameTests/TestRunnerShortcut.cs:291` | ANY scene | `testrunnerglobal` | three GUI-12 labels: `cek-testrunnerglobal-idle-advanced` (4214 in this window's subtree), `-collapsed-advanced` (470) and `-category-advanced` (476) - ITS FIRST PICTURES OF ANY KIND. The delta against row 12 is EXACTLY 3 nodes at every comparable state, and the dumps say which three: the `Search:` label, the text field and the 24 px `x` clear button this window does not draw (3.11) |
| 14 | `Set Parent Group` / `Manage Groups` | `UI/GroupPickerUI.cs:224` | as its host | excluded `TestCommandUiAction.cs:369-373`; reached by `op=picker picker=manage|setparent` | both titles on GUI-3 (`ib-missions-grouppicker-manage/setparent-advanced`) and GUI-4 (`bd-missions-grouppicker-manage/setparent-advanced`) |

Two asymmetries in that table are mechanical facts, not presentation choices:

- Window 13 is the ONLY one that calls raw `GUILayout.Window` instead of
  `ClickThruBlocker.GUILayoutWindow` (`InGameTests/TestRunnerShortcut.cs:291`), so it is the
  only Parsek window with no click-through protection; it compensates with its own
  `windowRect.Contains(Event.current.mousePosition)` input lock at `:300`. It is also the only
  row drawn OUTSIDE both scene hosts' `showUI` gate - its draw is in its own `OnGUI`
  (`:264`) - which is why the seam exempts it, and only it besides `main`, from the
  hidden-host settle refusal (`TestCommandUiAction.WindowDrawsOutsideHostShowUi`, `:857`).
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

Contents, top-down (`ParsekUI.cs:745-986`): the launcher column, the supply-route banner when
armed, the tooltip echo strip (`:977`), a version label and `Close` (`:979-986`). The flight
and KSC forms now start the same way (a 10 px gap, then the first launcher); the only flight
difference is the Real Spawn Control launcher on top. The title `Parsek` is drawn with
`ParsekUI.GetMainWindowStyle()` - the shared opaque window style, bold and 2 px larger - so it
stands out from every sub-window title (2026-09-22, owner review round 1).

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

Basic vs Advanced: Basic drops Real Spawn Control and Career; the `Space(10f)` that opens
the Kerbals/Career group is gated on the group being non-empty so Basic shows one gap, not
two. Census-verified sets (2026-09-11): KSC Basic 4 buttons, KSC Advanced 6, FLIGHT Basic 4,
FLIGHT Advanced 7. **Since the 2026-09-22 owner re-ruling the Kerbals launcher draws in Basic
too** (`design-gui-kerbals-window.md` ruling 1), so Basic is 5 buttons in both scenes from
that build on.

The flight status block (`DrawFlightStatus`: `State:` / `Recorded Points:` / `Duration:` /
`Active Ghosts:`) was REMOVED on 2026-09-22 at the owner's review: Parsek records everything,
so a recorder-state readout has nothing left to tell the player. Its pictures in GUI-2 / GUI-6
/ GUI-7 runs before that date are history, not the current window.

State variants without a picture: the RouteRunPrompt banner (`:817-855`, `Open Logistics` /
`Dismiss`), either Logistics tint, an enabled Real Spawn Control, and a populated tooltip
strip.

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
row checkboxes), `flight-missions-missions-advanced` (922, structurally identical), and since
the 2026-09-22 fold re-fly `ksc-missions-missions-collapsed-advanced` (922 - MEASURED identical
to the restored picture, i.e. this window opens fully collapsed),
`ksc-missions-missions-expanded-advanced` (1769, every vessel row, interval and digest open at
once) and `ksc-missions-missions-events-advanced` (999 - ONE mission's `Events (N)` digest open
with everything else shut). No picture:
loop ON with a phase-locked read-only period, `Looped by route`, `Forward` instead of `Rewind`,
`W*`, inline rename, `(partial)` / dimmed rows, `Docked with <partner>` in the Start event
cell, Fly/Seal, chapter headers, partner rows.

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
enabled with resolved targets), and since the 2026-09-22 fold re-fly
`ksc-missions-recordings-collapsed-advanced` (415 - MEASURED identical to the restored
picture), `ksc-missions-recordings-chain-advanced` (453 - ONE group folder open with ONE chain
block expanded inside it, the other fifteen folders shut) and
`ksc-missions-recordings-expanded-advanced` (1915 - every folder, chain block and leaf row at
once). No picture: STASH, the
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

**REBUILT 2026-09-15, REVISED 2026-09-22.** Both tabs are now COLUMN TABLES, the tabs are
named `Roster` and `Flights`, and the row model, the status vocabulary, the sizing numbers and
the `op=expand window=kerbals` seam row live in their own authority:
**`docs/dev/design-gui-kerbals-window.md`**. Everything below the divider is the 2026-09-11
measurement of the PRE-rebuild window, kept because the captures it cites are the "before"
half of that document's section 8.

The 2026-09-22 round (that document's rulings 11-19): the Roster is grouped by slot (each
stand-in a row under the kerbal he covers; the chain fold and its `roster:<name>` expand key
are gone), deleted stand-ins are no longer listed as `Available`, the `Since` column is gone
(`MinWindowWidth` 700 -> 586), a reservation reads `Reserved: aboard <vessel>` /
`Reserved: <mission>` with the release rule in its hover (a reservation does not lift when
its date passes - `KerbalReservationReleaseTests`), Lost rows carry the re-fly remedy in their
hover and every Last flight cell is a Timeline cross-link, an EVA kerbal reads `On EVA`, the
Flights tab dropped its Crew column (the stand-in note is `(flown by <stand-in>)` in the
Mission cell) and dates a mission by its launch only. **The window draws in Basic too**
(owner re-ruling): `MainButtonKerbals` is KEEP and the window left the Advanced -> Basic close
set.

Purpose (unchanged): read-only. What each kerbal is doing now, and how every recorded flight
a kerbal took ended. No reserve / unreserve / swap / clear control; its only mutations are
three transient fold states.

Hosts (unchanged): FLIGHT and SPACECENTER. Basic-HIDDEN at the launcher from 2026-09-15
until the 2026-09-22 owner re-ruling; since then it draws in both modes and survives the
switch to Basic (its Basic picture: `GUI-11` `bdk-kerbals-roster-basic`).

What the rebuild changed against the rows below: two indented outlines became two column
tables sharing one inset (`ParsekUI.GetTableRowStyle` / `GetTableBodyBoxStyle`, zero
header-vs-cell delta); the Roster tab lists EVERY visible kerbal rather than only
ledger-created slots, with plain available kerbals behind one fold row; the status words
changed to `Available` / `Assigned (<vessel>)` / `Reserved ...` / `Stand-in for <owner>` /
`Retired` / `Lost`; raw `UT n` stamps became calendar dates; the `Unlinked Retired` tail is
gone (a retiree with no slot is now an ordinary row); the Flights tab's row unit became the
MISSION rather than the recorded segment; and `DefaultWindowWidth` went 410 -> 760 with
`MinWindowWidth` 280 -> 700 (the arithmetic for both is in that document's section 5).

---

*The 2026-09-11 measurement of the pre-rebuild window follows.*

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

EVERY `file:line` IN THIS SUBSECTION WAS RE-DERIVED ON 2026-09-15 against both runner files,
because that is where the GUI-12 authoring pass hit them; the rest of section 3 is still the
2026-09-11 reading.

They share a row model, status icons, colours, category labels and `Run` / `Run+` / play
semantics (both call into `InGameTests/TestRunnerPresentation.cs` and the same
`InGameTestRunner` API). Two row kinds, no columns, no sort keys (categories are ordinal-sorted):
a category header `"{arrow} {category} ({passed}/{total})"` plus `Run` 40 and `Run+` 44, and a
test row with a 20 px status icon, an expanding name label with `[isolated]` / `[single]` /
`(NNNms)` suffixes, and a 24 px play button - followed by an ALWAYS-rendered error row that
collapses to `Height(0)` when empty, because a conditional begin/end would desync the
Layout/Repaint control count (`UI/TestRunnerUI.cs:386-398`).

| difference | global (Ctrl+Shift+T) | Settings-launched |
|---|---|---|
| search / filter bar | absent | present (`UI/TestRunnerUI.cs:497-512`) |
| footer labels | two (`InGameTests/TestRunnerShortcut.cs:638`, `:640`) | one (`UI/TestRunnerUI.cs:555`) |
| window host | raw `GUILayout.Window` (`:291`) | `ClickThruBlocker` (`UI/TestRunnerUI.cs:133`) |
| input lock | CAMERACONTROLS + six editor types + `KSC_ALL` (`:304-310`) | CAMERACONTROLS only |
| open-flag accessor | `IsOpenForTesting` + `WindowRectForTesting` (`:134`, `:148`), reached through this MonoBehaviour's OWN singleton `Instance` (`:109`) rather than through `ParsekUI` | `IsOpen` + `WindowRectForTesting` |
| complexity | never gated; draws in every scene but LOADING (`:267`) | launcher lives inside the Basic-hidden Diagnostics section, and the close handler shuts an open instance (`ParsekUI.cs:516-521`) |
| extra responsibility | hosts the M-A3 autorun hooks (env consts `:69`-`:80`, read-once parse `:223`, `UpdateAutorun` `:722`, `FireAutorun` `:815`, the multi-category driver `:874`) | none |

BOTH WINDOWS ARE NOW PHOTOGRAPHED, by `GUI-12-census-testrunners` (run `2026-09-15_2057`, host
the committed `career-earned-ksc` fixture at SPACECENTER, 113 categories and 624 play rows on
that build). Seven captures, and the node counts below are the window's OWN SUBTREE with the
whole dump's total in brackets: the Settings-launched runner idle 4217 (4253), every fold closed
473 (509), one category expanded over the closed list 479 (515), and the same rows after a real
run 479 (515); the global runner idle 4214 (4250), closed 470 (506), one category 476 (512).
IDLE MEANS EVERY CATEGORY EXPANDED for both, because each seeds its fold set from its own
discovery at lazy init (`UI/TestRunnerUI.cs:111-116`, `InGameTests/TestRunnerShortcut.cs:471-477`),
which is why GUI-1's single 4217-node picture was the largest dump in the census and still showed
one state of four - and why the load-bearing seam direction is `key=none` rather than `key=all`.

THE DIFFERENCE BETWEEN THE TWO WINDOWS IS MEASURED, not inferred: exactly 3 nodes at every
comparable state (4217/4214, 473/470, 479/476), and the dumps name them - the `Search:` label,
the text field and the 24 px `x` clear button the global window does not draw. The footer
asymmetry the table above states is confirmed in the same dumps and in that direction: the
GLOBAL window draws TWO footer labels (`Results file auto-updates after each run. Multi-scene
runs accumulate.` and `Ctrl+Shift+T to toggle from any scene`) while the Settings-launched one
draws ONE (`Ctrl+Shift+T opens a separate runner window, in any scene`, the P16 fix).

THE SEAM ROUTES THAT BOUGHT THOSE PICTURES, all automation-only, all writing internal state a
click already writes, and none of them a player-facing surface:

- `testrunnerglobal` is a twelfth window-table row, so every `op=describe` payload in the census
  now reads `windows=12`. The count is the seam's whole TABLE rather than the scene's drawn set,
  which is why the eleven older census specs had their pinned echoes bumped from `windows=11` in
  the same commit. It is the only row besides `main` exempt from the hidden-host settle refusal
  (`TestCommands/TestCommandUiAction.cs:857`), because its draw is in its own `OnGUI` outside
  both scene hosts' `showUI` gate, so a settled read-back over it describes a frame that really
  did draw it.
- `op=expand key=category:<name>` drives either window's fold set through ONE key prefix
  (`TestCommands/TestCommandUiState.cs:121`), one set per window, with `window=` saying which -
  the prefix names the same collection shape in both, so a second one would only invite the two
  to drift. The flown readings: `key=none` answered `changed=113 expanded=0 total=113` on each
  window, and `key=category:GuiTree state=true` answered `changed=1 expanded=1 total=113`.
- `op=run window=<runner> category=<name>` runs one in-game test category through THAT WINDOW'S
  OWN `InGameTestRunner` - the category header `Run` button's body, `ResetCategory` then
  `RunCategory` (`TestCommands/ParsekTestCommandAddon.UiRun.cs:106-107`). It is NOT the
  `RunTests` verb, and that is the whole reason it exists: each runner window constructs its own
  runner with its own reflection discovery, so a `RunTests` batch drives a THIRD runner and
  leaves both windows' tables reading "not run" - a capture labelled "results" over a table of
  dots. The flown reading is `uiaction run ok ... discovered=1 total=1 passed=1 failed=0
  skipped=0` over `GuiTree`, and the window followed it. The summary label moved from
  `idle | 0 passed  0 failed  0 skipped  (624 total)` to
  `idle | 1 passed  0 failed  0 skipped  (624 total)`, and the category header from
  `GuiTree (0/1)` to `GuiTree (1/1)`. So
  `cek-testrunner-results-advanced` is the product's own rendering of a real outcome rather than
  a seeded status. That runner is also read by the addon's safe-point gate (`IsBatchRunning` /
  `CommandRunnerIsRunningForGating`), which now covers the Settings-launched window's runner as
  well, since this op can start a batch on it.

NO PICTURE STILL: a FAILED row (the red error row that expands out of its `Height(0)` collapse),
the `Run+` isolated path, and either window MID-RUN - `op=run` is two-phase and polls
`!runner.IsRunning`, so a frame between the dispatch and the finish is not something a lane can
time.

### 3.12 Dialogs (21)

All are stock `PopupDialog` / `MultiOptionDialog` on `HighLogic.UISkin`, so all 21 are
invisible to `DumpGuiTree`, and as of wave 3 **six have a picture** (PNG only, for that same
reason - see the dialog row of section 2 for the six and the 14 that stay filed). Only one pins
an explicit width (08.6, 420 px); eight use the no-width ctor and inherit KSP's 300 px default;
the two cursor-anchored menus use 160 and 180.

**CORRECTION TO THE TABLE BELOW, measured 2026-09-15 while authoring `GUI-10-census-dialogs`
against the source rather than against this table.** The 21 rows are LEFT AS THEY WERE - they
are the 2026-09-11 reading against `4eb427e9e` and re-measuring them is its own task - but
three of them are known wrong today and every `file:line` in the table has moved:

- `Confirm: Wipe Game Actions` is now `ShowWipeMilestonesConfirmation`, title
  `Confirm: Wipe Milestones`, popup name `ParsekWipeMilestonesConfirm` (`ParsekUI.cs:1793`,
  spawn at `:1795`).
- the post-commit `Create Supply Route?` row (`UI/RouteCreationDialog.cs:312`) is GONE: that
  dialog was DELETED as unreachable (GUI census finding D4) and the class is now a pure span
  helper. The deletion is stated at the surviving confirm's own site,
  `UI/LogisticsWindowUI.cs:2801-2803`.
- one dialog the table omits entirely EXISTS: `Parsek Test Isolation` /
  `ParsekTestRestoreDegraded`, spawned at `InGameTests/InGameTestRunner.cs:2580`. It sits under
  `InGameTests/`, which is exactly the directory section 1's counting grep excludes, so it was
  never in the 21.
- so the population is STILL 21 but its MEMBERSHIP differs by those rows: the table's own grep
  (`PopupDialog.SpawnPopupDialog(` outside `InGameTests/` and `TestCommands/`) now returns
  **20**, and the 21st is the test-isolation dialog the exclusion hid.
- line numbers, re-derived: `MergeDialog.cs` 274 / 468 / 726 -> 308 / 502 / 764;
  `ParsekTrackingStation.cs` 1276 -> 1319; `SceneExitInterceptor.cs` 585 -> 590;
  `UI/RecordingsTableUI.cs` 4431 / 4474 / 4510 -> 4696 / 4739 / 4775;
  `UI/LogisticsWindowUI.cs` 2582 / 2623 / 2726 -> 2686 / 2727 / 2831;
  `UI/MissionsWindowUI.cs` 3006 -> 3192; `ParsekUI.cs` 1505 / 1538 -> 1755 / 1795.
  `CommittedActionDialog.cs:31`, `ReFlyRevertDialog.cs:236`, `RewindInvoker.cs:509`,
  `UnfinishedFlightSealHandler.cs:214`, `WarpToTimeController.cs:223` and
  `Patches/GhostVesselLoadPatch.cs:324` are unmoved.

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

None is inside a `GUI.Window` and the badges are uGUI, so none is in any `.gui.json` - WITH ONE
MEASURED EXCEPTION, and TWO now have a picture (updated 2026-09-11 off the wave-2 reading runs).

- **In the tree after all:** the Watch Mode overlay. Its two labels are ROOT-LEVEL nodes in
  `play-main-watchmode-advanced.gui.json` (`Watching: Part Showcase - Lights v1  (295 m)
  [Horizon]` at `rect=[170,15,300,22]`, `[ ] return  |  V camera  |  W cycle` at
  `[170,37,300,18]`), because the recorder patches `GUI.DoLabel` and not only `GUI.DoWindow`.
  The rule is the FUNNEL, not the window: IMGUI drawn outside a window is captured when it goes
  through one of the 17, and what stays invisible is uGUI plus anything drawn through
  `GUI.DrawTextureWithTexCoords` / `GUI.DrawTexture` (which is how the markers' icons are
  painted - neither call is a patched funnel).
- **Photographed:** the Watch Mode overlay (above) and the flight-map ghost markers
  (`play-mapview-ghostmarkers-advanced`, PNG only, with 243 `[GhostMap] Marker DRAWN` lines
  behind the frame).
- **Still with no picture (5):** the currency reservation tooltip and the stock-UI badges (both
  hover-only, and hover does not paint - see 6.2), the Tracking Station markers (no driveable
  scene), the in-world ghost labels (no `SpawnWarningUI` producer fired on either flight lane),
  and the Logistics launcher TINT in its broken state (zero
  `Logistics button broken-state tint applied` lines in any lane, so only the untinted button
  has a picture).
- **Marker LABELS are a separate miss from marker ICONS:**
  `MapMarkerRenderer.ShouldDrawLabel(sticky, hover) => sticky || hover` (`:324`), and the map
  capture had neither - the cursor was parked at the client corner and there is no click op to
  pin one - so that frame carries 243 icons and no label text.

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
| `MainButtonKerbals` | `:59` | KEEP since 2026-09-22 (was HIDE `:181`) | `ParsekUI.cs:904` | nothing in Basic any more; the launcher draws in both modes |
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
`BuildGatedWindowCloseSet` (`ParsekUI.cs:488-529`). That set carries five targets since the
2026-09-22 Kerbals re-ruling (six before it: Kerbals was the second) - CareerState,
GloopsRecorder, SpawnControl, TestRunner (maps to no `UiSurface`; its launcher lives
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
| P7 | ~~The stock Difficulty screen draws 5 Parsek settings whose edits the sidecar silently reverts~~ FIXED 2026-09-14: `ParsekSettings.GameMode` returns `GameParameters.GameMode.NONE`, so the stock screen builds no Parsek section at all (see 5.2) | `ParsekSettings.cs:22-56` |
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
| P1 | The per-recording playback checkbox: the map icon, orbit line, TS row and the KSC terminal-vessel spawn all ignore `PlaybackEnabled` (bug #433). RULED + FIXED 2026-09-14 on the RENDER surfaces (see 5.2); the KSC terminal-vessel spawn stays, by the same ruling | tooltip `UI/RecordingsTableUI.cs:1849`; `GhostMapPresence*.cs` / `ParsekTrackingStation.cs` never read it; `ParsekKSC.cs:373-396` |
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

### 6.0 What the wave-6 seam ops unlock (2026-09-21)

The read-only state audit of 2026-09-21 enumerated about 380 visibly distinct window states
against about 150 captured. Five automation-only seam additions landed for the reachable
part of the gap. This table is what a spec author aims a lane at; the exact steps are in the
wave-6 lane plan, the grammar and refusals in
`docs/dev/design-autotest-command-seam.md` -> `#### UiAction`.

| seam addition | states it unlocks | window |
| --- | --- | --- |
| `op=state key=srcRecordings\|srcActions\|srcEvents` | the three source-OFF row-population branches, all reading `true` in every existing dump | Timeline |
| `op=state key=archived` | the Archived toggle ON plus the `[archived]` row marker (zero hits program-wide today); the same flag from the Recordings tab's Archive checkbox | Timeline + Missions |
| `op=state key=customRange` + `key=preset` | the Custom reveal, the window's only two sliders, the `From:` / `To:` labels (zero hits), the four ranged presets and the active-range readout | Timeline |
| `op=state key=scrollY` | the window's first scrolled PNG. Note the dump already carried below-fold content with full rects, so this buys the PICTURE, not the data | Timeline |
| `op=state key=expandedStats` | the Info toggle's six extra columns (`MaxAlt` / `MaxSpd` / `Dist` / `Pts` / `Start` / `End`, in zero dumps) at +458 px - the largest single layout change in the window | Missions (Recordings tab) |
| `op=state key=archivedMissions` | whole missions dropping out; the only way to exercise `DisplayBlockRendersAnything` and the corner-connector precedence table | Missions (Missions tab) |
| `op=sort` | 20 Missions sort states, 16 Logistics, 8 Spawn Control - all zero captured, and each is materially different (the arrow moves AND the row order does, including group-vs-recording interleaving) | Missions, Logistics, Spawn Control |
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
SPACECENTER - `expandedStats`, both archive keys, three `op=sort` states, all three
`op=edit` editors, the Logistics sort, `popup=deleteroute` and the link + cadence pair),
`GUI-26-census-createroute-and-running-batch` (`rover-route-recorded`, SPACECENTER -
`popup=createroute` and `op=run await=false`) and `GUI-27-census-missions-include`
(`bdock-recorded`, SPACECENTER - `op=select` in all three forms). Their status rows are in
`autotest-status.md` under "The GUI census, wave 6".

FOUR ROWS OF THE 6.0 TABLE PREDICTED MORE THAN THE SOURCES DELIVER, corrected in place
rather than left to mislead the next author. Each was re-derived from the committed bytes
or from the applier, not from the op's name:

  * the **`key=archived`** row's `[archived]` ROW MARKER, and the **`key=archivedMissions`**
    row's "whole missions dropping out", need a host with an archived recording or an
    archived mission. NO FIXTURE AND NOT THE OPERATOR'S OWN CAREER HAS EITHER: the `hidden`
    key is written only when true and appears ZERO times across all 59 fixture directories
    and in `c1/persistent.sfs`, and `archived` reads `False` in all 9 of its occurrences.
    Both keys therefore buy the FILTER CONTROL moving (readable in the dump, which records
    a toggle's own `value`) and nothing else, so `DisplayBlockRendersAnything` and the
    corner-connector precedence table stay uncovered.
  * the **`op=sort`** row's "8 Spawn Control" states are UNREACHABLE THROUGH THE SEAM.
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
| Timeline minimum-size rendering | the existing GUI-1 host | a second `op=rect` at 520x150 plus one capture | GUI-4 `bd-timeline-refly-minsize-advanced`, at the 520x150 floor |
| Logistics populated: Active, Paused, `New (not yet run)`, the Send-Once-armed row | `interbody-route-recorded` (Active + Paused, `completedCycles = 0`) or `depot-route-recorded` (Active, `pauseAfterCurrentCycle = True`) | `op=rect` to at least 1410x500 FIRST, or the Name column collapses as it did in the shipped capture | GUI-3 `ib-logistics-collapsed-advanced` (both rows) and `ib-logistics-expanded-advanced` |
| Logistics `Dismissed (N)` header | `interbody-route-recorded` (carries `DISMISSED_ROUTE_CANDIDATES` with two ids) | none | GUI-3 `ib-logistics-collapsed-advanced` / `ib-logistics-expanded-advanced` |
| Logistics Candidates populated + run-cost suffix | `rover-route-recorded`, or `rover-route-career` for the Career + KSC cost | none | UNCLAIMED. `interbody-route-recorded` carries a DISMISSED list rather than live candidates, so GUI-3's expanded capture shows the candidate SECTION and not a populated one. Wants a `rover-route-recorded` lane of its own |
| Mission header `Looped by route` + greyed Loop | `depot-route-recorded` / `interbody-route-recorded` / `rover-route-recorded` | none; the label draws with no click | GUI-3 `ib-missions-missions-collapsed-advanced` / `ib-missions-missions-expanded-advanced` |
| Recordings route-bound greyed Loop toggles (header level) | same three | none; `AnyRecordingRouteBound` scans all committed | GUI-3 `ib-missions-recordings-expanded-advanced` |
| `Docked with <partner>` in the Start event cell; chapter header rows; `Docked partner:` rows | `bdock-recorded` (or `bdock-station-craft` + `bdock-station-pad`, or `bdock-forge-base`) | none; all three draw with no click | GUI-4 `bd-missions-missions-expanded-advanced` / `bd-missions-recordings-expanded-advanced` |
| Vessel-row Fly / Seal | `refly-a-recorded` | none | UNCLAIMED. `refly-a-recorded` is a fourth `saveTemplate` and therefore a seventh lane; GUI-4's `bdock-recorded` corpus has RewindPoints but its rows are not the Unfinished-Flights shape this needs |
| Kerbals reserved / active owner statuses | `eva2-lko-crewed` | none | UNCLAIMED. `eva2-lko-crewed` is a fifth `saveTemplate`. GUI-5 shoots both Kerbals tabs on a career with a real roster, which is the row below this one rather than this one |
| Kerbals and Career empty states | `fresh-career` | none | GUI-8 `fs-kerbals-outcomes-empty-advanced` and the four `fs-career-*-science-advanced` captures, on `fresh-science` rather than `fresh-career` - see the science row below |
| Missions empty state, Recordings `No recordings.` | `fresh-sandbox` or `fresh-career` | none | GUI-8 `fs-missions-missions-empty-advanced` / `fs-missions-recordings-empty-advanced` |
| Career Contracts SPLIT layout, `Pending in timeline`, the banner divergence suffix | `career-contract-pad` | none; the pending group defaults EXPANDED | UNCLAIMED, AND THE ROW'S OWN PREMISE WAS WRONG - corrected 2026-09-11 off the reading run. `career-earned-ksc`'s nine `CONTRACT` nodes are all `state = Offered`; the tab's `CurrentRows` come from the ACTIVE (accepted) snapshot (`UI/CareerStateWindowUI.cs:690-723`), so `cek-career-contracts-advanced` reads `Active (0)` / `(no active contracts)` under a `Mission Control L1 - slots 0/2 now, 0/2 at timeline end` header. NO committed fixture carries an ACCEPTED contract, so BOTH the populated list and the SPLIT layout now want `career-contract-pad` or a new fixture |
| Career Strategies populated (the `Flow` cell) | ~~`strategy-career`~~ - WRONG, corrected 2026-09-11 off the save's bytes: its `STRATEGIES` node is EMPTY by construction (the fixture seeds `rep = 25` so `L3`'s in-game cell can ACTIVATE a strategy at run time, and that cell's `finally` restores the pool). NO committed fixture carries a live `STRATEGY` node | a NEW fixture | IMPOSSIBLE AS WRITTEN - see the corrected fixture cell. GUI-5 `cek-career-strategies-empty-advanced` and GUI-8 `fs-career-strategies-science-advanced` shoot the two EMPTY forms instead, both labelled as such |
| Career Facilities upgraded rows | ~~`career-earned-ksc`~~ - WRONG, corrected 2026-09-11 off the save's bytes: all ten of its `ScenarioUpgradeableFacilities` entries read `lvl = 0`. That fixture is EARNED in its POOLS (funds 536558, sci 111.6, rep 2.0) and in its milestones, not in its buildings or its contracts | a NEW fixture | IMPOSSIBLE AS WRITTEN - see the corrected fixture cell. GUI-5 `cek-career-facilities-level0-advanced` shoots the all-level-0 form, labelled as such. WHAT THE PICTURE SHOWS, so the label is not misread: nine facility rows all reading `L1`, because the save's `lvl = 0` is the FIRST level and the window prints it one-based. The label names the save value, the picture names the display value, and they agree |
| Career Science-mode and Sandbox-mode banners and empty states | `fresh-science`, `fresh-sandbox` | none; the science lane alone buys four otherwise-dark code paths | GUI-8's four `fs-career-*-science-advanced` captures (science), and GUI-6 `play-career-contracts-sandbox-flight-advanced` (sandbox, in flight). A sandbox banner at the KSC is unclaimed |
| Timeline `R` greyed | any recorded fixture | capture with a pending tree, or delete one `RewindPoints/<id>.sfs` from the staged save | UNCLAIMED. Wants a STAGED save edit (delete one `RewindPoints/<id>.sfs`), which no lane does today - the harness stages a fixture verbatim |
| Timeline `FF` and the countdown time label | an `injectedRecordings` preset whose recording `StartUT` is ahead of the save UT | one capture buys both | UNCLAIMED. Wants a preset whose recording `StartUT` is ahead of the save clock; `part-showcase` starts at UT 50 and GUI-6 jumps PAST it to 55, so its Timeline is behind rather than ahead |
| Timeline `Archived` ON and the `[archived]` row suffix | a staged save with one archived recording and `HideActive=false` | none | UNCLAIMED. Wants a staged save with an archived recording and `HideActive=false`, which no committed fixture carries |
| Real Spawn Control (a GUI-3 flight lane) | `bdock-recorded` / `bdock-station-craft` / `bdock-station-pad` - anything with a recorded craft inside 250 m at under 2 m/s | `op=open window=spawncontrol` now returns OK; then `op=rect` + capture + dump. Closes `GUI-CENSUS-SPAWN-CONTROL-NEEDS-A-CANDIDATE-HOST` | PAID. GUI-6 `play-spawncontrol-advanced`, on LT-5's proven active-ghost host rather than a `bdock-*` one. The step was declared `expect = "OK"` and MET: `open=true already=false`, describe `w8open=true w8rect=268,8,750,200`, `op=rect` answering `270,8,750,300 clamped=false minW=350 minH=150`, and a 69-node dump whose `Parsek - Real Spawn Control` window holds ONE candidate row (`Surface Rover Drive / 435m / 7.9 m/s / Y1, D01, 00:01 / T-11s / Warp to Spawn`) under the launcher's `Real Spawn Control (1)`. No `reason=zero-candidates` line was written, and GUI-CENSUS-SPAWN-CONTROL-NEEDS-A-CANDIDATE-HOST is closed |
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
`Timeline R greyed`, `Timeline FF countdown` and `Timeline Archived` rows each want a save that
differs from a committed one by a single deletion or flag. The harness stages a `saveTemplate`
VERBATIM, so today those need a committed fixture of their own; a per-lane staged-edit hook
would buy all three at once and is the cheaper answer if a fourth ever appears.

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
| `expand` (toggle a disclosure or caret by key) | all 21 Recordings-tab leaf-row cells with their phase colours, status words and tree connectors; chain and grouped blocks; STASH member rows; expanded per-vessel interval rows; the `Events (N)` digest and its `Go to`; the Logistics route and candidate detail panels (the largest un-photographed control cluster in the mod); the near-miss, dismissed and dormant disclosures; expanded Kerbals owner chains and folded outcome headers; the Career `Pending in timeline` fold |
| `target` (invoke a typed entry point rather than synthesising a click: `OpenStructureWindowForMission`, `OpenStructureWindowForRoute`, `ShowWipeRecordingsConfirmation`, `SetUiComplexityMode`-style setters for `showExpandedStats`, `TimeRangeFilterState`, `showCustomRange`, `sortColumn`, `foldedKerbals`) | the populated Structure window in both modes, the Info-expanded stats columns, the time-range filter indicator and every non-default Timeline preset, the custom sliders, every non-default sort direction, the folded Kerbals summary |
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
| Test Runner (Settings) | 320 x 600 (`UI/TestRunnerUI.cs:79-84`, re-derived 2026-09-15) | 440 x 600 | yes | yes, 600 of 720 | yes |
| Test Runner (global) | 320 x 600 (`InGameTests/TestRunnerShortcut.cs:83-91`, re-derived 2026-09-15) | 440 x 600 at a FIXED screen position (20, 60), not anchored to the main window (`:282`) | yes | yes | yes. GUI-12 commands it to 620x700 through `op=rect window=testrunnerglobal`, the same rect as its twin, so the two are comparable at a glance |
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

Gutter = 20. Reserving `fixedWidth` alone left every pinned header exactly 5 px wider than its
body (1 px of scrollbar margin plus the 4 px the MAX replaces), which is what the 2026-09-11
re-flights measured after PR #1679.

A header that reserves the gutter as a trailing `GUILayout.Space` instead owes the same 20
when its body's list-area box spends a `padding.right` of its own - the Recordings tab, whose
rows measure x=14 width 1311 inside a 1319-wide box. The Missions tab's box spends none (rows
x=10 width 1319), so that header owes the bare 16 footprint; it keeps its own reservation
under GUI-MISSIONS-WINDOW-MERGED-FIRST-HEADER-CELL, whose remaining offset is structural.
