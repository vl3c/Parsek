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
- Adds a v12 carve-out alongside the v11 LegacyDebrisShadowGate. Two policy wrappers, but a shared lower-level position helper (see Recommendation §1).
- The existing wrapper `TryUseRelativeAbsoluteShadowFallback` (`ParsekFlight.cs:22674`) cannot be reused as-is: its first action is `TryRetireParentAnchoredDebrisOnRecordedAnchorMiss` (`:22691`), which retires v12 parent-anchored debris before the shadow lookup. The position-from-shadow code at `:17151-17158` needs to be lifted into a pure helper that the new tumbling-quality wrapper, the existing legacy wrapper, and the resolver-failure wrapper all call. Each wrapper keeps its own retire / log / event policy.
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

1. **Extract a shadow-pose helper** from `ParsekFlight.cs:17151-17158`. Note: the existing `InterpolationResult` struct carries velocity/body/altitude only -- it cannot hold the new fields. Either define a new internal struct `RelativeAbsoluteShadowPose { Vector3d WorldPos; Quaternion SurfaceRelRotation; double BracketBeforeUT; double BracketAfterUT; bool Resolved; }`, or use explicit out params. Out params are simpler for an internal helper; signature:

    ```
    internal static bool TryInterpolateRelativeAbsoluteShadowAt(
        TrackSection section,
        double targetUT,
        out Vector3d worldPos,
        out Quaternion surfaceRelRotation,
        out double bracketBeforeUT,
        out double bracketAfterUT)
    ```

    Pure function, takes a `TrackSection` with `absoluteFrames`, returns false if the section has no shadow / target UT outside coverage / NaN sample. No retire / log / event policy. Exists alongside `TryUseRelativeAbsoluteShadowFallback`, which keeps its v12-retire-first guard intact for the resolver-failure path.

2. **Add a new `IGhostPositioner` method**: `bool PositionFromRelativeAbsoluteShadow(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, out double bracketBeforeUT, out double bracketAfterUT)`. Implementation in `ParsekFlight` calls the extracted helper on the active Relative section, composes world rotation = `bodyRotation * surfaceRelRotation`, writes the ghost transform, and exposes bracket UTs for the caller's log line. Returns false when no shadow data covers the UT (caller falls back to Hidden).

3. **Convert `TryHideForAnchorRotationUnreliable` into a router** returning a small enum:
   - `AnchorRotationUnreliableRoute.None` -- gate did not fire, normal positioning runs.
   - `AnchorRotationUnreliableRoute.ShadowPositioned` -- gate fired AND the positioner reported success -- engine keeps the mesh active, suppresses FX/events for the frame, does NOT mark `state.anchorRetiredThisFrame = true`, emits a `[Anchor] anchor-rotation-shadow-route` log line that includes `bracketBeforeUT` / `bracketAfterUT` and the playback UT.
   - `AnchorRotationUnreliableRoute.Hidden` -- gate fired but no shadow data covered the UT -- existing hide path runs, log emits `[Anchor] anchor-rotation-unreliable-hidden` (renamed from the current single line so playtests distinguish the two routes).

4. **Transition handling**: at the route-edge frame (first frame the route flips to / from `ShadowPositioned`), the engine compares the just-resolved position against the previous-frame's rendered position. If `delta > threshold` (start at 5 m -- well above sample-boundary precision noise but well below visible teleport size), blend between previous and target positions with a smoothstep curve over the next N physics frames (start at N=4). Implementation lives in the engine's per-state struct as a short-lived `transitionBlend` field; resets to inactive when blend completes. No visibility fade; only position blend. Defaults are guesses, instrumented and tuned after first playtest.

5. **Hysteresis and FX teardown stay unchanged.** The gate predicate, `Evaluated` flag, hysteresis state, and `ApplyFrameVisuals` suppression flags carry over. The only behavioral change is the "what to do when the gate fires" branch.

6. **Phase D boundary stays crisp.** The shadow is read only after the gate has positively classified the parent chain as visually unreliable, AND only as recorded data, AND never as a substitute for live anchors. The plan doc and a comment at the new positioner method record this contract explicitly.

7. **Doc updates**: CHANGELOG entry, `docs/dev/todo-and-known-bugs.md` follow-up paragraph, and a one-line extension to `.claude/CLAUDE.md`'s Format-v7 paragraph noting the additional v12-tumbling-quality consumer of `absoluteFrames`. Same commit as the code.

## Open questions

- **Transition delta threshold and blend frame count**: defaults of 5 m / 4 frames are guesses. Plan to instrument the route-edge frames with `delta=` log lines on the first playtest after implementation, then tune. If 95th-percentile delta is well below 5 m, drop the blend entirely.
- **Recordings without `absoluteFrames`**: route to existing Hidden path. (Decided.)
- **Loop-relative debris**: out of scope; gate stays non-loop-only. (Decided.)

## Test plan

Unit:
- `DebrisShadowSmoothRouteTests.PositionFromShadow_LerpsBetweenAbsoluteFrames`: synthesize a recording with two `absoluteFrames` 0.20 s apart, request playback at midway UT, assert position is the linear lerp and rotation is the slerp.
- `DebrisShadowSmoothRouteTests.RouteIsShadowPositioned_WhenGateFiresAndShadowAvailable`: use the existing tumbling-parent test fixture, assert the new router returns `ShadowPositioned` instead of `Hidden`.
- `DebrisShadowSmoothRouteTests.RouteIsHidden_WhenGateFiresAndShadowMissing`: same gate, but section has empty `absoluteFrames`. Asserts `Hidden` (regression guard for the no-shadow case).
- `DebrisShadowSmoothRouteTests.TransitionBlend_AppliesWhenDeltaExceedsThreshold`: synthesize a route flip where shadowPos and chainPos differ by 50 m. Assert the engine emits 4 transition-blend frames, each pos within `[prev, target]` and monotonically advancing along smoothstep.
- `DebrisShadowSmoothRouteTests.RoutingDoesNotMarkRetired`: confirms `state.anchorRetiredThisFrame` stays false after the shadow route runs (so the existing `if (retired)` branch in `RenderInRangeGhost` does not skip ActivateGhostVisualsIfNeeded / TrackGhostAppearance for the shadow case).

In-game (`RuntimeTests.cs`):
- `TumblingParentDebris_ShadowRoute_KeepsGhostVisibleAndStable`: synthesize a tumbling-parent + 1500 m-offset debris pair (or use the existing fixture if present), verify visible ghost remains `active=true` through the chaos window, log `dM` per frame, assert max `dM` stays within 5x of `expectedDM`. Compare against the same fixture's pre-fix log (currently 30-300x).

## Out of scope for this investigation

- Phase D revisit on the broader live-anchor fallback (separate plan).
- Recorder-side improvements -- PR #793 already added the attitude trigger.
- Loop-relative debris: for now the gate continues to apply only to non-loop debris; extending to loop is a separate task.
