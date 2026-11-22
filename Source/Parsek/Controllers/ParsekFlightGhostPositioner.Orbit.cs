using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Orbit placement and every reader of the orbit cache.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        void IGhostPositioner.PositionFromOrbit(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut)
        {
            if (state?.ghost == null || traj?.OrbitSegments == null) return;
            bool foundSegment = GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                traj, ut, out OrbitSegment seg, out int segmentIndex);
            if (!foundSegment
                && GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(
                    traj, ut, out OrbitSegment slideSegment, out int slideIndex, out double slideLastPointUT))
            {
                PositionGhostOnPredictedTailSlide(
                    index, traj, state, slideSegment, slideIndex, slideLastPointUT, ut);
                return;
            }
            if (!foundSegment)
            {
                for (int i = 0; i < traj.OrbitSegments.Count; i++)
                {
                    OrbitSegment candidate = traj.OrbitSegments[i];
                    bool inRange = i == traj.OrbitSegments.Count - 1
                        ? ut >= candidate.startUT && ut <= candidate.endUT
                        : ut >= candidate.startUT && ut < candidate.endUT;
                    if (!inRange)
                        continue;

                    seg = candidate;
                    segmentIndex = i;
                    foundSegment = true;
                    break;
                }
            }

            if (foundSegment)
            {
                if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                    return;

                if (PlaybackOrbitDiagnostics.TryBuildPlaybackPredictedTailLog(
                    index, traj, seg, ut, out string logKey, out string logMessage))
                {
                    ParsekLog.VerboseRateLimited("Playback", logKey, logMessage, 1.0);
                }

                long orbitTailCacheKey = index * 10000 + Math.Max(0, segmentIndex);
                // A loop cycle replays a ground-ending predicted tail turned with the planet
                // (owner ruling R4b), so each repeat reaches the recorded crash site.
                double planetTurnSeconds = ResolveLoopTailPlanetTurnSeconds(traj, seg, state, ut);
                // A loop cycle replays the tail under a different body rotation every cycle,
                // so its continuity offset (last body-fixed point vs the inertial conic) is
                // recomputed rather than reused from an earlier cycle.
                PositionGhostFromOrbit(
                    state.ghost,
                    seg,
                    ut,
                    orbitTailCacheKey,
                    traj.RecordingId,
                    traj,
                    cacheContinuityOffset: state.loopCycleIndex < 0,
                    planetTurnSeconds: planetTurnSeconds);
                SetOrbitTailInterpolated(state, seg, ut, orbitTailCacheKey);

                // Re-aim seam/arrival diagnostic: trace the orbit-only re-aim ghost vs the live bodies so a
                // teleport at a segment seam or an arrival where the target is NOT is visible in the log.
                if (state.ghost != null)
                {
                    // The transform read stays inside the gated arms so a
                    // tracing-off faithful ghost costs zero extra ECalls.
                    bool isReaim = traj is Parsek.Reaim.ReaimedTrajectory;
                    bool seamTracing = ParsekSettings.Current != null
                        && ParsekSettings.Current.ghostRenderTracing;
                    if (isReaim || seamTracing)
                    {
                        Vector3 ghostWorldPos = state.ghost.transform.position;
                        if (isReaim)
                            LogReaimGhostTrace(index, seg, segmentIndex, ut, ghostWorldPos);
                        // Mode-AGNOSTIC Tier-C seam-teleport instrument: unlike the
                        // [ReaimSeam] verbose trace above, this samples FAITHFUL
                        // members too, so a faithful-vs-re-aim A/B measures both
                        // modes' seam behavior with the same predicate.
                        if (seamTracing)
                            TrackLoopSeamTeleport(index, traj, seg, segmentIndex, ut, ghostWorldPos);
                    }
                }
            }
        }

        /// <summary>
        /// Orbit-tail playback positions the ghost from an orbit segment and writes no
        /// interpolation result of its own, so without this the wheel, reentry FX and watch
        /// readers would keep the last point frame's velocities. Mirrors the orbit-segment
        /// branch of the point playback: the segment's orbital velocity, and the surface
        /// velocity derived from it (source Orbit).
        /// </summary>
        private void SetOrbitTailInterpolated(
            GhostPlaybackState state, OrbitSegment seg, double ut, long cacheKey)
        {
            if (state?.ghost == null || host.orbitCache == null
                || !host.orbitCache.TryGetValue(cacheKey, out Orbit orbit) || orbit == null)
                return;
            CelestialBody body = orbit.referenceBody;
            if (body == null)
                return;
            Vector3d zupVelocity = orbit.getOrbitalVelocityAtUT(ut);
            Vector3d ghostWorldPos = state.ghost.transform.position;
            // A planet-turned placement: the surface velocity is the unturned one turned with
            // it (a rigid turn about the spin axis commutes with the rotating-frame term).
            GhostPlaybackLogic.TailPlanetTurn turn = lastOrbitPlacementTurn;
            Vector3d rFrmPos = turn.Unapply(body.position, ghostWorldPos);
            var interp = new InterpolationResult(
                (Vector3)zupVelocity, seg.bodyName, body.GetAltitude(ghostWorldPos))
                .WithSurfaceVelocity(
                    turn.ApplyVector(GhostSurfaceVelocity.FromOrbitVelocity(zupVelocity, body.getRFrmVel(rFrmPos))),
                    GhostSurfaceVelocitySource.Orbit);
            state.SetInterpolated(interp);
        }

        /// <summary>
        /// The planet turn (seconds of longitude shift) for a loop cycle's ground-ending
        /// predicted tail (owner ruling R4b): the replay offset, playback UT minus now; 0 for
        /// a non-loop ghost or a tail that does not end on the ground.
        /// </summary>
        private static double ResolveLoopTailPlanetTurnSeconds(
            IPlaybackTrajectory traj, OrbitSegment seg, GhostPlaybackState state, double ut)
        {
            bool isLoopCycle = state != null && state.loopCycleIndex >= 0;
            if (!isLoopCycle || Planetarium.fetch == null)
                return 0.0;
            return GhostPlaybackLogic.ResolveTailPlanetTurnSeconds(
                traj, seg, isReplay: true, playbackUT: ut, currentUT: Planetarium.GetUniversalTime(),
                bodyFixedShiftSeconds: state.bodyFixedShiftSeconds);
        }

        /// <summary>
        /// Owner ruling L5: across a gap the bridge cap does not cover, slide the ghost from the
        /// last recorded point to the start of the predicted path
        /// (<see cref="GhostPlaybackLogic.TryComputeTailSlideWorldPosition"/>). The predicted
        /// orbit is placed only at its own start (never run backwards into the gap); the
        /// attitude eases from the last recorded one to the tail's start attitude; and the
        /// interpolation result carries no velocity, so no FX runs on a stale velocity.
        /// </summary>
        private void PositionGhostOnPredictedTailSlide(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            OrbitSegment seg, int segmentIndex, double lastPointUT, double ut)
        {
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                return;
            if (!GhostPlaybackLogic.TryGetTailSlideAnchorPoints(traj, seg,
                    out TrajectoryPoint last, out TrajectoryPoint prev, out bool hasPrev))
                return;

            long cacheKey = index * 10000 + Math.Max(0, segmentIndex);
            // Place the ghost on the tail's start: this resolves the conic, its start pose and
            // the surface clamp exactly as the first tail frame after the gap will (turned
            // with the planet for a looped ground-ending tail, owner ruling R4b).
            PositionGhostFromOrbit(
                state.ghost,
                seg,
                seg.startUT,
                cacheKey,
                traj.RecordingId,
                traj,
                cacheContinuityOffset: false,
                planetTurnSeconds: ResolveLoopTailPlanetTurnSeconds(traj, seg, state, ut));
            GhostPlaybackLogic.TailPlanetTurn startTurn = lastOrbitPlacementTurn;
            if (host.orbitCache == null
                || !host.orbitCache.TryGetValue(cacheKey, out Orbit orbit) || orbit == null)
                return;
            CelestialBody body = orbit.referenceBody;
            if (body == null)
                return;

            Vector3d bodyPos = body.position;
            Vector3d endWorld = state.ghost.transform.position;
            Quaternion endRot = state.ghost.transform.rotation;
            Vector3d endVelocity = (Vector3d)startTurn.ApplyVector((Vector3)TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                orbit.getOrbitalVelocityAtUT(seg.startUT)));

            double shift = state.bodyFixedShiftSeconds;
            Vector3d lastWorld = body.GetWorldSurfacePosition(
                last.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(last.longitude, shift, body),
                last.altitude);
            Vector3d prevWorld = hasPrev
                ? body.GetWorldSurfacePosition(
                    prev.latitude,
                    TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(prev.longitude, shift, body),
                    prev.altitude)
                : Vector3d.zero;
            if (!GhostPlaybackLogic.TryComputeTailSlideWorldPosition(
                    bodyPos, lastWorld, prevWorld, hasPrev, last.ut - prev.ut,
                    endWorld - bodyPos, endVelocity,
                    lastPointUT, seg.startUT, ut,
                    out Vector3d worldPos, out double fraction))
            {
                ParsekLog.VerboseRateLimited("Playback", "tail-slide-failed|" + (traj.RecordingId ?? "<no-rec>"),
                    "Predicted tail slide unresolved: rec=" + ShortRecordingId(traj.RecordingId)
                    + " ut=" + ut.ToString("F2", CultureInfo.InvariantCulture)
                    + "; ghost held at the predicted path's start", 5.0);
                return;
            }

            Quaternion startRot = body.bodyTransform != null
                ? body.bodyTransform.rotation * TrajectoryMath.FrameTransform.ShiftSurfaceRelativeRotation(
                    TrajectoryMath.SanitizeQuaternion(last.rotation), shift, body)
                : endRot;
            state.ghost.transform.position = worldPos;
            state.ghost.transform.rotation = Quaternion.Slerp(
                startRot, endRot, (float)GhostPlaybackLogic.SmoothStep01(fraction));
            // Replace the tail-start Orbit entry: the LateUpdate re-apply must hold the slide
            // pose, not snap the ghost back to the tail start.
            RegisterBodyFixedPoseEntry(state.ghost, body, worldPos, traj.RecordingId, ut);

            // Body and altitude for the FX readers, and no velocity: the slide is not a flight
            // path, so re-entry flames fade instead of burning on the last point's velocity.
            state.SetInterpolated(new InterpolationResult(
                Vector3.zero, seg.bodyName, body.GetAltitude(worldPos)));

            ParsekLog.VerboseRateLimited("Playback", "tail-slide|" + (traj.RecordingId ?? "<no-rec>"),
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted tail slide: rec={0} vessel=\"{1}\" ut={2:F2} lastPointUT={3:F2} tailStartUT={4:F2} gap={5:F1}s fraction={6:F3} startVelocity={7}",
                    ShortRecordingId(traj.RecordingId),
                    traj.VesselName ?? "?",
                    ut,
                    lastPointUT,
                    seg.startUT,
                    seg.startUT - lastPointUT,
                    fraction,
                    hasPrev ? "points" : "none"),
                5.0);
        }

        internal void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points,
            List<OrbitSegment> segments, ref int cachedIndex, double targetUT, int orbitCacheBase,
            out InterpolationResult interpResult, bool allowActivation = true, bool skipOrbitSegments = false,
            string recordingId = null, int sectionIndex = -1,
            string pointFrameSource = "input",
            List<TrackSection> flatPointSections = null)
        {
            // Check orbit segments first (unless suppressed for surface vehicles)
            if (!skipOrbitSegments && segments != null && segments.Count > 0)
            {
                int foundSegIdx = TrajectoryMath.FindOrbitSegmentIndex(segments, targetUT);
                if (foundSegIdx >= 0)
                {
                    OrbitSegment seg = segments[foundSegIdx];
                    if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                    {
                        int smaKey = ~orbitCacheBase; // bitwise complement — guaranteed no collision with positive cache keys
                        if (host.loggedOrbitSegments.Add(smaKey))
                            host.Log($"Orbit segment rejected (unusable elements): sma={seg.semiMajorAxis:F0}, " +
                                "falling through to point interpolation");
                    }
                    else
                    {
                        CelestialBody segBody = FindBodyByNameOrNull(FlightGlobals.Bodies, seg.bodyName);
                        // Use segment index as cache key offset
                        int segIdx = TrajectoryMath.ResolveOrbitSegmentIndexOfFound(
                            segments, foundSegIdx, targetUT);
                        int cacheKey = orbitCacheBase + segIdx;
                        if (host.loggedOrbitSegments.Add(cacheKey))
                            host.Log($"Orbit segment activated: cache={cacheKey}, body={seg.bodyName}, " +
                                $"sma={seg.semiMajorAxis:F0}, UT {seg.startUT:F0}-{seg.endUT:F0}");
                        PositionGhostFromOrbit(ghost, seg, targetUT, cacheKey, recordingId);
                        Vector3 vel = Vector3.zero;
                        Orbit orbit;
                        bool hasOrbit = host.orbitCache.TryGetValue(cacheKey, out orbit);
                        Vector3d zupVelocity = hasOrbit ? orbit.getOrbitalVelocityAtUT(targetUT) : Vector3d.zero;
                        if (hasOrbit)
                            vel = zupVelocity;
                        Vector3d ghostWorldPos = ghost.transform.position;
                        double segAlt = segBody != null ? segBody.GetAltitude(ghostWorldPos) : 0;
                        interpResult = new InterpolationResult(vel, seg.bodyName, segAlt);
                        if (hasOrbit && segBody != null)
                        {
                            interpResult = interpResult.WithSurfaceVelocity(
                                GhostSurfaceVelocity.FromOrbitVelocity(
                                    zupVelocity, segBody.getRFrmVel(ghostWorldPos)),
                                GhostSurfaceVelocitySource.Orbit);
                        }
                        return;
                    }
                }
            }

            // Fall through to point-based interpolation
            InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT, allowActivation, out interpResult,
                recordingId, sectionIndex, pointFrameSource, flatPointSections);
        }

        internal void ClearOrbitPlaybackCaches()
        {
            host.orbitCache?.Clear();
            predictedOrbitTailContinuityCache?.Clear();
        }

        void PositionGhostFromOrbit(GameObject ghost, OrbitSegment segment, double ut, long cacheKey,
            string recordingId = null, IPlaybackTrajectory traj = null, bool cacheContinuityOffset = true,
            double planetTurnSeconds = 0.0)
        {
            lastOrbitPlacementTurn = default(GhostPlaybackLogic.TailPlanetTurn);
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(segment))
            {
                host.Log($"Orbit segment rejected: unusable elements, sma={segment.semiMajorAxis:F0}, " +
                    $"UT {segment.startUT:F0}-{segment.endUT:F0}");
                return;
            }

            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, segment.bodyName);
            if (body == null)
            {
                host.Log($"Could not find body '{segment.bodyName}' for orbit playback");
                return;
            }

            Orbit orbit;
            if (!host.orbitCache.TryGetValue(cacheKey, out orbit))
            {
                try
                {
                    orbit = new Orbit(
                        segment.inclination,
                        segment.eccentricity,
                        segment.semiMajorAxis,
                        segment.longitudeOfAscendingNode,
                        segment.argumentOfPeriapsis,
                        segment.meanAnomalyAtEpoch,
                        segment.epoch,
                        body);
                }
                catch (Exception ex)
                {
                    host.Log($"Orbit segment construction failed: body={segment.bodyName}, " +
                        $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                    return;
                }
                host.orbitCache[cacheKey] = orbit;
            }

            Vector3d rawOrbitWorldPos;
            try
            {
                rawOrbitWorldPos = orbit.getPositionAtUT(ut);
            }
            catch (Exception ex)
            {
                host.Log($"Orbit segment propagation failed: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                return;
            }
            if (!ParsekFlight.IsFiniteVector3d(rawOrbitWorldPos))
            {
                host.Log($"Orbit segment propagation produced non-finite position: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}");
                return;
            }

            // Owner ruling R4b: a looped ground-ending predicted tail is turned with the planet
            // (read from the body's surface frame this frame), so it meets the recorded ground.
            GhostPlaybackLogic.TailPlanetTurn planetTurn = default(GhostPlaybackLogic.TailPlanetTurn);
            if (planetTurnSeconds != 0.0
                && GhostPlaybackLogic.TryResolveTailPlanetTurn(body, planetTurnSeconds, out planetTurn))
            {
                ParsekLog.VerboseRateLimited("Playback", "orbit-tail-planet-turn|" + (recordingId ?? "<no-rec>"),
                    string.Format(CultureInfo.InvariantCulture,
                        "Loop orbit tail turned with the planet: rec={0} ut={1:F2} shiftSeconds={2:F2} turnDeg={3:F4} body={4}",
                        ShortRecordingId(recordingId), ut, planetTurnSeconds,
                        planetTurn.AngleRad * (180.0 / Math.PI), segment.bodyName ?? "<none>"),
                    5.0);
            }
            Vector3d worldPos = planetTurn.Apply(body.position, rawOrbitWorldPos);
            Vector3d orbitContinuityOffset = Vector3d.zero;
            Vector3d rawOrbitContinuityOffset = Vector3d.zero;
            bool hasOrbitContinuityOffset = TryResolvePredictedOrbitTailContinuityOffset(
                traj,
                segment,
                orbit,
                ut,
                out orbitContinuityOffset,
                out rawOrbitContinuityOffset,
                out double orbitContinuityWeight,
                out double orbitContinuityAnchorUT,
                out double orbitContinuityBlendSeconds,
                out string orbitContinuityAnchorSource,
                cacheContinuityOffset && !planetTurn.Active,
                planetTurn);
            if (hasOrbitContinuityOffset)
                worldPos += orbitContinuityOffset;
            if (!ParsekFlight.IsFiniteVector3d(worldPos))
            {
                host.Log($"Orbit segment continuity produced non-finite position: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}");
                return;
            }

            // Surface clamp: if the Keplerian orbit goes underground (e.g., deorbit
            // orbit with periapsis below surface, or impact trajectory on airless body),
            // clamp to surface altitude. Prevents the ghost mesh from tunneling through
            // the planet during orbit-only recording sections where no trajectory points exist.
            double orbitAlt;
            bool orbitTerrainClamped = false;
            try
            {
                orbitAlt = body.GetAltitude(worldPos);
                if (!ParsekFlight.IsFinite(orbitAlt))
                {
                    host.Log($"Orbit segment altitude was non-finite: body={segment.bodyName}, " +
                        $"sma={segment.semiMajorAxis:F0}");
                    return;
                }

                if (orbitAlt < 0)
                {
                    double lat = body.GetLatitude(worldPos);
                    double lon = body.GetLongitude(worldPos);
                    worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
                    if (!ParsekFlight.IsFiniteVector3d(worldPos))
                    {
                        host.Log($"Orbit segment surface clamp produced non-finite position: body={segment.bodyName}, " +
                            $"sma={segment.semiMajorAxis:F0}");
                        return;
                    }
                    orbitTerrainClamped = true;
                }
            }
            catch (Exception ex)
            {
                host.Log($"Orbit segment body-position API failed: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                return;
            }

            ghost.transform.position = worldPos;

            Vector3d velocity;
            try
            {
                velocity = orbit.getOrbitalVelocityAtUT(ut);
            }
            catch
            {
                velocity = Vector3d.zero;
            }
            if (!ParsekFlight.IsFiniteVector3d(velocity))
                velocity = Vector3d.zero;

            // Orbital rotation: 3-way branch
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);

            // The attitude is decoded in the conic's own (unturned) frame, then turned with it.
            var (ghostRot, boundaryWorldRot) = ParsekFlight.ComputeOrbitalRotation(
                segment, orbit, ut, velocity, planetTurn.Unapply(body.position, worldPos), body.position,
                planetTurn.Active
                    ? TrajectoryMath.PureMultiply(TrajectoryMath.PureInverse(planetTurn.Rotation), ghost.transform.rotation)
                    : ghost.transform.rotation,
                cacheKey, hasOfr, spinning);

            ghost.transform.rotation = planetTurn.ApplyRotation(ghostRot);
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : ut;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    ut,
                    "UpdatePath",
                    "mode=Orbit cacheKey=" + cacheKey.ToString(CultureInfo.InvariantCulture)
                    + " body=" + (segment.bodyName ?? "<none>")
                    + " segmentUT=[" + segment.startUT.ToString("F3", CultureInfo.InvariantCulture)
                    + "," + segment.endUT.ToString("F3", CultureInfo.InvariantCulture) + "]"
                    + " rawWorld=" + GhostRenderTrace.FormatVector3d(rawOrbitWorldPos)
                    + " continuityHit=" + (hasOrbitContinuityOffset ? "true" : "false")
                    + " continuityAnchorUT=" + FormatFiniteOrNa(orbitContinuityAnchorUT, "F3")
                    + " continuityBlendSeconds=" + FormatFiniteOrNa(orbitContinuityBlendSeconds, "F3")
                    + " continuityWeight=" + FormatFiniteOrNa(orbitContinuityWeight, "F3")
                    + " continuityAnchorSource=" + (orbitContinuityAnchorSource ?? "<none>")
                    + " continuityRawOffset=" + GhostRenderTrace.FormatVector3d(rawOrbitContinuityOffset)
                    + " continuityOffset=" + GhostRenderTrace.FormatVector3d(orbitContinuityOffset)
                    + " terrainClamp=" + (orbitTerrainClamped ? "true" : "false")
                    + " orbitAlt=" + orbitAlt.ToString("F2", CultureInfo.InvariantCulture)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation)
                    + " rotationMode=" + (spinning ? "spin-forward" : hasOfr ? "orbital-frame" : "prograde"));
            }

            // First-activation logging (once per segment)
            if (!host.loggedOrbitRotationSegments.Contains(cacheKey))
            {
                host.loggedOrbitRotationSegments.Add(cacheKey);
                if (spinning)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: spin-forward (|angVel|={segment.angularVelocity.magnitude:F4})");
                else if (hasOfr)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: orbital-frame rotation");
                else
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: velocity-derived prograde (no ofr data)");
            }

            lastOrbitPlacementTurn = planetTurn;
            if (planetTurn.Active)
            {
                // The Orbit re-apply re-evaluates the unturned conic; hold the turned pose in the
                // body-fixed frame instead (same frame, so a floating-origin shift is exact).
                RegisterBodyFixedPoseEntry(ghost, body, worldPos, recordingId, ut);
                return;
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.Orbit,
                recordingId = recordingId,
                orbitCacheKey = cacheKey,
                orbitUT = ut,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = body,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT,
                boundaryWorldRot = boundaryWorldRot,
                orbitContinuityOffset = rawOrbitContinuityOffset,
                hasOrbitContinuityOffset = hasOrbitContinuityOffset,
                orbitContinuityAnchorUT = orbitContinuityAnchorUT,
                orbitContinuityBlendSeconds = orbitContinuityBlendSeconds,
            });
        }

        // The planet turn the last PositionGhostFromOrbit call applied (inactive when none), for
        // the interpolation result and the slide that follow it on the same frame.
        private GhostPlaybackLogic.TailPlanetTurn lastOrbitPlacementTurn;

        /// <summary>
        /// Registers the ghost's current pose for the LateUpdate / pre-cull re-apply in the
        /// body-fixed frame (a SinglePoint entry), for poses the Orbit re-apply cannot rebuild:
        /// the predicted-tail slide and a planet-turned tail.
        /// </summary>
        private void RegisterBodyFixedPoseEntry(
            GameObject ghost, CelestialBody body, Vector3d worldPos, string recordingId, double pointUT)
        {
            if (ghost == null || body == null || body.bodyTransform == null)
                return;
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.SinglePoint,
                recordingId = recordingId,
                bodyBefore = body,
                latBefore = body.GetLatitude(worldPos),
                lonBefore = body.GetLongitude(worldPos),
                altBefore = body.GetAltitude(worldPos),
                pointUT = pointUT,
                interpolatedRot = Quaternion.Inverse(body.bodyTransform.rotation) * ghost.transform.rotation,
            });
        }

        internal static Vector3d ResolveOrbitContinuityOffset(ParsekFlight.GhostPosEntry entry, double playbackUT)
        {
            if (!entry.hasOrbitContinuityOffset)
                return Vector3d.zero;

            double weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                entry.orbitContinuityAnchorUT,
                playbackUT,
                entry.orbitContinuityBlendSeconds);
            if (weight <= 1e-6)
                return Vector3d.zero;

            Vector3d offset = entry.orbitContinuityOffset * weight;
            return ParsekFlight.IsFiniteVector3d(offset) ? offset : Vector3d.zero;
        }

        internal static string FormatFiniteOrNa(double value, string format)
        {
            return ParsekFlight.IsFinite(value)
                ? value.ToString(format, CultureInfo.InvariantCulture)
                : "n/a";
        }

        /// <summary>
        /// Computes Quaternion.LookRotation(velocity, radialOut) with a fallback to Vector3.up
        /// when velocity and radialOut are nearly parallel (dot > 0.99). Logs rate-limited
        /// diagnostic when the fallback is used. Pass suffix "start" for startUT context or
        /// null for current-time context.
        /// <para>
        /// BOTH arguments are Y-up WORLD (see <see cref="ParsekFlight.ComputeOrbitalRotation"/>'s frame
        /// contract). That also makes the near-parallel test mean what it reads as: the dot of a
        /// world velocity with a world radial is the sine of the flight-path angle, so the
        /// fallback now fires on genuinely radial motion rather than on a cross-frame accident.
        /// </para>
        /// </summary>
        internal static Quaternion SafeOrbitalLookRotation(
            Vector3 velocity, Vector3 radialOut, long cacheKey, string suffix)
        {
            if (Mathf.Abs(Vector3.Dot(velocity.normalized, radialOut.normalized)) > 0.99f)
            {
                string tagSuffix = suffix != null ? $"-{suffix}" : "";
                string msgSuffix = suffix != null ? $" at {suffix}UT" : "";
                ParsekLog.VerboseRateLimited("Playback", $"orbit-near-parallel{tagSuffix}-{cacheKey}",
                    $"Orbit segment {cacheKey}: velocity/radialOut near-parallel{msgSuffix}, LookRotation fallback");
                return ParsekFlight.OrbitalLookRotation(velocity, Vector3.up);
            }
            return ParsekFlight.OrbitalLookRotation(velocity, radialOut);
        }

        /// <summary>
        /// Positions a ghost using only orbit segments (no trajectory points).
        /// Used for background-only recordings (vessels that stayed on rails).
        /// </summary>
        internal void PositionGhostFromOrbitOnly(GameObject ghost, Recording rec, double ut, int orbitCacheBase,
            bool allowActivation)
        {
            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                if (ut >= seg.startUT && ut <= seg.endUT)
                {
                    if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                        continue;

                    int cacheKey = orbitCacheBase + s;
                    if (host.loggedOrbitSegments.Add(cacheKey))
                        host.Log($"Orbit-only segment activated: cache={cacheKey}, body={seg.bodyName}, " +
                            $"sma={seg.semiMajorAxis:F0}, UT {seg.startUT:F0}-{seg.endUT:F0}");
                    PositionGhostFromOrbit(ghost, seg, ut, cacheKey, rec?.RecordingId, rec);
                    if (allowActivation)
                        ghost.SetActive(true);
                    return;
                }
            }
            // UT falls in a gap between orbit segments — hide ghost
            ghost.SetActive(false);
        }

        private bool TryGetOrbitForSegment(
            OrbitSegment segment,
            long cacheKey,
            out Orbit orbit,
            out CelestialBody body)
        {
            orbit = null;
            body = null;
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(segment))
                return false;

            body = FindBodyByNameOrNull(FlightGlobals.Bodies, segment.bodyName);
            if (body == null)
                return false;

            if (!host.orbitCache.TryGetValue(cacheKey, out orbit))
            {
                try
                {
                    orbit = new Orbit(
                        segment.inclination,
                        segment.eccentricity,
                        segment.semiMajorAxis,
                        segment.longitudeOfAscendingNode,
                        segment.argumentOfPeriapsis,
                        segment.meanAnomalyAtEpoch,
                        segment.epoch,
                        body);
                }
                catch
                {
                    orbit = null;
                    return false;
                }
                host.orbitCache[cacheKey] = orbit;
            }

            return true;
        }

        bool TryResolveOrbitTailWorldPosition(
            IPlaybackTrajectory traj,
            double targetUT,
            int orbitCacheBase,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (!GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                    traj, targetUT, out OrbitSegment segment, out int segmentIndex))
            {
                if (!GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(
                        traj, targetUT, out OrbitSegment slideSegment, out _, out double slideLastPointUT))
                    return false;
                CelestialBody slideBody = FindBodyByNameOrNull(FlightGlobals.Bodies, slideSegment.bodyName);
                return GhostPlaybackLogic.TryGetTailSlideWorldPosition(
                    traj, slideSegment, slideBody, slideLastPointUT, targetUT, 0.0, out worldPos);
            }

            long cacheKey = orbitCacheBase + Math.Max(0, segmentIndex);
            if (!TryGetOrbitForSegment(segment, cacheKey, out Orbit orbit, out CelestialBody body))
                return false;

            worldPos = orbit.getPositionAtUT(targetUT);
            if (TryResolvePredictedOrbitTailContinuityOffset(
                    traj,
                    segment,
                    orbit,
                    targetUT,
                    out Vector3d continuityOffset,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _))
            {
                worldPos += continuityOffset;
            }

            if (!ParsekFlight.IsFiniteVector3d(worldPos))
                return false;

            double orbitAlt = body.GetAltitude(worldPos);
            if (orbitAlt < 0)
            {
                double lat = body.GetLatitude(worldPos);
                double lon = body.GetLongitude(worldPos);
                worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
            }

            return ParsekFlight.IsFiniteVector3d(worldPos);
        }

        bool TryResolveOrbitWorldPosition(
            List<OrbitSegment> segments, double targetUT, int orbitCacheBase, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (segments == null || segments.Count == 0)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment seg = segments[i];
                if (targetUT < seg.startUT || targetUT > seg.endUT)
                    continue;

                CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, seg.bodyName);
                if (body == null)
                    return false;

                if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                    return false;

                Orbit orbit;
                int cacheKey = orbitCacheBase + i;
                if (!host.orbitCache.TryGetValue(cacheKey, out orbit))
                {
                    try
                    {
                        orbit = new Orbit(
                            seg.inclination,
                            seg.eccentricity,
                            seg.semiMajorAxis,
                            seg.longitudeOfAscendingNode,
                            seg.argumentOfPeriapsis,
                            seg.meanAnomalyAtEpoch,
                            seg.epoch,
                            body);
                    }
                    catch
                    {
                        return false;
                    }
                    host.orbitCache[cacheKey] = orbit;
                }

                try
                {
                    worldPos = orbit.getPositionAtUT(targetUT);
                    if (!ParsekFlight.IsFiniteVector3d(worldPos))
                        return false;

                    if (body.GetAltitude(worldPos) < 0)
                    {
                        double lat = body.GetLatitude(worldPos);
                        double lon = body.GetLongitude(worldPos);
                        worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
                        if (!ParsekFlight.IsFiniteVector3d(worldPos))
                            return false;
                    }
                }
                catch
                {
                    return false;
                }

                return true;
            }

            return false;
        }
    }
}
