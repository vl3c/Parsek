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
- **CONFIRMED root cause (2026-06-02) + the fix:** the `icon-off-orbit` metric (`lonIcon ~= lonOrbitLive`,
  `iconR == orbitEffR`, `angleIconVsOrbitEff ~= angleEffVsLive`) + the `Vessel.GetWorldPos3D` /
  `VesselPrecalculate` decompile proved the icon resolves at the orbit's LIVE phase, not effUT: KSP
  rebuilds a packed ghost's `CoMD = body.position + orbitDriver.pos` by re-propagating the orbit at the
  LIVE clock every FixedUpdate, so the legacy `UpdateFromUT(effUT)` drive is overwritten and the icon
  always sits at `orbit(live)`. The fix is `StockConicTreatment.SeedAndDriveLive` (Phase 8a): bake the
  loop shift into the orbit EPOCH and propagate at LIVE, so live-clock resolution lands on the recorded
  phase - the same phase the effUT-drive intended, moving PHASE only (LAN/inc/argPe untouched, so NOT the
  reverted per-element rotation), re-seeded every frame (no stall). `GhostOrbitArcPatch` clips at LIVE
  bounds when the epoch is baked; the probe measures against the live-clock orbit so `angleIconVsOrbitEff
  -> ~0` is the success signal. Decision-only shadow does NOT fix it (it writes nothing); the gated 8a
  drive does. Until validated in-game, the metric stays ~96.5 deg with the gate OFF: expected.
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
- **Phase 8a - VALIDATED IN-GAME 2026-06-05, DEFAULT-ON.** Setting
  `ParsekSettings.mapRenderDirectorDrive` (Settings, default TRUE as of 2026-06-05; toggle off restores legacy). When on, the new
  pipeline OWNS the StockConic icon: `GhostOrbitIconDrivePatch` calls
  `StockConicTreatment.SeedAndDriveLive(orbit, seg, body, shift, liveDriveUT)` - it bakes the loop shift
  into the orbit EPOCH and propagates at the LIVE clock, the only clock KSP actually resolves a packed
  ghost's icon at (`CoMD` is rebuilt from a live re-propagation every FixedUpdate, so the legacy effUT
  drive never reached the icon - the confirmed root cause). `GhostOrbitArcPatch` clips the arc at the
  LIVE bounds when the epoch is baked (signalled per-pid per-frame via
  `GhostMapPresence.IsDirectorEpochBaked`), and `MapRenderProbe` compares the icon against the live-clock
  orbit, so the metric is truthful. The earlier 8a `SeedAndDrive(raw epoch, effUT)` re-assert was proven
  a NO-OP (the elements were already correct; the icon was off because of the CLOCK, not the elements) -
  superseded. The shadow stores the per-pid seed (`ShadowRenderDriver` `seedByPid` +
  `TryGetFreshStockConicSeed`, +/-2 frame freshness); `ShadowRenderDriver.Enabled` makes the shadow run
  when tracing OR this gate is on. Toggle off restores the legacy effUT drive; the legacy drive also still
  runs per-frame wherever there is no fresh seed. **VALIDATED 2026-06-05** (s15 "Duna One",
  `mapRenderDirectorDrive` + `mapRenderTracing` on): `angleIconVsOrbitEff` collapses to ~1-3 deg (min 1.02)
  on director-driven frames vs the ~96.5 deg baseline, icon visually on its line. COVERAGE GAP (cutover
  Step 2, the prerequisite to 8e legacy deletion): 83/242 sampled director decisions in the validation log
  fell back to legacy (no fresh seed), clustering the residual >45 deg anomalies on the deliberately-skipped
  re-aim/overlap members + the hyperbolic-escape ecc>=1 segment; default-on stays safe because those frames
  are byte-identical to the legacy path. KNOWN LIMITS: only same-body StockConic ghosts are seeded
  (re-aim/overlap skipped); a fresh seed whose body name does not resolve (degenerate, never for a real
  recording) falls to the legacy path - the icon-drive + arc-clip + probe share ONE predicate
  (`ShadowRenderDriver.IsDirectorDriveActive` = gate AND fresh seed AND body resolves) so they never split
  on it; toggling the gate OFF mid-flight may briefly (~0.5s, until the next dispatcher reseed re-snaps the
  raw epoch) show a stale orbit (self-healing, manual-toggle-only). The default-on flip (8a finalization)
  landed on `maprender-cutover` after this in-game validation; the legacy path DELETION stays gated on
  closing the coverage gap above (cutover Step 2) and is the separate Phase 8e.
  In-game test: `DirectorDriveEpochBakePlacesIconOnRecordedPhase` (RuntimeTests, GhostMap, FLIGHT).
  - **Coverage-gap Category 1 (hyperbolic orbit LINE clip) - DONE.** `GhostOrbitArcPatch.Prefix`
    previously bailed to stock for `ecc>=1`, so a hyperbolic escape/flyby segment drew the FULL open
    asymptote-to-asymptote hyperbola instead of being clipped to its `[startUT,endUT]` window (the icon
    already rode the hyperbola correctly). The patch now clips the open arc via the same
    eccentric-anomaly path as the ellipse (verified hyperbolic-safe against the stock Orbit API:
    `EccentricAnomalyAtUT` -> `solveEccentricAnomalyHyp`, `GetTrueAnomaly` sinh/cosh branch,
    `getPositionFromEccAnomalyWithSemiMinorAxis` ecc>1 branch), gating off the three periodicity-only
    steps (full-period early-return, periapsis wraparound, elliptical radius diagnostic) and keeping the
    same director-drive-vs-effUT frame selection so the clipped line and the icon stay in lockstep.
    Pure math split into `ArcAnomalyMath` (unit-tested, `ArcAnomalyMathTests`); in-game test
    `HyperbolicArcClipBoundsLineToSegmentWindow` (RuntimeTests, GhostMap, FLIGHT).
- **Phase 7a - DONE (TS shadow parity).** `MapRender/TrackingStationScene.cs` (scene gate = TRACKSTATION)
  over the new shared `GhostMapSceneBase` (the scene-agnostic resolve/body/orbit plumbing extracted from
  `MapViewScene`, which is now just the FLIGHT gate). `ParsekTrackingStation.Update` runs
  `ShadowRenderDriver.RunFrame` over the TS map ghosts (gated + try/catch), so the reconciler now reports
  decision-vs-old-truth in BOTH scenes. **Phase 7b (next, NEEDS IN-GAME)**: make-before-break +
  cold-start-mid-segment (§10.19) + per-scene patched-conic divergence (§10.20), gated on the §15.2 probe
  (possible stop-point - if stock re-solves the patch chain materially differently per scene, escalate).
- **Phase 8b - TracedPath polyline ownership (in progress).**
  - **8b.0 - DONE.** Extracted the per-leg polyline draw into the scene-agnostic
    `GhostTrajectoryPolylineRenderer.TryDrawLeg` (verbatim old inlined body, byte-identical output) so
    the treatment can draw a single leg through the SAME mechanics.
  - **8b.1 - DONE.** When `mapRenderDirectorDrive` is on and the Director decides Visible+TracedPath for
    a ghost pid, the Driver routes that leg's draw through `TracedPathTreatment.TryDrawOwnedLeg` (the same
    `TryDrawLeg`) and stands down on its own direct call (single `if/else` on
    `ShadowRenderDriver.IsDirectorTracedPathActive(pid, frame)`), so the leg is never drawn twice and the
    stock proto is suppressed exactly when the treatment draws. The ownership signal stayed driven by the
    actual draw (`anyDrawn -> activeLegRecordings`, published on either path).
  - **8b.2 - DONE (this branch).** Made the Director/treatment the AUTHORITATIVE source of the "polyline
    owns this phase" ownership signal. **Fork resolved -> Option B (treatment publishes the ACTUAL draw),
    NOT Option A (repoint to the raw decision).** Option A was ruled UNSAFE: the Director's TracedPath
    decision is built from `traj.Points` (via `ChainAssembler.AppendTracedRuns`), while the polyline draw
    is built from `TrackSections.bodyFixedFrames`/`frames` + flat `Points`-outside-sections (via
    `BuildLegsForRecording`) and gated per-leg on the head UT + `m>=2` - two independent data pipelines
    that demonstrably read different source collections, so a decision-vs-draw mismatch (Director decides
    TracedPath but no drawable leg this frame -> proto hidden with nothing drawn, an invisible gap) cannot
    be ruled out. Option B preserves "proto hidden IFF a leg actually drew" for the
    `IsRenderingNonOrbitalLeg` / `IsPolylineOwningGhostPhase` signal this phase owns. (A SEPARATE,
    decision-based proto-line suppression in `GhostOrbitLinePatch.cs:130/552` (on
    `IsDirectorTracedPathActive`) can still set `line.active=false` on a decision-without-draw frame,
    but it also sets `ghostsWithSuppressedIcon`, which draws the non-proto marker, so the ghost is
    never left blank; retiring that line-level path is deferred, out of 8b.2 scope.) Mechanism: a new
    `directorOwnedLegRecordings` set is published by the OWNED draw path ONLY when `TryDrawOwnedLeg`
    actually returns drawn=true; `IsRenderingNonOrbitalLeg` dispatches (pure `ResolveNonOrbitalLegOwnership`)
    - gate ON -> the UNION of the director-owned set and the legacy `activeLegRecordings` (the legacy set
    still covers pid-0 / re-aim / overlap ghosts the shadow does not own, which still take the Driver-direct
    path under the gate); gate OFF -> the legacy set ONLY (byte-identical to pre-8b.2). The Driver's
    autonomous-walk publish is retired as the AUTHORITATIVE source for owned legs but kept as the gate-off
    fallback; its deletion is 8e. Tests: pure dispatch + seam-driven end-to-end gate read in
    `GhostTrajectoryPolylineBuildTests`; in-game `OwnershipSignal_DispatchesOnLiveGate_NoNewGap`
    (RuntimeTests, GhostMap, TRACKSTATION) covers the live-gate read + no-new-gap. **In-game gate to run:**
    s15 "Duna One" looped re-aim + a non-looped Mun mission through landing with `mapRenderDirectorDrive` on,
    watching the TracedPath<->StockConic seam: no orbit-line blink, no icon teleport, and crucially NO
    invisible gap (proto must never be hidden with nothing drawn).
- **Phase 8c - Marker / proto-icon-suppression decision (DONE - this branch, NEEDS IN-GAME).** Made the
  Director/treatment the AUTHORITATIVE source of the MARKER-draw + proto-ICON-suppression decision for the
  two marker call sites, behind the gate; kept the legacy `ghostsWithSuppressedIcon` / `IsIconSuppressed` /
  `ClassifyAtmosphericMarkerSkip` as the gate-OFF fallback (NOT deleted - that is 8e). **Fork resolved:**
  both `ParsekUI.DrawMapMarkers` and `ParsekTrackingStation.ClassifyAtmosphericMarkerSkip` previously
  decided "draw our non-proto marker" from `IsIconSuppressed(pid) || IsPolylineOwningGhostPhase(pid)`. The
  `IsPolylineOwningGhostPhase` half is ALREADY Director-sourced (8b.2 actual-draw set). The `IsIconSuppressed`
  half is written in two distinct contexts: **(a)** the Director TracedPath DECISION suppress
  (`GhostOrbitLinePatch.cs:132/556` on `IsDirectorTracedPathActive`) and **(b)** the no-bounds legacy
  transient (`:172/882` on `IsDirectorTracking`) plus the legacy below-atmosphere / off-arc clamp. 8c repoints
  the marker decision for context (a) onto the Director-sourced `IsDirectorTracedPathActive` directly, and
  KEEPS the legacy `IsIconSuppressed` disjunct as the fallback for context (b) + below-atmosphere + off-arc
  (none of which the Director owns yet). Mechanism: a single pure `GhostMapPresence.ResolveMarkerDrawDecision`
  (gate ON -> `directorTracedPathActive || polylineOwning || iconSuppressedLegacy`; gate OFF ->
  `iconSuppressedLegacy || polylineOwning`, byte-identical) + the Unity wrapper
  `ShouldDrawNonProtoMarkerForGhost` both call sites route through. **No marker gap:** the gate-ON decision
  is a SUPERSET of the legacy decision (adds only the `directorTracedPathActive` disjunct), so it is never
  FALSE on a frame the proto is hidden; whenever `IsDirectorTracedPathActive` is true the line Postfix's
  first branch (`:552`) has set `drawIcons=NONE` + added to `ghostsWithSuppressedIcon`, so the proto icon is
  not co-drawn (no double marker). **Marker rides the line** unchanged: `TryAnchorMarkerToPolyline` still
  anchors the marker to the drawn polyline when a leg drew this frame, falling back to the trajectory head
  otherwise (visible). The decision-based proto-LINE suppression in `GhostOrbitLinePatch.cs:130/552` is the
  line-level hide MECHANISM and is untouched (its retirement is 8e). Tests: pure `ResolveMarkerDrawDecision`
  (`MarkerDrawDecisionTests`, 6, incl. the exhaustive superset/no-gap proof); in-game
  `MarkerDrawDecision_DispatchesOnLiveGate_NoGap` (RuntimeTests, GhostMap, TRACKSTATION) covers the live-gate
  dispatch + fallback + no-double-marker. Build clean; full suite green (13408); GrepAudit OK. **In-game gate
  to run:** s15 "Duna One" looped re-aim + a non-looped Mun mission through landing with
  `mapRenderDirectorDrive` on: marker present and riding the line on owned legs, no double marker, no blank
  frame at the TracedPath<->StockConic seam; toggle the gate OFF -> byte-identical to the legacy marker paths.
- **Phase 8d - map-presence migration into the scene adapter (in progress).** Full sub-plan:
  `docs/dev/plans/maprender-8d-presence-extraction.md`. Migrates the last big autonomous surface, the
  ghost MAP-PRESENCE lifecycle (`ParsekPlaybackPolicy.CheckPendingMapVessels`, ~660 lines + 6 presence
  dictionaries), into `GhostMapPresence` behind the same gate so 8e can delete the legacy path. **HARD
  CONSTRAINT:** presence must NOT gate on `IsDirectorDriveActive` - the Director deliberately skips
  re-aim / overlap members, which still need presence created + torn down every frame; the gate selects
  only WHERE the identical work runs, never WHETHER it runs.
  - **8d.0 - DONE (no behavior change).** Routed the flight per-frame presence tick through the scene
    adapter instead of the direct policy call. New `IGhostMapScene.DriveMapPresence(double currentUT)`,
    `abstract` on `GhostMapSceneBase`; `MapViewScene` overrides it as `policy?.CheckPendingMapVessels(
    currentUT)` with the policy injected once at init via `SetPresenceDriver(policy)` (same style as
    `SetFrameInputs`); `TrackingStationScene` overrides it for compile symmetry (delegating to
    `UpdateTrackingStationGhostLifecycle(LoopUnits)`), NOT yet routed from any TS caller. `ParsekFlight`
    now calls `mapViewScene.DriveMapPresence(Planetarium.GetUniversalTime())` in the SAME per-frame slot
    the direct call used (between `RetryHeldGhostSpawns()` and the shadow-driver block). Byte-identical:
    same method, same argument, same slot; `policy?.` cannot diverge from the old unconditional `policy.`
    because `SetPresenceDriver` runs unconditionally at init before any frame; no director gate added.
    The `CheckPendingMapVessels` body + the 6 dictionaries are UNTOUCHED (that is 8d.1). Source-gate test
    `MapPresenceSeamTests` (4) locks the host off the direct call. Build clean; full suite green (13412).
  - **8d.1 (next, single PR) - relocate the body (PURE MOVE, no gate).** Move `CheckPendingMapVessels`
    + the 6 dictionaries (+ `terminalMapRetentionLoggedIds` + `nextMapOrbitUpdateTime` + the
    `PendingMapVessel` struct) into `GhostMapPresence.UpdateFlightMapGhostLifecycle(currentUT,
    loopUnits)`; the seam ALWAYS calls it, `CheckPendingMapVessels` deleted from the policy. **Decision
    (2026-06-05):** the body is a thin orchestrator over `GhostMapPresence.*` statics (every proto/KSP
    mutation already routes through them; only engine read is `CurrentLoopUnits`; the 6 dicts are touched
    ONLY in `ParsekPlaybackPolicy.cs`), so the relocation is a FAITHFUL COPY with no logic change; a gate
    would toggle two identical paths and duplicate ~655 lines, so 8d.1 is a no-behavior-change move (like
    8d.0 / 8b.0). `loopUnits` MUST be `engine.CurrentLoopUnits` (NOT the scene's cached `LoopUnits`,
    a different source). The 6 dicts become `internal static` on `GhostMapPresence` (the still-in-policy
    enqueue tail + teardowns reach them directly until 8d.2 moves them). Riskiest slice (655-line cut),
    so it ships on a HARD clean-context review + green tests; maintainer playtests the merged build
    (Duna One looped re-aim + a non-looped Mun mission through landing) as the standard post-merge check.
  - **8d.2 - DONE (PURE MOVE, no gate).** Extracted the PRESENCE portions of the two mixed-concern
    engine-event handlers out of the policy into `GhostMapPresence`: `HandleGhostCreated`'s enqueue ->
    `GhostMapPresence.HandleFlightGhostCreatedMapPresence(evt, loopUnits)` (the policy handler keeps the
    camera `TryAutoFollowChainSeamSpawn`), `HandleGhostDestroyed`'s teardown ->
    `GhostMapPresence.HandleFlightGhostDestroyedMapPresence(index)` (the policy handler keeps the log +
    `heldGhosts.Remove`). The policy stays the engine-event subscriber (subscription wiring + non-presence
    concerns stay); `HandleAllGhostsDestroying` / `Dispose` already delegated via
    `ClearFlightMapPresenceState()` (8d.1). `ShouldDeferLoopShiftedMapPresence` stays in the policy
    (`RuntimePolicyTests` callers), called cross-class. Faithful copy (empty code-only diff on both moved
    blocks), no behavior change. Source-gate tests added; build clean; full suite green (13418).
  - **8d.3 - DONE (behavior-preserving).** Decomposed `UpdateFlightMapGhostLifecycle` into three named
    `private static void` pass methods (`RunFlightMapDeferredCreatePass`, `RunFlightMapOrbitReseedPass`,
    `RunFlightMapStateVectorUpdatePass`) + an orchestrator that keeps the reseed gate + both early-returns
    + preamble INLINE (so they still skip Pass 2+3). Each pass body line-for-line identical to HEAD. Most
    predicates were already extracted, so the new pure surface was small: two trivial predicates extracted
    + unit-tested (`IsMapCreateAcceptedSource`, `IsSegmentBearingGhostSource`), correct sites verified. No
    behavior change. Build clean; full suite green (13431). **This completes the 8d presence migration**:
    the flight map-presence lifecycle now lives entirely in `GhostMapPresence`, policy is the thin
    engine-event subscriber.
- **Phase 8d - all sub-slices DONE (8d.0-8d.3).** The ghost map-presence lifecycle (seam, per-frame body
  + 6 dicts, lifecycle handlers, decomposition) is fully migrated from `ParsekPlaybackPolicy` into
  `GhostMapPresence`, no behavior change. Remaining cutover work is Phase 8e (delete the 8a/8b/8c legacy
  draw-side fallbacks + the autonomous Driver walk + grace fields, then drop the `mapRenderDirectorDrive`
  gate). NOTE: 8e legacy DELETION is still gated on closing the coverage gap (cutover Step 2: the
  re-aim / overlap members + the hyperbolic-escape segment that fall back to the legacy draw path); that
  needs its own scoping before deletion. Presence (8d) is independent of that gap (presence was never
  gated and always runs).
- **Cutover completion = INTEGRATION before deletion (decided 2026-06-06).** The 8e read-only scoping
  overturned the "delete legacy + drop the gate" premise: the legacy draw code (autonomous Driver walk,
  legacy effUT icon drive, `activeLegRecordings`, `ghostsWithSuppressedIcon`, grace fields) is NOT a
  gate-off-only fallback - it is the gate-ON DEFAULT path today for the ~34% of decisions (83/242)
  covering re-aim members, overlap members, and unseeded ghosts the Director architecturally cannot yet
  own; the autonomous Driver is also the single structural polyline draw host. So the design-doc end
  state (a single modular system) is reached by INTEGRATING those in-use legacy responsibilities INTO the
  Director pipeline, THEN deleting the now-dead legacy + dropping the gate (deletion LAST). Three
  integration pieces: (1) re-aim rendering, (2) overlap rendering, (3) the shared polyline draw host.
  - **Integration 1 - re-aim rendering (DONE, gated, awaiting in-game gate).** The re-aim TRAJECTORY
    workstream is confirmed READY (PR #1030 shipped the destination-SOI arrival hold; the re-aimed
    transfer+arrival geometry is computed per loop, cached, and already exposed via
    `GhostMapPresence.ResolveEffectiveMapOrbitSegments` / `ReaimPlaybackResolver.Shared`, and consumed by
    the legacy renderer every frame). The Director skipped re-aim only because it read the RAW recording.
    Fix (branch `maprender-reaim-render`): `ChainAssembler.Build` gains an `orbitSegmentsOverride` (fed the
    re-aimed list, while `traj.Points` still feeds the body-relative TracedPath legs - do NOT wrap in
    `ReaimedTrajectory`, its Points are empty by design); `ShadowRenderDriver.GetOrBuildChain` resolves the
    effective segments + window via a new `ResolveEffectiveMapOrbitSegments(out windowIndex)` overload,
    passes the override, caches by `|w{windowIndex}` (synodic-window advance invalidates), and records
    `chainHasReaimedSegments = !ReferenceEquals(effective, recorded)` on the cache entry; the skip becomes
    the coverage-aware `ShouldSkipReaimSegment(intentVisible, frameBodyIsStar, memberIsReaimOwner,
    chainHasReaimedSegments, sampleInSegment)` => skip = `intentVisible && frameBodyIsStar &&
    memberIsReaimOwner && !(chainHasReaimedSegments && sampleInSegment)`. The `sampleInSegment`
    (`sample.Coverage == InSegment`) term is LOAD-BEARING (plan-review catch): without it the Director
    would drive a held stale Sun conic across a trim gap where the legacy path hides. Once re-aim produces
    a StockConic seed, the 8a epoch-bake icon drive, Cat-1 hyperbolic arc clip, and 8c marker all apply
    unchanged via `IsDirectorDriveActive`, so the icon-off-orbit fix (CHANGELOG 0.10.0) is finally TRUE
    for the re-aim heliocentric leg (it was falling back to legacy, where the icon stayed live-clock =
    the residual >45deg anomalies). Gate-OFF byte-identical (all logic inside `RunFrame`, seeds consumed
    only under the gate). Two clean reviews (plan + code) SHIP. **In-game gate:** s15 "Duna One", gate-on
    + tracing-on: re-aim ghost's icon rides its heliocentric line (`angleIconVsOrbitEff` -> ~0, was
    >45deg), `decision-vs-truth` parity, trim-gap frames stay hidden, no SOI-seam blink; toggle gate-off
    -> byte-identical.
  - **Integration 2 - overlap rendering. TWO parts: 2a FOUNDATION (DONE, PR #1051), 2b PER-INSTANCE
    (planned, multi-PR - the real goal).** Full plan for 2b: `docs/dev/plans/maprender-overlap-per-instance.md`.
    - **2a (DONE, PR #1051, validated):** removed the conservative `SkipOverlap` early-skip in
      `ShadowRenderDriver.RunFrame` so an overlap member flows through the normal assemble->sample->decide->
      seed/stamp path instead of falling back to legacy. It renders ONE ghost at the newest (selected)
      cycle. Sampler-parity CONFIRMED (clean review + in-game): `ChainSampler.Sample` maps live->assembled
      UT via the SAME `GhostPlaybackLogic.ResolveTrackingStationSampleUT` the legacy single-head uses,
      driven by `unit.CadenceSeconds` (span-raised single-instance), NOT `unit.OverlapCadenceSeconds` (the
      short relaunch cadence consumed only by the flight MESH engine), so the Director lands on the same
      selected-cycle head-UT as legacy. `ShadowScope.SkipOverlap` enum + `ClassifyScope`/
      `ClassifyOverlapForMember` retained (classifier + tests stay; production stops skipping; counter
      `skipOverlap`->`overlapShadowed`). Gate-OFF byte-identical. Build clean; suite green (13477).
      In-game validated 2026-06-06 (flight-map, save s16, "Kerbal X" self-overlap): single icon rode its
      line, 1112/1112 `drawn-non-proto`, no blink, clean teardown of 17 live overlap meshes / 8 recordings.
    - **2b - PER-INSTANCE (decided 2026-06-06; the accurate end state):** 2a's single icon MISREPRESENTS
      reality - flight shows N staggered overlap meshes, the map shows ONE. The goal (maintainer's call:
      "render an icon for every ghost, make it accurate") is ONE map icon + orbit line + polyline PER LIVE
      overlap INSTANCE, so the map matches flight. **This is a real per-instance build-out, multi-PR**
      (comparable to 8c/8d), because N icons require N ProtoVessels (the icon is the stock orbit-driver's
      icon, one per vessel) and the whole map layer (~12 keyed maps + the presence lifecycle) is
      one-per-recording and must become per-(recording, cycleIndex). The flight engine ALREADY has the
      per-instance model to MIRROR (`overlapGhosts`, `GhostPlaybackLogic.GetActiveCycles` /
      `ComputeOverlapCyclePlaybackUT`, the `(recording, cycleIndex)` identity, cap
      `MaxOverlapGhostsPerRecording=20`) - reuse it, do not reinvent. FULL per-instance (not icon-only:
      N icons on one shared line is visibly wrong for non-orbital ascent/descent instances). Stacks on 2a
      (instance 0 = newest cycle = today's single ghost). Slices: **(i) map presence N-per-overlapping-
      recording lifecycle [the bulk] - DONE (awaiting in-game gate)**, (ii) Director per-instance
      enumeration + the existing `instanceKey` wiring (caches are already pid-keyed), (iii) polyline +
      marker per-instance. Efficiency: overlap-ONLY gate so non-overlap recordings stay EXACTLY
      one-per-recording (zero new cost); reuse the engine's cycles; throttle per-instance ProtoVessel
      create/destroy (the biggest risk = warp-time cycle churn); cap at 20. Gate-OFF stays legacy
      one-per-recording.
    - **Slice (i) DONE (this branch, awaiting in-game gate):** mirrors the proven `ParsekKSC`
      per-instance overlap model (which already renders N overlap ghosts on the KSC map with NO flight
      engine). New `overlapInstanceVessels : Dictionary<(recIdx,cycle),Vessel>` + `EnsureOverlapInstances`
      / `RunOverlapPerInstanceSweep` / `CreateOverlapInstanceVessel` (its OWN per-instance create path, not
      the single-slot `CreateGhostVesselFromSource` funnel); schedule via the PURE
      `GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule` (works in flight AND TS); `GetActiveCycles` +
      `ComputeOverlapCyclePlaybackUT` for the live-cycle set + per-instance epoch shift; gate =
      `mapRenderDirectorDrive && IsOverlapLoop(interval,duration)`. Each instance's icon phase comes from
      its per-pid `ghostOrbitEpochShift` (so slice i ships WITHOUT slice ii's instanceKey, which stays 0).
      `GetGhostVesselPidForRecording`/`HasGhostVesselForRecording` fall through to the newest-cycle
      instance so watch / TS-Fly / UI / polyline-owner readers don't get pid 0. Legacy create/reseed/
      state-vector passes skip overlap indices (sweep is sole authority). Teardown + the two leak-skip
      early-returns + all 3 reset sites extended; `RemoveOverlapInstance` cleans every per-pid map.
      State-vector overlap instances mirror the per-index state-vector contract (seed at live UT, clear
      the segment-drive dicts) - review-caught fix. Two clean reviews (plan + code) SHIP. Build clean;
      suite green (13498). **In-game gate:** an ORBITAL launch-to-LOW-orbit mission looped with period <
      length, map view, run past a relaunch: N icons on N orbit lines, one per live overlap instance,
      matching the N flight ghosts (appearing/expiring as they relaunch); gate-off / non-overlap
      byte-identical one-per-recording. (Use an ORBITAL mission: the per-instance ASCENT polyline is slice
      iii, so in slice i the ascent line stays single per recording - the icons-on-orbit-lines are the
      deliverable.)
  - **Rendering polish - polyline + marker pan-stability (DONE, gated-neutral).** Fixed map polylines +
    yellow label markers jittering / flickering when panning the camera (pre-existing, NOT from the
    cutover). Root cause: the polyline draw ran at `[DefaultExecutionOrder(-50)]`, BEFORE the map camera
    commits its pan, so the Vectrosity mesh lagged one frame; and `TryAnchorMarkerToPolyline` dropped the
    marker ride on transient leg-gap / not-drawn-this-frame frames, snapping the label to the frozen
    ghost mesh. Fix: SPLIT the Driver - the decide pass + ownership publish + head-UT gating STAY at -50
    (publish now on a `WillLegDraw` predicate that exactly mirrors `TryDrawLeg`'s non-degenerate early
    returns, so will-draw == actual-draw, no decision-without-draw gap), but the point-recompute +
    `Draw3D` + deactivation sweep move to a `Camera.onPreCull` pass filtered to the map camera (fires
    after every LateUpdate, so the mesh bakes against the COMMITTED pan). Plus a per-recordingId
    last-good-on-line cache holds the marker through transient gaps (bounded 8 frames + 5s UT, falls
    through on a genuine orbital exit), which ALSO targets the connector/deorbit-leg-loses-icon bug
    (confirm in re-fly: fixed iff that root is ride-dropout, not leg-non-construction). Gate-off
    byte-identical (only the draw slot moved). FIX 2 is flight-map-scoped (the TS marker path does not use
    the ride; TS line stability still benefits). Two clean reviews SHIP; build clean, suite green (13470).
  - **Integration 3 - shared polyline draw host (DEFERRED - read-only scoping verdict 2026-06-06).** Do
    NOT do the "fold the autonomous walk under the Director" rewrite. The scoping found it is NOT worth it
    now and NOT a true 8e prerequisite: #1050 made the `onPreCull` DRAW the sanctioned shared mechanism
    (not legacy); the `-50` LateUpdate already does only the DECIDE + ownership publish. The ONLY piece 8e
    genuinely needs is closing the pid-0 atmospheric-only enumeration gap (the Director enumerates
    `ghostMapVesselPids` = proto-bearing; atmospheric-only no-orbit no-terminal-state recordings are pid-0,
    reached only by the Driver's `CommittedRecordings` walk - `GetGhostVesselPidForRecording` returns 0,
    `ghostMapVesselPids.Add` only in the proto-create funnel). That gap is NEAR-EMPTY in practice and
    ALREADY DRAWS CORRECTLY today (a pid-0 leg always takes Driver-direct `TryDrawLeg`); it is only a
    coverage-accounting bookkeeping item for the eventual deletion. The full rewrite is high-risk against
    the now-working, user-praised pan-stable host for ZERO behavior change. **Recommendation: defer #3;
    do the MINIMAL pid-0 coverage surface as PART of 8e, when the deletion actually consumes it - add a
    proto-less-recording coverage set, leave the `-50`/`onPreCull` draw path byte-identical, prove the
    Director's accounted set is a superset of the autonomous walk's drawn set, THEN delete.**
  - **Then 8e (deletion LAST):** once #2 is validated + the minimal pid-0 coverage (folded from #3) lands
    and nothing rides the legacy draw path uncovered, delete the legacy fallbacks + the autonomous
    `CommittedRecordings` DECIDE-walk + grace fields (KEEP the `onPreCull` DRAW mechanism - it is the
    sanctioned shared host, not legacy), grep-audit no readers, drop the `mapRenderDirectorDrive` gate ->
    single modular system.
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
