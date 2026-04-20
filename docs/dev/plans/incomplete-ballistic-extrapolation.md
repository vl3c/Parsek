# Incomplete-Ballistic Extrapolation

## Problem

When a player exits the flight scene (to Space Center, Tracking Station, main menu, etc.) mid-flight, the recording is truncated at scene-exit UT. `FinalizeIndividualRecording(isSceneExit:true)` in `ParsekFlight.cs:6788` closes the recording off with whatever last frame was captured. On the next playback, `PositionGhostAtRecordingEndpoint` in `GhostPlaybackEngine.cs:1561` picks where to render the ghost.

The current fallback cascade is:

1. `RecordingEndpointResolver.TryGetOrbitEndpointUT` — if an orbit segment extends past the last trajectory point, propagate that orbit via `GhostExtender.PropagateOrbital`.
2. Otherwise, spawn at the last recorded trajectory point.
3. Otherwise, surface / last segment fallbacks.

**The gap:** `GhostExtender.PropagateOrbital` (`GhostExtender.cs:96-128`) bails at `ecc >= 1.0` and is only reached when the recording happens to carry an orbit segment whose `endUT` extends past the last trajectory point. For the common cases — physics-only recordings of suborbital/ballistic arcs, planes abandoned mid-air, or hyperbolic ejection trajectories — the ghost **freezes at the last recorded frame** (often mid-atmosphere). A plane at 3 km stops dead in the air. A ballistic missile at apoapsis hangs in space. A craft on a Jool-intercept trajectory spawns at the inner-Kerbol last frame rather than near Jool.

Stock KSP destroys unloaded vessels on atmosphere entry. Ghost playback should match that semantic and show the continuation arc up to the destruction moment.

## Core principles

**Ghosts are real vessels in the player's mind.** They are on real trajectories at real times, and the player expects to see where they are going — not just fragments.

**Current-SOI-only map rendering.** At any playback UT, a ghost has one "current SOI" (the body whose sphere contains it). Draw the ghost's orbit lines for **every future coast segment in the ghost's current SOI** — that is, all chain segments with `bodyName == currentSOI.bodyName` and `endUT > playbackUT`. Segments in other SOIs (past or future) are hidden; past segments in the current SOI are also hidden (clutter reduction, already current behaviour). When the ghost crosses an SOI boundary during playback, the drawn set switches to all future coast segments in the new SOI. This is an extension of the existing system: today it shows the immediate current-SOI segment up to the next SOI transition; this plan adds any **further** coast segments in the same SOI later in the chain (e.g. a post-flyby return-to-Kerbin orbit after a Mun excursion). Map camera focus is irrelevant — rendering is driven by the ghost's SOI, not the camera. This is simpler than stock's active-vessel patched-conic display (which also shows the foreign-SOI flyby arcs simultaneously), and is the right trade-off for ghosts, since the player is watching rather than planning.

**Only stable terminal states spawn.** The existing gate in `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` (`GhostPlaybackLogic.cs:3636`) already rejects every non-stable state (SubOrbital, Destroyed, Docked, Boarded, Recovered) from both RSW and the deferred spawn queue. This plan does not change that gate — it only changes what terminal state the recording ends up with after extrapolation. If the extrapolated arc terminates at atmo/ground → `Destroyed` (blocked). If it terminates at horizon in a stable orbit → `Orbiting` (spawns normally). Same rule, new inputs.

## Scope

This plan covers **only the uncovered tail** of an incomplete recording where no recorded data and no background-recorder data cover the interval `[lastCoveredUT, terminalUT]`. It does not replace, rewrite, or overlap recorded frames. Precedence:

1. Recorded flight frames (highest). Covers whatever the in-flight recorder captured — either the main active vessel or any tree child handled by `BackgroundRecorder` while flight was active.
2. **KSP patched-conic snapshot** — `vessel.patchedConicSolver`'s pre-computed chain of future patches captured at scene exit (flyby deflections, SOI captures).
3. Kepler extrapolation (this plan) for intervals past the end of the patched chain.

Parsek's `BackgroundRecorder` is an in-flight multi-vessel recorder (tree children during active flight), not a cross-scene background system. It stops when flight ends. Recordings produced by it are treated exactly the same as the main recording — a `BackgroundRecorder`-produced child recording that ends incomplete at scene exit gets the same snapshot + extrapolation treatment.

Rationale for (2): KSP's solver already computes flyby deflections and SOI captures using its authoritative patched-conics algorithm. Snapshotting that chain at scene exit gives the ghost a trajectory in map view that matches what the player saw, avoids us re-implementing flyby detection in §5 for cases where KSP already did the work, and produces stable multi-link chains. Extrapolation (3) only fires past the end of the snapshotted chain — typically for short-horizon solver cases or when the chain stops before a natural terminus.

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

### 0. Snapshot the KSP patched-conic chain at scene exit

Parsek today captures only the vessel's **current** orbit in `FlightRecorder.CreateOrbitSegmentFromVessel` (`FlightRecorder.cs:2247-2261`). There are zero references to `patchedConicSolver`, `nextPatch`, or `maxGeometryPatches` in the codebase — KSP's pre-computed flyby / capture chain is completely ignored. This plan changes that.

**Trigger points for snapshot:**

- Primary: scene-exit finalization (`FinalizeRecordingState` → new `SnapshotPatchedConicChain` call before `OrbitSegments.Add`).
- Also useful: on SOI change (`OnVesselSOIChanged`) to refresh the chain, since the newly-entered SOI re-plans downstream patches.
- Also useful: on manoeuvre node add/edit/delete, since these re-plan the chain. Cheapest observation hook is `GameEvents.onManeuverAdded` / `onManeuverRemoved` / `onManeuverNodeSelected` equivalent.

Start with scene-exit only (smallest surface area, covers the user's motivating case); add mid-recording refresh hooks in a follow-up once the storage layer is stable.

**Chain walk:**

```csharp
internal static List<OrbitSegment> SnapshotPatchedConicChain(Vessel v, int maxPatches)
{
    var result = new List<OrbitSegment>();
    var solver = v.patchedConicSolver;
    if (solver == null) return result;  // no chain available (on rails / no CommNet-equivalent check)

    // Ensure solver has computed enough patches. `maxGeometryPatches` defaults to
    // whatever the player set in settings; temporarily raise for capture.
    int savedLimit = solver.maxGeometryPatches;
    try
    {
        solver.maxGeometryPatches = Math.Max(savedLimit, maxPatches);
        solver.Update();  // re-plan with the new limit

        var patch = v.orbit;
        int guard = 0;
        while (patch != null && guard++ < maxPatches)
        {
            result.Add(new OrbitSegment {
                bodyName = patch.referenceBody.bodyName,
                startUT = patch.StartUT,
                endUT = patch.EndUT,
                semiMajorAxis = patch.semiMajorAxis,
                eccentricity = patch.eccentricity,
                inclination = patch.inclination,
                longitudeOfAscendingNode = patch.LAN,
                argumentOfPeriapsis = patch.argumentOfPeriapsis,
                meanAnomalyAtEpoch = patch.meanAnomalyAtEpoch,
                epoch = patch.epoch,
                isPredicted = true,  // new field
            });
            patch = patch.nextPatch;
        }
    }
    finally
    {
        solver.maxGeometryPatches = savedLimit;
    }
    return result;
}
```

**Settings interaction:**

- Parsek **overrides** `maxGeometryPatches` at capture time (temporarily) rather than respecting the player's display setting. The player's setting controls map-view clutter, not what's useful to snapshot. Capture target: `PatchedConicSolverCaptureLimit = 8` (tunable constant; generous enough for Kerbol → Eve → Kerbin chain returns, bounded to prevent runaway on chaotic arcs).
- Override is restored in a `finally` so an exception during `solver.Update()` can't leave the player's setting wrong.
- If in future we want a user-facing setting, expose `PatchedConicSolverCaptureLimit` in the Settings window under Recording. Not in scope now.

**Maneuver nodes — hard rule:**

Recordings capture actual traversed trajectories, never hypotheticals. An abandoned ghost executes no planned burns. The captured chain must reflect pure-coast continuation from the current state — no manoeuvre-node deltas.

Simplest implementation: walk `nextPatch` only up to (and not including) the first patch whose `patchEndTransition == Orbit.PatchTransitionType.MANEUVER`. The resulting chain is pure coast by construction. No node mutation, no restore logic, no risk of touching live player state.

If a later need arises to capture post-burn predictions (e.g. a user-opt-in "show planned path" overlay), build it as a separate capture path — do not conflate with this one.

**Concurrency / state safety:**

`solver.Update()` is synchronous and normally safe at scene-exit time (player is in flight scene, physics still running). Verify no reentrancy hazard by checking call order in `FinalizeRecordingState` — this runs during `OnSceneChangeRequested` before the scene tears down, so the solver is still valid.

### 1. Terminal state: `TerminalState.Destroyed` (existing)

`TerminalState.Destroyed` already exists in `TerminalState.cs:9` and is already gated out of the Real Spawn Control candidate list by `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` (`GhostPlaybackLogic.cs:3636`) alongside `Recovered`, `Docked`, `Boarded`, `SubOrbital`. This plan reuses the existing state — no enum change needed.

Semantically for this plan: "the vessel would not exist at the recording's nominal end UT because extrapolation predicts destruction before that UT." Assigned when an extrapolated arc crosses the body's atmosphere or ground.

**Ghost spawn behaviour for `Destroyed`:** already handled — `ShouldSpawnAtRecordingEnd` returns `(false, "terminal state Destroyed")`. The ghost is visible during `[lastCoveredUT, destructionUT]` while playing the extrapolated arc, then is removed from the scene. It never appears in Real Spawn Control, never triggers RSW spawn.

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

### 3. `ShouldExtrapolate` guard — driven by `vessel.situation`

Stock KSP already classifies each vessel's current situation, and its own unload-time behaviour matches what Parsek wants the ghost to do. Mirror it: drive the extrapolation decision from `vessel.situation` (captured at scene exit), not from altitude/speed heuristics.

| `Vessel.Situations` value | Extrapolate? | Rationale |
|---|---|---|
| `LANDED` | No | Stock holds position on unload; ghost stays put. |
| `SPLASHED` | No | Stock holds position on unload; ghost stays put. |
| `PRELAUNCH` | No | On the pad; no motion. |
| `DOCKED` | No | Different lifecycle; docked to another vessel. |
| `FLYING` | Yes | In-atmo plane or ballistic — cutoff = first atmo boundary (going up) or ground (going down). |
| `SUB_ORBITAL` | Yes | Above-atmo ballistic — cutoff = first atmo boundary. |
| `ORBITING` | Conditional | Extrapolate only if the orbit geometrically intersects atmo/ground (rare); otherwise stable, no extrapolation needed. |
| `ESCAPING` | Yes | Hyperbolic — SOI handoff to parent body per §5. |

Implementation:

```csharp
internal static bool ShouldExtrapolate(Vessel.Situations situation, Orbit orbit, CelestialBody body)
{
    switch (situation)
    {
        case Vessel.Situations.LANDED:
        case Vessel.Situations.SPLASHED:
        case Vessel.Situations.PRELAUNCH:
        case Vessel.Situations.DOCKED:
            return false;
        case Vessel.Situations.FLYING:
        case Vessel.Situations.SUB_ORBITAL:
        case Vessel.Situations.ESCAPING:
            return true;
        case Vessel.Situations.ORBITING:
            return OrbitIntersectsCutoffSurface(orbit, body);  // periapsis <= atmo/ground
        default:
            return false;
    }
}
```

Decision and input values logged at `[Extrapolator]` tag for debugging. No tunable thresholds.

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

Order matters — snapshot first, extrapolate only past the end of the snapshot:

```csharp
// Step 1: snapshot KSP's patched-conic chain.
var predicted = SnapshotPatchedConicChain(vessel, PatchedConicSolverCaptureLimit);
recording.OrbitSegments.AddRange(predicted);

// Step 2: compute the start state for extrapolation.
// If the patched chain exists, extrapolation starts at the LAST patch's endUT
// with the state vector at that UT. Otherwise start from the last recorded frame.
(Vector3d startPos, Vector3d startVel, double startUT, string startBody) =
    predicted.Count > 0
        ? StateAtPatchChainEnd(predicted)
        : StateAtLastRecordedFrame(recording);

// Step 3: decide whether extrapolation is warranted.
if (ShouldExtrapolate(startAlt, startSpeed, startVertSpeed, inferredState))
{
    var result = BallisticExtrapolator.Extrapolate(
        startPos, startVel, startUT, startBody, ExtrapolationLimits.Default);

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

If the patched-conic snapshot already terminates at atmo/ground on a body (e.g. KSP's solver predicted a Mun impact), skip extrapolation entirely — set `TerminalState.Destroyed` and `TerminalUT = lastPatch.EndUT` directly.

### 8. Integration with playback

Three touchpoints in `GhostPlaybackEngine.cs`:

1. **Extrapolated-arc rendering (ghost position).** During `[lastCoveredUT, terminalUT]`, ghost position comes from `ExtrapolatedArcs` via a new method `ExtrapolatedArcResolver.ResolveAt(double ut, List<OrbitArc> arcs)` that picks the right arc and evaluates Kepler at `ut`. This slots into the existing trajectory-evaluation fallback chain after recorded points exhaust.

2. **Destroyed-state spawn suppression.** If `terminalState == Destroyed`, the ghost's visual lifetime ends at `terminalUT` and the existing spawn gate (`ShouldSpawnAtRecordingEnd`) already blocks spawn. No new suppression code needed.

3. **Map-view rendering of predicted segments — current-SOI chain.** At each playback UT, identify the ghost's current SOI body. Among all `OrbitSegments` (recorded + `isPredicted`) and all `ExtrapolatedArcs`, draw **every segment whose `bodyName` matches the current SOI and whose `endUT > playbackUT`** — the immediate one (partial, from playback head forward to its `endUT`) plus any fully-future same-SOI segments later in the chain. Hide everything else for this ghost: segments in other SOIs (past or future), and past same-SOI segments whose `endUT <= playbackUT`.

   This extends the current system, which draws only the single current same-SOI segment up to the next SOI transition. Post-flyby return-to-same-SOI segments (e.g. post-Mun return to Kerbin orbit) are now also drawn, as multiple disjoint arcs in the same body's frame. The Mun flyby arc itself is not drawn (different SOI).

   On top of that, apply the coast-vs-thrust visibility rule: only draw the line when the playback head is on a coast segment (Kepler orbit line is cheap); during a thrust segment the ghost renders as icon only and no line is drawn — point-based custom-curve reconstruction is too expensive to render continuously per-frame per-ghost.

   When the ghost crosses an SOI boundary during playback, the drawn set switches from all same-old-SOI future segments to all same-new-SOI future segments automatically. Map camera focus does not affect the rendering — only the ghost's current SOI does.

### 9. Attitude during extrapolation

Freeze last captured orientation. No tumble simulation. Simple, and reads to the observer as "abandoned" rather than "physically simulated." Implement by reusing the last recorded `srfRelRotation` for all frames in `[lastCoveredUT, terminalUT]`.

### 10. New serialised fields

In the recording format:

- `TerminalUT` (double) — when the recording's ghost lifetime ends (atmo/ground contact or horizon).
- `ExtrapolatedArcs` (list of `OrbitArc`) — serialise as child ConfigNodes in the `.prec` sidecar.
- `OrbitSegment.isPredicted` (bool) — marks segments captured from `patchedConicSolver` at scene exit vs segments traversed during recording. Used by playback to style/log them differently and by mid-recording refresh logic to know which segments are safe to discard when re-snapshotting.

`TerminalState` enum already has `Destroyed` — no enum change. Format version bumps for the new serialised fields; no legacy migration per the `Format v0 reset` policy in memory.

## Edge Cases

- **Hyperbolic escape to Kerbol**: SOI handoff places the ghost in Kerbol orbit. If that orbit never re-encounters a body within horizon, terminal state = `Orbiting` at horizon, spawns normally. Covered by §5.
- **Atmo re-entry from above (e.g. stable orbit decayed by drag-free extrapolation — won't happen since drag is ignored)**: not a concern. Drag-free arcs don't decay. Only relevant for arcs that were already on a descending trajectory when the recording ended.
- **Re-entry from a high Kepler arc**: if the arc dips back into atmo, cutoff fires at atmo-entry UT, terminal state = `Destroyed`. No special case.
- **Gas giants (Jool)**: `body.atmosphere == true` → cutoff at atmo boundary. No solid-surface handling needed; atmo destruction always fires first.
- **Long-range interplanetary direct impact (inner-Kerbol → Jool)**: patched-conics chain via §5 reaches Jool SOI, enters Jool frame, crosses atmo boundary, terminates with `Destroyed`. One-time cost at finalization; acceptable.
- **Never-impacts trajectory**: bounded horizon produces `Orbiting` at horizon; ghost spawns at horizon state. No runaway compute.
- **Parked plane or rover on runway**: `vessel.situation == LANDED` → skip extrapolation; recording terminates in `Landed` as today.
- **Rover mid-drive at scene exit**: `vessel.situation == LANDED` → skip extrapolation. Mirrors stock behaviour, which holds the rover in place on unload.
- **Plane abandoned at altitude**: `vessel.situation == FLYING` → extrapolate with in-atmo cutoff (ground or first atmo-boundary crossing, whichever comes first).
- **Mountain impact / sea impact**: terrain sampling handles plain terrain within tens of metres; cliffs may clip briefly before despawn — accepted cosmetic per earlier discussion.
- **Body rotation during long falls**: propagation in body-inertial frame + lat/lon derivation at terminal UT handles this correctly without special casing.
- **Horizontal overshoot on plane-drop**: ghost continues arc for kilometres past realistic impact — accepted ("observer assumes residual thrust").
- **PQS unavailable for target body**: sea-level fallback; logged.
- **Recording that spans multiple bodies already (via existing orbit segments)**: extrapolation starts from the **last covered** state — whatever body and UT that was — and chains forward. Existing recorded content is untouched.
- **Very short extrapolation intervals**: rare. If `vessel.situation` is FLYING but arc trivially intersects ground within a second, extrapolator runs and produces a tiny arc — acceptable.
- **Player has `maxGeometryPatches` set low for performance**: snapshot override temporarily raises it, captures the chain, restores. Player's map-view experience unaffected.
- **Maneuver nodes present at scene exit**: `patchedConicSolver.Update()` re-plans through manoeuvre nodes, so the captured chain reflects the player's planned burns. The ghost's future trajectory matches what the player set up — good.
- **Player deletes/edits nodes after recording**: irrelevant; the snapshot was taken at scene exit and is immutable. Re-recording or mid-recording refresh (future work) would update.
- **Patched-conic chain terminates mid-space (solver ran out of patches)**: extrapolation picks up at the last patch's `endUT` and chains further via §5 until atmo/ground/horizon. Fills in where KSP's solver stopped.
- **Patched-conic chain terminates with an impact predicted by the solver**: detect via final patch's closest-approach altitude; short-circuit to `Destroyed` at that UT without running §5.

## Testing Strategy

### Unit tests (xUnit)

`Source/Parsek.Tests/BallisticExtrapolatorTests.cs`:

- `ShouldExtrapolate_Landed_ReturnsFalse`
- `ShouldExtrapolate_Splashed_ReturnsFalse`
- `ShouldExtrapolate_Prelaunch_ReturnsFalse`
- `ShouldExtrapolate_Docked_ReturnsFalse`
- `ShouldExtrapolate_Flying_ReturnsTrue`
- `ShouldExtrapolate_SubOrbital_ReturnsTrue`
- `ShouldExtrapolate_Escaping_ReturnsTrue`
- `ShouldExtrapolate_OrbitingStable_ReturnsFalse` (periapsis above atmo)
- `ShouldExtrapolate_OrbitingDecayingIntoAtmo_ReturnsTrue` (periapsis below atmo)
- `Extrapolate_InAtmoPlaneArc_TerminatesAtGroundImpact` (Kerbin, atmo → atmo boundary crossing fires first? No — in-atmo start, cutoff is atmo boundary which is above start altitude. Actually in-atmo start on an atmo body: the arc needs to cross `atmosphereDepth` going up — if it does, fine; but in-atmo plane at low altitude with no climb energy stays in atmo, eventually hits ground. So we need: for an atmo body, if the starting altitude < atmosphereDepth AND the arc's apoapsis is also < atmosphereDepth, cutoff = ground (sea level / terrain), because atmo-boundary cutoff cannot fire. The rule in §3 needs revision: cutoff is "first altitude crossing of atmoDepth going DOWN from above" OR "first ground contact if the whole arc stays below atmoDepth." Clarify in the implementation.)
- `Extrapolate_SuborbitalBallistic_TerminatesAtAtmoEntry` (Kerbin suborbital arc above atmosphere, descending back down)
- `Extrapolate_HyperbolicFromKerbin_HandsOffToKerbol` (ecc > 1 trajectory, verifies SOI handoff)
- `Extrapolate_StableOrbit_TerminatesAsOrbiting` (ecc < 1, periapsis > atmo: never crosses cutoff, stable-orbit outcome within horizon)
- `Extrapolate_NeverImpacts_HitsHorizonCap` (escape trajectory that never re-encounters a body)
- `Extrapolate_InterplanetaryJoolIntercept_ChainsToJoolAtmo` (set up a state vector in Kerbol frame on a Jool-intercept trajectory; verify terminal = Destroyed in Jool SOI)
- `Extrapolate_NonAtmoBody_TerminatesAtGround` (Mun suborbital → ground impact)
- `Extrapolate_NonAtmoBody_PQSUnavailable_UsesSeaLevel` (mock PQS-null; verify fallback + log)

Correction to §3: the cutoff rule is "the first altitude-boundary crossing the arc makes in the direction of decreasing altitude, where boundary is atmoDepth on atmo bodies and terrain/sea-level on non-atmo." Starting in-atmo on an atmo body → boundary is still atmoDepth but we're below it; arc either ascends through it and eventually descends back (terminate on descent crossing) or never reaches it (terminate on ground). The geometric test in §4 needs both surfaces checked for atmo bodies. Update implementation.

### Patched-conic snapshot tests

`Source/Parsek.Tests/PatchedConicSnapshotTests.cs`:

- `Snapshot_SingleOrbitVessel_CapturesOneSegment`
- `Snapshot_FlybyPredicted_CapturesPreAndPostFlybyPatches` (mock a solver with two patches across SOI transition)
- `Snapshot_NullSolver_ReturnsEmptyList` (on-rails / invalid state)
- `Snapshot_RestoresPlayerLimit` (verify `maxGeometryPatches` is restored even if Update() throws)
- `Snapshot_ImpactPredictedByKSP_TerminalStateIsDestroyed`
- `Snapshot_IsolatesPredictedFromTraversed` (verify `isPredicted = true` on captured segments)

KSP's `PatchedConicSolver` and `Orbit` are non-trivial to mock — consider a thin `IPatchedConicSource` interface over them for testability, or run these primarily as in-game tests. Decide during implementation.

### Log-assertion tests

Verify that each decision path logs at `[Extrapolator]` or `[PatchedSnapshot]` tag with expected data (inputs, chosen regime, terminal outcome, patch count captured).

### In-game tests (`InGameTests/RuntimeTests.cs`)

- `ExtrapolationIntegration_PlaneExitMidFlight_GhostFallsAndDespawns` — script a flight, exit at altitude, re-enter to Space Center, verify ghost is visible descending and despawns at impact UT.
- `ExtrapolationIntegration_SuborbitalExitAtApoapsis_GhostFollowsArc` — suborbital launch, exit at apoapsis, verify ghost plays full descent to atmo entry.
- `ExtrapolationIntegration_HyperbolicExit_GhostHandsOffToKerbol` — ejection burn, exit before SOI transition, verify ghost appears in Kerbol orbit after transition.
- `PatchedSnapshotIntegration_MunFlybyExit_GhostTrajectoryMatchesMapView` — set up a Mun flyby trajectory with no planned burns, exit flight before reaching Mun, verify ghost's map-view trajectory shows the same pre- and post-flyby patches the player saw.
- `PatchedSnapshotIntegration_ManeuverNodeStripped_GhostIgnoresBurn` — player places a manoeuvre node, exits before executing; verify captured chain reflects the trajectory WITHOUT the burn (ghost is abandoned, cannot execute the burn).

### Synthetic recordings

Add to `Tests/Generators/` fixtures:

- Truncated plane recording (ends mid-cruise at 3 km).
- Truncated suborbital recording (ends near apoapsis).
- Truncated hyperbolic recording (ends pre-SOI-exit).

Load each and verify finalization produces the expected terminal state and arc count.

## Migration / Compatibility

- Format version bump; no migration (per `Format v0 reset` policy).
- Existing recordings without `ExtrapolatedArcs` load fine; playback falls back to the current "spawn at last frame" behaviour — no regression for legacy data.
- Recordings with `TerminalState == Destroyed` are already filtered out of Real Spawn Control by the existing `ShouldSpawnAtRecordingEnd` gate — no RSW UI work needed.

## Locked Decisions (from design discussion 2026-04-20)

- **Q1 maneuver nodes**: snapshot walks `nextPatch` only up to (and not including) the first patch whose `patchEndTransition == MANEUVER`. Chain is pure coast by construction; no node mutation, no restore logic.
- **Q2 tracking-station / map presence for `Destroyed`**: treat as any other ghost with a shorter lifetime. `GhostMapPresence` creates the ProtoVessel at spawn, removes it at `terminalUT`. Targeting works until then.
- **Q3 RSW visibility**: no work needed. `TerminalState.Destroyed` already exists and is already in the spawn-eligibility blocklist at `GhostPlaybackLogic.cs:3636`.
- **Q4 deferred spawn queue**: no changes. Queue entry is gated by `ShouldSpawnAtRecordingEnd`, which rejects all non-stable terminal states before they reach the queue.
- **Q5 crew semantics**: Parsek never marks kerbals dead based on extrapolation. Crew state follows the real (stock KSP) vessel's fate via existing reservation reconciliation. Extrapolation is visual-only.
- **Q6 mid-recording patch refresh**: scene-exit snapshot only. No refresh on SOI change or maneuver-node edit during recording — the prediction chain has no rendering role until the recording ends.

## Locked Decisions (continued)

- **Q8 destruction visual FX**: Option A, silent disappear at atmo boundary. No FX in phase 1. Revisit only if players find the silent vanish confusing.
- **Q9 `ShouldExtrapolate` guard**: no thresholds. Decision is a pure switch on `vessel.situation` (stock KSP enum) — mirrors stock's own unload behaviour. LANDED/SPLASHED/PRELAUNCH/DOCKED skip; FLYING/SUB_ORBITAL/ESCAPING extrapolate; ORBITING extrapolates only if the orbit geometrically intersects atmo/ground.

## Open Questions (deferrable / calibration)

1. `maxHorizonYears = 50` and `PatchedConicSolverCaptureLimit = 8` — initial defaults, calibrate from playtests once the feature is live.
2. Map-view styling for `isPredicted = true` segments — dashed line, different colour, or identical to traversed? Recommend identical until there is a specific readability problem.
5. `maxHorizonYears = 50` is a guess. Calibrate against the longest realistic interplanetary missions before shipping.

## Work Breakdown

Rough phases; each lands as its own PR.

1. **Patched-conic snapshot** — `SnapshotPatchedConicChain` (scene-exit only, walk until first `MANEUVER` transition) + `OrbitSegment.isPredicted` + unit / in-game tests. Lands first because it's the cheapest real improvement and unblocks everything else.
2. `BallisticExtrapolator` module + unit tests (no integration).
3. `ExtrapolatedArcs` storage in recording + ConfigNode round-trip tests.
4. Finalization integration (hook into `FinalizeIndividualRecording`, wire snapshot → extrapolator pickup, assign `TerminalState.Destroyed` on atmo/ground hit).
5. Playback integration (arc rendering during `[lastCoveredUT, terminalUT]`, ghost despawn at `terminalUT`).
6. In-game tests + synthetic recordings.
7. Threshold calibration pass after first real-use feedback.

Note: no RSW, spawn-queue, or crew-reservation work needed — existing systems already handle `Destroyed` correctly via the `ShouldSpawnAtRecordingEnd` gate and via stock KSP crew-death events.

## References

- Investigation findings (this conversation, 2026-04-20): current freeze-at-last-frame behaviour for incomplete ballistic recordings; confirmed zero references to `patchedConicSolver` / `nextPatch` / `maxGeometryPatches` in the codebase today.
- `ParsekFlight.cs:6788` — `FinalizeIndividualRecording`.
- `ParsekFlight.cs:6983` — `InferTerminalStateFromTrajectory`.
- `FlightRecorder.cs:2247-2261` — `CreateOrbitSegmentFromVessel` (current orbit-only capture; extended by this plan).
- `FlightRecorder.cs:4683-4698` — `FinalizeRecordingState` (scene-exit entry point for snapshot).
- `GhostPlaybackEngine.cs:1561-1597` — `PositionGhostAtRecordingEndpoint`.
- `GhostExtender.cs:96-128` — `PropagateOrbital` (current ecc < 1.0 limitation).
- `RecordingEndpointResolver.cs:61-105` — orbit-endpoint resolution.
- `ParsekPlaybackPolicy.cs:140-270` — deferred spawn queue (impacted by `Destroyed` state).
- `docs/dev/plans/rsw-departure-aware-spawn-warp.md` — prior plan on spawn-time state divergence; overlapping concerns.
- KSP API: `PatchedConicSolver`, `Orbit.nextPatch`, `Orbit.patchEndTransition`, `ManeuverNode.RemoveSelf`.
