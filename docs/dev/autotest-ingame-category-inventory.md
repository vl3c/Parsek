# In-game test category inventory (all 110 categories)

Machine-derived from `Source/Parsek` by `hlib.parse_ingame_test_declarations` +
`hlib.derive_batch_tally`. Do NOT hand-edit the table: re-derive it. The generator
that produced it is the same pair of pure functions
`harness/lib/test_hlib.py::CommittedBatchTallySourceSyncTests` uses, so a number here
that disagrees with the source is a bug in this doc, never in the source.

THAT INSTRUCTION IS ENFORCED, not merely requested:
`test_hlib.py::IngameCategoryInventoryDocTests` re-derives the category set and the
five machine-derivable columns of every row on each test run, so adding an
`[InGameTest]` anywhere - including to a category no spec pins - reds locally instead
of leaving a stale row the next wave plans against. The one column it cannot gate is
"Members with self-skip"; see the note on it below.

Status authority for the automated-testing system as a whole is
`docs/dev/autotest-status.md`; this file is the DETAIL it links to for the in-game
category axis. Counts stated in both must agree.

THE TABLE IS THE AUTHORITATIVE COUNT, not the headings. A heading like the title
above is prose that a wave can leave stale; the rows are what the sync cells
re-derive and gate. When the two disagree, read the table and fix the heading.

## What the numbers mean

- **Decls** - `[InGameTest(...)]` methods declaring this category anywhere in the mod
  assembly. This is what `BATCH_COMPLETE`'s `total=` counts for a single
  `RunCategory` batch: `allTests.Count(Status != NotRun)`, which INCLUDES the tests
  both filters skip.
- **Exec FLIGHT / SPACECENTER / TRACKSTATION** - how many survive BOTH runner filters
  at that scene, in the runner's order: `FilterSceneEligibleBatchCandidates` on scene
  first, then `PrepareBatchExecution` on `AllowBatchExecution = false` over what
  survived. A test failing both is counted once, in the scene bucket, exactly as the
  runner counts it. These three columns model the ORDINARY batch path only, which is
  what makes them the right derivation for the pinned `skipped=` floor of the 15
  ordinary specs. For the isolated path see the next entry.
- **Batch-disabled** - declarations carrying `AllowBatchExecution = false`.
  CORRECTED BY R5 (2026-07-27). This entry used to end "These are isolated-run-only;
  no `RunTests` batch ever executes one", and the second half is no longer true. A
  `RunTests` step carrying `isolated = "true"` routes to
  `PrepareBatchExecutionIncludingFlightRestore`, whose filter is
  `AllowBatchExecution || RestoreBatchFlightBaselineAfterExecution` - so a
  batch-disabled declaration that ALSO carries
  `RestoreBatchFlightBaselineAfterExecution = true` does execute in a batch, with a
  flight-baseline quickload restored after it. Re-derived over the tree, the
  batch-disabled population splits three ways:

  | Flags | Count | Batch-reachable? |
  |---|---|---|
  | `AllowBatchExecution = false` + `RestoreBatchFlightBaselineAfterExecution = true` | 68 | Yes, on an ISOLATED batch (R5) |
  | `AllowBatchExecution = false`, no restore flag | 3 (all `Periodicity`) | No - genuinely manual-only |
  | `RestoreBatchFlightBaselineAfterExecution = true` with `AllowBatchExecution` left true | 4 (`Contracts` 2, `TestCommands` 1, `LedgerGroundTruth` 1) | Yes, on EITHER path - not counted in this column |

  Those last 4 are worth knowing about because they look like they should
  discriminate between the two paths and do not: both filters admit them, and both
  prime and restore the baseline for them, so their tallies are identical either
  way. The one place the paths differ for them is degraded - when no flight baseline
  is available, the isolated path SKIPS them with a reason, where the ordinary path
  runs them against a restore that silently no-ops - and that difference is
  fail-closed. Derive the isolated-path column with
  `hlib.derive_batch_tally(..., isolated=True)`; it is not in this table because no
  gate reads it and an ungated column goes stale.
- **Members with self-skip** - members whose body, or a helper it calls, contains an
  `InGameAssert.Skip`. This is the run-time skip surface the attributes cannot
  predict. A zero here plus a non-zero Exec column is the strongest signal a category
  will actually execute end to end. TWO CAVEATS. (1) This column is the ONLY one in
  the table not machine-gated: resolving "a helper it calls" needs a call-graph walk
  whose name resolution over-approximates, so it is a hand-verified best effort, and
  `Pipeline-Smoothing`'s entry is the known correction - it reads 1, not 0, because
  `Pipeline_Smoothing_StructuralEvent_HandlersRegistered` reaches an
  `InGameAssert.Skip` through its private `AssertHandlerRegistered` helper (see the
  bucket-A note). (2) A zero does not mean a category cannot pass vacuously - see the
  fourth trap below.
- **Driven by** - the committed scenario spec that runs this category, or `-`.

Two limits of this table, stated so nobody over-reads it:

1. A zero in "Members with self-skip" does NOT mean a category cannot pass
   vacuously. A test that walks `RecordingStore.CommittedRecordings` and asserts a
   violation count is zero PASSES over an empty store having measured nothing, and
   several do exactly that behind a silent `return` / `yield break` with a Verbose
   log instead of an `InGameAssert.Skip` - so they are reported PASSED, not Skipped,
   and no batch tally can distinguish them. The fixture is the only defence; see
   "The fourth trap" below.
2. The Exec columns are a scene-eligibility ceiling, not a promise. They say what
   the two ATTRIBUTE filters allow, which is exactly what a pinned `skipped=` floor
   can be derived from and no more.


## The table

| Category | Decls | Exec FLIGHT | Exec SPACECENTER | Exec TRACKSTATION | Batch-disabled | Members with self-skip | Driven by | Bucket |
|---|---|---|---|---|---|---|---|---|
| `AutoMergeCommit` | 1 | 0 | 0 | 0 | 1 | 1 | - | B |
| `AutoRecord` | 10 | 0 | 0 | 0 | 10 | 10 | - | B |
| `BackgroundSeeder` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `Bug289` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `ClawCouple` | 2 | 2 | 0 | 0 | 0 | 2 | H42 (flown 2026-08-28, executes 2 of 2) | A |
| `Coalescer` | 2 | 0 | 0 | 0 | 2 | 2 | - | B |
| `ContinuationIntegrity` | 2 | 2 | 2 | 2 | 0 | 0 | - | B |
| `Contracts` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `CrewReservation` | 15 | 14 | 6 | 5 | 0 | 12 | H31 | A |
| `CrewReservationLive` | 2 | 2 | 2 | 2 | 0 | 2 | - | B |
| `DataHealth` | 4 | 4 | 4 | 4 | 0 | 0 | H14 | A |
| `DisabledHoverEcho` | 1 | 1 | 1 | 1 | 0 | 1 | - | B |
| `Diagnostics` | 6 | 6 | 3 | 3 | 0 | 1 | H27 | A |
| `EvaSpawnPosition` | 2 | 2 | 0 | 0 | 0 | 2 | H20 | A |
| `FinalizeBackfill` | 7 | 7 | 0 | 0 | 0 | 0 | H10 | A |
| `FinalizeLimbo` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `Flight` | 2 | 2 | 0 | 0 | 0 | 1 | - | B |
| `FlightIntegration` | 4 | 4 | 0 | 0 | 0 | 0 | H17 | A |
| `ForwardRender` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `GameActionsHealth` | 4 | 4 | 4 | 4 | 0 | 3 | B10 / L1 | B |
| `GhostAudio` | 9 | 8 | 3 | 2 | 0 | 1 | H30 | A |
| `GhostChains` | 4 | 4 | 4 | 4 | 0 | 4 | H50 (flown 2026-08-28, executes 4 of 4) | A |
| `GhostLifecycle` | 17 | 15 | 0 | 2 | 0 | 17 | - (see the 2026-08-29 note) | B |
| `GhostMap` | 25 | 16 | 0 | 9 | 0 | 11 | S1.6, H44 (TRACKSTATION slice, flown 2026-08-28, executes 9 of 25 - the WHOLE TS slice, zero run-time skips) | B |
| `GhostMapOrbits` | 2 | 2 | 1 | 1 | 0 | 1 | - | B |
| `GhostPlayback` | 42 | 41 | 1 | 1 | 1 | 12 | S1.4 | B |
| `GhostVisuals` | 4 | 4 | 3 | 3 | 0 | 0 | H15 | A |
| `IdentityLoss` | 3 | 3 | 0 | 0 | 0 | 3 | - | B |
| `IncompleteBallistic` | 11 | 11 | 0 | 0 | 0 | 0 | H9 | A |
| `KSP` | 6 | 6 | 4 | 4 | 0 | 0 | H13 | A |
| `KspApiSanity` | 5 | 5 | 3 | 3 | 0 | 3 | H24 | A |
| `Ledger` | 4 | 0 | 4 | 0 | 0 | 4 | H48 (flown 2026-08-28, executes 4 of 4) | A |
| `LedgerGroundTruth` | 2 | 2 | 0 | 0 | 0 | 1 | L2 | B |
| `LocalizedName` | 3 | 3 | 3 | 3 | 0 | 0 | H29 | A |
| `LogContracts` | 10 | 10 | 8 | 8 | 0 | 2 | H26 | A |
| `Logistics` | 47 | 8 | 2 | 1 | 38 | 46 | H34 (SPACECENTER slice), H35 (FLIGHT ordinary slice), H38 (FLIGHT ISOLATED on a built pad rig, flown 2026-08-28, executes 39), H39 + H40 (the same ISOLATED slice on RECORDED hosts, both flown 2026-08-28, executing 34 and 35), RVR-1 (the same ISOLATED slice on the TARGET-BRANCH recorded host `rover-route-recorded`, authored 2026-08-30, NEVER FLOWN - predicted to convert the two dock-window cells H39/H40 both measured as unpayable by existing bytes, at the cost of the initiator cell they pin). Union across the five FLOWN slices: 42 of 47 | B |
| `LogisticsGrapple` | 4 | 3 | 0 | 0 | 1 | 2 | H41 (ISOLATED, flown 2026-08-28, executes 3 of 4; the 4th wants a harvested Grapple window) | A |
| `MapPresence` | 5 | 5 | 3 | 3 | 0 | 2 | H28 | A |
| `MapRender` | 22 | 21 | 0 | 0 | 1 | 14 | S1.7 | B |
| `MapView` | 4 | 3 | 3 | 4 | 0 | 2 | H47 (flown 2026-08-28, executes 4 of 4) | A |
| `MergeDialog` | 2 | 0 | 0 | 0 | 2 | 2 | - | B |
| `MissionPhasing` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `Missions` | 13 | 7 | 6 | 0 | 0 | 9 | M1, H54 (FLIGHT slice, flown 2026-08-28, executes **3 of 13** - see the archetype note below) | B |
| `Optimizer` | 2 | 0 | 2 | 0 | 0 | 2 | - | B |
| `PartEventFX` | 6 | 6 | 0 | 0 | 0 | 6 | - | B |
| `PartEventFidelity` | 5 | 5 | 0 | 0 | 0 | 5 | H37 | A |
| `PartEventTiming` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `Periodicity` | 13 | 1 | 9 | 0 | 3 | 5 | M2 | B |
| `Pipeline-Anchor` | 7 | 7 | 0 | 0 | 0 | 0 | H11 | A |
| `Pipeline-Anchor-BubbleEntry` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-AnchorPropagate` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-Frame` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-Outlier` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-Smoothing` | 4 | 4 | 0 | 0 | 0 | 1 | H18 | A |
| `Pipeline-Terrain` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `PlaybackControl` | 1 | 0 | 0 | 0 | 1 | 1 | - | B |
| `PlaybackFidelity` | 7 | 7 | 0 | 0 | 0 | 7 | H36 | A |
| `PreParsekBackup` | 4 | 4 | 4 | 4 | 0 | 4 | PPB-1 | A |
| `QuickloadResume` | 3 | 1 | 0 | 0 | 2 | 1 | - | B |
| `ReFlyWorldPreservation` | 6 | 6 | 0 | 0 | 0 | 6 | S4.2 | A |
| `ReStockCompat` | 9 | 9 | 0 | 0 | 0 | 9 | - | B |
| `RecordedSignals` | 3 | 3 | 1 | 1 | 0 | 2 | H33 | A |
| `Recording` | 1 | 0 | 1 | 0 | 0 | 0 | - | B |
| `RecordingFinalization` | 3 | 3 | 0 | 0 | 0 | 0 | H19 | A |
| `RecordingInvariants` | 2 | 2 | 0 | 0 | 0 | 0 | H5 | B |
| `RecordingStore` | 2 | 2 | 2 | 2 | 0 | 0 | - | B |
| `ReentryFx` | 3 | 3 | 0 | 0 | 0 | 1 | H52 (flown 2026-08-28, executes 3 of 3) | A |
| `RenderComposition` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `ResourceManifest` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `ResourceReconciliation` | 1 | 0 | 1 | 0 | 0 | 0 | - | B |
| `ResourceTopBar` | 2 | 0 | 2 | 0 | 0 | 2 | - | B |
| `RevertFlow` | 1 | 0 | 0 | 0 | 1 | 1 | - | B |
| `RevertVesselStrip` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `Rewind` | 38 | 26 | 6 | 0 | 6 | 24 | R7a / R7c | A |
| `RewindSaves` | 1 | 1 | 1 | 1 | 0 | 1 | - | B |
| `RouteDockCapture` | 6 | 0 | 0 | 0 | 6 | 6 | H55 (ISOLATED, AUTHORED 2026-09-02 and NEVER FLOWN; the ordinary path executes 0 of 6, so the isolated arg is the only way to run any of it. Pins `total=6` exactly with `passed=`/`skipped=` interim and `failed=0` asserted, plus one REQUIRED token per cell) | B |
| `RouteLifecycle` | 8 | 8 | 8 | 8 | 0 | 8 | RVR-3 (flown once 2026-09-01, `total=6 passed=4 failed=2`; the two failures were CONTRACT DRIFT - see the triage note - and the category is now 8 cells pinning BOTH halves of the armed-pause state machine. Pins `total=8` exactly with `passed=`/`skipped=` interim, `failed=0` asserted, plus a REQUIRED PASS token per resolution cell, and declares no render-composition expectations block, which is what keeps the five crossing cells from self-skipping) | B |
| `RouteLiveAnchor` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `RouteRewindTimeline` | 7 | 7 | 7 | 7 | 0 | 1 | H6 | B |
| `SaveLoad` | 4 | 4 | 4 | 4 | 0 | 2 | H51 (flown 2026-08-28, executes 4 of 4) | A |
| `SceneAndPatch` | 7 | 4 | 3 | 2 | 0 | 4 | H53 (FLIGHT slice, flown 2026-08-28, executes **2 of 7** - two residual skips are DRIVER-state, see below) | B |
| `SceneExitMerge` | 2 | 0 | 0 | 0 | 2 | 2 | H21 | A |
| `Serialization` | 4 | 4 | 4 | 4 | 0 | 1 | H25 | A |
| `Settings` | 5 | 4 | 3 | 4 | 0 | 2 | H46 (flown 2026-08-28, executes 4 of 5; the 5th is TRACKSTATION-scoped) | B |
| `SnapshotBaseline` | 7 | 7 | 0 | 0 | 0 | 7 | H32 | A |
| `SoiCrossingPlayback` | 3 | 3 | 0 | 0 | 0 | 3 | S1.8 | A |
| `SpawnCollision` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `SpawnHealth` | 3 | 3 | 3 | 3 | 0 | 0 | H16 | A |
| `SpawnRotation` | 10 | 10 | 0 | 0 | 0 | 0 | H8 | A |
| `SpawnTerminalOrbit` | 3 | 3 | 0 | 0 | 0 | 3 | - | B |
| `Spawner` | 2 | 2 | 0 | 0 | 0 | 1 | - | B |
| `StockUiOverlay` | 6 | 0 | 6 | 0 | 0 | 6 | H45 (flown 2026-08-28, executes 4 of 6; the 2 Mission Control cells want an OFFERED contract) | A |
| `StockWarpLimits` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `StrategyLifecycle` | 10 | 0 | 10 | 0 | 0 | 10 | L3 | A |
| `Structure` | 2 | 2 | 2 | 2 | 0 | 2 | - | B |
| `SwitchIntentPatch` | 3 | 1 | 1 | 1 | 0 | 0 | - | B |
| `SwitchSegment` | 6 | 6 | 0 | 0 | 0 | 0 | H12 | A |
| `TerminalOrbit` | 2 | 2 | 2 | 2 | 0 | 2 | - | B |
| `TerrainClearance` | 6 | 6 | 0 | 0 | 0 | 6 | H43 (flown 2026-08-28, executes 6 of 6) | A |
| `TestCommands` | 4 | 3 | 1 | 1 | 0 | 3 | - | B |
| `TestRunner` | 2 | 2 | 2 | 2 | 0 | 0 | - | B |
| `TestRunnerIsolation` | 2 | 1 | 2 | 1 | 0 | 1 | - | B |
| `TrackingStation` | 10 | 0 | 0 | 9 | 1 | 3 | H23 | A |
| `TrajectoryMath` | 8 | 8 | 8 | 8 | 0 | 0 | H7 | A |
| `TreeIntegrity` | 4 | 4 | 4 | 4 | 0 | 3 | H49 (flown 2026-08-28, executes 4 of 4) | A |
| `UiComplexityMode` | 4 | 4 | 0 | 0 | 0 | 4 | H22 | A |
| `Unity` | 4 | 4 | 4 | 4 | 0 | 1 | - | B |
| `WarpToTime` | 1 | 0 | 1 | 0 | 0 | 1 | - | B |
| `Watch` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `WaterfallCompat` | 8 | 8 | 0 | 0 | 0 | 7 | - | B |

## Triage

Totals, re-derived: **110 categories / 613 declarations**. Buckets **A 34 categories
(235 declarations)**, **B 76 categories (378 declarations)**, **C 0 categories (0
declarations)**. The 107th is `AutoMergeCommit` (R4, the AUTOMERGE-ON-BY-DEFAULT
wave; the 106th is `DisabledHoverEcho`, landed the same week): the plan-§7
autoMerge=ON scene-exit cell, batch-disabled and restore-backed exactly like the two
`SceneExitMerge` cells it mirrors, and in bucket **B** because no committed spec
drives it yet - the cell exists and is the coverage that was missing; pointing a spec
at it is the next step, not this one.
The 108th is `PreParsekBackup` (PPB-1 / PPB-2), and unlike the two above it
ships DRIVEN and in bucket **A** from its first commit - both specs flew green
on 2026-08-29.
The 109th is `RouteLifecycle` (RVR-3, 2026-08-30): EIGHT scene-agnostic cells driving
the supply-route send-once / pause lifecycle against the PRODUCTION
`LiveRouteRuntimeEnvironment` inside a live session - the live gate for the
blocked-then-paused fix. Bucket **B**: the cells exist and are batch-safe, but the
scenario spec that pins their tally, `RVR-3-route-lifecycle`, has flown ONCE and not
green, so the category stays in **B** until a clean census (deliberately its OWN
category rather than `Logistics`, whose `total=47` is pinned by four flown committed
specs plus the authored RVR-1).

WHAT THE FIRST FLIGHT SETTLED, and why the row moved 6 -> 8. RVR-3 flew
2026-09-01 measuring `total=6 passed=4 failed=2 skipped=0`. Both failures were
CONTRACT DRIFT in the cells rather than a Parsek defect: the category was authored
before PR #1583 landed, and that PR made `SourcesStale` / `WaitingForPartner`
POSTPONEMENT holds that deliberately KEEP an armed pause
(`RouteOrchestrator.IsPostponementHold`). Every cell reached the ERS gate first - a
synthetic route's fresh-GUID source ids are in no ERS snapshot - so the flight
measured only the postponement half, and `blocked-then-paused`, the reason the
category exists, had no live gate at all. The two cells were rewritten to pin the
SHIPPED postponement contract (arm KEPT, no pause, no marker row, no toast) and TWO
new cells were added to reach a RESOLUTION kind on purpose: they point the synthetic
route's `SourceRefs` at a real id out of the live ERS snapshot and its stop at a real
live vessel pid, then hand it a delivery manifest no craft can hold, so the
destination-capacity gate answers `DestinationFull` - which is not a postponement, so
the arm is consumed. The lesson generalises past this category: a cell that reaches
its assertion through the FIRST gate that refuses is measuring that gate, not the
contract it was written for, and only naming the expected kind in a pre-flight guard
makes the difference visible.
The 110th is `RouteDockCapture` (H55, 2026-09-02): SIX self-provisioning cells that
spawn docking ports, tanks and cargo containers beside the active vessel, couple them
in, move real resources and real stored cargo across the docked window and undock -
producing the roadmap's Tier B supply-route subjects (delivery, pickup + mixed
direction, EVA-construction part-set drift, multi-stop, round-trip) in-session instead
of costing a manual flight each - plus the origin-proof INSTRUMENT that decides whether
Tier B item 4 is a flight or a bug fix. The docking-port sibling of `LogisticsGrapple`,
and its own category for the same reason that one is: `Logistics`' `total=47` is pinned
by five committed specs. Bucket **B**: the whole category is driven by one committed
spec, but that spec has NEVER FLOWN, so it moves to **A** on its first clean census -
`RouteLifecycle`'s standing, arrived at from the other side.

Driven by a committed spec: **46 of 110 categories**, up from 35
across six waves - `ReFlyWorldPreservation` via S4.2, `RecordedSignals` via H33,
`SnapshotBaseline` via H32, and `Logistics` via H34 all landed together in one merge
(the S1.8 SoiCrossingPlayback wave had taken it to 35 from 34, and 28 and 8 the waves
before), then `PlaybackFidelity` via H36 and `PartEventFidelity` via H37. Measured
against declarations rather than categories, that is 418 of 613 inside a driven
category (was 318 before these waves: 324 after S4.2, 327 after H33, 334 after H32,
381 once `Logistics` counted, 388 with `PlaybackFidelity`, 393 with
`PartEventFidelity`, and 401 once L3's capture matrix took `StrategyLifecycle` from 3
declarations to 7, then 403 when this inventory's `LedgerGroundTruth` row caught
up with L2 - driven and armed since 2026-08-17, the row had lagged the spec - then 405
when the `rewind-recovery-bundle` wave took `Rewind` from 37 to 38, and 407 when the
`rep-debit-capture` wave took `StrategyLifecycle` from 7 to 9, adding the
reputation-INPUT converter measurement cell and the high-reputation curve
corroboration cell to the same L3 pair, and 408 when the `funds-debit-capture`
wave took it from 9 to 10 with the funds-INPUT converter measurement cell, and 412
when the `preparsek-backup-lane` wave added `PreParsekBackup` with 4 declarations,
driven from birth by PPB-1). `H35-logistics-route-proof` (2026-08-11) moves NEITHER number -
it is the second spec on a category H34 already counted - which is exactly the
distortion the paragraph after next is about, and
`H38-logistics-isolated` (2026-08-28) moves neither for the same reason, being
the THIRD spec on that same already-counted category.

`PreParsekBackup` (4 declarations, PPB-1 / PPB-2) is the 106th category and the
first one that ships DRIVEN AND IN BUCKET A from its first commit. It exists because
the pre-Parsek safety backup's four runtime properties had a manual runbook nobody
ever ran (docs/dev/todo-and-known-bugs.md), and because the reason they could not be
automated was a FIXTURE gap, not a harness gap: the backup fires only on a save with
no Parsek footprint that is also not brand-new-empty, and no committed fixture was
both until `preparsek-untouched-career` was built. All four cells are `AnyScene` (the
lane drives them from a FLIGHT-routing career and a SPACECENTER-routing brand-new
one), all four are batch-allowed, and all four carry a run-time `InGameAssert.Skip`
naming the missing context - which is why PPB-2's honest pin is `passed=2 skipped=2`
while PPB-1's is `passed=4 skipped=0`.

`RenderComposition` (1 declaration, M-A7) is the 105th category and moves the DRIVEN
numbers not at all: it ships undriven, in bucket B, waiting on the `renderCompose`
verifier row's lanes (Phase 3+ of `design-autotest-render-composition.md`). The one
cell is a WELL-FORMEDNESS gate - it force-arms the render-composition recorder, lets
the live flight scene drive the real capture path, exports, and asserts the manifest
parses with every section present and record counts matching the export payload. It
deliberately encodes no RC-* composition rule: those are evaluated in Python alone,
because a rule written in both languages is the second-copy drift that design
forbids for its own constant catalog. Scene = FLIGHT, batch-allowed, and it
self-skips with a named requirement when there is no active vessel or when the
session is already env-armed (running then would Reset the session's own
accumulation), so it is safe in any batch.

`ReFlyWorldPreservation` (6, driven by `S4.2-refly-world-preservation`) arrived with
its driver rather than as an undriven category: it was authored for the
REFLY-DELETES-NON-SLOT-WORLD fix, whose xUnit coverage is ConfigNode-level and
pure-predicate and therefore cannot observe the live post-load scene where the bug's
second deleting layer lived. All six members are Scene = FLIGHT and batch-allowed,
and all six self-skip with a named requirement when no Re-Fly session is live, so the
category is safe in any batch and vacuous in none.

`PlaybackFidelity` (7 declarations, wired as `H36-playback-fidelity`) is the live
half of P5/P6 - plume magnitude, deployable interpolation and synthesized motion. It
arrived with its driver rather than as an undriven backlog row, on the same ground
`ReFlyWorldPreservation` did: the headless suite owns every pure decision these
features make, and what is left over is exactly what needs a scene - a captured FX
baseline on a CLONED `KSPParticleEmitter`, an interpolated pose on a real
`Transform`, and `Quaternion.AngleAxis`, a native call that throws in a headless
process. All seven are Scene = FLIGHT and batch-allowed, so its attribute-derived
skip floor is 0; all seven ALSO self-skip with a named requirement when the install
gives them no usable prefab, which is why `H36` ships with an interim (loose
`passed=` / `skipped=`) pin until a flight measures the split. Bucket A1 by
admission criterion 1 and A1's exception 2 - the same standing `H26` / `H28` / `H31`
have.

`RecordedSignals` (3 declarations, wired as `H33-recorded-signals`) exists for the
live half of the 2026-08-09 part-action recording audit - the one step of the
wheel-spin fix (Unity's `AngleAxis` handedness) that no headless cell can reach, the
parachute cap restore at transform level, and the ground-contact gate that keeps a
rover riding a launch vehicle from spinning its wheels at orbital speed. Two of its
three cells carry run-time self-skips (its row's "Members with self-skip" column
reads 2), which is why its spec was pinned interim when it landed; the 2026-08-11
flight measured `total=3 passed=3 failed=0 skipped=0` and the pin is now whole.

`SnapshotBaseline` (7, driven by `H32-snapshot-baseline`) likewise arrived ALREADY in
bucket A rather than as a bucket-B backlog row: the category was authored together
with its scenario, for the M1 ghost snapshot-baseline fix. Its spec was also pinned
interim on landing, on the expectation that the stock-minimal profile might carry
neither Breaking Ground robotics nor deployable prefabs whose clips separate stow
from deploy; the 2026-08-11 flight measured `total=7 passed=7 failed=0 skipped=0` and
disproved both, so that pin is whole too.

`PartEventFidelity` (5 declarations, wired as `H37-part-event-fidelity`) is the live
half of P8, and it arrived with its driver on the same ground as the two above. Four
of its cells cover the wave's new signals - a broken deployable's subtree hide plus
its repair un-hide and loop-cycle re-show, the converter running loop's motion and
cyclic wrap and stop, the empty-deploy-name (large ISRU) shape of that loop, and the
EVA jetpack plume's lazy build and three-flag gate. THE FIFTH IS DIFFERENT IN KIND and
worth naming: it pins a NEGATIVE decision. P8 deliberately records nothing for the
science timeline because the science-experiment deploy visual was ALREADY recorded
through the ModuleAnimateGeneric path, and the audit's §2 wording was imprecise enough
to make that verdict look wrong. The cell asserts both halves of the claim on a live
prefab, so if the verdict ever stops being true it REDS instead of a doc sentence
quietly rotting. All five are Scene = FLIGHT and batch-allowed (attribute-derived skip
floor 0) and all five self-skip with a named requirement when the install gives them
no usable prefab. `H37` shipped with an interim `passed=` pin and is now LIVE-PROVEN:
after a first flight that red 3/5, the 2026-08-12 re-fly measured `total=5 passed=5
failed=0 skipped=0`, all five guards satisfied on stock-minimal, so the pin is literal,
no `RUNTIME_SKIPS` entry is owed and `IngameBatchWiringGroupTests.INTERIM_PIN_IDS` is
EMPTY again. Bucket A1, same standing as `H36`.

WHAT ITS FIRST FLIGHT COST AND BOUGHT, because it is the most useful thing this category
has produced so far: neither non-green cell was the install shortfall the interim pin was
hedging against. The RED was a PRODUCT defect (`ParticleSystem.Play()` on a ghost that is
not `activeInHierarchy` is a SILENT no-op, and a ghost is inactive throughout its
spawn-time prefix replay, so an EVA ghost spawning mid-burst stayed dark for the whole
burst while the log claimed it was emitting). The SKIP was a TEST defect - the Goo cell's
precondition tested POSITION only while a science canister's `Deploy` clip swings its
doors, and the re-fly's measured `span(pos=0 rot=29.99998deg)` is that diagnosis in one
number. The transferable lesson is about CELL STYLE rather than about either bug: the
other four cells passed on that first flight while the plume cell measured a lie, because
they read `activeSelf` flags and sampled poses, both of which are readable on an INACTIVE
hierarchy. Anything Unity silently refuses to do while inactive is invisible to that style
of cell, so a category whose claims depend on Unity ACTING should activate its fixture
through the production seam (`GhostPlaybackEngine.ActivateGhostVisualsIfNeededForTesting`),
as `H37` now does.

READ THAT 381 CAREFULLY - the last 47 of it are the largest single-spec jump in
this row's history and the least representative. `Logistics` contributes all 47 of its
declarations to "inside a driven category", and the honest execution count has moved
twice. WHEN TWO SPECS DROVE IT they EXECUTED **6 distinct declarations**: H34 runs 2 at
SPACECENTER (the inter-body builder-shape gate and the AnyScene tooltip cell) and H35
runs 5 at FLIGHT (the probe-admission cell, the prelaunch origin-proof cell, the
active-as-initiator route-proof cell, the mid-tree shuttle cell, and the same AnyScene
tooltip cell, which is why the union was 6 and not 7). The other 41 executed nowhere:
38 carried `AllowBatchExecution = false`, and 3 self-skipped on fixture shape.
**SINCE 2026-08-28 THAT NUMBER IS 42, AND IT IS NOW DERIVED MECHANICALLY** rather than
argued: `re.findall` over the `PASSED:` / `FAILED:` lines of the three isolated lanes'
green census logs (`2026-08-28_1833`, `_2119`, `_2122`), unioned with what H34 and H35
execute. The steps, because each one carries a reading:
`H38-logistics-isolated` executed **39 of the 47 in one batch**
(`total=47 passed=39 failed=0 skipped=8`) on a purpose-built pad rig with an empty
store. `H39-logistics-isolated-bdock` executed **34** and added **+2** -
`RouteProof_ActiveAsInitiatorDockWindow` and `RouteOriginProof_StartedOnRunway`, the
two that need recorded state a fresh pad fixture cannot carry.
`H40-logistics-isolated-depot-route` executed **35** and added **+0**: the same 2 over
H38, nothing over `H38 ∪ H39`. So the three isolated lanes union to **41**. H35's five
are all inside it; H34 adds the **+1** SPACECENTER inter-body cell that scene-skips at
FLIGHT. **41 + 1 = 42 of 47.**
TWO READINGS FALL OUT OF THAT and neither flatters a headline number. H40 adds no
declaration at all - its value is a new EXECUTION CONTEXT (the only committed Active
route) and the nine-cell defect family it found, not coverage. And the recorded hosts
COST as well as buy: H39 executes 5 fewer cells than H38 and H40 four fewer, because
both trade craft capability (a `BaseConverter`, a second inventory container, a stocked
container) for corpus.
ONLY FIVE DECLARATIONS NOW EXECUTE NOWHERE:
`RouteOriginProof_StartedDockedToNonKsc`, `RouteProof_ActiveAsTargetDockWindow`,
`RouteProof_CrossTreeCommittedPartner`, `HarvestCapture_CatchUpOnLoad` and
`HarvestRoute_AnalyzesEligible` - all five a missing recorded subject rather than a
rig defect. The first three are now a HARVEST requirement PROVEN on two independent
recorded hosts rather than predicted on one: every committed
`ROUTE_CONNECTION_WINDOWS` node in the suite is initiator-branch, because the docking
craft are `Kerbal X` descendants sharing one BAKED `persistentId`, so closing the
target and cross-tree cells needs a recorded dock between craft with DIFFERENT baked
pids and no existing bytes can supply it. The declaration
measure has always counted category membership rather than execution, so this is not
a new distortion, but at 47 declarations it is the first time the gap is big enough
to mislead on its own - and the additions moved the honest number 2 -> 6 -> 39 -> 42
while moving the headline number by zero. The bucket letters are
unchanged: `Logistics` stays **B**, on the same footing as `GhostMap` (S1.6),
`GhostPlayback` (S1.4) and `Missions` (M1) - driven, partially, without meeting
bucket A1's whole-category admission shape.

THE THIRD SPEC IS THE ONE THAT MOVED THAT HONEST NUMBER, and by a lot:
`H38-logistics-isolated` (authored AND FLOWN 2026-08-28) drives
the category through the ISOLATED entry point, where the admission filter is
`AllowBatchExecution || RestoreBatchFlightBaselineAfterExecution` and all 38 of
the batch-disabled declarations carry the restore flag, taking the admitted
population from 8 to 46.

ADMITTED IS NOT EXECUTED, so the number that matters is the MEASURED one, and the
authored form of this paragraph deliberately claimed nothing until a flight
produced it. It now has. Reading run 1 (`2026-08-28_1802`, PARSEK-FAIL) censused
`passed=36 failed=2 skipped=9` and produced three findings - one PRODUCT defect (the
D4 harvest poll not rails-gated for surface vessels, fixed at `f98d5477a`) and two
test defects, one of which was a reflection probe that resolved silently null and so
self-SKIPPED instead of failing. Run 2 (`2026-08-28_1833`, PASS attempt 1) over the
fixed DLL measured `total=47 passed=39 failed=0 skipped=8`, now pinned whole. So the
41 that executed nowhere is 5, not 3: the run-time self-skips are 7 rather than the
3 the ordinary path saw, because the isolated batch reaches cells whose preconditions
the ordinary one never even tried - and two of the seven are covered by H35's
recorded fixture, leaving 5 uncovered across all three slices. Both of the
assumptions only a Logistics batch could settle held on the first flight: A4
(inventory PROBE ORDER as KSP actually walks it) and A5 (the live resource floors).
Not one of the eight remaining skips is a rig property.

THE BUCKET LETTER IS STILL **B**, and this is worth stating explicitly now that the
number is 42 of 47. Bucket A1 is a WHOLE-CATEGORY admission shape, not a high
execution count: five declarations still execute nowhere, and three specs across two
scenes and two entry points is not one admitted category. `Logistics` leaves **B**
when the remaining five have a fixture, not when the count gets large.

The 2026-08-05 wave (`wire-wave-2`, H26-H31) wired exactly the list the previous
revision of this doc named as "the honest next wave": all five B6 members that
were still undriven (`LogContracts` 10 as H26, `Diagnostics` 6 as H27,
`MapPresence` 5 as H28, `LocalizedName` 3 as H29, `GhostAudio` 9 as H30) plus
`CrewReservation` (15, as H31) from B4. Every spec followed the per-test
skip-precondition read B4 demands; the two fixture substitutions that fell out of
it (H26 on career-pad-craft for the CAREER-gated resource cell, H31 on
b2-lko-craft as the ONLY committed fixture with both free crew seats and spare
Available kerbals) and the SHIP_VOLUME provision finding that would have
hard-failed H30 are recorded in the specs and in todo-and-known-bugs.md (the W2
entries). See the B6 and B4 sections below for what each wiring settled.

The 2026-08-04 wave (`wire-rewind-block`, roadmap R7 + two R8 stragglers) added
three: `Rewind` (37 declarations - the largest undriven category in the tree, and
the one two earlier waves skipped because 24 of its 37 carry run-time self-skips),
`KspApiSanity` (5) and `Serialization` (4). `Rewind` is driven by TWO specs, and
the reason is a property of the category rather than of the fixture: it is bimodal
on the live re-fly session, so no single boot can execute both halves. See bucket
B4 below, which is where it used to sit.

**BUCKET C IS EMPTY as of 2026-07-30.** Its last and only remaining member was
`TrackingStation`, held there by sub-reason C2 ("the scene is unreachable from the
command seam"). R12 shipped `LoadGame scene=trackstation`, `H23-tracking-station`
flew green the same day, and C2 is closed - see the C2 entry below. Bucket C existing
with nothing in it is worth keeping as a category, not deleting: it is the bucket a
future "we cannot reach this at all" finding belongs in.

R5 MOVED 6 CATEGORIES OUT OF BUCKET C (2026-07-27). Bucket C's C1 sub-reason held
that `AutoRecord`, `Coalescer`, `MergeDialog`, `SceneExitMerge`, `PlaybackControl`
and `RevertFlow` were not batch-runnable at all - that changing it "means
re-examining each test's reason for being isolated-only, which is a C# question, not
a harness one". That was true of the seam as it stood and false as a statement about
the runner: all 18 of those declarations already carried
`RestoreBatchFlightBaselineAfterExecution = true`, and the runner already had an
entry point that admits them. Only the unattended CALLERS were missing. R5 added the
`isolated` argument, so one of the six (`SceneExitMerge`, wired as `H21`) is now in
bucket A and the other five are in bucket B, needing a spec and a launchable fixture
rather than a C# redesign. C retains only C2, which R5 does not touch.

The 22-declaration gap between 207 and 185 is entirely in the eight PRE-EXISTING
driven categories - all 78 declarations these groups add execute. Decomposed, because
the one-line summary "the SPACECENTER categories scene-skip" is wrong on all three
counts (half the gap is at FLIGHT, one of the three SPACECENTER categories
contributes nothing, and 4 of the 22 are not scene skips at all):

| Category | Scene driven | Scene-skipped | Batch-disabled | Gap |
|---|---|---|---|---|
| `GhostMap` | FLIGHT | 9 | 0 | 9 |
| `Missions` | SPACECENTER | 7 | 0 | 7 |
| `Periodicity` | SPACECENTER | 1 | 3 | 4 |
| `GhostPlayback` | FLIGHT | 0 | 1 | 1 |
| `MapRender` | FLIGHT | 0 | 1 | 1 |
| `GameActionsHealth` | SPACECENTER | 0 | 0 | 0 |

The constraint that shapes every decision below: `hlib.SINGLE_BATCH_SELECTOR_RULE`
makes a batch-owning spec drive exactly ONE `RunTests` step naming exactly ONE
category, because the anti-vacuity probe is built for a single named category and a
`category=multi:<n>` aggregate cannot express "constituent B executed nothing". So
one wired category costs one KSP boot per cadence. Wiring all 74 undriven categories
would mean 89 boots. The question is never "can this category run in a batch" but
"is what it executes worth a boot".

### Bucket A - wired now (30 categories, 181 declarations)

Two sub-classes, admitted on DIFFERENT grounds. Conflating them is how the isolated
spec would end up pinned against the wrong derivation.

**A1 - the ordinary batch path (28 categories, 168 declarations).** Fourteen shipped
as one wave, `H7`-`H20`, tier `nightly`, over the committed `gloops-airshow`
fixture; `H22-ui-complexity-mode` joined afterward with the Basic/Advanced UI-mode
feature, tier `daily`, over the same fixture. The admission test each had to pass:

1. Every declaration survives both runner filters at FLIGHT (Exec FLIGHT == Decls),
   so the attribute-derived `skipped` floor is 0.
2. No member, and no helper any member calls, contains a reachable
   `InGameAssert.Skip` - checked directly and transitively, not by a per-file grep
   (`IncompleteBallisticRuntimeTests.cs` does contain `InGameAssert.Skip` calls - 4
   in `Ledger` members, 1 in `RouteLiveAnchor`, 1 in `TestRunnerIsolation` - so a
   per-file answer would have been wrong for both `IncompleteBallistic` and
   `SwitchSegment`, neither of which had any at the time the wave shipped).
   THREE MEMBERS OF BUCKET A DO NOT SATISFY THIS CRITERION AS WRITTEN, and saying so
   is cheaper than a footnote nobody reads. (a) `Pipeline-Smoothing`'s
   `Pipeline_Smoothing_StructuralEvent_HandlersRegistered` reaches an
   `InGameAssert.Skip` through its private `AssertHandlerRegistered` helper. That
   branch fires only if reflection cannot find `EventData<T>`'s internal `events`
   field, i.e. if a KSP version renamed it, which is unreachable on the pinned
   1.12.5. It is admitted on the narrower ground that the skip is a KSP-VERSION
   guard rather than a fixture-context guard, and `H18` documents it at length.
   (b) ALL THREE `UiComplexityMode` cells carry in-body `InGameAssert.Skip` guards
   (no live `ParsekUI`, Gloops recording in progress). Those ARE fixture-context
   guards, so `H22` is admitted on the same ground as `H20` rather than on this
   criterion: its `skipped=0` is a claim about `gloops-airshow` that a live run
   settles, and the 2026-07-28 flight settled it.
   (c) `IncompleteBallistic` STOPPED satisfying it on 2026-08-05, when the two
   `FrameCalibration_Site1_*` / `FrameCalibration_Site4_*` probes joined it - and a
   THIRD, `RecorderEncodedOrbitalFrameRotationResolvesToTheVesselAttitude`, joined
   later the same day with the fifth-frame-mismatch fix (branch
   `orbital-rotation-frame`). All three read the LIVE vessel and guard on it (no
   active vessel / no orbit / no main body / `FlightGlobals.Bodies` unavailable / a
   live orbit whose elements are not propagatable / a degenerate recorder encode),
   so `H9`'s `skipped=0` is now a fixture claim for those three cells in exactly the
   way `H20`'s and `H22`'s are, while staying a derivation for the original eight. Those two probes were also, briefly, the one place in bucket A
   where `failed=0` was EXPECTED TO BE VIOLATED: they were built to MEASURE the
   frame mismatch `docs/dev/todo-and-known-bugs.md` pins at sites 1 and 4, and
   `H9` red by design on runs `2026-08-04_2142` (`failed=2`) and `2026-08-04_2224`
   (`failed=1`) while that calibration was taken. The calibration landed on those
   numbers and the residual it exposed was fixed the same day, so as of confirm
   run `2026-08-04_2323` (PASS, both probes at 0.000) `H9` meets `failed=0` as a
   MEASUREMENT and the two cells are permanent frame-regression guards. What
   survives from this note is the `skipped=0` point above - that half is still a
   fixture claim, not a derivation. `H9` documents the arc at length.
3. The fixture already exists and its route is known.

That is what let 13 of the 14 pin their tally WHOLE (`total=N passed=N failed=0
skipped=0`) from a source derivation rather than a guess before any of them had flown.
`H20-eva-spawn-position` was the exception - both its cells carry run-time
`InGameAssert.Skip` guards and one is undecidable from source - so it shipped with the
honest interim form instead of an invented number.

**ALL 15 NOW PIN WHOLE, every one off a MEASURED line.** The wave of 14 flew
2026-07-27 and `H20` was re-flown alone afterwards so its log would survive to be read
(`total=2 passed=2 failed=0 skipped=0`; the endpoint-overlap probe fired and the
walkback path executed). `H22` arrived later with the Basic/Advanced UI-mode feature
and flew on its own on 2026-07-28 (`total=3 passed=3 failed=0 skipped=0`, 53 s, under
its pre-rename id `H7-ui-complexity-mode`). One asymmetry survives and is worth
carrying: for 13 of the 15, `skipped=0` is ALSO derivable from the attributes plus a
reachable-Skip scan, so the pin can be re-derived after a source change. For `H20` it
is measured only, and a fixture change that moves the host's collider geometry can
legitimately make its walkback cell skip. Read that as a fixture question, not a
walkback regression. `H22` is measured only for the same kind of reason - all three
of its cells carry run-time Skip guards that only the fixture rules out.

| Spec | Category | Tests | Why it is worth a boot |
|---|---|---|---|
| `H8-spawn-rotation` | SpawnRotation | 10 | The two-rotation-convention contract `.claude/CLAUDE.md` singles out as the easiest thing here to get silently wrong, resolved against live Kerbin AND Mun transforms |
| `H7-trajectory-math` | TrajectoryMath | 8 | Sampling predicate + quaternion helpers against live Unity arithmetic, and `ShouldRecordPoint` against the density preset the running game loaded |
| `H9-incomplete-ballistic` | IncompleteBallistic | 11 | Scene-exit tail extrapolation through atmosphere / terrain / SOI, patched-conic snapshot integration, extrapolated-segment map line, and the three live-vessel frame-calibration probes (two measurement cells that took their reading and now PASS as regression guards, plus the recorder attitude round trip added with the fifth-frame-mismatch fix, see (c) above) |
| `H10-finalize-backfill` | FinalizeBackfill | 7 | Terminal-orbit backfill, including the four stale-cached-tuple cases that otherwise park a ghost on last flight's orbit |
| `H11-pipeline-anchor` | Pipeline-Anchor | 7 | Anchor epsilon vs recorded geometric offset across all seven anchor situations, through live body transforms |
| `H12-switch-segment` | SwitchSegment | 6 | The Map Switch-To arming gate - a Harmony prefix, so only a running game exercises it |
| `H13-ksp-api-smoke` | KSP | 6 | The FIXTURE CANARY: `ActiveVesselExists` passing is the one positive proof that the save took the Focusable/FLIGHT route every other spec here infers |
| `H14-corpus-data-health` | DataHealth | 4 | Body names, OrbitSegment bodies, PartLoader resolution and time ranges across all 272 injected recordings |
| `H15-corpus-ghost-visuals` | GhostVisuals | 4 | A ghost mesh built from every recording in the corpus, every part name resolved through the live PartLoader |
| `H17-flight-integration` | FlightIntegration | 4 | Recorded lat/lon/alt vs `GetWorldSurfacePosition`, plus ParsekFlight and Harmony liveness |
| `H18-pipeline-smoothing` | Pipeline-Smoothing | 4 | The live GameEvents subscription contract - a dropped `GameEvents.X.Add(...)` compiles, unit-tests green, and silently stops recording docks |
| `H16-corpus-spawn-health` | SpawnHealth | 3 | Stuck `SpawnAbandoned` and out-of-bounds `SpawnDeathCount` across the corpus (one of its three cells is inert here, stated in the spec) |
| `H19-recording-finalization` | RecordingFinalization | 3 | BackgroundRecorder finalization-cache apply: destroyed-tail trim, stable-cache Orbiting, crash-tail append |
| `H20-eva-spawn-position` | EvaSpawnPosition | 2 | The category the 2026-07-25 EVA decision deferred to a dedicated batch-only spec; runs from the crewed landed pod host |
| `H22-ui-complexity-mode` | UiComplexityMode | 4 | The LIVE `InputLockManager`, which headless xUnit structurally cannot reach: entering Basic must force-close every gated window AND leave no Parsek control lock held, or the player's mouse soft-locks for the rest of the scene session |
| `H23-tracking-station` | TrackingStation | 10 | The TRACKSTATION scene itself, unreachable by any driven run until R12. The TS scene host, the span-clock TS seam, the synthetic-ghost ProtoVessel lifecycle and Fly-strip, and the map/TS render tracer's LIVE Vectrosity line truth. Note it breaks A1's shape: its `LoadGame` carries `scene = "trackstation"`, so it is the only spec here whose batch runs outside FLIGHT |

PHASE-4 WAVE 1 (2026-08-29) MOVED FOURTEEN CATEGORIES OUT OF B, and the split is
worth reading because only TEN of them landed in A. A category joins A when a lane
drives it WHOLE at that lane's boot - zero scene-skips - which is true of `ClawCouple`
(H42), `TerrainClearance` (H43), `StockUiOverlay` (H45), `MapView` (H47), `Ledger`
(H48), `TreeIntegrity` (H49), `GhostChains` (H50), `SaveLoad` (H51), `ReentryFx` (H52)
and `LogisticsGrapple` (H41, on the ISOLATED path). The other four are driven as
SLICES and STAY IN B on `Logistics`' precedent - "driven, partially, without meeting
bucket A1's whole-category admission shape": `GhostMap` (H44 takes the 9 TRACKSTATION-
eligible of 25), `Missions` (H54 takes the 7 FLIGHT-eligible of 13), `SceneAndPatch`
(H53 takes 4 of 7) and `Settings` (H46 takes 4 of 5). Each of those four leaves a
NAMED residue that needs a sibling lane at another scene, not a change to the lane
that exists.

ALL FOURTEEN FLEW ON 2026-08-28, every one PASS attempt 1 with zero failed cells, and
the MEASURED executed counts are now in the table's "Driven by" column. TWELVE LANES
EXECUTED EXACTLY WHAT THE DERIVATION SAID THEY WOULD. **TWO DID NOT, and the shortfalls
are the useful part of the wave:**

- **`SceneAndPatch` executed 2 of its derived 4** (H53). Both residual skips are
  DRIVER-STATE requirements rather than fixture ones, which is the distinction that
  makes them actionable: `GhostPidsNotLoadedAsPhysicsVessels` ("No ghost map PIDs -
  patch not exercised") needs playback armed so `ghostMapVesselPids` is non-empty, and
  `OutsiderActiveTreeParsesAndRoutesToLimboVesselSwitch_Bug266` ("No live active tree
  to use as a synth source") needs a `StartRecording` before the batch. NO HARVEST
  CLOSES EITHER - a save file cannot contain a LIVE tree. Both are left standing
  because closing them costs H53's `count = 274` corpus-integrity assertion, so they
  belong to a lane that trades that away deliberately.
- **`Missions` executed 3 of its derived 7** (H54), and its finding BOUNDS EVERY FUTURE
  `Missions` LANE. `RealSaveMissionInGameTests.cs` holds FOUR mission ARCHETYPES -
  re-aim, station rendezvous, joint landing+station arrival, off-Kerbin pad launch -
  and **any one real save is at most one or two of them.** `duna-one-recorded` is a
  single Kerbin->Duna re-aim mission, so it satisfied exactly the re-aim archetype and
  skipped the other three by construction. THE DERIVED "7 EXECUTABLE" IS THEREFORE AN
  ATTRIBUTE-LEVEL CEILING NO SINGLE-MISSION FIXTURE CAN REACH, and quoting it as a
  lane's target overstates what a `Missions` lane can do. The fourth skip is a separate
  and sharper lesson: **a fixture named by a skip string need not satisfy it.** The
  descent-handoff cell FOUND this save's 'Duna One' mission, walked it, rejected it for
  carrying no descent trigger (it is not a looped LANDING arrival), and named `s15` - an
  OPERATOR save that is not a committed harness fixture at all. Two saves shared the
  mission NAME the lane was chosen on while differing in the property the cell needed.
  Reading a guard string as a fixture spec is a hypothesis, not a derivation.

TWO SMALLER MEASURED CORRECTIONS, both against the slice framing above. `Settings`'
fifth cell is SCENE-FILTERED (`GhostMarkerProtoVesselsHaveHardenedPartTolerances` is
declared `Scene = TRACKSTATION`), not runtime-guarded, so its residue is scope rather
than debt; and `GhostMap`'s TS slice executed ALL NINE admitted cells with zero
run-time skips, so `skipped=16` is the pure attribute floor - see the audit correction
below, which the flight overturned.

THE WAVE ALSO CORRECTED ITS OWN COMMISSIONING AUDIT, which is the durable lesson.
That audit proposed nine of the thirteen ordinary lanes as "zero self-skips, pin
exactly". Re-deriving each against the source says FIVE can. The error was counting
guards rather than asking whether a guard can FIRE at the lane's own boot, and it went
BOTH ways: `ClawCouple` and `ReentryFx` have guarded bodies and still pin exactly (a
`PartLoader not ready` check cannot fire in a loaded FLIGHT scene; an atmosphere check
cannot fire on Kerbin), while `TerrainClearance`, `GhostMap` and `StockUiOverlay` were
proposed exact and are not. The `GhostMap` case is the sharpest: its
`MarkerDrawDecision_DispatchesOnLiveGate_NoGap` cell guards on an ACTIVE PAD VESSEL
that stages, which cannot exist at TRACKSTATION, so an exact pin at the attribute
floor would have red on a correct run. The rule the wave leaves behind is written into
`IngameBatchWiringGroupTests.INTERIM_PIN_IDS`: an exact pin is earned by an enumerated
reachability argument in the spec header, one line per guard, or it is not earned.

**AND THE FLIGHT OVERTURNED THE `GhostMap` HALF OF THAT CORRECTION** - the paragraph
above is left standing on purpose, because the way it was wrong is the lesson. H44
measured `skipped=16`, the attribute floor the audit proposed, and
`MarkerDrawDecision_DispatchesOnLiveGate_NoGap` PASSED at TRACKSTATION in 0.9 ms. The
source says why: that cell (`RuntimeTests.cs:10820`) carries NO GUARDS AT ALL - it
seeds three synthetic pids through testing hooks and asserts the pure
`ShouldDrawNonProtoMarkerForGhost` decision. The "active pad vessel" strings belong to
`FlightIntegrationTests` (`RuntimeTests.cs:3197` and `:10934`), a DIFFERENT class in
the same 10,000-line file, whose cells are FLIGHT-scoped and scene-skip here anyway.
So the re-derivation made the same class of error as the audit it was correcting, in
the opposite direction: **a guard read by grepping near a method NAME inherits its
neighbours' guards.** Attribute a guard to the method BODY that contains it - the
"source-derived guards use AST" house rule, arrived at from a third direction.
`TerrainClearance` and `StockUiOverlay` were correctly re-derived as inexact (and both
measured a split the attributes could not give); `GhostMap` was inexact for the wrong
reason, and only the flight could say so.

**A2 - the ISOLATED batch path (3 categories, 53 declarations, 5 specs).** `SceneExitMerge`,
wired as `H21-scene-exit-merge-isolated`, tier `nightly`, over `b2-lko-craft`. It
satisfies NONE of A1's three criteria as written, which is exactly why it needs
stating separately rather than being appended to the table above:

1. Its `Exec FLIGHT` is **0**, not `Decls`. Both declarations are
   `AllowBatchExecution = false`, so the ORDINARY filter skips both. The derivation
   its `skipped=0` floor rests on is `derive_batch_tally(..., isolated=True)`, which
   models `PrepareBatchExecutionIncludingFlightRestore` and yields 2.
2. Both members DO carry reachable `InGameAssert.Skip` guards - seven in-body
   (active vessel present, non-EVA, `situation == PRELAUNCH`, not already recording,
   no existing pending tree, `FlightInputHandler.state`, merge-dialog reflection
   handles) plus two more reachable through `WaitForRecordingToLeavePrelaunch` /
   `WaitForRecordingToClearPad`. So `skipped=0` is a FIXTURE claim here, not an
   attribute claim, and the first live run is what settles it.
3. The fixture is deliberately NOT `gloops-airshow`. That save's active vessel is a
   1-part `mk1-capsule` with zero `ModuleEngines`; both tests stage and then wait to
   leave PRELAUNCH and clear 80 m, so on it they would both self-skip and print
   `total=2 passed=0 failed=0 skipped=2` - numerically identical to the
   non-isolated failure the spec exists to rule out. `b2-lko-craft` carries a
   73-part stock launcher (8 engines, PRELAUNCH) that 11 committed flight specs
   already fly.

`Logistics` JOINED A2 ON 2026-08-28, wired as `H38-logistics-isolated`, tier
`operator`, over the purpose-built `logi-cargo-pad`. It is a DIFFERENT SHAPE from H21
on two of the three counts above, and both are worth stating because the next isolated
spec will look more like H38 than like H21:

1. Its `Exec FLIGHT` is **8**, not 0. `Logistics` is only PARTLY batch-disabled - 38 of
   its 47 declarations are `AllowBatchExecution = false`, one more is scene-ineligible
   at FLIGHT, and the remaining 8 are batch-eligible on the ORDINARY path (exactly
   H35's slice). So the isolated arg earns its place by admitting 46 where the ordinary
   filter admits 8: STRICTLY MORE, rather than being the only way to run anything, and
   the id is declared in `IsolatedBatchWiringGroupTests.PARTLY_BATCH_DISABLED_IDS`
   alongside `R7a`. TWO CONSEQUENCES FOLLOW, both easy to get wrong. The baseline-slot
   literal is **38** - neither the category total nor the admitted count - because the
   runner logs `ordered.Count(t => t.RestoreBatchFlightBaselineAfterExecution)` over the
   ADMITTED batch. And the tally's open interim form has to be a floor ABOVE the
   ordinary path's executable ceiling: the usual `passed=[1-9][0-9]*` would accept
   `passed=8`, the exact line a run that silently lost the isolated arg prints, so the
   spec pins `passed=(?:9|[1-9][0-9]+)` and a group cell sweeps the whole 0..8 range to
   prove the rejection.
2. It is a READING RUN and has not flown, so its split is deliberately unpinned and the
   id sits in `IsolatedBatchWiringGroupTests.INTERIM_PIN_IDS` - the same convention the
   ordinary H-series group uses for a pre-measurement member. The obligation that
   carries: the first flight measures the split, the pin is replaced whole, a
   `MEASURED_SKIPPED` entry is added if the run-time guards push `skipped` above the
   attribute floor of 1, and the id leaves the set, in the same commit.

The FIXTURE point is the same as H21's, and it is the whole reason this lane needed a
forge before it could need a spec. `Logistics` self-skips on five preconditions that no
stock craft and no prior committed fixture meets at once (listed under B4 below);
`FORGE-logi-pad` stamped `logi-cargo-pad` to meet all five in a six-part rig. Note that
the group's static fixture predicate - PRELAUNCH plus at least one `ModuleEngines` on
the ACTIVE vessel - is a FLOOR that catches the H21 failure mode only; it says nothing
about the other four, which is why the spec's own fixture block enumerates them.

`Logistics` THEN GAINED TWO MORE ISOLATED SPECS ON 2026-08-28, and the reason is the
one A2 exists to make legible: an isolated batch's split is a FIXTURE property, so the
same 46 admitted cells measure a different thing on a different host.
`H39-logistics-isolated-bdock` boots `bdock-recorded` and
`H40-logistics-isolated-depot-route` boots `depot-route-recorded` - both RECORDED hosts,
where H38's is a purpose-built pad rig with an EMPTY store. All 39 of H38's passes were
therefore claims about a store with zero trees; these two re-run the identical
population where committed trees, dock windows and (H40 only) a live Active route exist.
BOTH FLEW THREE TIMES THE SAME DAY, the third census of each PASS attempt 1, and both
are now pinned whole (`47/34/0/13` and `47/35/0/12`) with their recordings counts pinned
EXACTLY (21 / 22); `INTERIM_PIN_IDS` is back to empty. The pair's SECOND censuses are
what found the tree-deletion data loss - see the executed-count paragraph above and the
status doc's H39+H40 section.

THE PRE-FLIGHT DERIVATION IS THE INTERESTING PART, because it REFUTED half the reason
they were commissioned and did so before either flew. H38's skip roster named five of its
seven run-time skips as one debt - a missing RECORDED subject - and named
`bdock-recorded` as the answer. Reading the predicates against the committed bytes says
the corpus pays TWO and **structurally cannot pay three**: all three `RouteProof_*` cells
are pure walks over committed windows and **none reads `FlightGlobals.ActiveVessel`**, so
which vessel a host makes active is irrelevant to them; the entire committed corpus holds
exactly TWO `ROUTE_CONNECTION_WINDOWS` nodes; and BOTH sit on the INITIATOR branch with
the SAME pid 3620499050, because `persistentId` is craft-baked and two `Kerbal X`
descendants therefore dock into their own pid. The target cell's predicate is the strict
complement, and the cross-tree cell short-circuits on the initiator case before its
committed-partner walk. `RouteOriginProof_StartedDockedToNonKsc_ProducerLandsProof` is
the same story from the other side: `grep -c ROUTE_ORIGIN_PROOF` reads 0 on both
fixtures. SO THE RESIDUE IS A HARVEST REQUIREMENT, NOT A LANE REQUIREMENT - closing those
three needs a recorded dock between craft with DIFFERENT baked pids, and a mission that
starts docked to a non-PRELAUNCH partner. This is exactly the "wiring them now produces
green-looking specs that execute nothing" hazard bucket B4 warns about, caught one layer
earlier than usual: at authoring time, by reading the predicate, rather than on a flight.

THE FIXTURE PREDICATE WAS GENERALISED FOR THEM, and it is the third GENERALISED-BY move
in that class. It required a PRELAUNCH active vessel with at least one `ModuleEngines` -
which is the STAGING requirement, written when `SceneExitMerge` was the only member. Both
new hosts are ORBITING. `Logistics` never stages: a grep for `ActivateNextStage`,
`Situations.PRELAUNCH`, `WaitForRecordingToLeavePrelaunch` and
`ClassifyLaunchWaitTimeout` across all 18 Logistics test files plus their shared helper
returns ONE hit, and it is a comment about a RECORDED `startSituation`. The check is now
a per-category capability table (fail-closed, no default), with `Logistics` held to what
`UnloadedFuelVesselFixture`'s own failure path names - a snapshottable active vessel
carrying LiquidFuel with positive capacity, or it returns
`reason = no-liquidfuel-resource` and every unloaded-depot cell skips. That is the exact
Logistics analogue of H21's engineless host, and a new positive control runs `staging`
over a recorded host and requires REJECTION so the PRELAUNCH check cannot be deleted to
make these lanes pass.

| Spec | Category | Tests | Why it is worth a boot |
|---|---|---|---|
| `H21-scene-exit-merge-isolated` | SceneExitMerge | 2 | The R5 shakedown, and the D1 `commit-scene-exit` / `discard-rollback` cells no other mechanism produces: a real recording, a real launch, a real stock save-and-exit out of FLIGHT, and both branches of the pre-transition merge dialog |
| `H38-logistics-isolated` | Logistics | 47 declared, 46 admitted, 38 of them restore-flagged | The 38 restore-after-run `Logistics` declarations no unattended path could execute before it: unloaded-depot origin debit, pickup, loaded and multi-module cargo delivery, harvest capture, multi-stop and multi-origin escrow, round-trip pairing. FLOWN 2026-08-28: reading run 1 found 1 product + 2 test defects (the D4 harvest rails funnel, fixed at `f98d5477a`), run 2 PASS attempt 1 measured `total=47 passed=39 failed=0 skipped=8`, now pinned WHOLE with two targeted cell tokens; confirm runs pending |
| `H39-logistics-isolated-bdock` | Logistics | 47 declared, 46 admitted | The same 46 cells over `bdock-recorded` - the FIRST time the restore-flagged Logistics declarations run against a non-empty recording store (two committed trees, 19 recordings, one dock window). Pays two of H38's five missing-recorded-subject skips, and is the fixture-axis negative control on H38: the census delta says which of its 39 passes were RIG properties rather than universal ones - measured, 5 of them. FLOWN 3x 2026-08-28, pinned whole `47/34/0/13` with count 21; its census-2 recordings-floor red is half of how the tree-deletion data loss was found |
| `H40-logistics-isolated-depot-route` | Logistics | 47 declared, 46 admitted | The same 46 cells over `depot-route-recorded`, the suite's ONLY committed Active GhostDriving route (four `SOURCE_REF` rows carrying `routeProofHash`, a Dock and an Undock branch point, 22 recordings). The axis it adds is ROUTE-PRESENT vs ROUTE-ABSENT: every route-reading cell in the category has until now executed only against state a test forged in-body. Carries the `RevalidateSources ... routes=1 transitioned=0` anti-vacuity token, tightened to `reason=OnLoad` by the census (the authored wildcard also matched on the route-less host). FLOWN 3x 2026-08-28, pinned whole `47/35/0/12` with count 22. It adds ZERO distinct declarations over `H38 ∪ H39` - its value is the execution context plus the nine-cell destination-headroom test-defect family its census 1 exposed against a 720/720 tank |

### Bucket B - wireable, but needs something first (71 categories, 410 declarations)

Not one list but six reasons, and the reason is what decides whether it is worth
doing.

**B1 - needs a corpus or fixture extension.** The FUTURE recommendation in
`todo-and-known-bugs.md` was to wire `EvaSpawnPosition` AND `CrewReservationLive`
over the injected corpus. Half of it shipped; the other half cannot, and the reason
is structural rather than a matter of effort. Both `CrewReservationLive` cells
short-circuit on `spawnedCount == 0`, and the injected corpus contains no recording
with a non-zero `SpawnedVesselPersistentId`. Driving it would emit exactly
`total=2 passed=0 failed=0 skipped=2` - the vacuous line the anti-vacuity gate exists
to reject.

THE INVARIANT THAT MAKES THAT TRUE IS NOT THE ONE THIS DOC FIRST NAMED, and the
correction matters because the wrong one is an active trap. The first draft credited
`SyntheticRecordingTests.CleanSaveStart` -> `RemoveSpawnedPidLines` with stripping
every `spawnedPid = ` line "by construction". That helper runs BEFORE the corpus
writer injects, so it can only ever clean pre-existing save content - and it does not
run on the harness path at all, because `run.py` invokes `inject-recordings.ps1`
without `-CleanStart`, leaving `PARSEK_INJECT_CLEAN_START=0` and the whole block
skipped. The real invariants are (1) `RecordingBuilder.WithSpawnedPid` has ZERO
callers, so every synthetic recording serializes `spawnedPid == 0` and the key is
never emitted, and (2) `AddRealCareerRecordings` injects `RECORDING_TREE` nodes only,
so the single `spawnedPid` that does exist in the frozen career fixture
(`Source/Parsek.Tests/Fixtures/DefaultCareer/persistent.sfs`) sits in a standalone
`RECORDING` node that is never injected. Both are one edit away from ceasing to hold,
which is exactly why the mechanism has to be stated correctly.

DO NOT "make the stated mechanism real" by adding `-CleanStart` to the harness
inject. `CleanSaveStart` also runs `RemoveVesselBlocksFromFlightState` and
`ResetActiveVessel`, producing `activeVessel = -1` with zero `VESSEL` nodes;
`DecideLoadRoute` would then return `NoVesselSpaceCenter`, the batch would run at
SPACECENTER, and all four corpus-backed specs plus every FLIGHT-scoped member would
red on `scene=FLIGHT`. The doc's original wording pointed at the one switch that
breaks the group.

Unlocking `CrewReservationLive` means teaching the corpus writer to author
spawned-endpoint recordings, which also makes `SpawnHealth`'s third cell
(`SpawnedPidConsistency`, wired but inert for the same reason) meaningful. That is a
C# fixture change, not a spec change, and it is the highest-value item in this
bucket because it unblocks three cells across two categories at once.

**B2 - needs the modded-compat instance profile.** `ReStockCompat` (9) and
`WaterfallCompat` (8) are batch-eligible and substantial, but every cell gates on the
mod being installed, so on `stock-minimal` all 17 would skip. They belong on the
`modded-compat` profile, and `WaterfallCompat` in particular is where the pristine-FX
fallback lives.

**B3 - reachable only at SPACECENTER, and thin.** `Ledger` (4), `StockUiOverlay` (6),
`ResourceTopBar` (2), `Optimizer` (2), `Recording` (1),
`ResourceReconciliation` (1), `WarpToTime` (1). `StrategyLifecycle` LEFT this bucket
2026-08-18: the strategy-currency-conversion lane gave it a third declaration with a
real subject and a driver (`L3-strategy-currency-conversion`, on the same
`fresh-career` fixture this bucket names), which is what "revisiting as a group"
looks like for one category at a time. The vessel-less fixture the rest need
already exists (`fresh-career` / `fresh-sandbox`, as `B10` / `L1` / `M2` use), so the
fixture is not the blocker - the yield is. Every one of them is either a single test
or heavily self-skip-guarded (`StockUiOverlay` has a self-skip in all 6 members,
`Ledger` in all 4), so a boot each buys very little. Worth revisiting as a group if a
multi-category batch contract is ever designed; not worth eight boots now.

**B4 - self-skip guards whose preconditions the committed fixtures do not obviously
meet.** The large categories live here: `Logistics` (47 declarations, of which
38 are `AllowBatchExecution = false` and one more is scene-ineligible, leaving 8
executable at FLIGHT; nearly every member carries a self-skip),
`GhostLifecycle` (17, every member self-skip-guarded), `CrewReservation` (15, 12
guarded), `TerrainClearance` (6, all 6), `PartEventFX` (6, all 6). These are exactly
where the "specs that all Skip" warning bites: wiring them now produces green-looking
specs that execute nothing. Each needs its guard preconditions read and a fixture
chosen to satisfy them - real work, one category at a time.

`Logistics` IS NOW PARTLY WIRED, and the shape of that wiring is the worked example
this paragraph asks for. `H34-logistics-inter-body` (2026-08-11, flown under its
pre-rename id `H32-logistics-inter-body`) drives the category
at SPACECENTER rather than FLIGHT, which is where its two scene-eligible members
live: the `RouteInterBodyBuilderShapeInGameTest` inter-body builder-shape gate and
the AnyScene tooltip cell. Measured tally `total=47 passed=2 failed=0 skipped=45`
- so the 45 skips are entirely the ATTRIBUTE floor (FLIGHT-scoped or
batch-disabled declarations) and ZERO members self-skipped at run time, despite the
inter-body cell carrying three live guards. That is a narrow slice deliberately: the
FLIGHT bulk (38 batch-disabled, most of the self-skip-guarded remainder) is still
unwired and still needs the per-guard fixture read above. What the slice settles is
that the guards this paragraph warns about are readable and satisfiable one spec at
a time, and that a partial category can be wired honestly as long as the pin carries
the skip floor rather than hiding it.

`H35-logistics-route-proof` (2026-08-11, flown as `H33-logistics-route-proof`)
then wired the OTHER slice - the 8
FLIGHT-eligible declarations - and it is the sharper worked example, because it is
the case where the fixture, not the guard, was the whole problem. Its five
route-proof cells are pure READ-SIDE walkers: they inspect state a PRIOR recording
session wrote and Skip when the loaded save has nothing to walk, which is precisely
the "specs that all Skip" hazard in its purest form. The answer was not a guard read
but a RECORDED fixture - `bdock-recorded`, harvested `--keep-parsek` from a green
BDOCK-1 flight, carrying two committed trees and a real dock/undock
`ROUTE_CONNECTION_WINDOWS` node. Measured tally
`total=47 passed=5 failed=0 skipped=42`, i.e. the 39-declaration attribute floor
plus THREE run-time self-skips, and the three are the useful reading: two of the
five proof cells still cannot fire on this fixture (one because the single window's
target pid equals the recording's own, putting it on the initiator branch and out of
reach of both the target and the cross-tree predicates; one because no committed
mission profile STARTS docked to a non-PRELAUNCH partner, so the save carries zero
`ROUTE_ORIGIN_PROOF` nodes). Two lessons for the remaining B4 work. First, a
recorded fixture can un-skip read-side cells that no guard read would have fixed.
Second, "wired" is not "covered": going from a recording-free fixture to a recorded
one took this category's executed count from 2 to 6 of 47, and the residue is
FIXTURE SHAPE, which is a harvest problem rather than a spec problem.

AND THE 38 ARE NO LONGER UNWIRED - OR UNRUN. `H38-logistics-isolated` (authored and
FLOWN 2026-08-28; run 2 executed 39 of the 47) drives them through the R5 ISOLATED
entry point, which admits
`AllowBatchExecution || RestoreBatchFlightBaselineAfterExecution` - and all 38
batch-disabled Logistics declarations carry the restore flag. THE THIRD LESSON
FOR THE REMAINING B4 WORK, and the most transferable one: where H35 answered a
read-side skip with a harvested fixture, H38 answered a PRECONDITION skip with a
purpose-BUILT one. Reading the 38 guards produced five concrete craft
requirements - a PRELAUNCH pad rocket with a `ModuleEngines` (so
`UnloadedFuelVesselFixture` can snapshot the active vessel and re-spawn the copy
as an UNLOADED depot), at least one `LiquidFuel` RESOURCE node, a PARTIALLY
filled flow-enabled LF tank on the LIVE craft (the loaded-path delivery cell
pre-drains the first tank and asserts a +5.0 LF top-up, so it needs debitable
fuel AND headroom at once), a `BaseConverter`-derived module, and TWO
`ModuleInventoryPart` containers in probe order with the FIRST nearly full and a
LATER one free - none of which any stock craft or committed fixture met at once.
The answer was to AUTHOR a craft from those five requirements
(`harness/tools/build_logi_craft.py`, a derivation record with a byte-identity
drift gate) and forge a pad save from it (`FORGE-logi-pad` ->
`fixtures/saves/logi-cargo-pad`). For the remaining B4 categories that is the
cheaper road wherever the guards name capabilities rather than history: a guard
that wants a PART is a craft-building problem, and a guard that wants a PAST is a
harvest problem. THE FIRST FLIGHT SETTLED THAT SPLIT EMPIRICALLY: all five
craft-building requirements held on run 1, and every one of the SEVEN run-time skips
that remain after run 2 (the eighth is the SPACECENTER scene filter, H34's cell) is a
guard that wants a PAST - a docked-origin recording, a
PRELAUNCH-committed recording, three dock-window recordings, a drill rig landed on
ore, an injected synthetic tree. Not one is a part the rig could have carried.

An earlier revision of this paragraph opened the list with `Rewind` (37) and closed
"the payoff is high for `Rewind` and `GhostLifecycle` in particular". Half of that
resolved as predicted and half was measured WRONG, and the correction is the load-
bearing part:

- `Rewind` was wired 2026-08-04 (roadmap R7: `R7a` + `R7c`, 21 of 37 executing
  across the two, both live-proven) and left B4. The per-test read that sized it,
  the four flights of the abandoned session-live third spec, and the four test
  defects that surfaced are recorded in `todo-and-known-bugs.md`
  (R7-SESSION-BATCH-ISOLATION, R7-FIXTURE-GAPS).
- **`GhostLifecycle` is NOT the high-payoff target the old sentence claimed, and
  should not be wired next.** A full-body read of all 17 members (2026-08-04, the
  same wave) measured ~11 of 17 UNREACHABLE on ANY committed fixture, for reasons
  no fixture choice moves: (a) no committed asset can produce an OVERLAP
  recording - the injector's `.WithLoopPlayback()` default derives the written
  loop period as the recording's full point span, so `intervalSeconds >= duration`
  always and `GhostPlaybackLogic.IsOverlapLoop` is false BY CONSTRUCTION, which
  kills the five boundary-overlap cells; (b) no committed asset authors a MISSION
  node with `LoopPlayback = true`, so `MissionLoopUnitBuilder.Build` returns empty
  and the mission-loop cells are dead; (c) `loopPhaseOffsets` has exactly one
  writer, reached only on watch-mode entry, which no unattended batch performs;
  (d) the batch-start `PerformBetweenRunCleanup` destroys all ghosts in the same
  frame the first cell runs, so the first cell in discovery order self-skips on an
  empty ghost set. Realistic yield is ~4 of 17 on `gloops-airshow` +
  `all-synthetic`. Unblocking the rest is generator/product work (an overlap-
  capable `WithLoopPlayback` interval, a mission-node author), not spec authoring.

  NOTE ADDED 2026-08-29 (Phase-4 Wave 1), and it is a FLAG rather than a revision:
  clause (b) above says "no committed asset authors a MISSION node with
  `LoopPlayback = true`". That is no longer true of the committed corpus -
  `fixtures/saves/duna-one-recorded` carries a MISSION node with
  `loopPlayback = True`, which is precisely the condition (b) says nothing
  satisfies, and `H54-missions` in this wave boots that very save. So the
  2026-08-04 unreachability read NEEDS RE-DERIVATION: at minimum the mission-loop
  clause, and possibly the ~11-of-17 headline that rests on it.
  DELIBERATELY NOT RE-MEASURED HERE. Re-deriving 17 bodies against a fixture that
  did not exist when the read was taken is its own piece of work with its own
  evidence, and quietly editing the verdict without doing it would be worse than
  leaving it flagged. What this note buys is that the next person to read "should
  not be wired next" knows the premise moved. The row above is marked accordingly.

~~The honest next wave is therefore bucket B6 below (`LogContracts` 10,
`GhostAudio` 9, `Diagnostics` 6, `MapPresence` 5, `LocalizedName` 3 - all
read 2026-08-04 with high predicted execution on committed fixtures), plus
`CrewReservation` from this bucket, ahead of anything else in B4.~~
**DONE 2026-08-05 (`wire-wave-2`): that exact list shipped as H26-H31.**
`CrewReservation` left this bucket via the read it demanded: the blocker was
never the guards themselves but the FIXTURE - measured across all twelve
committed saves, only `b2-lko-craft` has both a crew part with free seats
(mk1-3pod, 2 free) AND spare Available Crew kerbals (3); on the family's
gloops-airshow host five of the six seat-matching cells skip on a full 1-seat
pod. H31 executes 14 of 15 (the one skip is the SPACECENTER-scoped
auto-assign-patch cell, which is currently unreachable in ANY scene - at
SPACECENTER it would self-skip on the empty CrewReplacements dict no committed
asset can populate; see W2-VACUOUS-CELLS in todo-and-known-bugs.md). Four of
the 14 are honest vacuous passes for the same empty-dict reason - converting
them to Skips is filed product work, and doing it will move H31's pin
deliberately.

**B5 - too small to justify a dedicated boot.** The long tail: `Bug289` (2),
`ContinuationIntegrity` (2), `MissionPhasing` (2), `PartEventTiming` (2),
`Pipeline-Frame` (1), `Pipeline-Outlier` (1), `Pipeline-Terrain` (1),
`Pipeline-AnchorPropagate` (1), `ForwardRender` (1), `ResourceManifest` (1),
`StockWarpLimits` (1), `RouteLiveAnchor` (1), `LedgerGroundTruth` (2) and similar.
Several are zero-self-skip and would pass immediately; they are simply not worth a
KSP boot each at one or two tests. They are the strongest argument for a future
multi-category batch contract, and the honest answer today is "not worth wiring",
not "cannot be wired".

**B6 - ~~THE NEXT WAVE~~ CLOSED 2026-08-05: all seven wired.** `KspApiSanity` /
`Serialization` shipped 2026-08-04 as H24 / H25; the remaining five shipped
2026-08-05 (`wire-wave-2`) as H26 (`LogContracts`), H27 (`Diagnostics`), H28
(`MapPresence`), H29 (`LocalizedName`), H30 (`GhostAudio`). Two of the five
needed more than the "share the family boot" default the table below predicted:
H26 moved to career-pad-craft (its resource cell is assertion-gated on CAREER
mode and passes VACUOUSLY on any sandbox host - the fourth-trap shape), and H30
was unflyable on any instance provisioned before 2026-08-05 (the profiles pinned
SHIP_VOLUME = "0", which zeroes ComputeGhostAudioVolume and makes the looped
audio START path unreachable - the W2-SHIP-VOLUME-ZERO finding; both profiles
now pin "1" with silence owned by MASTER_VOLUME alone). H28 injects the corpus
deliberately: two of its cells bail through a SILENT return on an empty
ghost-map pid set, so under "none" its green would mean less than the tally
suggests. The section is kept for the record of what B6 was; the table below is
the pre-wiring read.
B1-B5 were written as if they partitioned the bucket. They do not, and the omission
matters because these are the categories a reader would reach for first:

| Category | Decls | Exec FLIGHT | Members with self-skip | Note |
|---|---|---|---|---|
| `LogContracts` | 10 | 10 | 2 | Ties `SpawnRotation` as the largest wireable category left. The 2 guarded members are the FLIGHT-scoped store / career pair; the other 8 are unconditional |
| `GhostAudio` | 9 | 8 | 1 | One member is SPACECENTER-scoped, so it scene-skips at FLIGHT and the pin carries `skipped=1` |
| `Diagnostics` | 6 | 6 | 1 | |
| `MapPresence` | 5 | 5 | 2 | |
| `KspApiSanity` | 5 | 5 | 3 | |
| `Serialization` | 4 | 4 | 1 | |
| `LocalizedName` | 3 | 3 | 0 | **Meets bucket A's admission test verbatim** - identical on all three criteria to `RecordingFinalization` (3/3/0) and `SpawnHealth` (3/3/0), both of which WERE wired. It was omitted by oversight, not by judgment |

None of these is blocked by a fixture, a profile or a scene. They were left out
of the H7-H20 wave to keep it at 14 boots and because each carries at least one
self-skip whose fixture precondition wants reading first - except
`LocalizedName`, which carries none and should simply have been wired. (All
seven now are; the "one self-skip wants reading first" caution earned its keep
twice - see the header note's H26 and H30 corrections, both invisible to the
attribute columns above.)

**B6-ISO - needs an ISOLATED batch and a launchable-craft fixture (5 categories, 16
declarations).** `AutoRecord` (10), `Coalescer` (2), `MergeDialog` (2),
`PlaybackControl` (1), `RevertFlow` (1). These arrived in bucket B from the retired
C1 when R5 shipped: every one of their declarations is `AllowBatchExecution = false`
AND `RestoreBatchFlightBaselineAfterExecution = true`, so the ordinary filter
executes none of them and the isolated filter executes all of them. What each still
needs is ordinary spec-authoring work, not a capability:

1. A spec whose `RunTests` step carries `isolated = "true"`. Template:
   `harness/scenarios/H21-scene-exit-merge-isolated.toml`.
2. A fixture whose ACTIVE vessel can do what the tests do. This is the part that
   bites: `SceneExitMerge`'s cells stage the active vessel and wait for it to leave
   PRELAUNCH and clear 80 m, so the default `gloops-airshow` host (a 1-part
   `mk1-capsule` with zero `ModuleEngines`) would self-skip both and print the
   all-skipped tally the isolated arg exists to rule out. Read each category's
   `BatchSkipReason` and self-skip guards before choosing.
3. A budget sized for real quickloads. H21 MEASURED a two-test isolated batch at
   29.6 s of batch time inside 101 s wall, so the roadmap's fear that a 10-test
   `AutoRecord` batch is unaffordable in one boot looks overstated - but it is ten
   launch-and-restore cycles, so size it and expect the first run to find something.

`AutoRecord` (10) is the largest and closes D1 `auto-record-first-mod-switch`;
`Coalescer` closes D5 `crash-coalescing` / `controlled-decoupled-child`;
`RevertFlow` closes D1 `commit-revert-merge`. Tracked as R6 in
`docs/dev/autotest-roadmap.md`.

### Bucket C - not batch-runnable (1 category, 10 declarations)

One reason, and it is not fixable by writing a spec.

**C1 - RETIRED BY R5 (2026-07-27).** This sub-reason used to hold six categories -
`AutoRecord` (10), `Coalescer` (2), `MergeDialog` (2), `SceneExitMerge` (2),
`PlaybackControl` (1), `RevertFlow` (1), 18 declarations - on the grounds that every
declaration is `AllowBatchExecution = false`, so "a `RunTests` batch over any of
these categories runs zero tests, by design", and that changing it "means
re-examining each test's reason for being isolated-only, which is a C# question, not
a harness one".

The first half was an accurate description of the seam as it stood. The second half
was wrong, and the correction is worth writing down because the reasoning error is
reusable. The claim treated `AllowBatchExecution = false` as the whole admission
contract. It is not: the runner has always had a SECOND admission filter,
`PrepareBatchExecutionIncludingFlightRestore`, which reads
`AllowBatchExecution || RestoreBatchFlightBaselineAfterExecution` and restores a
quickloaded flight baseline after each test. Every one of those 18 declarations
already carried `RestoreBatchFlightBaselineAfterExecution = true` - their authors
had already done the "re-examining each test's reason" the paragraph called for, and
recorded the answer in the attribute. What was missing was not a C# redesign but an
unattended CALLER: `RunAllIncludingFlightRestore` and
`RunCategoryIncludingFlightRestore` were reachable only from the Ctrl+Shift+T window
and the Settings test-runner window. R5 added the seam's `isolated` argument and the
autorun `PARSEK_AUTORUN_ISOLATED` mirror, so all six are now drivable.

Where they went: `SceneExitMerge` to bucket A2 (wired as `H21`). `AutoRecord`,
`Coalescer`, `MergeDialog`, `PlaybackControl`, `RevertFlow` to bucket B, sub-reason
B6-ISO below - they need a spec and a fixture whose craft can actually fly, not a
capability. The one genuinely-manual population that survives the correction is
smaller and elsewhere: 3 `Periodicity` declarations carry
`AllowBatchExecution = false` with NO restore flag, and no batch path admits those.

The lesson generalizes past this table: a capability that is implemented, public and
called only from an interactive surface reads exactly like a capability that does
not exist. Before recording something as structurally impossible, check whether the
gap is the mechanism or only its callers.

**C2 - the scene is unreachable from the command seam**: ~~`TrackingStation` (10
declarations, 9 batch-eligible at TRACKSTATION, 0 anywhere else).~~
**CLOSED 2026-07-30 by roadmap R12.** The reasoning stood exactly as written:
`TestCommandLoadGame.DecideLoadRoute` had exactly two routes - `Focusable` (FLIGHT)
and `NoVesselSpaceCenter` (SPACECENTER) - and no implemented seam verb transitioned
scenes (`KscAction` applies stock actions in place), so no driven run could put the
game in the tracking station and 9 ready-to-run tests were stranded. It also proved
to be exactly the cheap unlock this entry predicted: the fix was the SECOND of the
two options named here, a third `LoadGame` route selected by an additive optional
`scene=` argument, and it bought all 9 on its first flight
(`H23-tracking-station`, `BATCH_COMPLETE v1 total=10 passed=9 failed=0 skipped=1
category=TrackingStation scene=TRACKSTATION`, 44 s).

Two things the closure is worth remembering for:

- **The `scene=TRACKSTATION` token inside the tally is not cosmetic.** Every member of
  this category is TRACKSTATION-scoped, which makes it the perfect B10 shape: a
  silently-wrong-scene boot scene-skips all ten, reports `passed=0 skipped=10`, and
  satisfies a bare `failed=0`. That is why the `scene=` parse is fail-closed and why
  the spec pins the landing scene on the tally line.
- **The 6 stranded MAINMENU / other-scene categories are NOT closed by this.** C2 was
  written about the tracking station specifically; a MAINMENU batch still has no
  route, and `scene=flight` is deliberately not an accepted value (see the M-A2
  R12 update block for why).

## The fourth trap: a vacuous PASS the tally cannot see

The 2026-07-25 EVA decision named three traps (`AllowBatchExecution = false`, tests
that Skip without a mid-flight crewed vessel, batch-isolation teardown coupling).
Enumerating the tree surfaced a fourth, and it is worse than the others because every
existing defence is blind to it.

`hlib.batch_contract_vacuity_gap` catches `passed == 0`. It cannot catch a test that
RUNS, PASSES, and asserts over nothing. The shape is a store walk that counts
violations:

```
foreach (var rec in RecordingStore.CommittedRecordings) { if (bad(rec)) violations++; }
InGameAssert.AreEqual(0, violations, ...);
```

Over an empty store the loop body never executes and the assertion passes on zero
items. The tally reads `total=N passed=N failed=0 skipped=0` - indistinguishable from
a real pass. Some tests make it worse by bailing early with a silent `return` /
`yield break` and a Verbose log INSTEAD of an `InGameAssert.Skip`, so they are
reported PASSED rather than Skipped:
`FlightIntegration.GhostPositionMatchesGeographic`,
`FlightIntegration.ActiveVesselBodySurfaceApi` (a bare `if (vessel == null) return;`),
`GhostVisuals.GhostHasRenderers` and
`GhostVisuals.IncrementalSnapshotBuild_YieldsThenCompletes` all do this - note three
of the four are in categories THIS WAVE WIRES - and a scan for the pattern across the
tree finds around a dozen more.

The only defence is the FIXTURE. That is why `H14` / `H15` / `H16` / `H17` inject the
272-recording corpus and pin `recordings.count = {min = 272, max = 272}`: the count
pin is what proves the store walks had something to walk, and it is asserted by
`IngameBatchWiringGroupTests.test_corpus_backed_members_inject_and_pin_the_corpus`.

Worth fixing at the source over time: a silent `yield break` in an in-game test
should be an `InGameAssert.Skip` naming the missing context, per the house rule
already stated in `.claude/CLAUDE.md`. Then the skip is visible in the tally and a
pinned `skipped=0` catches it.

## Measuring the pinned tallies - CLOSED 2026-07-27

ALL 14 FLOWN 2026-07-27 (`python run.py --tag ingame-batch`), against a DLL built and
provisioned from this branch. **All 14 PASS on attempt 1**, every verifier PASS or
SKIPPED, `batchComplete found=True failed=0 perCategory=1` on each.

WHAT THAT SETTLED, precisely. The `total=` values were already derivable statically
from the `[InGameTest]` attributes - that is what the source-sync gate does, and it
needs no flight. What the flights measured is the **passed / skipped SPLITS**, which
no static analysis predicts, because a run-time `InGameAssert.Skip` can move them at
any moment. Thirteen specs pin those splits as LITERALS, and `evaluate_expectations`
requires a required pattern to match, so a PASS means the runner printed the pinned
line token for token. Those thirteen derivations were all correct.

`H20` was the exception and is now CLOSED. Its pin was the loose interim form, so the
sweep's PASS proved only `total=2`, `failed=0` and `passed >= 1`, and its exact split
was in no artifact (collect-logs fires only on non-PASS, and the instance KSP.log was
overwritten by later scenarios in the same sweep). It was re-flown ALONE (59 s) so its
log survived to be read: `total=2 passed=2 failed=0 skipped=0`. The endpoint-overlap
probe fired, so the walkback path really executed. All 14 now pin whole off measured
lines. `H20`'s `skipped=0` is MEASURED rather than derivable - a fixture change to a
taller or elevated host can move the collider geometry the overlap probe depends on
and legitimately make it skip.

MEASURED COST, and it is far cheaper than this runbook estimated. **805 s (~13.4 min)
for all 14**, 49-71 s each. The per-scenario estimates below were 4-10 min, i.e. 5-8x
too high - they were extrapolated from the mission-flying scenarios rather than from
seam-only ones, and a batch-only scenario is dominated by KSP boot plus save load,
not by the batch. For scale: `B13` alone is a measured 2,825 s. Kept here rather than
corrected in place, as the record of how badly a boot-cost guess can miss.

The ORDER below is retained because it is still the right order to re-fly in after a
change, for the reasons given.

| # | Scenario | Why this position | Rough wall time |
|---|---|---|---|
| 1 | `H13-ksp-api-smoke` | FIRST, always. It is the fixture canary: if `scene=FLIGHT` is wrong for `gloops-airshow`, all 14 pins are wrong the same way and every later run is wasted. A red here reads `passed=4 skipped=2` and names the real scene | est ~4 min, MEASURED 50 s |
| 2 | `H7-trajectory-math` | Cheapest real category and fully scene-agnostic, so it isolates "the batch mechanism works" from "the scene is right" | ~4 min |
| 3 | `H14-corpus-data-health` | FIRST corpus-injecting run. Confirms the 272 count still holds on the current profile before three more specs depend on it | ~6 min |
| 4 | `H8-spawn-rotation` | Largest whole-tally pin (10) and a FLIGHT-scoped-only category, so it is the real test of the scene inference | ~5 min |
| 5 | `H9-incomplete-ballistic` | 8 tests, self-contained | ~5 min |
| 6 | `H10-finalize-backfill` | 7 tests, self-contained | ~5 min |
| 7 | `H11-pipeline-anchor` | 7 coroutine tests that yield across frames - the first one where the batch itself takes real time. Re-time the budget against this run | ~7 min |
| 8 | `H12-switch-segment` | 6 tests, Harmony prefix gate | ~5 min |
| 9 | `H18-pipeline-smoothing` | 4 tests plus the `asserted=5 of 5` GameEvents line, a second pinned contract to confirm | ~5 min |
| 10 | `H19-recording-finalization` | 3 tests, self-contained | ~5 min |
| 11 | `H17-flight-integration` | Corpus-backed; confirm from the log that `GhostPositionMatchesGeographic` actually found a surface recording rather than silently bailing | ~6 min |
| 12 | `H15-corpus-ghost-visuals` | Heaviest batch in the group (a mesh per recording across 272). Most likely of the 14 to need a budget bump; re-time it | ~10 min |
| 13 | `H16-corpus-spawn-health` | Corpus-backed, 3 tests | ~6 min |
| 14 | `H20-eva-spawn-position` | ~~LAST, and the only one whose pin was expected to change~~ DONE: re-flown alone 2026-07-27 and pinned whole at `total=2 passed=2 failed=0 skipped=0`. Nothing to hand-edit here any more. If a re-fly ever reds this as `passed=1 skipped=1`, do NOT re-pin to match - that is the endpoint-overlap probe not firing, i.e. the HOST's collider geometry changed; fix the fixture or explain the change, because absorbing it into the pin silently retires the walkback assertion | est ~8 min, MEASURED 59 s |

Estimated at roughly 80 minutes for all 14; the 2026-07-27 sweep did it in **805 s
(13.4 min)** in one pass via `python harness/run.py --tag ingame-batch`. At that cost
the ordering matters much less than it would have at 80 minutes, and flying the whole
tag in one go is now the sensible default. Individual runs
(`python harness/run.py --id H13-ksp-api-smoke`, ~50 s) remain the right move when
re-confirming a single pin after a source change.

What to do with each result:

- **PASS** - the derivation held. Change the spec's "EVIDENCE STANDING - DERIVED FROM
  SOURCE, NEVER MEASURED" paragraph to say MEASURED, cite the run id, and record the
  measurement in `docs/dev/autotest-status.md`.
- **RED on the tally with a different `skipped=`** - a run-time `InGameAssert.Skip`
  fired that the source scan said was unreachable, or a helper grew one. Read the log
  for the skip message, then either fix the fixture so the precondition is met or
  re-pin with the derivation updated to explain the skip. Do NOT widen the pattern.
- **RED on `scene=`** - the fixture's LoadGame route is not what every spec here
  assumes. Fix it once, in all 14, in one commit.
- **RED on `recordings.count`** - for a corpus spec, the corpus size moved (re-derive
  and update `H5` / `S1.4` too). For a non-corpus spec, a test LEAKED a recording into
  the save, which is a genuine campaign-isolation finding: file it in
  `todo-and-known-bugs.md` rather than widening the bound.
