# Incomplete-Ballistic Extrapolation

## Problem

When a player exits the flight scene (to Space Center, Tracking Station, main menu, etc.) mid-flight, the recording is truncated at scene-exit UT. `FinalizeIndividualRecording(isSceneExit:true)` in `ParsekFlight.cs:6788` closes the recording off with whatever last frame was captured. On the next playback, `PositionGhostAtRecordingEndpoint` in `GhostPlaybackEngine.cs:1561` picks where to render the ghost.

The current fallback cascade is:

1. `RecordingEndpointResolver.TryGetOrbitEndpointUT` — if an orbit segment extends past the last trajectory point, propagate that orbit via `GhostExtender.PropagateOrbital`.
2. Otherwise, spawn at the last recorded trajectory point.
3. Otherwise, surface / last segment fallbacks.

**The gap:** `GhostExtender.PropagateOrbital` (`GhostExtender.cs:96-128`) bails at `ecc >= 1.0` and is only reached when the recording happens to carry an orbit segment whose `endUT` extends past the last trajectory point. For the common cases — physics-only recordings of suborbital/ballistic arcs, planes abandoned mid-air, or hyperbolic ejection trajectories — the ghost **freezes at the last recorded frame** (often mid-atmosphere). A plane at 3 km stops dead in the air. A ballistic missile at apoapsis hangs in space. A craft on a Jool-intercept trajectory spawns at the inner-Kerbol last frame rather than near Jool.

Stock KSP destroys unloaded vessels on atmosphere entry. Ghost playback should match that semantic and show the continuation arc up to the destruction moment.

## Scope

This plan covers **only the uncovered tail** of an incomplete recording where no recorded data and no background-recorder data cover the interval `[lastCoveredUT, terminalUT]`. It does not replace, rewrite, or overlap recorded frames. Precedence:

1. Recorded flight frames (highest).
2. Background-recorder data (if enabled, for the gap it covers).
3. Kepler extrapolation (this plan) for the remaining uncovered interval.

The gap check is **per-segment**, not per-recording — if the background recorder dropped out partway or started late, extrapolate only still-uncovered sub-intervals.

## Solution

Introduce a unified "drag-free extrapolation" pass at recording finalization (`isSceneExit:true` path) that computes the natural terminal state and a continuation trajectory from the last covered UT forward. The continuation is a chain of Kepler arcs across SOIs, terminated at the first atmo/ground contact or at a bounded horizon if none occurs.

### Single propagation model

Drag-free Kepler propagation applies identically at all altitudes — an in-atmosphere plane arc at constant-g is just the low-altitude limit of a Kepler arc. One propagator, two cutoff rules:

- `body.atmosphere == true` → cutoff = first altitude crossing of `body.atmosphereDepth`.
- `body.atmosphere == false` → cutoff = first altitude crossing of terrain (or sea level if PQS unavailable).

Regime is chosen from the last-frame **state** (altitude, velocity, body), not from vessel type. A spaceplane at 65 km uses above-atmo Kepler; a rocket on final approach uses in-atmo Kepler. Same code path.

### Extrapolation outcomes

| Outcome | Trigger | Terminal State | Spawn Behavior |
|---|---|---|---|
| **Atmo entry** | Arc crosses `atmosphereDepth` on an atmospheric body | `Destroyed` (new) | Ghost plays extrapolated arc, despawns at atmo-entry UT |
| **Ground impact** | Arc crosses terrain/sea-level on a non-atmo body | `Destroyed` (new) | Ghost plays extrapolated arc, despawns at impact UT |
| **Stable orbit** | Arc circularises in a body's SOI without atmo/ground contact | `Orbiting` | Ghost spawns normally at terminal orbit |
| **Chained encounter** | Arc exits current SOI, re-enters a downstream SOI, eventually hits atmo/ground | `Destroyed` | Ghost plays chained arcs, despawns at terminal atmo/ground contact |
| **Unbounded / horizon-cap** | Arc never re-encounters a body within N years / M SOI transitions | `Orbiting` (at horizon state) | Ghost spawns normally at horizon state |

## Detailed Design

### 1. New terminal state: `TerminalState.Destroyed`

Extend the enum in `TerminalState.cs`. Semantically: "the vessel would not exist at the recording's nominal end UT because extrapolation predicts destruction before that UT." Used both for atmo-entry destruction and ground impact.

**Ghost spawn behaviour for `Destroyed`:** do not spawn a ghost at the terminal UT. The ghost is visible during `[lastCoveredUT, destructionUT]` while playing the extrapolated arc, then is removed from the scene.

### 2. New module: `BallisticExtrapolator`

Location: `Source/Parsek/Source/Parsek/BallisticExtrapolator.cs`.

Pure static methods, no ParsekFlight/GhostPlaybackEngine references — testable in isolation. Operates on state vectors `(position, velocity, UT, bodyName)` only.

```csharp
internal static class BallisticExtrapolator
{
    internal struct ExtrapolationResult
    {
        public TerminalState terminalState;      // Destroyed / Orbiting
        public double terminalUT;                // atmo/ground contact UT, or horizon UT
        public string terminalBodyName;          // body at terminal UT
        public Vector3d terminalPosition;        // body-fixed at terminalUT (for spawn)
        public Vector3d terminalVelocity;        // body-fixed at terminalUT
        public List<OrbitArc> arcs;              // one arc per SOI traversed (for playback rendering)
        public ExtrapolationFailureReason failureReason;  // none/degenerate/pqs-unavailable/etc.
    }

    internal struct OrbitArc
    {
        public string bodyName;
        public double startUT;
        public double endUT;
        public double sma, ecc, inc, argPe, lan, mna, epoch;  // Kepler elements
        public Vector3d startPos, startVel, endPos, endVel;
    }

    internal static ExtrapolationResult Extrapolate(
        Vector3d position,         // body-fixed position at lastCoveredUT
        Vector3d velocity,         // body-inertial velocity at lastCoveredUT
        double lastCoveredUT,
        string bodyName,
        ExtrapolationLimits limits);  // horizon-years, max-soi-transitions, threshold epsilons

    internal static bool ShouldExtrapolate(
        double altitude, double speed, double verticalSpeed,
        TerminalState? inferredState);  // the "don't launch parked craft" guard
}
```

### 3. `ShouldExtrapolate` guard (the "don't launch" safeguard)

Thresholds (tunable):

- If inferred state is already `Landed` or `Splashed` → **skip** extrapolation (craft is on the ground).
- If `altitude < 20 m` AND `|horizontalSpeed| < 10 m/s` AND `|verticalSpeed| < 2 m/s` → **skip** (parked / taxiing / stationary).
- If `altitude < 100 m` AND `|verticalSpeed| > -5 m/s` (not descending meaningfully) AND `|horizontalSpeed| < 40 m/s` → **skip** (low-speed ground handling — catches tumbling a post-landing rover, etc.).
- Otherwise → **extrapolate**.

Values logged at decision time with `[Extrapolator]` tag for debugging.

### 4. Propagator core

Given the start state in body-inertial frame:

1. Compute Kepler elements `(sma, ecc, inc, argPe, lan, mna, epoch)` from the state vector relative to the body.
2. Decide cutoff altitude from `body.atmosphere` and terrain sampling (§6).
3. Check geometric intersections **before** iterating:
   - If `periapsis <= cutoffAltitude + bodyRadius` → orbit geometrically intersects cutoff surface; solve for true anomaly at cutoff altitude; convert to UT.
   - Else if orbit is closed (`ecc < 1.0`) and `periapsis > cutoffAltitude + bodyRadius` → never intersects; craft remains in this SOI — check SOI escape (§5).
   - Else (hyperbolic and no cutoff intersection) → escape SOI; proceed to §5.
4. Solve `M(t) = E - e sin E` for cutoff crossing analytically (closed form from true anomaly).
5. Compute `(position, velocity)` at that UT for the terminal state.

All math operates in body-inertial frame (the frame the Kepler elements are computed in). Surface position (lat/lon) at terminal UT is derived by `body.transform.InverseTransformPoint` at terminal UT — the body rotates on its own based on UT, so the landing point is correct as the ground rotates underneath the arc.

### 5. SOI handoff (patched conics)

For hyperbolic/escape arcs and for arcs whose apoapsis crosses the SOI of a sibling or parent body:

1. Compute SOI exit UT for the current body: solve for `|r| = body.sphereOfInfluence`.
2. Transform `(position, velocity)` at SOI-exit UT into the parent body's frame (`position += body.orbit.getTruePositionAtUT(t)`, same for velocity with `orbit.GetFrameVel`).
3. Continue extrapolation with the parent body as the new central body — recursive call.
4. For each body the arc passes near, test SOI entry by checking distance to the body's position at each UT of the arc's parent-frame traversal. Coarse sampling (every T_sample) + bisection for refinement.
5. On SOI entry, transform into child body's frame and continue.

**Bounds** (via `ExtrapolationLimits`):
- `maxHorizonYears = 50` (game-UT cap; arcs that never re-encounter a body get terminated at horizon).
- `maxSoiTransitions = 8` (prevents chaotic chain explosions).
- `soiSampleStep = 3600 s` (coarse scan for SOI encounters — refined by bisection on candidate hits).

On horizon-cap: terminal state = `Orbiting`, terminal body = whatever body we're in at horizon UT, terminal position/velocity = state at horizon UT. Ghost spawns normally.

### 6. Terrain sampling (Option B with fallback)

For non-atmo bodies:

```csharp
double ResolveGroundAltitude(CelestialBody body, double lat, double lon)
{
    if (body.pqsController == null || !body.pqsController.isBuilt)
        return 0.0;  // sea-level fallback
    return body.TerrainAltitude(lat, lon);  // handles PQS internally
}
```

Two-pass solve:

1. Solve Kepler for `altitude = 0` → get candidate `(lat0, lon0, UT0)`.
2. Sample terrain at `(lat0, lon0)` → `h`.
3. Re-solve Kepler for `altitude = h` → final `(lat, lon, UT)`.

Single PQS lookup per extrapolation. Fallback to sea level silently if PQS is unavailable (unfocused body). Log `[Extrapolator] terrain lookup PQS-unavailable for {body}, falling back to sea level` when it happens.

### 7. Integration with finalization

In `ParsekFlight.FinalizeIndividualRecording(isSceneExit:true)`:

After `InferTerminalStateFromTrajectory` decides the baseline state, run a post-pass:

```csharp
if (ShouldExtrapolate(lastAlt, lastSpeed, lastVertSpeed, inferredState))
{
    var result = BallisticExtrapolator.Extrapolate(
        lastPos, lastVel, lastUT, lastBodyName, ExtrapolationLimits.Default);

    if (result.terminalState == TerminalState.Destroyed)
    {
        recording.TerminalStateValue = TerminalState.Destroyed;
        recording.TerminalUT = result.terminalUT;
        recording.ExtrapolatedArcs = result.arcs;  // new field
    }
    else if (result.terminalState == TerminalState.Orbiting)
    {
        recording.TerminalStateValue = TerminalState.Orbiting;
        recording.TerminalUT = result.terminalUT;
        recording.ExtrapolatedArcs = result.arcs;
        // TerminalOrbit* fields populated from final arc
    }
}
```

### 8. Integration with playback

Two touchpoints in `GhostPlaybackEngine.cs`:

1. **Extrapolated-arc rendering.** During `[lastCoveredUT, terminalUT]`, ghost position comes from `ExtrapolatedArcs` via a new method `ExtrapolatedArcResolver.ResolveAt(double ut, List<OrbitArc> arcs)` that picks the right arc and evaluates Kepler at `ut`. This slots into the existing trajectory-evaluation fallback chain after recorded points exhaust.

2. **Destroyed-state spawn suppression.** In `PositionGhostAtRecordingEndpoint`, if `terminalState == Destroyed`, do not spawn the ghost at `terminalUT` — the ghost's visual lifetime ends at `terminalUT`. Add a `TerminalState.Destroyed` branch that returns a "suppressed" result, and update callers (RSW, spawn queue) to treat it as "no spawn possible — ghost is gone."

### 9. Attitude during extrapolation

Freeze last captured orientation. No tumble simulation. Simple, and reads to the observer as "abandoned" rather than "physically simulated." Implement by reusing the last recorded `srfRelRotation` for all frames in `[lastCoveredUT, terminalUT]`.

### 10. New serialised fields

In the recording format:

- `TerminalUT` (double) — when extrapolation terminates (atmo/ground contact or horizon).
- `ExtrapolatedArcs` (list of `OrbitArc`) — serialise as child ConfigNodes in the `.prec` sidecar.

`TerminalState` enum gains `Destroyed`. Format version bumps; no legacy migration per the `Format v0 reset` policy in memory.

## Edge Cases

- **Hyperbolic escape to Kerbol**: SOI handoff places the ghost in Kerbol orbit. If that orbit never re-encounters a body within horizon, terminal state = `Orbiting` at horizon, spawns normally. Covered by §5.
- **Atmo re-entry from above (e.g. stable orbit decayed by drag-free extrapolation — won't happen since drag is ignored)**: not a concern. Drag-free arcs don't decay. Only relevant for arcs that were already on a descending trajectory when the recording ended.
- **Re-entry from a high Kepler arc**: if the arc dips back into atmo, cutoff fires at atmo-entry UT, terminal state = `Destroyed`. No special case.
- **Gas giants (Jool)**: `body.atmosphere == true` → cutoff at atmo boundary. No solid-surface handling needed; atmo destruction always fires first.
- **Long-range interplanetary direct impact (inner-Kerbol → Jool)**: patched-conics chain via §5 reaches Jool SOI, enters Jool frame, crosses atmo boundary, terminates with `Destroyed`. One-time cost at finalization; acceptable.
- **Never-impacts trajectory**: bounded horizon produces `Orbiting` at horizon; ghost spawns at horizon state. No runaway compute.
- **Parked plane or rover on runway**: caught by `ShouldExtrapolate` guard; extrapolation skipped, recording terminates in `Landed` state as today.
- **Mountain impact / sea impact**: terrain sampling handles plain terrain within tens of metres; cliffs may clip briefly before despawn — accepted cosmetic per earlier discussion.
- **Body rotation during long falls**: propagation in body-inertial frame + lat/lon derivation at terminal UT handles this correctly without special casing.
- **Horizontal overshoot on plane-drop**: ghost continues arc for kilometres past realistic impact — accepted ("observer assumes residual thrust").
- **PQS unavailable for target body**: sea-level fallback; logged.
- **Recording that spans multiple bodies already (via existing orbit segments)**: extrapolation starts from the **last covered** state — whatever body and UT that was — and chains forward. Existing recorded content is untouched.
- **Very short extrapolation intervals (e.g. player exits at 1 m altitude with 0.5 m/s descent)**: guard should catch with the "near-ground, low speed" branch. Boundary tuning likely needed during testing.

## Testing Strategy

### Unit tests (xUnit)

`Source/Parsek.Tests/BallisticExtrapolatorTests.cs`:

- `ShouldExtrapolate_ParkedOnRunway_ReturnsFalse`
- `ShouldExtrapolate_PlaneAtCruise_ReturnsTrue`
- `ShouldExtrapolate_AlreadyLanded_ReturnsFalse`
- `Extrapolate_InAtmoPlaneArc_TerminatesAtGroundImpact` (Kerbin, atmo → atmo boundary crossing fires first? No — in-atmo start, cutoff is atmo boundary which is above start altitude. Actually in-atmo start on an atmo body: the arc needs to cross `atmosphereDepth` going up — if it does, fine; but in-atmo plane at low altitude with no climb energy stays in atmo, eventually hits ground. So we need: for an atmo body, if the starting altitude < atmosphereDepth AND the arc's apoapsis is also < atmosphereDepth, cutoff = ground (sea level / terrain), because atmo-boundary cutoff cannot fire. The rule in §3 needs revision: cutoff is "first altitude crossing of atmoDepth going DOWN from above" OR "first ground contact if the whole arc stays below atmoDepth." Clarify in the implementation.)
- `Extrapolate_SuborbitalBallistic_TerminatesAtAtmoEntry` (Kerbin suborbital arc above atmosphere, descending back down)
- `Extrapolate_HyperbolicFromKerbin_HandsOffToKerbol` (ecc > 1 trajectory, verifies SOI handoff)
- `Extrapolate_StableOrbit_TerminatesAsOrbiting` (ecc < 1, periapsis > atmo: never crosses cutoff, stable-orbit outcome within horizon)
- `Extrapolate_NeverImpacts_HitsHorizonCap` (escape trajectory that never re-encounters a body)
- `Extrapolate_InterplanetaryJoolIntercept_ChainsToJoolAtmo` (set up a state vector in Kerbol frame on a Jool-intercept trajectory; verify terminal = Destroyed in Jool SOI)
- `Extrapolate_NonAtmoBody_TerminatesAtGround` (Mun suborbital → ground impact)
- `Extrapolate_NonAtmoBody_PQSUnavailable_UsesSeaLevel` (mock PQS-null; verify fallback + log)

Correction to §3: the cutoff rule is "the first altitude-boundary crossing the arc makes in the direction of decreasing altitude, where boundary is atmoDepth on atmo bodies and terrain/sea-level on non-atmo." Starting in-atmo on an atmo body → boundary is still atmoDepth but we're below it; arc either ascends through it and eventually descends back (terminate on descent crossing) or never reaches it (terminate on ground). The geometric test in §4 needs both surfaces checked for atmo bodies. Update implementation.

### Log-assertion tests

Verify that each decision path logs at `[Extrapolator]` tag with expected data (inputs, chosen regime, terminal outcome).

### In-game tests (`InGameTests/RuntimeTests.cs`)

- `ExtrapolationIntegration_PlaneExitMidFlight_GhostFallsAndDespawns` — script a flight, exit at altitude, re-enter to Space Center, verify ghost is visible descending and despawns at impact UT.
- `ExtrapolationIntegration_SuborbitalExitAtApoapsis_GhostFollowsArc` — suborbital launch, exit at apoapsis, verify ghost plays full descent to atmo entry.
- `ExtrapolationIntegration_HyperbolicExit_GhostHandsOffToKerbol` — ejection burn, exit before SOI transition, verify ghost appears in Kerbol orbit after transition.

### Synthetic recordings

Add to `Tests/Generators/` fixtures:

- Truncated plane recording (ends mid-cruise at 3 km).
- Truncated suborbital recording (ends near apoapsis).
- Truncated hyperbolic recording (ends pre-SOI-exit).

Load each and verify finalization produces the expected terminal state and arc count.

## Migration / Compatibility

- Format version bump; no migration (per `Format v0 reset` policy).
- Existing recordings without `ExtrapolatedArcs` load fine; playback falls back to the current "spawn at last frame" behaviour — no regression for legacy data.
- Recordings created after this change that have `TerminalState == Destroyed` cannot be "spawned" via Real Spawn Warp — RSW UI must check and surface that state explicitly ("Ghost destroyed; no spawn available").

## Open Questions

1. Should `Destroyed` ghosts be visible in Tracking Station / map view during their extrapolated arc? Current ghost-map-presence system creates ProtoVessels — worth deciding whether a transient "about to be destroyed" ghost warrants that overhead.
2. Should background-recorder integration be scoped in this plan or deferred? Current text treats background data as a pre-extrapolation source but does not modify background-recorder code. Recommend deferring.
3. How visible should the atmo-entry moment be? Optional: trigger stock re-entry FX at the destruction UT as a visual cue. Nice-to-have, not required.
4. Tunable thresholds in `ShouldExtrapolate` — expose in settings window or keep as compile-time constants? Recommend constants until user feedback indicates otherwise.
5. `maxHorizonYears = 50` is a guess. Calibrate against the longest realistic interplanetary missions before shipping.

## Work Breakdown

Rough phases; each lands as its own PR.

1. `BallisticExtrapolator` module + unit tests (no integration).
2. `TerminalState.Destroyed` enum value + serialisation.
3. `ExtrapolatedArcs` storage in recording + ConfigNode round-trip tests.
4. Finalization integration (hook into `FinalizeIndividualRecording`).
5. Playback integration (arc rendering + destroyed-state spawn suppression).
6. RSW / spawn-queue awareness of `Destroyed`.
7. In-game tests + synthetic recordings.
8. Threshold calibration pass after first real-use feedback.

## References

- Investigation findings (this conversation, 2026-04-20): current freeze-at-last-frame behaviour for incomplete ballistic recordings.
- `ParsekFlight.cs:6788` — `FinalizeIndividualRecording`.
- `ParsekFlight.cs:6983` — `InferTerminalStateFromTrajectory`.
- `GhostPlaybackEngine.cs:1561-1597` — `PositionGhostAtRecordingEndpoint`.
- `GhostExtender.cs:96-128` — `PropagateOrbital` (current ecc < 1.0 limitation).
- `RecordingEndpointResolver.cs:61-105` — orbit-endpoint resolution.
- `ParsekPlaybackPolicy.cs:140-270` — deferred spawn queue (impacted by `Destroyed` state).
- `docs/dev/plans/rsw-departure-aware-spawn-warp.md` — prior plan on spawn-time state divergence; overlapping concerns.
