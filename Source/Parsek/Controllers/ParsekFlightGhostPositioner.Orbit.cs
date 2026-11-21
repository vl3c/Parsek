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
                    cacheContinuityOffset: state.loopCycleIndex < 0);
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
            var interp = new InterpolationResult(
                (Vector3)zupVelocity, seg.bodyName, body.GetAltitude(ghostWorldPos))
                .WithSurfaceVelocity(
                    GhostSurfaceVelocity.FromOrbitVelocity(zupVelocity, body.getRFrmVel(ghostWorldPos)),
                    GhostSurfaceVelocitySource.Orbit);
            state.SetInterpolated(interp);
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
            string recordingId = null, IPlaybackTrajectory traj = null, bool cacheContinuityOffset = true)
        {
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

            Vector3d worldPos = rawOrbitWorldPos;
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
                cacheContinuityOffset);
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

            var (ghostRot, boundaryWorldRot) = ParsekFlight.ComputeOrbitalRotation(
                segment, orbit, ut, velocity, worldPos, body.position,
                ghost.transform.rotation, cacheKey, hasOfr, spinning);

            ghost.transform.rotation = ghostRot;
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
                return false;
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
