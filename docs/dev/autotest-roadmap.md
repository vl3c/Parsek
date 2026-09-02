# Autotest roadmap: building up from the basics

Companion to `autotest-status.md`. That file owns STATUS (what is shipped, proven,
gated). This file owns FORWARD ORDER: what we still cannot reproduce, why, and in
what sequence to build it. It is deliberately a separate file so it can be rewritten
without touching the status doc that several lanes edit concurrently.

Written 2026-07-27 on branch `autotest-roadmap`. Every number below was measured in
this worktree at HEAD `1591aa59f` by the command named beside it. Nothing here was
run against a real KSP instance: no `run.py`, no `provision.py`, no launch.

SCOPE OF THAT LAST SENTENCE: it describes the 2026-07 source analyses that built
this file. It does NOT describe "The loop-render coverage program" (added
2026-08-20), which cites FLOWN run ids as evidence for its taxonomy and its gap
ranking; `autotest-status.md` still owns every verdict those runs produced.


> **RECONCILED 2026-07-27 after #1358 merged.** This document was written against
> `main` at 38 scenarios / 8 driven categories and flagged, in the tracking table
> above, that #1358 would shift its coverage and undriven-declaration counts. It has.
> Every derived number below is re-derived at 52 scenarios / 22 driven categories,
> NOT hand-adjusted: scenarios 38 -> 52, coverage 83 -> 96 of 241, Cause A
> 414 tests / 89 categories -> 338 / 75. The RANKING and the causes are unchanged -
> #1358 closed part of R4/R6/R8, it did not reorder anything. Where a section header
> carried an old number it now carries the new one, with the pre-merge figure kept
> inline so the delta stays legible.

## Baseline, and the lanes already in flight against it

Every count in this file was originally measured at `1591aa59f` and **excluded
work open in review at the time of writing**. All four PRs listed below have since
MERGED (by 2026-07-28), along with #1362 (R1 gates), #1363 (H20 tally pin), #1365
(R1-EMPTY-PROVISIONAL resolved as a fixture artifact), #1366 (CL-1 crew-loss atom)
and #1367 (R5 isolated batches). The sections above are re-derived at that state;
the table below is kept for history:

| PR | Branch | Overlaps |
|---|---|---|
| #1358 | `ingame-test-wiring` | **R4 and parts of R6 / R8.** Wires 14 in-game categories as batch-only specs H7-H20, including `IncompleteBallistic`, `FinalizeBackfill`, `RecordingFinalization` (all of R4 except `FinalizeLimbo` / `Bug289`), `TrajectoryMath`, `Pipeline-Anchor`, `SwitchSegment` (R6), and `SpawnRotation`, `EvaSpawnPosition` (R8). MERGED 2026-07-27; all 14 flown and PASS (805 s for the group). Moved the scenario count 38 -> 52, driven categories 8 -> 22, coverage 83 -> 96 of 241 (95 from this branch alone; the 96th is the D3 `parent-anchored-debris` cell R1's debris gate claimed on main, folded in at the merge), and Cause A 414 undriven declarations / 89 categories -> 338 / 75. Every figure in this doc is re-derived at the post-merge numbers. |
| #1357 | `rewind-loop-lane` | **R3.** Re-tiers S1.5 and S4.1 from `operator` to `nightly` on the same measured premise this file argues (the fixture routes to FLIGHT), and adds an R1 rewind-loop scenario. If it merges first, R3 collapses to "read the first nightly result". |
| #1359 | `eva4-failopen` | The EVA-4 mission-oracle fail-open listed as OPEN in the open-bugs table below. |
| #1360 | `fix-refly-provisional` | A re-fly defect found by the rewind lane; no roadmap item, but it moves D9's live standing. |

Consequence for whoever reads this next: **re-measure before acting on R3, R4, R6 or
R8.** The measuring commands are named beside every number, and
`hlib.compute_coverage` plus `hlib.parse_ingame_test_declarations` will re-derive the
whole table in seconds. Do not treat a merged H7-H20 category as still-undriven work.

---

## Where we are

### Infrastructure: shipped

M-A1 offline analyzer, M-A2 command seam, M-A3 autorun hooks, M-A5 harness core,
M-A6 stack provisioner, M-B1 mission library, M-B2 ledger oracle, M-C1 seam verbs
batch 1, M-C2 EVA verbs. Status and per-module proof live in `autotest-status.md`.
None of the items in this roadmap are blocked on a missing module.

### Scenarios: 68 committed

`ls harness/scenarios/*.toml` returns **68** files (re-derived 2026-08-04 at
`modded-compat-lane`; the count authority is `autotest-status.md`'s test-case
tables, gated by `AutotestStatusScenarioCountTests`). The history below narrates
the first waves and is kept as written: this baseline started at 38, #1358 took
it to 52 (14 in-game batch specs), #1357 added `R1-rewind-loop-flown` (53),
#1366 added `CL-1-pod-impact` (54, the crew-loss atom), and #1367 added
`H21-scene-exit-merge-isolated` (55, the first isolated-batch spec); the
EVA / CL / S0.x / H22-H25 / V1 / BDOCK waves and R14's MC-1/MC-2 took it from
55 to 68. Adding a scenario is not the same as covering a cell. Re-derive
these rather than editing them by memory; both numbers have moved many times.

### Coverage: 108 of 242 registry cells (was 83 of 241 at the baseline)

Re-derived 2026-08-04 at `modded-compat-lane` (commit `5439b2e1b`), replacing
the stale 97/145 snapshot - the intervening EVA / CL / rewind waves had already
moved D1, D9 and D14 without this section being re-run.
`hlib.compute_coverage(specs, [], registry)` over the 68 committed specs and
`harness/coverage/registry.toml` returns exactly:

```
values 242   covered 108   uncovered 134   expectedFailValues 0   xpass 0
```

Per dimension (total / uncovered):

| Dim | Subject | Total | Uncovered |
|---|---|---:|---:|
| D1 | recording lifecycle | 18 | 7 |
| D2 | sampling | 4 | 1 |
| D3 | reference frames | 7 | 4 |
| D4 | track sections / optimizer | 12 | 6 |
| D5 | tree topology | 12 | 7 |
| D6 | playback / ghosts | 16 | 11 |
| D7 | part events / FX | 16 | 11 |
| D8 | ledger / career | 18 | 6 |
| D9 | rewind / re-fly | 16 | 4 |
| D10 | logistics / routes | 20 | 12 |
| D11 | missions abstraction | 18 | 10 |
| D12 | crew | 10 | 8 |
| D13 | spawn positioning | 11 | 7 |
| D14 | bodies / scenes | 32 | 16 |
| D15 | timeline | 1 | 1 |
| D16 | storage / sidecars | 13 | 9 |
| D17 | mod compatibility | 6 | 4 |
| D18 | re-fly / interaction | 12 | 10 |
| | | **242** | **134** |

### The headline

At the baseline this read: 13 of 18 basic recording-lifecycle cells (D1) never
exercised, D13 spawn positioning 11 of 11 uncovered, D17 mod compatibility 6 of 6.
One wave later (measured 2026-07-28 at `7f5efa738`) D1 is down to **8 of 18** -
the R4-family batches, H21 and the R1 gates closed `commit-scene-exit`,
`switch-segment`, `scene-exit-finalization`, `ballistic-extrapolation` and
`finalization-cache` - and D13 is down to 7 of 11. D17 opened 2026-08-04: R14
provisioned `automation/modded-compat` and MC-1/MC-2 flew the WaterfallCompat /
ReStockCompat categories green there, closing `waterfall-swe-fallback` and
`restock` (plus D7 `engine-fx-waterfall-fallback`), leaving D17 at 4 of 6:
`persistent-rotation` + `remotetech-commnet` are source-blocked, and
`better-time-warp` / `making-history` have the instance but no committed spec.

The D1 cells still uncovered are ordinary player actions:

```
auto-record-first-mod-switch  manual-gloops        stop-on-switch
commit-revert-merge           commit-abort         auto-merge
sub-2-point-drop              switch-segment-noop-discard
```

(`stop-on-switch` is one of the two R2 phantom cells - it may leave this list by
deletion rather than coverage.)

D9 is worse than its 8-uncovered row suggests. Seven further D9 cells are "covered"
only by `S1.5-rewind-loop` and `S4.1-rewind-merge`, both `tier = "operator"`, both
excluded from every cadence, neither ever run. **15 of 16 D9 cells therefore have no
live proof at all.** The one that does is `reconciliation-bundle`, via
`H6-route-rewind-timeline`. Rewind-to-Separation / Re-Fly is the v0.9 headline
feature and its entire automated proof is nominal.

---

## What we cannot reproduce yet, grouped by cause

The single largest cause is not a missing capability. It is that we own roughly five
times the test surface we execute and have no way to point the harness at it.

### Cause A: written, never driven (232 tests, 64 categories; was 414 / 89 pre-#1358, 336 / 74 pre-R7, 280 / 70 pre-wave-2)

Measured with `hlib.parse_ingame_test_declarations` over every `.cs` under
`Source/Parsek`, re-derived 2026-08-05 after `wire-wave-2` (H26-H31):

```
543 [InGameTest] declarations in 98 categories, 0 unresolved
311 declarations in the 34 categories a committed spec drives
232 declarations in the 64 categories nothing drives
```

The in-driven-declarations metric, continued honestly across the waves:
125 (8 categories, pre-#1358) -> 216 (25, post-#1358+H21/H22/H23) -> 263 (28,
post-R7/H24/H25) -> **311 (34, post-wave-2)**. The wave-2 delta is the whole of
bucket B6 plus `CrewReservation`; per-category executed/skipped splits and the
two fixture substitutions the per-test read forced are in the status doc's
wave-2 section and the H26-H31 specs themselves. The paragraph and tables below
this point predate the R7 and wave-2 recounts and are kept for history - the
inventory doc is the current per-category authority.

The 24 driven categories are the pre-#1358 eight - `GameActionsHealth`, `GhostMap`,
`GhostPlayback`, `MapRender`, `Missions`, `Periodicity`, `RecordingInvariants`,
`RouteRewindTimeline` - plus the 14 that #1358 wired: `DataHealth`,
`EvaSpawnPosition`, `FinalizeBackfill`, `FlightIntegration`, `GhostVisuals`,
`IncompleteBallistic`, `KSP`, `Pipeline-Anchor`, `Pipeline-Smoothing`,
`RecordingFinalization`, `SpawnHealth`, `SpawnRotation`, `SwitchSegment`,
`TrajectoryMath` - plus two that arrived after #1358: `SceneExitMerge` (R5's isolated
`H21`) and `UiComplexityMode` (`H22`, which ships with the Basic/Advanced UI-mode
feature and is a category that did not exist pre-#1358). The 206 / 184 split is
because three of the pre-existing eight run at SPACECENTER where some members
scene-skip; all 79 declarations the later specs add execute. Per-category detail, and
the A/B/C triage of the 74 that remain, is in
`autotest-ingame-category-inventory.md`.

Scene reachability of the 74 undriven categories (a category is "reachable" when
every member runs in FLIGHT, SPACECENTER, or scene-agnostic, because `LoadGame` can
route to exactly those two scenes), re-derived post-#1358 and again after R5 moved
`SceneExitMerge` (FLIGHT-only) into the driven set:

| Scenes present in the category | Categories | Pre-#1358 |
|---|---:|---:|
| FLIGHT only | 36 | 53 |
| scene-agnostic only | 13 | 16 |
| FLIGHT + SPACECENTER (+ agnostic) | 10 | 4 |
| SPACECENTER only | 8 | 9 |
| involves TRACKSTATION or MAINMENU | 7 | 7 |

**67 undriven categories are reachable today on fixtures we already own** (was 82).
Seven are not, because there is no seam route to TRACKSTATION or MAINMENU - and that
seven is UNCHANGED by #1358, which is the point: it wired only reachable categories,
so the unreachable set is exactly as hard as it was. `TrackingStation` alone is 9
batch-eligible tests stranded behind one missing seam verb.

The D1-D9 blocks that are batch-allowed and reachable RIGHT NOW, with no code change
and no new fixture. Struck-through rows have been CLOSED since - by #1358, then by
wave 2, then by the career-ledger lane; each strike names the spec that closed it -
and are kept so the remaining work is legible against the original list:

| Category | Tests | Dimension cells it reaches |
|---|---:|---|
| ~~`IncompleteBallistic`~~ | 8 | DRIVEN since #1358 by `H9` (live-proven 2026-07-27). D1 `ballistic-extrapolation`, `scene-exit-finalization` |
| ~~`FinalizeBackfill`~~ | 7 | DRIVEN since #1358 by `H10` (live-proven 2026-07-27). D1 `scene-exit-finalization` |
| ~~`RecordingFinalization`~~ | 3 | DRIVEN since #1358 by `H19` (live-proven 2026-07-27). D1 `finalization-cache` |
| `FinalizeLimbo` | 2 | D1 finalization |
| `Bug289` | 2 | D1 finalization |
| ~~`Pipeline-Anchor`~~ | 7 | DRIVEN since #1358 by `H11` (live-proven 2026-07-27). D3 `relative-anchored-nonloop`, `relative-loop`, `boundary-seam` |
| ~~`TrajectoryMath`~~ | 8 | DRIVEN since #1358 by `H7` (live-proven 2026-07-27). D2 `threshold-debounce` |
| `Optimizer` | 2 | D4 `env-body-split`, `surface-graze-suppression` |
| `BackgroundSeeder` | 2 | D4 `seed-event-split` |
| `Recording` | 1 | D5 `bg-on-rails` |
| ~~`SwitchSegment`~~ | 6 | DRIVEN since #1358 by `H12` (live-proven 2026-07-27). D1 `switch-segment` gate layer |
| `SwitchIntentPatch` | 3 | D1 switch-intent arming (partly TRACKSTATION) |
| `Rewind` | 31 of 37 | D9 `seal-stash-fly`, `unfinished-flights-stash`, `rp-disk-reaper`, `revert-during-refly-dialog`, `tombstones`, `merge-journal`, `terminal-kind-classify`, `read-back-guard` |
| `GhostLifecycle` | 15 of 17 | D6 `loop-period-modes`, `self-overlap`, `overlap-expiry-soft-caps` (the other 2 are `Scene = TRACKSTATION`, so this is one of the 7 partly-stranded categories) |
| ~~`GhostAudio`~~ | 9 | DRIVEN since wave-2 by `H30` (live-proven 2026-08-04; needed the W2-SHIP-VOLUME-ZERO provision fix). D6 `ghost-audio` |
| ~~`MapPresence`~~ | 5 | DRIVEN since wave-2 by `H28` (live-proven 2026-08-04). D6 `ghost-map-presence`; `commnet-relay` NOT closed - its only cell is vacuous under every committed asset (no generator writes AntennaSpecs; see W2-VACUOUS-CELLS) |
| `ReentryFx` | 3 | D6 `reentry-fx` |
| `Watch` | 2 | D6 `watch-mode-retarget-explosion-hold` |
| ~~`LedgerGroundTruth`~~ | 2 | DRIVEN since the career-ledger lane by `L2-ledger-groundtruth-career` (live-proven 2026-08-17, armed run `2026-08-17_2233`). D8 `ground-truth-harness`. The count was 1 here and is 2: `KerbalExperienceReassertTest.SurvivingCareerLogEntriesAreOnTheLiveRoster` (P9a) also declares this category |
| `Contracts` | 2 | D8 `contracts` |
| ~~`StrategyLifecycle`~~ | 9 | DRIVEN since the career-ledger lane by `L3-strategy-currency-conversion` (live-proven 2026-08-18, green runs `2026-08-18_2039` and, for the capture matrix, `2026-08-18_2140`), and from 2026-08-20 by the complementary `L3-strategy-exchanger-floor` at the opposite reputation. Took 7 -> 9 on the `rep-debit-capture` wave: a reputation-INPUT converter measurement/gate cell and the high-reputation curve adjudicator (green runs `2026-08-20_2115` and `2026-08-20_2117`). D8 `strategies` |
| `SpawnRotation` + 7 more | 29 | D13, all 11 cells |
| ~~`CrewReservation`~~ | 15 | DRIVEN since wave-2 by `H31` on b2-lko-craft (live-proven 2026-08-04, 14 of 15 executing). D12 `seat-matching`, `rescue-marker`; the rest of D12 has no producer in the category |

### Cause B: unreachable by ANY unattended path (68 tests) - CLOSED by R5, 2026-07-27

CLOSED as a CAPABILITY gap. The seam's `RunTests` verb now takes
`isolated = "true"` and the autorun dispatcher reads `PARSEK_AUTORUN_ISOLATED=1`,
both routing to the `*IncludingFlightRestore` entry points, so all 68 are drivable.
One of the 13 categories is actually DRIVEN so far (`SceneExitMerge`, wired as
`H21-scene-exit-merge-isolated`); the other 12 are now ordinary spec-authoring work
under R6 / R7 / R10 rather than blocked. The diagnosis below is kept verbatim
because it is the evidence the fix rests on.

`InGameTestRunner` has two batch entry points. The ordinary one admits
`test.AllowBatchExecution`. The other, `PrepareBatchExecutionIncludingFlightRestore`,
also admits `test.RestoreBatchFlightBaselineAfterExecution` and restores a flight
baseline afterwards. `RunAllIncludingFlightRestore` and
`RunCategoryIncludingFlightRestore` are public and fully implemented
(`InGameTestRunner.cs:389,420`).

They are called from exactly two places, both interactive:
`TestRunnerShortcut.cs:395,463` (the Ctrl+Shift+T window) and
`UI/TestRunnerUI.cs:249,375`.

They are called from neither unattended path. The seam's `RunTests`
(`ParsekTestCommandAddon.cs:1494,1496`) calls `RunAll()` / `RunCategory(category)`.
The autorun dispatcher (`TestRunnerShortcut.cs:725,739,789`) does the same.
(Both now branch on the R5 flag; the line numbers above are pre-R5.)

Counting attribute argument lists over all 542 declarations (see the note on the
fully-qualified attribute form at the end of this file - a naive `[InGameTest(` scan
misses 5 of them):

```
68 tests carry AllowBatchExecution = false AND RestoreBatchFlightBaselineAfterExecution = true
 4 carry restore = true only     (Contracts 2, TestCommands 1, LedgerGroundTruth 1)
 3 carry AllowBatchExecution = false only  (Periodicity)
```

Those 68 are tests whose authors already decided a quickload-baseline restore makes
them batch-safe. No unattended path can run them. By category:

```
Logistics 38   AutoRecord 10   Rewind 6   Coalescer 2   MergeDialog 2
QuickloadResume 2   SceneExitMerge 2   LogisticsGrapple 1   MapRender 1
TrackingStation 1   GhostPlayback 1   RevertFlow 1   PlaybackControl 1
```

Twenty-six of them sit in D1-D9 categories nothing drives (AutoRecord 10, Rewind 6,
Coalescer 2, MergeDialog 2, QuickloadResume 2, SceneExitMerge 2, RevertFlow 1,
PlaybackControl 1). Their `BatchSkipReason` strings say what they do, and it is
exactly the basics: `SceneExitMerge` starts a real recording, launches the active
vessel, and exits FLIGHT through stock save-and-exit; `RevertFlow` drives stock
Revert to Launch; `Coalescer` stages the active vessel to assert the parent-anchor
contract on a controlled-decoupled child; `AutoRecord` simulates an idle post-switch
watch on a landed vessel and nudges it.

This is the reason D1 `auto-record-first-mod-switch`, `commit-scene-exit`,
`commit-revert-merge`, D5 `controlled-decoupled-child` / `crash-coalescing`, and D9
`rewind-to-launch` cannot be closed by any amount of spec authoring. **No fixture, no
mission profile, and no existing verb produces them.**

### Cause C: missing seam capability

`TestCommandVerbs.cs` declares 19 implemented verbs and 11 reserved. The reserved set
maps almost one to one onto the largest uncovered dimensions:

| Reserved verb | Gates |
|---|---|
| `StartLoopPlayback` / `StopPlayback` / `EnterWatchMode` | D6 playback, D18 chains |
| `SealSlot` / `StashSlot` / `FlySlot` | D9 `unfinished-flights-stash`, `seal-stash-fly` |
| `RouteCommand` | D10 (12 uncovered) |
| `MissionConfig` | D11 loop behaviour (10 uncovered) |
| `SimulateStockSwitchClick` | D1 `switch-segment` / `switch-segment-noop-discard`, D5 `chain-continuation-switch`, D18 `committed-interaction-claiming` |
| `CrashAfterJournalPhase` | D9 `merge-journal`, `load-time-sweep` |
| `RunInvariantReport` | analyzer-in-scene |

Two further capability gaps, both verified:

**No vessel switch exists anywhere in the stack.** Parsek's switch-segment path is
armed only by stock click handlers writing a `StockActionIntentMarker`
(`Patches/MapFocusObjectOnSelectPatch.cs`, `Patches/GhostTrackingStationPatch.cs`,
`Patches/KscVesselMarkerFlyPatch.cs`). A kRPC `active_vessel` set calls
`FlightGlobals.SetActiveVessel` directly and arms nothing, so the machinery would be
bypassed silently. There is also no set-active-vessel action in the mission library's
action vocabulary. `SimulateStockSwitchClick` is not substitutable.

**No runtime-to-spec data path.** `run.py:1157` substitutes exactly one token,
`${runSave}`. Response payloads are read for the verdict only; no payload field is
ever captured into a variable a later step can reference. So every verb that
addresses a live object is unreachable unless the id is statically bakeable.
`InvokeRewind` matches `candidate.RewindPointId == rpArg` exactly, live ids are fresh
GUIDs, and `RecordingState`'s payload carries no RewindPoint list. S1.5 and S4.1 work
only because `rp_b9_root` is a baked id from the C# `RewindB9Fixture` generator. The
same shape blocks feeding a live recording id, vessel pid, route id, or mission id to
any verb.

**Scene entry is two-valued.** `TestCommandLoadGame.DecideLoadRoute` returns
`Focusable` (boot to FLIGHT), `NoVesselSpaceCenter` (boot to SPACECENTER), or
`Failed`. There is no route to TRACKSTATION, MAP, or EDITOR and no other scene-entry
verb. That directly explains D14 `scene-ts` / `scene-map` / `scene-editor` being
uncovered, and strands the 7 undriven categories with TRACKSTATION or MAINMENU
members (including `TrackingStation`, 10 tests).

### Cause D: missing fixture

Fixtures are largely NOT the bottleneck. Eleven fixture directories exist under
`harness/fixtures/saves/`, every forge has run, and no spec is fixture-blocked.

One real gap: **every flyable fixture is SANDBOX.** Counting `VESSEL` nodes in each
fixture's `persistent.sfs`:

| Fixture | Mode | VESSEL nodes |
|---|---|---:|
| gloops-airshow | SANDBOX | 2 |
| b1-pad-craft | SANDBOX | 2 |
| b2-lko-craft | SANDBOX | 2 |
| bdock-forge-base | SANDBOX | 2 |
| bdock-station-pad | SANDBOX | 2 |
| eva3-pad-3crew | SANDBOX | 2 |
| eva2-lko-crewed | SANDBOX | 7 |
| bdock-station-craft | SANDBOX | 0 (orphan, no spec LOADS it) |
| fresh-career | CAREER | **0** |
| fresh-science | SCIENCE_SANDBOX | **0** |
| fresh-sandbox | SANDBOX | 0 |

There is no CAREER save with a flyable craft anywhere in the repo. That one fact
blocks the L-track end goal (grand oracle career runs with repeated rewinds), D8
`milestones` / `contracts` / `strategies` / `tombstones` in their flown form, D12
`reservation-auto-hire` and `tombstone-rep-penalty`, and D9 `tombstones`. The forge
machinery to fix it exists and is live-proven (`forge_station.py`, `forge_lko.py`,
`harvest_bdock_station.py`); a `FORGE-career-pad` is a mechanical repeat of
`FORGE-eva3-pad`.

Second gap, smaller: injection is capped at three presets.
`hlib.INJECTED_RECORDINGS = ("none", "all-synthetic", "rewind-b9")`, with a comment
deferring broad preset/corpus-scoped injection to M-A4 / M-B5. Meanwhile
`Source/Parsek.Tests/Generators/` already holds `RouteFixtureBuilder`,
`DebrisFrameContractRecordingFixture`, `CrpFixtures` and `RecordingStorageFixtures`,
which are generators for exactly the D3/D5/D10/D16 shapes that are uncovered, with no
injection entrypoint wired to the harness.

Third gap: the modded instance has never been built. `automation/` contains only
`stock-minimal`, though `harness/provision/profiles/modded-compat.toml` is authored
and `WaterfallCompat` (8 tests) + `ReStockCompat` (9 tests) exist and self-skip on
stock. That is the whole of D17's blockage for 3 to 4 of its 6 cells.

### Cause E: missing harness machinery

**One category per spec.** `hlib.SINGLE_BATCH_SELECTOR_RULE` (`hlib.py:691`, enforced
at `hlib.py:2158-2190`) requires that a batch-owning spec drive exactly ONE
`RunTests` step naming exactly ONE category. More than one step is an error; a
multi-category selector (`"all"` or `"A,B"`) is an error. The rule applies to
`driver.autorun.tests` as well, not just `driver.steps`. The stated reason is honest
and narrow: the gating line for a multi-category run is the `category=multi:<n>`
aggregate, whose tally sums the constituents, so "category B executed nothing" is not
expressible on the current contract surface. There is a deliberate opt-out
(`expectations.logContracts.batchVacuityOptOut` plus a required reason) but taking it
throws away the anti-vacuity guarantee.

Consequence: every category costs its own KSP boot. That is affordable at the scale
of this roadmap (see COST) but it is the tax that sets the long-tail ceiling.

**No structural save-content assertion.** The only assertion any spec can make about
the produced recordings is `recordings.count`, a min/max integer window, and it is
documented COMMIT-BLIND. There is no way to assert "2 EVA branch points and 2 Board
branch points", "this TrackSection is Relative anchored to Y", or "this recording's
terminal is Landed on Mun". Everything structural is proxied through grep.

**Three expectation verifier families are declared and inert.**
`hlib.RESERVED_EXPECTATION_BLOCKS = ("route", "rewind", "loop")` are parsed, recorded
SKIPPED, and never evaluated. `S4.1-rewind-merge.toml` declares
`supersedeRows = { min = 1 }` and `tombstones = { max = 0 }` under
`[expectations.rewind]`; those have never been and currently cannot be checked.

**Multi-vessel choreography is bespoke.** BDOCK-1 works (live-proven over an
18-flight campaign) but its 18 phases are hand-written for one Station and one
Interceptor. A third vessel, a second dock, or a different pairing means a new
hand-written machine. There is no generic N-vessel layer, no claw/grapple action, no
crew-transfer action, and no inventory-part action in the mission action vocabulary.

### Cause F: unwritten, or not yet defined

- D1 `manual-gloops`: the Gloops Flight Recorder path (`FlightRecorder.IsGloopsMode`,
  `UI/GloopsRecorderUI.cs`). No seam verb, no in-game test category, nothing. The
  `gloops-airshow` fixture is named for it and nothing exercises it.
- D1 `commit-abort`: no production symbol identified. Needs a definition before it
  needs a test.
- D1 `stop-on-switch`: **registry defect.** `FlightRecorder.VesselSwitchDecision` is
  `{None, ContinueOnEva, ChainToVessel, DockMerge, UndockSwitch,
  TransitionToBackground, PromoteFromBackground}`. There is no Stop member; always-tree
  mode removed it. The cell as named cannot be honestly claimed. Either delete it or
  redefine it against `TransitionToBackground` and say so in the registry comment.
- D3 `surface-body-fixed`: **registry defect.** `ReferenceFrame` has exactly three
  members (`Absolute`, `Relative`, `OrbitalCheckpoint`). The only production symbol
  carrying "body-fixed" meaning is `TrackSection.bodyFixedFrames`, documented in
  source as "For Relative only: body-fixed world-coordinate primary surface", which
  makes it a sub-surface of Relative sections and therefore overlapping with
  `parent-anchored-debris`. Resolve before anyone claims it, or the claim is
  unfalsifiable.
- No in-game test found for: D6 `zone-transitions`, D4 `tail-trim`, D5
  `staging-debris-promotion`, D9 `load-time-sweep`, D2 `density-presets`. Each needs a
  new in-game test written against an existing seam.
- D5 `dock-merge-same-tree` needs a two-port single-launch craft (new fixture + new
  mission). D7 `inventory-place-remove` needs an inventory-carrying craft plus an EVA
  construction action. D8 `milestones` needs a career flight that earns one.
- D17 `persistent-rotation` has no source in the profile (GT-8 open) and
  `remotetech-commnet` is not in the profile at all. Two of six D17 cells are
  source-blocked, not capability-blocked.

---

## Build order

Ranked by dependency, not by ease. The rule applied: anything a live-proven scenario
already depends on comes before anything new; anything that unlocks a CLASS of cells
comes before the cells themselves; anything gating a shipped headline feature comes
before breadth.

"Flight?" means: does a real flown mission have to happen, or is a seam / in-game
batch / injected-corpus boot enough.

### Tier 0: free or nearly free. Do these first.

**R1. Close the gates on what we already fly.** Spec-only. No flights. No code.
**SHIPPED 2026-07-27 (PR #1362)**: the five Kerbal X debris-population tokens are
gated, so D3 `parent-anchored-debris` and its siblings now assert instead of
narrate.

Not because it is cheap, though it is, but because each of these is a surface a
live-proven scenario produces on every nightly with no assertion. `B2-lko-ascent`
sheds six radial boosters, each becoming a parent-anchored debris child recording,
its own spec comment says so, and its window is `count = { min = 1, max = 8 }`: a
total loss of the debris population still PASSES. The parent-anchored contract is one
of the most intricate in the codebase and nothing asserts it was produced.

This is SYSTEMIC, not a B2 quirk. Measured over the committed specs 2026-07-26,
SEVEN live-proven scenarios carry a recording-count window whose lower bound
admits the main recording alone:

| Scenario | window | span | status |
|---|---|---|---|
| B1-pad-hop | {1, 6} | 5 | OPEN - not a Kerbal X; breakup-child count is documented as genuinely per-run variable, so it needs its own measurement |
| B2-lko-ascent | {1, 8} | 7 | CLOSED -> `{7, 8}` + debris token (b2_decide: no flameout stage, so population 7; MEASURED 7 on `2026-07-25_0824`) |
| B4-reentry-splashdown | {1, 9} | 8 | CLOSED -> `{8, 9}` + debris token (b4_decide has no flameout stage, but commands a service-stage drop on the SOLE path into B4_REENTRY; MEASURED 8 on `2026-07-25_0828`, confirming the structural derivation) |
| B5-mun-flyby | {1, 9} | 8 | CLOSED -> `{8, 9}` + debris token (b5_decide reaches `_b5_flameout_stage`; MEASURED 8 on `2026-07-25_0643` and `_0847`) |
| B6-minmus-flyby | {1, 9} | 8 | CLOSED -> `{8, 9}` + debris token (same `b5_decide` as B5/B7; MEASURED 8 on `2026-07-25_0636` and `_0856`, confirming the inference) |
| B7-duna-flyby | {1, 8} | 7 | CLOSED -> `{8, 8}` + debris token (MEASURED 8 on `2026-07-25_0916_a2`; agrees with B15's pin) |
| BDOCK-1-station-interceptor | {2, 20} | 18 | OPEN - window spans TWO trees and is commented "never tightened"; wants its own measurement |

Every one of them would still read PASS if Parsek stopped writing child /
debris recordings entirely. The wide MAX is defensible and deliberately
reasoned - B2's own comment sources it to real staging-timing variance under
MechJeb autostage, and widening beats redding on nondeterminism. The wide MIN
is the defect: it turns a population contract into "the main recording exists".

The fix is NOT simply to tighten the windows, which would re-introduce the
nondeterminism the width was chosen to absorb. It is to assert the POPULATION
separately from the COUNT - the log tokens below are deterministic even when the
sidecar tally is not - and to tighten `min` only as far as the deterministic
part of each flight supports. B11/B12 show a tight `{8, 8}` pin is reachable
once a flight has been measured, so the tight-pin pattern already exists in the
committed set; these seven predate it.

Tokens verified present in source:

| Token | Site | Closes |
|---|---|---|
| `Child recording created (debris, TTL=...)` | `BackgroundRecorder.cs:1177` | D3 `parent-anchored-debris`, D5 `staging-debris-ttl` |
| `Debris TTL expired, ending recording:` | `BackgroundRecorder.cs:1307` | D5 `staging-debris-ttl` |
| `Child recording created (controlled, no TTL):` | `BackgroundRecorder.cs:1185` | D5 `controlled-decoupled-child` (needs a controlled child; Kerbal X boosters are uncontrolled) |
| `Sample rate changed: pid=` | `BackgroundRecorder.cs:1966` | D2 `proximity-cadence-bg` |
| `TrackSection started: env=... ref=...` | `FlightRecorder.cs:5126`, `BackgroundRecorder.cs:6573` | D3 `absolute` |
| `starting hysteresis timer` | `FlightRecorder.cs:4831,4911` | D4 `hysteresis` |
| `Part event: <Type> '<part>` | `FlightRecorder.cs:1507`, `BackgroundRecorder.PartEventPolling.cs` | D7 `decouple-stage-destroy`, `chute-cut`, `gear` |

CORRECTED 2026-07-27 while building this: **the four `BackgroundRecorder` tokens are
`ParsekLog.Info`, not Verbose.** The rest of the table is mixed - `starting hysteresis
timer` is Verbose at BOTH sites (`FlightRecorder.cs:4831`, `:4911`), and the
`Part event:` family is ~39 Verbose sites plus exactly one Info at
`FlightRecorder.cs:3862` (20 in `FlightRecorder.cs`, 19 more in
`BackgroundRecorder.PartEventPolling.cs`),
so check the level per token rather than per family. That matters
because it decides whether a spec needs a `SetSetting verboseLogging true` step to
depend on a token: the debris / TTL / sample-rate claims do NOT, and the five specs
gated below deliberately declare none. Part events log as
`Part event: {eventType} '{partName}'` with `eventType` from the `PartEventType` enum,
so `Decoupled`, `Destroyed`, `ParachuteCut`, `GearDeployed`, `FairingJettisoned` and
the rest are all producible forms; those ARE Verbose, `ParsekSettings.verboseLogging`
defaults `true` (`ParsekSettings.cs:50`), and `B1-pad-hop` pins two of them
(`Part event: ParachuteSemiDeployed 'parachuteSingle`), so that pattern is proven too.

**Every one of these tokens is a REGEX**, applied with `re.search` by
`evaluate_expectations`. `Child recording created (debris, TTL=` pasted verbatim from
the source raises `re.error: missing ), unterminated subpattern`; write
`Child recording created \(debris, TTL=`. `hlib.validate_spec` rejects an
uncompilable pattern since 2026-07-27, and `run.py --dry-run` now runs that
validation (it previously returned 0 before reaching it), so this costs a dry-run
rather than a
flight.

SHIPPED 2026-07-27 (debris population): `B2-lko-ascent`, `B4-reentry-splashdown`,
`B5-mun-flyby`, `B6-minmus-flyby`, `B7-duna-flyby` - each requires a debris-creation
token, pins a `count.min` derived per mission (see the table), and claims D3
`parent-anchored-debris`. Coverage 83 -> 84.

**READ THIS BEFORE PINNING ANY TOKEN FROM THIS TABLE.** The first cut of that gate
pinned `Child recording created \(debris, TTL=` alone and would have RED all five
flights. That token is the BACKGROUND-split site
(`BackgroundRecorder.RegisterChildRecordingsFromSplit`), reachable only through
`OnBackgroundPartJointBreak`, which early-returns unless the vessel is in
`tree.BackgroundMap` - and `RecordingTree.IsBackgroundMapEligible` excludes
`rec.RecordingId == ActiveRecordingId`. A Kerbal X sheds its boosters while it IS the
active vessel, so the line cannot fire on these profiles. Staging goes through
`ParsekFlight.ProcessBreakupEvent` -> `CreateBreakupChildRecording`, logging
`ProcessBreakupEvent: debris child created: pid=` (Info, tag `Coalescer`).
The lesson is general and cost two independent reviews to catch:
**a token's presence in source is not its reachability on a profile.** Trace the call
chain, or grep an archived KSP.log, before pinning - the discipline this section
already prescribed for the tokens it declined to claim, and did not apply to the one
it claimed.

The intermediate fix accepted EITHER site, because that trace was still argued from
source alone. **The shipped gate requires the FOREGROUND token only**, settled
2026-07-27 against all 60 archived B-lane `logs/*/KSP.log` folders (B2 10, B4 8,
B5 27, B6 6, B7 9): the foreground token appears in 58 of 60, and the substring
`Child recording created` - broad enough to catch the CONTROLLED sibling at `:1185`
too - appears in **zero**. The two without it are `2026-07-20_{1846,1854}_B2-lko-ascent`,
INVALID runs that recorded nothing. A green ascent emits it exactly 6 times, one per
booster. Corollary worth keeping: an EITHER-site alternation is a reasonable
intermediate when the trace is unconfirmed, but it is not the destination - a gate
that accepts two paths cannot tell you which one broke.

Also corrected while shipping: `min` is NOT one number across the five, and the rule
is not "does it reach `_b5_flameout_stage`" either - that was the second draft's error.
The floor follows **whether the mission commands a debris-producing stage drop beyond
launch ignition**: `B5`/`B6`/`B7` drop a flameout-staged core via `_b5_flameout_stage`,
and `B4` drops its service stage via an `ACTION_ACTIVATE_STAGE` on the SOLE transition
into `B4_REENTRY` - so all four floor at 8. Only `B2` stages once, at ignition, and
floors at 7 (`mlib.py:401-405`: the spent core never autostages because MechJeb
autostage fires only on EMPTY stages and the Kerbal X core keeps residual fuel).
The first cut used 7 everywhere, which on an 8-population spec is precisely the value
`B11`/`B12` record as **considered and rejected** ("7 is the exact count a single
dropped recording would produce"). Note `run.py` judges expectations only on a
driver-valid, non-short-circuited run, so a B4 that never reached REENTRY has its count
SKIPPED rather than passing under the lower floor.

**All five floors were converted from derived to MEASURED on 2026-07-27**, read off
`verifiers.expectations.observed.recordings.count` in verdict=PASS result JSONs (see
the status table for the run ids; the field landed in `72cf344fb`, 2026-07-25 06:48,
so every citation is from that morning). Both floors that had never been measured -
B4's structural derivation and B6's inference from the shared decide function -
measured at exactly the derived value, so no re-pin was needed.
**Do not measure this from the archived `logs/*/` folders.** `run.py` collects logs on
NON-PASS only (`run.py:2324`), so every archived B-lane folder is a run whose
expectations were SKIPPED rather than judged. Their `.prec` sidecars number 7 for B4
and B6 - those runs aborted before the extra stage drop - and reading that as a
contradiction would lower both floors straight back into the one-below-population
blind spot. The archives are ground truth for TOKENS, not for COUNTS.

The Targets line this section originally carried was wrong and is replaced by the
status column in the table above: it named `B11`/`B12`/`B13`/`B14`, which already had
`{8,8}` pins and eight-token contracts (B11 even requires `terminalState=Destroyed`,
which gates debris terminals), and omitted `B5`/`B6`/`B7`, which were vacuous.

STILL OPEN: D5 `staging-debris-ttl` and D2 `proximity-cadence-bg`. Their tokens exist
and are Info, but neither is structurally guaranteed the way creation is -
`DebrisTTLSeconds = 60.0` makes TTL expiry likely, not certain, since a booster
destroyed on reentry inside that window ends its recording by another reason. Claiming
on "likely" is what this section's own rule forbids. Close them by grepping an
archived B-lane KSP.log for both tokens first.
Rule: one token per claimed class; never loosen a token to keep a claim.

**R2. Resolve the two registry defects.** Registry-only. **STILL OPEN** -
re-verified 2026-07-28 at `7f5efa738`: both `stop-on-switch` and
`surface-body-fixed` are still in `registry.toml`, so the 242 denominator still
carries two unclaimable cells.

D1 `stop-on-switch` and D3 `surface-body-fixed` cannot be honestly claimed as
written (see Cause F). Both R1 and everything after it writes claims against the
registry, and the denominator moves, so decide before the next coverage snapshot
rather than retracting claims later. Cost: one edit to
`harness/coverage/registry.toml` comments and values.

**R3. Run S1.5 and S1.4's sibling S4.1 unattended. Two boots.**
**PARTLY OVERTAKEN**: #1357 re-tiered both to `nightly` on exactly this premise, and
the 2026-07-28 fixture-corrected R1 run (`2026-07-28_1509`, PASS) resolved
R1-EMPTY-PROVISIONAL as a fixture artifact, after which S4.1's `expectedFail` keys
were removed. What this item still means: confirm each has a green run on its OWN
row - a nightly-tier assignment is scheduling, not proof.

Both are `tier = "operator"` on the rationale, quoted from
`S1.5-rewind-loop.toml:3-8` and `S4.1-rewind-merge.toml:3-9`, that the verbs are
`RequiresFlight` and "run unattended from the gloops SPACECENTER host every verb only
DEFERS to a TIMEOUT". That premise is contradicted by measurement:

- Both use `saveTemplate = "fixtures/saves/gloops-airshow"`.
- gloops-airshow carries `activeVessel = 1` and 2 VESSEL nodes, so
  `TestCommandLoadGame.IsLoadedGameFocusable` is true and `DecideLoadRoute` returns
  `Focusable`, which boots to FLIGHT.
- Two live-proven specs on that exact template pin the measured scene, and both
  measured FLIGHT: `S1.4-injected-playback` pins
  `category=GhostPlayback scene=FLIGHT` and `H5-invariants-corpus` pins
  `category=RecordingInvariants scene=FLIGHT`.
- S0.5 / S0.6 / EVA-1 drive `RequiresFlight` verbs to green PASSes on it.
- S1.5's other stated blocker, a TimeJump completion-decider fix on branch
  `autotest-integration-fixes`, MERGED as PR #1322 (commit `eb94607dd`).

If the premise is stale, 7 nominal-only D9 cells become real, D6 `time-jump` and D8
`epoch-isolation` come with them, and 15-of-16-unproven D9 becomes a defended
dimension for the price of two boots. If it fails, the failure names the real
blocker, which nobody currently has. Either outcome beats more spec authoring.
Do NOT relax the specs' asserts to force a pass.
Residual risk to check first: `RewindInvoker.CanInvoke`'s five preconditions include
a deep parse of the RP quicksave, and `rewind-b9` writes that quicksave
synthetically; that path has never executed live.
Flight? No. Two seam boots.

**R4. Drive the D1 finalization family. Five specs, five boots, no code.**
**MOSTLY SHIPPED**: #1358 wired and flew `IncompleteBallistic` (H9),
`FinalizeBackfill` (H10) and `RecordingFinalization` (H19), and #1367's H21 covers
the scene-exit merge path via the isolated batch. Residual: `FinalizeLimbo` (2) and
`Bug289` (2) are still undriven.

`IncompleteBallistic` (8), `FinalizeBackfill` (7), `RecordingFinalization` (3),
`FinalizeLimbo` (2), `Bug289` (2). All 22 are FLIGHT-scene and all are
batch-allowed today. Template: `harness/scenarios/H5-invariants-corpus.toml`.
Closes D1 `scene-exit-finalization`, `ballistic-extrapolation`, `finalization-cache`.
Independent of R3 and R5; can be built in parallel with both.
Mandatory: pin the WHOLE `BATCH_COMPLETE` tally, not `passed=[1-9][0-9]*`. These
categories self-skip on fixture conditions and a `failed=0` pin over an all-skipped
batch is exactly the vacuity defect that was already found and closed once.
Every such spec must carry the sentence the precedent established: it gates the
DECISION layer in a live KSP process, not a flown situation.
Flight? No.

### Tier 1: the unlock. This is the largest single gain in the roadmap.

**R5. Ship the isolated-batch seam argument.** SHIPPED and LIVE-PROVEN 2026-07-27.
Code change, seam + autorun + hlib. The shakedown `H21-scene-exit-merge-isolated`
flew PASS on attempt 1 in 101 s, printing
`BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 category=SceneExitMerge
scene=FLIGHT` token for token. Coverage 96 -> 97 covered; the one new cell is D1
`commit-scene-exit`, which this file listed among the three that no fixture, mission
profile or verb could produce.

`RunTests` gains an `isolated` argument routing to `RunCategoryIncludingFlightRestore`
instead of `RunCategory`; mirror it in the autorun selector
(`TestRunnerShortcut.cs:739,789`); add the hlib spec-validation companion; land one
shakedown spec.

What shipped, and the three places this section was wrong:

- The autorun mirror is a SEPARATE env var, `PARSEK_AUTORUN_ISOLATED=1`, not a
  selector prefix. The selector string is consumed verbatim as the `category=` token
  both the runner stamps and `hlib._batch_probe_categories` synthesizes its
  anti-vacuity probe family from; a prefix would desynchronize those two copies of
  one name, every probe would miss on a token mismatch, and the gate would read a
  contract that rejects all probes as SAFE. Full argument in
  `design-autotest-autorun-hooks.md` "H1 - Isolated batches".
- **CORRECTION: the non-isolated form does NOT yield `total=0`.** The proof
  paragraph below and in `todo-and-known-bugs.md` both said it would. It yields
  `total=2 passed=0 failed=0 skipped=2`: `PrepareBatchExecution` sets
  `Status = Skipped` on the tests it filters out rather than dropping them, and
  `BATCH_COMPLETE`'s `total` is `allTests.Count(Status != NotRun)`, so filtered
  tests are counted. `total` is therefore IDENTICAL on both paths and cannot be the
  discriminator; the proof is the passed/skipped split. This turns out to make the
  proof stronger rather than weaker: `passed=0, failed=0, total==skipped` is exactly
  the one-parameter vacuity family the anti-vacuity gate enumerates, so the gate
  ALREADY guarantees an isolated spec's pin rejects the non-isolated line. It is no
  longer possible to read `H21` as green without the isolated route running.
- **The hlib companion is load-bearing, not optional.** `InGameTestDecl` did not
  carry `RestoreBatchFlightBaselineAfterExecution` at all and `derive_batch_tally`
  hardcoded the ordinary filter, so `CommittedBatchTallySourceSyncTests` would have
  REJECTED a correct isolated pin (deriving `executable = 0`). The field, an
  `isolated=` mode on the derivation, and `spec_batch_isolated` all had to land with
  the seam change.
- **The fixture is the expensive trap, and it is not in this section at all.** The
  shakedown spec loads `b2-lko-craft`, NOT the H-series `gloops-airshow`. Both
  `SceneExitMerge` tests stage the active vessel and wait for it to leave PRELAUNCH
  and clear 80 m; `gloops-airshow`'s active vessel is a 1-part `mk1-capsule` with
  ZERO `ModuleEngines`, so on it both self-skip and print
  `total=2 passed=0 failed=0 skipped=2` - the same line the non-isolated failure
  produces. Copying the H-series fixture would have produced a red indistinguishable
  from "the arg does not work". `IsolatedBatchWiringGroupTests` now gates the
  PRELAUNCH + non-zero-engine property statically.

This turns 68 already-written tests from unreachable into drivable, including the 26
D1-D9 tests no other mechanism can produce: `AutoRecord` (10), `Rewind` (6),
`Coalescer` (2), `MergeDialog` (2), `QuickloadResume` (2), `SceneExitMerge` (2),
`RevertFlow` (1), `PlaybackControl` (1). It also unlocks 39 for D10
(`Logistics` 38 + `LogisticsGrapple` 1).

Placed after R3 so the `Rewind` category's 6 restore-flagged tests join a lane whose
live state is known. Placed before R6-R7 because those depend on it.
Risk: the baseline restore is a real quickload, so a 10-test `AutoRecord` batch is
ten launch-and-restore cycles in one boot. Budget accordingly and expect the first
run to find something. `TestBatchMarker` / `ClassifyBatchIsolationMode` already own
crash reconcile.
Flight? No, but this is a mod code change and needs `provision.py --profile
stock-minimal` to reach a harness run.

### Tier 2: the actual basics sweep.

**R6. Drive the recording-lifecycle and classification batches.** Roughly 8 specs.

- Isolated (needs R5): `AutoRecord`, `SceneExitMerge`, `MergeDialog`, `RevertFlow`,
  `Coalescer`, `QuickloadResume`. Closes D1 `auto-record-first-mod-switch`,
  `commit-scene-exit`, `commit-revert-merge`; D5 `controlled-decoupled-child`,
  `crash-coalescing`; D9 `rewind-to-launch`.
- Free today: `Optimizer` (D4 `env-body-split`, `surface-graze-suppression`),
  `BackgroundSeeder` (D4 `seed-event-split`), `Recording` (D5 `bg-on-rails`),
  `TrajectoryMath` (D2 `threshold-debounce`), `Pipeline-Anchor` (D3
  `relative-anchored-nonloop`, `relative-loop`, `boundary-seam`), `SwitchSegment` +
  `SwitchIntentPatch` (the D1 switch-intent GATE layer, not a real switch).

Note on `Pipeline-Anchor`: analyzer rule `Inv3RelativeContract` already runs on every
scenario's produced save, but it fires only on VIOLATIONS. It cannot prove the
surface was produced. Presence still needs a token or a batch tally.
Flight? No.

**R7. Drive the D9 Rewind block.** ~~One or two specs (split FLIGHT / SPACECENTER).~~
**PARTLY SHIPPED 2026-08-04, branch `wire-rewind-block`. Two specs committed, a
third attempted and abandoned with its findings recorded.**

The pre-flight framing below was right that the split is FLIGHT / SPACECENTER and
wrong about why. Reading all 37 bodies (plus every helper they reach) found the
category is **BIMODAL on the re-fly session**, which no attribute or scene
analysis shows: TWELVE members gate on
`scenario.ActiveReFlySessionMarker != null`, and FOUR gate on it being NULL and
skip when one exists - FOUR as written, FIVE once this branch's
`F5MidReFlyResume` foreign-session skip guard is counted. The two sets are
DISJOINT, so no single boot can execute
both, and the split that matters is by PRECONDITION MODE, not only by scene.

What shipped:

- **`R7a-rewind-session-absent`** (ISOLATED, FLIGHT, `career-pad-craft`).
  LIVE-PROVEN, PASS attempt 1, 68 s, `total=37 passed=16 failed=0 skipped=21`.
  The pin was DERIVED before the flight and matched token for token. Claims D9
  `revert-during-refly-dialog`, `read-back-guard`, `rp-disk-reaper`. The isolated
  arg is worth 3 of the 16 (the `OnFlightReady_*` pair plus
  `PartPersistentIdStableAcrossSaveLoad`), and this is the first isolated spec
  whose category is only PARTLY batch-disabled - which required generalising
  three `IsolatedBatchWiringGroupTests` cells that had baked in H21 coincidences.
- **`R7c-rewind-spacecenter`** (SPACECENTER, `fresh-career`). FLOWN, tally
  MEASURED exactly as derived (`passed=4 failed=0 skipped=33`), batch green, but
  RED BY FINDING on the `forbidden` ERROR contract - see R7C-SITE-B1-ERROR in
  `todo-and-known-bugs.md`. Claims D9 `seal-stash-fly`, `rp-disk-reaper`.
- **The session-live spec was ABANDONED** after four flights. Arming a real
  session with `InvokeRewind` does unblock the twelve, but roughly a third of the
  category is written as "install synthetic session state, drive a global
  handler, assert" and is only correct when it is the only session in the
  process. Four genuine test-isolation defect-items were found and FIXED across
  three tests (`F5MidReFlyResume` twice - a settled-count assertion and a
  foreign-session skip guard; `JournalFinisherMarkerPresentVariant` eating the
  marker for nine later members; `KerbalRecoveryOnSupersede` asserting against an
  unflown provisional); four more failing tests were found and recorded. Full
  four-flight record, per-defect diagnosis and the recipe for a future attempt:
  R7-SESSION-BATCH-ISOLATION in `todo-and-known-bugs.md`.

What R7 did NOT close, and why: `unfinished-flights-stash` (the only cell that
would carry it skips - `ScenarioWriter` emits no `mergeState`, so nothing can
satisfy `IsUnfinishedFlight`); `tombstones`, `merge-journal`,
`terminal-kind-classify` (these need a FLOWN re-fly, which is CL-3's shape, not a
seam-only spec). Two permanent fixture gaps blocking five of the 37 are filed as
R7-FIXTURE-GAPS.
Flight? Yes - five flown (three R7 + two re-confirmations), 53-68 s each.

**R8. Drive D13, D6 and the D8 stragglers.** Roughly 15 specs, mostly free.

- D13 spawn positioning is now **7 of 11 uncovered** (was 11 of 11) and is NOT
  capability-blocked. 29 tests already exist and self-site off
  `FlightGlobals.ActiveVessel` - 26 declare `Scene = FLIGHT` and 3 are scene-agnostic
  (`SpawnHealth`), so all 29 run in a FLIGHT batch. #1358 wired three of the eight -
  `SpawnRotation` (10, H8), `SpawnHealth` (3, H16), `EvaSpawnPosition` (2, H20) - and
  claimed `surface-orbit-reseed`, `three-cycle-abandon`, `terrain-correction`,
  `trajectory-walkback`. REMAINING here: `TerrainClearance` (6),
  `SpawnTerminalOrbit` (3), `SpawnCollision` (2), `Spawner` (2), `Pipeline-Terrain`
  (1) - 14 tests, all FLIGHT, all on a fixture we own. Note every one of the five
  carries self-skip guards (see the inventory doc's bucket B4), which is why #1358
  left them: the batch would run and skip. Reading their guard preconditions and
  choosing a fixture that satisfies them is the actual remaining work, and it is
  still the cheapest whole-dimension close available.
- D6: `GhostLifecycle` (15 of 17; the other 2 are TRACKSTATION-scene - no longer
  stranded, R12 SHIPPED `LoadGame scene=trackstation`, but they need a TRACKSTATION
  spec of their own, since a batch names one category - AND see the inventory
  doc's B4 correction: a full-body read measured ~11 of 17 unreachable on any
  committed fixture, so it is generator/product work, not the next spec),
  ~~`GhostAudio` (9)~~ CLOSED by wave-2's `H30`, ~~`MapPresence` (5)~~ CLOSED by
  wave-2's `H28`, `ReentryFx` (3), `Watch` (2). None of the remainder needs the
  reserved `StartLoopPlayback` / `EnterWatchMode` verbs.
- D8: `LedgerGroundTruth` (1, needs a CAREER FLIGHT fixture - UNBLOCKED 2026-07-28,
  R11 is closed by `career-pad-craft`),
  `Contracts` (2), `StrategyLifecycle` (2), `Ledger` (4). `LedgerGroundTruth` is
  Layer B of the non-circular ground-truth harness and is the cheapest large increase
  in ledger trust available.
- ~~D12: `CrewReservation` (15).~~ CLOSED by wave-2's `H31` (b2-lko-craft, 14 of
  15 executing; D12 `seat-matching` + `rescue-marker` claimed).

Flight? No for what remains. (Wave-2's six flew 2026-08-04, 49-71 s each.)

### Tier 3: machinery that raises the ceiling.

**R9. Structural save-content expectations.** One harness PR plus one analyzer PR.
**SHIPPED-REPORT-ONLY 2026-07-31 (harness half, branch
`claude/r9-save-parse-verifier-tshhzv`), then ARMED AND LIVE-PROVEN ON S4.1 THE
SAME DAY (branch `r9-arm-s41`) - see the promotion block below.**

An `[expectations.recordings.structure]` block evaluated against the analyzer's
parsed model: branch-point counts by type, TrackSection frame and anchor, terminal
state and body per recording. Plus land the three inert blocks (`route`, `rewind`,
`loop`). Retires the presence-only grep proxy, makes D3/D4/D5/D7/D18 claims mean
something, and closes both the "one of two board merges dropped still passes" class
and S4.1's assertions that currently do nothing.

DELIVERED: the M-C2 save-parse verifier - a pure-Python parser
(`harness/lib/saveparse.py`, the oracle-precedent sibling) over the produced
save's ParsekScenario surfaces (RECORDING_TREE topology + recordings + branch
points, RECORDING_SUPERSEDES, LEDGER_TOMBSTONES, RECORDING_REWIND_RETIREMENTS,
REWIND_POINTS/slots), a new `saveParse` verifier row evaluating
`[expectations.rewind]` (supersedeRows / tombstones / rewindPoints windows) and
`[expectations.recordings.structure]` (tree counts, recording counts, terminal
states by name, branch-point counts by type), spec-surface validation in
`validate_spec`, and measured facets recorded on every driver-valid run.
REPORT-ONLY by default (verdict neutrality: S4.1 already declares a rewind
block, so a gating default would have moved a committed nightly's verdict with
no live run to prove the readings); a block arms with `gating = true`, declared
by ZERO committed specs AT THE TIME OF LANDING and guarded by a test-suite
sweep (S4.1 armed later the same day - see the promotion block below - and the
guard became an allowlist rather than being deleted). `rewind` LEFT
`RESERVED_EXPECTATION_BLOCKS` (sole-owner rule, the M-B2 `world` precedent).

PROMOTION DONE 2026-07-31 (branch `r9-arm-s41`) - this was item (a) of the
previous revision of the STILL-OPEN list; that list has since been renumbered
and its (a) is now CL-2 stage B.
S4.1-rewind-merge is now the FIRST and ONLY committed spec arming
`gating = true`, and the rewind save surface is a real gate rather than a
recorded reading. Three live runs did it - a report-only reading
(`2026-07-31_1628`), the armed re-fly (`_1635`), and a negative control that
inverted the window and reddened `PARSEK-FAIL(save-structure)` (`_1637`).
THE PER-RUN FACETS LIVE IN `docs/dev/autotest-status.md`, THE STATUS AUTHORITY -
this doc owns forward build order, not run results, so do not re-copy the
readings here.

The negative control is the load-bearing one: a gate nobody has watched fail is
an assumption, not a gate. Arming moved no verdict (both `max = 0` windows were
already satisfied by the measured save) - it made existing behaviour
load-bearing. The verdict-neutrality guard cell was NOT deleted on the way: it
became an explicit allowlist pinning exactly `{"S4.1-rewind-merge.toml"}`, so a
second spec arming still reds until someone edits it deliberately and cites the
run ids. Answered on the way: the merge REAPS `rp_b9_root` (`rewindPoints` 0,
and the save carries no `REWIND_POINTS` node at all); recorded, deliberately not
pinned.

STILL OPEN to close R9 fully: (a) CL-2 **stage B**'s windows. Stage A was
measured 2026-07-31 (`2026-07-31_1641_CL-2-pod-impact-ledger`, PASS, declares no
M-C2 block so the row is pure measurement): `rewind` all-zero, `structure`
`{trees 1, committedTrees 1, recordings 1, terminalStates {Destroyed: 1},
branchPoints {}}`. That is the PRE-REWIND baseline, not stage B's windows -
stage B rewinds across CL-1's crew loss, so its numbers must be read off stage
B's own report-only flight (expected `supersedeRows >= 1`, `tombstones >= 1`)
before arming, exactly as S4.1 just did; (b) `route` / `loop` stay RESERVED -
their consumers do not exist (zero committed declarers), so no evaluator was
built for them; (c) the analyzer-PR half (TrackSection frame/anchor +
per-recording body asserts over the analyzer's parsed model) - the .sfs surface
deliberately does not carry those, they live in `.prec` sidecars the analyzer
already parses.

**R10. Runtime-handle plumbing.** One harness PR plus one seam verb.

(a) Capture seam response payloads into a named store with `${step.field}`
substitution at `run.py:1157`; (b) generalize `mission_runner._perform_seam_commit`
(currently hardcoded to one reserved CommitTree id) to arbitrary verb plus
runtime-computed args with readback, which gives live-handle addressing INSIDE a live
flight; (c) add a RewindPoint / slot list verb, or extend `RecordingState`'s
four-field payload. Unblocks live-authored `InvokeRewind` and every future verb that
addresses a live tree, vessel, route, or kerbal.

**R11. A CAREER fixture with a flyable craft.** ~~One forge spec, one run.~~
**CLOSED 2026-07-28 by `harness/fixtures/saves/career-pad-craft`** - built BY
CONSTRUCTION rather than by a forge flight. `harness/tools/build_career_pad_craft.py`
splices `b1-pad-craft`'s Jumping Flea `VESSEL` node into `fresh-career`'s empty
`FLIGHTSTATE` and swaps the crew kerbal's roster row for the `state = Assigned` one,
with a `--check` mode plus a byte-identity drift cell in
`harness/missions/lib/test_cl1_crew_loss.py`. No flight, no operator session, no
`FORGE-career-pad` spec. First consumer: `CL-1-pod-impact`.

What that unblocks is now AVAILABLE, not delivered - each still needs its own spec:
the L-track end goal, D8 `milestones` / `contracts` / `strategies` / `tombstones` in
flown form, D12 `reservation-auto-hire` / `tombstone-rep-penalty`, D9 `tombstones`,
and D8 `ground-truth-harness` (which self-skips outside career).
Flight? None to close R11 itself. The consumers after it are mostly seam boots.

Original plan, kept for the record: `FORGE-career-pad`: fresh-career plus a craft
into `Ships/VAB` plus `launch_vessel`, a mechanical repeat of `FORGE-eva3-pad`.

**R12. Two scene and interaction verbs: `SimulateStockSwitchClick` plus a `scene=`
argument on `LoadGame`.** ~~Two seam verbs.~~ **SHIPPED 2026-07-30.**

kRPC cannot substitute for the first (it bypasses `StockActionIntentMarker`) and
nothing at all substitutes for the second. Unblocks D1 `switch-segment` /
`switch-segment-noop-discard` in their REAL form (R6 only reaches the gate layer),
D5 `chain-continuation-switch`, D18 `committed-interaction-claiming` /
`chain-tip-original-pid`, D14 `scene-ts`, and the 7 stranded TRACKSTATION /
MAINMENU categories including `TrackingStation` (10 tests).

DELIVERED as THREE capabilities, not two - the scope grew one item while the design
was written, because Cause C ("scene entry is two-valued") has a second half that the
`scene=` argument does not reach: nothing could LEAVE flight either.

- **A1 `LoadGame scene=<spacecenter|trackstation>`** - a third boot route, mirroring
  the SPACECENTER bootstrap verbatim (verified against the KSP 1.12.5 decompile;
  zero deltas). Fail-closed, case-sensitive parse.
- **A2 `ExitToSpaceCenter`** - the live FLIGHT -> SPACECENTER transition, with the
  wedge guard that refuses (`REJECTED msg=dialog-required variant=<v>`) rather than
  driving into a modal no seam verb can answer.
- **B `SimulateStockSwitchClick`** (site=map) - the first PROMOTION out of the
  reserved verb list since M-C1.

Live-proven by three first-consumer specs, all green on `stock-minimal` 2026-07-30:
`H23-tracking-station` (44 s), `S0.7-exit-auto-commit` (47 s),
`S0.8-switch-click-segment` (45 s). `TrackingStation` is driven, which empties
inventory bucket C; D14 `scene-ts` is covered for the first time.

WHAT R12 LEAVES BEHIND, each a separate follow-up and none of it a regression:

- **`site=ts` / `site=ksc`** on `SimulateStockSwitchClick` - typed
  `REJECTED site-not-implemented` in v1. Both cross scenes into a fresh FLIGHT load
  and must go through their own patched handlers (the TS one runs
  `RemoveAllGhostVesselsBeforeStockFly`, a live-list/saved-file index desync fix a
  hand-rolled `FlightDriver.StartAndFocusVessel` would reintroduce), so each belongs
  with its own consumer.
- **The dialog cases** (`dialog-required case=A-session|B-unloaded|
  C-loaded-separate-committed`) and **unloaded targets** - typed refusals in v1. A
  seam verb cannot answer a `ControlTypes.All`-locking modal, so driving one needs a
  dialog-answering capability first, not a wider switch verb.
- **The CL-1 spec extension onto `ExitToSpaceCenter`** - **STAGE A SHIPPED
  2026-07-30** as `CL-2-pod-impact-ledger` (a NEW spec; CL-1 itself is untouched,
  because the committed cell
  `test_cl1_crew_loss.py::test_the_spec_drives_no_commit_and_declares_no_ledger_block`
  exists precisely to forbid the naive edit). It is CL-1's step list
  plus `SetSetting autoMerge=true`, `ExitToSpaceCenter`, and an
  `[expectations.ledger]` block. `S0.7` was right about the prerequisite - proving
  the pending-tree AUTO-COMMIT needs a tree that is not idle-on-pad, and no seam
  primitive provides dwell while recording, so a driver that genuinely FLIES was
  required. CL-1's 262-point / 11.9 km hop clears the 30 m idle threshold by three
  orders of magnitude, and the commit fired: `Silent full-fidelity auto-commit
  (scene-exit)`, `Committed tree ... Total committed: 1 recordings, 1 trees`,
  `CreateKerbalAssignmentActions: 1 crew members`, and `OnSave: saving 1 committed
  tree(s)` against the archived pre-commit run's `saving 0`. D1
  `commit-scene-exit` + `auto-merge` - the two values S0.7 had to DROP - are now
  claimed with tokens, and D8 gains its first crew-loss claims.
- **Stage B, the TOMBSTONE half, remains UNBUILT**, and the split is structural
  rather than a scoping convenience. `SupersedeCommit` is the ONLY producer of a
  `LedgerTombstone` and `CommitTombstones` runs strictly inside the RE-FLY merge
  tail after supersede relations land, so no auto-commit can reach D9 `tombstones`
  / D12 `dead-crew-strip` / D12 `tombstone-rep-penalty` however the exit is driven.
  Closing them needs a rewind ACROSS the crew loss plus a `RunTests` step driving
  the in-game `Rewind` category, so `InGameTests/KerbalRecoveryOnSupersedeTest`
  stops auto-skipping with "No kerbal-death actions in supersede subtree" - CL-1's
  committed tree IS the subtree it wants. Stage B cannot be folded back into CL-2:
  it needs `InvokeRewind`, and `hlib.validate_spec` HARD-REJECTS `InvokeRewind`
  paired with `[expectations.ledger]` (a rewind rewrites the career pools from a
  quicksave the seed+manifest contract cannot reconstruct). Two prerequisites to
  settle first: `dead-crew-strip` has no pinned definition in the registry (see
  `todo-and-known-bugs.md`), and the crew-end-state defect CL-2 flight 1 found
  means the subtree's kerbal-death action currently carries `KerbalEndState.
  Unknown`, which is exactly what that in-game test skips on.
- **D5 `chain-continuation-switch` / D18 `committed-interaction-claiming` /
  `chain-tip-original-pid`** are still UNCOVERED. `S0.8`'s measured consume route is
  `standalone` (`parentRecId=<standalone> branchPointId=<none>`), so no chain link is
  created; claiming them would need a fixture whose switch target is a background
  member of the live tree, or a committed spawned vessel.
- **The other 6 stranded TRACKSTATION / MAINMENU categories** (including the 2
  TRACKSTATION-scene `GhostLifecycle` tests named under R8) are now REACHABLE through
  `scene=trackstation`; each still needs its own spec.

**R13. Widen `SINGLE_BATCH_SELECTOR_RULE` to N categories with N pinned tallies.**
One harness PR.

The runner already emits per-category `BATCH_COMPLETE` lines plus a
`category=multi:<n>` aggregate, and `hlib.resolve_batch_complete` already parses
both. What is missing is teaching `batch_contract_vacuity_gap` to probe each named
category against its own pinned tally, which is what the current rule's own comment
says is "NOT expressible on this contract surface". Ranked HERE and not higher on
purpose: it is an efficiency item, not an unlock. At one category per spec the whole
undriven-category fan-out is roughly 89 boots, about 89 minutes, which the schedule
can absorb. The reason to do it is the long tail, not the D1-D9 basics.

**R14. Provision `modded-compat` and add one spec.** One provision run, one spec.

Every `devSourcedMods` entry in `harness/provision/profiles/modded-compat.toml` is
reportedly present in the dev GameData (UNVERIFIED here; not re-checked in this
worktree). `WaterfallCompat` (8) and `ReStockCompat` (9) exist and self-skip on
stock, so running them on stock-minimal would be vacuous, which is precisely why the
second instance is load-bearing. Closes 3 or 4 of the 6 D17 cells
(`waterfall-swe-fallback`, `restock`, `better-time-warp`, possibly `making-history`);
`persistent-rotation` and `remotetech-commnet` are source-blocked, not
capability-blocked. Side benefit: a second instance directory is a second possible
run lane, and runs are currently strictly serial under a per-instance run lock.

**OUTCOME 2026-08-04 (branch `modded-compat-lane`): CLOSED, with the claim
smaller than the prediction.** The instance provisioned first try (the dev
GameData really carried every pin; only the audio-silencing settings deltas were
missing from the profile) and TWO specs landed, not one (MC-1/MC-2, both
LIVE-PROVEN). Closed 2 D17 cells, not 3-4: `better-time-warp` and
`making-history` need their own scenario subjects (BTW warp behavior, MH
parts/sites), which no compat batch exercises - instance available, spec work
open. The "second run lane" side benefit did NOT materialize: the 2026-08-02
machine-lock rework made the lock machine-wide (one lockfile for both
instances), deliberately, because kRPC ports and the GPU are machine-global.
Status authority for what shipped: `autotest-status.md` "Modded-compat
instance (D17), R14".

### Tier 4: the expensive residue. Schedule, do not attempt opportunistically.

- D1 `manual-gloops`: new seam verb or new in-game test.
- D1 `commit-abort`: needs a definition first.
- New in-game tests against existing seams: D6 `zone-transitions`, D4 `tail-trim`,
  D5 `staging-debris-promotion`, D9 `load-time-sweep`, D2 `density-presets`.
- D5 `dock-merge-same-tree`: two-port single-launch craft, new fixture + mission.
- D7 `inventory-place-remove`, D10 `inventory-cargo`: inventory craft + an inventory
  action in the mission vocabulary.
- D10 `claw-producer`: no grapple action exists; the `ClawCouple` (2) and
  `LogisticsGrapple` (4) in-game categories are the only path.
- D12 `crew-swap`, `seat-matching`: no crew-transfer action.
- D8 `milestones`: a career flight that EARNS one. Rode R11, which is CLOSED
  2026-07-28 (`career-pad-craft`); still not this lane. NOTE: `CL-1-pod-impact` is
  the first career FLIGHT and will MEASURE which progress milestones a 12 km crewed
  hop trips - that measurement is the input this cell has been missing.
- D7 `engine-fx-waterfall-fallback`: belongs with R14, not with the part-event work.
- A declarative multi-piece mission composer to replace bespoke phase machines.

### Tier 5 (added 2026-07-29): beyond R14 - pointer index only

The 2026-07-29 full-stack audit (`test-coverage-audit-2026-07-29.md`) and the
unified testing design (`design-testing-unified.md`) extend this roadmap past
R14. The ranked detail and rationale live in `design-testing-unified.md` §8 -
this tier is a pointer index so the roadmap stays the single forward-order
surface, and it does NOT reorder R1-R14: the audit independently re-confirmed
this file's sequencing rule (gate what already flies before growing the flight
lane) and its top unbuilt items (R9 structural expectations was the single
highest-leverage item until its harness half SHIPPED-REPORT-ONLY 2026-07-31 -
see the R9 entry; R5's unlock remains almost entirely unconsumed).

Clusters, in the design doc's phase order:

- **Fail-open closures** (spec/config; extends Tier 0's spirit): the
  `STOCK_AWARD_PATTERNS` rewrite (known-gate 3), anomaly count-budgets
  (known-gate 0), and a report-only raw-Unity-exception scan LANDED via
  PR #1377. The armings have since happened: the capture cross-check on CL-2
  (2026-07-31), then on 2026-08-04 the unity-exception scan on 14 specs plus
  the seven-token anomaly promotion (status-doc gates 0/3/11 carry the
  measured baselines). Still open: the B4 chute-latch diagnosis
  (known-gate 7); converting the ~19 silent early-return PASS sites to loud
  Skips; ERS/ELS gate hardening (`CommittedTrees` pattern,
  fail-on-missing-pwsh).
- **Data-integrity units** (xUnit, no flight time): `SafeWriteConfigNode`
  destroy-on-failed-save LANDED via PR #1375; the schema-reject prune chain
  end-to-end and the `SaveActiveTreeIfAny` both-or-neither fix LANDED via
  PR #1376. Still open: rewind-across-SOI; RP-slot ambiguity; the
  crew-death -> tombstone -> rep-penalty chain; journal crash matrix as a
  `[Theory]` over the phase enum.
- **Property/fuzz lane** (xUnit): `RecalculationFuzzer` extended to all 9 modules
  with state invariants; a seeded random-tree fuzzer for the supersede/chain/
  closure walkers.
- **Visual validation program** (V1-V7 in `design-testing-unified.md` §6):
  **V1 LANDED 2026-07-30** (branch `v1-map-dwell`): the `V1-map-dwell-mun-orbit`
  operator-tier scenario aims the existing parity oracle at real flown geometry
  across time - delegated B11 flight + the R1 rewind cycle (LOAD-BEARING:
  PlaybackScopeTracker keeps a forward-play committed tree dormant, so a dwell
  without the rewind is structurally vacuous, measured in BDOCK-1's 674
  post-commit ghosts=0 probe frames) + a staged kRPC map camera (new
  `read_camera` OBSERVED channel) + a 1x hold, a rails-warp stair, and a held
  re-cross of the recorded Kerbin->Mun SOI boundary UT, gated on the probe's
  own nonzero-ghosts / parity-sampled summary lines. Its green runs'
  `anomalySweep.hitCounts` / `unlistedReasons` are the calibration data the
  anomaly count-budget arming pass (known-gate 0) waits on. Run evidence +
  baseline: `autotest-status.md`. Still open, in order: gating the FX emission
  probe (V2); always-collect + HTML contact sheets (V3); `Screenshot`/
  `MapCamera` seam verbs (V4); the self-consistency double-render pixel oracle
  (V5); pixel-free geometric invariants (V6); the `UiSmokeRender` window sweep
  (V7); the render composition manifest + verifier (V8, module M-A7 -
  design authority `design-autotest-render-composition.md`).
- **Mode-axis expansion**: a science-mode spec lane; seam-forged career fixtures
  (`KscAction` progression -> harvest, the FORGE pattern applied to career state);
  the templated mid-career matrix; `LedgerGroundTruthHarness` wired to a career
  spec; the L-track grand oracle (depends on the `STOCK_AWARD_PATTERNS` rewrite).
- **Mission-fleet growth** (after the gates above): `b5_decide` body sweeps; a
  curated craft fleet including vetted downloaded craft (persisted-VESSEL intake
  for career; the dead `[fixture].craft` spec key needs fixing or deleting);
  Duna landing; rendezvous-without-docking; perf/soak scenarios (ghost-count
  frame-cost budgets, long-recording round-trip budgets).
- **Loop-render coverage** (operator-tier calibration flights, NOT nightly
  growth): the class taxonomy, gap register G1-G9 and ranked sequencing for
  "any origin, any destination" supply-run rendering live in "The loop-render
  coverage program" below. Pointer only; it reorders nothing above it.

---

## The loop-render coverage program

The objective is the supply run: a looped transfer recording between any
origin and any destination, rendering accurately for as long as the loop runs.

Added 2026-08-20, after the moon-to-moon program (PR #1513) measured the
moon-to-moon routing - the H3 answer: Parsek neither re-aims nor phase-locks a
moon-to-moon hop, it replays faithfully on the raw cadence - on the first
subject of that shape ever flown. M-MIS-7 PROPER is not answered: that is
intra-SOI per-leg re-aim (Jool-centric Lambert re-solves, per-leg holds at each
moon-SOI seam), gated in `design-mission-multimoon-alignment.md` section 8 on
an in-game looped Jool tour playtest that has not run.

This section is the DIRECTION document for the V-lane family: what "rendering
is confirmed" means, what is confirmed today, which classes are missing, and
the ranked order to close them. Status of individual lanes stays in
`autotest-status.md`; this section owns only the taxonomy, the gap register,
the confirmation criteria and the sequencing.

Sequencing, reconciled with this doc's standing verdict that the nightly flight
lane should NOT grow until the basics are gated: these are OPERATOR-tier
calibration flights - the V14-V17 pattern - and they rank inside that budget,
not inside the nightly one. The verdict stands unamended.

### The objective, stated as a product claim

Logistics is FEATURE-COMPLETE in the product: M1-M6 all shipped by 0.10.3, the
claw joined docking as the second connection producer, and station phase-lock
shipped in v0.10.1. The detail is owned elsewhere - see `docs/roadmap.md`
Phase 13 and `docs/parsek-logistics-supply-routes-design.md` section 19 - and
is deliberately not re-derived here.

Product reach is scoped by doctrine, which also scopes what this program owes
coverage for: a route moves cargo between DOCK- OR CLAW-CONNECTED pairs, so
stock crossfeed and crew delivery are out of scope by doctrine and are not
classes owed a render lane. Within that scope every origin -> destination pair
is PRODUCT-REACHABLE today, while render confirmation covers only the classes
below. The product claim that needs confirmation is:

> A looped transfer recording between ANY origin and ANY destination renders
> accurately in map view, the Tracking Station, and the KSC scene wherever its
> Kerbin gate makes that host non-vacuous - correct icons, correct lines,
> correct body frames, correct cadence - for as long as the loop runs.

Nobody can visually check every pair. The confirmation instrument is the V-lane
discipline, already proven across the committed lane pairs: fly the transfer
once (B-lane), harvest the produced save as a fixture, loop it, TimeJump to
epochs DERIVED from the measured loop clock, hold census dwells there, and gate
on the rendered-truth log lines (the `MapRenderTrace` / `MapRender` / `GhostMap`
censuses, faithful-parity, seam-endpoint, anomaly sweep) through the three-run
reading -> armed -> negative-control sequence.

### The coverage unit is the equivalence class, not the pair

The Kerbol system has 16 flyable bodies (7 planets + 9 moons), ~240 ordered
pairs before counting endpoint types. Flying them all is neither possible nor
necessary: the playback and render code paths are selected by CLASS, not by
pair. The dimensions that actually change code paths:

1. **Routing road** - which loop planner owns trajectory and timing. FOUR
   values: `phase-lock` (same-parent target: cadence quantized to
   target-period multiples, no trajectory synthesis); `station phase-lock`
   (registry D11 `station-phase-lock`, shipped v0.10.1: a rendezvous mission
   relaunched against the station's live orbit); `re-aim`
   (classifier-admitted transfers: synthesized ancestor-frame conic, synodic
   window spacing); and `faithful fixed-cadence` (everything the classifier
   declines: verbatim replay, frame-relative arrival rendering,
   self-overlapping when the span exceeds the overlap cadence).
2. **Recording shape** - properties of the subject bytes that flip render
   policies. The fail-closed policy's trigger is NESTED SOI, not a seam count:
   `FailClosedClassifier.Classify` builds `NestedSoiSubtree.FindNestedRoot`
   and fail-closes when two VISITED bodies are siblings under a shared
   NON-ROOT parent - two moons of one planet. A single-level cross-SOI chain
   (Kerbin -> Mun -> Sun -> Duna) is explicitly NOT auto-failed
   (`FailClosedClassifier.cs:128-135`, with the sibling test at
   `NestedSoiSubtree.cs:207-218`), so "two or more seams" is not the
   predicate. Terminology, because the two get swapped: the PRODUCER token is
   `nested-soi` (`FailClosedClassifier.ReasonToken`, emitted as
   `producer=nested-soi ... action=render-recorded-verbatim`), while
   `ProtoOrbitLine` is the render SURFACE the fail-closed member then draws
   on. The other shape properties that flip policy: segment-less tails (kill
   the TS init walk's orbit source), self-overlap (spawn throttle and re-arm
   creation-frame behavior), eccentric / inclined targets, and a cadence that
   is a PERIOD MULTIPLE rather than one period (V16's cadence = 20*P subject).
   "k" is reserved below for cycles observed, never for the period multiple.
3. **Render host** - THREE, not two. The flight map and the Tracking Station
   render through different hosts (`ParsekUI.DrawMapMarkers` vs
   `ParsekTrackingStation`; the TS additionally splits into the one-shot init
   walk and the dynamic overlap path), and the KSC scene is a third:
   `ParsekKSC` owns its own ghost dictionary, its own overlap-ghost model and
   its own route push seam, and is HARD-GATED to Kerbin-frame points (skip
   reason `non-kerbin`, `ParsekKSC.Playback.cs:288-297` and `:362-372`, both
   ahead of every other branch). That gate makes the KSC host VACUOUS for
   outbound interplanetary lanes and non-vacuous exactly for return legs
   arriving at Kerbin. Registry D14 already carries `scene-ksc`.
4. **Endpoint type** - orbit, moving vessel/dock, or surface. Every committed
   loop lane ends at an ORBIT; supply routes end at docks and surface bases.
5. **Render surface** - which drawing surface carries the truth, because a
   class can render correctly on one and wrongly on another. Three: the ghost
   proto icon plus proto orbit line; the ghost trajectory polyline / Director
   TracedPath shadow; and the ROUTE OVERVIEW LINE
   (`Display/RouteTrajectoryLineRenderer.cs`, the M6 map-view route lines -
   default-on `showRouteLines`, drawn on the flight map AND the Tracking
   Station, skipping any recording the polyline Driver already published
   through `GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg`, and, on
   an inter-body route, keeping the ENDPOINT-BODY legs only with the recorded
   transfer frame DELIBERATELY dropped because M5 re-aim replaces that
   geometry per window). Registry cell D10 `route-map-lines` has ZERO
   declarers: no committed spec exercises that surface at all.

Three further axes are deliberately NOT dimensions here. Ghost count and
co-residency - N routes rendering at once, where both the boundary-overlap
secondary and the overlap soft caps are count-dependent - is folded into the
long-horizon gap G8 rather than tracked on its own. Career-vs-sandbox becomes
load-bearing only for route-driven lanes, and narrowly: `RouteDispatchEvaluator`
runs a KSC-origin funds check under `IsCareer && IsKscOrigin`, so career gates
the FUNDS lane of a KSC-origin route rather than dispatch as such. Loop
watch-mode is OUT of scope entirely - it is a flight-camera state machine, not
a map render surface, and three lanes have already measured its refusals.

### The bridging assumption, stated so it can be attacked

Every committed V-lane drives a MISSION loop. A real supply route's ghost
enters through a DIFFERENT front door.
`RouteGhostDriverSelector.SelectGhostDrivingBackingMissions` materializes a
backing Mission per route through `RouteBackingMission.BuildMission` - a
Mission that is never inserted into `MissionStore` - and each host unions that
list into `MissionLoopUnitBuilder` inside its own `DriveMissionLoopUnits`
(three byte-identical seams: `ParsekFlight`, `ParsekKSC`,
`ParsekTrackingStation`). The selector gates on
`RouteStatusPolicy.GhostDriving`, so `Paused`, `EndpointLost`,
`MissingSourceRecording` and `SourceChanged` SUPPRESS the ghost - a
render-visible outcome no lane has ever observed. And the loop then runs on a
ROUTE-OWNED clock: `RouteLoopClock`, with `DispatchInterval` derived as N x the
run's span, `Route.LoopAnchorUT` seeded from the recorded span start on a
create-Active route or from the live UT on a Paused -> Activate and then
FLOORED to `spanEndUT` by `MissionLoopUnitBuilder` (so the route's own anchor
is diagnostic-only - a subtlety three source files pin and no lane has
exercised), plus the M5 N-residual modulo (`ResolveResidualCadence`) on
re-aimed windows.

Nothing measured so far touches any of that. The mission-loop lanes are a
PROXY: they exercise the shared render machinery BELOW the front door and say
nothing about the door itself, the status gate, or the route clock. That is an
assumption this program rests on, not a finding it has made. Closing it is G1.

### What is confirmed today (class matrix)

Representatives are bare lane ids - per-lane verdicts, run ids, roads and
arming state live in `autotest-status.md`'s test-case tables, and this table
must never carry them.

| Class | Road | Representatives | Confirmed? |
|---|---|---|---|
| Planet -> its own moon | phase-lock | V6M/V6T, V7M/V7T, V14M/V14T, V15M/V15T, V16M/V16T | YES |
| Kerbin -> planet, transfer admitted | re-aim | V5, V8/V8T, V9, V10, V11/V11A, V12/V12A, V13/V13A | YES |
| Kerbin -> planet, classifier-declined profile | faithful | none | NO (see note) |
| Moon -> sibling moon | faithful | V17M/V17T, V21M/V21T | YES |
| Return leg, moon -> its own parent | faithful | V19M/V19T | YES (map + TS) |
| Return leg, planet -> Kerbin | faithful | V20M/V20T | YES (map + TS; see note) |

Seven scoping notes the table cannot carry without becoming a status doc:

- The planet-to-Kerbin row's `YES (map + TS)` is CLASS-LEVEL and the two hosts are
  confirmed on DIFFERENT LENSES, which the G2 entry below states in full. The TS
  third is discharged on a RENDERED-FRAME token; the flight-map third is
  discharged on a ghost-proto CREATION-frame token plus a seed-side arrival token,
  because that host's proto ORBIT-LINE lens was measured segment-zero-only. The
  KSC third is NOT confirmed and belongs to `V20K`. The row says YES for the class
  and the note is where the asymmetry lives.

- The phase-lock row's scene coverage is its ARMED halves - V6M/V6T, V14T,
  V15T, V16T. It does NOT include V7T, which is RED BY FINDING and
  deliberately ungated until that finding is explained.
- The re-aim row's Tracking-Station coverage is V5 and V8T only. V10-V13's
  A-suffix lanes are SECOND SAME-SCENE lanes, not TS halves, and reading them
  as scene coverage would overstate the row.
- V9 sits on the re-aim row because after the `dres-split-cohesion` fix it
  classifies ENGAGED re-aim and is the armed regression floor for that fix.
  Its pre-fix FAITHFUL runs are history, not a class representative.
- The two RETURN-LEG rows are the INVERTED direction and are a separate class
  from the outbound rows above them, not a re-reading of those:
  `IsSameParentTarget` asks only whether the target is a direct CHILD of the
  launch body, so the relation is DIRECTIONAL BY CONSTRUCTION.
- The moon-to-parent row's YES is scoped to TWO of the three render hosts - the
  flight map and the Tracking Station - and its "Confirmed?" column says so,
  because the KSC host is STRUCTURALLY VACUOUS on V19's Laythe-rooted subject
  rather than merely unflown. Its road reads `faithful` because that is what
  V19M MEASURED (R3), not because the classifier was read. The planet-to-Kerbin
  row's road reads `unmeasured` deliberately: nothing has flown it, and writing
  `faithful` there because the moon-to-parent row came back faithful would
  extrapolate one direction's measurement onto a different class, which is
  exactly what the mirror-direction lesson forbids.
- The classifier-declined faithful row has NO subject. V3F and V8F are
  KNOB-FORCED (`forceFaithfulLoopPlayback`) A/B controls, not classifier
  declines. The only flown decline at this class was the
  OPTIMIZER-SPLIT-DEFEATS-REAIM-CLASSIFIER defect V9 measured on runs
  `2026-08-12_0150` / `_0153`, and that defect is fixed - so the class is
  currently believed unreachable by any flyable Kerbin -> planet profile - a
  belief resting on a fixed defect rather than on a measurement. Same posture as
  G7: a product question, not a scheduled lane, and excluded from the
  definition of done for that reason.

### Confirmation criteria

"Confirmed" for a class means: a representative lane has a green ARMED run
whose required tokens pin the destination-frame render, plus a negative control
that reds on demand. Three sharpenings, each bought by a flight rather than
argued:

**(a) Pin the destination frame on the lens that carries it FOR THIS SUBJECT'S
SHAPE.** A nested-SOI subject fail-closes its proto orbit line to the ROOT
frame, so a `surface=ProtoOrbitLine ... body=<destination>` pin on such a
subject is STRUCTURALLY UNSATISFIABLE - not a red, an unsatisfiable pin. V17M
spent four reading runs learning this (`2026-08-20_1841`, `_1859`, `_1908`,
`_1915`); the anti-vacuity pins were re-targeted onto the fail-closed
DECLARATION LINE plus the Director TracedPath SHADOW drive of the destination
approach segment, which is the lens that actually carries the destination
frame. Simple (non-nested) subjects keep the proto-line lens. A spec must name
which of the two lenses it pins, and why that is the right one for its shape.

**(b) The negative control must invert a REQUIRED RENDER TOKEN**, or the spec
must state why a render-token inversion is structurally impossible for that
lane. The standing shared inversion - a temporary `rewind.supersedeRows`
minimum - proves the `saveParse` EVALUATOR can red; it proves nothing about
whether the render pins can.

**FIRST DISCHARGED 2026-08-21 BY THE V19 PAIR**, which until then every V pair
had failed: each half inverted a required RENDER token of its OWN, and each
red'd on exactly that token with `saveParse` still PASS and
`driverValidity` / `anomalySweep` clean - so the red is provably on the render
pin rather than on the evaluator. V19M `2026-08-21_0855`:
`logContracts.required not matched: phase=body-orbit surface=ProtoOrbitLine
.*body=Vall`. V19T `2026-08-21_0858`: `logContracts.required not matched:
phase=GhostCreated surface=ProtoIcon pid=\d+ .*body=Vall scene=TRACKSTATION`.
Both inverted the destination body to `Vall`, a body their subject never
visits, and both were reverted after the control. **TWO CONTROLS, NOT ONE
SHARED, IS PART OF THE DISCHARGE AND NOT REDUNDANCY:** the halves pin DIFFERENT
LENSES - the proto orbit line on the flight map, the proto icon in the Tracking
Station - so a single shared inversion would have proven exactly one of them.
A pair whose halves pin the same lens may share one; a pair whose halves pin
different lenses owes one each. Every OTHER committed V pair still shares the
`rewind.supersedeRows` inversion and still owes this.

**(c) A documented-limitation escape under clause (b) of the definition of done
must CITE A FLOWN RUN ID.** Limitations of this system are discovered by
flights, not by inspection - the fail-closed root-frame policy itself was
(V17M `_1908`). An escape argued from reading the code is a prediction wearing
a limitation's clothes.

### The gap register, ranked

Ranked by supply-run value per flight-hour. Each entry names the class, why it
matters, the cheapest representative, the expected-but-unmeasured routing, and
what counts as confirmation. Scenario ids B27-B32 and V18-V26 are RESERVED HERE
(across all their suffixes: the established `M` player/flight-map and `T`
tracking-station lanes, plus `K` for a KSC-host lane - G2's correction below
reserves `V20K`, and G3a reserves `V22K`, which is the first committed `K` lane)
- this section is their only home - so sibling PRs do not collide; check open
PRs before authoring and renumber only if one already claims an id.

**V24 IS RESERVED HERE AND NEW, AND IT MINTS THE `W` SUFFIX**, which is the part
worth reading twice. Every suffix so far names the RENDER HOST the lane observes
from: `M` the player flight map, `T` the tracking station, `K` the KSC. `W` names
a DRIVE SHAPE instead - a **warp-schedule lane**, one that moves the game clock
with a real rails ladder rather than with instantaneous `TimeJump`s. It exists
because M-A7's RC-WARP rule is satisfiable ONLY by that shape: both armed
render-composition lanes drive `TimeJump`s exclusively, so their warp histograms
are 1x-only by construction (confirmed on four flights) and `warpBuckets` may
never be declared on either. A `W` lane may therefore SHARE a host with an
existing lane - `V24W-duna-one-warp-stair` observes the same flight map V2 does -
because what distinguishes it is not where it watches from but how its clock
moves. **The first committed `W` lane is `V24W-duna-one-warp-stair`** (authored
2026-08-25, never flown, reading run pending; status row in
`autotest-status.md`), over `fixtures/saves/duna-one-recorded`, the harvest of
the first free-play ground-truth session. `V24M` / `V24T` / `V24K` are reserved
alongside it and unused; a second warp-schedule subject should take the next free
number with a `W`, not a second suffix letter.

**V25 IS RESERVED HERE AND NEW (2026-08-26), ACROSS ALL ITS SUFFIXES**, on the
same rule as every reservation above: this section is their only home, so check
open PRs before authoring and renumber only if one already claims an id. It takes
the ORDINARY `M` suffix and mints nothing, because what makes it a distinct
subject is the RECORDING rather than the host or the drive shape. **The first
committed V25 lane is `V25M-duna-park-player-loop`** (authored 2026-08-26, never
flown, reading run pending; status row in `autotest-status.md`), over
`fixtures/saves/duna-park-recorded` - the SECOND payload stripped out of the same
visually-validated s15 free-play save that gave `duna-one-recorded`, and disjoint
from it. It is **re-aim's second departure class**: every prior re-aim subject in
the suite (V2 / V8 / V10 / V24W) is a DIRECT ejection, while this one escapes
Kerbin almost immediately, phases on the SUN for 13,502,219.94 s on one
near-circular orbit 3.47 % outside Kerbin's own, and only then burns for Duna.
That path exists in the product BECAUSE of these bytes - `ReaimClassifier`'s
partial-transfer decline carries an exception whose comment reads "EXCEPTION (s15
Kerbal X #2)" and whose admissibility doc quotes this recording's own ecc and sma
- and no committed lane has ever driven it. `V25T` / `V25K` / `V25W` are reserved
alongside and unused.

THE B-RANGE ROSTER, because it is now full enough that the next author cannot
pick a free id by eye: **B27** G1 (`B27-station-route`), **B28** G2 moon-to-parent
(`B28-laythe-jool-return`, FLOWN 2026-08-20 and committed), **B29** G2
planet-to-Kerbin (`B29-jool-kerbin-return`, AUTHORED 2026-08-26 and committed,
NEVER FLOWN), **B30** G4 (`B30-mun-minmus-transfer`). **B31 remains reserved for
a Kerbin -> Duna SETUP flight, DEMOTED from "the lane B29 needs" to a
when-wanted breadth point.** **B32** G10 (`B32-interbody-route`, a
harvest-plus-builder stamp like B27, not a flight).

**THE B29 / B31 ENTRY IS REWRITTEN AS OF 2026-08-26 AND THE DELETED VERSION'S
FINDING IS KEPT, because it was right about the arithmetic and wrong only about
what follows from it.** It read: B29 departs a Duna-orbit fixture, every
committed Duna-parked fixture carries the DD1 probe with only ~1,180 m/s -
short of a Kerbin capture - so B29 needs a subject flown to a Duna park with
enough margin to come back, and B31 is that setup lane, ranked ahead of B29.

THE ARITHMETIC IS CONFIRMED AND SHARPENED, derived from the fixture's own bytes
rather than from memory: `duna-park-probe`'s DD1 probe carries **1,097.5 m/s**
(dry 1.6300 t; LiquidFuel 56.214143897950763 + Oxidizer 68.706157991791599 =
0.62460 t; LV-909 at the 345 s vacuum Isp its stock cfg declares) against
**1,645 m/s** needed - 585.0 for the ejection from its 718 km park plus 1,060.5
for a Kerbin capture-and-circularize - i.e. ~550 m/s short, 50% over budget, and
STRUCTURAL rather than tunable (a lower-periapsis two-burn Oberth ejection saves
12 m/s; the probe has no heat shield and there is no aerobrake verb).

WHAT DOES NOT FOLLOW IS THAT B29 MUST WAIT ON B31. A sweep of every committed
fixture for a craft ORBITING a planet found `jool-park-nerv` - already
Parsek-stripped, already crewed, already carrying a heat shield and chutes, and
carrying ~3,967 m/s against the 2,506.6 a Jool -> Kerbin return costs. **Jool and
Duna are both direct children of the Sun, so Jool -> Kerbin and Duna -> Kerbin
are the IDENTICAL sibling-planet inbound relation** and the class G2 wants
measured is unchanged. B29 is therefore re-scoped to `B29-jool-kerbin-return`
and costs no setup flight at all. B31 stays reserved so the id cannot be reused,
and is now a G9-style when-wanted breadth point - the Duna-origin instance of a
class B29 already measures - rather than a blocker for anything. Nothing for B31
is built.

**G1 - Route-driven rendering.** One committed SAME-BODY supply route over the
BDOCK station fixture, driving a real looped ghost (`B27-station-route`, lanes
V18M/V18T). It ranks first because one lane measures five unmeasured things at
once: the route front door (`SelectGhostDrivingBackingMissions` ->
`BuildMission` -> the host union), the `RouteStatusPolicy.GhostDriving`
suppression gate, the route-owned cadence (`RouteLoopClock`,
`DispatchInterval`, the floored anchor), the ROUTE OVERVIEW LINE (registry D10
`route-map-lines`, zero declarers today), and the dock/station endpoint.
Career-vs-sandbox also becomes load-bearing here for the first time, through
the `IsCareer && IsKscOrigin` funds check. WHAT THE ARRIVAL TRUTH ACTUALLY IS,
because it is easy to over-claim: `MovingTargetStationApproach` is DEFINE-ONLY
and fail-closed-to-faithful in v1 - its own header says the type "is never
handed to the live draw spine in v1" - and `FailClosedClassifier` routes a
live-vessel arrival anchor to `moving-target-station` / `FaithfulFallback`
(documented and log-pinned by `WarpThroughInteriorGapSpineInGameTest`, which
RECORDS `HoldPhase-producer=VACUOUS-under-flag-ON` rather than asserting it, so
the pin is a grep-checkable claim rather than a gate). So this lane's arrival
truth is THE FAITHFUL FALLBACK AT A STATION ENDPOINT - a deliberate policy
worth pinning as such - and NOT "the ghost renders at the station's current
position". The catalog's Tier-4 intent S4.4 (station rendezvous phase-locked
loop) is this lane; track it here, not under both ids. G3b - the surface
`RouteEndpointResolver` nearest-vessel fallback - is DEFERRED INTO THIS
ENTRY: it is route front-door work needing the driver, the status gate and
the route clock this lane stands up, so it rides G1 rather than a mission
loop.

**AMENDMENT 2026-08-26: B27's SUBJECT IS A HARVEST, NOT A FORGE OVER THE BDOCK
FIXTURE.** The paragraph above says "one committed SAME-BODY supply route over
the BDOCK station fixture", and that path is closed by this entry's own header:
route candidacy is gated on `IsTreeFullySealed`, and BOTH verbs that could
satisfy it - `SealSlot` and `RouteCommand` - were RESERVED command-seam verbs
(H35 ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH) when this was written. No driven
run could create a ROUTE at all, so a forge over BDOCK could not produce the
subject. (BOTH VERBS SHIPPED 2026-08-30 - the capability exists now; this
amendment stands as the record of why B27's subject is a harvest, and a forge
variant is a new piece of work rather than a re-reading of this one.) The
verb-free path is the `duna-one-recorded` provenance class - harvest a save an
operator already flew - and that is what was done: `fixtures/saves/
depot-route-recorded`, harvested from the operator's own free-play sandbox save
`orbital supply route DELIVERY test` and finished by
`harness/tools/build_depot_route_recorded.py`. It carries ONE ROUTE
(`5420f805...`, `status = Active`, `completedCycles = 1`, SameBody
Kerbin -> Kerbin, DockingPort STOP onto the `Depot`), two whole recording trees,
22 recordings, and reads GREEN under `analyze-recordings.ps1 -FailOnRed
-FreshSaveGate`.

Two consequences for whoever picks this up. **B27 is a FORGE-CLASS STAMP, not a
flight**: the id now names a tool plus its drift test
(`harness/lib/test_build_depot_route_recorded.py`), and the FLIGHT variant -
a route created in-run through the seam - stays DEFERRED behind `SealSlot` /
`RouteCommand`. Do not book B27 as an unflown flight; do not renumber it when
those verbs land, extend it. **The five things G1 measures are unchanged and
still unmeasured** - they now hang off V18M/V18T over these bytes rather than
off a forged fixture. Registry D10 `route-map-lines` stays UNDECLARED until a
GATING token earns it (H35 CLAIM-IS-NOT-GATE); a lane that merely draws a route
line without asserting one does not get to declare the dimension.

**V18T CAN FLY FIRST, and that is a measured fact rather than a preference.**
`RouteTrajectoryLineRenderer.DrawAll` has exactly one production call site -
`GhostTrajectoryPolylineRenderer.Driver`'s `Camera.onPreCull` hook
(`Display/GhostTrajectoryPolylineRenderer.cs:3894-3906`) - and its complete
guard chain is `PlanetariumCamera.fetch != null && cam == PlanetariumCamera.Camera`,
`scene is TRACKSTATION or FLIGHT`, and a per-frame de-dupe. There is NO
`MapView.MapIsEnabled` on that path, in the host (`[KSPAddon(Instantly, once)]`
+ DDOL) or inside `DrawAll` (whose only gate is the `showRouteLines` setting,
default true). The GHOST polyline pass is the one that is map-gated, at
`:4014`, one structure apart. So a TRACKSTATION route lane needs no
`EnterMapView` verb; that verb is owed only by a lane that also asserts GHOST
polyline facets.

**AND IT HAS BEEN AUTHORED (2026-08-26): `V18T-depot-route-ts-arrival` IS G1's
FIRST LANE.** Never flown, reading run pending; spec header and status row carry
the detail. Three things about it are worth reading here rather than there,
because they are decisions about the GAP and not about the lane.
(a) **It arms no mission loop.** The route drives - `SelectGhostDrivingBackingMissions`
-> `RouteBackingMission.BuildMission` -> the TS host union, which
`GhostMapPresence.BuildStartupTrackingStationLoopUnits` folds in before the
one-shot startup create. Arming a mission would measure the mission path and
call it the route front door.
(b) **Of G1's five named things, ONE and a HALF are gated on flight 1.** The
front door and the `GhostDriving` suppression gate are gated, three ways
(`RevalidateSources ... routes=1 transitioned=0`, `ghostDriving=[1-9]`,
`routeMissions=[1-9]`) - and the `transitioned=0` half is the load-bearing one,
because the realistic way this whole lane goes green-and-empty is the LOAD-TIME
OPTIMIZER moving a `startUT` on one of the four ROUTE `SOURCE` recordings and
flipping the route to `SourceChanged`, which never auto-recovers. The route
overview line is MEASURED but NOT gated (the bare
`[expectations.renderComposition]` block records `routeLineBuilds`, which would
be its first non-zero reading anywhere). The route-owned cadence is READ only:
the spec derives THREE candidate phase-anchor branches from the committed bytes
and finds they cannot be separated without a flight, so it pins no cadence token
and writes the calibration recipe instead. The dock/station endpoint is
partially read.
(c) **D10 `route-map-lines` is still UNDECLARED**, exactly as the amendment
above requires. It gets declared in the commit that arms `routeLineBuilds`,
citing the run - not in the commit that first draws a line.

**G2 - Return legs (moon -> its parent; planet -> Kerbin).** A supply run is a
round trip and every committed loop subject is outbound. The return direction
also delivers "Kerbin as a destination" - a body-frame arrival at the one body
every route network touches - and it is the ONLY thing that activates the KSC
render host, whose Kerbin gate makes it vacuous on every outbound lane.
Cheapest representatives, both reusing committed fixtures:
`B28-laythe-jool-return` (depart `laythe-park-nerv`, park in Jool orbit - a
one-burn escape, no transfer planner involved) and `B29-jool-kerbin-return`
(depart `jool-park-nerv`, capture into a Kerbin ellipse). **THAT SECOND NAME AND
ITS PARK SHAPE BOTH CHANGED ON 2026-08-26** - it was `B29-duna-kerbin-return`,
departing a Duna-orbit fixture and returning to LKO - and the reasons are the
B-range roster's re-scope entry above (the Duna fixture is ~550 m/s short, and
Jool -> Kerbin is the identical sibling-planet inbound relation) plus a
margin descope recorded in the spec: circularizing at the Kerbin arrival
periapsis costs 1,926.12 m/s against a 6,000 km-apoapsis ellipse's 1,188.07, and
**what this gap's lanes read off the product is a KERBIN-FRAME ARRIVAL, not an
altitude**, so the ellipse costs the measurement nothing. Routing is genuinely open: nobody
has measured whether the same-parent classification and the re-aim classifier
treat the INVERTED direction symmetrically, and the mirror-direction lesson
(PRs #1474/#1475) says walk the mirror rather than assume it. Lanes:
V19M/V19T over B28's recording, V20M/V20T over B29's, plus **V20K** - the KSC
host lane, reserved here by the correction below. Confirmation:
destination-frame render tokens at derived epochs on the flight map, the TS,
and the KSC host; armed, with a control that inverts a render token.

**STATUS 2026-08-21: HALF CLOSED. DO NOT BOOK THE GAP.** The MOON-TO-PARENT half
is done and meets the confirmation bar in full: `B28-laythe-jool-return` flew
green on its first attempt (`2026-08-20_2330`) and was harvested as
`fixtures/saves/jool-return-recorded`, and the `V19M`/`V19T` pair over those bytes
is LIVE-PROVEN AND ARMED on BOTH the flight map and the Tracking Station, each
half with its OWN negative control inverting a required RENDER token
(`2026-08-21_0855` and `_0858`) - the first discharge of criterion (b) in this
program. THE ROUTING CAME BACK **R3, FAITHFUL**: Parsek neither re-aims nor
phase-locks a moon-to-parent return, it replays verbatim. This entry called that
routing "genuinely open" and it is now measured, INCLUDING THE ASYMMETRY THE
MIRROR-DIRECTION LESSON WARNED ABOUT: the decline that fires here is one no
outbound subject can reach, because Jool is a strict ANCESTOR of Laythe, so the
Jool park satisfies `helioIdx`, the missing-heliocentric-leg decline every prior
V lane printed cannot fire, and the arrival scan instead finds no body that is
neither Jool nor Laythe. Per-lane detail lives in `autotest-status.md`.

**UPDATE 2026-08-27: THE PLANET-TO-KERBIN HALF IS CLOSED FOR PRODUCTION ON THE
FLIGHT-MAP AND TRACKING-STATION THIRDS. THE KSC THIRD IS NOT, SO G2 IS STILL NOT
BOOKED.** `B29-jool-kerbin-return` FLEW GREEN on its third attempt (flight 3,
2026-08-27, PASS attempt 1, MISSION-OK) after two INVALID calibration reads and a
re-scope onto a two-stage parent relay, and was harvested as
`fixtures/saves/kerbin-return-recorded` - **the first recording in the corpus
that arrives at Kerbin from another planet**. The `V20M`/`V20T` pair over those
bytes is LIVE-PROVEN AND ARMED and completed the full discipline the same day:
readings, an armed re-flight each, and **its OWN per-lane negative control each**,
both red on exactly one inverted RENDER token with every other verifier row -
including the now-gating `saveParse` - green. That pairing is what proves the red
is on the render pin rather than on the evaluator, and it is the second discharge
of criterion (b) in this program after V19. Per-lane run chains, verdicts and
arming payloads live in `autotest-status.md`; they are not repeated here.

THE ROUTING CAME BACK **R3, FAITHFUL**, as it did for the moon-to-parent half -
but by a DIFFERENT and deeper door, which is the class fact this entry was
ranked for. This is the first committed subject whose ARRIVAL SCAN SUCCEEDS: the
classifier walks all the way past the direct-child, cross-parent and multi-hop
guards and the transfer-run reconstruction, and declines only at the
partial-transfer departure gate, on a string no committed lane had ever printed
(`transfer departs from a heliocentric parking orbit or mid-course correction
(deferred); staying faithful`). So the inverted direction is confirmed faithful
at BOTH of its representative classes, by two different mechanisms.

**THE TWO CONFIRMED HOSTS ARE CONFIRMED ON DIFFERENT LENSES, AND THE ASYMMETRY IS
A MEASUREMENT RATHER THAN A CONVENIENCE.** The TS third is discharged on a
RENDERED-FRAME token (a ghost proto materialized in a Kerbin body frame in the
tracking station). The flight-map third is discharged on a ghost-proto
CREATION-frame token plus a seed-side arrival token, because that host's proto
ORBIT-LINE lens was measured to report the recording's FIRST segment for every
proto regardless of what frame the proto was created in - across three runs, two
deliberately different jump orders and four long dwells, including a run that
contained a Kerbin-CREATED flight proto. **That is an instrument/host limitation,
not a lane defect and not a product bug**, it is filed report-only on
`docs/dev/todo-and-known-bugs.md` ->
TIMEJUMP-CANNOT-OBSERVE-LIVE-FRAME-OVERLAP-PROTOS-ON-LONG-PITCH-SUBJECTS with no
mechanism claimed, and it would only become answerable if the M-A2 seam grammar
ever gains a WARP verb (it has none, and inventing one to make a pin reachable is
exactly what a reading round must not do). **DO NOT READ THE MATRIX'S CLASS-LEVEL
`YES` AS "both hosts on the same lens".**

**WHAT REMAINS FOR G2, AND IT IS ONE THING:** `V20K`, the KSC host lane over
these same bytes, per the correction below. `B31` IS NOT AHEAD OF IT - the
re-scope removed the Duna-origin dependency entirely and B31 is now a when-wanted
breadth point (see the B-range roster above).

**CORRECTION TO THIS ENTRY'S KSC PREMISE (2026-08-21) - AN INSPECTION, NOT A
MEASUREMENT.** The sentence above claims the return direction "is the ONLY thing
that activates the KSC render host". That does not survive a read of the code.
Dimension 3 of this section cites the per-point Kerbin gate in
`ParsekKSC.Playback.cs`, but there is a STRICTER gate one level up:
`ParsekKSC.IsKscStructurallyEligible` (`Source/Parsek/ParsekKSC.cs:1483-1490`)
rejects a recording outright on `rec.Points[0].bodyName != "Kerbin"` - the
recording's FIRST point, not its arrival body - and the Update loop
(`ParsekKSC.cs:326`) continues past an ineligible recording, logging nothing
unless it has a leftover ghost to destroy. **BOTH cheapest representatives named
above are rooted at a foreign body**: B28's harvested recording starts at Laythe
(its `.prec` string table carries Laythe and Jool and ZERO occurrences of
Kerbin), and B29 starts at JOOL since its 2026-08-26 re-scope - it would equally
have started at Duna before it, so **the re-scope changes nothing about this
correction**: both candidate origins are foreign and the gate reads the first
point either way. So a return leg that ARRIVES at Kerbin may
still be excluded from the KSC host whole, and this entry's KSC payoff is
UNPROVEN rather than delivered. Two consequences to carry forward. (1) The
V19M/V19T pair discharges the flight-map and Tracking-Station thirds of the
confirmation bar above and NOT the KSC third; do not book G2 closed on two green
V19 runs. (2) **This is a code reading and no KSC lane has flown**, so under
confirmation criterion (c) it may NOT be written up as a documented limitation
anywhere - not here, not in a spec, not in a status row. `V20K` (over B29's
Kerbin-arrival recording) is where it gets MEASURED, and only that run can
convert this paragraph into either a closed payoff or a cited limitation.
**AMENDED 2026-08-27: THE RECORDING NOW EXISTS AND THE QUESTION IS SHARPER, NOT
ANSWERED.** `kerbin-return-recorded`'s first POINT reads `body = Jool`, so the
outright-rejection gate above still looks like it fires - but this is also the
FIRST recording in the corpus that has Kerbin-bodied points at all (56 in its
final section), which is a genuinely different input to the per-point playback
gate than B28's zero-occurrences-of-Kerbin subject presented. That difference is
exactly what `V20K` measures. Criterion (c) is UNCHANGED and binding: until that
run exists, nothing about the KSC host may be written up as a documented
limitation here, in a spec, or in a status row - and the V20M/V20T specs and
status rows have been held to it.

**G3 - Surface endpoints.** Every committed loop lane ends at an ORBIT. A loop
whose recording ENDS LANDED OR SPLASHED exercises a different render stack, and
the product's v0.10.1 claim that "looped landings after destination parking
render connected" has never had a lane. THE GAP SPLITS IN TWO, and the split is
load-bearing because the halves have different owners:

- **G3a, the MISSION-LOOP form - CLOSED 2026-08-24.** All five lanes
  (V22M/V22T/V22K over a Kerbin surface arrival, V23M/V23T over a Mun landing)
  completed the reading -> armed -> per-lane render-token control discipline in
  one day. THE MEASURED CLASS ANSWER moved the lens model: a landed-terminal
  loop member gets NO map/TS proto at ANY epoch (deliberate policy - see
  LANDED-TERMINAL-LOOP-HAS-NO-MAP-PRESENCE-OUTSIDE-THE-FLIGHT-SCENE), so the
  class's lenses are the FLIGHT-scene mesh lifecycle, the TS init-walk hidden
  declaration in value form, and the KSC surface-resolution line - V22K, the
  first KSC lane ever flown, answered the surface-resolution question YES at an
  in-window landed epoch. The pre-flight lens sketch below is KEPT as the
  record of what inspection predicted and flight refuted.
- **G3b, the route-level endpoint resolution** - `RouteEndpointResolver` prefers
  the recorded PID and falls back to ONE nearest compatible stock vessel within
  `RouteOrchestrator.SurfaceProximityRadiusMeters` = 500 m. That fallback is
  headlessly unit-tested (`RouteEndpointResolverTests`) and reached transitively
  by a delivery test, but no loop, map or render lane has ever exercised it, so
  "the ghost renders at the SUBSTITUTED endpoint" is untested at the surface.
  This is ROUTE FRONT-DOOR work and it is **DEFERRED WITH G1**: it needs the
  route ghost driver, the status gate and the route clock that G1 stands up
  first, and measuring it through a mission loop would measure the wrong door.

**WHAT THE LENS IS FOR THIS CLASS, per confirmation criterion (a).** Below
atmosphere there is NO CONIC, so the proto orbit line is NOT the lens - pinning
`surface=ProtoOrbitLine ... body=<destination>` on a landed subject is the V17M
mistake in a new costume. The destination frame is carried by (i) the OWNED
DESCENT POLYLINE (`GhostTrajectoryPolylineRenderer`, the
`TracedPathTreatment.TryDrawOwnedLeg` / `ShadowRenderDriver` shadow-drive pair),
(ii) the SUPPRESSED-ICON marker fallback (`ghostsWithSuppressedIcon` /
`IsIconSuppressed` - the only marker signal for below-atmosphere and no-bounds
ghosts), and (iii) the terminal state itself (`Landed` / `Splashed`). Tier-C
`rigid-seam-tangent-discontinuity` raises at the owned descent draw
(tracing-gated, once per onset) and is in the GATED anomaly set, so a lane
shipping `allowedAnomalies = []` should expect to read it.

**THE ICON-SUPPRESSION REASON TOKEN IS BODY-DEPENDENT, and getting it wrong
makes a pin structurally unsatisfiable.** `belowAtmosphere` is
`body.atmosphere && orbit.altitude < body.atmosphereDepth`
(`GhostOrbitLinePatch.cs:903-906`), so an ATMOSPHERIC arrival (Kerbin, Eve,
Laythe, Duna) suppresses under `reason=below-atmosphere` with
`belowAtmosphere=True`, while an AIRLESS one (Mun, Minmus, Vall, Ike, Gilly)
never takes that branch at all and suppresses under `reason=polyline-owns-phase`
with `belowAtmosphere=False`. A spec must pin the token that matches its body.

**NO NEW B IDS ARE NEEDED, and that is the cheapest thing about this gap.** Both
subjects come from B lanes that are already committed and already LIVE-PROVEN;
what is missing is not a flight capability but a HARVEST - no landed or splashed
save has ever been harvested into a committed fixture, while fourteen orbit and
transfer fixtures have.

- **V22M/V22T/V22K** loop `kerbin-splashdown-recorded`, harvested `--keep-parsek`
  from `B4-reentry-splashdown` (live-proven 2026-07-20, deterministic SPLASHED
  terminal). Routing is itself a reading: a LAUNCH-BODY-ONLY loop has no
  same-parent target and no transfer run for the classifier, so the plausible
  outcome is FAITHFUL FIXED CADENCE and the lane gates on nothing there. Two
  shape dimensions move at once (surface endpoint AND a multi-recording debris
  tree), which the induction caveat asks to be pre-registered rather than
  avoided, because no single-recording landed subject exists to avoid it with.
  **V22K IS THE FIRST COMMITTED KSC LANE - AND, ON G2's OWN CORRECTION, THE ONLY
  ONE PROPOSED SO FAR WHOSE SUBJECT CAN CLEAR THE STRUCTURAL GATE.** `V20K` is
  reserved ahead of it and neither has flown, so "first" here means committed and
  flyable rather than first reserved. The correction above establishes that
  `IsKscStructurallyEligible` rejects on `Points[0].bodyName != "Kerbin"` - the
  recording's FIRST point - and notes that BOTH G2 representatives are rooted at
  a foreign body (B28's recording starts at Laythe; B29's starts at Jool since
  its 2026-08-26 re-scope, and would have started at Duna before it),
  so V20K may be excluded from the host WHOLE and its KSC payoff is unproven. A
  Kerbin ascent-to-splashdown subject is rooted at Kerbin and stays Kerbin-frame
  END TO END, so it clears both that gate and the per-point one. That makes this
  lane the cheapest available MEASUREMENT of the paragraph above - which, per
  criterion (c), may not be written up as a limitation anywhere until a KSC lane
  has actually flown. Its plumbing
  already exists and did not wait on G2: R12's `LoadGame scene=spacecenter`
  (`TestCommandLoadGame.RequestedBootScene.SpaceCenter`), used by exactly one
  committed spec before this (`H34-logistics-inter-body.toml:84`), with
  `ParsekKSC.cs:282` driving the same `DriveMissionLoopUnits` seam as the other
  two hosts. The reason it is reachable NOW is narrower than "the host was
  vacuous", and the loose version is wrong: there are TWO Kerbin gates and they
  differ. `IsKscStructurallyEligible` (`ParsekKSC.cs:1483`) admits a recording on
  `Points[0].bodyName == "Kerbin"` - the FIRST point only, terminal state not
  consulted - so a Kerbin-LAUNCHED outbound recording is eligible and its ascent
  leg renders. It is the per-frame POSE resolvers that gate EVERY point, so what
  an outbound subject loses is not the host but the ARRIVAL: the per-point gate
  discards exactly the epoch a loop-render lane measures. A Kerbin-frame SURFACE
  subject is the first whose ARRIVAL this host can render at all, so the KSC
  coverage the definition of done requires arrives here rather than with the
  return legs.
- **V23M/V23T** loop `mun-landing-recorded`, harvested from `B13-mun-landing`
  (live-proven full PASS on flight 1, 2026-07-25). **THE MISSION LIBRARY ALREADY
  LANDS**: `landingEnabled` is a flag-gated, inert-by-default phase driving
  MechJeb's `LandingAutopilot.LandUntargeted` through
  `mission_runner._perform_land_untargeted`, verified against the installed
  darchambault KRPC.MechJeb v0.8.1 pin - so this lane costs a re-fly and a
  harvest, not a new mission mode. Kerbin -> its own moon is the PHASE-LOCK road
  measured five times over, and V6M/V6T are Kerbin -> Mun exactly, so V23 is V6
  WITH THE ENDPOINT MOVED FROM ORBIT TO SURFACE - the clean single-dimension
  extension, and its own A/B control. NO KSC HALF - and for the narrow reason
  above rather than the loose one: the recording launches from Kerbin, so the KSC
  host admits it and renders its ascent leg, but the per-point gate skips every
  Mun-frame sample, which is the whole descent and touchdown. The ARRIVAL is what
  that host cannot show, and an arrival lane is what a KSC half would have to be.

Confirmation: destination-frame render tokens at epochs derived from the MEASURED
loop clock, on the flight map, the TS, and - for V22 only - the KSC host; armed,
with a control that inverts a REQUIRED RENDER TOKEN.

**THE CONTROL RULE G2 ESTABLISHED APPLIES HERE IN FULL, AND IT IS PER-LANE, NOT
PER-PAIR.** Criterion (b) was first discharged by the V19 pair on 2026-08-21,
each half running its OWN control on its OWN lens (`_0855` red on
`surface=ProtoOrbitLine .*body=Vall`, `_0858` on `surface=ProtoIcon ... body=Vall
scene=TRACKSTATION`) rather than one shared inversion for the pair, BECAUSE the
two halves pin different lenses and a shared control would have proven exactly
one of them. G3a's lanes pin lenses that diverge FURTHER than V19's did - the
flight and TS halves share the polyline and suppressed-icon pair, but V22K's
lens is a KSC pose resolution that exists in neither - so each of the five lanes
owes its own control. A shared one would leave the host nobody has ever gated
ungated.

**G4 - Second moon-to-moon point: Mun -> Minmus** (`B30-mun-minmus-transfer`,
lanes V21M/V21T). Confirms H3 is a property of the CLASS rather than of the
Jool system, at a fraction of V17's cost (minutes-scale transfers, existing
craft, and the Parsek-stripped derived-fixture recipe - see the B23 and B24
rows in `autotest-status.md` for the recipe and the failure it was built to
prevent). Mun and Minmus ARE sibling moons under Kerbin, so the subject also
replicates the FAIL-CLOSED NESTED-SOI policy at a second parent - the trigger
is the sibling relation, not the seam count. Expected routing: identical to
V17 - the relay coast will again miss the whole-revolution conjunct unless
deliberately flown to close a revolution, and that variant is G7, not this
lane.

**STATUS 2026-08-24: THE SUBJECT IS FLOWN; THE ROUTING READ IS NOT.**
`B30-mun-minmus-transfer`, `b30_mun_minmus` (+ schema + unit cells), the
Parsek-stripped `fixtures/saves/mun-park-kerbalx` and the `V21M`/`V21T` pair are
committed; the only `mlib` change the lane needed is one cited
`STOCK_BODY_GRAVITY` row for Mun. **B30 FLEW GREEN ON ATTEMPT 1** (run
`2026-08-24_1536`, mission wall 2,320 s, the full twenty-phase chain through
ORBIT-COMMITTED with all eight assertions met) and its product is committed as
`fixtures/saves/mun-minmus-recorded`; the V21 pair is RE-PINNED off that
fixture's real bytes and both `PENDING_FIXTURE_LANES` entries are retired.
**AND THE V21 PAIR HAS NOW READ IT: H3 REPLICATED AT A SECOND PARENT.** V21M
(`2026-08-24_1639`) and V21T (`2026-08-24_1642_a2`) both measured `reaimed=False`
x41 with `MissionLoopUnit: ... not re-aim (no member yields a re-aim transfer);
faithful` and `PhaseLock SKIPPED ... support=UnsupportedCrossParent` - so the
research doc's section-11.3 statement that the mechanism belongs to the FLIGHT
PROFILE rather than to the pair is a MEASUREMENT at a second parent rather than a
prediction, and the `IsHeliocentricParkingDeparture` door B30's lane left open did
NOT open at Kerbin. **THE GAP NEVERTHELESS STAYS OPEN UNTIL THE PAIR IS ARMED AND
ITS TWO NEGATIVE CONTROLS ARE DISCHARGED**: V21T is green and armable but owes a
re-fly at a jump UT that moved 3 s when V21M re-derived off its logged anchor, and
V21M itself red PARSEK-FAIL(expectations) on ONE WORD of one required token (its
shadow pin asked for `treatment=TracedPath` where this subject's hyperbolic Minmus
approach - `sma=-113900 ecc=4.1888` - renders `treatment=StockConic`; 238 measured
lines carry the Minmus frame, zero `TracedPath` lines exist anywhere in the log, and
V21T's log agrees from the other scene). **THAT IS A LENS-VARIANT READING, NOT A
DEFECT AND NOT AN EPOCH MISS** - the coverage program's lens-per-shape criterion
gains a third variant - and the pin is corrected off the measured word. The
class-matrix row above moved to YES on 2026-08-24: both lanes armed off their
green readings, armed re-flights PASS attempt 1 (`_2030`/`_2031`), and TWO
render-token controls red exactly on their inverted tokens (`_2036`/`_2038`),
then reverted - G4 is CLOSED; H3 is a property of the class, measured at two
parents.

**WHAT B30's FLIGHT MEASURED, against the five targets it pre-registered.**
Three landed on their derivation. The escape node read 146.9262076658111 m/s
against a derived 146.93, leaving the BOUND Mun orbit the corrected escape
contract intends (ecc 0.783858 against 0.7836, apoapsis 15.35% past the SOI
against 15.2%); the delivered Kerbin orbit read a = 9.09 Mm, INSIDE the derived
6.41-49.2 Mm band and STILL BOUND, so (1)'s lane-ending parent-escape tail did
not materialise and the deliberate UNDER-sizing was right; and MechJeb's
moon-path `OperationTransfer` PLANNED ON THE FIRST ASK from an ECCENTRIC
(ecc 0.302) Kerbin orbit toward the inclined target, which was the ranked-#1
predicted failure and did not occur.
TWO CAME BACK OUT OF BAND, and both are this entry's own predictions being
tested rather than defects. **(2)'s attribution problem is now concrete:** the
stage-2 node read 212.700 m/s against an 18.8/129.2/186.6 band - 14.0% above the
worst corner - and because the lane changes the parent AND adds an inclined
target in one step, THERE IS NO WAY TO SAY WHICH OF THE TWO THE OVERAGE BELONGS
TO. The spec said so before the flight; the flight has not made it decidable,
and nothing in this entry may later cite 212.700 as a parent effect.
**And the arrival periapsis inverted the finding-16d expectation**, delivering
303,202 m against a 250,000 m request (k = 1.213 at req/SOI 11.12%) where the
corpus's monotone collapse predicted 0.5-0.7. Recorded as a reading with no
mechanism claimed: it is only the fourth point in that regime and it followed a
sign-change rescue.
**AND THE THIRD ESCAPE-BOUNDARY OPTIMIZER DATA POINT WAS NOT COLLECTED.** The
recorder suppressed both SOI boundaries in tree mode and the produced count came
back at 1, but the load-time optimizer pass never ran on the subject (the only
`Optimization pass:` line is `skipped (no recordings)`), and the flight ran with
AUTO-MERGE RECORDINGS ON, under which a count of 1 cannot distinguish 'never
split' from 'split then merged'. That measurement is OWED TO V21's LoadGame.

**WHAT AUTHORING IT ALREADY ESTABLISHED, because it is not what this entry
assumed.** This entry priced G4 as a cheap replication ("at a fraction of V17's
cost ... existing craft"), and the craft and fixture halves of that held. The
PHYSICS half did not: three properties of Kerbin's moons make the lane a
different measurement rather than a re-scaled one. All three are DERIVED rather
than flown, so they are predictions this entry now owns.
(1) **The parent envelope is 12.9x tighter and it INVERTS the escape's binding
constraint.** Kerbin's SOI is 7.01x Mun's orbital radius where Jool's is 90.35x
Laythe's, and Minmus sits at 55.8% of Kerbin's SOI where Vall sits at 1.8% of
Jool's. B26 had to OVER-size its escape (450 m/s against a 347.245 ideal) to
clear a geometric reachability floor; B30 has to UNDER-size its escape (110
against a 142.257 ideal), because an unaimed departure at the ideal puts 4.0% of
its delivered band OUTSIDE KERBIN'S SOI entirely - a lane-ending outcome B26
never had to size against. Same mode, opposite constraint.
(2) **Minmus is inclined ~6 deg** where Laythe and Vall are coplanar, so this
lane unavoidably BENDS the induction caveat's one-dimension-at-a-time rule: it
changes the parent AND adds an inclined target in one step. That is stated in
the spec rather than discovered later, and the consequence is named - a stage-2
node above the derived band cannot be attributed to the parent change alone. It
is unavoidable because a coplanar sibling pair under a second parent DOES NOT
EXIST in the stock system; Kerbin has exactly two moons and one is inclined.
(3) **The pair is NOT resonant**, where B26's sits in Jool's 1:2:4 chain. That
chain put P_Vall and the Laythe-Vall synodic 0.6615 s apart and made V17's two
candidate jump tables identical on cycle 1; P_Minmus/P_Mun = 7.7513, so V21's
seed calibration is genuinely harder than V17's was and a dwell-nowhere first
reading run is the expected outcome rather than an edge case.
A fourth, owed to the V21 lanes rather than to B30: the SEAM MAGNITUDE is WORSE
at this pair, not better - section 5.2's table already carries Mun at 20.25% of
its orbit against Laythe's 13.70%.

**G5 - Moon -> foreign body (Laythe -> Kerbin, or Mun -> Duna).** NOT a
fail-closed measurement: a single-level cross-SOI chain is explicitly
SUPPORTED, so more seams do not buy a policy reading here. Its value is
different and still real - it is the realistic "export from a moon base" route
shape, and it would be the first SUPPORTED chain of three or more cross-body
seams anyone has flown, a length the render path has never been measured at.
Expensive (a full interplanetary flight from a moon origin); schedule after
G1-G4.

**G6 - Planet -> planet not from Kerbin (e.g. Duna -> Jool).** Same
heliocentric-parking class as the Kerbin lanes by inspection of the classifier;
one representative flight would convert the inspection into a measurement. Low
expected information; DEFER until a real route wants it, and EXCLUDED from the
definition-of-done set for that reason.

**G7 - The re-aim road for moon-to-moon.** Currently UNREACHABLE by any flyable
profile (MechJeb refuses moon-parked direct ejections; the parent-relay mode is
two-burn by construction). The cheap unlock recorded in V17M's header - let the
post-escape parent-frame coast close ONE full revolution before stage 2 - is
real but the gate behind it is DEEPER than "one missed conjunct":
`IsHeliocentricParkingDeparture` carries SIX explicit decline branches plus a
tail tolerance comparison, and one of them is a `DefaultKeepRevs` multi-rev cap
scanning EVERY common-ancestor run, not just the transfer's predecessor - so a
LONGER coast can newly trip a decline it does not trip today. The admissible
band is narrow: exactly one whole revolution on every solar run. Measuring it
would exercise a DIFFERENT code path (does the parking-departure exception work
in a moon frame), which is the path inter-moon supply routes would ride if the
product ever wants them re-aimed rather than faithful. This is a PRODUCT
DECISION, not a scheduled follow-up - faithful is a valid answer - and V17M's
posture on it stands.

**G8 - Long-horizon recurrence (cycle 50, not cycle 2), and co-residency.**
Every drift measurement so far is k <= 2 cycles (the census rate-limit work
bought exactly two). A supply route runs for a career's lifetime. Needs
instrument work before lanes: either wall-spaced multi-cycle brackets (cost
linear in cycles) or a sampled far-cycle jump - which crosses many loop
re-arms, and the V17 creation-frame finding says a re-arm-crossing jump is
itself a render state worth measuring, so the instrument and the measurement
compose. THE DONE BAR IS THREE ROADS, not two: one self-overlapping subject,
one phase-locked subject, and one RE-AIMED subject - re-aim recomputes window
spacing per cycle and is what M5 inter-body routes dispatch on, so a
long-horizon reading that skips it skips the road the product actually uses
between bodies. Ghost count / co-residency (N routes rendering simultaneously,
where the boundary-overlap secondary and the overlap soft caps are both
count-dependent) folds in here, since a long-horizon lane already has to hold
many instances alive.

**G9 - Remaining-bodies breadth.** The untouched bodies: Tylo / Bop / Pol (Bop
adds the inclined-and-eccentric MOON target the way Moho did for planets), plus
deep-space return shapes. Breadth work; schedule opportunistically behind
G1-G5.

**G10 - INTER-BODY route composition** (`B32-interbody-route` subject stamp,
lanes `V26M`/`V26T`). G1 stood up the route front door on a SAME-BODY route
(`depot-route-recorded`, V18T armed - the suite's first armed route lane), but
every route mechanism that is route-SPECIFIC in the render engages only
inter-body: `ClassifyRouteScope = InterBody` has never been read live,
`FilterLegsToEndpointBodies` (the ratified transfer-leg DROP - the deliberate
gap in the overview line) has never dropped a leg on a driven run, the
re-aimed route ghost's star-leg conics have never rendered under a route-owned
clock, `DispatchWindowPeriod != 0` synodic cadence is unmeasured, and
RC-ROUTE's endpoint-filter accounting plus RC-COVER's classification of the
ratified transfer gap have never evaluated against non-vacuous data (on a
SameBody subject those clauses are satisfied BY SCOPE). Subject provenance is
the B27 lesson verbatim: route candidacy is seal-gated with no seam path, so
B32 is a HARVEST-plus-builder stamp over an operator free-play save carrying
one Active inter-body route (Kerbin -> Duna class, dispatch-windowed), not a
forged fixture and not a flight. Lanes follow the V18T pattern (declare the
`[expectations.renderComposition]` block, two readings, windows, armed
re-flight, negative control); V26M additionally owes the map-open pair since
it asserts ghost polyline facets. This is the highest-value remaining
composition gap for the 20+-routes product claim: it is the one route shape
players actually run between bodies, and no armed proof of it exists.
Sequencing: behind nothing technically (the M-A7 instrument and the V18T
grammar both exist); ahead of G8's multi-route co-residency, which wants this
lane's subject class as one of its co-residents.

**BLOCKED AS OF 2026-09-02 ON A PRODUCT CHANGE, not on a subject** (feasibility
walk: `docs/dev/research/g10-interbody-route-feasibility.md`; the "behind nothing
technically" sentence above is superseded and kept only so the correction is
legible). `ClassifyRouteScope` reads `Route.DispatchWindowPeriod` as the
authoritative scope flag, and `RouteBuilder.cs:486` hard-codes
`DispatchWindowPeriod = 0.0`. That is the ONLY assignment in `Source/Parsek/`
outside `RouteCodec`'s parse and two hand-built in-game-test synthetics, so NO
production path can mint a route whose scope classifies `InterBody`. A route
whose members span Kerbin and Duna classifies `MalformedMixedBodies` instead and
`ClassifyRouteLineSkip` skips its line, and `FilterLegsToEndpointBodies` - the
ratified transfer-leg DROP this entry is built around - sits behind the same
`DispatchWindowPeriod != 0.0` branch and is unreachable too. Verified against the
operator's own data, not argued: the plain `orbital supply route` save's
`name = Route: KSC -> Duna` (`persistent.sfs:4358`, `status = Active`,
`reaimWindowBasisEngaged = True`) carries `dispatchWindowPeriod = 0`. So the
save this entry assumed exists is the MALFORMED case; harvesting it as B32 today
pins `malformed=1` and nothing G10 wants. Fix first
(ROUTE-INTERBODY-SCOPE-NEVER-REACHABLE in `todo-and-known-bugs.md`), then
harvest, then author the lanes. Nothing else about the entry changes: the render
gap it names is real and gets WIDER once the fix lands, since the malformed skip
is currently hiding it.

**THE OPERATOR SAVE SPECIFICATION for B32** (write it once, fly it by hand; the
seam cannot create this and no driven lane can either, because route candidacy is
seal-gated and the create gate refuses `candidate-ineligible MissingRouteProof`
over every committed fixture - the four that carry a `ROUTE_CONNECTION_WINDOW`
are Kerbin-only, and `duna-park-recorded` / `duna-one-recorded` carry none at
all). Fly it in a SANDBOX save with `autoRecordOnLaunch` on, one save, one
continuous campaign:

1. **Put a depot at Duna FIRST.** Launch a depot craft from the KSC pad, transfer
   to Duna, capture, and leave it in a stable Duna orbit (any altitude; a
   circular park is easiest to redock with). It must carry a docking port and
   spare capacity in a routable resource (LiquidFuel + Oxidizer is the shape
   `depot-route-recorded` uses), and it must have a `ModuleCommand` part so it is
   a vessel rather than debris. Let its recording finish and merge.
2. **Launch the transport from the KSC pad**, in the SAME save, as a fresh
   launch. KSC origin is what clears the M1 workflow gate
   (`RouteAnalysisStatus.UndockedStartOrigin`); do NOT start the transport
   already in orbit or already docked to something.
3. **Carry cargo.** The transport must launch with more LiquidFuel/Oxidizer than
   it burns, so the arrival dock can transfer a positive delivery manifest to the
   depot. Nothing may be harvested en route (that switches the analysis onto the
   harvest-origin path and changes the subject).
4. **Transfer to Duna and DOCK to the depot** with a docking port (a claw works
   but stamps `Grapple`, a different `RouteConnectionKind` reading). While
   docked, **transfer resources FROM the transport TO the depot** through the
   stock transfer UI. That is the delivery manifest; without a transfer the
   window analyses `NoDeliveryManifest`.
5. **UNDOCK** and let the transport fly clear. Dock and undock must BOTH be
   recorded in one continuous session - that pair is the
   `ROUTE_CONNECTION_WINDOW` (`dockUT` / `undockUT` /
   `transferKind = DockingPort`).
6. **Conclude the tree** (return and recover, or end the flight cleanly) and let
   it commit. Then **seal it**: every recording in the tree must reach
   `MergeState.Immutable`, else the create gate answers `tree-not-sealed`.
7. **Create the route** from the Logistics window's "Create Route" over that
   tree, name it, and leave it `Active` with `pauseAfterCurrentCycle` unset. Do
   not run a cycle; a fresh route with `completedCycles = 0` is the cleaner
   subject.
8. **Do not delete anything afterwards** - not the ascent debris, not the
   decoupled stages, not the depot. The route's `CREATION_TREE_RECORDINGS`
   snapshot and the member-body collection both read the whole tree.

What the seam then does over the harvested save: `LoadGame` -> `SetSetting
showRouteLines=true` -> `EnterMapView` (V26M) or a TRACKSTATION boot (V26T) ->
`TimeJump` into the route's dispatch window -> the `renderCompose` manifest step.
What B32's builder asserts is the predicate in the feasibility memo; its last
clause (`dispatchWindowPeriod != 0.0`) is the one the product fix has to make
true, and until it does the harvest reads `MalformedMixedBodies`.

**Cross-cutting instrument, not a gap: M-A7, the render composition manifest +
verifier** (design authority `design-autotest-render-composition.md`; indexed
as V8 of the visual program in `design-testing-unified.md` section 6 and in
Tier 5 above). Every entry in this register confirms a CLASS through required
render-token pins on one lens at derived epochs; M-A7 raises what a
confirmation can SAY: a per-run structured manifest of everything the
composition rendered (plan, chains, dwells, seams, holds, cuts, clock events,
route-line accounting), verified as an accounting proof - coverage cycle
after cycle, every discontinuity matched to its ratified contract, nothing
outside the catalog - through a `renderCompose` chain row on the R9 arming
discipline, across a declared warp matrix. It reserves NO scenario ids here:
composition lanes extend committed V specs with a manifest step and an
`[expectations.renderComposition]` block, and the route surfaces ride G1's
B27 / V18M / V18T (V18T armed 2026-08-26) and G10's B32 / V26M / V26T for
the inter-body shape. Sequencing: independent of G2-G9; its
Phase 3 wants one committed loop lane per road (phase-lock, re-aim,
faithful), all of which exist today, so it can start any time and every gap
entry above gets a stronger confirmation bar once it lands.

### The induction caveat (why classes must be flown, not argued)

The moon-to-moon program is the standing exhibit: THREE render behaviors nobody
predicted surfaced on the first subject of a new shape - the nested-SOI
fail-closed root-frame proto line (which made the lane's original anti-vacuity
pin structurally unsatisfiable), the TS overlap spawn throttle (newest-cycle
first within the per-frame spawn budget, so the arrival-leg instance spawns
LAST; the figure and its call sites are in the V17T header and are not restated
here), and the re-arm creation-frame reversion - which, stated precisely, is a
new VARIANT of an already-ledgered family
(`MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP`), not a new family. None
of the three is a defect; all three would have silently distorted a coverage
claim made by class-induction alone. Corollaries:

- A class is confirmed by a FLOWN representative, never by inspection of the
  routing code. Inspection ranks the queue; it does not close it.
- Extend ONE shape dimension per new lane where possible. V17T changed two at
  once (self-overlap AND nested-SOI), and the icon-off-orbit non-recurrence it
  measured is consequently unattributable - an open reading instead of a
  mechanism.
- A new lane's reading run is EXPECTED to red at least once on an instrument or
  pin miscalibration; that is the discipline working, and the reds are ledgered
  in the spec headers as calibration evidence.

### Definition of done

The objective above is MET when the phase-lock, re-aim and moon-to-moon rows of
the class matrix, plus G1-G5, G8 and G9, each either (a) have a live-proven,
ARMED representative pair on the flight map and the Tracking Station - plus the
KSC host wherever the Kerbin gate makes it non-vacuous - meeting the
confirmation criteria above, or (b) carry a documented product limitation in
`todo-and-known-bugs.md` stating what does not render and why, CITING THE FLOWN
RUN THAT MEASURED IT (the fail-closed root-frame policy is the model: a
deliberate, tested product behavior, not a gap). Three things are excluded and
each says why in its own entry: G6 is a defer, G7 is a product decision, and
the classifier-declined faithful row has no reachable subject. G8's own done
bar is stated in G8 rather than restated here. At that point "supply runs
render accurately for any origin and destination" is a checkable roster
statement rather than a feeling.

---

## Open bugs blocking or degrading the system

Forensics live in `docs/dev/todo-and-known-bugs.md`; this is a pointer index only.

| Item | Effect on this roadmap |
|---|---|
| ~~EVA-4 mission oracle returns MISSION-OK while the kerbal dies~~ (CLOSED by PR #1359, merged 2026-07-27) | Was: anyone reading `results/*_mission.json` alone read that flight as a success, with survival proven only by seam log tokens. Closed on both sides: the canopy-gated standoff completion in `TestCommandEvaExit.cs` (the C# EvaExit verb no longer completes before the observed canopy state allows it) plus the harness-side `missionOutcome` gate (`classify_post_mission_outcome_miss`), which reds a subject death as `PARSEK-FAIL(mission-outcome)` instead of letting a retry discard the evidence. Mission-level verdicts stay HANDOFF-scoped by design (`mlib.MISSION_HANDOFF_CONTRACTS` declares what a mission did not verify); the gate, not the mission JSON, carries survival. `autotest-status.md` already reflects this. |
| ~~`ANOMALY_TOKENS` drift (status doc known-gate 0)~~ (RESOLVED 2026-08-04, branch `arming-sweep`) | Was: `icon-jump` a dead token; nine raised reasons including `icon-teleport` ungated. Closed in two halves: the dead token retired 2026-07-29, then the per-token calls made 2026-08-04 off the real-geometry silence baseline - seven promoted into the gated set (a raise now reds the tracer-armed specs), two kept as declared report-only instruments (`unaccounted-drawn-recording`, `factory-parity`). Known-gate 0 carries the full resolution record. |
| ~~`STOCK_AWARD_PATTERNS` dead against real KSP logs (known-gate 3)~~ (CLOSED: mechanism 2026-07-29, ARMED on CL-2 2026-07-31, re-verified at HEAD 2026-08-04) | Was: `unmatched_captured_awards` captured nothing, making the ledger oracle's independence cross-check a structural no-op. The patterns were rewritten from measured lines (reputation-only, permanently - KSP logs no funds/science award line), and `CL-2-pod-impact-ledger` arms `captureCrossCheck = "gate"` with `utWindow` phase bounds; nine consecutive bit-stable captures archived. Known-gate 3 carries the record. |
| B4 `chuteDeployed` is still a commanded latch (known-gate 7, audit debt) | Same class that let B1 ship four months of green nightlies on a chute that never opened. B4's fixture carries the same `automateSafeDeploy = 0`. Needs its own diagnosis from a B4 recording before anyone concludes either way. |
| INV2 double-cover recorder seam (known-gate 5) | Real Parsek defect, fixed in its own lane. |
| The no-1x-coast certification cannot see coast warp-thrash (known-gate 8) | A real gap in an existing gate. Bounded for now by the machine-side thrash fast-fail. |
| `autotest-status.md` EVA-2 rows contradict themselves | The EVA table says "STILL pending-fixture: `eva2-lko-crewed` does not exist yet" while the section header says all four EVA scenarios are LIVE-PROVEN, Operator item 2 says the fixture was forged and committed, the fixture exists on disk with 7 VESSEL nodes, the spec reads `tier = "daily"`, and `duration.json` carries a measured 57 s run. Not a system bug; a stale doc row that reads as a blocker. Deliberately NOT edited here to avoid colliding with concurrent sessions; filed as a todo. |
| ~~The L6 recover lane's landed dwell straddles the optimizer's 5 s split floor~~ (CLOSED 2026-09-02, branch `l6-dwell-variants`) | Was: `L6-career-same-name-recover` committed 4, then 3, then 4 recordings on one fixture and one DLL, because the second half of its touchdown split (`recoverUT - touchdownSectionUT`) measured 5.34 / 4.82 / 5.88 s against `CanAutoSplitIgnoringGhostTriggers`'s 5.0 s both-halves floor - so no count pin could be exact and the lane could not be promoted. THE FLOOR STAYS (it is the hop guard); the INPUT is now controlled. `science_bench_recover` gained an optional `preRecoverDwellSeconds` (default 0.0 = the pre-change machine, replayed against `origin/main` rather than asserted), L6 declares 12.0 and pins its counts exactly, the new sibling `L6-career-same-name-natural-dwell` keeps the uncontrolled range as an A/B control, and the sub-floor side - which no hold can produce, since a hold only lengthens a tail - is pinned headlessly at the measured magnitudes by `RecordingOptimizerTests`. ALL THREE READING RUNS FLEW 2026-09-02: L6 long PASS with count=4 and every exact pin matched (realized tail 11.84 s, margin +6.84 s - the predicted landedUT-to-section offset was 3.4 s and MEASURED 0.50 s, wrong in the safe direction), the natural-dwell control PASS at a 5.70 s tail (0.70 s above the floor, so the natural band is now four points with one still below), and L3 PARSEK-FAIL on ONE token that is a landing-site biome roll rather than the dwell (L3-CREWREPORT-BIOME-PIN-DEPENDS-ON-LANDING-SITE; L3's timeline is unmoved, measured against the prior recovery run at 0.16-0.20 s). What remains is the long lane's ARMED run. Forensics: L6-RECOVER-DWELL-STRADDLES-SPLIT-FLOOR |
| `harness/fixtures/saves/bdock-station-craft/` is an orphan | No spec LOADS it: no `saveTemplate` points at it. It IS named in a provenance comment at `BDOCK-1-station-interceptor.toml:97` (whose own `saveTemplate` is `bdock-station-pad`), and by `harness/tools/harvest_bdock_station.py` plus the design doc. Decide keep or delete - and if delete, drop that comment reference with it. |
| `S1.5-rewind-loop.toml:3-8` and `S4.1-rewind-merge.toml:3-9` carry a SPACECENTER-host premise contradicted by the LoadRoute contract | Keeps two specs and up to 16 cells off every cadence. R3 settles it. |

---

## The ghost-replay coverage program (GS-4 derivatives)

Added 2026-08-28, after PR #1550/#1553 landed and live-proved the pattern this
section generalizes: fly a profile, commit, REWIND TO LAUNCH
(`InvokeRewindToLaunch`), roll a watcher onto the pad, and WATCH the replay in
FLIGHT VIEW with the armed `ghostLifecycle` evaluator holding every spawned
mesh to a destroy. The load-bearing realization: **GS-4 is a template, not a
lane.** Every profile the mission library can fly is now a ghost-render
subject ON DEMAND - no pre-harvested fixture, no injected recording - because
the subject is produced, committed and replayed inside one run. That is the
capability the V-family never had (every V-lane needs a committed fixture its
producer flew first), and it is what makes the D7 part-event region reachable
at all: a purpose-built craft can fire any visual event on a scripted timeline
and have the REPLAY of that event gated the same day.

Direction-document rules as for the loop-render program: status of individual
lanes stays in `autotest-status.md`; this section owns the taxonomy, the gap
register and the sequencing. These are OPERATOR-tier calibration flights and
rank inside that budget; the standing verdict that the nightly lane does not
grow until the basics are gated stands unamended.

What is confirmed today (the template's own proofs, all 2026-08-27/28):
GS-4 (mesh-lifecycle-derender ARMED: spawn/destroy balance gates as
PARSEK-FAIL(ghost-lifecycle); census 8/8/0 measured four flights running) and
W1 (the same-body 300 km watch-entry boundary as the single measured
variable, far probe drawing the real `max 300km` refusal, near probe entering
at 463 m with the map closed - the flight-view watching rule is pinned by
`exitmapview ok mapOpen=false`). Two refutation lessons are already banked in
those specs' headers and MUST be read before authoring any derivative: the
loaded corpus is not the on-disk corpus (the load-time optimizer splits
fixture flights into chain members; probes target LIVE members, proven by
id-free `camera-live member` tokens), and watch entry races the engine's
first spawn frame (hold-then-retry, never a single eager ask).

### Tier A - direct derivatives, one flight each, no new machinery

1. **GS-6 part-event render sweep.** A purpose-built craft firing every visual
   part event on a scripted timeline - chutes (two-phase AND cut), gear,
   lights, panels/antennas/radiators, fairing, shroud, cargo bays, decoupler
   destruction - flown, rewound, WATCHED, with per-event logContract tokens
   pinned against the REPLAY. The largest empty region on the map: 10 of 16
   D7 cells read UNCOVERED (`chute-cut`, `shroud`, `fairing`,
   `panels-antennas-radiators`, `gear`, `bays`, `lights`,
   `decouple-stage-destroy`, `engine-fx-legacy`, `engine-fx-effects`); one
   flight can claim most of them. Craft note: legacy vs EFFECTS engine FX
   needs both populations aboard (Mainsail/LV-T45 are legacy `fx_*` parts;
   Spark/RAPIER carry real EFFECTS nodes - see the PristinePartFxResolver
   entry in CLAUDE.md). The in-game H37 category tests part-event fidelity
   headlessly; this is its flown-replay sibling and claims the D7 cells H37
   cannot (CLAIM-IS-NOT-GATE: each cell needs its replay-side token).
   **DONE 2026-09-02: `GS-6-part-event-applier-sweep` is FLOWN GREEN AND ARMED.**
   Four flights in one day: `_1420` (revision 1, stock Kerbal X, PASS - 55 applier
   lines, D7 `shroud` found free), `_1505` (revision 2, PARSEK-FAIL - six stale
   vessel-name tokens plus the parts-dropped-at-load craft defect), `_1524`
   (revision 3, PASS 25/25 on the fixed `Kerbal X Sweep` craft) and the armed
   re-flight `_1553` (PASS, 33/33, ghostLifecycle gate live). TWO negative
   controls, both uncommitted and both red exactly where aimed: one flipped
   applier token, and `spawned = {min = 9}` against a census of 8.
   EIGHT D7 cells now gate from applier text - `decouple-stage-destroy`,
   `engine-fx-legacy`, `panels-antennas-radiators`, `rcs`, `shroud`, `gear`,
   `lights`, `fairing` - and the lane found a NINTH family nobody planned for:
   the landing legs' `ModuleWheelSuspension` fires the ROBOTIC family
   (`applied=3`), which is required as evidence but not claimed, D7 having no
   robotics value.
   HOW IT WAS BUILT: PR #1608 gave the applier one grep-stable line per family
   per ghost surface; this lane is GS-4's flight plus an OPT-IN `PART-SWEEP`
   phase (`partSweepSteps`, a closed vocabulary now enforced at ADMIT), so an
   empty list keeps the pre-GS-6 22-phase graph and GS-4 is byte-identical.
   THE DRIVER GAP IS CLOSED: kRPC 0.5.4 exposes `Control.lights` / `.gear` /
   `.brakes` / `.rcs` / `toggle_action_group` plus
   `SolarPanel|Antenna|Radiator.deployed`, `CargoBay.open`,
   `ResourceConverter.start()`/`.stop()`, `Parachute.arm()`/`.deploy()`/`.cut()`
   and `Engine.active` - every family this entry names is a step name.
   STILL OPEN, each with its own entry: `chute-two-phase` / `chute-cut` (aboard
   and armed, but the profile never re-enters -
   `GS6-CHUTE-TWO-PHASE-NEEDS-A-DESCENT-VARIANT`), `bays` (no liftable
   ServiceBay tail - `GS6-CARGOBAY-NEEDS-A-HARVESTED-SERVICEBAY-TAIL`), and
   `engine-fx-effects` (the Ant flew and its ignition is RECORDED, but an applier
   line's `pid=` is the tally's representative rather than an enumeration, so the
   replay cannot be proved per-pid). Five families are out of reach of ANY sweep
   craft and are scoped out rather than owed
   (`GS6-FAMILIES-WITHOUT-A-KRPC-DRIVER`). THREE PRODUCT FINDINGS came out of it,
   all report-only: `GS6-GHOST-HAS-NO-COLORCHANGER-STATE` (which ANSWERS
   `SHOWCASE-COLORCHANGER-APPLY-UNOBSERVABLE`), its sibling
   `GS6-GHOST-HAS-NO-CONVERTER-LOOP-STATE`, and
   `GS6-DEPLOYABLE-NO-RESOLVED-VISUAL-solarPanels5`.
2. **Watch retarget + explosion hold.** D6
   `watch-mode-retarget-explosion-hold` is the value the entire V-family
   deliberately declined ("a measured REFUSAL is not evidence of it"; "an
   ENTRY is evidence of neither"). GS-4 measured `auto-followed during hold`
   once as a side effect; the derivative drives it deliberately: watch the
   CORE CHILD (W1's `index=` targeting makes that reliable) through its
   destruction for the explosion-hold path, or a crash-landing profile whose
   multi-part breakup also claims D5 `crash-coalescing`. The 3-5 s hold and
   its `watch hold expired` / retarget destroy reasons are already in the
   ghostlife vocabulary.
3. **Reentry FX** (D6 `reentry-fx` UNCOVERED): a B4-shaped reentry profile
   rewound + watched, FX arm/disarm lines pinned during the replay.
4. **Zone transitions** (D6 `zone-transitions` UNCOVERED): place the watcher
   so the replay crosses the 120 km visual range; the engine-teardown
   MeshDestroyed emit (added with GS-4's review round) plus the zone tracer's
   torn-down/rebuilt lines make mesh unload/reload assertable for the first
   time. Pair with a ghostLifecycle balance that must survive the round trip.
5. **Debris lifecycle split** (D5 `staging-debris-ttl` /
   `staging-debris-promotion`, both UNCOVERED): GS-4's flight already
   produces the population; a variant asserts the TTL-expiry vs promotion
   fork in the produced save through the saveParse structure block.

### Tier B - the rewind system's own open questions

6. **The vanished RewindPoint.** GS-4 OBSERVED (not gated) the core-discard
   RP authored live (`slots=2 focusSlot=0`) and GONE after the rewind
   (`ReapOrphanedRPs: remaining=0`, saveParse rewindPoints=0) - the rewind
   lands before its branch point. DESIGN CALL FIRST: is a re-fly affordance
   the player still deserves once the replay passes the branch point again,
   or is rewound-out-of-existence the contract? Then a lane pins whichever
   answer, the GS-1/GS-2 both-branches pattern.
7. **Rewind-to-launch x Re-Fly interplay.** Rewind-to-launch on a tree
   carrying supersede rows exercises `DropSupersedesRewoundOutOfExistence`
   and D9 `load-time-sweep` (the dimension's one UNCOVERED cell) live -
   both currently unit-level only. The verb hands the raw HEAD root by
   measured design (splitter Step12); this lane is where that contract gets
   its flight.
8. **Repeat-rewind idempotence.** Rewind, watch to completion, rewind AGAIN
   from the same committed tree. Cheap; proves the `parsek_rw_*` quicksave
   lifecycle is reusable rather than one-shot.
9. **Arm `unityExceptions`** on GS-4 and W1 (`maxTotal` windows) - the NRE
   census is stable at 1-4 stock scene-change lines across four flights.

### Tier C - machinery that raises the ceiling (build before the lanes that need it)

10. **ghostlife v2.** Three additive surfaces, each motivated by a documented
    gap: PER-CYCLE balance for loop playback (the loop demote path emits no
    destroy by design - the evaluator's census caveat says spawnLines vs
    destroyLines must not be read as a leak on a looping lane, which today
    means loops simply cannot arm requireBalanced); per-vessel-name windows
    (`Kerbal X Debris >= 6` is sharper than the census floor); and
    `destroyedReasons.required`. Prerequisite for item 12.
11. **A replay-parity evaluator.** The flight tracer's `AfterUpdate` lines
    already carry `dM=` / `expectedDM=`; a pure evaluator over them turns D6
    `recorded-vs-rendered-parity` into a GATEABLE NUMBER in the flight scene
    - the quantitative sibling of the boolean derender tripwire, and the
    honest home for D6 `attitude-preservation` (UNCOVERED) if rotation
    residuals join the line.
12. **Loop-cycle rendering on the GS-4 subject** (D6 `loop-period-modes`,
    `self-overlap`, `overlap-expiry-soft-caps` - all UNCOVERED): loop the
    committed Kerbal X tree, watch across a loop seam, gate per-cycle balance.
    Blocked on item 10; do not author it against the v1 evaluator, whose own
    caveat says the census misleads under loops.

### Tier D - cheap seam-drivable D1 residue (no mission, step sequences only)

13. `stop-on-switch`, `switch-segment-noop-discard`, `commit-abort`,
    `discard-rollback`, `sub-2-point-drop` - all UNCOVERED, all reachable
    with existing verbs; batch as one authoring pass, one flight each.

Sequencing recommendation, stated once: Tier A item 1 first (largest coverage
per flight), then item 2 (the long-declined D6 cell), then Tier C item 10 +
its dependent loop lane (item 12) as one arc. Tier B rides operator judgment -
item 6's design call costs nothing and should be taken early; Tier D is
filler between calibration flights.

---

## The supply-route coverage program (D10, the rover-route wave)

Added 2026-08-30, after the first SURFACE route subject ever flown: two rovers
at KSC (save `logistics-rover-a`), Runway-origin transport, dock at a landed
endpoint, 97.6 LiquidFuel + two inventory parts across the window, route
`fd6ee2ff` created and Send-Once'd live. That one manual session paid for
itself three times over before any lane existed: it found and same-day-fixed
SENDONCE-BLOCKED-CYCLE-NEVER-PAUSES (a blocked cycle consumed a Send Once but
left the route Active and ghost-looping forever), exposed the
ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT career defect (fixed 2026-09-01,
which is what unblocks Tier C below), and produced the
exact fixture shape `test_hlib.py`'s skip roster names as "a HARVEST
requirement" (the target-branch dock window + cross-tree committed partner
that no orbital fixture can produce, because the two docking craft there are
Kerbal X descendants sharing one baked persistentId).

Direction-document rules as for the other programs: status of individual lanes
stays in `autotest-status.md`; this section owns the taxonomy, the gap
register and the sequencing. D10 is the registry dimension (20 cells, 12
uncovered at time of writing); the Cause-C row `RouteCommand -> D10` above is
what Tier A retires.

Lessons already banked from the manual flight - read before authoring any lane:

- **Cargo is a corner-difference, not an event.** The window's manifests are
  dock-vs-undock snapshots; a transfer-and-transfer-back nets to zero and the
  window then has NO cargo (reject 5). Lanes must move cargo one direction and
  leave it moved.
- **The route is not in the save until the game saves again.** Route creation
  happened after the last `persistent.sfs` write, so the collected save
  carries the trees + the LANDED window but no ROUTES node. A creation lane
  (Tier A2) sidesteps this; a route-READING fixture needs a post-creation
  re-save.
- **Do not pin `interval == cadence`.** The loop clock runs on the
  phase-locked quantized cadence (`QuantizeCadenceToMultipleOfP`, measured
  90.08 -> 95 here); `Route.DispatchInterval` stays raw `N x span`. Two
  numbers, both correct.
- **Send Once is now observable.** The fix wave added grep-stable tokens:
  `ArmedPause:` with `reason=blocked-then-paused`, the delivered/blocked
  toasts, and `TrySendOneCycleNow:` with `PauseAfterCurrentCycle=true`. Pin
  these, not UI state.
- **A destination fills up.** The first delivered cycle consumed the
  endpoint's free inventory slots and the second cycle blocked
  `DestinationFull stored-part:evaScienceKit` - which is not a nuisance but
  the cheapest reproducible route-hold producer we have. Tier A2 uses it
  deliberately.

### Tier A - the rover-route basics (this branch's wave; machinery listed below)

1. **RVR-1 rover fixture reading lane.** ~~Harvest `logistics-rover-a` (the
   established `depot-route-recorded` recipe: generic harvest tool +
   fixture-specific finisher + `RECORDED_FIXTURES` shape pin) and run the
   isolated `Logistics` category over it.~~ **FIXTURE AND SPEC AUTHORED
   2026-08-30, NEVER FLOWN** - fixture `rover-route-recorded`
   (`harness/tools/build_rover_route_recorded.py` +
   `harness/lib/test_build_rover_route_recorded.py` +
   `RECORDED_FIXTURES` row; analyzer Forbid gate reads `RED=0` with NO `.prec`
   repair), spec `harness/scenarios/RVR-1-rover-route-proof.toml`. Predicted to
   un-skip the suite's two never-executed cells
   (`RouteProof_ActiveAsTargetDockWindow`,
   `RouteProof_CrossTreeCommittedPartner`) - the only Logistics skips shared
   by BOTH existing recorded hosts - and both are pinned as REQUIRED cell
   tokens rather than left to the tally. The window is TARGET-branch because
   the two rovers carry DIFFERENT baked `persistentId`s (313889796 vs
   2123618197), which is exactly the harvest H39's roster asked for. THE
   TRADE, recorded rather than hidden: the INITIATOR cell skips here (strict
   complements over a one-window corpus), so the three cells are covered
   ACROSS the family and this lane does not replace H39/H40. Tally split is
   INTERIM (`IsolatedBatchWiringGroupTests.INTERIM_PIN_IDS`) until the first
   census: the host is a 17-part landed rover with TWO inventory containers
   where both other recorded hosts have one, so four of H39's inherited
   run-time skips are re-opened. NO D10 claim yet, on the CLAIM-IS-NOT-GATE
   rule - the surface flavor of `ksc-origin` + `dock-producer` is earned in
   the commit that measures the lane green.
2. **RVR-2 route creation lane - the unlock.** ~~Load the same fixture, then
   drive the seam.~~ **SPEC AUTHORED 2026-08-30, NEVER FLOWN** -
   `harness/scenarios/RVR-2-rover-route-create.toml`: `SealSlot` (no-op
   guard - the fixture is pinned to carry no `mergeState` key anywhere, so
   `remaining=0 alreadySealed=True` is a real assertion) ->
   `RouteCommand action=create` (interval deliberately OMITTED so the driven
   create takes the same `ComputeRootToUndockSpan` default a player create
   takes) -> `RouteCommand action=send-once` + `TimeJump` (delivers: the
   Delivery-write and Inventory-store rows are pinned, with `path=unloaded`
   pinned DELIBERATELY - the endpoint resolves by pid with no loaded gate to a
   vessel 5.4 km away, and the proto-snapshot writers DO deliver) ->
   `RouteCommand action=send-once` + `TimeJump` again (destination now full:
   `BLOCKED kind=DestinationFull` + `reason=blocked-then-paused` + the
   `RoutePaused` marker). The cycle-1-fits / cycle-2-blocks arithmetic is
   DERIVED from the recorded window's own dock/undock resource rows (97.6 LF
   manifest against 102.4 of endpoint headroom) and is gated in `harness/lib`
   so a re-harvest cannot move it silently. First driven route CREATION
   anywhere in the suite. THE ONE UNSETTLED LINK, named in the spec header so
   a first red is diagnosed rather than re-argued: whether an instantaneous
   `TimeJump` past several loop periods produces a dock crossing the
   orchestrator acts on. NO D10 claim yet; `candidate-detection`, `delivery`,
   `resource-cargo`, `inventory-cargo`, `hold-reasons` and
   `destination-full-gate` are earned in the commit that measures it green.
3. **RVR-3 situational in-game category.** ~~New scene-agnostic, batch-safe
   category (RouteRewindTimeline's synthetic-route pattern) driving the
   lifecycle headlessly-in-KSP~~ **CATEGORY SHIPPED 2026-08-30** as
   `RouteLifecycle` (6 cells, `Source/Parsek/InGameTests/RouteLifecycleRuntimeTests.cs`):
   send-once blocked->paused with the kept hold (the live regression gate for the
   blocked-then-paused fix), the arm's own observable transition, the
   pause-while-in-transit provenance resolving on a blocked cycle, the unarmed
   negative control, the live RouteCommand create-gate walk, and the
   deliverable-cycle probe that PINS why no scene-agnostic cell can drive a real
   delivery (the live endpoint resolver refuses every synthetic destination, and
   a real one would mean mutating the player's vessels - that half stays with the
   headless fire tests and the driven RVR-1 / RVR-2 flights). Every cell drives
   the PRODUCTION `LiveRouteRuntimeEnvironment`; no fake env anywhere.
   ~~STILL OPEN: the scenario spec with its own pinned tally.~~ **SPEC AUTHORED
   2026-08-30, NEVER FLOWN** - `harness/scenarios/RVR-3-route-lifecycle.toml`,
   pinning `BATCH_COMPLETE v1 total=6 passed=6 failed=0 skipped=0
   category=RouteLifecycle scene=FLIGHT` over the same `rover-route-recorded`
   host (five cells need only a loaded FLIGHT scene with a live clock; the
   create-gate walk needs COMMITTED trees to walk, and this host supplies both
   in one boot). It declares NO render-composition expectations block, which is
   what leaves `PARSEK_RENDER_MANIFEST` unset so the three ticking cells
   execute - and that absence is GATED in `harness/lib`
   (`RoverRouteSpecFixtureSyncTests`) rather than left to memory. `total=6` is
   attribute-exact and the spec is auto-enrolled in
   `CommittedBatchTallySourceSyncTests`, so a seventh cell reds locally. The
   `passed=6 skipped=0` half is a per-cell prediction: what it cannot settle is
   the five crossing cells' `RequireLivePostponementBlock` / `RequireLiveResolutionBlock` pre-flights, a run-time reading
   of the live environment against a synthetic route. Deliberately NOT added to
   `Logistics` (whose `total=47` is pinned by four committed specs - five as of
   this wave, counting RVR-1).

**Tier A flight census, 2026-09-01.** All three lanes flew twice the same day and are
GREEN on round 2 (PASS attempt 1, every verifier): RVR-1 `total=47 passed=39 failed=0
skipped=8` pinned whole (both debt cells PASSED for the first time in the suite's history),
RVR-2 delivered-then-blocked exactly as derived (cycle 0 `path=unloaded` delivery of 97.6 LF +
2 items, cycle 1 `DestinationFull reason=LiquidFuel` -> `blocked-then-paused`) - the first
driven route creation AND delivery anywhere in the suite - and RVR-3 8/8. Round 1 found no
product defect: one authoring pin (a wheeled Runway rollout is `Landed`, not `Prelaunch`),
one polluted fixture endpoint (repaired builder-side, proven from the flight log's slot
addresses), and one contract drift in the category (cells authored before #1583's
postponement exemption merged in; rebuilt to the shipped contract and grown 6 -> 8 with a real
`DestinationFull` live gate). Tiers: RVR-1/RVR-2 nightly, RVR-3 daily. The B4 subject is now
gated behind the ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE probe (todo) before any flight.

### Tier B - surface-route variants (one flight each, template established by RVR)

4. **Start-docked origin proof.** ~~Record the transport STARTING docked to the
   base rover off-pad, undock, deliver elsewhere.~~ **GATED ON THE PROBE, AND
   THE PROBE IS NOW AUTHORED (2026-09-02).**
   `OriginProofProbe_SettledDockLeavesNoExternalParent` (category
   `RouteDockCapture`, lane H55) couples a spawned `dockingPort2` into the
   active vessel, counts the parts satisfying the producer's OWN predicate
   (`p.parent.vessel != v`) after the couple settles, and reads back whether
   `RouteOriginProof` was captured - logging
   `OriginProofProbe: externalParentParts=N proofCaptured=<bool> ...` and
   asserting NO verdict. `externalParentParts=0 proofCaptured=False` confirms
   ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE (todo, SUSPECTED) and this item
   becomes a bug fix plus a capture cell rather than a flight; anything else
   refutes it and the flight goes ahead as written. Either way the first-ever
   execution of `RouteOriginProof_StartedDockedToNonKsc_ProducerLandsProof` and
   the surface flavor of D10 `docked-depot-origin` still ride whichever route
   the measurement picks. DO NOT FLY THIS ITEM BEFORE READING THE PROBE LINE.
   **FIRST MEASUREMENT TAKEN 2026-09-01 (H55 flight 1), AND IT IS HALF AN
   ANSWER**: `externalParentParts=0 proofCaptured=False situation=4
   outcome=active-vessel-PRELAUNCH partnerPid=108351093`. The zero count
   supports the suspicion, but the pad host is PRELAUNCH and the producer
   short-circuits on that BEFORE walking candidates, so the branch in question
   is still unexercised. Closing it needs the probe on a LANDED host - the
   committed `rover-route-recorded` fixture is one - which is a follow-up lane,
   and until it runs this item stays gated exactly as written.
   **THAT FOLLOW-UP LANE IS NOW AUTHORED AND HAS NEVER FLOWN:
   `harness/scenarios/H56-route-dock-capture-landed.toml`** - the same six
   `RouteDockCapture` cells H55 flies green, over `rover-route-recorded`, whose
   active vessel is `sit = LANDED` (situation 1). The PRELAUNCH short-circuit
   cannot fire there, so the resolver walks candidates for the first time
   anywhere. Read its probe line and nothing else to settle this item:
   `outcome=no-external-coupling` with `candidates=0` CONFIRMS
   ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE and B4 becomes a bug fix plus a
   capture cell; a `Captured` outcome - or a non-zero count landing on
   `PartnerPidZero` / `PartnerPrelaunch` / `PartnerAmbiguous` - refutes it and
   B4 stays a flight as written. The lane's probe token is a VALUE REGEX, not a
   verdict pin, so a green run proves the line was emitted and settles nothing
   by itself; a human reads the numbers.
   **SETTLED 2026-09-02 (H56, `2026-09-02_0545`, PASS): on the LANDED host the probe read `situation=1 outcome=no-external-coupling externalParentParts=0` - the producer is dead code on a settled dock. ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE is CONFIRMED; this item is now a BUG FIX plus a `RouteDockCapture` capture cell, and the manual flight is RETIRED.**
   **FIXED 2026-09-02 (code green, not yet flown).** The producer now builds its
   candidates from the docking node's own docked-partner information
   (`RouteProofCapture.IsSettledDockSeam`: a `vesselInfo` created only by a real
   cross-vessel `DockToVessel`, a non-zero `dockedPartUId`, and a partner part that
   still resolves on the SAME vessel), with the merged vessel as the origin partner;
   the old external-parent reading is KEPT alongside it for the unsettled mirror
   case. The probe cell became the regression gate
   (`OriginProof_SettledDockCapturesProofFromDockingNode`, same category, same count,
   same `OriginProofProbe:` token): it asserts `externalParentParts=0` on both hosts,
   the `active-vessel-PRELAUNCH` skip on H55's pad host, and
   `proofCaptured=True outcome=captured` on H56's landed host. THE NEXT FLIGHT ON
   THIS ITEM IS AN H55 + H56 RE-RUN, not a manual subject. The surface flavor of D10
   `docked-depot-origin` is claimed off H56's post-fix census, and the Tier B item-4
   subject proper - transport STARTS docked, undocks, delivers elsewhere on a landed
   host - is now lane `H57-route-start-docked-origin-landed.toml`, authored and never
   flown.
5. **Ground pickup + mixed direction.** ~~The base loads cargo ONTO the
   **THE LANE, AUTHORED 2026-09-02 AND NEVER FLOWN:** two cells in a NEW category
   `RouteStartDockedOrigin` (a separate category on purpose - `RouteDockCapture`'s
   `total=6` is pinned by two flown-green specs and a seventh declaration would red
   both). The subject docks a self-provisioned partner rig into the active vessel
   with NO recording running, starts the recording docked, undocks, then docks a
   SECOND rig and delivers LiquidFuel across a real window, and after the stop reads
   the proof back off the captured recording asserting a real body name and
   `IsSurface=true`. The negative control docks and UNDOCKS before the start and must
   capture nothing. ONE CAVEAT, recorded in the spec and the todo entry: the cells
   couple with a raw `Part.Couple`, which writes no docking-node bookkeeping, so each
   stamps it from the decompiled `DockToVessel` contract first - half the gate is a
   stock-contract emulation, and what it buys is the whole LIVE producer path. The
   non-emulated confirmation is a real player dock and stays unbought.
   **FLIGHT 1 (`2026-09-02_1005`) PARSEK-FAIL(results), `total=2 passed=1 failed=1`, and
   the failure is the PRODUCT rather than the lane:** the control passed, the subject red
   on reading the proof back off a committed recording, and the caller set says the proof
   never reaches one in always-tree mode
   (ROUTE-ORIGIN-PROOF-NEVER-REACHES-A-TREE-RECORDING). **SO ITEM 4 IS NOT DONE.** The
   producer half is fixed and live-confirmed by H56's post-fix probe
   (`proofCaptured=True outcome=captured`); the persistence half and the partner-pid rule
   (ROUTE-ORIGIN-PROOF-PARTNER-IDENTITY) are open, and no D10 `docked-depot-origin` row is
   claimed until both land and H57 flies green.
   **H57 FLEW GREEN 2026-09-02 (`2026-09-02_1044`, PASS attempt 1, `total=2 passed=2
   failed=0 skipped=0` pinned whole, promoted to nightly).** The subject and its mirrored
   negative control both read exactly as derived, so the Tier B item-4 SUBJECT is now
   produced unattended. **ITEM 4 IS STILL NOT DONE**, and the green census is what makes
   that precise rather than a hedge: the lane proves the PRODUCER end to end and stops at
   `CaptureAtStop`, and the produced save's `ROUTE_ORIGIN_PROOF` count was 0 on H56 and
   H57 alike. D10 `docked-depot-origin` is claimed in the commit that reads a green census
   off a lane whose subject asserts a PERSISTED proof - after the forwarding fix and the
   partner-identity ruling - and not before.
   transport (pickup manifest), then a both-directions window
   (`mixed-direction`). Same two-rover template, transfers reversed.~~
   **AUTOMATABLE: `RouteDockCapture` cells authored 2026-09-02, lane H55 never
   flown.** Two cells -
   `DockCapture_LiveRecordedDockingPortCouple_StampsDockingPortWindowWithDelivery`
   (the delivery half: LiquidFuel AND a stored cargo item transport->partner)
   and `DockCapture_PickupAndMixedDirection_ManifestsBothWays` (LiquidFuel
   partner->transport plus inventory transport->partner in ONE window).
6. **EVA-construction drift, live.** ~~Attach a part to a vessel EXTERIOR during
   the docked window: the `Route window part-set drift on undock` warning path
   has never fired outside unit tests. Report-only lane; pins that the route
   still builds and the moved part appears in NO manifest (the documented
   contract).~~ **AUTOMATABLE: `DockCapture_EvaConstructionDrift_WarnsButRoute-
   StillBuilds` authored 2026-09-02, lane H55 re-fly pending.** The KSP fact that
   shaped it, decompiled rather than assumed: `Part.Couple` fires
   `onPartCouple` UNCONDITIONALLY, so a second couple inside an open docked
   window opens a SECOND route window whose transport pid set already spans the
   partner, and the later undock then fails
   `RouteProofCapture.TryVerifyRoutePartSetsSeparated` instead of completing -
   a drift cell built on `Part.Couple` could never observe the warning at all.
   Real EVA construction does not use it: `EVAConstructionModeEditor.AttachPart`
   ends in `Part.OnAttachFlight(parent)`, which sets parent / vessel and adds
   the part to `vessel.Parts` with NO coupling event. The cell drives THAT.
7. **Multi-stop rover run.** ~~One transport, two landed bases in one recording
   (D10 `multi-stop` - the multi-window `LoopRoute(multi)` path, whose blocked
   branch the fix wave also patched but no flight has ever driven).~~
   **AUTOMATABLE: `DockCapture_TwoPartnersSequential_TwoWindowsOneRecording`
   authored 2026-09-02, lane H55 re-fly pending.** Note the shape the architecture
   forces and the cell pins: each dock opens its own dock-merged CHILD, so two
   stops are two windows on two recordings of ONE tree - which is exactly what
   `AnalyzeTree`'s M4a collection walks, and NOT two windows on one recording.
8. **Round-trip pair.** ~~A->B->A with cargo both legs (D10
   `round-trip-pair`).~~ **AUTOMATABLE:
   `DockCapture_RoundTripPair_SamePartnerTwice` authored 2026-09-02, lane H55
   re-fly pending** - deliver on the first window, pick up on the second, both
   against the same PHYSICAL partner. The cell asserts that sameness on the
   endpoint PART pids rather than on `TransferTargetVesselPid`, because
   `Part.Undock` builds a fresh `Vessel` whose `persistentId` is re-stamped by
   `Vessel.Initialize` - so a re-dock of the same craft legitimately reports a
   different target vessel pid, and any future route lane comparing vessel pids
   across an undock is comparing the wrong thing.

**H55 GREEN 2026-09-01 (run 2, `2026-09-01_2229`, 6/6, re-tiered nightly): B5, B6, B7 and B8 are MEASURED on a driven run - the only Tier B item still owing anything is B4, gated on the probe reading on a LANDED host.**

**RVR-4 GREEN 2026-09-01 (run 3, `2026-09-01_2253`, re-tiered nightly): Tier C item 9 is MEASURED on a driven career run - dispatch cost 7410 (offline derivation confirmed to the unit), the FundsShort hold at shortfall 3820 (live funds are the seed alone: PatchFunds' guarded uplift keeps the ledger's milestone awards out of the live pool on a file-constructed career), recovery credit absent. Round 1 of that lane found the shipped free-dispatch fix insufficient on the real tree (two blockers, both fixed on this branch).**

**What Tier B still owes a flight, after H55.** The lane itself: FLOWN ONCE
2026-09-01 (`total=6 passed=1 failed=5`) and RED on an authoring defect in the
partner rig - it carried no `ModuleCommand` part, so
`ParsekFlight.IsTrackableVessel` classified the undocked half as debris,
`DeferredUndockBranch` returned before `CreateSplitBranch`, and no route window
could ever complete. Every rig is now rooted on a probe core and the re-fly is
pending. THE STANDING LESSON IS WIDER THAN THIS LANE: any self-provisioned
undock subject must be TRACKABLE (SpaceObject, EVA kerbal, or a part carrying
`ModuleCommand`) or it produces no branch and no window completion at all.
Flight 1 did prove the spawn half, the live `kind=DockingPort` classification,
the window capture and both live cargo moves, and the probe cell passed and
measured. And the SURFACE half that was always the
other axis of these items - H55 produces the docked-window behaviour on the KSC
pad host, not on two landed rovers at distance, so the surface flavors of the
D10 rows stay unclaimed until a lane measures them. What H55 removes is the
need to spend a manual flight to see delivery, pickup, mixed direction, drift,
multi-stop and round-trip AT ALL.

**THE LANDED-HOST LANE NOW EXISTS, AND HAS NEVER FLOWN**
(`harness/scenarios/H56-route-dock-capture-landed.toml`, authored 2026-09-02).
It is H55's six cells over `rover-route-recorded` - a 17-part LANDED rover on
the Runway - with the driver, the batch and every token shape taken from H55
step for step, so a census delta is attributable to the host alone. It pays
BOTH halves of what is written above: the probe's non-PRELAUNCH branch settles
item 4, and the five capture cells are the SURFACE measurement the paragraph
above says the D10 rows are waiting on. No D10 row is claimed in the spec -
they are earned in the commit that reads a green census, on the
CLAIM-IS-NOT-GATE rule.

**TIER B ITEM 4 IS CLOSED, 2026-09-02, AND IT COST NO MANUAL FLIGHT.** The
sequence the two paragraphs above predicted ran to its end: H56's probe on the
LANDED host measured `outcome=no-external-coupling` and CONFIRMED
ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE, which turned item 4 from a hand-flown
subject into a bug fix plus cells; the producer was rebuilt on the docking
node's own docked-partner record; lane `H57-route-start-docked-origin-landed`
was authored as the subject and then measured TWO further product defects that
no reading could have settled - the proof never reached a `Recording` in
always-tree mode, and the origin partner was whichever half stock made dominant
rather than the depot. Both are fixed, and the armed census
`2026-09-02_1428` (PASS, `total=2 passed=2 failed=0 skipped=0`, zero FAILURE
SITE lines) plus its negative control `H57-NEGCTL-P6-treeproof-no`
(PARSEK-FAIL(expectation), exactly one unmet token, zero forbids) close the
item. D10 `docked-depot-origin` is claimed off that census in the same commit.

TWO RESIDUES ARE NAMED RATHER THAN QUIETLY CARRIED, both filed in
`docs/dev/todo-and-known-bugs.md`. (a) THE DEPOT MUST BE PLAYER-TYPED:
`Vessel.FindDefaultVesselType()` only ever RAISES a vessel's type to a part's,
and no stock part declares `Base` or `Station`, so an ordinary landed base reads
`Ship` / `Probe` / `Lander` and the rule FAIL-CLOSES to no proof until the player
sets the type in the tracking station - announced once at recording start
(ROUTE-ORIGIN-PROOF-REQUIRES-A-PLAYER-TYPED-DEPOT, with the four rejected
alternatives recorded). (b) DEPOT-SIDE SELF-ORIGIN: a base that starts a
recording with a transport docked to it still records ITSELF as its origin,
because at capture the two cases are indistinguishable; removing it needs
undock-side routing of the proof to the non-origin half, which is also where the
depot's pid would bind (ROUTE-ORIGIN-PROOF-SELF-ORIGIN-ON-A-DEPOT-SIDE-START).
Neither blocks the item: the first is a stated requirement with a discoverable
message, the second is inert until a depot-side recording is also a route
source. What remains unchanged in substance is the SURFACE flavors of the other
D10 rows, which H56 owes and this item never did.

### Tier C - economics (career)

9. **Costed dispatch.** ~~UNBLOCKED 2026-09-01 - LANE STILL TO AUTHOR.~~
   **FIXTURE AND SPEC AUTHORED 2026-09-02, NEVER FLOWN** - fixture
   `rover-route-career` (`harness/tools/build_rover_route_career.py` +
   `harness/lib/test_build_rover_route_career.py` + a `RECORDED_FIXTURES` row),
   spec `harness/scenarios/RVR-4-rover-route-career-cost.toml`.
   ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT was fixed 2026-09-01 (the
   costing basis falls back to the first `SourceRefs` member carrying a
   single-vessel snapshot, so the rover shape - a snapshot-less runway-stub root
   - now prices a dispatch instead of returning 0) and has been unmeasured
   since; this lane is that measurement.
   **THE SUBJECT COST NO FLIGHT.** `rover-route-career` is
   `rover-route-recorded` STAMPED INTO CAREER by construction from two committed
   inputs - the recorded save supplies the Parsek payload and the world,
   `fresh-career` supplies seven career SCENARIO nodes lifted verbatim - with
   `Mode` flipped, `ScenarioNewGameIntro` (SANDBOX-only) dropped, and the
   `ParsekScenario` node, the whole `FLIGHTSTATE` and every sidecar asserted
   BYTE-IDENTICAL to the sibling's. The lane is RVR-2's thirteen driven steps
   verbatim, so `env.IsCareer` is the only variable moved and RVR-2's green run 2
   is the control. Because both inputs are committed the drift cell RE-RUNS the
   build and asserts byte-identity, which no other recorded fixture's cell can
   do. Precedent: `build_strategy_career.py`, applied in the other direction.
   **WHAT IT PINS:** the `FundsCost basis=... snapshotSource=... fallback=1`
   breadcrumb, where `fallback=1` IS the assertion (the fix firing on the shape
   that motivated it - the root `cf8d06fc` has no `_vessel.craft` sidecar at
   all, and the dock member is skipped as a combined-vessel snapshot), plus
   `DispatchDebit: ... cost=<non-zero> careerKsc=1`. The cost DERIVES to 7410
   (7250 of parts + 200 LiquidFuel at 0.8 from the root's complete launch
   manifest) but is pinned as a regex, not a number: the price table came off the
   automation instance's own `ModuleManager.ConfigCache` - which no CI job can
   re-read, and where ProbesBeforeCrew prices `dockingPort2` at 600 against
   Squad's 280 - so the first flight is what measures the figure.
   **FLOWN TWICE 2026-09-01, AND TWO OF THE THREE ASKS ARE NOW MEASURED.** Both
   runs driver-valid, both `PARSEK-FAIL(expectation)`, neither on a token that
   was wrong about the product. Round 1 (`_2204`, 74 s) measured the round-1 cost
   fix as INSUFFICIENT for this shape and is why the ghost-surface follow-on
   exists: `UNCOSTED - no SourceRefs member (of 2, root cf8d06fc...)`, so
   `cost=0` and the dispatch was still free. It also killed the lane's authored
   basis derivation in passing - `of 2`: the route holds TWO SourceRefs, because
   `ComputeMemberRecordingIds` collects one id per composition through-line head,
   so the transport's driving legs strip back to the root and `4370a799...` is
   not a member at all. Round 2 (`_2228`, 50 s) on the follow-on DLL measured the
   whole chain and red only on this spec's own two derivations.
   **WHAT ROUND 2 BOUGHT:** the first COSTED route dispatch in the suite's
   history (`FundsCost basis=launch-manifest source=cf8d06fc...
   snapshotSource=cf8d06fc... fallback=0 snapshotSurface=ghost
   cost=7410.0000023841858` - the ROOT priced ITSELF off its ghost copy, so the
   subset path was never needed and its absence is now pinned by contiguity), the
   first career funds debit driven end to end
   (`Career KSC funds debited: -7410.0000023841858`), AND the funds-short hold.
   THE DERIVED COST WAS RIGHT TO THE UNIT.
   **THE FUNDS-SHORT HOLD FIRED, AND THIS ITEM'S OWN NOTE HAD CALLED IT
   UNREACHABLE.** The correction is worth carrying because the missing step is a
   general trap: A LEDGER AMOUNT IS NOT A LIVE POOL AMOUNT. The committed
   `ledger.pgld`'s five `MilestoneAchievement` rows do raise the RUNNING balance
   by 18,200, but `KspStatePatcher.PatchFunds` runs its target through
   `ApplyDrawdownGuard` and the "keep what you earned" guard refuses the upward
   patch, holding the live pool at the spent value (`GUARDED UPLIFT clamped
   resource=Funds running=29200 live=11000 clampedTo=11000 - spent value held;
   ledger may be missing a spending channel`). So the live pool IS the 11000
   seed: cycle 0 is charged 7410, cycle 1 sees 3590 and blocks
   `kind=FundsShort reason=funds-short shortfall=3820.0000047683716` into
   `blocked-then-paused armedBy=send-once hold kind=FundsShort`. The shortfall is
   `2 * cost - seed`, exactly what the seed band was solved to produce - so the
   band was never "for a future lane", it was this lane's gate all along.
   Cycle 1 stops reading `DestinationFull` only because the funds gate is step 7
   of `CheckEligibility` and capacity is step 8; both refusals are true on these
   bytes and ordering picks which is logged, which is what round 1 measured from
   the other side at `cost=0`.
   **THE RECOVERY CREDIT stays the one ask not collected**, and that half of the
   derivation held: the ledger carries no recovery rows, measured as
   `credit-skip zero-recovery (recoveryRows=0)` and now pinned as a token so the
   absence is a gate rather than a paragraph. A recovery-credit lane needs a
   recorded flight that ENDS in a KSC recovery, which no committed route fixture
   is - that is the remaining Tier C gap, and it is a fixture decision.
   RE-FLY OWED on the corrected token set; the tier stays `pending-fixture` until
   a run is green. NO D10 claim yet, on the CLAIM-IS-NOT-GATE rule: the career
   flavor of `ksc-origin` is earned in the commit that measures the lane green.
10. **Escrow competition.** Two routes sharing one physical source (D10
    `multi-origin-escrow`). **BUILT AND LIVE-PROVEN 2026-09-02: the in-game
    category `RouteEscrowContention` (2 cells) plus
    `harness/scenarios/H60-route-escrow-contention.toml` over `logi-cargo-pad`.
    Census `2026-09-02_1314`, PASS attempt 1, wall 79 s, `total=2 passed=2
    failed=0 skipped=0` PINNED WHOLE, tier nightly.** THE MEASUREMENT IS THE
    INVARIANT: `raw=11 netted=5` while the holder held, then `raw=5 netted=5`
    after it debited - the competitor's availability did not move across the
    release, only the CAUSE did (`escrow` -> `physical`), so the escrow withheld
    exactly what the debit took (no over-block, no double-claim). Cell 2 read
    `freedByRemoval=6 pickedUpByB=8`: the holder removed mid-cycle freed its
    reservation WITHOUT debiting, and the competitor took its full 8. **ARMED DISCIPLINE COMPLETE the same day, and D10 `multi-origin-escrow`
    IS CLAIMED off it**: armed re-flight `2026-09-02_1339` PASS attempt 1 on the
    whole pin with `mismatches=[]`, and a negative control (cell 1's
    `causeAfterWindow` flipped `physical` -> `escrow`) red on EXACTLY that one
    token with zero forbids - so the pin discriminates rather than merely passes.
    The two runs drew different source pids and read identical amounts, so the
    invariance is the product's and not one log's. **ITEM 10 IS CLOSED.**
    **THE SCOPING'S SECOND HALF WAS REFUTED BY THE BUILD'S OWN DERIVATION, and
    that is the item's most useful residue.** The scoping prescribed asserting
    `ReleaseWindowEscrow` and "a subsequently eligible B after A's later window
    fires". The second is UNREACHABLE, and not by tuning: the reserve is the
    SUMMED pickup manifest `M` (`RouteOrchestrator.cs:1886`), each window's
    release is that window's OWN manifest (`:1966`), and the release fires
    TOGETHER WITH a physical debit of the SAME manifest, unconditionally
    (`:2690-2709`). So a competitor sees `max(S0 - M, 0)` at EVERY point of the
    holder's cycle - its availability is invariant across the release, and the
    escrow is an exact PRE-IMAGE of the debit (no double-claim, no over-block).
    What DOES move across the holder's window is the hold CAUSE, `escrow` ->
    `physical`, because `IsEscrowCausedShort` needs `raw >= need` and that fails
    once the cargo is taken. Cell 1 therefore drives reserved ->
    blocked(escrow) -> causeFlip(physical), which is the invariant driven rather
    than asserted; cell 2 drives the ONLY release that frees a competitor -
    `RouteStore.RemoveRoute` -> `DropRouteEscrow` mid-cycle, the player-reachable
    no-cargo release - after which the competitor is eligible and physically
    picks the cargo up. Two cells because one timeline cannot carry both: cell 1
    consumes the hold by debiting it, cell 2 needs it released un-debited. The
    identity is pinned headlessly by `RouteCargoEscrowTests.NettedAvailable_*`
    over the extracted pure `RoutePickupSourceGate.NettedAvailable`, which the
    live reader now calls.
    **THE SCOPING'S FIRST HALF STANDS UNCHANGED**, and is why the lane looks the
    way it does. Two corrections to this
    item's own wording came out of the scoping. FIRST, "no driven lane" was
    already stale: the in-game cell
    `Escrow_CompetingRouteSeesReservation_Holds` executes and PASSES on H38,
    H40 and RVR-1 (skipping only on H39, whose shared source is too large to
    net), so the GATE and the `source-reserved:` TOKEN are live-proven. What is
    genuinely uncovered is narrower - the ORCHESTRATOR-driven reserve / hold /
    release cycle between two routes actually stored in `RouteStore` and
    ticked, since that cell reserves and gates directly and adds neither route.
    SECOND, the missing subject is not a fixture problem and "two runs from one
    base" would not have produced it either: contention is UNREACHABLE on the
    single-stop path by construction, because reserve and release both sit
    inside one synchronous `EmitLoopCycle` and `CompareRoutesForTick` lets a
    route finish its whole cycle before any competitor is processed
    (`RouteOrchestrator.cs:2500-2506`). Only a MULTI-STOP route holds escrow
    across the dispatch-to-window gap (`:1407-1412`), and every route-bearing
    committed fixture carries exactly one `WINDOW` node, so every route the
    corpus can create is single-stop.
    **WHAT WAS BUILT, AND THE ONE PLACE IT DEPARTS FROM THE SCOPING'S PLAN.**
    The scoping named H55's already-green
    `DockCapture_TwoPartnersSequential_TwoWindowsOneRecording` as the producer.
    It is not the one used: that cell produces two RECORDED dock windows in one
    tree, but two routes still cannot be promoted off one tree
    (`candidate-already-promoted`) and a seam-created route's dispatch timing is
    derived from the recorded span rather than chosen. The builder shapes from
    `LogisticsMultiOriginRuntimeTests` are used instead - synthetic route
    topology plus the `LoopUnitResolverForTesting` clock - which is the whole of
    what is emulated; both routes are really in `RouteStore`, the tick is the
    production `RouteOrchestrator.Tick`, the gate is the production
    `CheckEligibility` over a live `LiveRouteRuntimeEnvironment`, the source is a
    live spawned vessel and every debit is a real resource write. Processing
    order is fixed by the production priority rule (`CompareRoutesForTick`
    ascending on `DispatchPriority`, holder 0 and competitor 1), so nothing turns
    on luck. A NEW category rather than two more `RouteDockCapture` cells,
    precisely so H55's and H56's pinned `total=6` does not move.
    Full derivation, the three stacked secondary blockers and the two forgeries
    deliberately not attempted: `docs/dev/todo-and-known-bugs.md` ->
    `C10-ESCROW-CONTENTION-NEEDS-A-MULTI-STOP-ROUTE`.

### Tier D - scale and rendering (pair with the render programs' budgets)

11. **Surface-route map presence.** ~~Measured pin exists: a landed-terminal
    loop has NO map/TS proto (flight-mesh only; KSC host works in-window).
    A `route-map-lines` lane for a SURFACE route must be authored against
    that pin, not against the orbital route lines V18T covers.~~
    **CENSUS FLOWN, READ AND ARMED 2026-09-02** -
    `harness/scenarios/H59-surface-route-map-lines.toml` over `rover-route-recorded`,
    run `2026-09-02_0947`, PASS attempt 1, wall 99 s, every verifier green.
    **THE ANSWER IS OUTCOME A, AND RICHER THAN EITHER PRE-REGISTERED OUTCOME: a
    surface route's path IS on the FLIGHT MAP, and BOTH producers draw it** - the
    static overview and the per-cycle ghost, with the handoff observed map-open.
    (Precisely: the second drawn frame lands 140 ms after `exitmapview`, because
    the route-draw slot gates on the planetarium camera and the scene rather than
    on `MapView.MapIsEnabled`, so the measured claim is CO-PRESENCE rather than a
    strict alternation inside one window.) Thirteen `Route line draw:` lines split 2 pre-create /
    2 drawn (`routesDrawn=1 legsDrawn=1 skippedOwned=0 malformed=0 other=0`) /
    9 handed off (`skippedOwned=1`, with `Polyline frame: scene=FLIGHT drawn=1` in
    the same window), plus one
    `Route line build: route=<run-local 8-hex> members=2 groups=1 legs=1 transferDropped=0`.
    IT DOES NOT CONTRADICT THE LANDED PIN: the member still gets no proto, and what
    the census adds is that proto presence and route-overview presence are
    INDEPENDENT. THE PRE-REGISTRATION EARNED ITS KEEP - its warning that
    `skippedOwned=1` is NOT an absence is the half that carried the result, since a
    naive read of nine-of-thirteen lines says "no route line" and the truth is that
    the ghost had the leg. The lane is now ARMED on 18 required / 6 forbidden
    tokens (both draw shapes pinned AS A PAIR, so neither producer can quietly stop
    drawing), and what it owes is the ARMED RE-FLIGHT plus the negative control.
    TWO BY-PRODUCTS worth carrying: `RC-OWN-DRAW-HALF-IS-MAP-GATED` is now measured
    on a surface-route subject and NARROWED accordingly (the publish half proven
    through the route renderer's own `skippedOwned` counter, which reads the same
    `drewNonOrbitalLegRecordings` set the manifest hook is fed from); and the
    manifest's route counters read 0 for a reason that is not about routes, filed
    as RENDER-MANIFEST-VERB-EXPORT-IN-A-SECOND-SCENE-CLOBBERS-THE-FIRST-SCENE-
    ACCUMULATION, which is why `[expectations.renderComposition]` stays bare and
    V18T's armed window was NOT copied.
    THE AUTHORING RECORD FOLLOWS, kept because it is what the census was judged
    against. Every required token was structural or a VALUE REGEX, so the flight's
    product was a reading a human acts on and nothing was armed.
    WHAT THE PIN LEAVES OPEN IS THE SUBJECT, and it is worth stating because the
    obvious reading of the pin is wrong: `LANDED-TERMINAL-LOOP-HAS-NO-MAP-PRESENCE-
    OUTSIDE-THE-FLIGHT-SCENE` is about PROTO-DRIVEN presence for a loop MEMBER,
    while a route's OVERVIEW LINE is a different producer -
    `RouteTrajectoryLineRenderer.DrawAll` walks `RouteStore.CommittedRoutes` from
    its own cache, consults no proto, no `GhostMapPresence` entry and no terminal
    state, and publishes no ownership by design. So a landed subject having no
    proto does not decide whether its ROUTE draws a line. TWO OUTCOMES ARE
    PRE-REGISTERED in the spec header, both read off the one line `DrawAll` emits
    unconditionally (`Route line draw: enabled=True routesDrawn=R legsDrawn=L
    skippedOwned=S malformed=M other=O`): the route DRAWS (`routesDrawn=1`,
    `legsDrawn>=1`, `other=0`, `routeLineBuilds >= 1` in the manifest), or it does
    NOT, with `other=1` / `malformed=1` / `skippedOwned=1` naming three different
    reasons - and `skippedOwned=1` is not an absence at all, it is the per-cycle
    ghost owning the leg that frame. The line being ABSENT is an instrument red,
    not an outcome, so it is a required token. THE LANE ALSO PAYS
    `RC-OWN-DRAW-HALF-IS-MAP-GATED` A SECOND TIME: it is the first committed lane
    to drive `EnterMapView` on a route or a landed subject, so `Polyline frame:` -
    that debt's own evidence line, absent whenever the map-gated LateUpdate bails -
    is pinned as an instrument and `ownershipChanges` is captured report-only
    (V6M closed the debt on a MUN ORBIT subject and its closure text carries the
    qualifier). The KSC half is V22K's pattern, structural tokens only. NO D10 /
    D11 claim: the SURFACE flavor of `route-map-lines` is earned by an armed gating
    token off a green census, exactly as V18T earned the orbital flavor, and a
    census that reads the not-drawn outcome earns no row and re-words this item
    instead. FLIGHT OWED; nothing else is.
    **SETTLED: the census read the DRAWN outcome, so the row is earned rather than
    re-worded - a NEW registry value, D10 `route-map-lines-surface` (added per the
    growth rule), not a second claim on V18T's `route-map-lines`. CLAIMED
    UNCONDITIONALLY as of the armed re-flight `2026-09-02_1038` (PASS attempt 1)
    plus a negative control that red on exactly the headline token. THE ITEM IS
    CLOSED; the lane's only remaining debt is a report-only reading run for its
    `[expectations.routes]` block, which was added after the last flight and which
    no run has evaluated.**
12. **Route x rewind, flown.** H6 covers the timeline synthetically;
    a rover-route rewind variant makes `route-x-rewind` a flown claim.
    **DONE 2026-09-02: LIVE-PROVEN, AND `route-x-rewind` IS NOW A FLOWN CLAIM**
    (`harness/scenarios/H58-route-rewind-to-launch.toml`, run
    `2026-09-02_1020`, 62 s, PASS attempt 1, re-tiered nightly). The
    pre-registration was CONFIRMED on every number - `retiredRouteRows=1
    committedRoutes=1 dormantRoutes=0`, `kept=1 (reconciled=1) dormant=0`,
    `derivedPaused=1 derivedActive=0 oneShotFlagsCleared=1
    countersReconstructed=0` - and the produced save read the route back
    `Paused` from a session the player left Active, with the one-shot flags
    cleared. H6 keeps `route-x-rewind` as the SYNTHETIC declarer; this is the
    flown one, and it is the first flight to execute the REAL
    `HandleRewindOnLoad` go-back scene load that H6's own header names as
    unreachable there. The prediction is
    written down first, in the spec header and in `autotest-status.md`, and is
    UNCHANGED by the machinery that lets the lane test it: an Active route HOLDS
    across a Rewind-to-Launch - dormant only when the cutoff precedes
    `Route.CreatedUT`, otherwise kept with cursors reset, pause state re-derived
    from the kept PLAYER lifecycle rows, counters reconstructed, and the armed
    one-shots cleared unconditionally - cited to the design doc's lines 905 /
    909 / 1030. The lane takes the KEPT branch by construction (the route is
    created ~600 s below the cutoff, with `RewindToLaunchLeadTimeSeconds = 15.0`
    doing the arithmetic), and its TimeJump is taken while the route is PAUSED
    because RVR-2 measured that a jump past several loop periods fires a cycle.
    **THE AUTHORING MEASURED TWO BLOCKERS AND CLOSED THE SECOND ONE.** (1) NO
    COMMITTED RECORDED FIXTURE CAN BE A REWIND-TO-LAUNCH SUBJECT: `CanRewind`
    needs a `rewindSave` on the tree root and every recorded fixture carries it
    EMPTY by harvest policy, gated in both directions by
    `build_rover_route_recorded.py` with INV9's dangling-hint WARN as the stated
    rationale - so "the rover fixture is committed" was true and irrelevant, and
    THIS HALF STILL STANDS. The lane routes around it by producing its own
    subject in-run: `CaptureRewindSave` writes the `parsek_rw_*` quicksave at
    every non-promotion recording start, so `StartRecording` -> `CommitTree`
    yields a rewindable tree. (2) NAMING that tree was the other half - a
    runtime `Guid`, and `${runSave}` is the harness's only substitution, so the
    auto-select refused `ambiguous-tree` over the host's two committed trees.
    CLOSED 2026-09-02 by the seam addition `InvokeRewindToLaunch tree=latest`
    (contract: `design-autotest-command-seam.md` -> `#### D12/A2`): the most
    recently committed tree, id path untouched and still winning, bare no-arg
    call still refusing - which H58 keeps as a live negative control reading
    `committedTrees=3`. Filed as
    ROUTE-REWIND-TO-LAUNCH-UNREACHABLE-ON-COMMITTED-FIXTURES, now narrowed to
    blocker 1. What the lane also buys: the suite's FIRST driven route
    pause/activate pair (the exact player-intent rows `DeriveTimelineStatus`
    reads at every rewind, never produced by a driven run before), and the first
    `OnLoad: go-back route reconcile` line any flight has ever printed - the
    seam H6's own header names as unreachable there.
13. **Harvest-provenance, surface.** An ISRU drill rover feeding the route
    (D10 `harvest-provenance` surface flavor; the orbital flavor has
    coverage via the depot-drill lanes).
14. **Inter-body surface delivery.** The Nth-window inter-body machinery
    (H34) with a landed endpoint - the "tanker to a Mun base" shape the
    design doc names as v0's ceiling.

### Machinery register (build order inside the program)

- **`RouteCommand` + `SealSlot` seam verbs** (Cause C closure) - in flight on
  this branch. Everything in Tier A2+ depends on them.
- **saveparse `route` expectation block** - `routes` is a RESERVED block name
  today (IMPROVEMENT-SAVEPARSE-NO-ROUTES-FACET); until it parses ROUTES nodes,
  creation lanes pin end-state via logContract tokens + the builder-side
  `verify_route()` pattern. Promote when RVR-2 stabilizes, then arm per the
  standing report-only-first protocol.
- ~~**Fixture: `logistics-rover-a` harvest** (RVR-1/2 host).~~ **LANDED
  2026-08-30 as `harness/fixtures/saves/rover-route-recorded`** - named for the
  LANE and never for the source save, because `run.py::stage_fixture` rmtree's
  the same-named save inside the automation instance. Two committed trees, five
  recordings, 19 authoritative sidecars, one TARGET-branch
  `ROUTE_CONNECTION_WINDOWS` node, NO `ROUTES` node (the operator created the
  route after the save was written, which is what gives RVR-2's create something
  to do), NO `ROUTE_ORIGIN_PROOF` node (both trees start at the Runway) and no
  `mergeState` key anywhere (both trees already fully sealed). A second,
  route-CARRYING fixture (post-creation re-save) is still optional and only
  needed for route-reading lanes that must not create.
- **Self-provisioning `RouteDockCapture` category + lane H55** - LANDED
  2026-09-02 (`Source/Parsek/InGameTests/RouteDockCaptureInGameTest.cs`,
  `RouteDockCaptureMath.cs`, `harness/scenarios/H55-route-dock-capture-isolated.toml`).
  The docking-port sibling of H41's claw cell: it spawns docking ports, tanks
  and cargo containers beside the active vessel and drives the REAL
  `Part.Couple` -> classifier -> window capture -> stock cargo move ->
  `Part.Undock` -> window completion cycle, which is what makes Tier B items
  5-8 producible without a flight each. Everything Tier B needs beyond it is a
  fixture decision (a two-rover landed host), not new machinery. The honest
  residual, same as H41's: the stock docking FSM is not exercised.
  **THE SECOND HOST IS NOW WIRED** - `H56-route-dock-capture-landed` (authored
  2026-09-02, NEVER FLOWN) runs the identical six cells over the committed
  `rover-route-recorded` fixture, which is the "two-rover landed host" the
  sentence above calls a fixture decision. No new machinery was needed for it,
  which is the claim above being cashed: the lane is a spec file plus two
  registration entries. Both hosts share ONE tally derivation and ONE set of
  formatters, so a seventh cell moves both members in the same commit and a
  format drift reds in `dotnet test` rather than on either flight.
- **Generators** - landed on this branch: `WithRouteConnectionWindow` /
  `WithRouteOriginProof` through the production codec chokepoint,
  `RouteWindowFixtures.SurfaceDeliveryWindow` (rover-flight constants),
  `VesselSnapshotBuilder.AddStoredPartToInventory`. Tier B/C unit siblings
  build on these, not on new hand-rolled window helpers (16 already exist;
  do not add a 17th).

Sequencing: Tier A rides this branch. Tier B is no longer five separate
calibration flights: items 5-8 are authored as in-game cells on lane H55
(operator tier, flown once and red on a rig defect, re-fly pending), so the
sequencing is now ONE clean census of H55 followed by whatever its probe line
says about item 4. Flight 1 already took half that measurement
(`externalParentParts=0`, which supports the suspicion) but the pad host is
PRELAUNCH and the producer short-circuits there, so item 4 now also owes the
probe a run on a LANDED host before it can be settled either way - which is
lane `H56-route-dock-capture-landed`, authored 2026-09-02 and NEVER FLOWN. Tier C item 9
has FLOWN TWICE (2026-09-01), measured the costed dispatch AND the funds-short
hold, and owes one re-fly on its corrected tokens; item 10 is still unauthored.
The recovery credit is now the only unbought third of item 9, and it needs its
own subject - a recorded flight that ENDS in a KSC recovery - rather than a
re-tuned seed or a second career stamp.
Tier D items ride along whenever their sibling program
(loop-render / ghost-replay) is already paying the flight cost. The standing
verdict - the nightly lane does not grow until the basics are gated - stands
unamended here too.

**HAND-OFF, 2026-09-02 (end of the rover-route sessions).** Green and nightly:
RVR-1/2/3/4, H55, H56. What can be built NEXT WITHOUT a manual flight, in
order: (a) the B4 producer fix + capture cell (todo
ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE, now CONFIRMED); (b) D12 route x rewind
flown - CORRECTED 2026-09-02: "`InvokeRewindToLaunch` is implemented and the
rover fixture is committed" is true and NOT sufficient, and H58's authoring is
what measured why (see item 12: no committed fixture carries a launch quicksave,
so the lane must produce its own subject in-run, and naming that subject needed
the `tree=latest` seam addition that landed with it). H58 IS AUTHORED AND A
REWIND FIRES IN IT; what it owes is its first flight;
(c) D11 surface-route map presence over the rover fixture with the map-view
verbs, authored against the landed-terminal-no-proto pin; (d) ~~C10 escrow
competition - scope a synthetic two-candidate fixture before assuming a
flight~~ SCOPED 2026-09-02, ANSWERED NO: the synthetic fixture cannot reach
contention and neither can a manual flight, because every route the corpus can
create is single-stop and a single-stop route never holds escrow past its own
`EmitLoopCycle` - the next move is a C# `RouteDockCapture` cell, not a fixture
or a flight (item 10 above; todo
`C10-ESCROW-CONTENTION-NEEDS-A-MULTI-STOP-ROUTE`);
(e) the saveparse `route` block promotion. WHAT STILL NEEDS THE OPERATOR AT THE
CONTROLS: D14 inter-body surface delivery (no targeted landing / rover driving
in the mission library - the biggest remaining manual subject); D13 surface
harvest-provenance (a drill ON ORE - check for a landed-on-ore fixture first);
C9's recovery-credit third (a recorded route flight that ENDS in a KSC recovery
- possibly seam-drivable with the recover verbs, unscoped). ~~C10 only if the
synthetic route fails (two runs from one base).~~ C10 IS NO LONGER AN OPERATOR
ITEM EITHER, and the reason is worth carrying: two hand-flown runs from one base
would have produced two SINGLE-STOP routes, which cannot contend for the same
reason a synthetic pair cannot. B4 is NO LONGER a manual flight.

## Trust and fail-open risks still outstanding

What IS load-bearing today: the 8-verifier chain (driverValidity, batchComplete,
analyzer, logValidate, testResults, anomalySweep, expectations, ledgerOracle); the
analyzer's INV1-INV9 with baseline-Forbid on fresh saves; the anti-vacuity gate built
by construction (`hlib.vacuous_batch_complete_probes` /
`batch_contract_vacuity_gap`); `CommittedBatchTallySourceSyncTests` cross-checking
every pinned tally against the C# attributes; and the two negative controls on
S1.6 / S1.7, which are the only place in the system where a zero-drift assertion is
proven able to fail.

Remaining fail-open surfaces, ranked:

1. **`logContracts` is presence-only and cannot count.** `hlib.evaluate_expectations`
   applies each pattern with a bare `re.search` over the whole log. No occurrence
   counts, no ordering, no "exactly N". A regression that drops one of two board
   merges and keeps one passes every log contract in the suite. NARROWED by R9
   2026-07-31: the branch-point-count and supersede/tombstone-row surfaces are now
   measurable structurally (`saveParse` row, report-only until armed), so the
   dropped-board class no longer depends on log counting; the log side itself is
   still presence-only.
2. **The only save-content assertion is an integer**, and it is COMMIT-BLIND.
   ADDRESSED by R9 2026-07-31, and CLOSED FOR S4.1: the `saveParse` verifier reads
   tree topology, terminal states, merge/commit markers, supersede rows, tombstones
   and rewind points off the produced save on every driver-valid run. It becomes a
   GATE per scenario when that scenario arms `gating = true` after its report-only
   readings are confirmed live. S4.1-rewind-merge is armed (runs `2026-07-31_1628`
   read-only / `_1635` armed / `_1637` negative control); every other spec is still
   report-only, so for them this remains ADDRESSED-REPORT-ONLY.
3. **Three expectation verifier families are declared and inert** (`route`,
   `rewind`, `loop`). PARTIALLY CLOSED by R9 2026-07-31: `rewind` is now evaluated
   AND ARMED on its one declarer - S4.1's asserts stopped being comments and became
   a gate that has been watched both pass and fail. `route` / `loop` stay reserved
   BY CHOICE: zero committed declarers, so an evaluator would be unused surface; the
   spec-author trap is bounded to blocks nobody declares.
4. **The ledger oracle's independence check is a structural no-op** (see the open-bugs
   table). `compute_expected` consumes seam-declared entries only, with no live
   cross-check, in the one verifier the entire L-track depends on.
5. **Claim is not gate.** `[dimensionsCovered]` is declarative and
   `hlib.validate_spec` does not check that a claimed cell has a gating assertion. A
   pass that added claims without tokens would move 83 to roughly 130 and prove
   nothing. Every item in the build order names its gate deliberately; hold that line.
6. **Decision layer is not flown situation.** Driving an in-game batch gates a
   decision surface in a live KSP process, not a flown scenario. The precedent is
   explicit and honest (`M1-mission-loop-unit`: gates the PLAN, not the playback).
   Every spec from R4, R6, R7 and R8 must carry the same sentence. A `RunTests`-covered
   `commit-scene-exit` is a weaker claim than a flown one, and the roadmap should never
   silently trade one for the other.
7. **The analyzer proves absence of malformation, not presence.** INV1-INV10 would
   catch a malformed debris recording. They cannot catch a MISSING one. That asymmetry
   is exactly why R1 is about presence tokens and not analyzer rules.
8. **Mutation proof is prose, not a gate.** Every mutation claim in the docs was
   produced and re-verified by hand; no mutation tool exists anywhere in `harness/` or
   `scripts/`. A refactor that makes a cell vacuous will not red anything and the doc
   will keep asserting the cell bites.
9. **The near-vacuous batch is admitted by design.** The gate blocks only
   `passed == 0`, so `total=42 passed=1 failed=0 skipped=41` satisfies every rule. Pin
   whole tallies.

---

## Cost, and what it implies for sequencing

Measured from `harness/coverage/duration.json` p50 values and the spec tiers:

| Cadence | Scenarios | Priced p50 sum | Unpriced |
|---|---:|---:|---|
| daily | 17 | **716 s (11.9 min)** | 4 (M1, M2, S1.6, S1.7) |
| nightly | 32 | **14,443 s (4.01 h)** | 5 (the 4 above + EVA-4) |

All 38 specs carry `retry.policy = "once"`, so the nightly worst case is roughly 8
hours: a full overnight window already consumed.

Concentration: five scenarios are 9,733 s, about 67 percent of the nightly wall.
B13 2,825 s, BDOCK-1 2,164 s, B14 2,141 s, B11 1,317 s, B15 1,286 s. The 17 daily
specs together are 716 s, about 5 percent.

Runs are strictly serial. `run.py` loops scenarios one at a time under a per-instance
run lock and `resolve_instance_dir` maps one profile to one directory. There is no
parallel lane today. R14 would create the first possible second lane.

**Cost per claimed registry cell, measured.** This is the number that should drive
sequencing:

| Lane | Measured range | Examples |
|---|---|---|
| seam / in-game batch | **5.8 to 17.2 s per cell** | L1-hire 5.8, B10 7.4, S0.5 9.2, EVA-1 9.8, S1.4 14.0, H5 17.2 |
| flown mission | **39.8 to 235.4 s per cell** | B1 39.8, B6 45.4, B2 50.4, B12 62.7, BDOCK-1 103.0, B11 131.7, B4 161.9, B14 178.4, B13 235.4 |

The batch lane buys coverage roughly 4 to 20 times cheaper per cell than the flight
lane, and the new specs in R4, R6, R7 and R8 are all batch shapes measured at 46 to
70 s each. Roughly 30 new specs across R4 through R8 is about 25 to 35 minutes of
added wall time. That belongs on `daily`, where it displaces nothing and roughly
triples the daily lane's 716 s while leaving it far under any window.

The nightly flight lane should NOT grow until the basics are gated. The whole point
of R1 is that adding a fourteenth flown mission to a suite that cannot detect the
disappearance of the parent-anchored debris model buys less than a spec-only PR does.

Before setting the rotation, price the new specs into `harness/coverage/duration.json`
from their first green runs rather than from these estimates.

---

## Corrections made to the source analyses, and what stays UNVERIFIED

Two independent analyses fed this roadmap. Where they disagreed or overreached, this
is what was verified and preferred.

**Rejected: "multi-category `[driver.autorun]` batches are implemented, validated,
and used by zero specs, so a family collapses into one boot for free."** The runner
and parser halves are indeed implemented (`category=multi:<n>` aggregate plus
per-category lines, both parsed by `hlib.resolve_batch_complete`), but
`hlib.validate_spec` REJECTS a multi-category selector fail-closed under
`SINGLE_BATCH_SELECTOR_RULE`, and the check reads `driver.autorun.tests` as well as
`driver.steps`. Using the form today requires taking the
`batchVacuityOptOut` escape and discarding the anti-vacuity guarantee. This is why
R13 is framed as widening the rule rather than "just use the existing form", and why
R4's cost is honestly five specs and five boots rather than one.

**Corrected: "drive the `Logistics` category, 47 written tests, one 60 s boot."**
38 of the 47 carry `AllowBatchExecution = false`, so only 9 are batch-reachable
today. The other 38 need R5.

**Corrected: undriven-category scene reachability.** Measured as 82 categories fully
reachable via FLIGHT / SPACECENTER / scene-agnostic and 7 involving TRACKSTATION or
MAINMENU, rather than 78 and 6.

**Corrected: 39 committed specs.** There were 38 when this roadmap was written,
at HEAD and at `origin/main`. PR #1357 makes it 39; the count above is updated and
the coverage figure is unchanged because R1 claims no new registry value.

**Reconciled: the restore-flag count.** An earlier draft reported a raw `grep` of 78
occurrences of `RestoreBatchFlightBaselineAfterExecution = true` against an
attribute-scoped 72. **78 is not reproducible by any grep form** and was dropped on
review: over `Source/Parsek` the identifier appears 100 times, of which 72 lines carry
`= true` (84 if `Source/Parsek.Tests` is included). The attribute-scoped count is also
72, of which 68 also carry `AllowBatchExecution = false`; that agreement is the point,
not a discrepancy to explain. The attribute-scoped numbers are the ones used above.

**Preferred, where the two agreed independently:** the S1.5 / S4.1 operator-tier
premise being stale (both reached it by different routes; the LoadRoute contract plus
the two FLIGHT-pinned specs on the same template is the evidence), and the
isolated-batch seam argument being the single biggest unlock.

UNVERIFIED in this pass, flagged rather than asserted:

- **`modded-compat.toml` mod presence in the dev GameData.** One analysis reported
  every `devSourcedMods` entry present. Not re-checked here. Verify before scheduling
  R14.
- **Whether `Pipeline-Anchor`'s 7 tests map to D3 `relative-anchored-nonloop` /
  `relative-loop` / `boundary-seam` one-for-one.** The category name and test names
  suggest it strongly; the mapping was not read test by test. Confirm before pinning
  the R6 claims.
- **Whether the Kerbal X upper stage carries deployable solar panels**, which would
  make D7 `panels-antennas-radiators` free on B11-B14 via a `Part event:
  DeployableExtended` token. Check an archived recording or log before claiming.
- **Which specific D5 / D6 / D9 cells each `Rewind` test closes.** The category-level
  mapping in R7 came from test names, not from reading all 37.
- **Whether `Coalescer`'s `crash-coalescing` test actually produces the D5 cell** as
  opposed to asserting a decision about it.
CORRECTED 2026-07-27 (review of this file). An earlier draft of this section claimed
"5 bare `[InGameTest]` declarations, 539 total minus 534 with argument lists, default
to `Category = "General"`". **There are no bare declarations.** All 542 carry an
argument list and all 542 resolve to a real category: `General` does not appear among
the 98, and `hlib.parse_ingame_test_declarations` reports 0 unresolved. The 5-count
was an artifact of the counting method - 5 declarations use the fully-qualified
attribute form `[Parsek.InGameTests.InGameTest(...)]` (4 in `Ledger`, 1 in `Rewind`,
all in `IncompleteBallisticRuntimeTests.cs`), which a `[InGameTest(` scan misses and
`hlib` resolves. Three further `[InGameTest]` occurrences are prose inside comments,
which `hlib`'s noise mask correctly excludes. Every per-category number in this file
came from `hlib`, so none of them moved; only the reconciliation paragraph was wrong.
Rule this establishes: count in-game declarations with
`hlib.parse_ingame_test_declarations`, never with grep.
