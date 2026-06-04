# Plan: Map / TS Ghost Render Architecture — Clean Modular Rewrite (phases)

*Step-4 implementation plan for `docs/dev/design-map-ts-render-architecture.md` (the
authoritative design — read it first). This plan maps the design onto the codebase as an
ordered, incremental, reversible migration. It is the durable companion to the design doc;
task-level detail is created per phase by the orchestrator.*

*Revised after a clean-context plan review: the shadow-mode contract is now decision-only for
the StockConic surface, the reconciler is sequenced before the scene wiring it verifies, and
the cutover is split per-surface. See "Shadow contract" and Phase 8.*

## Principles

- **Incremental and reversible.** Every Workstream-A phase introduces the new module behind
  existing behavior; the old coordination is retired **per surface** in Phase 8, each step
  gated by build + test + in-game verification.
- **Shadow contract (decision-only).** In Phases 4–6 the new module computes chain → sample →
  intent and the **reconciler compares the intent against the OLD path's rendered truth**
  (intent-vs-old-truth parity). It does **not** write the stock surfaces in shadow. Reason: the
  StockConic surface is a *single shared stock object per pid*, re-asserted every frame by
  `GhostOrbitLinePatch.Postfix` (`:425`) + `GhostOrbitIconDrivePatch.Prefix` (`:107`); a second
  writer would *fight* the still-live patch and the reconciler would see thrash that is an
  artifact of running both, not a real anomaly. Only the **fully-Parsek-owned TracedPath**
  surface (its own polyline objects) may additionally do a real shadow draw. The StockConic
  surface is therefore **not shadow-drawable**; it is flipped *first* at cutover (8a) by
  replacing the patch's decision body with `Director.Decide` behind a runtime gate — not
  deleted wholesale.
- **Three workstreams.** **A** = core render module (critical path). **B** = solver extraction
  behind `ITransferSolver`/`IEncounterSolver` (parallel, isolated; A does NOT depend on B's
  internals — confirmed). **C** = surface-track closeout in the re-aim pipeline (parallel).
- **New module home:** `Source/Parsek/MapRender/`, namespace `Parsek.MapRender`.
- **Diagnostic logging is homed per layer** (CLAUDE.md "every branch logs"): each phase emits
  its own design-§13 log lines with a co-located log-assertion test — logging is NOT deferred
  to the reconciler phase.
- **Review gates** follow `development-workflow.md` §4: full clean-context review on the first
  logic in the new area and every risky phase; self-review for additive/test-only phases.

## Grounding (confirmed against the code)

- Span clock reusable: `GhostPlaybackLogic.TryComputeSpanLoopUT` / `DecompressSpanUT` /
  `ResolveTrackingStationSampleUT` — pure `(liveUT, unit) → assembled-UT`.
- Loop-unit owner: `MissionLoopUnitBuilder.Build → LoopUnitSet` (cached `cachedLoopUnits`);
  Director consumes it directly, not via `GhostPlaybackEngine.CurrentLoopUnits` (a passthrough).
- Solver funnel tiny: `ReaimPlaybackResolver` → `ReaimTransferSynthesizer.TrySynthesizeTransfer`
  → `UvLambert.Solve` (pure, `UvLambertTests`). **A is downstream of the resolver and already
  `OrbitSegment`-only, so A does not depend on B's extraction.**
- Conic test reusable: `IsOrbitSegmentBelowSurface` + `ComputeOrbitalCoverIntervals` (internal
  static in `GhostTrajectoryPolylineRenderer.cs`).
- **Coupling reality (drives the Phase-8 split):** the gate flags fan out across ~6 files —
  `GhostMapPresence.cs` (9860 lines; 15+ sites incl. scene-change clears), `GhostOrbitLinePatch.cs`
  (~20 sites), `ParsekTrackingStation.cs`, `ParsekUI.cs` (marker skip), `MapRenderProbe.cs`. The
  flight-map presence drive `ParsekPlaybackPolicy.CheckPendingMapVessels` (~300 lines) interleaves
  the deferred-create queue, re-aim segment swap, loop-epoch-shift, terminal-orbit synthesis, and
  the materialized-vessel bypass — it is NOT separable by a section boundary. Cutover is a
  per-surface rewrite, not ~5 deletions.
- Tracer substrate exists: `MapRenderTrace`/`MapRenderProbe` (tiered emit, anomaly predicates,
  `LineRenderIntent`). Test substrate: `MockTrajectory : IPlaybackTrajectory`, `RecordingBuilder`.

## Phase order (critical path)

`0 → 1 → 2 → 3 → 6 → 4 → 5 → 7 → 8(a–e)` — the reconciler (6) lands **before** the scene wiring
(4) whose "done" it verifies. B is parallel after Phase 0; C is parallel (re-run §1 assembler
tests on C-merge).

---

## Workstream A — Core render module

### Phase 0 — Probes first, then module skeleton + data model
- **Task 0.0 (FIRST, gating).** Resolve the §15.3 probe: do v1 re-aim recordings retain debris
  that decouples *during* the heliocentric leg? This **gates the `RenderSegment` frame field
  shape**. If yes → `Frame` is a discriminated union `body | parent-generated-conic`; if no →
  `FrameBodyName` string + a documented deferral. Do NOT create `RenderSegment` before this
  answer. Also run the §15.1 (proto re-seed latency) and §15.2 (per-scene patched-conic
  divergence) probes; record findings.
- **Create (after 0.0).** `MapRender/RenderSegment.cs` (frame field per 0.0; + `SegmentKind`/
  `Treatment`/`SeamKind`/`Coverage` enums, `SegmentPayload`), `MapRender/GhostRenderChain.cs`
  (incl. `CommittedIndex`, `InstanceKey`, `IsFaithfulFallback`, O(log n) locate), `GhostSample.cs`,
  `GhostRenderIntent.cs`.
- **Tests.** `MapRender/GhostRenderChainTests.cs` — locate over a UT sweep; gap returns gap.
- **Done.** Build + tests green; probe findings recorded; `Frame` shape matches 0.0. **Deps:** none.
- **Review.** Orchestrator reviews the type shapes (sets the pattern; the `Frame` decision is load-bearing).

### Phase 1 — `ChainAssembler` (L1)
- **Goal.** Pure `Build(IPlaybackTrajectory, LoopUnit, faithfulSignal) → GhostRenderChain`,
  cached by `(BuildSignature, reaim-window index, InstanceKey)`.
- **Create.** `MapRender/ChainAssembler.cs`. Treatment assignment via the existing conic test;
  split each segment at intra-arc SOI crossings (one frame per segment). **Emit the §13 assembly
  log line + co-located log-assertion test.**
- **Tests.** `MapRender/ChainAssemblerTests.cs` (MockTrajectory): per-structure assembly,
  treatment assignment, intra-arc frame split, faithful-fallback. **C-merge note:** when C1/C2
  land (new recorded surface coverage on ascent/descent members), re-run/extend these tests with
  re-stitched-descent + single-recording-ascent fixtures — the assembler is written against
  today's data shapes.
- **Done.** Tests pass; callable but unused. **Deps:** Phase 0. **Review.** Full.

### Phase 2 — `ChainSampler` (L2)
- **Goal.** Pure `Sample(chain, liveUT) → GhostSample`; reuse the span clock verbatim; three-valued
  coverage (`InSegment` / `InInteriorGap` / `OutsideWindow`). **Emit the §13 locate+coverage log
  + assertion test.**
- **Tests.** UT sweep incl. a flexible-seam gap; span-clock + loiter-cut + future-launch +
  re-aim-window-advance. **Done.** Tests pass. **Deps:** Phase 1. **Review.** Full.

### Phase 3 — `GhostRenderDirector` (L3)
- **Goal.** `Decide(sample) → GhostRenderIntent` — single Parsek-owned decision; gap classification;
  make-before-break swap as a bounded swap state (model the §15.1 finding); explicit single-owner
  scope. **Emit the §13 intent/swap log + assertion test.**
- **Tests.** Intent invariants (never two treatments, never icon-off-line on the owned surface);
  gap-vs-retire; swap-state; overlap instance keying. **Done.** Tests pass. **Deps:** Phase 2.
- **Review.** Full, extra attention (the "no single owner" fix lives here).

### Phase 6 — `GhostRenderReconciler` (L5) — built BEFORE the scene wiring
- **Goal.** Extend `MapRenderTrace`/`MapRenderProbe` to compare the first-class `intent` against
  the **old path's** rendered truth (the shadow signal Phases 4–5 need): per-decision-point emits
  + new anomaly predicates (polyline origin-shift, decision-vs-old-truth, gap-vs-retire).
- **Modify.** `MapRenderTrace.cs`/`MapRenderProbe.cs`; optional `MapRender/GhostRenderReconciler.cs` façade.
- **Tests.** Injected divergence flagged; **log-assertion tests for the reconciler-anomaly lines
  only** (per-layer lines are tested in their own phases). **Done.** Flags injected anomalies.
  **Deps:** Phase 3 (only needs `GhostRenderIntent`; no dependency on Phase 4). **Review.** Full.

### Phase 4 — `IGhostMapScene` + `MapViewScene` (flight), decision-only shadow
- **Goal.** Scene adapter + flight-map impl; wire Director→intent→reconciler in **decision-only
  shadow** (compute intent, compare vs old-path truth; write **nothing** to stock surfaces).
- **Tasks (explicit — these were prose-only before):**
  1. **Proto-vessel lifecycle behind the adapter as a pass-through** — `IGhostMapScene` wraps the
     existing create/destroy (`BuildAndLoadGhostProtoVesselCore`/`CreateGhostVessel`/`RemoveGhostVessel`),
     `ghostMapVesselPids`, and bounds caching, delegating to today's `GhostMapPresence` methods. This
     makes Phase 8 a *flip*, not a discovery that the relocation was never done.
  2. **Shared per-frame floating-origin frame** in `IGhostMapScene`, consumed by both treatments
     and all instances (design §6.6) — with an origin-shift reconciler test (pairs with Phase 6).
  3. **Camera-focus continuity across a make-before-break swap** (re-home / persistent anchor) —
     with the §14 in-game focus-through-swap test cross-referenced.
  4. The shadow call site in `ParsekPlaybackPolicy`/`GhostMapPresence` (compute + reconcile only).
- **Done.** Decision-only shadow runs; reconciler reports intent-vs-old-truth parity on flight-map;
  origin-frame + focus tasks have tests. **Deps:** Phase 6 (the reconciler). **Review.** Full.

### Phase 5 — Treatments (L4)
- **Goal.** `TracedPathTreatment` (fully owns polyline+marker; **may do a real shadow draw** since
  it owns its objects) and `StockConicTreatment` (drives stock proto+line at `DriveUT`; **MANAGED**;
  in shadow it stays **decision-only**, no stock writes — it only produces intent for the reconciler).
  Reuse the polyline + arc-clipping/icon-drive mechanics. **Emit the §13 seam + moon-config logs +
  assertion tests.**
- **Done.** TracedPath follows intent in shadow (real draw, diffable); StockConic intent matches
  old-path truth via the reconciler; no double-draw/icon-off-line on the TracedPath shadow.
- **Deps:** Phase 4. **Review.** Full, **extra attention on `StockConicTreatment`** (the managed↔KSP
  surface; confirm the cutover-8a flip plan, not a shadow draw, is how it gets exercised).

### Phase 7 — `TrackingStationScene` (split)
- **7a — Adapter at parity.** `MapRender/TrackingStationScene.cs`; proto-vessel lifecycle behind the
  adapter as a pass-through (as Phase 4 task 1, for TS); drive the existing single-instance proto
  from the chain at parity with today's `ResolveTrackingStationSampleUT` remap (no new behavior).
  **Done:** TS shadow parity with today. **Deps:** 3–6.
- **7b — New TS behavior.** make-before-break + cold-start-mid-segment (§10.19) + per-scene
  patched-conic divergence handling (§10.20), **gated on the resolved §15.2 finding**. If §15.2 came
  back "stock re-solves the patch chain materially differently per scene," that is a **design
  escalation / stop-point**, not a Phase-7 implementation detail — flag to the orchestrator.
  **Review.** Full, extra attention — riskiest scene.

### Phase 8 — Cutover, per surface (split into gated sub-phases)
Each sub-phase makes the Director authoritative for ONE surface, with its own build + test +
in-game gate. **Not "mechanical" — each is a ~1–3-file rewrite of a visibility contract.**
- **8a — StockConic line/icon.** Replace the decision body of `GhostOrbitLinePatch.Postfix`
  (`:425`) + `GhostOrbitIconDrivePatch` (`:107`) with `Director.Decide`, behind a runtime gate
  (keep the patch as the *mechanism*, swap its *decision*). This is the StockConic surface's first
  real exercise — its own in-game gate. **Riskiest sub-phase.**
- **8b — TracedPath polyline ownership.** Retire `activeLegRecordings` / `IsRenderingNonOrbitalLeg`
  publish + visibility logic from `GhostTrajectoryPolylineRenderer.cs`; the treatment owns it.
- **8c — Marker / icon-suppression.** Retire `ghostsWithSuppressedIcon` + the marker-skip paths in
  `ParsekUI.cs` (`:1170`) and `GhostOrbitLinePatch.cs`, and `ClassifyAtmosphericMarkerSkip`.
- **8d — Flight-map presence extraction.** Extract the map-presence half of
  `ParsekPlaybackPolicy.CheckPendingMapVessels` (the ~300-line interleaved method) behind the
  Director/adapter; leave the mesh/spawn half. **Multi-session refactor; treat as its own mini-plan.**
- **8e — Cleanup.** Delete `ghostOrbitLineGraceUntilFrame` + remaining grace fields; remove the
  implicit −50/0 ordering contract; grep-audit that deleted flags have no readers (mirror
  `GrepAuditNonLoopLivePidTests`); non-looped regression test.
- **Out of scope (untouched):** `GhostPlaybackEngine` mesh path, `IGhostPositioner`,
  `ParsekPlaybackPolicy` mesh/spawn half, recording/recorder/scheduler.
- **Deps:** Phases 4–7 + **B and C merged**. **Review.** Full + mandatory in-game per sub-phase.

---

## Workstream B — Solver extraction (parallel after Phase 0; merge before 8 — over-conservative, can merge anytime)
- **B1 `ITransferSolver`** (`UvLambert.Solve` default impl; guarded by `UvLambertTests`).
- **B2 `IEncounterSolver`** (isolate `CalculatePatch` + proximity fallback from `TrySynthesizeTransfer`;
  keep plane-projection/handedness/tof-search Parsek-side). **Decision before B2 (§15.4):** add
  off-Unity coverage or accept canary-only. Extra review.
- **B3 frame-agnostic return** (`TransferConic`/`OrbitSegment` instead of live `Orbit`).

## Workstream C — Surface-track closeout (parallel; merge before 8; re-run §1 assembler tests on merge)
- **C1 single-recording ascent (§4.2).** Re-time recorded ascent onto the re-aimed departure;
  touches `ReaimedTrajectory.cs` + `ReaimSegmentAssembler.cs`.
- **C2 descent re-stitch (§4.1, largest).** Seam the recorded descent to the re-aimed capture — a
  **rigid, on-camera** orbit↔landing hand-off. **Decision before C2 (§15.5):** re-stitch approach +
  seam tolerance. Extra review.
- **C3 polish** (ascent re-timing / S1 / arrival blend) — may land after Phase 8.

---

## Risk hotspots (extra review)
1. **Phase 8a** (StockConic flip — first real exercise of the managed surface, no prior shadow draw).
2. **Phase 8d** (`CheckPendingMapVessels` extraction — ~300 interleaved lines).
3. **Phase 7b** (TS cold-start + per-scene patch divergence; may escalate on §15.2).
4. **Phase 8 overall** (per-surface staged; in-game gate each).
5. **B2** (`CalculatePatch` off-Unity test gap). **C2** (on-camera rigid seam tolerance).

## Gating decisions (resolve before the named phase)
- **§15.3 transfer-phase debris → FIRST Phase-0 task** (gates the `RenderSegment.Frame` shape).
- §15.1 re-seed latency → before Phase 3 (probed in Phase 0).
- §15.2 per-scene patch divergence → before Phase 7b (probed in Phase 0; may be a stop-point).
- §15.4 `IEncounterSolver` off-Unity coverage → before B2.
- §15.5 descent re-stitch tolerance → before C2.

## Parallelism map
- Critical path: A: `0 → 1 → 2 → 3 → 6 → 4 → 5 → 7a → 7b → 8a → 8b → 8c → 8d → 8e`.
- Parallel: B (any time after Phase 0); C (parallel with A — re-run §1 assembler tests on merge);
  no other A phases parallelize (each consumes the prior).
- Ordered: 1→2→3 (data→sample→decide); 6 before 4 (reconciler before the wiring it verifies);
  3 before any scene/treatment; 8 last (needs all shadow parity + B + C merged).

## Known plan risks the reviewer flagged (now addressed above)
- Shadow mode is decision-only for StockConic (cannot shadow-draw a single shared stock object the
  old patch co-owns) — Principles + Phases 4/5.
- Reconciler sequenced before scene wiring — Phase order.
- Cutover split per surface (8a–8e) with the real coupling acknowledged — Phase 8 + Grounding.
- Proto-vessel lifecycle relocation has an explicit pass-through task — Phases 4/7a.
- Origin-frame + camera-focus have tasks+tests — Phase 4.
- §15.3 debris probe gates the data model — Phase 0.0.
- §13 logging homed per layer — Phases 1/2/3/5.
</content>
