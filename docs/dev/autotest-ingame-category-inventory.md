# In-game test category inventory (all 97 categories)

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

## What the numbers mean

- **Decls** - `[InGameTest(...)]` methods declaring this category anywhere in the mod
  assembly. This is what `BATCH_COMPLETE`'s `total=` counts for a single
  `RunCategory` batch: `allTests.Count(Status != NotRun)`, which INCLUDES the tests
  both filters skip.
- **Exec FLIGHT / SPACECENTER / TRACKSTATION** - how many survive BOTH runner filters
  at that scene, in the runner's order: `FilterSceneEligibleBatchCandidates` on scene
  first, then `PrepareBatchExecution` on `AllowBatchExecution = false` over what
  survived. A test failing both is counted once, in the scene bucket, exactly as the
  runner counts it.
- **Batch-disabled** - declarations carrying `AllowBatchExecution = false`. These are
  isolated-run-only; no `RunTests` batch ever executes one.
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
| `AutoRecord` | 10 | 0 | 0 | 0 | 10 | 10 | - | C |
| `BackgroundSeeder` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `Bug289` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `ClawCouple` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `Coalescer` | 2 | 0 | 0 | 0 | 2 | 2 | - | C |
| `ContinuationIntegrity` | 2 | 2 | 2 | 2 | 0 | 0 | - | B |
| `Contracts` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `CrewReservation` | 15 | 14 | 6 | 5 | 0 | 12 | - | B |
| `CrewReservationLive` | 2 | 2 | 2 | 2 | 0 | 2 | - | B |
| `DataHealth` | 4 | 4 | 4 | 4 | 0 | 0 | H14 | A |
| `Diagnostics` | 6 | 6 | 3 | 3 | 0 | 1 | - | B |
| `EvaSpawnPosition` | 2 | 2 | 0 | 0 | 0 | 2 | H20 | A |
| `FinalizeBackfill` | 7 | 7 | 0 | 0 | 0 | 0 | H10 | A |
| `FinalizeLimbo` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `Flight` | 2 | 2 | 0 | 0 | 0 | 1 | - | B |
| `FlightIntegration` | 4 | 4 | 0 | 0 | 0 | 0 | H17 | A |
| `ForwardRender` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `GameActionsHealth` | 4 | 4 | 4 | 4 | 0 | 3 | B10 / L1 | B |
| `GhostAudio` | 9 | 8 | 3 | 2 | 0 | 1 | - | B |
| `GhostChains` | 4 | 4 | 4 | 4 | 0 | 4 | - | B |
| `GhostLifecycle` | 17 | 15 | 0 | 2 | 0 | 17 | - | B |
| `GhostMap` | 25 | 16 | 0 | 9 | 0 | 11 | S1.6 | B |
| `GhostMapOrbits` | 2 | 2 | 1 | 1 | 0 | 1 | - | B |
| `GhostPlayback` | 42 | 41 | 1 | 1 | 1 | 12 | S1.4 | B |
| `GhostVisuals` | 4 | 4 | 3 | 3 | 0 | 0 | H15 | A |
| `IdentityLoss` | 3 | 3 | 0 | 0 | 0 | 3 | - | B |
| `IncompleteBallistic` | 8 | 8 | 0 | 0 | 0 | 0 | H9 | A |
| `KSP` | 6 | 6 | 4 | 4 | 0 | 0 | H13 | A |
| `KspApiSanity` | 5 | 5 | 3 | 3 | 0 | 3 | - | B |
| `Ledger` | 4 | 0 | 4 | 0 | 0 | 4 | - | B |
| `LedgerGroundTruth` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `LocalizedName` | 3 | 3 | 3 | 3 | 0 | 0 | - | B |
| `LogContracts` | 10 | 10 | 8 | 8 | 0 | 2 | - | B |
| `Logistics` | 47 | 8 | 2 | 1 | 38 | 46 | - | B |
| `LogisticsGrapple` | 4 | 3 | 0 | 0 | 1 | 2 | - | B |
| `MapPresence` | 5 | 5 | 3 | 3 | 0 | 2 | - | B |
| `MapRender` | 22 | 21 | 0 | 0 | 1 | 14 | S1.7 | B |
| `MapView` | 4 | 3 | 3 | 4 | 0 | 2 | - | B |
| `MergeDialog` | 2 | 0 | 0 | 0 | 2 | 2 | - | C |
| `MissionPhasing` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `Missions` | 12 | 7 | 5 | 0 | 0 | 9 | M1 | B |
| `Optimizer` | 2 | 0 | 2 | 0 | 0 | 2 | - | B |
| `PartEventFX` | 6 | 6 | 0 | 0 | 0 | 6 | - | B |
| `PartEventTiming` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `Periodicity` | 11 | 1 | 7 | 0 | 3 | 5 | M2 | B |
| `Pipeline-Anchor` | 7 | 7 | 0 | 0 | 0 | 0 | H11 | A |
| `Pipeline-Anchor-BubbleEntry` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-AnchorPropagate` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-Frame` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-Outlier` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `Pipeline-Smoothing` | 4 | 4 | 0 | 0 | 0 | 1 | H18 | A |
| `Pipeline-Terrain` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `PlaybackControl` | 1 | 0 | 0 | 0 | 1 | 1 | - | C |
| `QuickloadResume` | 3 | 1 | 0 | 0 | 2 | 1 | - | B |
| `ReStockCompat` | 9 | 9 | 0 | 0 | 0 | 9 | - | B |
| `Recording` | 1 | 0 | 1 | 0 | 0 | 0 | - | B |
| `RecordingFinalization` | 3 | 3 | 0 | 0 | 0 | 0 | H19 | A |
| `RecordingInvariants` | 2 | 2 | 0 | 0 | 0 | 0 | H5 | B |
| `RecordingStore` | 2 | 2 | 2 | 2 | 0 | 0 | - | B |
| `ReentryFx` | 3 | 3 | 0 | 0 | 0 | 1 | - | B |
| `ResourceManifest` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `ResourceReconciliation` | 1 | 0 | 1 | 0 | 0 | 0 | - | B |
| `ResourceTopBar` | 2 | 0 | 2 | 0 | 0 | 2 | - | B |
| `RevertFlow` | 1 | 0 | 0 | 0 | 1 | 1 | - | C |
| `RevertVesselStrip` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `Rewind` | 37 | 26 | 5 | 0 | 6 | 24 | - | B |
| `RewindSaves` | 1 | 1 | 1 | 1 | 0 | 1 | - | B |
| `RouteLiveAnchor` | 1 | 1 | 0 | 0 | 0 | 1 | - | B |
| `RouteRewindTimeline` | 7 | 7 | 7 | 7 | 0 | 1 | H6 | B |
| `SaveLoad` | 4 | 4 | 4 | 4 | 0 | 2 | - | B |
| `SceneAndPatch` | 7 | 4 | 3 | 2 | 0 | 4 | - | B |
| `SceneExitMerge` | 2 | 0 | 0 | 0 | 2 | 2 | - | C |
| `Serialization` | 4 | 4 | 4 | 4 | 0 | 1 | - | B |
| `Settings` | 3 | 2 | 2 | 3 | 0 | 0 | - | B |
| `SpawnCollision` | 2 | 2 | 0 | 0 | 0 | 2 | - | B |
| `SpawnHealth` | 3 | 3 | 3 | 3 | 0 | 0 | H16 | A |
| `SpawnRotation` | 10 | 10 | 0 | 0 | 0 | 0 | H8 | A |
| `SpawnTerminalOrbit` | 3 | 3 | 0 | 0 | 0 | 3 | - | B |
| `Spawner` | 2 | 2 | 0 | 0 | 0 | 1 | - | B |
| `StockUiOverlay` | 6 | 0 | 6 | 0 | 0 | 6 | - | B |
| `StockWarpLimits` | 1 | 1 | 0 | 0 | 0 | 0 | - | B |
| `StrategyLifecycle` | 2 | 0 | 2 | 0 | 0 | 2 | - | B |
| `Structure` | 2 | 2 | 2 | 2 | 0 | 2 | - | B |
| `SwitchIntentPatch` | 3 | 1 | 1 | 1 | 0 | 0 | - | B |
| `SwitchSegment` | 6 | 6 | 0 | 0 | 0 | 0 | H12 | A |
| `TerminalOrbit` | 2 | 2 | 2 | 2 | 0 | 2 | - | B |
| `TerrainClearance` | 6 | 6 | 0 | 0 | 0 | 6 | - | B |
| `TestCommands` | 4 | 3 | 1 | 1 | 0 | 3 | - | B |
| `TestRunner` | 2 | 2 | 2 | 2 | 0 | 0 | - | B |
| `TestRunnerIsolation` | 2 | 1 | 2 | 1 | 0 | 1 | - | B |
| `TrackingStation` | 10 | 0 | 0 | 9 | 1 | 3 | - | C |
| `TrajectoryMath` | 8 | 8 | 8 | 8 | 0 | 0 | H7 | A |
| `TreeIntegrity` | 4 | 4 | 4 | 4 | 0 | 3 | - | B |
| `Unity` | 4 | 4 | 4 | 4 | 0 | 1 | - | B |
| `WarpToTime` | 1 | 0 | 1 | 0 | 0 | 1 | - | B |
| `Watch` | 2 | 2 | 0 | 0 | 0 | 0 | - | B |
| `WaterfallCompat` | 8 | 8 | 0 | 0 | 0 | 7 | - | B |

## Triage

Totals, re-derived: **97 categories / 539 declarations**. Buckets **A 14 categories
(76 declarations)**, **B 76 categories (435 declarations)**, **C 7 categories (28
declarations)**. Driven by a committed spec after this change: **22 of 97
categories**, up from 8. Measured against declarations rather than categories, that
is 201 of 539 inside a driven category (was 125) of which 179 actually execute (was
103).

The 22-declaration gap between 201 and 179 is entirely in the eight PRE-EXISTING
driven categories - all 76 declarations this group adds execute. Decomposed, because
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
one wired category costs one KSP boot per cadence. Wiring all 89 undriven categories
would mean 89 boots. The question is never "can this category run in a batch" but
"is what it executes worth a boot".

### Bucket A - wired now (14 categories, 76 declarations)

All 14 ship as `H7`-`H20`, tier `nightly`, over the committed `gloops-airshow`
fixture. The admission test each had to pass:

1. Every declaration survives both runner filters at FLIGHT (Exec FLIGHT == Decls),
   so the attribute-derived `skipped` floor is 0.
2. No member, and no helper any member calls, contains a reachable
   `InGameAssert.Skip` - checked directly and transitively, not by a per-file grep
   (`IncompleteBallisticRuntimeTests.cs` does contain `InGameAssert.Skip` calls - 4
   in `Ledger` members, 1 in `RouteLiveAnchor`, 1 in `TestRunnerIsolation` - so a
   per-file answer would have been wrong for both `IncompleteBallistic` and
   `SwitchSegment`, neither of which has any).
   ONE MEMBER OF BUCKET A DOES NOT SATISFY THIS CRITERION AS WRITTEN, and saying so
   is cheaper than a footnote nobody reads: `Pipeline-Smoothing`'s
   `Pipeline_Smoothing_StructuralEvent_HandlersRegistered` reaches an
   `InGameAssert.Skip` through its private `AssertHandlerRegistered` helper. That
   branch fires only if reflection cannot find `EventData<T>`'s internal `events`
   field, i.e. if a KSP version renamed it, which is unreachable on the pinned
   1.12.5. It is admitted on the narrower ground that the skip is a KSP-VERSION
   guard rather than a fixture-context guard, and `H18` documents it at length.
3. The fixture already exists and its route is known.

That is what lets 13 of the 14 pin their tally WHOLE (`total=N passed=N failed=0
skipped=0`) from a source derivation rather than a guess. `H20-eva-spawn-position` is
the exception and carries the honest interim form; see below.

| Spec | Category | Tests | Why it is worth a boot |
|---|---|---|---|
| `H8-spawn-rotation` | SpawnRotation | 10 | The two-rotation-convention contract `.claude/CLAUDE.md` singles out as the easiest thing here to get silently wrong, resolved against live Kerbin AND Mun transforms |
| `H7-trajectory-math` | TrajectoryMath | 8 | Sampling predicate + quaternion helpers against live Unity arithmetic, and `ShouldRecordPoint` against the density preset the running game loaded |
| `H9-incomplete-ballistic` | IncompleteBallistic | 8 | Scene-exit tail extrapolation through atmosphere / terrain / SOI, patched-conic snapshot integration, extrapolated-segment map line |
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

### Bucket B - wireable, but needs something first (76 categories, 435 declarations)

Not one list but five reasons, and the reason is what decides whether it is worth
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
`ResourceTopBar` (2), `StrategyLifecycle` (2), `Optimizer` (2), `Recording` (1),
`ResourceReconciliation` (1), `WarpToTime` (1). The vessel-less fixture these need
already exists (`fresh-career` / `fresh-sandbox`, as `B10` / `L1` / `M2` use), so the
fixture is not the blocker - the yield is. Every one of them is either a single test
or heavily self-skip-guarded (`StockUiOverlay` has a self-skip in all 6 members,
`Ledger` in all 4), so a boot each buys very little. Worth revisiting as a group if a
multi-category batch contract is ever designed; not worth eight boots now.

**B4 - self-skip guards whose preconditions the committed fixtures do not obviously
meet.** The large categories live here: `Rewind` (37 declarations, 26 batch-eligible
at FLIGHT, most members carrying self-skips), `Logistics` (47 declarations, of which
38 are `AllowBatchExecution = false` and one more is scene-ineligible, leaving 8
executable at FLIGHT; nearly every member carries a self-skip),
`GhostLifecycle` (17, every member self-skip-guarded), `CrewReservation` (15, 12
guarded), `TerrainClearance` (6, all 6), `PartEventFX` (6, all 6). These are exactly
where the "specs that all Skip" warning bites: wiring them now produces green-looking
specs that execute nothing. Each needs its guard preconditions read and a fixture
chosen to satisfy them - real work, one category at a time - and the payoff is high
for `Rewind` and `GhostLifecycle` in particular.

**B5 - too small to justify a dedicated boot.** The long tail: `Bug289` (2),
`ContinuationIntegrity` (2), `MissionPhasing` (2), `PartEventTiming` (2),
`Pipeline-Frame` (1), `Pipeline-Outlier` (1), `Pipeline-Terrain` (1),
`Pipeline-AnchorPropagate` (1), `ForwardRender` (1), `ResourceManifest` (1),
`StockWarpLimits` (1), `RouteLiveAnchor` (1), `LedgerGroundTruth` (1) and similar.
Several are zero-self-skip and would pass immediately; they are simply not worth a
KSP boot each at one or two tests. They are the strongest argument for a future
multi-category batch contract, and the honest answer today is "not worth wiring",
not "cannot be wired".

**B6 - THE NEXT WAVE: qualified on every criterion, deferred only on batch size.**
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

None of these is blocked by a fixture, a profile or a scene. They were left out of
this wave to keep it at 14 boots and because each carries at least one self-skip
whose fixture precondition wants reading first - except `LocalizedName`, which
carries none and should simply have been wired. This is the highest-confidence
starting point for the next wave, ahead of B4's large guarded categories.

### Bucket C - not batch-runnable (7 categories, 28 declarations)

Two distinct reasons, and neither is fixable by writing a spec.

**C1 - every declaration is `AllowBatchExecution = false`** (isolated-run-only, by
the test author's deliberate choice, usually because the test destroys live state
mid-batch): `AutoRecord` (10), `Coalescer` (2), `MergeDialog` (2), `SceneExitMerge`
(2), `PlaybackControl` (1), `RevertFlow` (1). 18 declarations. `PrepareBatchExecution`
skips all of them, so a `RunTests` batch over any of these categories runs zero
tests, by design. The 2026-07-25 EVA decision already recorded this for the
`AutoRecord` EVA cells specifically; the table above shows it is the whole category
and five more besides. Changing it means re-examining each test's reason for being
isolated-only, which is a C# question, not a harness one.

**C2 - the scene is unreachable from the command seam**: `TrackingStation` (10
declarations, 9 batch-eligible at TRACKSTATION, 0 anywhere else).
`TestCommandLoadGame.DecideLoadRoute` has exactly two routes - `Focusable` (FLIGHT)
and `NoVesselSpaceCenter` (SPACECENTER) - and no implemented seam verb transitions
scenes (`KscAction` applies stock actions in place). So no driven run can put the
game in the tracking station, and 9 ready-to-run tests are stranded. This is the
cheapest large unlock in the whole inventory: one seam verb, or a third LoadGame
route, buys 9 tests.

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

## PENDING-OPERATOR: measuring the pinned tallies

None of `H7`-`H20` has ever been flown. Thirteen pin a tally DERIVED from the
`[InGameTest]` attributes plus a source scan; one (`H20`) carries the interim form.
Every derivation's failure mode is a loud RED naming the real numbers, never a false
green, so these are safe to leave on the nightly - but each pin should be confirmed
against a measured line, and `H20`'s must be replaced by one.

Fly them in this order. The order is not arbitrary: it front-loads the runs that
invalidate the most other work if they fail.

| # | Scenario | Why this position | Rough wall time |
|---|---|---|---|
| 1 | `H13-ksp-api-smoke` | FIRST, always. It is the fixture canary: if `scene=FLIGHT` is wrong for `gloops-airshow`, all 14 pins are wrong the same way and every later run is wasted. A red here reads `passed=4 skipped=2` and names the real scene | ~4 min |
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
| 14 | `H20-eva-spawn-position` | LAST. The only one whose pin is expected to CHANGE: read the `BATCH_COMPLETE` line and replace the interim pattern with the whole tally (`passed=2 skipped=0` or `passed=1 skipped=1`), then delete the interim paragraph from the spec | ~8 min |

Roughly **80 minutes** of wall time for all 14 on a warm instance, plus operator time
to read each line. They can be flown in one sitting with
`python harness/run.py --tier nightly`, but the value of the ordering is lost that
way: fly at least items 1-3 individually first
(`python harness/run.py --id H13-ksp-api-smoke`) and confirm the scene token before
committing to the rest.

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
