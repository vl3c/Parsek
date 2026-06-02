# Map/TS render rewrite — implementation status (branch `claude/maprender-rewrite`)

*Live status of the rewrite from `docs/dev/design-map-ts-render-architecture.md` +
`docs/dev/plans/map-ts-render-rewrite-phases.md`. Phases 0–3 were authored in a cloud container
with NO `dotnet` / NO KSP assemblies; they have since been BUILT + UNIT-TESTED on a KSP box
(2026-06-02) — see "Build + test verification" below. Phase 6 (reconciler) is now also built +
tested.*

*Commits are UNSIGNED (the harness SSH signer fails in a sibling worktree). Re-sign / squash at
merge if the repo needs verified commits.*

## Done (the pure pipeline — Phases 0–3, committed, UNBUILT)

`Source/Parsek/MapRender/` (namespace `Parsek.MapRender`, all `internal`):

- **Phase 0 — data model.** `RenderSegment.cs` (RenderSegment + Treatment/SegmentKind/SeamKind/
  Coverage enums + SegmentPayload), `GhostRenderChain.cs` (per-member/instance ordered view, O(log n)
  locate, tri-state `ClassifyCoverage`), `GhostSample.cs`, `GhostRenderIntent.cs`. Frame is a
  body-name string (per the §15.3 probe; see below). World-space icon/line geometry is deliberately
  NOT in these pure types — the scene/treatment resolves it.
- **Phase 1 — `ChainAssembler.cs`.** Builds the chain: treatment assignment reusing
  `GhostTrajectoryPolylineRenderer.ComputeOrbitalCoverIntervals`/`IsOrbitSegmentBelowSurface`;
  intra-arc body-split; seam classification (body change → FlexibleSoi, else Rigid); §13 Verbose
  log. Injected `BodySurfaceProvider` is the only KSP-coupled input (null in tests).
- **Phase 2 — `ChainSampler.cs`.** `Sample(chain, liveUT, units)` delegates the live→assembled-UT
  mapping to the proven pure `GhostPlaybackLogic.ResolveTrackingStationSampleUT` (reuses the span
  clock + loiter-cut decompression + relaunch schedule), then routes coverage → GhostSample.
- **Phase 3 — `GhostRenderDirector.cs`.** Pure `Decide(sample, priorIntent, label)`: one treatment,
  gap-hold, hidden-outside. Make-before-break EXECUTION + the §15.1 settle are left to the scene.

Tests in `Source/Parsek.Tests/MapRender/`: `GhostRenderChainTests`, `ChainSamplerTests`,
`GhostRenderDirectorTests`, `ChainAssemblerTests`, `ChainAssemblerLoggingTests` (the §13 assembly
log-assertion test, added at build time).

## Phase 6 — `GhostRenderReconciler` (DONE — built + tested)

`MapRender/GhostRenderReconciler.cs` (namespace `Parsek.MapRender`). Reuses `MapRenderTrace` as the
emit sink + frame-stamped intent store (new `MapRenderTrace.RecordRenderIntent` /
`TryGetFreshRenderIntent`, primitive-only so the tracer keeps no MapRender dependency). Pure compare
predicates: `ReconcileVisibility` (the `gap-vs-retire` class), `ReconcileTreatment`
(`decision-vs-old-truth`), `IsPolylineOriginShiftJump` (`polyline-origin-shift`, §10.15 — predicate
only; probe wiring lands with Phase 4 since it needs the polyline world position). `NoteIntent`
(shadow producer) + `CheckIntentAgainstOldTruth` (wired into `MapRenderProbe.Sample`, dormant until
Phase 4 calls `NoteIntent`). Tests: `Source/Parsek.Tests/MapRender/GhostRenderReconcilerTests.cs`
(pure compares + the reconciler-anomaly log-assertion lines).

### Build + test verification (2026-06-02)
- `cd Source/Parsek && dotnet build` → clean (0 warnings, 0 errors). The blind-written Phase 0–3
  code compiled as-is; the only fixes were in the TEST project: (a) `Coverage` is `internal` so a
  public `[Theory]` could not take it as a parameter (CS0051) — pass its underlying int; (b) a
  doc-comment in `GhostRenderChain.cs` contained the literal `RecordingStore.CommittedRecordings`,
  which the ERS/ELS grep-audit flagged — reworded (the type does not read that collection).
- `cd Source/Parsek.Tests && dotnet test` → full suite green (13248), incl. the 32 pipeline tests +
  14 reconciler tests.

## Probe findings

- **§15.3 transfer-phase debris = NO (code-level definitive).** A part-joint split needs a
  loaded-physics parent (`BackgroundRecorder.cs:512`); a heliocentric coast is packed/on-rails, and
  `HasHeliocentricLegInWindow` already declines such a child to faithful. → v1 uses a body-only
  frame string; the future `body | parent-generated-conic` widening point is TODO'd at the
  `RenderSegment` frame field. (One belt-and-suspenders in-game confirmation remains but does not
  change the data shape.)
- **§15.1 (proto re-seed latency) and §15.2 (per-scene patched-conic divergence) = DEFERRED /
  NEEDS-IN-GAME.** These are about KSP `OrbitRenderer`/`PatchedConics` behavior, not our code — not
  attempted blind, per the maintainer's steer. They gate Phase 3's swap-settle (modeled as a
  parameterized scene hook, not guessed) and Phase 7b.

## Not started — needs build + in-game (do NOT write blind)

- **Phase 4–5** scene adapter `IGhostMapScene` + `MapViewScene` + the two treatments
  (`StockConicTreatment` managed-vs-KSP, `TracedPathTreatment` owned) — heavy KSP (proto-vessel,
  OrbitRenderer, Vectrosity, camera, floating-origin). Wire in **decision-only shadow** (compare
  intent vs the OLD path's truth; write nothing to stock).
- **Phase 7** `TrackingStationScene` (7a parity / 7b new behavior, gated on §15.2).
- **Phase 8** per-surface cutover (8a–8e) — deletes the scattered gates; in-game per sub-phase.
- **Workstream B** solver extraction: B1 `ITransferSolver` (trivial wrap of `UvLambert.Solve`,
  guarded by `UvLambertTests`) — safe but unused until the synthesizer is rewired (needs build);
  B2 `IEncounterSolver` (wraps `CalculatePatch`, §15.4 test-gap decision) + B3 `TransferConic` return.
- **Workstream C** surface-track closeout: C1 single-recording ascent, C2 descent re-stitch
  (on-camera seam tolerance §15.5), C3 polish.

## Reminder: the integration contract

The new pipeline is consumed per active ghost instance, at a fixed execution order before the
stock `OrbitRenderer`:
`ChainAssembler.Build(...)` (cached per BuildSignature/reaim-window/InstanceKey) →
`ChainSampler.Sample(chain, liveUT, loopUnitSet)` → `GhostRenderDirector.Decide(sample, priorIntent, label)`
→ scene applies the intent via the active treatment → reconciler checks truth vs intent. The
loop-unit set comes from `MissionLoopUnitBuilder.Build` directly (not via `GhostPlaybackEngine.CurrentLoopUnits`).
