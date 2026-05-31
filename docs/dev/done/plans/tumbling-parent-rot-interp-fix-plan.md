# Implementation Plan v4: Anchor rotation interpolation chaos when parent is tumbling

> **Plan v4** -- fixes the v3 review findings: `RelativeAnchorResolverContext` is readonly, the child local offset must be computed before parent resolution, site 1157 is child-relative rotation and must not drive the parent-rotation gate, loop-synced debris must evaluate at the actual playback UT, hysteresis must be keyed per child+anchor pair, and the tests/threshold text must match the final cleanup policy.
>
> **v2 RESOLVED from v1:** v7 shadow fallback eliminated (replaced by HIDE-on-suspect, consistent with `DebrisRelativePlaybackPolicy`); all relevant slerp sites were audited (parent sites 878/1074 gated, child-relative site 1157 explicitly out of scope); PR #787 interaction verified.

## Bug summary

When a parent recording captures a vessel tumbling at high angular velocity, debris ghosts anchored to that parent with large local-offset vectors render with chaotic per-frame motion (50-200m dM where expectedDM ~2m). Adjacent recorded parent rotations are 50 deg apart over 0.22s = ~240 deg/s tumble. Slerp between sparse samples doesn't reproduce the actual rotation curve. With debris local-offset of 1400m+, rotation-interpolation errors translate to 100m+ position errors per physics frame.

Evidence -- debris #2 (`a118a1dc`):
```
frame 11177 ut=42.000 pos=(1352.88, 54.67, -369.29) dM=111.87 expectedDM=2.08
frame 11179 ut=42.020 pos=(1308.92, 54.35, -471.13) dM=110.92 expectedDM=2.08
frame 11181 ut=42.040 pos=(1257.66, 54.00, -568.42) dM=109.97 expectedDM=2.09
```

## Critical constraint: `DebrisRelativePlaybackPolicy` contract

[`Source/Parsek/DebrisRelativePlaybackPolicy.cs:14-22`](Parsek-fix-tumbling-parent-rot-interp/Source/Parsek/DebrisRelativePlaybackPolicy.cs:14):

> Parent-anchored debris should disappear when its recorded parent anchor cannot be resolved. The v7 absolute shadow is not an independent fallback for this case because **it can continue stale motion after the debris has left the parent's resolvable range**.

v4 extends the policy semantics from "anchor cannot be resolved" to include "rotation interpolation unreliable due to data sparsity," and updates the file's XML comment in the same PR. This is a strict broadening, not a reversal.

## 1. Fix shape

### 1.1 HIDE vs FREEZE-AT-LAST-STABLE -- consideration of alternatives (v2 review IMPORTANT)

The v2 reviewer asked whether freezing the ghost at its last reliably-resolved world pose for the suspect window is better than hiding it. v4 keeps that decision:

- **Freeze-at-last-stable:** holds the last stable position of the ghost mesh while the rotation interp is unreliable. Visual: ghost appears to stop moving for the duration of the parent's tumble, then resumes. Pros: preserves visual continuity (the player still sees the booster). Cons: position diverges from physics reality (the booster IS still falling/moving in the world); may cause overlap with terrain or other ghosts if held too long; does not match the existing seed-bridge precedent (`DebrisRelativePlaybackPolicy.TryResolveInitialStructuralSeedBridgeEndUT` hides through the bridge window).
- **HIDE (chosen):** ghost mesh disappears for the suspect window. Pros: matches the existing precedent of "hide when contract data is unreliable"; no position-divergence risk; consistent with the policy's "should disappear" language. Cons: brief visual discontinuity (the booster vanishes for a few seconds during the parent's tumble window).

Decision: HIDE. The "freeze" alternative may LOOK better for short windows but introduces position-divergence risk that the policy was explicitly written to prevent ("can continue stale motion after the debris has left the parent's resolvable range" -- the same phrase applies if the ghost is frozen while the world keeps moving). HIDE is safer and consistent with the existing seed-bridge precedent.

### 1.2 Engine-side gate via `TrajectoryPlaybackFlags[]` (v2 BLOCKER + v4 loop-UT fix)

(v2 review BLOCKER: `FrameContext` struct-by-value can't carry positioner output. Use the existing per-frame flags array instead.)

Add the shared decision payload to `Source/Parsek/TumblingParentInterpolationGate.cs`:

```csharp
internal readonly struct AnchorRotationReliabilityDecision
{
    public readonly bool Unreliable;
    public readonly string AnchorRecordingId;
    public readonly double BracketDegrees;
    public readonly double RateDegreesPerSecond;
    public readonly double OffsetMeters;

    public AnchorRotationReliabilityDecision(
        bool unreliable,
        string anchorRecordingId,
        double bracketDegrees,
        double rateDegreesPerSecond,
        double offsetMeters)
    {
        Unreliable = unreliable;
        AnchorRecordingId = anchorRecordingId;
        BracketDegrees = bracketDegrees;
        RateDegreesPerSecond = rateDegreesPerSecond;
        OffsetMeters = offsetMeters;
    }
}
```

Add a host-owned evaluator seam to `Source/Parsek/GhostPlaybackEvents.cs`:

```csharp
internal delegate bool TryEvaluateAnchorRotationReliability(
    int index,
    IPlaybackTrajectory traj,
    double playbackUT,
    out AnchorRotationReliabilityDecision decision);
```

Add ONE field to `TrajectoryPlaybackFlags`:

```csharp
/// <summary>
/// Host callback for parent-anchored debris. The engine invokes this with the
/// actual playback UT it is about to render, including loop-synced parent UTs.
/// Null means no anchor-rotation reliability check is needed.
/// </summary>
public TryEvaluateAnchorRotationReliability tryEvaluateAnchorRotationReliability;
```

`Source/Parsek/ParsekFlight.cs:14833` `ComputePlaybackFlags` assigns this callback only for `rec.IsDebris && rec.DebrisParentRecordingId != null`. Non-debris, legacy debris, and disabled/no-data recordings leave it null.

Why this is not a precomputed bool: loop-synced debris can render with `parentLoopUT`, not `ctx.currentUT` (`GhostPlaybackEngine.cs:701-703`). A boolean computed once in `ComputePlaybackFlags(committed, currentUT)` would hide/show the ghost for the wrong bracket. The engine must call the callback at the same `visiblePlaybackUT` it passes to positioning.

**v3 review NIT fix:** sister API matches the existing house style (out-params, not tuple return):

```csharp
internal static bool TryResolveAnchorPoseWithReliability(
    RelativeAnchorResolverContext context,
    string anchorRecordingId,
    double ut,
    HashSet<string> visited,
    out AnchorPose pose,
    out AnchorRotationReliabilityDecision decision);
```

Mirrors `TryResolveAnchorPose` at `RelativeAnchorResolver.cs:86`. The original `TryResolveAnchorPose` stays unchanged for non-engine callers (map presence, KSC playback).

`TryResolveAnchorPoseWithReliability` reports the instantaneous bracket decision. The `ParsekFlight` callback applies `AnchorRotationHysteresisState` keyed by child+anchor before returning `decision.Unreliable` to the engine.

**Design seam -- where the gate lives** (v3 review BLOCKER #3 + v4 correction): sites 878 and 1074 inside `TryResolveAbsoluteBracketPose` / `TryResolveAbsoluteFramesPose` operate on the PARENT recording during recursive anchor resolution. The debris's local offset currently comes from `TryInterpolateRelativeFrame` (lines 1154-1156), but current code resolves the parent first at `RelativeAnchorResolver.cs:912-915`. v4 therefore explicitly reorders the relative-section path:

1. Resolve `anchorRecordingId`.
2. Interpolate the child relative frame first with `TryInterpolateRelativeFrame` to obtain `dx/dy/dz` and `relativeRotation`.
3. Compute `offsetSq = dx*dx + dy*dy + dz*dz`.
4. Call `TryResolveAnchorPoseWithReliability(context.WithDebrisLocalOffsetSquaredMeters(offsetSq), anchorRecordingId, ut, visited, out parentPose, out reliabilityDecision)`.
5. If reliable, apply the already-interpolated `dx/dy/dz/relativeRotation` to `parentPose`.

Because `RelativeAnchorResolverContext` is a `readonly struct`, the offset must be threaded immutably:

```csharp
// New readonly field on RelativeAnchorResolverContext:
public readonly double DebrisLocalOffsetSquaredMeters;

internal RelativeAnchorResolverContext WithDebrisLocalOffsetSquaredMeters(double value)
{
    return new RelativeAnchorResolverContext(
        FocusTree,
        FocusRecordingId,
        FocusTreeId,
        ActiveReFlyMarker,
        ProvisionalRecordings,
        PendingTree,
        SectionAnchorRecordingIdResolver,
        AbsoluteWorldPositionResolver,
        BodyWorldRotationResolver,
        OrbitalCheckpointPoseResolver,
        value);
}
```

The constructor gains an optional final `double debrisLocalOffsetSquaredMeters = 0.0` parameter so existing callers compile unchanged. This preserves the existing readonly-context style and avoids mutating the struct after construction.

The parent-resolution helper stays close to the slerp sites:

```csharp
// New shared internal helper, used at the 2 parent-rotation slerp sites:
internal static AnchorRotationReliabilityDecision EvaluateParentRotationInterpolation(
    string anchorRecordingId,
    TrajectoryPoint before, TrajectoryPoint after,
    double debrisLocalOffsetSquaredMeters)
{
    double offsetMeters = Math.Sqrt(Math.Max(0.0, debrisLocalOffsetSquaredMeters));
    double bracketSeconds = after.ut - before.ut;
    float bracketDeg = TrajectoryMath.ComputeQuaternionAngleDegrees(before.rotation, after.rotation);
    double rate = bracketSeconds > 0.0 ? (double)bracketDeg / bracketSeconds : 0.0;
    bool unreliable =
        offsetMeters >= TumblingParentInterpolationGate.MinOffsetMagnitudeMeters
        && bracketSeconds > 0.0
        && bracketDeg >= TumblingParentInterpolationGate.EnterAngleDegrees
        && rate >= TumblingParentInterpolationGate.EnterRateDegreesPerSecond;
    return new AnchorRotationReliabilityDecision(
        unreliable, anchorRecordingId, bracketDeg, rate, offsetMeters);
}
```

The production implementation may keep a cheap offset-first fast path for close debris, but it must still return a populated decision when logging a transition. The gate fires BEFORE parent slerp at lines 878/1074, so the bug-regime case never invokes the unreliable parent interpolation.

Site 1157 is intentionally NOT part of this gate. It interpolates the child's anchor-local relative rotation after `dx/dy/dz` is already known, and it does not rotate the large position offset around the parent. Hiding on site 1157 would false-positive on far debris that is spinning while its parent pose is stable.

In `Source/Parsek/GhostPlaybackEngine.cs`, add one private helper and call it from every relative-positioning path that can render parent-anchored debris:

- `RenderInRangeGhost` at the second `visiblePlaybackUT` site (`GhostPlaybackEngine.cs:1106`), immediately before `TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage` / positioning. Do not use the earlier `initialCoveragePlaybackUT` at `:967`; that is only for the pre-spawn coverage-retire guard.
- `PositionLoopAtPlaybackUT` before `TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage` / `positioner.PositionLoop`. Update `PositionLoopAtPlaybackUT` to accept `TrajectoryPlaybackFlags flags`, `frameUT`, and `warpRate`, then thread those through every caller:
  - direct primary loop call at `GhostPlaybackEngine.cs:1759`: pass `f`, `ctx.currentUT`, `ctx.warpRate`
  - overlap loop calls at `:1999` and `:2178`: pass `f`, `ctx.currentUT`, `ctx.warpRate`
  - `PositionGhostAtLoopEndpoint` call at `:2295`: expand `PositionGhostAtLoopEndpoint` to accept `flags`, `frameUT`, and `warpRate`, and update its callers at `:1571`, `:2080`, and `:2222`. Where there is no distinct frame UT in scope, use the endpoint playback UT as the log-only `frameUT`.

This covers normal playback, loop-synced debris that reaches `RenderInRangeGhost`, and direct loop playback that bypasses `RenderInRangeGhost` through `ShouldLoopPlayback(traj)`.

```csharp
private bool TryHideForAnchorRotationUnreliable(
    int index,
    IPlaybackTrajectory traj,
    TrajectoryPlaybackFlags flags,
    GhostPlaybackState state,
    double playbackUT,
    double frameUT,
    float warpRate,
    string phase)
{
    if (flags.tryEvaluateAnchorRotationReliability == null
        || !flags.tryEvaluateAnchorRotationReliability(index, traj, playbackUT, out var decision)
        || !decision.Unreliable)
    {
        return false;
    }

    if (state?.ghost != null)
    {
        state.ghost.SetActive(false);
        ResetGhostAppearanceTracking(state);
        ApplyFrameVisuals(index, traj, state, playbackUT, warpRate,
            skipPartEvents: true,
            suppressVisualFx: true,
            allowTransientEffects: false);
    }

    CountFrameSkip(GhostPlaybackSkipReason.AnchorRotationUnreliable);
    GhostRenderTrace.EmitGuardSkip(traj, index, frameUT,
        "anchor-rotation-unreliable phase=" + phase);
    return true;
}
```

Usage in `RenderInRangeGhost`:

```csharp
if (TryHideForAnchorRotationUnreliable(
        i, traj, f, state, visiblePlaybackUT, ctx.currentUT, ctx.warpRate, "non-loop"))
{
    return true;
}
```

Usage in `PositionLoopAtPlaybackUT`:

```csharp
if (TryHideForAnchorRotationUnreliable(
        index, traj, flags, state, loopUT, frameUT, warpRate, callsite))
{
    return;
}
```

No `GhostPlaybackState.suppressedThisFrame` field is added; it does not exist today. The hide helper reuses the existing FX teardown pattern (`ApplyFrameVisuals(... suppressVisualFx: true, allowTransientEffects: false)`) so engine/RCS/reentry/audio state cannot keep rendering after the mesh is hidden.

The helper is evaluated at the same playback UT that positioning would use. For loop-synced debris that enters through the parent-loop path, `syncCtx.currentUT = parentLoopUT` before `RenderInRangeGhost`, so `visiblePlaybackUT` is already the loop-derived UT. For direct loop playback, `PositionLoopAtPlaybackUT` receives `loopUT` directly.

`GhostPlaybackSkipReason.AnchorRotationUnreliable` added with `ToLogToken` mapping `"anchor-rotation-unreliable"`.

The engine never imports `RelativeAnchorResolver`. The engine only invokes a host-supplied callback at the actual playback UT. Standalone-mod boundary preserved.

### 1.3 Slerp site 878 narrow coverage (v2 review IMPORTANT)

Site 878 (`TryResolveAbsoluteBracketPose`) is a small-section-gap fallback with bracket span <= 0.10s (per the comment at line 876-877). The fallback path itself is uncommon at runtime, so the site is expected to be exercised infrequently. When it is exercised in the bug regime, the predicate can still trip correctly: 240 deg/s over 0.10s is ~24 deg, above both the 8 deg angle threshold and 150 deg/s rate threshold when the debris offset is large. v4 documents this expected behavior in the helper's XML comment so a future reviewer doesn't think the gate is "broken at site 878."

The gate IS still wired at site 878 for correctness; most runs simply will not enter this fallback path. Coverage is a defense-in-depth measure.

### 1.4 Threshold tuning + hysteresis (v4 -- refined per review)

**Enter thresholds** -- fire HIDE when ALL of:
- `delta-theta >= EnterAngleDegrees = 8 deg` per bracket pair
- `delta-theta/delta-t >= EnterRateDegreesPerSecond = 150 deg/s`
- debris local offset magnitude `>= MinOffsetMagnitudeMeters = 50m`
- AND `traj.IsDebris && traj.DebrisParentRecordingId != null`

**Exit thresholds** (hysteresis):
- Release immediately if debris local offset magnitude drops below `MinOffsetMagnitudeMeters = 50m`.
- Otherwise release only when `delta-theta <= ExitAngleDegrees = 4 deg` AND `delta-theta/delta-t <= ExitRateDegreesPerSecond = 75 deg/s`.

**Reaction-wheel false-positive analysis (review IMPORTANT #6 fix -- raised rate threshold):**

The v3 review correctly flagged that the original 90 deg/s rate threshold was optimistic: a 180 deg/s probe at t=3s post-separation can have 50-100m offset to debris children (decoupled stage with sustained relative velocity from a separation motor). Both thresholds would fire -- false positive.

**v4 uses `EnterRateDegreesPerSecond = 150 deg/s`**:
- Bug regime: 240 deg/s+ tumble (well above 150 deg/s) -> gate fires reliably.
- Stock + tweakable reaction-wheel limit: ~120 deg/s peak observed in vanilla KSP gameplay. Buffer of 30 deg/s above the legitimate-fast ceiling.
- KER/MJ scripted maneuvers can briefly hit 180 deg/s but are typically very short bursts (<0.5s); even when they pass the rate threshold, the bracket is so short that the angle-threshold AND the offset-threshold filters typically save them.
- For the bug regime where playback shows ~5 seconds of sustained chaos, the rate must be sustained -- brief 180 deg/s spikes during legitimate scripted maneuvers are unlikely to coincide with a debris recording having 50m+ offset for that exact bracket.

The 150 deg/s value is an empirical tuning knob, not a serialized contract. If post-merge reports show false positives, tune the rate threshold first; the offset filter is the more principled guard because it directly represents whether parent rotation error can amplify into visible translation.

Hysteresis enter/exit:
- Enter: delta-theta >= 8 deg AND delta-theta/delta-t >= 150 deg/s AND offset >= 50m
- Exit: offset < 50m OR (delta-theta <= 4 deg AND delta-theta/delta-t <= 75 deg/s)

**The AND of three conditions (angle + rate + offset) is load-bearing.** Each one alone false-positives. Documented as `internal const` rationale in `TumblingParentInterpolationGate.cs` XML comment.

The v3 review's recommendation (b) -- add a third AND condition based on debris-relative-velocity -- was considered but rejected: it adds another threshold to tune and the offset filter alone is sufficient given the conservative rate raise.

### 1.5 Hysteresis state lifetime (v4 review fix)

`Dictionary<AnchorRotationHysteresisKey, AnchorRotationHysteresisState> _anchorRotationHysteresis`, owned by `ParsekFlight`.

Key shape:

```csharp
internal readonly struct AnchorRotationHysteresisKey : IEquatable<AnchorRotationHysteresisKey>
{
    public readonly string ChildRecordingId;
    public readonly string AnchorRecordingId;

    public AnchorRotationHysteresisKey(string childRecordingId, string anchorRecordingId)
    {
        ChildRecordingId = childRecordingId ?? string.Empty;
        AnchorRecordingId = anchorRecordingId ?? string.Empty;
    }

    public bool Equals(AnchorRotationHysteresisKey other)
    {
        return string.Equals(ChildRecordingId, other.ChildRecordingId, StringComparison.Ordinal)
            && string.Equals(AnchorRecordingId, other.AnchorRecordingId, StringComparison.Ordinal);
    }

    public override bool Equals(object obj)
    {
        return obj is AnchorRotationHysteresisKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ChildRecordingId ?? string.Empty);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(AnchorRecordingId ?? string.Empty);
            return hash;
        }
    }
}
```

`TumblingParentInterpolationGate.cs` needs `using System;` for `IEquatable<T>`, `StringComparison`, and `StringComparer`.

The key is child+anchor, not parent-only. This is required because the offset threshold is per debris child. A 1500m-offset booster should not put a 10m-offset sibling into the same hysteresis hold.

Implement `IEquatable<AnchorRotationHysteresisKey>` and `GetHashCode` explicitly. The default `ValueType.Equals` path works functionally but is reflection-based and unsuitable for per-frame dictionary lookups.

For test fixtures or transient trajectories with an empty `RecordingId`, `ParsekFlight` uses the stable per-frame index fallback already passed to the callback (`"idx-" + index`) as the child key component.

**v3 review correction:** `RecordingStore.OnRecordingRemoved` event does NOT exist. Grep across `Source/Parsek` returns zero matches. The plan v2 was wrong to claim it. Cleanup hooks now use ONLY events that demonstrably exist:

- **Scene exit:** `ParsekFlight.OnDestroy` (verified at `ParsekFlight.cs:1924`) and `ParsekFlight.OnSceneChangeRequested` (verified at `:1007`) -- both clear the dictionary entirely. This is the primary cleanup path.
- **Recording deletion within a scene:** explicitly NOT handled. Stale entries persist until next scene change. Memory footprint: at most 1 entry per child+anchor pair across the dictionary's lifetime in a scene. A long sandbox session can accumulate hundreds or even thousands of pairs, but each entry is only two string references plus the small state struct; even thousands of pairs stay comfortably below 100 KB and are cleared on scene exit. Acceptable. Documented in the helper's XML comment as a known limitation.
- **Re-Fly session end:** `ParsekScenario.ActiveReFlySessionMarker` is a state OBJECT, not an event. There is no `OnReFlySessionEnd` event today. v4 does NOT add one -- the scene-exit hook handles the common case (every Re-Fly transitions through a scene change at completion). If the hysteresis flapping turns out to be a real symptom across Re-Fly sessions within one flight, file a follow-up to add the event.

Memory-leak risk: bounded by scene lifetime and small enough for worst-case gameplay sessions. Acceptable.

### 1.6 Recorder-side BG attitude trigger (recurrence prevention)

For NEW recordings only: add an attitude trigger to BG sampling at `Source/Parsek/BackgroundRecorder.cs:1937` that fires when `Quaternion.Angle(currentWorldRotation, state.lastWorldRotation) >= 1.0f` AND `elapsed >= attitudeMinSampleInterval`.

**v3 simplification** (v2 review IMPORTANT -- sister helper not needed): reuse `FlightRecorder.ShouldRecordAttitudePoint` directly. It is `internal static` and stateless at `FlightRecorder.cs:8346`, taking `currentWorldRotation, lastWorldRotation, currentUT, lastRecordedUT, hasLastWorldRotation, minInterval, rotationThresholdDegrees`. No sister helper needed. Pass BG-side `state.lastWorldRotation`, `state.hasLastWorldRotation`, and BG-side `attitudeMinSampleInterval`.

`FlightRecorder.attitudeSampleThresholdDegrees` is a private const at `FlightRecorder.cs:918`, so `BackgroundRecorder` cannot reference it directly. Add a local BG-side private const:

```csharp
private const float backgroundAttitudeSampleThresholdDegrees = 1.0f;
```

Use that constant when calling `ShouldRecordAttitudePoint`. If the foreground threshold ever changes, update both constants in the same commit.

**v4 correction:** do NOT pass the normal BG `effectiveMinSampleInterval` for attitude. For non-high-fidelity background vessels that value is the proximity tier interval (`FarInterval = 2.0s`), which can still leave a tumbling parent too sparse. Compute a separate attitude floor:

```csharp
float foregroundAttitudeMin = FlightRecorder.ResolveEffectiveMinSampleInterval(
    true,
    minSampleInterval);
float attitudeMinSampleInterval =
    Math.Min(effectiveMinSampleInterval, foregroundAttitudeMin);
```

Then call `ShouldRecordAttitudePoint(..., attitudeMinSampleInterval, backgroundAttitudeSampleThresholdDegrees)`. This only bypasses the proximity cadence while the vessel is inside the normal background sampling range; the existing out-of-range return remains intact. A far-tier tumbling parent inside the physics bubble can now emit attitude samples at foreground minimum cadence, while high-fidelity windows keep any more aggressive motion cadence and stationary far-tier vessels continue using velocity/proximity cadence.

Note: `FlightRecorder.ResolveEffectiveMinSampleInterval(true, minSampleInterval)` is currently a no-op wrapper over `minSampleInterval` (`FlightRecorder.cs:805-808`), but the implementation keeps the wrapper call so this background path stays aligned if foreground cadence policy grows another override. The active-recorder 4-arg overload also considers Re-Fly tree cadence; background attitude sampling intentionally does not introduce that Re-Fly tree override because the BG sampler does not currently participate in active Re-Fly tree cadence control.

Implementation detail: split the current inline `TrajectoryMath.ShouldRecordPoint(...)` guard into two booleans, matching the active recorder pattern:

```csharp
bool motionTriggered = TrajectoryMath.ShouldRecordPoint(... effectiveMinSampleInterval ...);
bool attitudeTriggered = FlightRecorder.ShouldRecordAttitudePoint(
    ... attitudeMinSampleInterval, backgroundAttitudeSampleThresholdDegrees);
if (!motionTriggered && !attitudeTriggered)
    return;
```

New fields on `BackgroundVesselState` (in-memory only, not serialized -- verified by reading `BackgroundRecorder.cs:189`, declared `private class`, no codec):

```csharp
public Quaternion lastWorldRotation;
public bool hasLastWorldRotation;
```

Updated by the BG sample loop on every successful sample emission, mirroring the active recorder's pattern.

**PR #787 interaction (v2 verified, v4 refined):** parent vessel recordings are NOT debris recordings -> the debris-specific `effectiveMaxSampleInterval` cap still does not apply to them. The new attitude-only minimum is a separate recurrence-prevention path for tumbling parents and does not change the velocity/proximity cadence for non-rotating parents. No conflict.

## 2. Risk surface

- **`DebrisRelativePlaybackPolicy` contract:** v4 extends the "should disappear" semantics in the policy file's XML comment as part of the same PR. Single-file, single-helper internal change. Acceptable in-PR contract evolution.
- **Parent rotation slerp sites covered** (878, 1074). Site 878 has narrow coverage by design (small bracket span); documented. Site 1157 is child-relative rotation and is explicitly not a parent-rotation gate.
- **`ParsekFlight.cs:21688`** is debris's own rotation interp, not the parent's. Different concern, out of scope.
- **PR #787 interaction:** verified clean (parent isn't debris; cap doesn't apply).
- **Reaction-wheel false-positive risk:** addressed by AND with 50m offset filter + child+anchor hysteresis. Small probes don't have large offsets.
- **Resolver harness scenarios 4, 5, 7:** static parent rotation -> delta-theta = 0 deg -> gate doesn't fire. Baselines preserved. New harness scenario 11 added with explicit tumbling case.
- **Watch mode:** SetActive(false) for several seconds during parent's tumble. `WatchModeController.cs` checks `state.ghost == null` not `activeSelf` (verified for Bug 1 plan, applies here too), so the hide gate must emit the explicit ExitWatch camera action when it hides the currently watched debris. Implementation mirrors the existing parent-anchored coverage-retire path.
- **Hysteresis state lifetime:** cleanup hooks specified in section 1.5.
- **`BackgroundVesselState` is in-memory only** (verified). New fields are safe.
- **Smoothing/co-bubble/spline pipelines** consume engine output. Hidden ghost = no positioning = smoothing skips frame. Verified.
- **Map presence:** does not invoke `InterpolateAndPositionRecordedRelative`. Unaffected.
- **Bug587StripPreExistingDebris / ERS / ELS:** read-only flag and pure predicates. No mutations.

## 3. Test coverage

xUnit, `[Collection("Sequential")]` only on shared-state classes:

1. **`TumblingParentInterpolationGateTests.cs`** -- focused predicate and hysteresis coverage, including value-type dictionary-key equality/hash behavior and playback-scope isolation.
2. **`RelativeAnchorResolverTests.cs` additions** -- large-offset tumbling parent returns an unreliable decision; small-offset sibling remains reliable; exact waypoint playback does not hide; recursive relative-frame parent rotation interpolation is gated.
3. **`BackgroundAttitudeSamplingTests.cs`** -- attitude minimum-interval helper coverage for normal and high-fidelity background sampling. The live Unity vessel loop remains runtime-only.
4. **`GhostPlaybackEngineTests.cs` / `FlightPlaybackExplainabilityTests.cs` additions** -- loop-positioning signature coverage and frame-summary skip-counter coverage for `anchorRotationUnreliable`.
5. **Runtime follow-up candidate** -- an in-game canary can still synthesize a tumbling-parent + large-offset debris pair and assert hidden/visible windows, but this PR keeps the fixture cost out of the mod assembly.

## 4. Logging additions

- `[Engine]` aggregate per-frame skip-summary: add `anchorRotationUnreliable=N` field.
- Wire the aggregate counter all the way through `GhostPlaybackSkipReason.AnchorRotationUnreliable`, `CountFrameSkip`, `GhostPlaybackFrameCounters`, `ShouldEmitFrameSummary`, `BuildCurrentFrameCounters`, and `BuildFrameSummaryMessage`.
- `[Anchor]` Info one-shot per child+anchor+playback-scope key: `anchor-rotation-interp-hold-engaged: recording=... anchorRecordingId=... scope=... bracketDeg=... rateDegPerSec=... offsetMeters=...`.
- `[Anchor]` symmetric Info on transition out: `anchor-rotation-interp-hold-released: recording=... anchorRecordingId=... scope=... heldFrames=... bracketDeg=... rateDegPerSec=... offsetMeters=...`.
- `[GhostRenderTrace] phase=GuardSkip reason=anchor-rotation-unreliable` (existing infra).

## 5. Documentation updates

Same commit:

- `CHANGELOG.md` under `0.9.2 / Bug Fixes` -- trimmed per memory rule: "Fixed chaotic translation for parent-anchored debris when the recorded parent tumbles faster than its rotation samples can safely interpolate. Future tumbling parents now receive attitude-triggered BG samples."
- `docs/dev/todo-and-known-bugs.md`: NEW top-level `## Done - Debris ghost sustained chaos when parent vessel tumbles`. Cross-reference the existing initial-position-slide entry. Note this is "separate from Re-Fly post-load settle bug" so reviewers see the distinction.
- `Source/Parsek/DebrisRelativePlaybackPolicy.cs` XML comment update (lines 14-22): add a sentence -- "Also disappears when the parent's rotation samples are too sparse to reliably interpolate at the playback UT, with debris offset large enough to amplify the interpolation error into a visible discontinuity."

## 6. PR scope and Bug 1 conflict surface

Files this PR touches:

- `Source/Parsek/TumblingParentInterpolationGate.cs` (new -- pure predicate + thresholds + reliability decision payload + hysteresis key/state structs)
- `Source/Parsek/RelativeAnchorResolver.cs` (readonly-context `WithDebrisLocalOffsetSquaredMeters`, relative-section child-offset prepass, reliability-returning sister methods for the two parent-rotation slerp paths at 878/1074; site 1157 intentionally not gated)
- `Source/Parsek/GhostPlaybackEngine.cs` `RenderInRangeGhost` and `PositionLoopAtPlaybackUT` (invoke reliability callback at the actual positioning UT; mesh hide; FX teardown; skip counter)
- `Source/Parsek/GhostPlaybackEvents.cs` (`TryEvaluateAnchorRotationReliability`, callback field on `TrajectoryPlaybackFlags`; `AnchorRotationUnreliable` enum + log token + frame counter)
- `Source/Parsek/ParsekFlight.cs` ~14833 (`ComputePlaybackFlags` populates `tryEvaluateAnchorRotationReliability` for eligible parent-anchored debris only); child+anchor hysteresis dictionary + cleanup-hook handlers
- `Source/Parsek/BackgroundRecorder.cs` ~1937 (attitude trigger reusing `FlightRecorder.ShouldRecordAttitudePoint`); new fields on `BackgroundVesselState`
- `Source/Parsek/DebrisRelativePlaybackPolicy.cs` (XML comment update only)
- `Source/Parsek.Tests/TumblingParentInterpolationGateTests.cs` (new)
- `Source/Parsek.Tests/BackgroundAttitudeSamplingTests.cs` (new)
- `Source/Parsek.Tests/RelativeAnchorResolverTests.cs` (reliability-gate regressions)
- `Source/Parsek.Tests/GhostPlaybackEngineTests.cs` (signature/counter-adjacent updates)
- `Source/Parsek.Tests/FlightPlaybackExplainabilityTests.cs` (frame-summary counter coverage)
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`

Bug 1 (`Parsek-fix-refly-settle-anchor-jump/`) targets the post-load settle anchor-pose stability and shares 3 files with Bug 2: `ParsekFlight.cs`, `GhostPlaybackEngine.cs`, `GhostPlaybackEvents.cs`. Disjoint regions in each:

- `ParsekFlight.cs`: Bug 1 ~14833 (`ComputePlaybackFlags` populates `anchorReFlyUnstable`); Bug 2 same method populates `tryEvaluateAnchorRotationReliability` for eligible debris and owns a separate child+anchor hysteresis dictionary. Same method, adjacent additions, likely textual conflict only.
- `GhostPlaybackEngine.cs`: Bug 1 new gate after `SessionSuppressionState` reading `flags[i].anchorReFlyUnstable`; Bug 2 gates inside `RenderInRangeGhost` and `PositionLoopAtPlaybackUT` at the actual positioning UT. These are mostly different sites after the v4 loop-UT correction, but `PositionLoopAtPlaybackUT` signature churn may create mechanical callsite conflicts.
- `GhostPlaybackEvents.cs`: Bug 1 adds `anchorReFlyUnstable` field + `AnchorReFlyUnstable` enum entry; Bug 2 adds the reliability delegate, callback field, frame counter, and `AnchorRotationUnreliable` enum entry. Independent additions, but expect a normal enum/summary textual merge.

Three-way merge should be manageable either way, but expect normal textual conflicts around the shared enum/flag additions and `PositionLoopAtPlaybackUT` callsite signature churn.

`CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md`: textual conflicts but no semantic coupling. Bug 2 entry references "see also: Bug 1 (Re-Fly post-load settle anchor jump)" so reviewers see the distinction.

No invariant from PR #787, PR 3a/3b/3c, PR #785, Bug587StripPreExistingDebris, or the ERS/ELS audit is touched.
