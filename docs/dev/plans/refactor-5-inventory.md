# Refactor-5 Inventory — Files Created Since Refactor-4

**Date:** 2026-06-14.
**Status:** Draft audit report. This is a read-only opportunity map, NOT approval
to move production code. Every candidate still needs a focused proposal, explicit
scope, and a clean-context review before implementation (see
`docs/dev/refactor-guidelines.md`).
**Baseline of record:** Refactor-4 (`docs/dev/done/refactor/refactor-4-inventory.md`,
base `3c863ff0`, 2026-04-25) and its living tracker
`docs/dev/plans/refactor-remaining-opportunities.md` (2026-04-30).

## How This Audit Was Scoped

This clone's git history was reset/squashed at 2026-06-07, so per-file git
creation dates are unusable for "created since refactor-4" (everything reads as
June). The audit therefore uses the **document baseline**: a file is treated as
"new since the last refactor pass" when its type name is never mentioned in any
prior refactor inventory/plan/remaining-opportunities doc. That set
(`UNSEEN`) is the audit surface below. Files whose owners were already extracted
or named by Refactor-3/4 (`ADDRESSED`) are excluded from new-file candidates but
noted where they grew materially.

Workflow used to produce this report:

1. Built the current production inventory (380 files / 307,741 lines, excluding
   `InGameTests`, `bin`, `obj`).
2. Cross-referenced every file's type name against the Refactor-3/4 docs to mark
   `UNSEEN` vs `ADDRESSED` (240 `UNSEEN` files / ~84k lines).
3. Ran a mechanical long-method scan (methods ≥90 lines) over the `UNSEEN`
   ≥400-line files for an objective backbone.
4. Fanned out 7 parallel read-only audit agents over disjoint subsystem batches,
   each applying the Refactor-4 Pass-0/Pass-1/Pass-2 conventions, and synthesized
   their findings below.

## Baseline Growth Since Refactor-4

| Item | Refactor-4 base | Now (2026-06-14) | Delta |
|------|-----------------|------------------|-------|
| Production C# files | 176 | 380 | +204 |
| Production C# lines | 145,229 | 307,741 | +162,512 (≈ 2.1×) |
| Files never named in a prior refactor doc (`UNSEEN`) | — | 240 (~84,038 lines) | — |
| `UNSEEN` files ≥ 400 lines | — | 62 | — |
| Methods ≥ 90 lines in `UNSEEN` ≥400-line files | — | 23 | — |

The production code base **more than doubled** since the last structural pass.
Most growth is split between (a) brand-new subsystems in new directories and
(b) continued accretion in the legacy giants (e.g. `ParsekFlight.cs` 14,503 →
28,670; `GhostMapPresence.cs` 3,408 → 12,967; `FlightRecorder.cs` 6,689 →
11,172). The legacy giants are out of scope for *this* report (they are tracked
by `refactor-remaining-opportunities.md`); this report covers the **new files**.

## New Directories (did not exist at Refactor-4)

Refactor-4's directory summary was `<root>`, `GameActions`, `UI`, `Patches`,
`Diagnostics`, `Timeline`, `Properties`. Five directories are entirely new:

| New directory | Files | Lines | Subsystem |
|---------------|-------|-------|-----------|
| `Logistics/` | 36 | 12,919 | Phase 13 route dispatch / delivery |
| `Rendering/` | 14 | 6,242 | Ghost trajectory render pipeline (anchor/smoothing/outlier) |
| `Display/` | 1 | 4,654 | Map-view non-orbital polyline renderer |
| `Reaim/` | 16 | 2,911 | Re-aim transfer synthesis (Lambert/window planning) |
| `MapRender/` | 16 | 1,925 | Map/TS shadow render driver + reconciler |

## `UNSEEN` Lines By Subsystem Bucket

| Bucket | Files | Lines |
|--------|-------|-------|
| Logistics / Route | 44 | 16,871 |
| Rendering / Map / Display | 31 | 12,319 |
| Re-Fly / Switch / Lifecycle | 29 | 10,137 |
| Missions | 11 | 8,222 |
| Anchor / Reaim | 21 | 5,855 |
| Orbit / Storage | 12 | 5,308 |
| Tracers (render/ledger/playback) | 7 | 4,278 |
| Other (career, FX, settings, debris policy, misc UI) | 85 | 21,048 |

## Largest `UNSEEN` Files (≥ 400 lines)

| File | Lines |
|------|-------|
| `Display/GhostTrajectoryPolylineRenderer.cs` | 4,654 |
| `UI/LogisticsWindowUI.cs` | 3,042 |
| `MissionPeriodicity.cs` | 2,783 |
| `Logistics/RouteOrchestrator.cs` | 2,414 |
| `RelativeAnchorResolver.cs` | 2,291 |
| `UI/MissionsWindowUI.cs` | 2,270 |
| `Rendering/RenderSessionState.cs` | 1,459 |
| `RecordingTreeSplitter.cs` | 1,391 |
| `MissionLoopUnitBuilder.cs` | 1,293 |
| `Patches/GhostOrbitLinePatch.cs` | 1,225 |
| `Patches/MapFocusObjectOnSelectPatch.cs` | 1,200 |
| `GhostRenderTrace.cs` | 1,113 |
| `MapRenderTrace.cs` | 1,107 |
| `StockUiOverlayController.cs` | 1,063 |
| `RouteProofCapture.cs` | 1,028 |
| `UnfinishedFlightClassifier.cs` | 950 |
| `Logistics/RouteCodec.cs` | 846 |
| `SceneExitInterceptor.cs` | 844 |
| `Logistics/RouteAnalysisEngine.cs` | 842 |
| `Logistics/RouteBackingMission.cs` | 770 |
| `Rendering/SmoothingPipeline.cs` | 760 |
| `MapRenderProbe.cs` | 757 |
| `Logistics/RouteHarvestAnalysis.cs` | 755 |
| `RouteProofCodec.cs` | 742 |

(62 files total ≥400 lines; full machine list lives in the audit's
`marked.txt` scratch artifact.)

## Mechanical Long-Method Scan (methods ≥ 90 lines, `UNSEEN` ≥400-line files)

| Lines | File | Method | Start |
|-------|------|--------|-------|
| 396 | `Patches/GhostOrbitLinePatch.cs` | `Postfix` | 640 |
| 362 | `Patches/MapFocusObjectOnSelectPatch.cs` | `Prefix` | 119 |
| 252 | `Patches/GhostOrbitLinePatch.cs` | `Prefix` | 125 |
| 211 | `Logistics/RouteStore.cs` | `RevalidateSources` | 266 |
| 206 | `SceneExitInterceptor.cs` | `Prefix` | 637 |
| 187 | `MissionRouteStructureList.cs` | `Build` | 116 |
| 184 | `Logistics/RouteCodec.cs` | `DeserializeFrom` | 238 |
| 174 | `Logistics/RouteCodec.cs` | `SerializeInto` | 59 |
| 167 | `UI/LogisticsWindowUI.cs` | `DrawRouteRow` | 679 |
| 151 | `Logistics/RouteOrchestrator.cs` | `ApplyDelivery` | 1567 |
| 146 | `Logistics/RouteOrchestrator.cs` | `ApplyDeliveryFromPlan` | 1728 |
| 143 | `RecordingTreeSplitter.cs` | `RollBackInMemory` | 1092 |
| 122 | `UI/GroupPickerUI.cs` | `ApplyGroupPopupChanges` | 356 |
| 117 | `ParsekSettingsPersistence.cs` | `LoadIfNeeded` | 87 |
| 111 | `UI/TestRunnerUI.cs` | `DrawTestRunnerWindow` | 285 |
| 106 | `Rendering/AnchorCandidateBuilder.cs` | `BuildAndStorePerSection` | 84 |
| 104 | `Patches/GhostOrbitLinePatch.cs` | `Prefix` | 1120 |
| 103 | `UI/TestRunnerUI.cs` | `DrawTestCategoryList` | 148 |
| 97 | `UI/MissionsWindowUI.cs` | `DrawMissionsTabContent` | 312 |
| 96 | `MapRender/ShadowRenderDriver.cs` | `RunFrame` | 247 |
| 95 | `UI/RouteCreationDialog.cs` | `Spawn` | 233 |
| 93 | `UI/LogisticsWindowUI.cs` | `DrawIntervalCell` | 861 |
| 91 | `UI/LogisticsWindowUI.cs` | `ComputeRouteLegibility` | 2282 |

Observation: the new files are, on the whole, **better factored than the legacy
giants** — the worst offenders are concentrated in two Harmony patch files
(`GhostOrbitLinePatch`, `MapFocusObjectOnSelectPatch`) and the IMGUI/codec
surfaces, not in the pure-logic engines. That shifts the highest-value targets
toward (a) the Harmony patch bodies and (b) IMGUI-embedded pure decisions.

<!-- PER-SUBSYSTEM FINDINGS: filled from the 7 audit agents below -->

## Per-Subsystem Findings

Each entry classifies candidates as **Pass1** (behavior-neutral same-file private
helper extraction), **Pass2** (cross-file owner proposal — discuss before
landing), or **Skip** (already well-factored / too order-sensitive). Risk is
**pure** (headless xUnit-testable) or **runtime** (Unity/IMGUI/Harmony/save-load
coupled — needs in-game validation).

> Audit status: complete. All 7 subsystem batches (Missions, Logistics codec/UI,
> Logistics backend, Rendering/Display, Anchor/Reaim, Re-Fly/Switch/Patches,
> Tracers/Orbit/Storage) reported and are folded in below.

### Missions (`Mission*`, `UI/MissionsWindowUI`, `UI/StructureListWindowUI`)

Overwhelmingly **pure** (constraint math + structure builders); only the two UI
files and one `FlightGlobals` seam are runtime-coupled. Tuning literals in
`MissionPeriodicity` are already named `const`s with rationale comments, and
static logging state already has `Reset*ForTesting` hooks — no constants-host or
test-reset gaps.

- **`MissionLoopUnitBuilder.TryBuildMissionUnit` (@111, ~537 lines)** — the
  largest single method in the audited surface. Numbered phases 1–8. Pass1:
  extract the 7e re-aim block (`@327-572`, ~245 lines, the whole `if
  (!phaseLocked)` body), the two `ReaimDiag` dump blocks, and the final
  PhaseLock summary-log block (`@592-645`). ~537 → ~250 lines. **Order-sensitive**
  (each phase mutates `phaseAnchorUT`/`effectiveCadence`); review must enforce
  checklist items 2 (extraction position) and 5 (grouped-block mutations).
  `pure`.
- **`MissionPeriodicity.ClassifyVesselOrbitalConstraint` (@1877, ~187)** — Pass1:
  extract the Rule-1 per-entry resolve+partition+merge loop body (`@1909-2012`)
  into `ResolveAnchorCandidate`. Isolates the densest identity logic for direct
  testing. `pure`.
- **`MissionPeriodicity.TryBuildRelaunchSchedule` (@1227, ~167)** /
  **`TryFindNextScheduleK` (@1014, ~118)** — Pass1: extract the periods/tolerance
  partition loop, the knob-config assembly block, and the nested `|d|`-ascending
  shift-search (`@1062-1107`). Order-sensitive short-circuit/tie rules; flag in
  the review prompt. `pure`.
- **`MissionRouteStructureList.Build` (@116, ~187)** — Pass1: extract the four
  step-emitting phases (`AddLaunchSteps`/`AddBranchPointSteps`/`AddStagingSteps`/
  `AddTerminalSteps`). Cleanest phase boundaries in the batch, strong existing
  tests, lowest risk. Note this file holds two builder types — confirm owner
  boundaries before any cross-method move. `pure`.
- **Pass2 (propose only):** a `MissionLogSuppressionScope` IDisposable to dedup
  the three `SuppressLogging` save/restore `try/finally` blocks in
  `MissionsWindowUI.cs` (`@444`, `@506`, `@1242`), mirroring `SuppressionGuard.cs`.
- **Skip:** `MissionComposition` (coherent recursive builder), `MissionStore`,
  `MissionStructure` (short, already delegated), `UI/StructureListWindowUI`.

### Logistics codec / proof + Logistics UI

- **`UI/LogisticsWindowUI.DrawRouteRow` (@679, ~167)** — Pass1, **runtime**.
  Highest line-reduction-per-risk in this batch. Extract the self-contained
  Status / Badge / Actions cell blocks into three `private void` helpers
  consuming already-computed `leg`/`sendOnceArmed` locals. **Must preserve the
  IMGUI Layout/Repaint control-count invariant** (the window already documents
  this hazard near `DrawTooltipEchoBox`). No pure unit coverage — validate by
  opening the window in-game.
- **`DrawIntervalCell` (@861, ~93)** — Pass1: split the editing-vs-display
  TextField branch. The two *detail* steppers (`DrawCadenceStepper@1621`,
  `DrawPriorityStepper@1662`) share byte-identical chrome and dedup into one
  `DrawDetailStepper(...)`; the interval cell's stepper uses a different `-`
  width (20f vs 24f) so it is **not** mergeable. `runtime`.
- **RouteCodec ↔ RouteProofCodec shared node codec (Pass2, `pure`)** — the
  largest cross-file dup: `SerializeEndpoint`/`DeserializeEndpoint`, the
  inventory-item codec, and the `ResourceAmount` codec are field-identical across
  both files (the source comment already documents the deliberate shape-match).
  **Byte/field-order-critical across two frozen on-disk surfaces (gen-4 schema,
  no migrations).** Propose a `RouteNodeCodec` owner; do **not** fold in the
  resource-manifest codec (RouteCodec uses flat `name=amount`, RouteProofCodec
  uses nested `RESOURCE{...}` nodes — different shapes). Also dedup the two
  identical `ParseConnectionKind` copies.
- **`RouteCodec.DeserializeFrom` (@238, ~184)** — Pass1, `pure`: collapse the 5
  repeated "GetValue/if-empty→null" idioms into `NullIfEmpty(string)` and the two
  identical repeated-value loaders into `LoadStringList(...)`. Do **not** touch
  the scalar `AddValue` ordering in either codec half (byte-order contract).
- **`RouteProofCodec` (Pass1, `pure`):** host the inline value-key literals
  (`"WINDOW"`/`"ITEM"`/`"RESOURCE"`/`"pid"`/`"name"`/`"amount"`/`"maxAmount"`) in
  a constants block to match RouteCodec's documented convention.
- **Skip:** `ComputeRouteLegibility` (manifest-shaped, already delegated),
  `RouteProofHasher.ComputeRouteProofHashFromRecording` (append order is a frozen
  fingerprint — extraction risk > value), `LogisticsDeliveryPresentation`,
  `RouteCreationFormatters`, `RouteProofMetadata` (already pure owners),
  `RouteCreationDialog`, `CurrencyReservationOverlay`.

### Logistics dispatch backend (`Logistics/Route*`, `GameActions/RouteModule`)

Pure analysis/builder core is well-factored; the genuine surface is one giant
builder method, a handful of within-file twin-method dedups, and two cross-file
duplications. **Ledger row order and currency-mutation timing in
`RouteOrchestrator`/`RouteModule` are behavior-critical (CLAUDE.md) — extract
only identical heads, never reorder emits.**

- **Pass1 — `Logistics/RouteBuilder.BuildRoute` (@52, ~534 lines)** — the single
  largest function in the whole audited surface. Extract a `Reject(reason)`
  `RouteBuildOutcome` factory (folds ~8 repeated `Info + return` reject gates), a
  `ResolveBackingMissionGeometry` phase, and the final `Route` assembly phase.
  Highest value / lowest risk (`pure`, no ledger coupling). Do **not** dedup
  `IsSurfaceSituation` with `RouteEndpointResolver`'s (int bitmask vs enum — not
  equivalent).
- **Pass1 — `RouteOrchestrator` twin-method + replay-branch dedup (mostly `pure`,
  file is `runtime`)** — fold `IsDeliveryAlreadyInLedger` (@1883) +
  `IsRecoveryCreditAlreadyInLedger` (@1925) into one
  `ElsContainsRouteCycleRow(type, routeId, cycleId)` (structurally identical), and
  extract the shared *head* (guard + `CompletedCycles` bump + log) of the two
  replay branches (`ApplyDelivery@1586`, `EmitLoopCycle@981`) — keep the differing
  tail state-cleanup inline. Also phase-split `EmitDispatchDebit@1292` (origin
  debit branch) and `ProcessLoopRoute@569` (blocked vs fired branches).
- **Pass1 — `Logistics/RouteStore.RevalidateSources` (@266, ~211)** — extract
  `BuildErsIndex`, `InspectRouteSources`, `DecideRevalidatedStatus`. `pure`,
  ERS-gated, has `ResetForTesting`.
- **Pass1 — `RouteRunCostCalculator`** — fold `SumRecoveredCredits` /
  `SumRecoveredCreditsForCandidate` (documented "identical predicate") onto one
  core; make `ResolveTreeRecordingIds(Route)` delegate to the tree overload.
- **Pass2 — `RouteIds.Short(string)` helper (`pure`, trivial but cross-cutting)**
  — `ShortId(string)` is byte-identical across `RouteStore`, `RouteTreeGuard`,
  `RouteRunCostCalculator`, `RouteOrchestrator` (+ 4 `Route`-typed wrappers).
  Host one helper; do it as its own commit.
- **Pass2 — `RouteResourceTankIterator` owner (`runtime`)** — the loaded/unloaded
  `Part.Resources` / `ProtoPartResourceSnapshot` tank-walk with the
  `ShouldDeliverToResource` flow gate is reimplemented 3× across
  `LiveDeliveryWriters`, `LiveOriginDebitWriters`, `LiveDeliveryCapacityProbe`.
  Propose a shared iterator taking a per-tank callback (each consumer keeps its
  distinct accumulation: delivery reads stored+capacity, debit reads stored,
  probe reads free). Highest dedup payoff but live-mutation coupled — sequence
  after the Pass1 items, validate in-game.
- **Skip:** `RouteAnalysisEngine` (flat dispatcher, minor `RejectResult` factory
  only), `RouteDispatchEvaluator`, `RouteLoopClock`, `RouteEndpointResolver`,
  `RouteCandidateFinder`, `Route` DTO, `GameActions/RouteModule` (walk-order
  contract — do not touch).

### Rendering / Display core + MapRender

**This batch is unusually mature** — the pure math/decision surfaces were already
factored into `internal static` xUnit-tested helpers in prior work, magic
literals are already named `const`s, and every static cache has
`Clear()`/`ResetForTesting()`. Most files are Skip; the only real same-file
target is one large headless method.

- **Pass1 — `Rendering/RenderSessionState.RebuildFromMarker` (@498, ~370)** — the
  highest value/risk pick. A strict ordered guard cascade (marker-null →
  no-recordings → origin-missing → no-parent-BP → no-siblings → live-vessel-missing
  → live-no-point → live-body) then a cohesive sibling-anchor write loop. Lift the
  guard blocks into `bool`/`Try*` helpers with no reordering; keep the loop whole.
  `pure`/headless. `TryEvaluatePerSegmentWorldPositions@1028` (~178) is a
  secondary same-pass candidate. Strong behavior lock: the logging tests pin the
  exact `Pipeline-Session`/`Pipeline-Anchor` lines.
- **Skip (do NOT refactor now) — `Display/GhostTrajectoryPolylineRenderer.cs`
  (4654)** — biggest file in the batch, but its decision helpers are already
  extracted/tested; the residual length is irreducible per-frame orchestration
  (`Driver.LateUpdate@2962 ~415`, `DecideForwardWindowForRecording@3967 ~290`)
  bound to the `-50`/onPreCull pending-draw handoff, `Time.frameCount` stamping,
  Vectrosity one-shot draw, ownership-publish sets, and per-frame rate-limit keys
  under the 8b/8e/8f visual contracts. Any extraction is `runtime`-only and needs
  full in-game FLIGHT+TRACKSTATION map validation for marginal gain.
- **Skip:** `SmoothingPipeline` (already decomposed), `TerrainCacheBuckets`,
  `SectionAnnotationStore`, `ForwardRenderWindow` (pure, ideal),
  `MapRender/ShadowRenderDriver` / `ChainAssembler` / `GhostRenderReconciler`
  (short, helpers already pure).
- **Do NOT move `Rendering/OutlierClassifier.OutlierThresholds.Default` literals
  to ParsekConfig** — they are hashed into the pannotations sidecar config-hash
  (`PannotationsSidecarBinary.ComputeConfigurationHash`), so relocating them is a
  serialization-contract change, not behavior-preserving. The whole `Rendering/`
  tree deliberately avoids ParsekConfig to stay deterministic/headless.

### Anchor resolution + Reaim

Mostly **pure** math, well-factored; the standout is a cross-file duplication
between two runtime resolvers, and the Reaim numerical kernel should be left
alone (its tolerances are algorithm-intrinsic, not config).

- **Pass2 — shared anchor world-frame / context-factory owner** for
  `Rendering/ProductionAnchorWorldFrameResolver.cs` and
  `RecordedRelativeAnchorPoseResolver.cs`. ~150 duplicated lines
  (`TryFindFocusTree`, `ResolveAbsoluteWorldPosition`, `ResolveBodyWorldRotation`,
  `TryResolveOrbitalAnchorPose`, `ResolveBody`, context build). **Not a mechanical
  dedup:** the bodies diverge (`ResolveBody` uses `b.bodyName` vs `b.name` and
  catches different exception types; the two `TryBuildContext` overloads wire
  different live-anchor delegates; the recorded resolver has an extra
  `[ERS-exempt]` `TryFindRecordingById` branch). Requires a behavioral-equivalence
  decision on the `ResolveBody` divergence before any move. `runtime`.
- **Pass1 — `RelativeAnchorResolver.TryResolveRelativeSectionPose` (@1555, ~151)**
  — extract the loop-anchor-mismatch / live-PID parent-pose block (`@1575-1648`)
  into `TryResolveRelativeSectionAnchorPose`; the relative-frame compose math
  stays delegated to `TrajectoryMath`. Also (verification-gated) collapse the
  byte-identical `TryInterpolateRelativeFrame` / `…WithBracket` pair **if** the
  bracket variant is confirmed unused. `pure`.
- **Pass1 — `Rendering/AnchorCandidateBuilder.BuildAndStorePerSection` (@84, ~106)**
  — extract `BuildBranchPointTypeLookup` + the bounded per-candidate
  tally/Verbose block (`LogAndCountCandidates`); leave `Compute`'s per-source
  emitters untouched. Lowest-risk Pass1 in the batch. `pure`.
- **Latent test-bleed traps (observational, low-risk):** add `ResetForTesting()`
  to the process-wide `Reaim/ReaimPlaybackResolver.Shared` (it has only
  `Clear()`), and guard-or-keep-the-warning on the mutable
  `Reaim/ReaimTransferSynthesizer.TransferSolver` static.
- **Skip:** `AnchorCorrection` (math-sensitive lerp value types), `AnchorDetector`
  (already pure predicates), and the Reaim numerical core (`UvLambert`,
  `ReaimWindowPlanner`, `DestinationArrivalSolver`, `ReaimClassifier`,
  `ReaimTransferSynthesizer` glue) — well-factored; solver convergence epsilons
  belong with the algorithm, **not** in ParsekConfig.

### Re-Fly / supersede / switch-segment / scene-exit / patches

**Already unusually well-factored** — most files post-date Refactor-4 and were
written with extracted pure predicates. The genuine surface is concentrated in 4
files; the rest are Skip (linear reason-emitting gate cascades or thin
orchestrators where splitting would fragment a single-decision flow).

- **Pass1 — `SwitchSegmentBuilder.CreateSwitchContinuationSegment` (@344, ~160)**
  — best value/risk: `pure`, headless. A 6×-identical 5-line precondition-failure
  block (`FailureReason = …; LogCreationRefused(…8 args…); return result;`) folds
  into one `Refuse(result, reason, …)` helper (guideline #10). Leave the build
  phases inline.
- **Pass1 (careful — Harmony Postfix) — `Patches/GhostOrbitLinePatch.Postfix`
  (@640, ~396)** — the longest method in the batch; ~8 terminal branches each
  repeat `line.active=…; drawIcons=…; ghostsWithSuppressedIcon.Add/Remove(pid);
  LogOrbitLineDecision(…); return;`. Dedup into `ApplyLineDecision(__instance,
  pid, active, icons, suppress, reason)` — the helper **must take Add/Remove and
  drawIcons as params** (they differ per branch). `runtime`, no headless coverage
  of the Postfix wiring → needs in-game map validation (ghost above/below
  atmosphere, burn-seam, transfer descent; no icon/line blink regression).
- **Pass1 — `StockUiOverlayController` 3×-duplicated disabled-overlay gate+log**
  (`DecorateRnD`/`DecorateAstronaut`/`DecorateMissionControl`) → one
  `OverlaysEnabledOrLogSkip(screenName)` (only the scene word differs). `runtime`
  (Unity); pure mark-builders already tested. Note: this file is the one
  static-state gap (≈4 static fields, no `ResetForTesting`) — but they are
  Unity-instance/event-driven, so it's a note, not an action item.
- **Pass1 — two contiguous sub-blocks in `Patches/MapFocusObjectOnSelectPatch.Prefix`
  (@119, ~362)** — the Case-C "separate committed target" classification
  (~265–297) and the `OpenDialog` arm's two no-op-auto-discard blocks (~328–384).
  `runtime`. The 4 dialog button-handlers share a guard but diverge in
  session/no-session bookkeeping → handler-dedup is **Pass2 (deferred)**, not
  Pass1.
- **Pass1 (careful) — `SceneExitInterceptor.Prefix` (@637, ~206)** — extract only
  the safest single contiguous block (the no-active-tree session-tree dialog,
  ~723–795 → `TryShowNoActiveTreeDialog`); the whole decision-matrix split is
  **deferred** (phase ordering is behavior-critical, mirrors refactor-4's
  treatment of `HandleRewindOnLoad`).
- **Skip / Pass2-deferred — `RecordingTreeSplitter` (1391)** — `SplitOriginAtRewindUT`
  (~440) / `RunPostSplitSteps` (~280) / `RollBackInMemory` (@1092 ~143) are dense
  numbered crash-recovery orchestrators where call order *is* the contract and
  rollback symmetry is load-bearing; leave whole (same rationale as refactor-4's
  `FinalizeIndividualRecording`).
- **Skip:** `UnfinishedFlightClassifier`, `TreeDiscardPurge`,
  `SwitchSegmentNoOpClassifier`, `ReFlyAutoSealPreview`, and the marker/session
  codecs (`ReFlySessionMarker`, `StockActionIntentMarker`, `SwitchSegmentSession`,
  `MarkerValidator`, …) — all have focused methods, named TTL/tolerance consts,
  and existing reset hooks.

### Observability tracers + orbit / storage / career / FX

The three tracers are **deliberately not targets**, and most orbit/storage/career
files are already pure and well-factored. The real surface is concentrated dedup
in the settings-persistence file and a couple of guard-cascade / probe methods.

- **Pass1 — `ParsekSettingsPersistence.cs` (`pure`)** — highest dedup density in
  the whole audit: 4 repeated patterns each ×6 — `LoadIfNeeded@87 ~117` bool-parse
  block → `TryLoadBool`; `ApplyTo` override-apply block → `ApplyBoolOverride`; the
  3 byte-identical `Record*Tracing` methods → one `RecordTracingFlag`; `Save`'s 10
  `if .HasValue) AddValue` lines. Headless via existing `*ForTesting` seams.
- **Pass1 — `TerminalOrbitSpawnSafety.Evaluate` (@49, ~107, `pure`)** — clean
  altitude/periapsis guard cascade → one `CheckXxx` per gate; high clarity +
  test-isolation gain, zero Unity coupling.
- **Pass1 — `MapRenderProbe.Sample` (@254, ~355, `runtime`)** — splits along its
  already-commented Tier blocks (`SampleIconJumpAnomaly` / `SampleIconOffOrbit` /
  `SampleLineBlink` / `SampleDecisionReconcile`); biggest single-method reduction
  in the batch, but the phases share many locals → needs a small context struct +
  call-order preservation, and validation is in-game (the pure
  `MapRenderTrace.Is*` predicates are already unit-covered).
- **Pass1 (lower value):** `PristinePartFxResolver` (PART-node / MODULE
  extraction), `PatchedConicSnapshot` (patch-capture loop, `runtime`/reflection),
  `OrbitSeedResolver` (build-orbit phase, `runtime`), `PannotationsSidecarBinary.TryRead`
  (block-parse extraction, `pure`), `UI/SettingsWindowUI` (Defaults-block →
  `ApplyDefaults`), `UI/GroupPickerUI.ApplyGroupPopupChanges` (add/remove loop
  dedup), `UI/TestRunnerUI` (IMGUI section extraction).
- **DOC-DEFERRED (do not touch) — tracer formatters.** The byte-identical
  `FormatVector3d`/`FormatVector3`/`FormatQuaternion`/`FormatDouble`/`Token`/`Bool`/
  `ShortId` set is duplicated across `GhostRenderTrace`, `MapRenderTrace`, and
  `LedgerTrace` **by design**; CLAUDE.md explicitly defers the shared
  `RenderTraceFormat` owner and forbids touching `GhostRenderTrace.cs`. Record it
  as a known future Pass2, not actionable now.
- **Optional micro-Pass2:** `IsFinite(double)`/`IsFinite(Vector3d)` is triplicated
  across `OrbitReseed`, `OrbitSeedResolver`, `OrbitalCheckpointDensifier` → a tiny
  `OrbitMathUtil.IsFinite` owner (very low value).
- **Skip:** `OrbitSegmentCheckpointBridge`, `OrbitalCheckpointDensifier`,
  `LedgerGroundTruthDiff`, `CareerSaveParser`, `LedgerGroundTruth`,
  `PostLoadStripper`, `RecoveryPayoutContext`, `RewindPointReaper`,
  `RewindPointDiskUsage`, `Timeline/TimelineEntryDisplay`, `GhostAudioPresets`,
  `ReStockPatchFxIndex`, `DebrisRelative*`, `TraceSeparation` — pure, focused,
  reset hooks present where stateful.

## Cross-Batch Synthesis

**Headline:** the code added since refactor-4 is, on the whole, **markedly
healthier than the legacy giants the prior passes fought.** Pure decision logic is
already extracted into `internal static` xUnit-tested helpers, tuning values are
already named `const`s with rationale, and stateful files generally already carry
`Reset*ForTesting` hooks. There is **no large generic-extraction backlog** in the
new files comparable to the `ParsekFlight`/`GhostMapPresence` situation. The
opportunities are narrower and fall into four shapes:

1. **A few genuinely large single methods** that are pure and lift cleanly:
   `RouteBuilder.BuildRoute` (~534), `MissionLoopUnitBuilder.TryBuildMissionUnit`
   (~537), `RenderSessionState.RebuildFromMarker` (~370),
   `MapRenderProbe.Sample` (~355, runtime).
2. **Repeated-block dedups** within a file: `ParsekSettingsPersistence` (×6
   patterns), `SwitchSegmentBuilder.Refuse`, `GhostOrbitLinePatch.Postfix`
   branch shape, `StockUiOverlayController` disabled-gate, `RouteCodec` null/list
   loaders.
3. **Cross-file dedups requiring an owner proposal** (Pass2): the byte-order
   `RouteNodeCodec` (RouteCodec ↔ RouteProofCodec), the live
   `RouteResourceTankIterator` (3 logistics writers/probe), the anchor
   world-frame/context factory (Production ↔ Recorded resolvers), the trivial
   `RouteIds.Short` helper, and the **doc-deferred** `RenderTraceFormat`.
4. **Things to deliberately leave alone:** the per-frame render orchestration in
   `GhostTrajectoryPolylineRenderer` + the Harmony patch bodies (runtime-only,
   visual-contract-bound), `RecordingTreeSplitter` (ordered crash-recovery), the
   Reaim numerical kernel and `OutlierThresholds` (math-/hash-sensitive), and the
   ledger-walk order in `RouteOrchestrator`/`RouteModule`.

## Recommended Next Proposal

> **Execution roadmap + per-slice proposal docs:** `refactor-5-slices.md` (index +
> shared zero-logic-change rules + the universal validation/review gate), with
> `refactor-5-slice1-proposal.md` … `refactor-5-slice6-proposal.md` covering every
> actionable item below. Implement from a checkout that can build + run the xUnit
> gate (the audit container has no .NET SDK).

Sequence by value/risk, each as its own focused proposal + clean-context review
(`docs/dev/refactor-guidelines.md`). Start with the **pure, headless-testable,
single-file** wins before any cross-file owner or runtime extraction:

**Slice 1 — Pure same-file method extractions (lowest risk, immediate win).**
Bundle the three highest-value pure extractions, one commit each:
- `Logistics/RouteBuilder.BuildRoute` → `Reject` factory + geometry +
  assembly phases. Validate: `--filter ~RouteBuilderTests`.
- `MissionLoopUnitBuilder.TryBuildMissionUnit` → `TryApplyReaim` + the two
  `ReaimDiag` dumps + summary log. Validate:
  `--filter "~MissionLoopUnitBuilderTests|~MissionZeroDriftScheduleTests|~MissionLoiterKnobTests"`.
  (Order-sensitive — review must enforce checklist items 2 & 5.)
- `Rendering/RenderSessionState.RebuildFromMarker` guard cascade. Validate:
  `--filter "~RenderSessionStateTests|~RenderSessionStateLoggingTests"`.

**Slice 2 — Pure repeated-block dedups.** `ParsekSettingsPersistence` (×6),
`SwitchSegmentBuilder.Refuse`, `RouteCodec` `NullIfEmpty`/`LoadStringList`,
`RouteRunCostCalculator` `SumRecoveredCredits*`. All headless; each its own
commit with the matching `~Route*` / `~SettingsPersistence` / `~SwitchSegment`
filter green.

**Slice 3 — The trivial cross-cutting `RouteIds.Short` owner** (one tiny helper,
touches ~8 files, byte-identical bodies) — do as a standalone commit with the
full `~Logistics`/`~Route` suites green.

**Slice 4+ — Cross-file owner proposals (Pass2, discuss before landing).** In
risk order: `RouteNodeCodec` (byte/field-order-critical, two frozen on-disk
surfaces — the riskiest; gate on the round-trip serialization suites), the
anchor world-frame/context factory (prove the `ResolveBody` / delegate
divergences first), and the live `RouteResourceTankIterator` (runtime — needs
in-game delivery/debit validation).

**Defer:** every runtime/IMGUI/Harmony extraction (`DrawRouteRow`,
`GhostOrbitLinePatch.Postfix`, `MapRenderProbe.Sample`, `StockUiOverlayController`)
until a slice explicitly budgets in-game validation; the per-frame
`GhostTrajectoryPolylineRenderer` orchestration and `RecordingTreeSplitter`
indefinitely (value < risk).

## Cross-Cutting Follow-Ups

- **Magic-literal audit is largely NOT warranted** for the new files — tuning
  values are already named `const`s. The only real gaps: inline ConfigNode
  value-key strings in `RouteProofCodec` and a couple of IMGUI indent literals.
  Explicitly **do not** move `Rendering/OutlierClassifier.OutlierThresholds` or
  the Reaim solver epsilons to ParsekConfig (config-hash / algorithm-intrinsic).
- **Static-state reset hooks** are mostly already present. Two small gaps worth a
  one-line observational fix when an adjacent slice lands:
  `Reaim/ReaimPlaybackResolver.Shared` (has `Clear()`, no `ResetForTesting`) and
  the mutable `Reaim/ReaimTransferSynthesizer.TransferSolver` static. The
  `RouteOrchestrator` `*ForTesting` injection statics are test-only (null in
  prod) — note, not action.
- **Shared IMGUI helpers** (a `LogSuppressionScope` IDisposable for the 3×
  Missions suppression try/finally; a `DrawDetailStepper` for the two logistics
  detail steppers) are plausible small Pass2 owner types — propose only if a UI
  slice is already open.
- This report covers **new files only**. The legacy giants that kept growing
  (`ParsekFlight` 14.5k→28.7k, `GhostMapPresence` 3.4k→13.0k, `FlightRecorder`
  6.7k→11.2k) remain tracked by `refactor-remaining-opportunities.md` and are a
  separate, higher-risk effort.

