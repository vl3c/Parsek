# Orbit / Ghost Correctness Fix Plan

Worker A scope: stale ghost/map orbit seeds plus the tail-orbit state-vector sibling call-site audit. This is a plan only; no production or test code has been changed.

## Worktree

- Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-orbit-ghost-correctness`
- Branch: `fix-orbit-ghost-correctness`
- Base: `origin/main` at `c84010d8` (`Merge pull request #818 from vl3c/fix-tail-orbit-state-vector-frame`)

## Evidence

### Retained logs

- `logs/2026-05-10_2123/KSP.log`
  - `rec_f1363fc127ab47a28812ce4be6515453` starts as an in-place Re-Fly continuation at UT ~135.7.
  - Ghost map creation chooses the stale early orbit segment:
    - `map-presence-initial-create` at line 88992: `source=Segment`, `segmentUT=135.7-136.3`, `sma=512941`, `world=(-98165.0,169.1,-26872.9)`.
    - `create-segment-done` at line 88997: same source/segment, ghost pid `721691038`.
    - `update-segment` at line 96966 later switches to segment `142.2-415.0`, still `sma=512941`, not the trajectory-tail orbit.
  - Spawn-side tail resolution reached a later tail frame than the segment seed, but the retained pre-fix log still used the bad frame-mismatched reseed:
    - line 101040: `Tail-derived terminal orbit: rec=rec_f136... body=Kerbin tailUT=453.66 sma=567357.1 ecc=0.7149` (incorrect; expected corrected tail orbit is `sma≈4_547_677`, `ecc≈0.822`).
    - line 101256: later retry rejects only because `spawnUT=559.25` exceeds the 30s rotation-drift guard.
  - The saved sidecar confirms the map seed source:
    - `logs/2026-05-10_2123/saves/s15/Parsek/Recordings/rec_f136...prec.txt`
    - first `ORBIT_SEGMENT`: `startUT=135.66307052612405`, `endUT=136.34307052612439`, `sma=512940.98893225839`, `epoch=135.30307052612386`.
    - second `ORBIT_SEGMENT`: `startUT=142.16307052612737`, `endUT=415.02214255412281`, `sma=512941.00660478976`, `epoch=142.16307052612737`.
    - tail frames continue through `endUT=453.66214255408767`; the terminal metadata in `persistent.sfs` has `terminalState=0`, `endpointPhase=2`, `endpointBodyName=Kerbin`.

- `logs/2026-05-10_1713/KSP.log`
  - Confirms the pre-PR #818 raw state-vector residual class:
    - line 14955: `Predicted orbit-tail reseeded ... rawMeters=848.67 residualMeters=670611.60`.
    - line 20626: `Predicted orbit-tail reseeded ... rawMeters=8.74 residualMeters=665982.58`.
  - GhostMap state-vector create/update was not recently exercised in either target log. The GhostMap entries are segment-based or state-vector-threshold/no-state-vector-point skips; there are no `create-state-vector-done` / `update-state-vector` hits in the target cases.

### Code audit

- `Source/Parsek/OrbitReseed.cs`
  - New helper on `origin/main` documents KSP's `UpdateFromStateVectors` contract:
    - `FromLatLonAltAndRecordedVelocity`: body-fixed lat/lon/alt + recorder Y-up velocity -> `(pos - body.position).xzy`, `vel.xzy`.
    - `FromWorldPosAndZupVelocity`: world Y-up position + already-Zup velocity -> `(pos - body.position).xzy`, velocity passthrough.

- `Source/Parsek/VesselSpawner.cs`
  - `TryDeriveTerminalOrbitSeedFromTrajectoryTail` already uses `OrbitReseed.FromLatLonAltAndRecordedVelocity`, so PR #818 fixed the spawn-tail path.
  - `TryBuildRecordedTerminalOrbitForSpawn` can prefer the tail-derived seed before endpoint-aligned stored segments.
  - Remaining raw sibling call sites:
    - `ValidateSpawnSnapshot` fallback around `orbit.UpdateFromStateVectors(worldPos, velocity, body, currentUT)` uses endpoint lat/lon/alt + `TrajectoryPoint.velocity`; this should route through `OrbitReseed.FromLatLonAltAndRecordedVelocity` if retained.
    - `SpawnAtPosition` path around `orbit.UpdateFromStateVectors(worldPos, velocity, body, ut)` needs a per-call-site velocity-frame audit before changing.

- `Source/Parsek/GhostMapPresence.cs`
  - `TryResolveTrackingStationGhostSource` prefers any active `OrbitSegment` before state-vector/terminal fallback. For `rec_f136...`, this locks map presence to the stale early checkpoint path.
  - The current terminal fallback path cannot fix that by only returning a synthetic terminal `OrbitSegment`: `TryResolveTrackingStationGhostSource` can populate `segment` for `TerminalOrbit` at `GhostMapPresence.cs:3979`, but `CreateGhostVesselFromSource` ignores that `segment` for `TerminalOrbit` and calls `CreateGhostVesselForRecording` at `GhostMapPresence.cs:4542`.
  - `CreateGhostVesselForRecording` then recomputes the seed through `BuildAndLoadGhostProtoVessel` / `TryResolveGhostProtoOrbitSeed` at `GhostMapPresence.cs:6964`, and `TryResolveGhostProtoOrbitSeed` calls `RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed` first at `GhostMapPresence.cs:7056`. Therefore a plan that only changes terminal source resolution is incomplete; the selected tail seed must be represented in the source returned to the dispatcher or explicitly threaded through terminal creation.
  - `TryResolveGhostProtoOrbitSeed` delegates to `RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed`, which currently returns the last matching `OrbitSegment` before terminal fallback. It does not know about the tail-derived seed helper.
  - `CreateGhostVesselFromStateVectors` and `UpdateGhostOrbitFromStateVectors` still call raw `UpdateFromStateVectors(worldPos, vel, body, ut)` with `TrajectoryPoint.velocity`; once exercised, they likely carry the same frame bug as the old tail finalizer.
  - `ApplyOrbitToVessel` uses `SetOrbit` for stored segments and is not part of the raw state-vector problem.

- `Source/Parsek/FlightRecorder.cs`
  - `TryCanonicalizeReFlyRecordingOrbitSegment` currently gets `rawWorld = vessel.orbit.getPositionAtUT(startUT)` and velocity from `vessel.orbit.getOrbitalVelocityAtUT(startUT)`, applies the Re-Fly world offset, then calls raw `UpdateFromStateVectors(canonicalWorld, velocity, vessel.mainBody, startUT)`.
  - This must use `OrbitReseed.FromWorldPosAndZupVelocity` for the primary orbit velocity path.
  - Its fallback to `vessel.obt_velocity` is likely Y-up recorder/world velocity, not already-Zup; the method needs explicit fallback handling rather than feeding it through the same helper silently.

- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs`
  - `TryBuildReseededPredictedTailSegmentFromRecordedAnchor` still calls raw `UpdateFromStateVectors(anchorWorld, anchorVelocity, body, anchorPoint.ut)`.
  - This directly matches the 665-671 km residuals in `2026-05-10_1713` and should use `OrbitReseed.FromLatLonAltAndRecordedVelocity`.

## Proposed fix

1. Add a shared tail-derived endpoint orbit seed resolver.
   - Preferred location: a new `OrbitSeedResolver` helper or `RecordingEndpointResolver`, not `VesselSpawner`, because `GhostMapPresence` cannot depend on spawn-only policy.
   - Inputs: `IPlaybackTrajectory traj`, resolved endpoint `CelestialBody body`, `double currentUT`, and a policy enum: `TailSeedUse.Spawn` or `TailSeedUse.MapPresence`.
   - Output shape: `TailDerivedOrbitSeed` with `Accepted`, `Segment`, `BodyName`, `TailUT`, `TailSma`, `TailEcc`, `LatestStoredSegmentEndUT`, `RotationDriftSeconds`, `DeclineReason`, `FrameSource`, `UsedAbsoluteFramesShadow`, and `UsedHistoricalBodyRotation`.
   - `TailSeedUse.Spawn` keeps the current contract from `VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail`: it derives the tail position through `body.GetWorldSurfacePosition` at call time, so it must preserve the `abs(currentUT - tailUT) <= TailDerivedOrbitMaxRotationDriftSeconds` guard.
   - `TailSeedUse.MapPresence` uses a different contract: derive the orbit at `tailUT` from a historical body-relative state vector, not from `body.GetWorldSurfacePosition` at `currentUT`. Therefore the `rec_f136...` stale case can select `EndpointTail` at `currentUT=135.7` from `tailUT=453.66` without relaxing the spawn drift guard.
   - Historical body rotation reconstruction for `TailSeedUse.MapPresence`:
     - Select the latest valid ExoBallistic absolute or `absoluteFrames` tail point.
     - Resolve body spin phase at the recorded tail UT using the same sign convention as `TrajectoryMath.FrameTransform.LiftToInertial`: `phaseDeg = initialRotationDeg + tailUT * 360.0 / rotationPeriod`. If `initialRotation` is unavailable/non-finite, use `0` and log that the absolute phase used the existing no-initial-rotation convention; if `rotationPeriod` is unavailable/non-finite/zero, decline with `historical-rotation-unavailable`.
     - Compute `inertialLon = WrapLongitude(point.longitude + phaseDeg)`.
     - Build a body-relative historical position from `(point.latitude, inertialLon, point.altitude)` without applying the body's current `bodyTransform.rotation`. Preferred implementation is a new helper such as `OrbitReseed.ComputeHistoricalBodyRelativeSurfacePositionYup(...)` that uses `CelestialBody.GetRelSurfacePosition` when available; the helper must have a pure seam that performs the equivalent spherical lat/lon/alt conversion for xUnit.
     - Feed `Orbit.UpdateFromStateVectors` with `posForUpdate = bodyRelativeHistoricalYup.xzy`, `velForUpdate = point.velocity.xzy`, body, and `tailUT`.
     - Build the synthetic `OrbitSegment` with orbital elements from that orbit, `epoch=tailUT`, `startUT=PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj)`, and `endUT=traj.EndUT` so GhostMap can create the ProtoVessel at activation UT while the orbit itself is anchored to the historical tail state.
   - Keep `VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail` as a `TailSeedUse.Spawn` wrapper or move its internals to the shared helper to avoid two policies drifting.

2. Wire terminal-tail map creation through the actual GhostMap dispatch path.
   - Add a distinct `TrackingStationGhostSource.EndpointTail` value rather than overloading `TerminalOrbit`. This is the least ambiguous path because `CreateGhostVesselFromSource` currently ignores `segment` for `TerminalOrbit`.
   - `TryResolveTrackingStationGhostSource` should return `EndpointTail` with `segment = tailSeed.Segment` when the stale-segment override predicate below accepts.
   - `CreateGhostVesselFromSource` must add a case for `EndpointTail` that calls a segment-backed creation path, not `CreateGhostVesselForRecording`. Either:
     - call `CreateGhostVesselFromSegment(recordingIndex, traj, segment)` and pass proto/source detail `endpoint-tail`, or
     - add `CreateGhostVesselFromEndpointTail(recordingIndex, traj, segment, tailSeedDiagnostics)` as a thin wrapper around `BuildAndLoadGhostProtoVessel(traj, segment, ...)`.
   - `UpdateTrackingStationGhostLifecycle` and `UpdateGhostOrbitForRecording` should treat `EndpointTail` like a segment-backed orbit for create/update/removal, with source/diagnostics preserved as `EndpointTail`.
   - If reviewers prefer not to add a new enum, the alternative must explicitly thread `(currentUT, tailSeed)` into `CreateGhostVesselForRecording` and `BuildAndLoadGhostProtoVessel`; simply returning `TerminalOrbit` with a synthetic `segment` is not sufficient.

3. Define the stale-segment override predicate exactly.
   - `endpointTailAvailable`: shared resolver accepts a tail seed for the preferred endpoint body, and `tailSeed.TailUT > latestStoredSegmentEndUT + TailDerivedOrbitFreshnessEpsilon`.
   - `terminalMapPresenceRegion`: `currentUT >= PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj)` and the recording is eligible for terminal-orbit map presence (`IsTerminalStateEligibleForTerminalOrbitMapPresence(traj.TerminalStateValue)`), with endpoint body matching the selected/tail body. This is a GhostMap source-selection region, not `currentUT >= traj.EndUT`; it covers active map ProtoVessels that are created at activation UT but should represent the authoritative endpoint orbit when the stored segments are known stale.
   - `endpointStaleSegment`: the visible segment selected at `GhostMapPresence.cs:3762` is stale iff all of these are true:
     - `endpointTailAvailable` is true.
     - The accepted tail seed came from `TailSeedUse.MapPresence` with `UsedHistoricalBodyRotation=true`; a spawn-style tail seed with `abs(currentUT - tailUT) > 30s` is not sufficient for GhostMap override.
     - The selected segment body equals the preferred endpoint body.
     - The selected segment's `endUT <= latestStoredSegmentEndUT + TailDerivedOrbitFreshnessEpsilon`.
     - The selected segment is part of the stale endpoint-segment seed family: `RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed` would currently report `Source="endpoint-segment"` for the same endpoint body, and that endpoint segment's `endUT` is older than `tailSeed.TailUT`.
     - The selected segment is not the segment containing the tail seed (`selected.startUT <= tailSeed.TailUT <= selected.endUT` must be false).
   - Do not override when the selected segment is a legitimate in-window checkpoint:
     - no newer accepted tail seed exists;
     - or the segment contains the tail seed / is the current endpoint segment;
     - or endpoint phase/body do not indicate the segment family is being used as the endpoint seed;
     - or the tail seed declines for body mismatch, non-finite vector, or `historical-rotation-unavailable`.
   - Expected log after fix for `rec_f136...`: no map ProtoVessel created from `segmentUT=135.7-136.3` after the recording sidecar contains tail frames; creation/update lines should show `source=EndpointTail`, `tailUT=453.66`, and `sma≈4_547_677` rather than the pre-fix frame-mismatched `567357`.

4. Fix raw state-vector sibling call sites.
   - `IncompleteBallisticSceneExitFinalizer.TryBuildReseededPredictedTailSegmentFromRecordedAnchor`: replace raw update with `OrbitReseed.FromLatLonAltAndRecordedVelocity`.
   - `FlightRecorder.TryCanonicalizeReFlyRecordingOrbitSegment`:
     - Primary path: `rawWorld` from `vessel.orbit.getPositionAtUT`, velocity from `getOrbitalVelocityAtUT` -> `OrbitReseed.FromWorldPosAndZupVelocity`.
     - Fallback path: if `getOrbitalVelocityAtUT` is invalid and `vessel.obt_velocity` is used, either convert through a new helper for world-position + Y-up velocity or skip canonicalization with a logged reason such as `orbit-velocity-unavailable`. Do not feed fallback velocity to the Zup helper without proof.
   - `GhostMapPresence.CreateGhostVesselFromStateVectors` / `UpdateGhostOrbitFromStateVectors`: replace raw update with the helper that matches `TrajectoryPoint.velocity` frame. For Absolute/Relative resolved points, velocity still comes from the recorded `TrajectoryPoint`, so use a new `OrbitReseed.FromWorldPosAndRecordedVelocity` or derive via lat/lon helper before relative resolution. This needs a small API choice:
     - Either add `FromWorldPosAndRecordedVelocity(Orbit, CelestialBody, Vector3d worldPosYup, Vector3d recordedVelWorldYup, double ut)` that does `(worldPos - body.position).xzy`, `vel.xzy`.
     - Or pass through lat/lon/alt for absolute points and special-case relative points with the new world-position helper.
   - `VesselSpawner.ValidateSpawnSnapshot` endpoint fallback: same `TrajectoryPoint.velocity` frame, route through `FromLatLonAltAndRecordedVelocity`.
   - Leave `TimeJumpManager` and `VesselSpawner.SpawnAtPosition` for separate audit unless their velocity source is proven to be recorded Y-up; document any retained raw call with a comment naming the velocity frame.

5. Make diagnostics part of the design, not an afterthought.
   - Extend `GhostProtoOrbitSeedDiagnostics` with fields:
     - `string Source`
     - `string EndpointBodyName`
     - `string FailureReason`
     - `string FallbackReason`
     - `bool TailSeedConsidered`
     - `bool TailSeedAccepted`
     - `string TailDeclineReason`
     - `double TailUT`
     - `double TailSma`
     - `double TailEcc`
     - `double LatestSegmentEndUT`
     - `double RotationDriftSeconds`
     - `string TailFrameSource`
   - Add one formatter, e.g. `FormatGhostProtoOrbitSeedDiagnostics(seedDiagnostics)`, used by all source-detail paths that call `TryResolveGhostProtoOrbitSeed`.
   - Update `GhostMapPresence.cs:4048` terminal source detail to include `tailConsidered`, `tailAccepted`, `tailUT`, `tailSma`, `tailEcc`, `latestSegmentEndUT`, `rotationDrift`, and `tailDeclineReason`.
   - Update `GhostMapPresence.cs:4087` / failure-detail path to include the same tail decline fields when seed resolution fails or falls back.
   - Update the source-resolution lines emitted by `TryResolveTrackingStationGhostSource`, the creation detail in `BuildAndLoadGhostProtoVessel`, and the new `EndpointTail` source-detail path so all GhostMap decision lines can prove whether the tail seed was accepted, declined, or bypassed.
   - Raw-call replacements should preserve or add one-shot `OrbitReseed` diagnostics where they would help post-hoc investigation.

## Proposed tests

- `GhostMapPresence` / tracking-station source tests:
  - Pure stale-override predicate seam: given a selected visible segment `135.7-136.3`, last matching endpoint segment ending `415.0`, accepted map tail seed `tailUT=453.66`, `UsedHistoricalBodyRotation=true`, and `currentUT=135.7`, the predicate should choose `EndpointTail`. This test must not depend on `CelestialBody`, `body.GetWorldSurfacePosition`, or live KSP transforms.
  - Map resolver stale in-window case: with a `TailSeedUse.MapPresence` resolver seam returning the historical tail seed above, source resolution should choose `EndpointTail`, not `Segment`, even though `abs(currentUT - tailUT) ~= 318s`.
  - Legitimate in-window checkpoint: recording whose selected segment contains the authoritative endpoint/tail window, or whose tail candidate is not newer than the selected/latest segment, must remain `Segment`.
  - Dispatcher wiring: when source is `EndpointTail`, `CreateGhostVesselFromSource` must call the segment-backed creation/update path and must not call `CreateGhostVesselForRecording`.
  - Terminal fallback guard: returning `TerminalOrbit` with a synthetic `segment` must be covered by a negative test or removed so future edits do not recreate the ignored-segment bug.
  - Tail older than the latest segment should not override.
  - Tail on a different body should not override.
  - Split drift tests:
    - `TailSeedUse.Spawn` with `abs(currentUT - tailUT) > 30s` should decline with `rotation-drift-out-of-bounds`.
    - `TailSeedUse.MapPresence` with the same UT gap should not evaluate the spawn drift guard; it should accept only when the historical body-relative transform succeeds, and decline with `historical-rotation-unavailable` when that transform cannot be built.
  - Diagnostics tests should assert that terminal source details at the `GhostMapPresence.cs:4048` and failure path at `:4087` equivalents include tail considered/accepted/decline fields.

- State-vector frame tests:
  - Add a pure historical body-rotation helper/test target, e.g. `OrbitReseed.ComputeHistoricalSurfaceStateVectorInputs(lat, lon, alt, recordedVelYup, rotationPeriod, initialRotation, tailUT, surfaceToBodyRelativeYup)`, asserting:
    - longitude is lifted by `initialRotation + tailUT * 360 / rotationPeriod` with the same wrap/sign convention as `TrajectoryMath.FrameTransform.LiftToInertial`;
    - position output is the historical body-relative vector `.xzy`;
    - recorded velocity output is `recordedVelYup.xzy`;
    - no `currentUT` input is needed for the map historical path.
  - Add a pure helper/test target, e.g. `OrbitReseed.ComputeRecordedVelocityStateVectorInputs(worldPosYup, bodyPositionYup, recordedVelYup)`, returning `posForUpdate=(worldPosYup - bodyPositionYup).xzy` and `velForUpdate=recordedVelYup.xzy`.
  - Add a second pure helper/test target for already-Zup velocity, e.g. `ComputeZupVelocityStateVectorInputs(worldPosYup, bodyPositionYup, velAlreadyZup)`, returning `posForUpdate=(worldPosYup - bodyPositionYup).xzy` and velocity passthrough.
  - Headless unit tests should assert the pure transform outputs with non-zero body position and asymmetric vector components. Do not rely on `CelestialBody`, `bodyTransform`, or real `Orbit` residuals in xUnit; those require Unity/KSP runtime state.
  - Keep full residual/orbit validation in runtime in-game tests under `Source/Parsek/InGameTests/RuntimeTests.cs`: predicted-tail reseed residual should be small at the anchor, GhostMap state-vector create/update should produce a sane orbit from recorded velocity, and the old 665-671 km residual class should not recur.
  - `FlightRecorder.TryCanonicalizeReFlyRecordingOrbitSegment` helper selection can be unit-tested via extracted/pure transform selection where possible; the end-to-end orbit residual belongs in an in-game test.

- Regression fixture:
  - Add a synthetic recording matching `rec_f136...`: early orbit segments, Relative/absolute tail frames to 453.66, endpoint phase `OrbitSegment`, terminal `Orbiting`, endpoint body Kerbin.
  - Assert map seed source is the tail-derived orbit and not the early `sma=512941` segment.

## Risk / rollback

- Risk: overriding active `OrbitSegment` source too broadly could break legitimate checkpoint map arcs. Guard the tail preference to endpoint/terminal map presence and require tail UT newer than segment end.
- Risk: velocity-frame assumptions are subtle. Any new helper must name the input velocity frame; do not reuse `FromWorldPosAndZupVelocity` for recorded velocities.
- Risk: the map historical path needs a correct body-rotation phase. Keep the spawn 30s guard for `TailSeedUse.Spawn`; do not apply it to `TailSeedUse.MapPresence` because that path must not depend on current body rotation. If historical rotation cannot be reconstructed, decline the map tail seed rather than falling back to a relaxed current-rotation calculation.
- Rollback: revert GhostMap tail preference independently from state-vector call-site fixes. The `OrbitReseed` helper is additive and can remain.

## Open decisions for review

- Should the shared tail-derived seed live in `RecordingEndpointResolver`, `TrajectoryMath`, or a new `OrbitSeedResolver` helper? I prefer a new helper or `RecordingEndpointResolver` to avoid making `GhostMapPresence` depend on `VesselSpawner`.
- For `FlightRecorder.TryCanonicalizeReFlyRecordingOrbitSegment`, should `vessel.obt_velocity` fallback be transformed as recorded Y-up or should canonicalization be skipped when `getOrbitalVelocityAtUT` fails? I prefer skip + explicit log unless we prove the frame.
- `EndpointTail` is no longer optional in this plan unless the implementation explicitly threads the selected tail seed through `CreateGhostVesselForRecording` / `BuildAndLoadGhostProtoVessel`. The default recommendation is a distinct `TrackingStationGhostSource.EndpointTail` enum value because it guarantees the dispatcher consumes the selected synthetic segment.
