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

Option (c) is the user-facing "stable trajectory all the way" they asked for, with one nuance: the shadow lerp between two samples is a chord of the true tumbling-debris arc, so during a tumble the ghost will track straight lines that approximate the arc. At 1500 m offset and 0.22 s cadence the chord error peaks at a few metres -- visually invisible compared to the 800 m teleport the player sees today.

## Phase D and the LegacyDebrisShadowGate precedent

The in-repo CLAUDE.md explicitly notes:

> Format-v7 RELATIVE sections additionally store an `absoluteFrames` shadow list... The shadow remains recording data for resolver and compatibility paths; **Phase D removed the active-Re-Fly display-alignment and stale/no-live live-anchor fallback selectors from flight-scene playback**.

PR 3c added a `LegacyDebrisShadowGate` that fires only for `IsDebris && DebrisParentRecordingId == null && Relative section && non-empty shadow`, routing the frame through `TryUseRelativeAbsoluteShadowFallback` ahead of the resolver attempt. v12+ debris (with `DebrisParentRecordingId`) skips the gate per Decision section 7.

That precedent does NOT apply to v12 parent-anchored debris. So whatever we do here is an explicit additional carve-out, not an extension of the legacy gate. Phase D removed live-anchor fallback selectors -- the recorder shadow is recording-only data, never a live-anchor proxy, so the spirit of Phase D should still be respected.

## Options

### Option A: Render via `absoluteFrames` only inside the unstable gate window

Keep the existing tumbling-parent gate as the policy detector. Replace the action from "hide" to "render at `absoluteFrames` lerp position." Outside the gate, keep the parent-relative chain.

Implementation sketch:

- `TryHideForAnchorRotationUnreliable` becomes `TryRouteAnchorRotationUnreliable` (or similar): same predicate, different downstream effect. Instead of `state.ghost.SetActive(false)` and FX teardown, the engine calls a positioner method that resolves the world position from the recording's `absoluteFrames` lerp and a smoothed rotation (TBD).
- Engine continues to reject visual events / FX during the unreliable window (since the rotation isn't ground-truth), but the mesh stays visible at the smooth position.
- Gate transitions: the moment the gate releases, the next frame uses the parent chain. At a parent sample boundary (t=0 or t=1 in the parent bracket), the chain and the shadow lerp give identical world positions to within float precision -- so the transition is invisible if it happens at a boundary frame. The gate already releases at sample boundaries (release log lines have low bracketDeg, which only applies at boundary frames), so we get this for free.

Pros:
- Surgical: the gate, hysteresis, FX teardown, all unchanged. Only the position-routing decision in the gate-fired branch flips from "hide" to "lerp shadow."
- Reuses recorder data -- no new fields, no recorder changes.
- Visually: no disappearance, no teleport. Smooth motion.
- Outside tumble windows the parent chain still wins, preserving the existing fidelity guarantees.

Cons:
- Adds a v12 carve-out alongside the v11 LegacyDebrisShadowGate. Two debris-shadow code paths to keep aligned. Worth abstracting through a shared `TryUseRelativeAbsoluteShadowFallback` site if there isn't one already.
- The shadow's recorded `absoluteFrames` are FOREGROUND-recorded by the recorder when the focused vessel passed through the recording window. For BACKGROUND-loaded debris the shadow may be sparser (matches the BG sample cadence). For our test data, `absFrames=117` and `frames=118` are nearly equal, so there is essentially no density difference here. Future low-cadence BG recordings would show coarser smooth motion -- still smooth, just lower-resolution.
- Rotation handling: `absoluteFrames` carries world positions, not rotations. The debris's recorded local rotation is interpolated against the parent's interpolated rotation -- still tumble-amplified. We can either (i) freeze the visual rotation at the gate-engage frame's rotation through the unreliable window, (ii) lerp between the rotation samples at parent-boundary frames (where they are exact), or (iii) accept the existing rotation slerp since the visual artifact people complained about is positional, not rotational. Option (ii) is cheap and matches the position approach.

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

Implement Option A:

1. Generalise the existing `TryUseRelativeAbsoluteShadowFallback` (used by `LegacyDebrisShadowGate`) to also fire for v12 parent-anchored debris during the unreliable-anchor-rotation window. The v11 gate continues to fire for v11 debris; the v12 gate fires only when the tumbling-parent gate would have hidden. Two distinct triggers, one shared positioning helper.
2. Replace the engine's `HideAnchorRotationUnreliableState` action with a "route to shadow-lerp" action when the recording's section has `absoluteFrames`. Fall back to the existing hide-on-unreliable when no shadow data is available (older recordings that did not write a shadow, or future recordings that opt out).
3. Keep the gate's hysteresis and FX-teardown semantics. The visible mesh stays on; FX still suppress because the rotation interp is still untrustworthy.
4. Optional rotation handling: at parent sample boundaries the chain and shadow agree exactly, so lerp the rotation between the cached parent-anchored rotations at the bracket-boundary UTs. Avoids visible spinning chaos.
5. Update CHANGELOG, todo, and the in-repo CLAUDE.md to record the new v12 absolute-shadow carve-out and where it sits relative to Phase D.

Open questions:

- **Rotation policy during the smooth window**: freeze, slerp at sample boundaries only, or accept current slerp? Plan to start with "slerp at sample boundaries only" (cheapest correct option), revisit if the visual still reads weird.
- **Transition jitter**: confirm via instrumentation that gate engage/release happens at parent sample boundaries; if it happens mid-bracket, add a small fade-in across 2-3 frames at the release transition.
- **Recordings without `absoluteFrames`**: keep the hide path as the fallback when shadow data is absent. Log the route taken so playtest distinguishes the two paths.

## Test plan

- New unit test in `RelativeAnchorResolverTests` (or a new `DebrisAbsoluteShadowSmoothTests`): given a recording with `absoluteFrames` and the tumbling-parent gate engaged, the resolved position equals lerped shadow at the playback UT, not the unreliable parent-chain world position.
- New unit test: given a recording WITHOUT `absoluteFrames` (e.g. legacy v6 only), the engine still falls back to the existing hide path (regression guard so we do not lose the hide safety net).
- Integration test in `RuntimeTests.cs` (in-game): synthesize a tumbling-parent + 1500 m-offset debris pair, verify the visible ghost remains active=true through the chaos window, and the per-frame delta stays within a small multiple of expected (say 5x) instead of the current 30-300x.

## Out of scope for this investigation

- Phase D revisit on the broader live-anchor fallback (separate plan).
- Recorder-side improvements -- PR #793 already added the attitude trigger.
- Loop-relative debris: for now the gate continues to apply only to non-loop debris; extending to loop is a separate task.
