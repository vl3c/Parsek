# Plan: #450 Phase B3 — lazy reentry FX pre-warm

## Problem

Phase A's one-shot WARN from the 2026-04-18 playtest:

```
heaviestSpawn[type=recording-start-snapshot
              snapshot=0.00ms timeline=15.90ms dicts=1.28ms reentry=6.94ms
              other=0.08ms total=24.20ms]
```

Reentry FX costs **6.94 ms at spawn time** — 29 % of a single bimodal 24.2 ms
spawn. Parts of this work are paid up-front for every ghost whose trajectory
has reentry potential (has orbit segments OR any trajectory point ≥ 400 m/s),
regardless of whether the ghost ever actually renders reentry visuals in the
current session.

## What B3 does

Defer `TryBuildReentryFx` from spawn time to **the first frame the ghost is
actually near reentry conditions** — in atmosphere, below the body's
`atmosphereDepth`. Ghosts that never enter atmosphere (orbital-only, EVA,
KSC pad ghosts, showcases) never pay the build cost.

## Current behaviour (baseline)

`GhostPlaybackEngine.TryPopulateGhostVisuals` at spawn:

```csharp
if (TrajectoryMath.HasReentryPotential(traj))
{
    state.reentryFxInfo = TryBuildReentryFx(ghost, state.heatInfos, index, traj.VesselName);
    DiagnosticsState.health.reentryFxBuildsThisSession++;
}
else
{
    state.reentryFxInfo = null;
    DiagnosticsState.health.reentryFxSkippedThisSession++;
}
```

`UpdateReentryFx` is only called from two sites, both guarded by
`state.reentryFxInfo != null`. When the info is null, reentry FX simply
doesn't render — which is exactly what happens on the `skip` branch today
for vessels below the 400 m/s floor.

## Proposed design (revised after clean-context review)

### New state field

`GhostPlaybackState.reentryFxPendingBuild` (bool, defaults false).
Cleared in `ClearLoadedVisualReferences` alongside the existing
`reentryFxInfo = null` reset.

### Spawn path change

```csharp
if (TrajectoryMath.HasReentryPotential(traj))
{
    // B3: defer the build to the first in-atmosphere frame. Cost avoided:
    // part-mesh combine + ParticleSystem setup + glow-material cloning.
    state.reentryFxInfo = null;
    state.reentryFxPendingBuild = true;
    DiagnosticsState.health.reentryFxDeferredThisSession++;  // B3 telemetry
}
else
{
    state.reentryFxInfo = null;
    state.reentryFxPendingBuild = false;
    DiagnosticsState.health.reentryFxSkippedThisSession++;
    ParsekLog.VerboseRateLimited("ReentryFx", $"skip-{index}", "…", 5.0);
}
```

`reentryFxBuildsThisSession` counter moves to the lazy-build site so the
session metric continues to mean "number of FX actually built"; the new
`reentryFxDeferredThisSession` counter tracks spawns that entered the
lazy-build queue. Their difference at session end is the count of
trajectories that saved the build entirely (never entered atmosphere)
— the signal B3 uses to prove it worked.

### Lazy build lives INSIDE `UpdateReentryFx` (review finding #10)

Original plan had a separate `TryLazyBuildReentryFx` helper called
before `UpdateReentryFx` — that duplicated the `FlightGlobals.Bodies.Find`
lookup every frame. Reuse the body lookup the existing update flow
already does by placing the lazy-build branch inside `UpdateReentryFx`,
right after the body/atmosphere/altitude guards:

```csharp
internal void UpdateReentryFx(int recIdx, GhostPlaybackState state, string vesselName, float warpRate)
{
    if (state == null || state.ghost == null) return;

    // B3: widened entry condition — allow pending-but-not-yet-built ghosts
    // through so we can build lazily below.
    if (state.reentryFxInfo == null && !state.reentryFxPendingBuild) return;

    if (state.reentryFxInfo != null && GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
    {
        DriveReentryToZero(state.reentryFxInfo, recIdx, state.lastInterpolatedBodyName,
            state.lastInterpolatedAltitude, vesselName);
        return;
    }

    string bodyName = state.lastInterpolatedBodyName;
    double altitude = state.lastInterpolatedAltitude;
    if (string.IsNullOrEmpty(bodyName)) return;

    CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
    if (body == null) { /* existing "body not found" log + return */ }
    // existing no-atmosphere / above-atmosphere / NaN density paths unchanged …

    // B3: lazy build fires here — body/atmosphere/altitude all validated,
    // no duplicate Find, no duplicate NaN checks.
    if (state.reentryFxPendingBuild)
    {
        TryPerformLazyReentryBuild(recIdx, state, vesselName, body.name, altitude);
        if (state.reentryFxInfo == null) return;  // throttled, or build failed
    }

    var info = state.reentryFxInfo;
    // … rest of existing intensity/layer flow, now guaranteed info != null …
}
```

Callers at `GhostPlaybackEngine.cs:932` and `:1425` widen their guard from
`if (state.reentryFxInfo != null)` to
`if (state.reentryFxInfo != null || state.reentryFxPendingBuild)`.

### Per-frame cap (review finding #3)

`MaxLazyReentryBuildsPerFrame = 2` (const, mirrors `MaxSpawnsPerFrame`).
Without a cap, five ghosts crossing `atmosphereDepth` on the same frame
would pay `5 × 6.94 = 34.7 ms` — relocating the bimodal hitch rather than
eliminating it. With the cap, excess builds defer to the next frame.
The atmosphere-entry window is many frames wide at orbital reentry speeds
(hundreds of m/s of downward velocity relative to a ~100 km atmosphere
depth), so a 1-frame delay is invisible.

```csharp
private int frameLazyReentryBuildCount;      // reset each frame
private int frameLazyReentryBuildDeferred;   // reset each frame, diagnostic only

private void TryPerformLazyReentryBuild(
    int recIdx, GhostPlaybackState state, string vesselName,
    string bodyName, double altitude)
{
    if (frameLazyReentryBuildCount >= MaxLazyReentryBuildsPerFrame)
    {
        frameLazyReentryBuildDeferred++;
        ParsekLog.VerboseRateLimited("ReentryFx", "lazy-throttle",
            $"Lazy reentry build throttled: #{recIdx} deferred to next frame " +
            $"(used {frameLazyReentryBuildCount}/{MaxLazyReentryBuildsPerFrame})", 1.0);
        return;  // flag stays true, will retry next frame
    }

    // Guards against state that was cleared between spawn and first in-atmosphere
    // frame — heatInfos is only nulled by ClearLoadedVisualReferences (which also
    // clears our flag) or a full rebuild, but defensive.
    if (state.heatInfos == null) { state.reentryFxPendingBuild = false; return; }

    frameLazyReentryBuildCount++;
    state.reentryFxPendingBuild = false;  // one-shot: clear even if build returns null
    state.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
        state.ghost, state.heatInfos, recIdx, vesselName);
    DiagnosticsState.health.reentryFxBuildsThisSession++;

    ParsekLog.Verbose("ReentryFx",
        $"Lazy reentry build fired for ghost #{recIdx} \"{vesselName}\" — " +
        $"body={bodyName} alt={altitude:F0}m " +
        $"(deferred at spawn, built on first atmospheric frame)");
}
```

### Pure decision helper (for tests — review finding #11)

The decision logic is testable in isolation without Unity. A speed gate was
added after the post-implementation review (P1 finding) because the original
"in atmosphere" condition alone still fired the lazy build on frame 1 of
every KSC launch recording — those ghosts spawn already inside Kerbin's
atmosphere (altitude ~67 m at the pad) so the ~7 ms build cost remained
attached to the spawn hitch, just relocated from the `spawn` bucket to the
`mainLoop` bucket. The fix gates on `speed ≥ ReentryPotentialSpeedFloor`
(400 m/s, shared with `TrajectoryMath.HasReentryPotential`) — at that speed
the ghost has actually reached conditions where `ComputeReentryIntensity`
can produce non-zero output within the next few frames.

```csharp
// In GhostPlaybackLogic (pure static).
internal static bool ShouldBuildLazyReentryFx(
    bool pendingFlag, string bodyName, bool bodyHasAtmosphere,
    double altitudeMeters, double atmosphereDepthMeters,
    float surfaceSpeedMetersPerSecond, float speedFloorMetersPerSecond)
{
    if (!pendingFlag) return false;
    if (string.IsNullOrEmpty(bodyName)) return false;
    if (!bodyHasAtmosphere) return false;
    if (altitudeMeters >= atmosphereDepthMeters) return false;
    if (!(surfaceSpeedMetersPerSecond >= speedFloorMetersPerSecond)) return false;
    return true;
}
```

The `!(speed >= floor)` form intentionally pins the NaN-safe direction — a
malformed velocity suppresses the build rather than burning it. Engine's
`UpdateReentryFx` calls this with the already-resolved `body` reference
AND the computed `surfaceVel.magnitude`, so the speed reuses the same
`body.getRFrmVel` lookup the rest of `UpdateReentryFx` already pays.

### Invariants preserved

- No-reentry-potential trajectory: no build, same as pre-B3.
- Reentry-potential + enters atmosphere: same build output one frame later.
- Reentry-potential + never enters atmosphere: **no build at all — the
  cost savings**.

### Scoped-out paths (review finding #7)

`ParsekFlight.cs` preview / Gloops preview ghosts at `:7765-7775, 7865-7880`
take their own `reentryFxInfo` path and are **explicitly NOT covered by
B3**. They are short-lived UI ghosts with a different cost profile
(single-preview, user-initiated) and the bimodal spawn-burst pattern
#450 targets does not apply to them. If playtest shows they matter, file
as a separate tracked follow-up.

### Visual correctness (review finding #1 — verified)

`GhostVisualBuilder.ComputeReentryIntensity` clamps to 0 below
`AeroFxThermalStartMach` (Mach 2.5) AND below `AeroFxDensityFadeStart`
(0.0015 kg/m³). Both gates are effectively zero at the very edge of
`atmosphereDepth` — KSP's definition of `atmosphereDepth` is the altitude
where density rounds to 0. So the one-frame build delay cannot produce
visible FX loss even on ballistic meteor-style reentries. Pre-B3 behaviour
was structurally the same: `UpdateReentryFx:1782` already early-returns
when `altitude >= body.atmosphereDepth`.

### Loop-cycle rebuild interaction (review finding #8 — benign)

Flight-recording loop rebuilds (`DestroyGhost` → `SpawnGhost` on cycle
boundary, see `GhostPlaybackEngine.cs:1038`) clear `reentryFxPendingBuild`
via `ClearLoadedVisualReferences` and set it again on the next
`TryPopulateGhostVisuals`. For a ghost mid-reentry at the cycle boundary,
the next frame's `UpdateReentryFx` observes both the pending flag AND
in-atmosphere conditions → builds immediately. Cost lands on the same
frame as the old behaviour, so no cadence-hitch regression beyond what
#406's loop-cycle-rebuild-is-itself-expensive already does.

### Interaction with `TryBuildReentryFx` spawn-state assumptions (review #4/#5)

`TryBuildReentryFx` (`GhostVisualBuilder.cs:6087`) uses
`GetComponentsInChildren<Renderer>(true)` and `includeInactive=true` in
`CombineGhostMeshFilters`. Unity's `includeInactive=true` gathers inactive
GameObjects AND enabled-flag-false components, so the coverage is
resilient to both distance-LOD fidelity reduction (disabled renderers)
and part-event-driven hidden parts. Lazy build captures post-event mesh
state, which is the CURRENT physical composition of the ghost — arguably
more correct than the pre-B3 spawn-frozen version. Documented here so
the trade-off is explicit. Covered by a dedicated integration-style log
assertion in the test plan below.

### Diagnostic surfacing

The new work appears as `mainLoop` time in the playback budget breakdown
(outside `spawnStopwatch`). Correct attribution — the cost is genuinely
outside spawn. Phase A's `heaviestSpawn[reentry=…ms]` should drop toward
zero on a post-B3 playtest; if it doesn't, the fix didn't take.

Session counters in `DiagnosticsStructs.HealthCounters`:
- `reentryFxBuildsThisSession` — actual builds (moved from spawn to lazy site).
- `reentryFxSkippedThisSession` — unchanged semantic (HasReentryPotential = false at spawn).
- `reentryFxDeferredThisSession` — new: B3-deferred at spawn OR at each rehydrate. Counted per build-event, NOT per unique trajectory — a ghost that unloads and rehydrates N times before ever reaching atmosphere contributes N.
- `buildsAvoided` = `deferred − builds` (display-only, computed in the report): number of build EVENTS that would have fired pre-B3 but didn't post-B3. Renamed from `neverBuilt` because that label implied unique-trajectory semantics, which is not what the subtraction measures.

## Files touched

- `Source/Parsek/GhostPlaybackState.cs` — new `reentryFxPendingBuild`
  bool; reset in `ClearLoadedVisualReferences`.
- `Source/Parsek/GhostPlaybackEngine.cs` — spawn-path defer, per-frame
  `frameLazyReentryBuildCount` + `frameLazyReentryBuildDeferred` fields,
  `MaxLazyReentryBuildsPerFrame` const, `TryPerformLazyReentryBuild`
  helper, widened `UpdateReentryFx` entry condition, widened call-site
  guards at lines 932 and 1425.
- `Source/Parsek/GhostPlaybackLogic.cs` — pure `ShouldBuildLazyReentryFx`
  decision helper (internal static, testable in isolation).
- `Source/Parsek/Diagnostics/DiagnosticsStructs.cs` — new
  `reentryFxDeferredThisSession` field on `HealthCounters`; reset in
  `HealthCounters.Reset`.
- `Source/Parsek.Tests/Bug450B3LazyReentryTests.cs` (new) — unit tests.
- `docs/dev/todo-and-known-bugs.md` — extend #450 entry: B3 shipped,
  B2 next.
- `docs/dev/done/plan-450-b3-lazy-reentry.md` (this file).

## Tests

### Pure decision logic (ShouldBuildLazyReentryFx)

Exercises the 5-input helper directly:

1. `NotPending_ReturnsFalse` — `pendingFlag=false` → never build.
2. `NoBodyName_ReturnsFalse` — `bodyName=null` and `""` → not ready yet.
3. `BodyHasNoAtmosphere_ReturnsFalse` — `hasAtmosphere=false` → never build
   (covers Mun / Minmus / asteroids).
4. `AboveAtmosphereDepth_ReturnsFalse` — `altitude = depth + 1` → still in space.
5. `BelowAtmosphereDepth_ReturnsTrue` — the positive case.
6. `JustBelowDepth_ReturnsTrue` — boundary at `depth − 1m`.
7. `ExactlyAtDepth_ReturnsFalse` — pins the `>=` sign on the altitude check.
8. `NegativeAltitude_ReturnsTrue` — below-surface altitude (reload artifact
   on a landed ghost) still inside atmosphere; defensive.

### Engine integration

9. `LazyBuild_Idempotent_WhenFlagAlreadyCleared` — call
   `TryPerformLazyReentryBuild` with `pendingFlag=false`, assert no log
   line emits and no build is attempted.
10. `LazyBuild_PerFrameCap_Throttles` — call the helper twice over the cap
    without a frame reset; assert the second call increments
    `frameLazyReentryBuildDeferred` and emits the "Lazy reentry build
    throttled" verbose line.
11. `LazyBuild_PerFrameCap_ResetsOnNextFrame` — reset counters (simulating
    next `UpdatePlayback` tick), call again, assert it fires.
12. `LazyBuild_FiresLogLine` — positive case log assertion: verbose line
    `"Lazy reentry build fired for ghost #N"` appears when conditions are
    met (verifies B3 telemetry did not silently regress).
13. `LazyBuild_ClearsPendingFlag_EvenOnBuildFailure` — the flag clears
    after one attempt regardless of whether `TryBuildReentryFx` returns
    null, so a failing build doesn't retry every frame.

### Session counter semantics

14. `SpawnPath_DeferredCounterIncrements` — reentry-potential spawn
    increments `reentryFxDeferredThisSession`, not `reentryFxBuildsThisSession`.
15. `LazyBuild_BuildsCounterIncrements` — a lazy build fires, asserts
    `reentryFxBuildsThisSession` increments by exactly 1.

### In-game test (`RuntimeTests.cs` — live KSP only)

16. Spawn a synthetic reentry-capable ghost via the test fixture above
    `atmosphereDepth`; assert `reentryFxInfo == null` and
    `reentryFxPendingBuild == true`. Move the ghost below
    `atmosphereDepth`, drive one playback frame, assert
    `reentryFxInfo != null` and `reentryFxPendingBuild == false`.

17. Spawn a reentry-capable ghost, apply a decouple part event (hides a
    part), then drop into atmosphere. Assert the build succeeds (null
    check only — verifying spawn-state assumptions from review #4 don't
    crash on a decoupled ghost). The visual correctness of post-event
    mesh coverage is the deliberate trade-off documented above.

## Rollout / risk

- Zero behaviour change for ghosts already in atmosphere at spawn (the
  lazy-build fires on the very next playback frame, indistinguishable
  from spawn-time build in wall clock).
- Single-frame delay of FX setup on first atmospheric entry — invisible
  because intensity is ~0 at the atmosphere boundary.
- Orbital-only, pad-showcase, and sub-400 m/s trajectories save the full
  6.94 ms each.
- On a realistic save with many orbital ghosts the savings compound
  significantly — one of the reasons this branch is the right first
  Phase B step.

## Post-ship validation

Next playtest: the #450 one-shot breakdown should show
`heaviestSpawn[reentry=…ms]` drop to ≤ 1 ms (or 0) for most spawns. If
the dominant `timeline` bucket still ≥ 15 ms, B2 (coroutine split inside
`BuildTimelineGhostFromSnapshot`) is the follow-up.
