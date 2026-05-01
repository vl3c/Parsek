# Fix landing-ghost terrain clip via tail-lift

## Problem

When a recording's terminal trajectory ends on a body's surface (Landed / Splashed / Recovered) and the player watches the ghost in a later session, KSP's procedural terrain at the same lat/lon may have shifted up or down by tens of metres because of LOD / quality differences. Two existing fixes do not cover the descent:

1. **Phase 7** (`feat/terrain-correction-phase7`, PR #660) clamps `SurfaceMobile` points to `currentTerrain + recordedGroundClearance` per point. SurfaceMobile is the only environment where the recorder writes a finite clearance.
2. **PR #702** extends the recorder to also write clearance on `SurfaceStationary` sections (new recordings only), and lifts the **endpoint hold** for Landed / Splashed / Recovered + SurfacePos via `ApplyLandedGhostClearance`. Endpoint hold engages **after** the last trajectory point's UT.

Two gaps remain:

- **Atmospheric descent (env=0)** during normal interpolation playback renders at the recorded absolute altitude. If the new terrain has risen, the ghost flies through what is now solid mountain.
- **Legacy SurfaceStationary tails** in pre-PR-#702 recordings have NaN clearance on all but the boundary point shared with the previous SurfaceMobile section. Phase 7's interpolation hook needs **both** endpoints to carry finite clearance to fire, so the SurfaceStationary section between non-boundary points falls through to recorded altitude.

The visible failure mode: ghost clips through the mountain during descent, then snaps up to the correct ground at the moment endpoint hold engages — a one-frame "pop."

Reproducer: a Kerbal X Probe descent recorded landing on a mountainside (`saves/s21/Parsek/Recordings/c6262fb1*` UT 681–696 + `f94bc871*` UT 696–708 in `logs/2026-05-01_1545_optimizer-merge-investigation/`). The atmospheric (env=0) section in `c6262fb1` has no `clearance =` lines, and the env=4 section in `f94bc871*` has clearance only on the first (boundary) point — those are the points that clip during playback.

## Goal

Make the final descent of a landed/splashed/recovered ghost end on the new terrain at the recorded landing lat/lon, with no visible boundary pop. Keep the change minimal: one new file's worth of pure helpers, one cache, one wrap of the existing Phase 7 hook. Activate only when needed.

## Out of scope

- Mid-flight low-altitude flybys that clip through hills not at the terminal lat/lon (would need per-point probing and per-point lift; cost-vs-benefit unclear).
- Trajectories whose terminal is anything other than `Landed` / `Splashed` / `Recovered`.
- Recording-time changes — this is purely a rendering fix using existing recording data (`TerrainHeightAtEnd`, last-point lat/lon/UT, terminal state).
- Mod-version bumps, recording format changes, persistence-format changes.

## Solution: tail-lift

For each ghost recording whose terminal is Landed / Splashed / Recovered:

1. At first render of the ghost in the current scene, sample the **current** PQS terrain height at the recording's terminal lat/lon (the lat/lon of the last `TrajectoryPoint`). Subtract the recorded `TerrainHeightAtEnd` to get `Δ` (signed). If `|Δ| < 2 m`, do nothing.
2. Define a linear ramp:
   - `0` lift at `rampStartUT = terminalUT − rampSeconds` (default 30 s).
   - `Δ` lift at `terminalUT`.
   - Linear in between.
3. At each render frame for that recording, **only when `recordedGroundClearance` is NaN** (i.e. Phase 7 didn't apply), add `ramp(point.ut) · Δ` to the altitude returned by `ResolvePhase7EffectiveAltitude`. SurfaceMobile / SurfaceStationary (post-#702) points keep Phase 7's per-point `terrain + clearance` as authoritative — at the terminal point the two paths converge to the same altitude (`currentTerrain + clearance ≈ recordedAlt + Δ`), so the handoff is seamless and the endpoint-hold "pop" goes away.

Why this beats per-point probing: a single PQS sample per recording per scene + per-frame arithmetic, instead of a per-frame TerrainAltitude call per ghost. TerrainCacheBuckets makes the PQS sample O(1) anyway, so even the one-shot lookup is cheap.

Cost: one PQS lookup per recording per scene (cached in `TerrainCacheBuckets`), then 5 floating-point ops per frame per ghost. Zero allocations on the hot path.

## Files to change

### New

- **`Source/Parsek/TerrainCorrector.cs`** (extend, don't replace existing helpers)

  Add:
  - `internal readonly struct TailLiftPlan` — `Active`, `TerminalUT`, `RampStartUT`, `TerrainDelta`, plus `Inactive` static factory. `default` of the struct is the inactive sentinel.
  - `internal static TailLiftPlan BuildTailLiftPlan(TerminalState? terminalState, double terrainHeightAtEnd, double currentTerrainAtEnd, double terminalUT, double rampSeconds, double minDeltaMeters)` — pure decision; returns `Inactive` when terminal isn't surface, when either terrain is NaN, when `|Δ| < minDeltaMeters`, or when `rampSeconds <= 0`.
  - `internal static double EvaluateTailLift(double pointUT, in TailLiftPlan plan)` — pure ramp; returns 0 if inactive or `pointUT <= rampStart`, `Δ` if `pointUT >= terminalUT`, linear in between.
  - Constants: `internal const double DefaultTailLiftRampSeconds = 30.0;` and `internal const double TailLiftMinAbsDeltaMeters = 2.0;`.

### Modified

- **`Source/Parsek/ParsekFlight.cs`** (host-scene positioner)

  - Add a private cache field `private readonly Dictionary<string, TailLiftPlan> tailLiftPlanCache = new Dictionary<string, TailLiftPlan>();`. Keyed by `IPlaybackTrajectory.RecordingId`.
  - Add `internal void InvalidateTailLiftPlanCache()` — clears the cache. Call from the same site that drives `TerrainCacheBuckets.Clear()` (scene transition); grep for that call site and mirror it.
  - Add `private TailLiftPlan ResolveTailLiftPlan(IPlaybackTrajectory traj, CelestialBody body)`:
    - Returns `Inactive` if `traj` / `traj.Points` / `body` is null/empty or `traj.RecordingId` is null/empty.
    - Cache hit → return cached.
    - Cache miss → take `lastPt = traj.Points[traj.Points.Count - 1]`, call `TerrainCacheBuckets.GetCachedSurfaceHeight(body, lastPt.latitude, lastPt.longitude)`, build via `TerrainCorrector.BuildTailLiftPlan(...)`, store, log once with `[Pipeline-Terrain]` (Verbose when `Active`, VerboseRateLimited when inactive — keyed by recordingId).
  - Add a thin compositor:
    ```csharp
    private double ResolveEffectiveAltitudeWithTailLift(
        CelestialBody body, double latitude, double longitude,
        double recordedAltitude, double recordedGroundClearance,
        ReferenceFrame referenceFrame,
        double pointUT, IPlaybackTrajectory traj)
    ```
    Returns `ResolvePhase7EffectiveAltitude(...)` plus, **only when `recordedGroundClearance` is NaN and `referenceFrame == Absolute`**, `EvaluateTailLift(pointUT, ResolveTailLiftPlan(traj, body))`.
  - Replace each of the four `ResolvePhase7EffectiveAltitude` call sites with `ResolveEffectiveAltitudeWithTailLift` and thread `point.ut` (or the interpolation endpoint UT) and the in-scope `traj` (or `null` from BG callers — see below). Existing `ResolvePhase7EffectiveAltitude` stays as-is — it remains the pure renderer-altitude helper.
  - For call sites that don't have `IPlaybackTrajectory` in scope (e.g. background ghost paths in `InterpolateAndPosition` overloads taking a `List<TrajectoryPoint>` directly), call `ResolvePhase7EffectiveAltitude` unchanged. Those paths don't render terminal-tail descents through atmosphere.

- **`Source/Parsek.Tests/`** — add `TerrainCorrectorTailLiftTests.cs`:
  - Pure tests for `BuildTailLiftPlan`: terminal in/out of allowed set, NaN guards, sub-threshold delta returns inactive, finite delta yields active plan with correct ramp endpoints.
  - Pure tests for `EvaluateTailLift`: before ramp = 0, at ramp end = Δ, midpoint = Δ/2, inactive plan = 0, zero-span ramp returns Δ at/after terminal.

- **No change** to `IPlaybackTrajectory`, `Recording`, `TrajectoryPoint`, codecs, recording format version, save/load.

## Logging

All new logs under tag `[Pipeline-Terrain]`. Pattern:

- One-shot per `(recordingId, plan-resolution)`:
  ```
  [Pipeline-Terrain] TailLift active: rec=<id> vessel='<name>' delta=+12.3m
  terminalUT=707.7 rampSec=30.0 (recTerrain=3050.7 curTerrain=3063.0)
  ```
- VerboseRateLimited (30s) when inactive due to guard:
  ```
  [Pipeline-Terrain] TailLift inactive: rec=<id> vessel='<name>'
  reason=delta-below-threshold delta=0.4m
  ```
  — emit one line per distinct skip-reason key per recording.

Do not log per-frame altitude deltas. The Phase 7 `Pipeline-Terrain frame summary` line is the existing per-frame breadcrumb; tail-lift can stay invisible at frame granularity once a plan is cached.

## Cache invalidation

`TerrainCacheBuckets.Clear()` runs on scene transition. The tail-lift cache must clear at the same moment so a fresh PQS sample is taken in the new scene. Two minimal options:

1. Call `InvalidateTailLiftPlanCache()` from the same `OnSceneChangeRequested` / equivalent hook that already invokes `TerrainCacheBuckets.Clear()`. Grep for `TerrainCacheBuckets.Clear()` in `Source/Parsek/` and mirror the call site.
2. (Simpler but less precise) On every cache miss in `ResolveTailLiftPlan`, also recompute. Already covered — cache misses naturally produce a fresh plan. Active-vessel motion within one scene won't normally invalidate the terminal lat/lon's terrain; if it does, accept one frame of stale lift until the next scene transition.

Pick option 1 — it's two extra lines and matches existing semantics.

## Tests

- xUnit (pure):
  - `BuildTailLiftPlan_ReturnsInactive_OnNonSurfaceTerminal` — terminal=Destroyed → Inactive.
  - `BuildTailLiftPlan_ReturnsInactive_OnNaNTerrain` — recorded NaN → Inactive; current NaN → Inactive.
  - `BuildTailLiftPlan_ReturnsInactive_OnSmallDelta` — Δ=1.0, threshold=2.0 → Inactive.
  - `BuildTailLiftPlan_ReturnsActive_WithCorrectRampEndpoints` — Δ=12, rampSec=30, terminal=100 → Active, RampStartUT=70.
  - `EvaluateTailLift_BeforeRamp_ReturnsZero`
  - `EvaluateTailLift_AfterTerminal_ReturnsDelta`
  - `EvaluateTailLift_AtMidpoint_ReturnsHalfDelta`
  - `EvaluateTailLift_InactivePlan_ReturnsZero`
  - Optional log-capture test for the active/inactive `[Pipeline-Terrain]` lines using `ParsekLog.TestSinkForTesting` per the project pattern.
- In-game (`InGameTests/RuntimeTests.cs`): not required for v1 — pure-helper coverage plus the existing Phase 7 in-game tests already exercise the surrounding render path.

## Risks

- **Other recordings sharing the same RecordingId across scenes**: the cache key is the recording id, which is stable. Scene transition clears the cache. OK.
- **NaN propagation**: `BuildTailLiftPlan` returns Inactive on any NaN input; `EvaluateTailLift` short-circuits Inactive plans. The tail-lift addition is `+0` for inactive plans — no NaN can reach `body.GetWorldSurfacePosition`.
- **Overshoot at boundary into SurfaceMobile**: at the last atmospheric point the lift = Δ. The first SurfaceMobile point (Phase 7) renders at `currentTerrain + clearance`. If those don't match within a few metres there will be a visible step. Verify by spot-check during in-game playtest at a recording with finite Δ; if the step is visible, narrow the threshold (e.g. trigger only when |Δ| ≥ 5 m) or extend the ramp into the SurfaceMobile section. Defer until observed.
- **Loop playback**: looped recordings replay through the tail repeatedly. The plan is stable per scene → consistent across loops. OK.

## Acceptance

- A recording whose `TerrainHeightAtEnd` differs from the live PQS terrain at its terminal lat/lon by ≥ 2 m, played in a fresh scene, no longer shows the descent passing through risen terrain in the final 30 s; the ghost meets the new ground roughly at the recorded landing lat/lon, and there is no visible altitude pop at the endpoint-hold boundary.
- `dotnet test` passes including the new `TerrainCorrectorTailLiftTests`.
- KSP.log under the reproducer save shows one `[Pipeline-Terrain] TailLift active: …` line per affected recording on Watch entry, no per-frame spam.
- Phase 7 in-game tests still pass. PR #702's `Recovered + SurfacePos` endpoint-hold clamp keeps working — the tail-lift composes with it, doesn't replace it.
