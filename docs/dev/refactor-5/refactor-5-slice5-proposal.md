# Refactor-5 Slice 5 Proposal — Remaining Pure Same-File Phase Extractions

**Date:** 2026-06-14. **Status:** Proposal (not implemented).
**Roadmap:** `docs/dev/refactor-5/refactor-5-slices.md` (shared rules + validation gate).

Pure (headless) Pass-1 same-file phase extractions that didn't make Slice 1's top
three. Each is independent; do them in any order, one commit per method/file. Line
numbers are as-of-audit — confirm against code at implementation. Several are
**order-sensitive** (running locals mutated across phases): the review must enforce
checklist items 2 (extraction position) and 5 (grouped-block mutations).

## Targets

### 5.1 `MissionPeriodicity.cs` (order-sensitive)

- `ClassifyVesselOrbitalConstraint` (@1877, ~187) → extract the Rule-1 per-entry
  resolve+partition+merge loop body (@1909–2012) into `ResolveAnchorCandidate`
  (returns resolved pid/guid/recId + a reject reason). Self-contained per-entry
  classification.
- `TryBuildRelaunchSchedule` (@1227, ~167) → extract the periods/tolerance/shiftable
  partition loop (@1302–1328) and the knob-config assembly (@1346–1374).
- `TryFindNextScheduleK` (@1014, ~118) → extract the nested `|d|`-ascending
  shift-search (@1062–1107) as `TryResolveBestShift`. **Preserve the `!shiftWithin`
  loop guard and strict-`<` update exactly** (short-circuit + bounded-best tie
  rules).

Tuning literals are already named `const`s with rationale — no constants work.
**Validate:** `--filter "FullyQualifiedName~MissionPeriodicityTests"`.

### 5.2 `MissionRouteStructureList.Build` (@116, ~187)

Extract the four step-emitting phases → `AddLaunchSteps` / `AddBranchPointSteps`
(@151–192) / `AddStagingSteps` (@194–260, incl. the nested dedup) / `AddTerminalSteps`.
The local `Rec(id)` closure (@127) is shared — pass `tree`/`structure` + the closure
(or a `Func`) into each helper. **Note:** this file holds two builder types (a
second builder ~@439, both with a `Build`) — confirm owner boundaries before any
cross-method move. Cleanest phase boundaries in the Mission batch, strong tests.
**Validate:** `--filter "FullyQualifiedName~MissionStructureTests|FullyQualifiedName~MissionCompositionTests"`.

### 5.3 `RelativeAnchorResolver.TryResolveRelativeSectionPose` (@1555, ~151)

Extract the loop-anchor-mismatch / live-PID parent-pose block (@1575–1648) into
`TryResolveRelativeSectionAnchorPose` (returns `parentPose`). The relative-frame
compose math stays delegated to `TrajectoryMath` (untouched). **Verification-gated
extra:** `TryInterpolateRelativeFrame` (@1910) and `TryInterpolateRelativeFrameWithBracket`
(@1961) appear byte-identical except the bracket variant's extra `out`s — collapse
the plain one to forward to the bracket variant **only after** grepping that the
bracket variant has callers (it may be dead). Pure.
**Validate:** `--filter "FullyQualifiedName~RelativeAnchorResolverTests|FullyQualifiedName~LoopAnchorTests|FullyQualifiedName~RelativeFrameTests"`.

### 5.4 `Rendering/AnchorCandidateBuilder.BuildAndStorePerSection` (@84, ~106)

Extract `BuildBranchPointTypeLookup` (the `bpTypeByUT` build, @108–118) and
`LogAndCountCandidates` (the bounded per-candidate tally/Verbose block, @120–188).
Leave `Compute`'s per-source `Emit*` emitters untouched. Pure, lowest-risk in the
anchor batch.
**Validate:** `--filter "FullyQualifiedName~AnchorCandidateBuilderTests|FullyQualifiedName~AnchorPipelineTests"`.

### 5.5 `Logistics/RouteStore.RevalidateSources` (@266, ~211)

Four phases → `BuildErsIndex` (@273–301), `InspectRouteSources` (the per-route
source-inspection loop, @330–360), `DecideRevalidatedStatus` (status decision,
@362–431). Leave the transition + credit-flush tail (@433–468) inline. ERS-gated,
pure, has `ResetForTesting`.
**Validate:** `--filter "FullyQualifiedName~RouteStore|FullyQualifiedName~RevalidateSources"`.

### 5.6 `Logistics/RouteHarvestAnalysis.CheckTransportGains` (@149, ~124)

Phase-split into lineage-resolve / gain-compute / verdict. Low urgency; pure.
**Validate:** `--filter "FullyQualifiedName~RouteHarvest"`.

### 5.7 `TerminalOrbitSpawnSafety.Evaluate` (@49, ~107)

Guard-clause cascade (≥5 sequential altitude/periapsis checks) → one `CheckXxx`
helper per gate; turns the body into a readable checklist and isolates each gate for
testing. Reason codes + `DefaultSafetyMarginMeters` already named `const`s. Pure,
zero Unity coupling.
**Validate:** `--filter "FullyQualifiedName~TerminalOrbitSpawnSafety"`.

### 5.8 `PannotationsSidecarBinary.TryRead` (@260, ~175)

Four sequential block-parse phases (string-table / spline / outlier-flags /
anchor-candidate), each a self-contained loop with a `ValidateCount` guard →
`ReadSplineBlock` / `ReadOutlierFlagsBlock` / `ReadAnchorCandidateBlock`
(`private static`, taking `BinaryReader`+`Stream`). Each mutates a different `out`
list + shares `failureReason` — pass those explicitly. Pure binary codec.
**Validate:** `--filter "FullyQualifiedName~Pannotations|FullyQualifiedName~SidecarBinary"`.

### 5.9 `PristinePartFxResolver.TryExtract` (@122, ~58)

Extract `FindPartNodeInFile` (the PART-node search loop) and a MODULE-iteration
helper. `ParseLegacyFxKeys` stays whole. File I/O via the `*ForTesting` seam; pure.
**Validate:** `--filter "FullyQualifiedName~PristinePartFx"`.

### 5.10 `Reaim/DestinationConstraintExtractor.ExtractDestinationConstraints` (@86, ~152)

Optional phase split (moon-config collection / station de-dup / result assembly).
Pure; only worth doing if a focused test exists.
**Validate:** `--filter "FullyQualifiedName~DestinationConstraint"`.

### 5.11 `RouteOrchestrator` identical-head dedups (BEHAVIOR-CRITICAL FILE — careful)

`RouteOrchestrator`'s ledger row order + currency timing are behavior-critical, so
extract **only** the structurally-identical parts and **never reorder an emit**:

- Fold `IsDeliveryAlreadyInLedger` (@1883) + `IsRecoveryCreditAlreadyInLedger`
  (@1925) — structurally identical ELS scans — into
  `ElsContainsRouteCycleRow(GameActionType type, string routeId, string cycleId,
  string logCtx)`. Mostly pure (only `EffectiveState.ComputeELS`).
- Extract the **shared head** (guard + `CompletedCycles` bump + log) of the two
  replay branches (`ApplyDelivery` @1586, `EmitLoopCycle` @981) — keep the differing
  tail state-cleanup inline.
- Optionally phase-split `EmitDispatchDebit` (@1292, origin-debit branch) and
  `ProcessLoopRoute` (@569, blocked vs fired branches).

**Validate:** `--filter "FullyQualifiedName~RouteOrchestratorTests|FullyQualifiedName~RouteDeliveryTests|FullyQualifiedName~RouteRecoveryCreditTests"`,
and treat the clean-context review as load-bearing (item 5).

## Cheap Observational Follow-Ups (fold into an adjacent commit)

- Add `ResetForTesting()` to `Reaim/ReaimPlaybackResolver.Shared` (it has only
  `Clear()`) — latent cross-test-bleed trap.
- Guard or keep-the-warning on the process-wide mutable
  `Reaim/ReaimTransferSynthesizer.TransferSolver` static.

These are test-hygiene additions, not production-logic changes.
