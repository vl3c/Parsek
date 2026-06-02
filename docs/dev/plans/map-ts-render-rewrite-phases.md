# Plan: Map / TS Ghost Render Architecture — Clean Modular Rewrite (phases)

*Step-4 implementation plan for `docs/dev/design-map-ts-render-architecture.md` (the
authoritative design — read it first). This plan maps the design onto the codebase as an
ordered, incremental, reversible migration. It is the durable companion to the design doc;
task-level detail is created per phase by the orchestrator.*

## Principles

- **Incremental and reversible.** Every Workstream-A phase introduces the new module **in
  shadow mode** behind existing behavior (compute chain → sample → intent → reconcile, while
  the old path still draws pixels). The scattered coordination is deleted **only at the
  Phase-8 cutover**, staged file-by-file with a build/test gate between each.
- **Three workstreams.** **A** = core render module (critical path, Phases 0–8). **B** =
  solver extraction behind `ITransferSolver`/`IEncounterSolver` (parallel, isolated). **C** =
  surface-track closeout in the re-aim pipeline (parallel). B and C merge **before** Phase 8 so
  the cutover renders the full chain.
- **New module home:** `Source/Parsek/MapRender/`, namespace `Parsek.MapRender`.
- **Review gates** follow the project workflow (`development-workflow.md` §4): full
  clean-context review on the first logic in the new area and on every risky phase; self-review
  for additive/test-only phases.

## Grounding (confirmed against the code)

- Span clock is the reusable seam: `GhostPlaybackLogic.TryComputeSpanLoopUT` /
  `DecompressSpanUT` / `ResolveTrackingStationSampleUT` — pure `(liveUT, unit) → assembled-UT`.
- Loop-unit owner: `MissionLoopUnitBuilder.Build → LoopUnitSet` (cached `cachedLoopUnits`); the
  Director consumes it directly, not via `GhostPlaybackEngine.CurrentLoopUnits` (a passthrough).
- Solver funnel is tiny: `ReaimPlaybackResolver` (two entry points → one synthesis path) →
  `ReaimTransferSynthesizer.TrySynthesizeTransfer` → `UvLambert.Solve` (pure, `UvLambertTests`).
- Conic test reusable: `IsOrbitSegmentBelowSurface` + `ComputeOrbitalCoverIntervals` (internal
  static in `GhostTrajectoryPolylineRenderer.cs`).
- Scattered gates to delete at cutover: `activeLegRecordings`, `ghostsWithSuppressedIcon`,
  `IsPolylineOwningGhostPhase`/`…Recently…`, `ghostOrbitLineGraceUntilFrame`, and the
  flight-map presence drive `CheckPendingMapVessels`/`ResolveMapPresenceSampleUT`/loop-epoch-shift.
- Tracer substrate exists: `MapRenderTrace`/`MapRenderProbe` (tiered emit, anomaly predicates,
  `LineRenderIntent` truth-recording) → the reconciler.
- Test substrate: `MockTrajectory : IPlaybackTrajectory`, `RecordingBuilder`, public `OrbitSegment`.

---

## Workstream A — Core render module

### Phase 0 — Module skeleton, data model, decision probes
- **Goal.** Land the runtime-only types (design §6.2/§6.10) and the folder/namespace skeleton,
  zero behavior change. Resolve the gating probes.
- **Create.** `MapRender/RenderSegment.cs` (+ `SegmentKind`/`Treatment`/`SeamKind`/`Coverage`
  enums, `SegmentPayload`), `MapRender/GhostRenderChain.cs` (incl. `CommittedIndex`,
  `InstanceKey`, `IsFaithfulFallback`, O(log n) locate-by-UT), `MapRender/GhostSample.cs`,
  `MapRender/GhostRenderIntent.cs`.
- **Probes (read-only, documented findings, no code):** §15.1 proto-vessel re-seed latency;
  §15.2 stock patched-conic per-scene divergence; §15.3 whether v1 re-aim recordings retain
  transfer-phase debris (decides if the frame abstraction must generalize in Phase 1).
- **Tests.** `MapRender/GhostRenderChainTests.cs` — locate over a UT sweep; gap returns gap.
- **Done.** Build + tests green; probe findings recorded. **Dependencies:** none.
- **Review.** Orchestrator reviews the type shapes (sets the pattern).

### Phase 1 — `ChainAssembler` (L1)
- **Goal.** Pure `Build(IPlaybackTrajectory, LoopUnit, faithfulSignal) → GhostRenderChain`,
  cached by `(BuildSignature, reaim-window index, InstanceKey)`. Not wired to a scene.
- **Create.** `MapRender/ChainAssembler.cs`.
- **Reuse.** Treatment-assignment via the existing conic test (`IsOrbitSegmentBelowSurface` +
  `ComputeOrbitalCoverIntervals`): StockConic iff above-surface conic, else TracedPath;
  generated transfer → StockConic. **Split each segment at intra-arc SOI crossings** so each
  has one `FrameBodyName`.
- **Tests.** `MapRender/ChainAssemblerTests.cs` (MockTrajectory): per-structure assembly
  (faithful full chain, re-aimed, direct departure/landing, non-looped); treatment assignment;
  intra-arc frame split; faithful-fallback chain.
- **Done.** Assembly tests pass; callable but unused. **Dependencies:** Phase 0.
- **Review.** Full (the treatment rule is load-bearing).

### Phase 2 — `ChainSampler` (L2)
- **Goal.** Pure `Sample(chain, liveUT) → GhostSample` with the three-valued coverage.
- **Create.** `MapRender/ChainSampler.cs`. **Reuse the span clock verbatim** to map liveUT →
  assembled-chain UT, then locate. Coverage: `InSegment` / `InInteriorGap` / `OutsideWindow`.
- **Tests.** `MapRender/ChainSamplerTests.cs` — UT sweep incl. a flexible-seam gap; span-clock +
  loiter-cut + future-launch + re-aim-window-advance integration.
- **Done.** Sampler tests pass; unused. **Dependencies:** Phase 1. **Review.** Full.

### Phase 3 — `GhostRenderDirector` (L3)
- **Goal.** `Decide(sample) → GhostRenderIntent` — the single Parsek-owned decision. Gap
  classification (`InInteriorGap`→hold/suppress icon-jump; `OutsideWindow`→retire);
  make-before-break swap as a bounded swap state (model the §15.1 answer here); explicit
  single-owner scope (owns Parsek surfaces + the show/hide decision; not KSP's `line.active`).
- **Create.** `MapRender/GhostRenderDirector.cs`. Test-driven only (old gates still authoritative).
- **Tests.** Intent invariants (never two treatments, never icon-off-line on owned surface);
  gap-vs-retire; swap-state transitions; overlap instance keying (distinct `InstanceKey`).
- **Done.** Director tests pass. **Dependencies:** Phase 2. **Review.** Full, extra attention
  (this is where the "no single owner" fix lives).

### Phase 4 — `IGhostMapScene` + `MapViewScene` (flight), shadow mode
- **Goal.** Scene adapter + flight-map impl; wire Director→treatments→scene **behind** existing
  behavior (shadow: compute intent + reconcile, old path draws). One floating-origin frame per
  frame shared across instances; camera-focus continuity across swap.
- **Create.** `MapRender/IGhostMapScene.cs`, `MapRender/MapViewScene.cs`.
- **Modify (minimal).** Add a shadow call site in `ParsekPlaybackPolicy`/`GhostMapPresence` that
  builds chain/sample/intent and feeds the reconciler, no live-draw change.
- **Done.** Shadow path runs; reconciler reports intent-vs-truth parity. **Dependencies:** Phase
  3 (+ Phase 6 for the comparison). **Review.** Full.

### Phase 5 — Treatments (L4)
- **Goal.** `TracedPathTreatment` (fully owns polyline+marker; reuses polyline drawing
  primitives) and `StockConicTreatment` (drives stock proto + orbit line at `DriveUT`; reuses
  arc-clipping/icon-drive; **MANAGED** — re-asserts every frame, reconciler catches blinks).
- **Create.** `MapRender/TracedPathTreatment.cs`, `MapRender/StockConicTreatment.cs`.
- **Done.** Treatments follow intent in the shadow path; no double-draw/icon-off-line in shadow.
- **Dependencies:** Phase 4. **Review.** Full, **extra attention on `StockConicTreatment`** (the
  managed↔KSP `line.active` surface; confirm re-assert + reconciler coverage are real).

### Phase 6 — `GhostRenderReconciler` (L5)
- **Goal.** Extend `MapRenderTrace`/`MapRenderProbe` to compare against the first-class `intent`:
  per-decision-point emits (assembly/locate/gap/intent/swap/seam/projection/moon-config) + new
  anomaly predicates (polyline origin-shift, decision-vs-truth-across-swap, gap-vs-retire).
- **Modify.** `MapRenderTrace.cs`/`MapRenderProbe.cs`; optional `MapRender/GhostRenderReconciler.cs` façade.
- **Tests.** Injected `line.active` toggle / origin shift → flagged; **log-assertion tests** for
  the decision lines. **Dependencies:** Phase 3 (can run parallel to 4–5). **Review.** Full.

### Phase 7 — `TrackingStationScene` (extend Phase-F into the chain model)
- **Goal.** Drive TS proto-vessels from the chain (treatment switching, make-before-break)
  instead of the single per-recording `ResolveTrackingStationSampleUT` remap; keep by-design
  single-span-instance.
- **Create.** `MapRender/TrackingStationScene.cs`. **Modify.** the
  `UpdateTrackingStationGhostLifecycle` call site (shadow path, consume `cachedLoopUnits`).
- **Done.** TS shadow path; reconciler shows flight↔TS intent parity. **Dependencies:** 3–6.
- **Review.** Full, **extra attention — riskiest scene** (verify §10.19 cold-start, §10.20
  per-scene patch divergence using the Phase-0 §15.2 finding).

### Phase 8 — Cutover (delete the scattered coordination)
- **Goal.** Make the Director authoritative in both scenes; delete the old gates. Mechanical,
  because the shadow phases proved parity.
- **Modify/delete (staged, build/test gate between each):** remove `activeLegRecordings`
  publish + visibility logic from `GhostTrajectoryPolylineRenderer.cs`; remove
  `ghostsWithSuppressedIcon`/`IsPolylineOwningGhostPhase`/grace reads from `GhostOrbitLinePatch.cs`;
  remove those fields from `GhostMapPresence.cs` (keep relocated lifecycle behind the adapter);
  **split** `ParsekPlaybackPolicy.cs` — delete the flight-map presence half, leave the
  mesh/spawn half; remove the implicit −50/0 ordering contract.
- **Out of scope (untouched):** `GhostPlaybackEngine` mesh path, `IGhostPositioner`,
  `ParsekPlaybackPolicy` mesh/spawn half, recording/recorder/scheduler.
- **Tests.** Non-looped regression; grep-audit that deleted flags have no readers.
- **Done.** Old gates deleted; full suite green; reconciler silent on the live path; in-game
  matrix verified. **Dependencies:** Phases 4–7 in shadow parity + **B and C merged in**.
- **Review.** **Full + mandatory in-game manual testing — the single riskiest phase.**

---

## Workstream B — Solver extraction (parallel, isolated worktree; merge before Phase 8)

- **B1 — `ITransferSolver` (pure Lambert).** `Reaim/ITransferSolver.cs`; `UvLambert.Solve`
  becomes the default impl; inject into `ReaimTransferSynthesizer`. Guarded verbatim by
  `UvLambertTests`.
- **B2 — `IEncounterSolver` (wraps `CalculatePatch` + proximity fallback).** Isolate the two
  halves currently co-located in `TrySynthesizeTransfer`; keep plane-projection / `IsSaneTransferConic`
  / handedness / tof-search Parsek-side. **Decision before B2:** add off-Unity coverage for the
  `CalculatePatch` path or accept canary-only validation (§15.4). Extra review.
- **B3 — Frame-agnostic return.** Synthesizer returns Kepler elements (`TransferConic`/`OrbitSegment`)
  instead of a live `Orbit`; push `ReaimOrbitSegmentConverter.ToSegment` behind the interface.

## Workstream C — Surface-track closeout (parallel; merge before Phase 8)

- **C1 — Single-continuous-recording ascent (§4 item 2).** Re-time the recorded ascent onto the
  re-aimed departure (or formalize chain decomposition); touches `ReaimedTrajectory.cs` +
  `ReaimSegmentAssembler.cs`. Tests: ascent `Points`/`TrackSections` survive onto the re-aimed member.
- **C2 — Descent re-stitch (§4 item 1, largest).** Seam the recorded descent to the re-aimed
  capture orbit — a **rigid, on-camera** orbit↔landing hand-off. **Decision before C2:** the
  re-stitch approach + acceptable seam tolerance (§15.5). Tests: position/velocity continuity at
  the seam. Extra review.
- **C3 — Ascent re-timing / S1 ejection synthesis / arrival seam blend (polish).** May land
  after Phase 8.

---

## Risk hotspots (extra review)

1. **Phase 8 cutover** — staged deletions, mandatory in-game verification.
2. **Phase 7 TS adapter** — riskiest scene; cold-start + per-scene patch divergence.
3. **Phase 5 `StockConicTreatment`** — the managed↔KSP `line.active` surface.
4. **B2 `IEncounterSolver`** — off-Unity test gap.
5. **C2 descent re-stitch** — on-camera rigid seam, undecided tolerance.

## Gating decisions (resolve before the named phase)

- §15.1 re-seed latency → before Phase 3 (probed in Phase 0).
- §15.2 per-scene patch divergence → before Phase 7 (probed in Phase 0).
- §15.3 transfer-phase debris → before Phase 1 assembler (probed in Phase 0).
- §15.4 `IEncounterSolver` off-Unity coverage → before B2.
- §15.5 descent re-stitch tolerance → before C2.

## Parallelism map

- Critical path: A: 0 → 1 → 2 → 3 → {4, 6} → 5 → 7 → 8.
- Parallel: B (after Phase 0); C (parallel with A); Phase 6 overlaps 4–5.
- Ordered: 1→2→3 (data → sample → decide); 3 before any scene/treatment; 8 last (needs all
  shadow parity + B + C merged).
</content>
