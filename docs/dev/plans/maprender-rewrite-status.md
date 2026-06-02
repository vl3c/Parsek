# Map/TS render rewrite - implementation status (branch `claude/maprender-rewrite`)

*Live status of the rewrite from `docs/dev/design-map-ts-render-architecture.md` +
`docs/dev/plans/map-ts-render-rewrite-phases.md`. Phases 0-3 were authored in a cloud container
with NO `dotnet` / NO KSP assemblies; they have since been BUILT + UNIT-TESTED on a KSP box
(2026-06-02) - see "Build + test verification" below. Phase 6 (reconciler) is now also built +
tested.*

*Commits are UNSIGNED (the harness SSH signer fails in a sibling worktree). Re-sign / squash at
merge if the repo needs verified commits.*

## Done (the pure pipeline - Phases 0-3, committed, UNBUILT)

`Source/Parsek/MapRender/` (namespace `Parsek.MapRender`, all `internal`):

- **Phase 0 - data model.** `RenderSegment.cs` (RenderSegment + Treatment/SegmentKind/SeamKind/
  Coverage enums + SegmentPayload), `GhostRenderChain.cs` (per-member/instance ordered view, O(log n)
  locate, tri-state `ClassifyCoverage`), `GhostSample.cs`, `GhostRenderIntent.cs`. Frame is a
  body-name string (per the §15.3 probe; see below). World-space icon/line geometry is deliberately
  NOT in these pure types - the scene/treatment resolves it.
- **Phase 1 - `ChainAssembler.cs`.** Builds the chain: treatment assignment reusing
  `GhostTrajectoryPolylineRenderer.ComputeOrbitalCoverIntervals`/`IsOrbitSegmentBelowSurface`;
  intra-arc body-split; seam classification (body change -> FlexibleSoi, else Rigid); §13 Verbose
  log. Injected `BodySurfaceProvider` is the only KSP-coupled input (null in tests).
- **Phase 2 - `ChainSampler.cs`.** `Sample(chain, liveUT, units)` delegates the live->assembled-UT
  mapping to the proven pure `GhostPlaybackLogic.ResolveTrackingStationSampleUT` (reuses the span
  clock + loiter-cut decompression + relaunch schedule), then routes coverage -> GhostSample.
- **Phase 3 - `GhostRenderDirector.cs`.** Pure `Decide(sample, priorIntent, label)`: one treatment,
  gap-hold, hidden-outside. Make-before-break EXECUTION + the §15.1 settle are left to the scene.

Tests in `Source/Parsek.Tests/MapRender/`: `GhostRenderChainTests`, `ChainSamplerTests`,
`GhostRenderDirectorTests`, `ChainAssemblerTests`, `ChainAssemblerLoggingTests` (the §13 assembly
log-assertion test, added at build time).

## Phase 6 - `GhostRenderReconciler` (DONE - built + tested)

`MapRender/GhostRenderReconciler.cs` (namespace `Parsek.MapRender`). Reuses `MapRenderTrace` as the
emit sink + frame-stamped intent store (new `MapRenderTrace.RecordRenderIntent` /
`TryGetFreshRenderIntent`, primitive-only so the tracer keeps no MapRender dependency). Pure compare
predicates: `ReconcileVisibility` (the `gap-vs-retire` class), `ReconcileTreatment`
(`decision-vs-old-truth`), `IsPolylineOriginShiftJump` (`polyline-origin-shift`, §10.15 - predicate
only; probe wiring lands with Phase 4 since it needs the polyline world position). `NoteIntent`
(shadow producer) + `CheckIntentAgainstOldTruth` (wired into `MapRenderProbe.Sample`, dormant until
Phase 4 calls `NoteIntent`). Tests: `Source/Parsek.Tests/MapRender/GhostRenderReconcilerTests.cs`
(pure compares + the reconciler-anomaly log-assertion lines).

### Build + test verification (2026-06-02)
- `cd Source/Parsek && dotnet build` -> clean (0 warnings, 0 errors). The blind-written Phase 0-3
  code compiled as-is; the only fixes were in the TEST project: (a) `Coverage` is `internal` so a
  public `[Theory]` could not take it as a parameter (CS0051) - pass its underlying int; (b) a
  doc-comment in `GhostRenderChain.cs` contained the literal `RecordingStore.CommittedRecordings`,
  which the ERS/ELS grep-audit flagged - reworded (the type does not read that collection).
- `cd Source/Parsek.Tests && dotnet test` -> full suite green (13248), incl. the 32 pipeline tests +
  14 reconciler tests.

## Probe findings

- **§15.3 transfer-phase debris = NO (code-level definitive).** A part-joint split needs a
  loaded-physics parent (`BackgroundRecorder.cs:512`); a heliocentric coast is packed/on-rails, and
  `HasHeliocentricLegInWindow` already declines such a child to faithful. -> v1 uses a body-only
  frame string; the future `body | parent-generated-conic` widening point is TODO'd at the
  `RenderSegment` frame field. (One belt-and-suspenders in-game confirmation remains but does not
  change the data shape.)
- **§15.1 (proto re-seed latency) and §15.2 (per-scene patched-conic divergence) = DEFERRED /
  NEEDS-IN-GAME.** These are about KSP `OrbitRenderer`/`PatchedConics` behavior, not our code - not
  attempted blind, per the maintainer's steer. They gate Phase 3's swap-settle (modeled as a
  parameterized scene hook, not guessed) and Phase 7b.

## Workstream B1 - `ITransferSolver` (DONE - built + tested)

`Reaim/ITransferSolver.cs`: the replaceable Lambert boundary + `UvLambertTransferSolver` (verbatim,
behaviour-identical delegation to `UvLambert.Solve`). `ReaimTransferSynthesizer.TransferSolver` is the
injectable seam (defaults to the delegation), routing its single solve call through the interface.
Tests: `TransferSolverInterfaceTests`. Behaviour unchanged; guarded by `UvLambertTests` + the canaries.

## Phase 4 - scene adapter + decision-only shadow CORE (DONE - built + tested; in-game signal now LIVE)

The decision-only shadow is wired and produces the reconciler signal in flight. Files:
- `MapRender/IGhostMapScene.cs` - the scene adapter (design §6.6). Phase-4 (shadow) scope only:
  `IsActive`, `CurrentUT`, `LoopUnits`, `GhostPids`, `TryResolveGhost`, `BodySurface`. The DRAW-side
  members (projection, proto lifecycle pass-through, floating-origin frame, camera-focus continuity)
  extend it in Phase 5, when the treatments actually draw - declaring them now, unused, would be
  speculative.
- `MapRender/MapViewScene.cs` - flight impl; thin pass-through to `GhostMapPresence`
  (`TryGetCommittedTrajectoryForPid`, a new ERS-exempt physical-correlation helper) + `FlightGlobals`
  body radii. Consumes `cachedLoopUnits` (the `MissionLoopUnitBuilder.Build` output) via `SetFrameInputs`.
- `MapRender/ShadowRenderDriver.cs` - per ghost: assemble -> sample -> decide -> `NoteIntent`; writes
  NOTHING to stock. Emits the §13 locate/intent + frame-summary lines (tag `MapRender`). **Scope (MVP):
  faithful single-instance only** - re-aim members (raw recording lacks the synthesized transfer) and
  overlap members (per-instance phasing not modelled by a single pid->recording resolve) are SKIPPED
  with a logged reason, so the reconciler signal stays clean; they land with the re-aim / overlap
  wiring in a later phase. Pure helpers `ClassifyScope` + `DecideForGhost` are unit-tested.
- `ParsekFlight` hook: after `CheckPendingMapVessels`, gated on `MapRenderTrace.IsEnabled`, wrapped in
  try/catch (diagnostic-only; must never break the live flight update).
Tests: `Source/Parsek.Tests/MapRender/ShadowRenderDriverTests.cs` (11). Full suite green (13262).

**In-game now:** with `mapRenderTracing` on, the log carries `[MapRender] shadow ...` locate/intent
lines and the Phase-6 reconciler emits `[MapRenderTrace] ... reason=decision-vs-old-truth` /
`reason=gap-vs-retire` anomalies whenever the new Director's decision for a faithful ghost diverges
from what the old path drew. That is the parity signal to validate the rewrite against.

**Deferred from Phase 4 to Phase 5** (draw-side, only exercised once treatments draw): proto-vessel
lifecycle pass-through behind the adapter, the shared per-frame floating-origin frame, and
camera-focus continuity across a swap. Also still scoped out of the shadow: re-aim + overlap members.

### PRIMARY regression target for Phase 5 / cutover - the `icon-off-orbit` bug (read before StockConicTreatment)

The concrete defect the whole rewrite exists to kill: on the looped re-aim "Duna One" mission (`s15`)
the ghost ICONS sit ~96.5 deg around Kerbin from their correctly-drawn orbit LINES, a pure rotation
(`iconR == orbitEffR`), equal to Kerbin's rotation over the huge loop shift. Root cause: TWO
mechanisms drive one ghost (line at the raw recorded inertial epoch via
`GhostMapPresence.ApplyOrbitToVessel` `SetOrbit(..., segment.epoch, ...)`; icon at `effUT` via
`Patches/GhostOrbitLinePatch.cs` `GhostOrbitIconDrivePatch`; gap-glide from body-fixed surface points
via `OrbitReseed.TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch`). Introduced by `9966ace`
(PR #1003). The buggy ghosts are "Kerbal X" (hyperbolic Kerbin escape) + "Kerbal X Probe" (elliptical
Kerbin orbit) - both KERBIN-relative, i.e. exactly the faithful departure members the **per-member
shadow fix** (commit `52728631`) now shadows. So the shadow already covers them; the cutover must fix
them.

- **Metric (use it):** `MapRenderProbe`'s gated Tier-C `icon-off-orbit` anomaly logs
  `angleIconVsOrbitEff` (pure `MapRenderTrace.IsIconOffOrbit`, threshold 1 deg). It is ALREADY on this
  branch (PR #1014, merged from main). **Success = `angleIconVsOrbitEff` goes to ~0 for both ghosts on
  the looped re-aim mission with tracing on.** Make it an explicit in-game assertion (design §14, now
  recorded there).
- **The fix is `StockConicTreatment` (Phase 5 / 8a):** icon and line come from ONE source - the SAME
  `OrbitSegment` driven at `DriveUT`, in the LIVE body frame, with the whole ghost's frame unified at
  once (line + icon + gap-glide + mesh + sibling stages), NEVER per-element. Then the angle is 0 by
  construction (design §6.5 invariant 2). Decision-only shadow does NOT fix it (it writes nothing), so
  until the 8a cutover the metric stays ~96.5 deg in-game: expected, not a new regression.
- **Dead-ends - do NOT repeat:** per-element LAN rotation (`3136477`, reverted `2cbaec4`),
  gap-glide-only inertial reseed (Fix A, PR #1012 - gap is ~2 frames, loiter is the bulk), re-aim
  relaunch alignment to whole body rotations (Fix B - conflicts with the transfer window). Full
  writeup: looped-re-aim entry in `docs/dev/todo-and-known-bugs.md`.

## The in-game wall - what remains, and why it cannot be written blind

Everything below is heavily KSP-coupled (proto-vessel / OrbitRenderer / Vectrosity / camera /
floating-origin / `PatchedConics`) and the maintainer's standing steer is **do NOT write blind**. The
shadow (Phase 4 core) writes nothing, so it was safe to land; the phases below DRAW, so they need
**live KSP** validation. Turn on `mapRenderTracing`, run the §14 verification matrix, watch the
reconciler for decision-vs-old-truth parity, and resolve the in-game probes before the phase each gates.

- **Phase 5 - treatment CLASSES built (callable, wired at cutover).** `MapRender/IGhostRenderTreatment.cs`
  + `StockConicTreatment.cs` + `TracedPathTreatment.cs`. `StockConicTreatment.SeedAndDrive` is the
  one-source fix core: `orbit.SetOrbit(<segment elements>)` then `orbit.UpdateFromUT(driveUT)`, so the
  line (stock-drawn from the elements) and the icon (the same orbit at driveUT) cannot disagree -
  design §6.5 invariant 2. `IGhostMapScene` gained the draw-side `TryGetGhostOrbit` / `ResolveBody`.
  Pure `ShouldApply` predicates are unit-tested (`TreatmentTests`, 4). Per the plan the StockConic
  surface is NOT shadow-drawn (the old patch co-owns the stock object); it is exercised at the 8a flip.
  TracedPath is a follower shell until 8b (the autonomous polyline still draws). STILL DEFERRED in
  Phase 5: the floating-origin frame + camera-focus draw-side, the §13 seam / moon-config logs, and
  resolving §15.1 (proto re-seed latency) in-game before the swap execution.
- **Phase 8a (next) wires it:** swap `GhostOrbitLinePatch`'s decision/drive to the Director +
  `StockConicTreatment` behind a runtime gate, at a fixed exec order before the stock `OrbitRenderer`,
  and validate `angleIconVsOrbitEff -> ~0` in-game.
- **Phase 7** `TrackingStationScene` (7a parity / 7b new behavior, gated on §15.2 - possible stop-point).
- **Phase 8** per-surface cutover (8a-8e) - deletes the scattered gates; in-game per sub-phase.
- **Workstream B** B2 `IEncounterSolver` (wraps `CalculatePatch`, §15.4 test-gap decision) + B3
  `TransferConic` frame-agnostic return - touch the in-game-validated re-aim path.
- **Workstream C** surface-track closeout: C1 single-recording ascent, C2 descent re-stitch
  (on-camera seam tolerance §15.5), C3 polish.

## Reminder: the integration contract

The new pipeline is consumed per active ghost instance, at a fixed execution order before the
stock `OrbitRenderer`:
`ChainAssembler.Build(...)` (cached per BuildSignature/reaim-window/InstanceKey) ->
`ChainSampler.Sample(chain, liveUT, loopUnitSet)` -> `GhostRenderDirector.Decide(sample, priorIntent, label)`
-> scene applies the intent via the active treatment -> reconciler checks truth vs intent. The
loop-unit set comes from `MissionLoopUnitBuilder.Build` directly (not via `GhostPlaybackEngine.CurrentLoopUnits`).
