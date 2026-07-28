# Autotest roadmap: building up from the basics

Companion to `autotest-status.md`. That file owns STATUS (what is shipped, proven,
gated). This file owns FORWARD ORDER: what we still cannot reproduce, why, and in
what sequence to build it. It is deliberately a separate file so it can be rewritten
without touching the status doc that several lanes edit concurrently.

Written 2026-07-27 on branch `autotest-roadmap`. Every number below was measured in
this worktree at HEAD `1591aa59f` by the command named beside it. Nothing here was
run against a real KSP instance: no `run.py`, no `provision.py`, no launch.


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

### Scenarios: 55 committed

`ls harness/scenarios/*.toml` returns **55** files. This baseline was written at
38, correct at `origin/main` when the roadmap landed. Since then: #1358 took it to
52 (14 in-game batch specs), #1357 added `R1-rewind-loop-flown` (53), #1366 added
`CL-1-pod-impact` (54, the crew-loss atom), and #1367 added
`H21-scene-exit-merge-isolated` (55, the first isolated-batch spec). Coverage moved
to **97 of 242**: #1358's specs carried the bulk of the gain, CL-1 added AND covered
the new D12 `crew-death-in-flight` value (which is also what moved the denominator
to 242), H21 covered the isolated scene-exit merge, and R1 added none because it
claims no registry value that was not already claimed. Adding a scenario is not the
same as covering a cell. Re-derive these rather than editing them by memory; both
numbers have moved five times in three days.

### Coverage: 97 of 242 registry cells (was 83 of 241 at the baseline)

`hlib.compute_coverage(specs, [], registry)` over the committed specs and
`harness/coverage/registry.toml` returns exactly:

```
values 242   covered 97   uncovered 145   expectedFailValues 0   xpass 0
```

Per dimension (total / uncovered):

| Dim | Subject | Total | Uncovered |
|---|---|---:|---:|
| D1 | recording lifecycle | 18 | 8 |
| D2 | sampling | 4 | 1 |
| D3 | reference frames | 7 | 4 |
| D4 | track sections / optimizer | 12 | 6 |
| D5 | tree topology | 12 | 7 |
| D6 | playback / ghosts | 16 | 11 |
| D7 | part events / FX | 16 | 12 |
| D8 | ledger / career | 18 | 6 |
| D9 | rewind / re-fly | 16 | 9 |
| D10 | logistics / routes | 20 | 12 |
| D11 | missions abstraction | 18 | 10 |
| D12 | crew | 10 | 8 |
| D13 | spawn positioning | 11 | 7 |
| D14 | bodies / scenes | 32 | 18 |
| D15 | timeline | 1 | 1 |
| D16 | storage / sidecars | 13 | 9 |
| D17 | mod compatibility | 6 | **6** |
| D18 | re-fly / interaction | 12 | 10 |
| | | **242** | **145** |

### The headline

At the baseline this read: 13 of 18 basic recording-lifecycle cells (D1) never
exercised, D13 spawn positioning 11 of 11 uncovered, D17 mod compatibility 6 of 6.
One wave later (measured 2026-07-28 at `7f5efa738`) D1 is down to **8 of 18** -
the R4-family batches, H21 and the R1 gates closed `commit-scene-exit`,
`switch-segment`, `scene-exit-finalization`, `ballistic-extrapolation` and
`finalization-cache` - and D13 is down to 7 of 11. D17 is untouched at 6 of 6.

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

### Cause A: written, never driven (338 tests, 75 categories; was 414 / 89 pre-#1358)

Measured with `hlib.parse_ingame_test_declarations` over every `.cs` under
`Source/Parsek`:

```
539 [InGameTest] declarations in 97 categories, 0 unresolved
201 declarations in the 22 categories any spec drives (179 of them execute)
338 declarations in the 75 categories nothing drives
```

The 22 driven categories are the pre-#1358 eight - `GameActionsHealth`, `GhostMap`,
`GhostPlayback`, `MapRender`, `Missions`, `Periodicity`, `RecordingInvariants`,
`RouteRewindTimeline` - plus the 14 that #1358 wired: `DataHealth`,
`EvaSpawnPosition`, `FinalizeBackfill`, `FlightIntegration`, `GhostVisuals`,
`IncompleteBallistic`, `KSP`, `Pipeline-Anchor`, `Pipeline-Smoothing`,
`RecordingFinalization`, `SpawnHealth`, `SpawnRotation`, `SwitchSegment`,
`TrajectoryMath`. The 201 / 179 split is because three of the pre-existing eight run
at SPACECENTER where some members scene-skip; all 76 declarations the new group adds
execute. Per-category detail, and the A/B/C triage of the 75 that remain, is in
`autotest-ingame-category-inventory.md`.

Scene reachability of the 75 undriven categories (a category is "reachable" when
every member runs in FLIGHT, SPACECENTER, or scene-agnostic, because `LoadGame` can
route to exactly those two scenes), re-derived post-#1358:

| Scenes present in the category | Categories | Pre-#1358 |
|---|---:|---:|
| FLIGHT only | 37 | 53 |
| scene-agnostic only | 13 | 16 |
| FLIGHT + SPACECENTER (+ agnostic) | 10 | 4 |
| SPACECENTER only | 8 | 9 |
| involves TRACKSTATION or MAINMENU | 7 | 7 |

**68 undriven categories are reachable today on fixtures we already own** (was 82).
Seven are not, because there is no seam route to TRACKSTATION or MAINMENU - and that
seven is UNCHANGED by #1358, which is the point: it wired only reachable categories,
so the unreachable set is exactly as hard as it was. `TrackingStation` alone is 9
batch-eligible tests stranded behind one missing seam verb.

The D1-D9 blocks that are batch-allowed and reachable RIGHT NOW, with no code change
and no new fixture. Struck-through rows were CLOSED by #1358 and are kept so the
remaining work is legible against the original list:

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
| `GhostAudio` | 9 | D6 `ghost-audio` |
| `MapPresence` | 5 | D6 `commnet-relay` |
| `ReentryFx` | 3 | D6 `reentry-fx` |
| `Watch` | 2 | D6 `watch-mode-retarget-explosion-hold` |
| `LedgerGroundTruth` | 1 | D8 `ground-truth-harness` |
| `Contracts` | 2 | D8 `contracts` |
| `StrategyLifecycle` | 2 | D8 `strategies` |
| `SpawnRotation` + 7 more | 29 | D13, all 11 cells |
| `CrewReservation` | 15 | D12 |

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

Counting attribute argument lists over all 539 declarations (see the note on the
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

**R7. Drive the D9 Rewind block.** One or two specs (split FLIGHT / SPACECENTER).

31 of 37 `Rewind` tests are batch-allowed today; the other 6 need R5. Closes
`seal-stash-fly`, `unfinished-flights-stash`, `rp-disk-reaper`,
`revert-during-refly-dialog`, `tombstones`, and hardens `merge-journal`,
`terminal-kind-classify`, `read-back-guard` beyond R3's single live cycle.
Deliberately after R3: a live re-fly proof plus a 31-test decision batch is a
genuinely defended dimension; the batch alone is not.
Flight? No.

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
- D6: `GhostLifecycle` (15 of 17; 2 are TRACKSTATION-scene and stay stranded until
  R12), `GhostAudio` (9), `MapPresence` (5), `ReentryFx` (3), `Watch` (2). None of
  these needs the reserved `StartLoopPlayback` / `EnterWatchMode` verbs;
  `GhostLifecycle` measures the loop / overlap surface over a live corpus.
- D8: `LedgerGroundTruth` (1, needs a CAREER FLIGHT fixture - UNBLOCKED 2026-07-28,
  R11 is closed by `career-pad-craft`),
  `Contracts` (2), `StrategyLifecycle` (2), `Ledger` (4). `LedgerGroundTruth` is
  Layer B of the non-circular ground-truth harness and is the cheapest large increase
  in ledger trust available.
- D12: `CrewReservation` (15).

Flight? No.

### Tier 3: machinery that raises the ceiling.

**R9. Structural save-content expectations.** One harness PR plus one analyzer PR.

An `[expectations.recordings.structure]` block evaluated against the analyzer's
parsed model: branch-point counts by type, TrackSection frame and anchor, terminal
state and body per recording. Plus land the three inert blocks (`route`, `rewind`,
`loop`). Retires the presence-only grep proxy, makes D3/D4/D5/D7/D18 claims mean
something, and closes both the "one of two board merges dropped still passes" class
and S4.1's assertions that currently do nothing.

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
argument on `LoadGame`.** Two seam verbs.

kRPC cannot substitute for the first (it bypasses `StockActionIntentMarker`) and
nothing at all substitutes for the second. Unblocks D1 `switch-segment` /
`switch-segment-noop-discard` in their REAL form (R6 only reaches the gate layer),
D5 `chain-continuation-switch`, D18 `committed-interaction-claiming` /
`chain-tip-original-pid`, D14 `scene-ts`, and the 7 stranded TRACKSTATION /
MAINMENU categories including `TrackingStation` (10 tests).

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

---

## Open bugs blocking or degrading the system

Forensics live in `docs/dev/todo-and-known-bugs.md`; this is a pointer index only.

| Item | Effect on this roadmap |
|---|---|
| ~~EVA-4 mission oracle returns MISSION-OK while the kerbal dies~~ (CLOSED by PR #1359, merged 2026-07-27) | Was: anyone reading `results/*_mission.json` alone read that flight as a success, with survival proven only by seam log tokens. Closed on both sides: the canopy-gated standoff completion in `TestCommandEvaExit.cs` (the C# EvaExit verb no longer completes before the observed canopy state allows it) plus the harness-side `missionOutcome` gate (`classify_post_mission_outcome_miss`), which reds a subject death as `PARSEK-FAIL(mission-outcome)` instead of letting a retry discard the evidence. Mission-level verdicts stay HANDOFF-scoped by design (`mlib.MISSION_HANDOFF_CONTRACTS` declares what a mission did not verify); the gate, not the mission JSON, carries survival. `autotest-status.md` already reflects this. |
| `ANOMALY_TOKENS` drift (status doc known-gate 0, REPORTED not resolved) | `icon-jump` is a dead token; nine raised reasons including `icon-teleport` are ungated. Now source-derived and report-only via `anomalySweep.unlistedReasons`, so visible but non-gating. |
| `STOCK_AWARD_PATTERNS` dead against real KSP logs (known-gate 3) | `unmatched_captured_awards` captures nothing, so the ledger oracle's independence cross-check is a structural no-op. Degrades every D8 claim and the whole L-track. Needs the operator stock-award capture session. |
| B4 `chuteDeployed` is still a commanded latch (known-gate 7, audit debt) | Same class that let B1 ship four months of green nightlies on a chute that never opened. B4's fixture carries the same `automateSafeDeploy = 0`. Needs its own diagnosis from a B4 recording before anyone concludes either way. |
| INV2 double-cover recorder seam (known-gate 5) | Real Parsek defect, fixed in its own lane. |
| The no-1x-coast certification cannot see coast warp-thrash (known-gate 8) | A real gap in an existing gate. Bounded for now by the machine-side thrash fast-fail. |
| `autotest-status.md` EVA-2 rows contradict themselves | The EVA table says "STILL pending-fixture: `eva2-lko-crewed` does not exist yet" while the section header says all four EVA scenarios are LIVE-PROVEN, Operator item 2 says the fixture was forged and committed, the fixture exists on disk with 7 VESSEL nodes, the spec reads `tier = "daily"`, and `duration.json` carries a measured 57 s run. Not a system bug; a stale doc row that reads as a blocker. Deliberately NOT edited here to avoid colliding with concurrent sessions; filed as a todo. |
| `harness/fixtures/saves/bdock-station-craft/` is an orphan | No spec LOADS it: no `saveTemplate` points at it. It IS named in a provenance comment at `BDOCK-1-station-interceptor.toml:97` (whose own `saveTemplate` is `bdock-station-pad`), and by `harness/tools/harvest_bdock_station.py` plus the design doc. Decide keep or delete - and if delete, drop that comment reference with it. |
| `S1.5-rewind-loop.toml:3-8` and `S4.1-rewind-merge.toml:3-9` carry a SPACECENTER-host premise contradicted by the LoadRoute contract | Keeps two specs and up to 16 cells off every cadence. R3 settles it. |

---

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
   merges and keeps one passes every log contract in the suite. Fixed by R9.
2. **The only save-content assertion is an integer**, and it is COMMIT-BLIND. This is
   the deepest verification gap in the system and it caps how much D3/D4/D5/D7/D18 can
   ever be trusted. Fixed by R9.
3. **Three expectation verifier families are declared and inert** (`route`, `rewind`,
   `loop`). A spec author can write assertions that silently do nothing, and S4.1
   already has. Fixed by R9.
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
to `Category = "General"`". **There are no bare declarations.** All 539 carry an
argument list and all 539 resolve to a real category: `General` does not appear among
the 97, and `hlib.parse_ingame_test_declarations` reports 0 unresolved. The 5-count
was an artifact of the counting method - 5 declarations use the fully-qualified
attribute form `[Parsek.InGameTests.InGameTest(...)]` (4 in `Ledger`, 1 in `Rewind`,
all in `IncompleteBallisticRuntimeTests.cs`), which a `[InGameTest(` scan misses and
`hlib` resolves. Three further `[InGameTest]` occurrences are prose inside comments,
which `hlib`'s noise mask correctly excludes. Every per-category number in this file
came from `hlib`, so none of them moved; only the reconciliation paragraph was wrong.
Rule this establishes: count in-game declarations with
`hlib.parse_ingame_test_declarations`, never with grep.
