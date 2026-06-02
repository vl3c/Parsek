# Map/TS render rewrite — implementation status (branch `claude/maprender-rewrite`)

*Live status of the rewrite from `docs/dev/design-map-ts-render-architecture.md` +
`docs/dev/plans/map-ts-render-rewrite-phases.md`. Worked in a cloud container that has NO
`dotnet` and NO KSP assemblies, so nothing below was compiled or tested — all code is written
against verified type contracts and must be built + tested first thing on a KSP box.*

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
`GhostRenderDirectorTests`, `ChainAssemblerTests`. (No log-assertion test yet — needs the sink +
verbose enabled; add when building.)

### FIRST when home
1. `cd Source/Parsek && dotnet build`; fix any compile errors (written blind — most likely culprits:
   nested-type qualification `GhostPlaybackLogic.LoopUnitSet` / `GhostTrajectoryPolylineRenderer.BodySurfaceProvider`,
   `Array.Empty`/tuple/`out _` usage, the `Vector3d` global type).
2. `cd Source/Parsek.Tests && dotnet test` (the 4 new MapRender test classes are pure — should run
   without KSP). Confirm the locate/coverage/sampler/director/assembler logic.

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
- **Phase 6** reconciler — extend `MapRenderTrace`/`MapRenderProbe` against the `GhostRenderIntent`.
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
