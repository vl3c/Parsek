# Debris smooth trajectory during parent tumble - investigation

> Branched from `fix-tumbling-parent-rot-interp` after Fix 1 (Evaluated flag) and Fix B (site-1157 zero-offset skip) made the gate's hide path actually work. The remaining user-visible problem is that hiding looks bad: the debris vanishes for ~2 seconds and reappears with a teleport. The ask is to render a stable trajectory through the unstable window instead of hiding.

## Symptom recap

After PR #793 + Fix 1 + Fix B (commit `e995d33`), the run-3 trace from `logs/2026-05-10_1156_debris-hide-policy-investigation/KSP.log` shows:

- One engage at UT 126.923 (parent rate 156 deg/s, bracket 34 deg, debris offset 1469 m).
- One release at UT 128.683 (heldFrames 206, parent rate dropped to 14 deg/s).
- Zero `active=true` frames inside the hold window. (Fix B working.)
- One large-delta event at the release UT: dM 219 m vs expected dM ~1.7 m -- the legitimate teleport at release.
- One large-delta event one frame BEFORE the engage UT (UT 126.903, dM 65 m) -- the gate is reactive, so one frame of chaos is rendered before the engage detects.

User's complaint: "they all just disappear and reappear; the solution would be to just have them move in a stable trajectory all the way." Reasonable -- a 2-second-long visible disappearance plus a teleport reads as a worse artifact than a slightly-imprecise smooth motion would.

## What data is already on disk

Trace from the same run:

```
phase=FrameStart rec=2f8c3916 ghostIndex=1 sec=0 secUT=[112.043,140.303]
  ref=Relative env=Atmospheric source=Background
  frames=118 absFrames=117 checkpoints=0 anchorRec=290c1ee5
```

The debris's Relative section already carries `absoluteFrames=117` -- the v7+ shadow that records the debris's true world position at each sample. Sample interval matches the recorder cadence (~0.22 s in this background-loaded case). Linear interpolation between adjacent `absoluteFrames` gives a smooth, recorder-truth path through the same window the gate is currently hiding.

The parent recording (`rec=290c1ee5`, `ref=Absolute`) does not carry an absolute shadow; it does not need one because its own frames are absolute world positions.

## Why the parent-chain produces chaos at all

For a parent-anchored debris ghost the per-frame world position is:

```
debris_world(ut) = parent.worldPos(ut) + parent.worldRot(ut) * debris.localOffset(ut)
```

`parent.worldRot(ut)` is computed by slerping between adjacent recorded parent attitudes. When the parent was tumbling at 200+ deg/s and the recorder only persisted samples every 0.22 s, the slerp arc between two adjacent recorded attitudes can be 50 deg apart -- and the actual rotation path between them was much wilder than a great-circle slerp can reproduce. Multiplied through a 1500 m offset, the per-frame swing is hundreds of metres.

This is fundamentally an undersampled-input problem: there is no in-between truth on disk. We can only:

(a) suppress the visible artifact by hiding (current),
(b) substitute a smoother synthetic motion that is approximately right,
(c) substitute the recorder's own world-position shadow (which is exact at sample times and a straight line between samples).

Option (c) is the user-facing "stable trajectory all the way" they asked for. The shadow lerp between two samples is a chord through space, so during a tumble the ghost tracks straight-line segments between recorded world positions. The relevant chord error is the sample-to-sample direction change in the `absoluteFrames` themselves, NOT the parent's lever-arm sweep -- the shadow already encodes the child's true world path, and parent angular velocity is exactly the slerp artifact we are bypassing. Run-3 trace data shows expected per-frame motion of ~1.7 m at 0.02 s for the watched debris, so the shadow's between-sample curvature is gentle. The visible artifact is bounded by the chord vs arc gap of the child's own trajectory and resets to zero at every sample tick. Compared to today's behaviour (1.76 s of hidden mesh followed by a 219 m teleport on release), this is unambiguously better.

## Phase D and the LegacyDebrisShadowGate precedent

The in-repo CLAUDE.md explicitly notes:

> Format-v7 RELATIVE sections additionally store an `absoluteFrames` shadow list... The shadow remains recording data for resolver and compatibility paths; **Phase D removed the active-Re-Fly display-alignment and stale/no-live live-anchor fallback selectors from flight-scene playback**.

PR 3c added a `LegacyDebrisShadowGate` that fires only for `IsDebris && DebrisParentRecordingId == null && Relative section && non-empty shadow`, routing the frame through `TryUseRelativeAbsoluteShadowFallback` ahead of the resolver attempt. v12+ debris (with `DebrisParentRecordingId`) skips the gate per Decision section 7.

That precedent does NOT apply to v12 parent-anchored debris. So whatever we do here is an explicit additional carve-out, not an extension of the legacy gate. Phase D removed live-anchor fallback selectors -- the recorder shadow is recording-only data, never a live-anchor proxy, so the spirit of Phase D should still be respected.

## Options

### Option A: Render via `absoluteFrames` only inside the unstable gate window

Keep the existing tumbling-parent gate as the policy detector. Replace the action from "hide" to "render at `absoluteFrames` lerp position." Outside the gate, keep the parent-relative chain.

Implementation sketch:

- `TryHideForAnchorRotationUnreliable` becomes a router (call it `TryRouteAnchorRotationUnreliable`): same predicate, but instead of always hiding, it returns a 3-state outcome (`None` / `ShadowPositioned` / `Hidden`). When `ShadowPositioned`, the engine calls a new positioner method that resolves world position AND world rotation from the recording's `absoluteFrames`. When `Hidden`, the existing hide path runs as a fallback for recordings without shadow data.
- The mesh stays visible during the `ShadowPositioned` route. Visual events / FX are still suppressed (rotation interp through the parent chain is what was untrustworthy; the shadow rotation is recorder-truth at sample times but we still don't want to fire transient FX during a route window). The ghost is NOT marked retired, so the engine's existing `if (retired)` short-circuit does not skip Activate / TrackGhostAppearance.
- Route transitions: when the route flips between `None` and `ShadowPositioned`, the next-rendered position MAY differ from the previous-rendered position. Hysteresis preserves hold across unevaluated sample-boundary frames, so release fires on the first evaluated frame after a boundary -- not necessarily at the boundary itself -- and the chain pose at that moment can sit anywhere within a bracket. The engine compares the just-resolved position against the previous-frame position; if delta exceeds a small threshold (start at 5 m), it blends position with smoothstep over the next N physics frames (start at N=4). No visibility fade; only position blend. Defaults are placeholders, instrumented and tuned on first playtest.
- Rotation: `absoluteFrames` carry full `TrajectoryPoint`s, including `srfRelRotation`, set at recorder-time before the relative-conversion call (`FlightRecorder.cs:7146` / `BuildTrajectoryPoint` at `:8436`; same on the BG side). Existing absolute-shadow playback at `ParsekFlight.cs:17158` already slerps the shadow rotation and composes it with `bodyTransform.rotation`. Routing a v12 ghost through the same per-section helper gets correct rotation handling with zero new code; no parent or local rotation interp involved.

Pros:
- Surgical: the gate, hysteresis, FX teardown, all unchanged. Only the position/rotation-routing decision in the gate-fired branch flips from "hide" to "shadow lerp."
- Reuses recorder data -- no new fields, no recorder changes.
- Visually: no disappearance, no teleport. Smooth motion with bounded chord error.
- Outside tumble windows the parent chain still wins, preserving the existing fidelity guarantees.

Cons:
- Adds a v12 carve-out alongside the v11 LegacyDebrisShadowGate. Two policy wrappers, but both go through the same existing `InterpolateAndPosition` flow (see Recommendation §1) so there is no duplicated position math.
- The existing wrapper `TryUseRelativeAbsoluteShadowFallback` (`ParsekFlight.cs:22674`) cannot be reused as-is: its first action is `TryRetireParentAnchoredDebrisOnRecordedAnchorMiss` (`:22691`), which retires v12 parent-anchored debris before the shadow lookup. The new tumbling-quality wrapper has to bypass that retire-first guard and call `InterpolateAndPosition(state.ghost, target.Section.absoluteFrames, ...)` directly, the same way the legacy wrapper does after it confirms shadow availability at `:22702-22722`. Each wrapper keeps its own retire / log / event policy on top of the shared interpolation call.
- Background-loaded debris: `absoluteFrames` cadence matches the recorder cadence, so for BG cases the shadow is as sparse as the parent samples. For our test data (`absFrames=117`, `frames=118`) they are nearly equal. Sparser future BG recordings will produce coarser smooth motion -- still smooth, just lower-resolution between samples.

### Option B: Render via parent chain but use sampled-truth waypoints between brackets

Compute the chain at the EXACT parent sample UTs (where slerp t=0 or t=1 gives ground truth) and lerp those world positions per frame. Effectively: pre-resolve the chain at parent's sample times into a per-debris cached "smoothed" trajectory, render via lerp through cache.

Pros:
- Uses the parent chain's authority all the way; no new data path.
- Matches the recorded shadow exactly at sample times (since chain at sample boundary IS the recorded position).
- Smooth between-sample motion via straight-line lerp between cached positions.

Cons:
- Conceptually equivalent to Option A: at parent sample times, chain == shadow exactly. So this just rebuilds the shadow at runtime instead of using the persisted shadow. More code, same visual.
- Adds a per-recording cache to invalidate on chain edits (rare, but a new state surface to maintain).

Verdict: Option A dominates. The shadow is already on disk and was already recorded for exactly this reason.

### Option C: Cubic-Hermite interpolation through parent rotation samples

Instead of slerp(N, N+1, t), fit a cubic curve through (N-1, N, N+1, N+2) and sample at t. Gives a C1-continuous rotation curve.

Pros:
- Stays in the parent-chain world; no shadow involvement.
- Generic improvement, not a tumble-specific carve-out.

Cons:
- Cubic fitting on quaternions is expensive (squad / nlerp + tangent estimation) and still won't reproduce the actual sub-sample rotation path -- it just smooths the visible artifact. Sub-sample chaos is fundamentally lost data; a cubic fit is still inventing a curve that wasn't recorded.
- Doesn't fix the position error. Slerp 50 deg between samples produces 80 deg sub-sample-arc errors that a cubic fit only mildly reduces.
- More code than Option A and worse outcome.

### Option D: Pre-record denser parent attitude (recurrence prevention, NOT a fix for existing recordings)

The original PR #793 already added a recorder-side attitude trigger so future BG-loaded tumbling parents emit denser samples. Old recordings (the user's playtest data) cannot benefit. Option A is needed for back-compat regardless.

## Recommendation

Implement Option A, structured as a "quality fallback" route distinct from the existing resolver-failure fallback. The corrections from external code review (around `absoluteFrames` carrying rotation, the existing slerp at `ParsekFlight.cs:17158`, and the v12-retire-first guard inside `TryUseRelativeAbsoluteShadowFallback` at `:22691`) are folded into the steps below.

1. **Reuse the existing `InterpolateAndPosition` path -- do NOT extract a parallel helper.** The earlier draft proposed extracting just `ParsekFlight.cs:17151-17158` (raw lerp + slerped rotation). That scope is too narrow: the surrounding `InterpolateAndPosition` flow ALSO

    - resolves body / effective altitude (tail-lift, terrain clearance) per side of the bracket (`ParsekFlight.cs:17142-17154`),
    - populates an `InterpolationResult` (velocity, body, altitude) that callers feed into `state.SetInterpolated(interpResult)` -- watch mode camera and FX paths read `lastInterpolatedBodyName / Altitude / Velocity` from there (canonical caller-side pattern at `ParsekFlight.cs:16653-16657`: `InterpolateAndPositionRecordedRelative(... out interpResult)` followed by `state.SetInterpolated(interpResult)`),
    - registers a `GhostPosEntry` (`ParsekFlight.cs:17343-17413`) for LateUpdate re-positioning after `FloatingOrigin.setOffset` shifts. Without this registration the ghost snaps one frame on every origin shift.

    A new shadow-only helper that omits any of these would visibly regress watch-mode camera, FX, and origin-shift handling. So the new tumbling-quality wrapper calls `InterpolateAndPosition(state.ghost, target.Section.absoluteFrames, ref playbackIdx, ut, ..., out interpResult)` directly with the section's absoluteFrames -- exactly the shape `TryUseRelativeAbsoluteShadowFallback` already uses at `ParsekFlight.cs:22711-22722`. The reuse gets body/altitude/InterpolationResult/GhostPosEntry handling for free, and the section's `[BracketBeforeUT, BracketAfterUT]` are recoverable from the same frames list for the caller's log line.

2. **Add a new `IGhostPositioner` method** with the standard positioner contract: `bool PositionFromRelativeAbsoluteShadow(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, out InterpolationResult result, out double bracketBeforeUT, out double bracketAfterUT)`. Implementation in `ParsekFlight` runs `InterpolateAndPosition` against the active Relative section's `absoluteFrames` exactly as the legacy shadow path does, returns false when no shadow covers the UT (caller falls back to `Hidden`), and otherwise:

    - returns the populated `InterpolationResult` for the engine to feed into `state.SetInterpolated(...)` (matching the canonical pattern at `ParsekFlight.cs:16653-16657`: `InterpolateAndPositionRecordedRelative(... out interpResult)` then `state.SetInterpolated(interpResult)`),
    - returns the section's bracket UTs around the playback UT for the caller's `[Anchor] anchor-rotation-shadow-route` log,
    - relies on `InterpolateAndPosition`'s own GhostPosEntry registration so LateUpdate handles FloatingOrigin shifts the same way it does for legacy v11 shadow playback.

3. **Convert `TryHideForAnchorRotationUnreliable` into a router** returning a small enum:
   - `AnchorRotationUnreliableRoute.None` -- gate did not fire, normal positioning runs.
   - `AnchorRotationUnreliableRoute.ShadowPositioned` -- gate fired AND the positioner returned true. Engine calls `state.SetInterpolated(interpResult)` with the InterpolationResult the positioner produced, keeps the mesh active, suppresses FX/events for the frame, does NOT mark `state.anchorRetiredThisFrame = true`, emits a `[Anchor] anchor-rotation-shadow-route` log line that includes `bracketBeforeUT` / `bracketAfterUT` and the playback UT.
   - `AnchorRotationUnreliableRoute.Hidden` -- gate fired but the positioner returned false (no shadow covered the UT). Existing hide path runs, log emits `[Anchor] anchor-rotation-unreliable-hidden` (renamed from the current single line so playtests distinguish the two routes).

   Sequencing inside the router: when the gate fires, the router calls the new positioner FIRST. Only if it returns false does the router fall through to the hide path. This call ordering is what lets the engine pick `ShadowPositioned` vs `Hidden` without the gate's `AnchorRotationReliabilityDecision` needing a new "shadow available" field.

   Local variable rename in `RenderInRangeGhost`: today the engine reads `bool anchorRotationHidden` from the gate and short-circuits the post-position pipeline with `bool retired = anchorRotationHidden || ...` at `GhostPlaybackEngine.cs:1217-1219`. The router's `ShadowPositioned` outcome must NOT short-circuit -- so `anchorRotationHidden` becomes only-true when the route is `Hidden`, and the `ShadowPositioned` outcome falls through to the normal Activate / TrackGhostAppearance branch (modified to skip FX events). Without this rename / branch split, the mesh stays at the new shadow position but FX teardown and appearance tracking get skipped, which would silently regress watch mode and FX state.

   **Loop and overlap callsites are in scope.** `TryHideForAnchorRotationUnreliable` has only TWO direct callers today: `RenderInRangeGhost` (non-loop) at `GhostPlaybackEngine.cs:1149` and `PositionLoopAtPlaybackUT` (primary loop) at `:2907`. The overlap-loop path (`UpdateExpireAndPositionOverlaps`) reaches the gate transitively by calling `PositionLoopAtPlaybackUT` at `:2230` rather than calling the gate directly, so converting the gate's body produces routing for all three contexts (non-loop, primary loop, overlap loop) by editing only the two direct call sites. The post-router engine code that calls `state.SetInterpolated(interpResult)` on `ShadowPositioned` and renames the local `bool retired = ...` short-circuit needs adding at two places: the non-loop branch at `:1217-1219` (paired with the `ApplyFrameVisuals` branches around `:1224-1275`), and the primary-loop branch at `:1815-1824`. The overlap-loop retired-branch mirror at `:2235-2257` does not call the gate directly; signalling has to come back through `PositionLoopAtPlaybackUT`'s outputs (either a new `out AnchorRotationUnreliableRoute` parameter, or a sibling flag on `state`/`ovState`). The host predicate `tryEvaluateAnchorRotationReliability` is only installed for `IsDebris && DebrisParentRecordingId != null` (`ParsekFlight.ShouldEvaluateAnchorRotationReliability`), so loop debris that is NOT parent-anchored (live-PID `LoopAnchorVesselId` only) skips the gate entirely the same way it does today. Tests cover all three contexts in §Test plan.

4. **Map / Tracking-Station playback is unaffected.** The flight-scene engine is the only consumer of the tumbling-parent gate. Map and TS playback go through `GhostMapPresence` (ProtoVessels) and `RecordedRelativeAnchorPoseResolver` for relative anchors -- neither path invokes `TryHideForAnchorRotationUnreliable`. The proposed router change is local to `GhostPlaybackEngine` and `ParsekFlight`'s positioner methods. No map/TS code edits, no separate carve-out for those scenes.

5. **Transition handling**: at the route-edge frame (first frame the route flips to / from `ShadowPositioned`), the engine compares the just-resolved position against the previous-frame's rendered position. If `delta > threshold` (start at 5 m -- well above sample-boundary precision noise but well below visible teleport size), blend between previous and target positions with a smoothstep curve.

   **Blend duration is wall-clock seconds, not physics frames.** The artifact being smoothed is a chord-error pop whose magnitude depends on parent rotation rate (recording-time data), not playback rate. A frame-count budget collapses to ~80 ms under 4x timewarp and stretches to ~400 ms under heavy lag, both wrong. The blend duration is `0.15 s` of UNSCALED wall-clock time, capped at 6 physics frames (so heavy-lag situations don't smear the blend across an unbounded sim-time window). At normal frame rates this is ~9 frames at 60 fps; the cap kicks in only on stutter.

   The blend state lives in `GhostPlaybackState` as a short-lived bundle (`transitionBlendStartUnscaledTime`, `transitionBlendDurationSeconds`, `transitionBlendFramesCap`, `transitionBlendFramesElapsed`, `transitionBlendStartPos`, `transitionBlendTargetPos`, all default to inactive sentinels). Each frame: if active, increment `framesElapsed`, compute `t = clamp01(min((Time.unscaledTime - startUT) / durationSeconds, framesElapsed / framesCap))`; on `t >= 1` the bundle resets to inactive.

   Explicit clear sites required, mirroring the existing five `state.anchorRetiredThisFrame` clear sites in `GhostPlaybackEngine` (canonical example: `:1147` and `:1374`):
   - Engine-level scene cleanup (`ClearAllGhosts` / `OnDestroy` paths) -- bundle cleared alongside ghost dictionary.
   - Per-ghost `DestroyGhost` -- state goes away with the ghost; covered by struct lifetime.
   - Soft-cap evict -- folded into `DestroyGhost` (same path; one bullet, not two).
   - Zone hidden->visible transition: `HandleHiddenGhostVisualState` should reset the blend bundle on re-show because the prior-frame position cached in the bundle may be stale across an LOD round-trip.
   - Loop cycle restart in `UpdateLoopingPlayback` / `UpdateExpireAndPositionOverlaps`: cycle boundary clears the bundle so a blend running across a cycle boundary doesn't anchor against the previous cycle's last frame.
   - `ResetForRender` / engine reset: blend bundle clears alongside per-frame counters. **F5 / F9 quickloads, scene transitions (flight->map / map->flight), and Re-Fly invocation all re-enter through scene-cleanup + engine reset, so they are covered by this bullet, not separate sites.** The implementer must verify by walking each transition path; if any of them re-uses `GhostPlaybackState` instances across the round-trip without going through `ResetForRender`, an explicit clear is needed there.

   Test `DebrisShadowSmoothRouteTests.TransitionBlend_ClearsOnLoopCycleRestart` (and the equivalent zone, scene-exit, ghost-destroy variants) lock these clear sites; see §Test plan.

   **Risk -- child-rotation jitter is NOT addressed by this fix.** The shadow's `srfRelRotation` samples are recorder-truth at sample times and great-circle slerp between. When the parent was tumbling fast enough that the gate fires, the CHILD's own attitude samples (separated debris inherits angular velocity at separation, plus its own residuals) may also be far apart in the same 0.20-0.22 s bracket. Slerping 60 deg over 0.22 s gives a smooth great-circle curve that may not match what actually happened sub-sample. This is no worse than today's parent-chain rotation handling, and there is no lever-arm amplification on rotation, so the visible artifact is bounded by the chord-vs-arc gap of the rotation curve itself. The user's complaint was positional, not rotational, so this is acceptable for now. If post-implementation playtest shows visible mesh-rotation popping, follow up with a separate fix (likely "freeze rotation at engage-frame attitude through the route window" or a parent-sample-boundary-only slerp similar to what was considered for position before option A was chosen).

6. **Hysteresis and FX teardown stay unchanged.** The gate predicate, `Evaluated` flag, hysteresis state, and `ApplyFrameVisuals` suppression flags carry over. The only behavioral change is the "what to do when the gate fires" branch.

7. **Phase D boundary stays crisp.** The shadow is read only after the gate has positively classified the parent chain as visually unreliable, AND only as recorded data, AND never as a substitute for live anchors. The plan doc and a comment at the new positioner method record this contract explicitly.

8. **Doc updates** (same commit as the code):

    `CHANGELOG.md` -- add to the v0.9.2 Bug Fixes section, modeled on the existing tumbling-parent bullet (CHANGELOG hard rule: 1 line per item, ≤2 sentences, user-facing only):

    > Parent-anchored debris with a tumbling parent now stays visible and follows a smooth recorded trajectory during the tumble window instead of disappearing. Recordings without an absolute-shadow track fall back to the existing hide path.

    `docs/dev/todo-and-known-bugs.md` -- new "## Done - v0.9.2 tumbling parent debris hide-and-teleport visual" entry (or extend the existing tumbling-parent entry as another follow-up paragraph; pick at code-time):

    > The tumbling-parent gate from PR #793 + Fix 1 + Fix B successfully hid debris during sparse-rotation windows, but the gate's hide-then-reactivate visual (~2 s of vanished mesh + a single-frame teleport at release of up to 845 m in run-1 logs) read worse to the player than the original chaotic motion would. Fix: replaced the gate's hide action with a route to the recording's `absoluteFrames` shadow when the section carries one. Mesh stays active, FX/events still suppressed, transform follows recorder-truth lerp through the chaos window. Recordings without shadow data still hide. Coverage: `DebrisShadowSmoothRouteTests` (router enum, position lerp, single-sample fallback, transition-blend timing and clear sites, no-retired-mark regression, loop-callsite parity), in-game `TumblingParentDebris_ShadowRoute_KeepsGhostVisibleAndStable`.

    `.claude/CLAUDE.md` -- one-line extension to the Format-v7 paragraph (around the existing "The shadow remains recording data for resolver and compatibility paths..." sentence) noting the new v12-tumbling-quality consumer:

    > Parent-anchored debris (v12+) also routes through the shadow when the tumbling-parent gate (PR #793) has positively classified parent chain rotation as unreliable, as a recorded-data quality fallback distinct from Phase D's removed live-anchor selectors.

## Open questions

- **Transition delta threshold and blend frame count**: defaults of 5 m / 4 frames are guesses. Plan to instrument the route-edge frames with `delta=` log lines on the first playtest after implementation, then tune. If 95th-percentile delta is well below 5 m, drop the blend entirely.
- **Recordings without `absoluteFrames`**: route to existing Hidden path. (Decided.)
- **Loop / overlap callsites**: routed the same way as non-loop -- the gate already fires from those callsites, the host predicate gates on `IsDebris && DebrisParentRecordingId != null`, so loop-only-anchored debris (live `LoopAnchorVesselId`, no parent recording) still skips the gate. Tests cover all three callsites. (Decided -- previously listed as out of scope.)

## Test plan

Unit:
- `DebrisShadowSmoothRouteTests.PositionFromShadow_LerpsBetweenAbsoluteFrames`: synthesize a recording with two `absoluteFrames` 0.20 s apart, request playback at midway UT, assert position is the linear lerp and rotation is the slerp.
- `DebrisShadowSmoothRouteTests.RouteIsShadowPositioned_WhenGateFiresAndShadowAvailable`: use the existing tumbling-parent test fixture, assert the new router returns `ShadowPositioned` instead of `Hidden`.
- `DebrisShadowSmoothRouteTests.RouteIsHidden_WhenGateFiresAndShadowMissing`: same gate, but section has empty `absoluteFrames`. Asserts `Hidden` (regression guard for the no-shadow case).
- `DebrisShadowSmoothRouteTests.RouteIsHidden_WhenSectionHasSingleAbsoluteFrame`: section has exactly one `absoluteFrame` so `InterpolateAndPosition`'s single-frame path can or cannot cover the requested UT. Asserts the positioner returns false and the router falls through to `Hidden` rather than rendering off the only sample.
- `DebrisShadowSmoothRouteTests.RouteIsHidden_WhenTargetUTOutsideShadowCoverage`: section has shadow data but the requested playback UT is outside `[absoluteFrames.first.ut, absoluteFrames.last.ut]`. Asserts the router falls through to `Hidden`.
- `DebrisShadowSmoothRouteTests.PositionFromShadow_BackgroundSparseShadow_LerpsAcrossLargerCadence`: synthesize a recording with `absoluteFrames` at 2.0 s cadence (BG worst case), request playback at midway UT, assert position is the linear lerp and the per-frame rendered delta stays bounded across a sweep of N test UTs. Locks the visual-quality regression guard for sparse BG shadows.
- `DebrisShadowSmoothRouteTests.TransitionBlend_AppliesWhenDeltaExceedsThreshold`: synthesize a route flip where shadowPos and chainPos differ by 50 m. Assert the engine emits 4 transition-blend frames, each pos within `[prev, target]` and monotonically advancing along smoothstep.
- `DebrisShadowSmoothRouteTests.TransitionBlend_DoesNotApplyWhenDeltaBelowThreshold`: route flip where the delta is 1 m (below 5 m default). Asserts the engine renders the new pose immediately, no blend frames.
- `DebrisShadowSmoothRouteTests.TransitionBlend_ClearsOnLoopCycleRestart`: blend running, loop cycle restart fires, asserts the bundle resets to inactive sentinels (so the next cycle does not anchor against the previous cycle's last frame).
- `DebrisShadowSmoothRouteTests.TransitionBlend_ClearsOnZoneShowAfterHidden`: blend running, ghost goes through `HandleHiddenGhostVisualState` and back. Asserts the bundle resets so the re-show frame does not blend against a stale prior pose.
- `DebrisShadowSmoothRouteTests.TransitionBlend_ClearsOnGhostDestroy`: blend running, ghost destroyed and recreated under the same index. Asserts the new state's bundle is in the inactive default state.
- `DebrisShadowSmoothRouteTests.RouteFlipAcrossSectionBoundary_BlendsPositionAndDoesNotDoubleSuppressFX`: synthesize a recording with two adjacent Relative sections where section A has empty `absoluteFrames` and section B has populated `absoluteFrames`. Sample at section A then at section B (gate firing in both). Asserts: route flips Hidden -> ShadowPositioned at the boundary, transition-blend engages on the flip, and FX-suppression flag is set exactly once per frame (not double-applied at the section transition).
- `DebrisShadowSmoothRouteTests.TransitionBlend_DurationIsWallClockSecondsCappedAtFrameBudget`: simulate a frame with normal physics dt and assert the blend completes after ~9 frames at 60 fps; simulate stuttered frames with a 50 ms physics dt and assert the blend completes at the 6-frame cap (300 ms wall-clock) rather than running for the full 0.15 s budget.
- `DebrisShadowSmoothRouteTests.RoutingDoesNotMarkRetired`: confirms `state.anchorRetiredThisFrame` stays false after the shadow route runs (so the existing `if (retired)` branch in `RenderInRangeGhost` does not skip ActivateGhostVisualsIfNeeded / TrackGhostAppearance for the shadow case).
- `DebrisShadowSmoothRouteTests.ShadowRoute_FiresFrom_PositionLoopAtPlaybackUT`: same gate setup as `RouteIsShadowPositioned_WhenGateFiresAndShadowAvailable`, but the engine entry is the primary-loop callsite (`PositionLoopAtPlaybackUT`). Asserts the route returns `ShadowPositioned` and `state.SetInterpolated(interpResult)` is called for that callsite too.
- `DebrisShadowSmoothRouteTests.ShadowRoute_FiresFrom_UpdateExpireAndPositionOverlaps`: same as above for the overlap-loop callsite.
- `DebrisShadowSmoothRouteTests.LoopOnlyAnchoredDebris_NotParentAnchored_SkipsGateEntirely`: debris with `LoopAnchorVesselId != 0` and `DebrisParentRecordingId == null`. Asserts the host predicate is not installed and the router never fires (preserves the existing live-anchor contract for loop-only debris).
- `DebrisShadowSmoothRouteTests.ShadowRoute_EmitsLogLineWithBracketUTs`: route flips to `ShadowPositioned`. Asserts the `[Anchor] anchor-rotation-shadow-route` log line is emitted exactly once (at the route-engage transition) with `bracketBeforeUT=...` and `bracketAfterUT=...` fields present and parseable. Locks the log contract so future log-validation tests can assert on it.
- `DebrisShadowSmoothRouteTests.HiddenRoute_EmitsRenamedLogLine`: route flips to `Hidden` (no shadow data). Asserts the renamed `[Anchor] anchor-rotation-unreliable-hidden` log line is emitted (regression guard so the rename does not break log validation).

In-game (`RuntimeTests.cs`):
- `TumblingParentDebris_ShadowRoute_KeepsGhostVisibleAndStable`: synthesize a tumbling-parent + 1500 m-offset debris pair (or use the existing fixture if present), verify visible ghost remains `active=true` through the chaos window, log `dM` per frame, assert max `dM` stays within 5x of `expectedDM`. Compare against the same fixture's pre-fix log (currently 30-300x).

## Out of scope for this investigation

- Phase D revisit on the broader live-anchor fallback (separate plan).
- Recorder-side improvements -- PR #793 already added the attitude trigger.
