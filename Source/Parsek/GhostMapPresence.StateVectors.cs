using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // State-vector ghosts: create / remove thresholds, checkpoint fallback, map-point and
    // world-position resolution, orbit update and gap glide.
    internal static partial class GhostMapPresence
    {
        internal struct OrbitalCheckpointStateVectorFallbackDecision
        {
            public bool Accepted;
            public string Reason;
            public string ExpectedBody;
            public string StateVectorBody;
            public bool SegmentSourceAvailable;
            public bool IsSoiGapRecovery;
            public bool GapBodyTransition;
            public bool BodyMatches;
            public bool WithinPlaybackWindow;
            public string GapPreviousBody;
            public string GapNextBody;
            public double ActivationStartUT;
            public double EndUT;
        }

        internal static bool ShouldCreateStateVectorOrbit(double altitude, double speed, double atmosphereDepth)
        {
            double minAltitude = atmosphereDepth > 0 ? atmosphereDepth : StateVectorCreateAltitude;
            return altitude > minAltitude && speed > StateVectorCreateSpeed;
        }

        internal static bool ShouldRemoveStateVectorOrbit(double altitude, double speed, double atmosphereDepth)
        {
            if (atmosphereDepth > 0 && altitude < atmosphereDepth)
                return true;
            return altitude < StateVectorRemoveAltitude || speed < StateVectorRemoveSpeed;
        }

        /// <summary>
        /// Frame-aware form of <see cref="ShouldRemoveStateVectorOrbit"/>: a
        /// Relative-frame point never trips the removal threshold. In a Relative
        /// section a <c>TrajectoryPoint.altitude</c> is the anchor-local dz in metres,
        /// not a geographic altitude, so feeding it to the threshold reads a vessel
        /// flying centimetres from its anchor as a vessel at ground level and tears the
        /// ghost down every cycle while the create path re-adds it (#547 P1). Both
        /// state-vector removal call sites - the tracking-station refresh pass and the
        /// flight-scene policy pass - go through here so the bypass cannot drift apart.
        /// </summary>
        internal static bool ShouldRemoveStateVectorOrbitForFrame(
            bool inRelativeFrame, double altitude, double speed, double atmosphereDepth)
        {
            // The altitude field is frame-dependent, the speed is not: a Relative section keeps
            // the vessel's own recorded velocity (FlightRecorder only rewrites position and
            // rotation), so the speed floor still applies there. Remove below 30 m/s, create
            // above 60 m/s (ShouldCreateStateVectorOrbitForFrame): the gap is the hysteresis
            // that keeps a docking ghost from churning.
            if (inRelativeFrame)
                return speed < StateVectorRemoveSpeed;
            return ShouldRemoveStateVectorOrbit(altitude, speed, atmosphereDepth);
        }

        /// <summary>
        /// Frame-aware form of <see cref="ShouldCreateStateVectorOrbit"/>. A Relative-frame
        /// point skips the altitude floor (its altitude is the anchor-local dz) but keeps the
        /// speed floor: a state vector with near-zero velocity (a ghost parked beside a
        /// landed anchor) seeds a degenerate conic whose elements come out NaN, and stock
        /// then removes the ghost's orbit driver ("had a NaN Orbit and was removed").
        /// </summary>
        internal static bool ShouldCreateStateVectorOrbitForFrame(
            bool inRelativeFrame, double altitude, double speed, double atmosphereDepth)
        {
            if (inRelativeFrame)
                return speed > StateVectorCreateSpeed;
            return ShouldCreateStateVectorOrbit(altitude, speed, atmosphereDepth);
        }

        internal static double GetAtmosphereDepth(string bodyName)
        {
            try
            {
                var bodies = FlightGlobals.Bodies;
                if (bodies == null) return 0;
                for (int i = 0; i < bodies.Count; i++)
                {
                    if (bodies[i].name == bodyName && bodies[i].atmosphere)
                        return bodies[i].atmosphereDepth;
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "GetAtmosphereDepth: FlightGlobals unavailable for body={0} ({1}) — using 0",
                        bodyName ?? "(null)",
                        ex.GetType().Name));
            }
            return 0;
        }

        internal static bool IsInRelativeFrame(IPlaybackTrajectory traj, double ut)
        {
            if (traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative;
        }

        /// <summary>
        /// True iff the track section covering <paramref name="ut"/> is specifically
        /// <see cref="ReferenceFrame.Absolute"/> (body-fixed lat/lon/alt). The flat
        /// <c>Recording.Points</c> list (driven by the gap-points glide via
        /// <see cref="TrajectoryMath.BracketPointAtUT"/>) carries geographic lat/lon/alt
        /// only inside Absolute sections: Relative sections store anchor-local metre
        /// offsets in those same fields, and OrbitalCheckpoint coast sections are
        /// represented by sparse / empty flat points (the on-rails coast is a Keplerian
        /// bridge, not a per-frame body-fixed sample stream). Requiring an Absolute
        /// covering section makes the "has real lat/lon/alt to drive from" contract
        /// explicit, so the gap glide never brackets a stale flat point on an
        /// OrbitalCheckpoint or Relative gap and mis-positions the icon.
        /// </summary>
        internal static bool IsInAbsoluteFrame(IPlaybackTrajectory traj, double ut)
        {
            if (traj == null || traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Absolute;
        }

        internal static bool HasRecordedTrackCoverageAtUT(IPlaybackTrajectory traj, double ut)
        {
            if (traj == null)
                return false;

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                for (int i = 0; i < traj.TrackSections.Count; i++)
                {
                    TrackSection section = traj.TrackSections[i];
                    if (ut >= section.startUT && ut <= section.endUT)
                        return true;
                }

                return false;
            }

            if (traj.Points != null && traj.Points.Count > 0)
            {
                for (int i = 0; i < traj.Points.Count; i++)
                {
                    double pointUT = traj.Points[i].ut;
                    if (Math.Abs(pointUT - ut) <= 0.001)
                        return true;

                    if (pointUT > ut)
                    {
                        if (i == 0)
                            return false;

                        double previousUT = traj.Points[i - 1].ut;
                        double bracketGap = pointUT - previousUT;
                        return ut >= previousUT && bracketGap <= LegacyPointCoverageMaxGapSeconds;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the immediate orbit-segment gap bracketing <paramref name="ut"/>.
        /// <paramref name="segments"/> must be sorted by increasing UT, matching the
        /// playback trajectory invariant.
        /// </summary>
        internal static bool TryFindOrbitSegmentGap(
            IReadOnlyList<OrbitSegment> segments,
            double ut,
            out OrbitSegment previous,
            out OrbitSegment next)
        {
            previous = default(OrbitSegment);
            next = default(OrbitSegment);
            if (segments == null || segments.Count < 2)
                return false;

            int previousIndex = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment candidate = segments[i];
                if (candidate.endUT <= ut)
                {
                    previousIndex = i;
                    continue;
                }

                if (previousIndex < 0 || candidate.startUT <= ut)
                    return false;

                previous = segments[previousIndex];
                next = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pure decision: should the ghost map icon be driven from recorded trajectory
        /// POINTS (the body-fixed state-vector path) across this UT instead of carrying
        /// the previous OrbitSegment forward?
        ///
        /// <para>
        /// Context: <see cref="TrajectoryMath.FindOrbitSegmentOrSameBodyCarry"/> keeps a
        /// ghost's orbit alive across a same-body inter-segment gap by returning the
        /// PREVIOUS segment (the "capture-burn between two Mun orbits" case it was built
        /// for). That carry is correct when the two bracketing orbits are essentially the
        /// same shape. But when the gap is a real orbit RAISE (parking orbit -> higher
        /// loiter orbit, sma 671928 -> 731230 across a ~205s burn arc), carrying the stale
        /// parking segment freezes the icon on the OLD orbit, and when UT finally enters
        /// the next segment the icon TELEPORTS to the new orbit (the ~1318 km jump the
        /// playtest showed). The recorder actually captured that raise arc as ~84 body-fixed
        /// Absolute trajectory POINTS; this predicate routes the icon onto those points so
        /// it glides continuously across the gap.
        /// </para>
        ///
        /// <para>
        /// Returns true iff ALL hold:
        /// (1) <paramref name="effUT"/> falls in a genuine orbit-segment gap (no segment
        ///     contains it) bracketed by a previous + next segment;
        /// (2) the recording has track coverage at effUT (real lat/lon/alt POINTs to
        ///     drive from);
        /// (3) the covering track section is specifically Absolute (body-fixed lat/lon/alt).
        ///     This excludes Relative sections (which store anchor-local metre offsets in
        ///     the lat/lon/alt fields, not geographic coordinates -- the state-vector
        ///     positioner would produce a position deep inside the planet) AND
        ///     OrbitalCheckpoint coast sections (which are sparse / empty in the flat
        ///     Points list -- bracketing them would mis-position the icon). The flat
        ///     Recording.Points list driven by the glide only carries geographic
        ///     coordinates inside Absolute sections;
        /// (4) the bracketing segments are NOT orbit-equivalent for map display -- so the
        ///     genuine same-orbit carry case (capture burn between two equivalent orbits)
        ///     that the carry was built for stays on the carry path, byte-identical.
        /// </para>
        /// </summary>
        internal static bool ShouldDriveGapFromPoints(
            IReadOnlyList<OrbitSegment> effectiveSegments,
            IPlaybackTrajectory traj,
            double effUT)
        {
            if (traj == null)
                return false;

            if (!TryFindOrbitSegmentGap(effectiveSegments, effUT,
                    out OrbitSegment previous, out OrbitSegment next))
                return false;

            // Same-body gap only (self-protecting contract): the points glide is for a raise / plane
            // change within one SOI. A cross-body gap is an SOI crossing, handled by the
            // OrbitalCheckpoint state-vector path, not here. The flight + TS dispatchers already pre-gate
            // on FindOrbitSegmentOrSameBodyCarry (which is same-body), so this is belt-and-suspenders, but
            // it keeps the predicate correct in isolation - TryFindOrbitSegmentGap, unlike the carry, has
            // no body check - and robust against a future caller that does not pre-gate.
            if (!string.Equals(previous.bodyName, next.bodyName, StringComparison.Ordinal))
                return false;

            if (!HasRecordedTrackCoverageAtUT(traj, effUT))
                return false;

            // Require an Absolute covering section. This is strictly tighter than the
            // old "not Relative" check: it also rejects OrbitalCheckpoint coast gaps,
            // where the flat Points list is sparse / empty and bracketing it would
            // strand the icon on a stale clamped point. The gap glide reads geographic
            // lat/lon/alt from the flat Points list, which is only populated and
            // geographic inside Absolute (atmospheric / maneuver) regions.
            if (!IsInAbsoluteFrame(traj, effUT))
                return false;

            // Preserve the carry's original purpose: when the two bracketing segments
            // describe the same orbit shape, the carry (previous segment held forward)
            // is correct and the icon stays put with no teleport. Only the NON-equivalent
            // gap (a real raise / plane change) needs the points glide.
            if (TrajectoryMath.AreOrbitSegmentsEquivalentForMapDisplay(previous, next))
                return false;

            return true;
        }

        /// <summary>
        /// Evaluates the narrow OrbitalCheckpoint state-vector carve-out for SOI gaps.
        /// <paramref name="expectedSoiGapBody"/> is a caller hint; when the segment
        /// list contains the bracketing gap, the actual post-gap segment body wins.
        /// </summary>
        internal static OrbitalCheckpointStateVectorFallbackDecision EvaluateOrbitalCheckpointStateVectorFallback(
            IPlaybackTrajectory traj,
            double currentUT,
            TrajectoryPoint point,
            bool segmentSourceAvailable,
            bool allowSoiGapRecovery,
            string expectedSoiGapBody)
        {
            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
            double endUT = traj?.EndUT ?? double.NaN;
            bool withinPlaybackWindow =
                traj != null
                && currentUT >= activationStartUT
                && currentUT <= endUT
                && point.ut >= activationStartUT
                && point.ut <= endUT;

            string expectedBody = expectedSoiGapBody;
            bool hasGap = false;
            bool gapBodyTransition = false;
            string gapPreviousBody = null;
            string gapNextBody = null;
            if (traj?.OrbitSegments != null
                && TryFindOrbitSegmentGap(traj.OrbitSegments, currentUT, out OrbitSegment previousSegment, out OrbitSegment nextSegment))
            {
                hasGap = true;
                gapPreviousBody = previousSegment.bodyName;
                gapNextBody = nextSegment.bodyName;
                gapBodyTransition = !string.Equals(
                    previousSegment.bodyName,
                    nextSegment.bodyName,
                    StringComparison.Ordinal);
                expectedBody = nextSegment.bodyName;
            }

            bool isSoiGapRecovery = allowSoiGapRecovery && hasGap && gapBodyTransition;
            bool bodyMatches = !string.IsNullOrEmpty(expectedBody)
                && string.Equals(point.bodyName, expectedBody, StringComparison.Ordinal);

            string reason;
            bool accepted = false;
            if (segmentSourceAvailable)
                reason = OrbitalCheckpointStateVectorRejectSaferSegment;
            else if (!isSoiGapRecovery)
                reason = OrbitalCheckpointStateVectorRejectNotSoiGap;
            else if (!bodyMatches)
                reason = OrbitalCheckpointStateVectorRejectBodyMismatch;
            else if (!withinPlaybackWindow)
                reason = OrbitalCheckpointStateVectorRejectOutsideWindow;
            else
            {
                accepted = true;
                reason = SoiGapStateVectorFallbackReason;
            }

            return new OrbitalCheckpointStateVectorFallbackDecision
            {
                Accepted = accepted,
                Reason = reason,
                ExpectedBody = expectedBody,
                StateVectorBody = point.bodyName,
                SegmentSourceAvailable = segmentSourceAvailable,
                IsSoiGapRecovery = isSoiGapRecovery,
                GapBodyTransition = gapBodyTransition,
                BodyMatches = bodyMatches,
                WithinPlaybackWindow = withinPlaybackWindow,
                GapPreviousBody = gapPreviousBody,
                GapNextBody = gapNextBody,
                ActivationStartUT = activationStartUT,
                EndUT = endUT
            };
        }

        private static string FormatOrbitalCheckpointStateVectorFallbackDecision(
            OrbitalCheckpointStateVectorFallbackDecision decision)
        {
            return string.Format(ic,
                "orbitalCheckpointFallback={0} fallbackReason={1} isSoiGapRecovery={2} gapBodyTransition={3} gapPreviousBody={4} gapNextBody={5} segmentSourceAvailable={6} bodyMatches={7} stateVectorBody={8} expectedBody={9} withinPlaybackWindow={10} activationStartUT={11:F1} endUT={12:F1}",
                decision.Accepted ? "accept" : "reject",
                decision.Reason ?? "(none)",
                decision.IsSoiGapRecovery,
                decision.GapBodyTransition,
                decision.GapPreviousBody ?? "(none)",
                decision.GapNextBody ?? "(none)",
                decision.SegmentSourceAvailable,
                decision.BodyMatches,
                decision.StateVectorBody ?? "(null)",
                decision.ExpectedBody ?? "(none)",
                decision.WithinPlaybackWindow,
                decision.ActivationStartUT,
                decision.EndUT);
        }

        private static bool TryResolveOrbitWorldPosition(
            OrbitSegment segment,
            double currentUT,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            try
            {
                CelestialBody body = FindBodyByName(segment.bodyName);
                if (body == null)
                    return false;

                Orbit orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                worldPos = orbit.getPositionAtUT(currentUT);
                return IsFinite(worldPos);
            }
            catch (Exception)
            {
                worldPos = Vector3d.zero;
                return false;
            }
        }

        private static bool TryResolvePointWorldPosition(
            TrajectoryPoint point,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            try
            {
                CelestialBody body = FindBodyByName(point.bodyName);
                if (body == null)
                    return false;

                worldPos = body.GetWorldSurfacePosition(
                    point.latitude,
                    point.longitude,
                    point.altitude);
                return IsFinite(worldPos);
            }
            catch (Exception)
            {
                worldPos = Vector3d.zero;
                return false;
            }
        }

        private static string FormatWorldPosition(Vector3d value)
        {
            return string.Format(ic,
                "({0:F1},{1:F1},{2:F1})",
                value.x,
                value.y,
                value.z);
        }

        private static bool IsFinite(Vector3d value)
        {
            return !(double.IsNaN(value.x) || double.IsNaN(value.y) || double.IsNaN(value.z)
                || double.IsInfinity(value.x) || double.IsInfinity(value.y) || double.IsInfinity(value.z));
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }

        private static string NormalizeStateVectorSkipReasonForNoOrbit(string stateVectorSkipReason)
        {
            return string.IsNullOrEmpty(stateVectorSkipReason)
                || stateVectorSkipReason == "no-points"
                    ? "no-orbit-data"
                    : stateVectorSkipReason;
        }

        private static bool TryResolveCheckpointStateVectorMapPoint(
            IPlaybackTrajectory traj,
            double currentUT,
            ref int cachedIndex,
            out TrajectoryPoint point,
            out string skipReason,
            out TrackSection section)
        {
            point = default(TrajectoryPoint);
            skipReason = null;
            section = default(TrackSection);

            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
            {
                skipReason = "no-track-sections";
                return false;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, currentUT);
            if (sectionIdx < 0)
            {
                skipReason = "no-current-section";
                return false;
            }

            section = traj.TrackSections[sectionIdx];
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint)
            {
                skipReason = "not-orbital-checkpoint";
                return false;
            }

            if (section.frames == null || section.frames.Count == 0)
            {
                skipReason = "no-checkpoint-points";
                return false;
            }

            TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(
                section.frames,
                currentUT,
                ref cachedIndex);
            if (!pt.HasValue)
            {
                skipReason = "no-checkpoint-state-vector-point";
                return false;
            }

            point = pt.Value;
            return true;
        }

        // #583: when state-vector resolution was attempted (either because
        // there are no orbit segments OR because we're in a Relative section)
        // and produced a meaningful skip reason — relative-frame /
        // relative-anchor-unresolved / relative-state-vector-segment-gap /
        // state-vector-threshold — surface that reason. Otherwise fall back
        // to the legacy split between
        // `no-current-segment` (orbit-bearing recording) and `no-orbit-data`
        // (state-vector-only recording with no points). Preserves the prior
        // log-shape contract for callers that don't reach the new Relative
        // gate while making the new defer-and-retry skip reasons visible in
        // the structured source-resolve line.
        private static string ResolveStateVectorOrSegmentSkipReason(
            IPlaybackTrajectory traj,
            bool considerStateVector,
            string stateVectorSkipReason)
        {
            if (considerStateVector
                && !string.IsNullOrEmpty(stateVectorSkipReason)
                && stateVectorSkipReason != "no-points"
                && stateVectorSkipReason != "no-state-vector-point")
            {
                return stateVectorSkipReason;
            }
            return traj.HasOrbitSegments
                ? "no-current-segment"
                : NormalizeStateVectorSkipReasonForNoOrbit(stateVectorSkipReason);
        }

        private static bool TryResolveStateVectorMapPoint(
            IPlaybackTrajectory traj,
            double currentUT,
            ref int cachedIndex,
            out TrajectoryPoint point,
            out string skipReason)
        {
            return TryResolveStateVectorMapPointPure(
                traj,
                currentUT,
                ref cachedIndex,
                out point,
                out skipReason);
        }

        /// <summary>
        /// Resolve whether a state-vector trajectory point exists at the current
        /// UT and is suitable for ghost map creation. #583: when the current UT
        /// lies inside a Relative-frame section, the recorded
        /// <c>point.altitude</c> is the anchor-local dz offset (metres), not
        /// geographic altitude, so the create/remove altitude thresholds are
        /// meaningless and are skipped. v11 Relative sections must name an
        /// anchor recording; the later state-vector world-frame resolver owns
        /// recorded-pose resolution and skips unresolved chains without live
        /// vessel PID lookups.
        /// </summary>
        internal static bool TryResolveStateVectorMapPointPure(
            IPlaybackTrajectory traj,
            double currentUT,
            ref int cachedIndex,
            out TrajectoryPoint point,
            out string skipReason)
        {
            point = default(TrajectoryPoint);
            skipReason = null;

            if (traj.Points == null || traj.Points.Count == 0)
            {
                skipReason = "no-points";
                return false;
            }

            double activationStartUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
            if (currentUT < activationStartUT || currentUT > traj.EndUT)
            {
                skipReason = "no-state-vector-point";
                return false;
            }

            // Resolve the section covering currentUT once; the Relative branch
            // needs the reference-frame metadata, the Absolute branch needs
            // nothing more.
            TrackSection? currentSection = null;
            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, currentUT);
                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                    currentSection = traj.TrackSections[sectionIdx];
            }

            bool inRelative = currentSection.HasValue
                && currentSection.Value.referenceFrame == ReferenceFrame.Relative;

            if (inRelative)
            {
                if (ShouldUseBodyFixedPrimaryForParentAnchoredDebris(
                        traj,
                        currentSection.Value,
                        currentUT))
                {
                    if (!TrySelectBodyFixedPrimaryStateVectorPoint(
                            traj,
                            currentSection.Value,
                            currentUT,
                            out TrajectoryPoint bodyFixedPoint,
                            out skipReason))
                    {
                        return false;
                    }

                    double bodyFixedAtmosphereDepth = GetAtmosphereDepth(bodyFixedPoint.bodyName);
                    if (!ShouldCreateStateVectorOrbit(
                            bodyFixedPoint.altitude,
                            bodyFixedPoint.velocity.magnitude,
                            bodyFixedAtmosphereDepth))
                    {
                        skipReason = TrackingStationGhostSkipStateVectorThreshold;
                        return false;
                    }

                    point = bodyFixedPoint;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(currentSection.Value.anchorRecordingId))
                {
                    skipReason = TrackingStationGhostSkipRelativeAnchorUnresolved;
                    return false;
                }

                TrajectoryPoint? relPt = TrajectoryMath.BracketPointAtUT(traj.Points, currentUT, ref cachedIndex);
                if (!relPt.HasValue)
                {
                    skipReason = "no-state-vector-point";
                    return false;
                }

                // The altitude floor is skipped: point.altitude is the
                // anchor-local dz (metres along the anchor's local z), not
                // geographic altitude. The speed floor stays: the recorded
                // velocity is the vessel's own, and a near-zero one seeds a
                // NaN conic (RL-1: a loop ghost parked beside a landed anchor).
                // Symmetric to the removal gate both update passes run.
                if (!ShouldCreateStateVectorOrbitForFrame(
                        inRelativeFrame: true,
                        relPt.Value.altitude,
                        relPt.Value.velocity.magnitude,
                        atmosphereDepth: 0.0))
                {
                    skipReason = TrackingStationGhostSkipStateVectorThreshold;
                    return false;
                }
                point = relPt.Value;
                return true;
            }

            TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(traj.Points, currentUT, ref cachedIndex);
            if (!pt.HasValue)
            {
                skipReason = "no-state-vector-point";
                return false;
            }

            double atmosphereDepth = GetAtmosphereDepth(pt.Value.bodyName);
            if (!ShouldCreateStateVectorOrbit(
                pt.Value.altitude,
                pt.Value.velocity.magnitude,
                atmosphereDepth))
            {
                skipReason = TrackingStationGhostSkipStateVectorThreshold;
                return false;
            }

            point = pt.Value;
            return true;
        }

        private static bool ShouldUseBodyFixedPrimaryForParentAnchoredDebris(
            IPlaybackTrajectory traj,
            TrackSection section,
            double playbackUT)
        {
            return section.referenceFrame == ReferenceFrame.Relative
                && DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj)
                && !GhostPlaybackEngine.ShouldUseLoopAnchoredDebrisChain(traj, playbackUT);
        }

        internal static uint ResolveBodyFixedPrimaryAnchorPid(
            IPlaybackTrajectory traj,
            TrackSection section)
        {
            string anchorRecordingId = !string.IsNullOrWhiteSpace(section.anchorRecordingId)
                ? section.anchorRecordingId.Trim()
                : traj?.ParentAnchorRecordingId;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
                return 0u;

            if (TryFindRecordingByIdForBodyFixedAnchorPid(
                    anchorRecordingId,
                    out Recording anchorRecording))
            {
                return anchorRecording?.VesselPersistentId ?? 0u;
            }

            return 0u;
        }

        private static bool TryFindRecordingByIdForBodyFixedAnchorPid(
            string recordingId,
            out Recording recording)
        {
            // [ERS-exempt] Body-fixed-primary anchor pid lookup correlates a
            // section's stored anchorRecordingId / ParentAnchorRecordingId to a
            // VesselPersistentId for state-vector telemetry only. The walk is
            // string-id keyed (recording-id, not chain semantics) so ERS filtering
            // by supersede/visibility would mask the very anchor recording an
            // active Re-Fly provisional may need to resolve. Read-only; no ledger.
            recording = null;
            if (string.IsNullOrWhiteSpace(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.TryGetValue(recordingId, out recording)
                        && recording != null)
                    {
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTreeSlot.HasPendingTree
                ? PendingTreeSlot.PendingTree
                : null;
            if (pending?.Recordings != null
                && pending.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            IReadOnlyList<Recording> committedRecordings = RecordingStore.CommittedRecordings;
            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording candidate = committedRecordings[i];
                    if (candidate != null
                        && string.Equals(candidate.RecordingId, recordingId, StringComparison.Ordinal))
                    {
                        recording = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TrySelectBodyFixedPrimaryStateVectorPoint(
            IPlaybackTrajectory traj,
            TrackSection section,
            double playbackUT,
            out TrajectoryPoint point,
            out string skipReason)
        {
            point = default(TrajectoryPoint);
            skipReason = TrackingStationGhostSkipBodyFixedPrimaryUnavailable;
            if (!ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                    section,
                    playbackUT,
                    out _,
                    out _))
            {
                if (DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                        traj,
                        playbackUT,
                        out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
                    && !string.IsNullOrWhiteSpace(diagnostic.Reason))
                {
                    skipReason = diagnostic.Reason;
                }
                return false;
            }

            int bodyFixedIndex = 0;
            TrajectoryPoint? bodyFixedPoint = TrajectoryMath.BracketPointAtUT(
                section.bodyFixedFrames,
                playbackUT,
                ref bodyFixedIndex);
            if (!bodyFixedPoint.HasValue)
                return false;

            point = bodyFixedPoint.Value;
            skipReason = null;
            return true;
        }

        /// <summary>
        /// Outcome of resolving a state-vector trajectory point to a world-space
        /// position. The reference frame of the originating <see cref="TrackSection"/>
        /// determines whether <c>point.latitude/longitude/altitude</c> are body-fixed
        /// surface coordinates or anchor-local XYZ offsets — the wrong interpretation
        /// silently places the ghost roughly at the body surface but at a horizontally
        /// meaningless lat/lon (#582 / #571 contributor). This struct centralises the
        /// branch so both call sites in <see cref="CreateGhostVesselFromStateVectors"/>
        /// and <see cref="UpdateGhostOrbitFromStateVectors"/> stay in sync.
        /// </summary>
        internal struct StateVectorWorldFrame
        {
            public bool Resolved;
            public Vector3d WorldPos;
            public string Branch;          // "absolute", "relative", "orbital-checkpoint", "no-section"
            public string FailureReason;   // null on success
            public uint AnchorPid;         // 0 unless Branch == "relative"
        }

        /// <summary>
        /// The velocity a state-vector map ghost is seeded with. The recorded vector is a
        /// world vector in the recording session's axes, surface-relative below the body's
        /// inverse-rotation altitude, while the seed position is rebuilt in THIS session's
        /// axes, so the frame and yaw are resolved from the sample and its same-section
        /// neighbour (<see cref="OrbitReseed.ResolveRecordedSampleSeedVelocityInSession"/>).
        /// A Relative or body-fixed-primary branch, a single sample or an unresolved pair
        /// keeps the recorded vector, the seed this site always used; the line names which.
        /// </summary>
        private static Vector3d ResolveStateVectorSeedVelocity(
            IPlaybackTrajectory traj,
            TrajectoryPoint point,
            CelestialBody body,
            StateVectorWorldFrame resolution,
            string site,
            int recordingIndex,
            bool rateLimit)
        {
            OrbitReseed.RecordedSampleSeedVelocity v = OrbitReseed.ResolveRecordedSampleSeedVelocityInSession(
                body,
                traj?.Points,
                traj?.TrackSections,
                point,
                resolution.WorldPos);
            // The update path runs per ghost per reseed tick: build the line, its key and the
            // closure only when verbose logging is on.
            if (!ParsekLog.IsVerboseEnabled)
                return v.VelocityYup;
            System.Func<string> line = () => OrbitReseed.FormatRecordedSampleSeedVelocityLine(
                    "State-vector seed velocity",
                    site,
                    traj?.RecordingId,
                    body?.name,
                    point,
                    v)
                + string.Format(ic, " recIndex={0} branch={1}", recordingIndex, resolution.Branch ?? "(null)");
            if (rateLimit)
            {
                // The update runs per ghost per reseed tick: one line per recording, outcome
                // and decline reason, so a changed outcome still prints at once.
                ParsekLog.VerboseRateLimited(Tag,
                    "sv-seed-velocity-" + site + "-" + (traj?.RecordingId ?? "(null)")
                        + "-" + v.Source + "-" + (v.DeclineReason ?? "(none)"),
                    line,
                    5.0);
            }
            else
            {
                ParsekLog.Verbose(Tag, line());
            }
            return v.VelocityYup;
        }

        internal static bool ShouldRetryStateVectorCreationAfterResolutionMiss(
            StateVectorWorldFrame resolution)
        {
            return !resolution.Resolved
                && string.Equals(resolution.Branch, "relative", StringComparison.Ordinal)
                && string.Equals(resolution.FailureReason, "anchor-not-found", StringComparison.Ordinal);
        }

        /// <summary>
        /// Pure-static resolution: given a trajectory point, the originating section
        /// (or null), the body, and pre-resolved anchor data, return the world-space
        /// position the state-vector orbit should be seeded at. No KSP API calls —
        /// callers do the body / anchor lookups and pass results in. Pure for testability.
        /// </summary>
        internal static StateVectorWorldFrame ResolveStateVectorWorldPositionPure(
            TrajectoryPoint point,
            TrackSection? section,
            int recordingFormatVersion,
            Func<double, double, double, Vector3d> absoluteSurfaceLookup,
            bool anchorFound,
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRot,
            uint anchorVesselId,
            bool allowOrbitalCheckpointStateVector = false,
            TrajectoryPoint? bodyFixedPrimaryPoint = null)
        {
            // Parent-anchored debris uses `bodyFixedFrames` as the primary
            // surface for ordinary debris. Callers pass the selected body-fixed
            // point here after deciding the recording is not a loop-anchored
            // chain. Resolved through the standard surface lookup it yields the
            // recorded world position directly. Returns Branch="body-fixed-primary"
            // so call-site logs and tests can distinguish it from Absolute.
            if (bodyFixedPrimaryPoint.HasValue)
            {
                TrajectoryPoint shadow = bodyFixedPrimaryPoint.Value;
                Vector3d pos = absoluteSurfaceLookup(shadow.latitude, shadow.longitude, shadow.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "body-fixed-primary",
                    FailureReason = null,
                    AnchorPid = anchorVesselId,
                };
            }
            // No track sections at all — fall back to the original Absolute interpretation.
            // This preserves behaviour for legacy / synthetic recordings that have not yet
            // been split into sections, where the lat/lon/alt fields are still surface coords.
            if (!section.HasValue)
            {
                Vector3d pos = absoluteSurfaceLookup(point.latitude, point.longitude, point.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "no-section",
                    FailureReason = null,
                    AnchorPid = 0
                };
            }

            ReferenceFrame frame = section.Value.referenceFrame;
            if (frame == ReferenceFrame.Absolute)
            {
                Vector3d pos = absoluteSurfaceLookup(point.latitude, point.longitude, point.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "absolute",
                    FailureReason = null,
                    AnchorPid = 0
                };
            }

            if (frame == ReferenceFrame.Relative)
            {
                if (!anchorFound)
                {
                    return new StateVectorWorldFrame
                    {
                        Resolved = false,
                        WorldPos = default(Vector3d),
                        Branch = "relative",
                        FailureReason = "anchor-not-found",
                        AnchorPid = anchorVesselId
                    };
                }

                // The lat/lon/alt fields are reused as anchor-local XYZ offsets in
                // RELATIVE sections (TrajectoryPoint.cs:13-15 docstring). Resolve via
                // the same canonical helper used by the flight-scene playback path.
                Vector3d worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorWorldPos,
                    anchorWorldRot,
                    point.latitude,
                    point.longitude,
                    point.altitude);

                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = worldPos,
                    Branch = "relative",
                    FailureReason = null,
                    AnchorPid = anchorVesselId
                };
            }

            // OrbitalCheckpoint state-vector entry points are normally refused:
            // checkpoint sections are supposed to have segment/terminal-orbit
            // sources available, and stale checkpoint-frame interpretation can
            // reintroduce wrong-position bugs. The only caller that may opt in is
            // the explicit SOI/orbit-segment-gap recovery path after it has already
            // proven there is no safer source and the body/window match.
            if (allowOrbitalCheckpointStateVector)
            {
                Vector3d pos = absoluteSurfaceLookup(point.latitude, point.longitude, point.altitude);
                return new StateVectorWorldFrame
                {
                    Resolved = true,
                    WorldPos = pos,
                    Branch = "orbital-checkpoint",
                    FailureReason = null,
                    AnchorPid = 0
                };
            }

            return new StateVectorWorldFrame
            {
                Resolved = false,
                WorldPos = default(Vector3d),
                Branch = "orbital-checkpoint",
                FailureReason = "state-vector-from-orbital-checkpoint",
                AnchorPid = 0
            };
        }

        /// <summary>
        /// KSP-dependent wrapper over <see cref="ResolveStateVectorWorldPositionPure"/>.
        /// Looks up the section/body and resolves recorded Relative anchors
        /// through <see cref="RecordedRelativeAnchorPoseResolver"/>, then
        /// delegates to the pure helper.
        /// </summary>
        private static StateVectorWorldFrame ResolveStateVectorWorldPosition(
            IPlaybackTrajectory traj,
            TrajectoryPoint point,
            CelestialBody body,
            bool allowOrbitalCheckpointStateVector = false)
        {
            TrackSection? section = null;
            if (traj?.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, point.ut);
                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                    section = traj.TrackSections[sectionIdx];
            }

            bool anchorFound = false;
            Vector3d anchorPos = default(Vector3d);
            Quaternion anchorRot = Quaternion.identity;
            uint anchorPid = 0u;
            if (section.HasValue
                && section.Value.referenceFrame == ReferenceFrame.Relative
                && ShouldUseBodyFixedPrimaryForParentAnchoredDebris(
                    traj,
                    section.Value,
                    point.ut))
            {
                uint bodyFixedAnchorPid = ResolveBodyFixedPrimaryAnchorPid(traj, section.Value);
                if (!TrySelectBodyFixedPrimaryStateVectorPoint(
                        traj,
                        section.Value,
                        point.ut,
                        out TrajectoryPoint bodyFixedPoint,
                        out string bodyFixedSkipReason))
                {
                    return new StateVectorWorldFrame
                    {
                        Resolved = false,
                        WorldPos = default(Vector3d),
                        Branch = "body-fixed-primary",
                        FailureReason = bodyFixedSkipReason,
                        AnchorPid = bodyFixedAnchorPid
                    };
                }

                return ResolveStateVectorWorldPositionPure(
                    point,
                    section,
                    traj?.RecordingFormatVersion ?? 0,
                    (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt),
                    anchorFound: false,
                    anchorWorldPos: default(Vector3d),
                    anchorWorldRot: Quaternion.identity,
                    anchorVesselId: bodyFixedAnchorPid,
                    allowOrbitalCheckpointStateVector: allowOrbitalCheckpointStateVector,
                    bodyFixedPrimaryPoint: bodyFixedPoint);
            }

            if (section.HasValue
                && section.Value.referenceFrame == ReferenceFrame.Relative
                && RecordedRelativeAnchorPoseResolver.TryFindFocusRecording(traj, out Recording focusRecording)
                && RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose(
                    focusRecording,
                    section.Value,
                    point.ut,
                    out AnchorPose anchorPose))
            {
                anchorFound = true;
                anchorPos = anchorPose.WorldPos;
                anchorRot = anchorPose.WorldRotation;
            }

            int formatVersion = traj?.RecordingFormatVersion ?? 0;
            return ResolveStateVectorWorldPositionPure(
                point,
                section,
                formatVersion,
                (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt),
                anchorFound,
                anchorPos,
                anchorRot,
                anchorPid,
                allowOrbitalCheckpointStateVector,
                bodyFixedPrimaryPoint: null);
        }

        /// <summary>
        /// Create a ghost map ProtoVessel from interpolated trajectory state vectors.
        /// Used for physics-only suborbital recordings that have no orbit segments.
        /// Constructs a Keplerian orbit from position + velocity at the given UT.
        /// Honours the originating TrackSection's <see cref="ReferenceFrame"/>: Absolute
        /// uses surface lat/lon/alt; Relative resolves through TrackSection.anchorRecordingId
        /// via the recorded-anchor resolver, then applies the anchor-local offset.
        /// Sets <paramref name="retryLater"/> = true when a
        /// transient state-vector deferral or active-Re-Fly suppression path fires.
        /// Callers that maintain a "pending map vessel" queue (cf.
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c>) keep the pending
        /// entry alive on (<c>null</c>, <c>retryLater = true</c>) so the recording
        /// is retried next tick rather than silently dropped forever.
        /// </summary>
        internal static Vessel CreateGhostVesselFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point,
            double ut,
            out bool retryLater,
            bool allowOrbitalCheckpointStateVector = false,
            string stateVectorCreateReason = null)
        {
            retryLater = false;
            if (traj == null) return null;

            // Phase 7 session-suppression gate (design §3.3).
            if (IsSuppressedByActiveSession(recordingIndex))
            {
                RemoveGhostVesselForRecording(recordingIndex, "session-suppressed subtree");
                return null;
            }

            if (IsStateVectorGhostDeferredForActiveReFlySession(
                    recordingIndex,
                    out string deferredSessionKey))
            {
                retryLater = true;
                var deferred = NewDecisionFields("create-state-vector-deferred");
                deferred.RecordingId = traj.RecordingId;
                deferred.RecordingIndex = recordingIndex;
                deferred.VesselName = traj.VesselName;
                deferred.Source = "StateVector";
                deferred.Body = point.bodyName;
                deferred.StateVecAlt = point.altitude;
                deferred.StateVecSpeed = point.velocity.magnitude;
                deferred.UT = ut;
                deferred.Reason = string.Format(ic,
                    "active-refly-deferred-session session={0} retryLater=true",
                    deferredSessionKey);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(deferred));
                return null;
            }

            if (vesselsByRecordingIndex.ContainsKey(recordingIndex))
                return vesselsByRecordingIndex[recordingIndex];

            // Intent-line: log the input shape before any work — easy filter
            // for "what was Parsek asked to create at this UT?".
            {
                var intent = NewDecisionFields("create-state-vector-intent");
                intent.RecordingId = traj.RecordingId;
                intent.RecordingIndex = recordingIndex;
                intent.VesselName = traj.VesselName;
                intent.Source = "StateVector";
                intent.Body = point.bodyName;
                intent.StateVecAlt = point.altitude;
                intent.StateVecSpeed = point.velocity.magnitude;
                intent.UT = ut;
                intent.Reason = stateVectorCreateReason;
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(intent));
            }

            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
            {
                var miss = NewDecisionFields("create-state-vector-miss");
                miss.RecordingId = traj.RecordingId;
                miss.RecordingIndex = recordingIndex;
                miss.VesselName = traj.VesselName;
                miss.Source = "StateVector";
                miss.Body = point.bodyName;
                miss.UT = ut;
                miss.Reason = "body-not-found";
                ParsekLog.Error(Tag, BuildGhostMapDecisionLine(miss));
                return null;
            }

            StateVectorWorldFrame resolution =
                ResolveStateVectorWorldPosition(
                    traj,
                    point,
                    body,
                    allowOrbitalCheckpointStateVector);
            if (!resolution.Resolved)
            {
                bool retryResolutionLater = ShouldRetryStateVectorCreationAfterResolutionMiss(resolution);
                if (retryResolutionLater)
                    retryLater = true;

                var skip = NewDecisionFields("create-state-vector-skip");
                skip.RecordingId = traj.RecordingId;
                skip.RecordingIndex = recordingIndex;
                skip.VesselName = traj.VesselName;
                skip.Source = "StateVector";
                skip.Branch = MapResolutionBranch(resolution.Branch);
                skip.Body = point.bodyName;
                skip.AnchorPid = resolution.AnchorPid;
                skip.StateVecAlt = point.altitude;
                skip.StateVecSpeed = point.velocity.magnitude;
                skip.UT = ut;
                skip.Reason = retryResolutionLater
                    ? string.Format(ic, "{0} retryLater=true", resolution.FailureReason ?? "(null)")
                    : (resolution.FailureReason ?? "(null)");
                ParsekLog.Warn(Tag, BuildGhostMapDecisionLine(skip));
                return null;
            }

            // Compute optional recorded-relative metadata for the structured line.
            Vector3d? anchorPosForLog = null;
            Vector3d? localOffsetForLog = null;
            if (resolution.Branch == "relative")
            {
                localOffsetForLog = new Vector3d(point.latitude, point.longitude, point.altitude);
            }

            // Bug #587 third facet: during in-place continuation Re-Fly, the
            // state-vector-fallback path can synthesize a ProtoVessel right
            // next to the active Re-Fly target when the recording is in a
            // Relative-frame section anchored to that very vessel. The result
            // is a "doubled upper-stage" the user sees in scene. Suppress
            // creation here; GhostPlaybackEngine still renders the legitimate
            // in-physics-zone ghost. See
            // docs/dev/plans/refly-doubled-ghostmap-protovessel-fix.md.
            //
            // PR #574 review P2: scope this to the *parent* recording chain of
            // the active Re-Fly recording — a docking-target recording that
            // is Relative-anchored to the active vessel for legitimate #583 /
            // #584 reasons must NOT be suppressed. PR #574 review P2 also
            // adds retry-later semantics: on suppression we set retryLater =
            // true so the flight-scene caller leaves its pending-map entry
            // intact (otherwise the recording would never get a map ghost
            // again after the Re-Fly session ends).
            // #611: at Re-Fly load time the active recording lives in
            // PendingTree (not CommittedTrees, which the load-side
            // RemoveCommittedTreeById call has just emptied for this tree).
            // Compose the search list so the parent-chain walk can find the
            // active recording in either committed or pending state.
            IReadOnlyList<RecordingTree> searchTrees = ComposeSearchTreesForReFlySuppression(
                RecordingStore.CommittedTrees,
                PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null);
            if (ShouldSuppressStateVectorProtoVesselForActiveReFlyAtCreateTime(
                    SessionSuppressionState.ActiveMarker,
                    resolution.Branch,
                    resolution.AnchorPid,
                    traj,
                    ut,
                    traj.RecordingId,
                    RecordingStore.CommittedRecordings,
                    searchTrees,
                    out string activeReFlySuppressReason))
            {
                MarkStateVectorGhostDeferredForActiveReFly(recordingIndex);
                retryLater = true;
                var suppressed = NewDecisionFields("create-state-vector-suppressed");
                suppressed.RecordingId = traj.RecordingId;
                suppressed.RecordingIndex = recordingIndex;
                suppressed.VesselName = traj.VesselName;
                suppressed.Source = "StateVector";
                suppressed.Branch = MapResolutionBranch(resolution.Branch);
                suppressed.Body = point.bodyName;
                suppressed.AnchorPid = resolution.AnchorPid;
                suppressed.AnchorPos = anchorPosForLog;
                suppressed.LocalOffset = localOffsetForLog;
                suppressed.StateVecAlt = point.altitude;
                suppressed.StateVecSpeed = point.velocity.magnitude;
                suppressed.UT = ut;
                suppressed.Reason = string.Format(ic,
                    "{0} sess={1} retryLater=true",
                    activeReFlySuppressReason,
                    SessionSuppressionState.ActiveMarker?.SessionId ?? "<no-id>");
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(suppressed));
                return null;
            }

            // #611: when a Re-Fly session is active but the predicate
            // declined to suppress, emit a Verbose decision line so the
            // playtest log records WHY the gate didn't fire — without this,
            // a successful create-state-vector-done line is the only signal
            // and we can't tell whether the predicate ran or skipped which
            // reject branch it took. Skip when no Re-Fly session is active
            // (the gate is trivially a no-op then; logging would spam every
            // ProtoVessel create).
            if (SessionSuppressionState.ActiveMarker != null
                && !string.IsNullOrEmpty(activeReFlySuppressReason))
            {
                var notSuppressed = NewDecisionFields("create-state-vector-not-suppressed-during-refly");
                notSuppressed.RecordingId = traj.RecordingId;
                notSuppressed.RecordingIndex = recordingIndex;
                notSuppressed.VesselName = traj.VesselName;
                notSuppressed.Source = "StateVector";
                notSuppressed.Branch = MapResolutionBranch(resolution.Branch);
                notSuppressed.Body = point.bodyName;
                notSuppressed.AnchorPid = resolution.AnchorPid;
                notSuppressed.StateVecAlt = point.altitude;
                notSuppressed.StateVecSpeed = point.velocity.magnitude;
                notSuppressed.UT = ut;
                notSuppressed.Reason = string.Format(ic,
                    "{0} sess={1}",
                    activeReFlySuppressReason,
                    SessionSuppressionState.ActiveMarker?.SessionId ?? "<no-id>");
                ParsekLog.Verbose(Tag, BuildGhostMapDecisionLine(notSuppressed));
            }

            Vector3d worldPos = resolution.WorldPos;
            Vector3d vel = ResolveStateVectorSeedVelocity(
                traj, point, body, resolution, "create", recordingIndex, rateLimit: false);

            Orbit orbit = new Orbit();
            OrbitReseed.FromWorldPosAndRecordedVelocity(orbit, body, worldPos, vel, ut);

            string logContext = string.Format(ic,
                "recording #{0} (state vectors alt={1:F0} spd={2:F1} frame={3})",
                recordingIndex, point.altitude, point.velocity.magnitude, resolution.Branch);
            string protoSource = string.IsNullOrEmpty(stateVectorCreateReason)
                ? "state-vector-fallback"
                : stateVectorCreateReason;
            Vessel vessel = BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                protoSource,
                string.Format(ic,
                    "stateBody={0} stateUT={1:F1} stateAlt={2:F0} stateSpeed={3:F1} frame={4} anchorPid={5}",
                    point.bodyName ?? "(null)",
                    ut,
                    point.altitude,
                    point.velocity.magnitude,
                    resolution.Branch,
                    resolution.AnchorPid));
            if (vessel != null)
            {
                TrackRecordingGhostVessel(recordingIndex, traj, vessel);
                lifecycleCreatedThisTick++;

                // Exit-line: full input/output now that the vessel is bound.
                var done = NewDecisionFields("create-state-vector-done");
                done.RecordingId = traj.RecordingId;
                done.RecordingIndex = recordingIndex;
                done.VesselName = traj.VesselName;
                done.Source = "StateVector";
                done.Branch = MapResolutionBranch(resolution.Branch);
                done.Body = body.name;
                done.WorldPos = worldPos;
                done.GhostPid = vessel.persistentId;
                done.AnchorPid = resolution.AnchorPid;
                done.AnchorPos = anchorPosForLog;
                done.LocalOffset = localOffsetForLog;
                done.StateVecAlt = point.altitude;
                done.StateVecSpeed = point.velocity.magnitude;
                done.UT = ut;
                done.Reason = string.IsNullOrEmpty(stateVectorCreateReason)
                    ? string.Format(ic, "formatV={0}", traj.RecordingFormatVersion)
                    : string.Format(ic, "{0} formatV={1}", stateVectorCreateReason, traj.RecordingFormatVersion);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(done));

                StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
                {
                    RecordingId = traj.RecordingId,
                    VesselName = traj.VesselName,
                    GhostPid = vessel.persistentId,
                    Source = "StateVector",
                    Branch = MapResolutionBranch(resolution.Branch),
                    Body = body.name,
                    WorldPos = worldPos,
                    AnchorPid = resolution.AnchorPid,
                    LastUT = ut
                });
            }

            return vessel;
        }

        /// <summary>
        /// Translate the lowercase <see cref="StateVectorWorldFrame.Branch"/>
        /// strings ("absolute" / "relative" / "orbital-checkpoint" /
        /// "no-section") into the capitalised user-facing branch names used by
        /// the structured log lines.
        /// </summary>
        internal static string MapResolutionBranch(string resolutionBranch)
        {
            switch (resolutionBranch)
            {
                case "absolute": return "Absolute";
                case "relative": return "Relative";
                case "body-fixed-primary": return "BodyFixedPrimary";
                case "orbital-checkpoint": return "OrbitalCheckpoint";
                case "no-section": return "no-section";
                default: return resolutionBranch ?? "(n/a)";
            }
        }

        /// <summary>
        /// Update a ghost map ProtoVessel's orbit from interpolated trajectory state vectors.
        /// Used for per-frame orbit updates of physics-only suborbital ghosts.
        /// Handles SOI transitions (body change + orbit renderer rebuild).
        /// Honours the originating TrackSection's <see cref="ReferenceFrame"/> — see
        /// <see cref="CreateGhostVesselFromStateVectors"/> for the contract.
        ///
        /// <paramref name="loopEpochShiftSeconds"/> (default 0) is the Mission-loop epoch shift
        /// <c>liveUT - effUT</c>: the world position/velocity in <paramref name="point"/> were
        /// sampled at the loop-mapped <c>effUT</c> (a past recorded UT), but the map icon renders
        /// at <c>orbit.getPositionAtUT(liveUT)</c>, so the reseeded orbit epoch is pushed forward
        /// by the shift to <c>ut + loopEpochShiftSeconds == liveUT</c>. The icon then sits exactly
        /// at the replayed position now instead of being propagated a fraction of an orbit. Zero
        /// for non-loop ghosts (effUT == liveUT), so behavior is unchanged off the loop path.
        /// </summary>
        internal static bool UpdateGhostOrbitFromStateVectors(
            int recordingIndex, IPlaybackTrajectory traj,
            TrajectoryPoint point,
            double ut,
            bool allowOrbitalCheckpointStateVector = false,
            string stateVectorUpdateReason = null,
            double loopEpochShiftSeconds = 0.0)
        {
            if (!vesselsByRecordingIndex.TryGetValue(recordingIndex, out Vessel vessel))
                return false;

            // Per-frame rebind seam: the index-keyed ghost can outlive the
            // recording that created it (committed-list reorder / stub churn);
            // the trajectory driving THIS update is the truth.
            if (vessel != null && traj != null)
                RebindGhostRecordingId(vessel.persistentId, traj.RecordingId);

            if (vessel.orbitDriver == null)
            {
                LogStateVectorUpdateMiss(recordingIndex, traj, point, vessel, ut, "no-orbit-driver");
                return false;
            }

            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
            {
                LogStateVectorUpdateMiss(recordingIndex, traj, point, vessel, ut, "body-not-found");
                return false;
            }

            StateVectorWorldFrame resolution =
                ResolveStateVectorWorldPosition(
                    traj,
                    point,
                    body,
                    allowOrbitalCheckpointStateVector);
            if (!resolution.Resolved)
            {
                LogStateVectorUpdateSkip(recordingIndex, traj, point, ut, vessel, resolution);
                return false;
            }

            // Compute optional recorded-relative metadata for the structured line.
            Vector3d? anchorPosForLog = null;
            Vector3d? localOffsetForLog = null;
            if (resolution.Branch == "relative")
            {
                localOffsetForLog = new Vector3d(point.latitude, point.longitude, point.altitude);
            }

            if (TrySuppressStateVectorUpdateForActiveReFly(
                    recordingIndex, traj, point, ut, vessel, resolution, anchorPosForLog, localOffsetForLog))
                return true;

            bool soiChanged = ResolveStateVectorSoiChange(
                recordingIndex, traj, ut, vessel, body, resolution, stateVectorUpdateReason);

            Vector3d worldPos = resolution.WorldPos;

            bool inertialPathFired = false;
            string inertialDeclineReason = null;
            double inertialLonForLog = double.NaN;
            if (ShouldSeedGapGlideInertial(stateVectorUpdateReason, resolution.Branch))
                SeedStateVectorGapGlideInertial(
                    recordingIndex, traj, point, loopEpochShiftSeconds, vessel, body,
                    ref inertialPathFired, ref inertialDeclineReason, ref inertialLonForLog);

            ClearStaleSegmentDriveForStateVectorReseed(vessel, recordingIndex, loopEpochShiftSeconds);

            if (!inertialPathFired)
                ReseedStateVectorOrbitBodyFixed(
                    recordingIndex, traj, point, ut, vessel, body, resolution, worldPos, loopEpochShiftSeconds);
            vessel.orbitDriver.updateFromParameters();
            NormalizeGhostOrbitDriverTargetIdentity(vessel, "update-state-vector");

            RefreshStateVectorSeamComd(vessel);

            RebuildRendererAndRecordStateVectorBody(vessel, body, soiChanged);

            lifecycleUpdatedThisTick++;

            LogStateVectorUpdateAndStash(
                recordingIndex, traj, point, ut, vessel, body, resolution, worldPos,
                anchorPosForLog, localOffsetForLog, stateVectorUpdateReason,
                inertialPathFired, inertialLonForLog, inertialDeclineReason);
            return false;
        }

        /// <summary>
        /// The update-state-vector-miss line of a state-vector update that cannot run: the ghost
        /// has no OrbitDriver, or the point's body is not found (<paramref name="reason"/>).
        /// </summary>
        internal static void LogStateVectorUpdateMiss(
            int recordingIndex, IPlaybackTrajectory traj, TrajectoryPoint point, Vessel vessel,
            double ut, string reason)
        {
            var miss = NewDecisionFields("update-state-vector-miss");
            miss.RecordingId = traj?.RecordingId;
            miss.RecordingIndex = recordingIndex;
            miss.VesselName = traj?.VesselName;
            miss.Source = "StateVector";
            miss.Body = point.bodyName;
            miss.GhostPid = vessel.persistentId;
            miss.UT = ut;
            miss.Reason = reason;
            ParsekLog.Error(Tag, BuildGhostMapDecisionLine(miss));
        }

        /// <summary>
        /// The update-state-vector-skip line of a state-vector update whose world position did not
        /// resolve (the resolution's branch and failure reason).
        /// </summary>
        internal static void LogStateVectorUpdateSkip(
            int recordingIndex, IPlaybackTrajectory traj, TrajectoryPoint point, double ut,
            Vessel vessel, StateVectorWorldFrame resolution)
        {
            var skip = NewDecisionFields("update-state-vector-skip");
            skip.RecordingId = traj?.RecordingId;
            skip.RecordingIndex = recordingIndex;
            skip.VesselName = traj?.VesselName;
            skip.Source = "StateVector";
            skip.Branch = MapResolutionBranch(resolution.Branch);
            skip.Body = point.bodyName;
            skip.GhostPid = vessel.persistentId;
            skip.AnchorPid = resolution.AnchorPid;
            skip.StateVecAlt = point.altitude;
            skip.StateVecSpeed = point.velocity.magnitude;
            skip.UT = ut;
            skip.Reason = resolution.FailureReason ?? "(null)";
            ParsekLog.Warn(Tag, BuildGhostMapDecisionLine(skip));
        }

        /// <summary>
        /// Active Re-Fly suppression of a state-vector update: when the resolved frame is anchored
        /// to the vessel being re-flown, marks the ghost deferred for the session, logs
        /// update-state-vector-suppressed and returns true (the caller tears the ghost down).
        /// </summary>
        internal static bool TrySuppressStateVectorUpdateForActiveReFly(
            int recordingIndex, IPlaybackTrajectory traj, TrajectoryPoint point, double ut,
            Vessel vessel, StateVectorWorldFrame resolution, Vector3d? anchorPosForLog,
            Vector3d? localOffsetForLog)
        {
            IReadOnlyList<RecordingTree> searchTrees = ComposeSearchTreesForReFlySuppression(
                RecordingStore.CommittedTrees,
                PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null);
            if (ShouldRemoveStateVectorProtoVesselForActiveReFlyOnUpdate(
                    SessionSuppressionState.ActiveMarker,
                    resolution.Branch,
                    resolution.AnchorPid,
                    traj?.RecordingId,
                    RecordingStore.CommittedRecordings,
                    searchTrees,
                    out string activeReFlySuppressReason))
            {
                MarkStateVectorGhostDeferredForActiveReFly(recordingIndex);
                var suppressed = NewDecisionFields("update-state-vector-suppressed");
                suppressed.RecordingId = traj?.RecordingId;
                suppressed.RecordingIndex = recordingIndex;
                suppressed.VesselName = traj?.VesselName;
                suppressed.Source = "StateVector";
                suppressed.Branch = MapResolutionBranch(resolution.Branch);
                suppressed.Body = point.bodyName;
                suppressed.WorldPos = resolution.WorldPos;
                suppressed.GhostPid = vessel.persistentId;
                suppressed.AnchorPid = resolution.AnchorPid;
                suppressed.AnchorPos = anchorPosForLog;
                suppressed.LocalOffset = localOffsetForLog;
                suppressed.StateVecAlt = point.altitude;
                suppressed.StateVecSpeed = point.velocity.magnitude;
                suppressed.UT = ut;
                suppressed.Reason = string.Format(ic,
                    "{0} sess={1} removeReason={2}",
                    activeReFlySuppressReason,
                    SessionSuppressionState.ActiveMarker?.SessionId ?? "<no-id>",
                    TrackingStationGhostSkipActiveReFlyRelativeUpdate);
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(suppressed));
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when the body about to be applied differs from the one Parsek last applied to this
        /// ghost (or none was applied); logs update-state-vector-soi-change when it does.
        /// </summary>
        internal static bool ResolveStateVectorSoiChange(
            int recordingIndex, IPlaybackTrajectory traj, double ut, Vessel vessel, CelestialBody body,
            StateVectorWorldFrame resolution, string stateVectorUpdateReason)
        {
            // SOI transition handling (same pattern as ApplyOrbitToVessel): compare against the body
            // PARSEK last applied, not orbitDriver.referenceBody (a KSP-owned field), so the renderer
            // rebuild fires once per genuine Parsek-driven body change. (KSP's per-frame SOI transition
            // is now blocked for ghosts by GhostOrbitDominantBodyPatch; this stays as the correct
            // invariant + avoids a redundant rebuild.) OrbitDriver.celestialBody is only for real
            // CelestialBody drivers; vessel targets must keep identity in OrbitDriver.vessel.
            ghostLastAppliedOrbitBody.TryGetValue(vessel.persistentId, out string lastAppliedBody);
            bool soiChanged = GhostOrbitBodyChanged(lastAppliedBody, body.name);
            if (soiChanged)
            {
                var soi = NewDecisionFields("update-state-vector-soi-change");
                soi.RecordingId = traj?.RecordingId;
                soi.RecordingIndex = recordingIndex;
                soi.VesselName = traj?.VesselName;
                soi.Source = "StateVector";
                soi.Branch = MapResolutionBranch(resolution.Branch);
                soi.Body = body.name;
                soi.GhostPid = vessel.persistentId;
                soi.UT = ut;
                soi.Reason = stateVectorUpdateReason;
                ParsekLog.Info(Tag, BuildGhostMapDecisionLine(soi));
            }
            return soiChanged;
        }

        /// <summary>
        /// Orbit-raise gap glide: seeds the ghost orbit from the inertial position reconstructed
        /// from the glide sample and its neighbour. Sets <paramref name="inertialPathFired"/> when
        /// it seeded, otherwise the decline reason (the caller falls back to the body-fixed seed).
        /// </summary>
        internal static void SeedStateVectorGapGlideInertial(
            int recordingIndex, IPlaybackTrajectory traj, TrajectoryPoint point,
            double loopEpochShiftSeconds, Vessel vessel, CelestialBody body,
            ref bool inertialPathFired, ref string inertialDeclineReason, ref double inertialLonForLog)
        {
            // Orbit-raise gap glide: reconstruct the recorded INERTIAL position. The body-fixed
            // FromWorldPosAndRecordedVelocity reseed resolves the gap point via
            // body.GetWorldSurfacePosition at the LIVE body orientation; on a loop replayed ~1e9 s
            // later the body has rotated, so the body-fixed gap rendering sits rotated off the
            // inertial orbit lines drawn from recorded Keplerian elements. Reconstruct the point's
            // inertial position from its recorded UT + body rotation (incl. initialRotation) so the
            // parking -> raise -> loiter -> escape arc lives in one inertial frame (no teleport at the
            // raise->loiter seam). Gated to the orbit-raise-gap-points reason on an Absolute branch
            // ONLY; the launch ascent (no preceding OrbitSegment) never reaches this path via
            // ShouldDriveGapFromPoints, and SOI-gap / checkpoint / terminal callers pass other reasons.
            // The recorded velocity's frame and world yaw are resolved from the glide sample
            // and its neighbour (a parking orbit below the inverse-rotation altitude records
            // surface-relative vectors), as the endpoint-tail map seed does.
            if (!OrbitSeedResolver.TryResolveRotationPeriod(body, out double gapRotationPeriod))
            {
                inertialDeclineReason = "historical-rotation-unavailable";
            }
            else if (!OrbitReseed.TrySelectGapGlidePair(
                    traj?.Points,
                    traj?.TrackSections,
                    point,
                    out TrajectoryPoint gapPrevious,
                    out TrajectoryPoint gapTail,
                    out string gapPairReason))
            {
                inertialDeclineReason = gapPairReason;
            }
            else
            {
                double gapInitialRotation = OrbitSeedResolver.ResolveInitialRotation(body);
                if (OrbitReseed.TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch(
                        vessel.orbitDriver.orbit,
                        body,
                        gapPrevious,
                        gapTail,
                        gapTail.ut + loopEpochShiftSeconds,
                        gapRotationPeriod,
                        gapInitialRotation,
                        out OrbitReseed.HistoricalTailPairSeedInputs gapSeed,
                        traj?.RecordingId))
                {
                    inertialPathFired = true;
                    inertialLonForLog = gapSeed.InertialLongitude;
                    gapGlideInertialSeedCount++;
                }
                else
                {
                    inertialDeclineReason = gapSeed.FailureReason ?? "historical-helper-declined";
                }
            }

            if (!inertialPathFired)
            {
                gapGlideBodyFixedFallbackCount++;
                // Rate-limited per ghost and reason: a sample with no same-section
                // neighbour declines on every frame the glide dwells on it.
                ParsekLog.WarnRateLimited(Tag,
                    string.Format(ic, "gap-glide-inertial-decline-{0}-{1}",
                        vessel.persistentId, inertialDeclineReason ?? "(null)"),
                    string.Format(ic,
                        "Gap-glide inertial seed declined for ghost pid={0} recIndex={1} body={2} "
                        + "recordedUT={3:F2} shift={4:F1} reason={5}: falling back to body-fixed seed "
                        + "(icon may rotate off the inertial orbit lines this frame)",
                        vessel.persistentId, recordingIndex, body.name, point.ut,
                        loopEpochShiftSeconds, inertialDeclineReason ?? "(null)"),
                    5.0);
            }

            // Aggregate proof-of-fire summary (shared key, ~5s window): a KSP.log capture must
            // show inertial>0 for the looped re-aim playtest; inertial==0 means the gate never
            // engaged (the no-op trap). bodyFixed counts the safety fallbacks.
            ParsekLog.VerboseRateLimited(Tag,
                "gap-glide-inertial-summary",
                string.Format(ic,
                    "Gap-glide seed summary: inertial={0} bodyFixed={1} "
                    + "(orbit-raise gaps reconstructed in inertial frame)",
                    gapGlideInertialSeedCount, gapGlideBodyFixedFallbackCount),
                5.0);
        }

        /// <summary>
        /// Drops the segment-drive state (orbit bounds, loop shift, epoch shift, icon-drive
        /// propagate UT) a previous segment phase left for this ghost, before a state-vector reseed.
        /// </summary>
        internal static void ClearStaleSegmentDriveForStateVectorReseed(
            Vessel vessel, int recordingIndex, double loopEpochShiftSeconds)
        {
            // The state-vector path keeps the OLD contract: a SHIFTED-epoch orbit (seeded at
            // ut + loopEpochShiftSeconds == liveUT) that stock propagates at the LIVE Planetarium
            // clock. The segment-driven raw-epoch + effUT-drive contract (GhostOrbitIconDrivePatch)
            // must therefore NOT engage for this ghost. If this pid was previously a covering
            // OrbitSegment ghost (a loop mission crossing from a parking-orbit segment into a
            // transfer-coast OrbitalCheckpoint gap updates the SAME ghost in place, especially in
            // the Tracking Station where the dispatcher does not remove+recreate it), the prior
            // segment phase left stale segment bounds + loop-shift + epoch-shift entries. The
            // now-authoritative drive patch would read those stale dicts and re-subtract a shift
            // from this already-shifted state-vector orbit, mis-positioning or freezing the icon and
            // suppressing it. Clear them here (BEFORE updateFromParameters so even the in-call frame
            // defers to stock) so TryGetVisibleOrbitBoundsForGhostVessel returns false, the drive
            // patch returns true, and stock correctly propagates the shifted-epoch orbit at live UT.
            bool hadStaleSegmentDrive =
                ghostOrbitBounds.Remove(vessel.persistentId)
                | ghostBodyFrameOrbitBounds.Remove(vessel.persistentId)
                | ghostOrbitLoopShiftedPids.Remove(vessel.persistentId)
                | ghostOrbitEpochShift.Remove(vessel.persistentId);
            // Drop the icon-drive propagateUT record at this segment->state-vector handoff too: the
            // drive patch now returns true (stock re-propagates the orbit at live UT), so the icon-drive
            // Prefix stops recording, and a record left from the pre-reseed segment frames would let the
            // probe trust a stale phase for up to SeedFreshnessFrames after the icon has moved to live.
            // Separate statement (NOT folded into the hadStaleSegmentDrive OR-chain) so it never flips
            // that flag on a propagateUT-only entry.
            ghostIconDrivePropagation.Remove(vessel.persistentId);
            if (hadStaleSegmentDrive)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "State-vector reseed cleared stale segment-drive state for ghost pid={0} "
                        + "recIndex={1} (segment->state-vector transition, shift={2:F1}, scene={3}): "
                        + "icon now stock-propagated at live UT off the effUT drive path",
                        vessel.persistentId, recordingIndex, loopEpochShiftSeconds,
                        HighLogic.LoadedScene));
            }
        }

        /// <summary>
        /// Body-fixed state-vector seed: rebuilds the ghost orbit from the resolved world position
        /// and the recorded velocity at the loop-shifted epoch <c>ut + loopEpochShiftSeconds</c>.
        /// </summary>
        internal static void ReseedStateVectorOrbitBodyFixed(
            int recordingIndex, IPlaybackTrajectory traj, TrajectoryPoint point, double ut,
            Vessel vessel, CelestialBody body, StateVectorWorldFrame resolution, Vector3d worldPos,
            double loopEpochShiftSeconds)
        {
            // Loop replay: push the orbit epoch forward by (liveUT - effUT) so the icon, drawn at
            // getPositionAtUT(liveUT), lands on the world position recorded at effUT instead of
            // being propagated forward along the ellipse. Zero shift for non-loop ghosts. Skipped
            // when the orbit-raise inertial seed already seeded the orbit (it seeded at the
            // same loop-shifted epoch via the reconstructed inertial position) so we don't re-seed
            // it back into the body-fixed frame; the body-fixed seed still runs for every other
            // reason and as the safety fallback when the inertial reconstruction declined.
            Vector3d vel = ResolveStateVectorSeedVelocity(
                traj, point, body, resolution, "update", recordingIndex, rateLimit: true);
            OrbitReseed.FromWorldPosAndRecordedVelocity(
                vessel.orbitDriver.orbit,
                body,
                worldPos,
                vel,
                ut + loopEpochShiftSeconds);
        }

        /// <summary>
        /// After a state-vector reseed, re-snaps the cached CoMD onto the new conic when the conic
        /// changed (the seam), and records the applied elements.
        /// </summary>
        internal static void RefreshStateVectorSeamComd(Vessel vessel)
        {
            // Seam CoMD re-snap (Bug A loop-icon-warp-lag): this state-vector reseed runs in a Parsek
            // per-frame pass AFTER this frame's VesselPrecalculate.FixedUpdate, so the cached
            // Vessel.CoMD (the value Vessel.GetWorldPos3D returns, and what the map icon renders at)
            // still holds the PRE-reseed conic phase - while the orbit LINE is drawn from the
            // freshly-reseeded elements. updateFromParameters() above propagated orbitDriver.pos to the
            // live clock and moved the vessel TRANSFORM via SetPosition, but SetPosition does NOT touch
            // CoMD, and GetWorldPos3D returns the cached CoMD (only self-recomputing while
            // !firstStatsRunComplete - true only at creation, which is why ForceImmediateIconDrive
            // works at creation but a steady-state re-drive does not). So at high warp the
            // (currentUT - reseed-epoch) gap leaves the icon up to ~168 deg off its own line for one
            // frame until the NEXT FixedUpdate refreshes CoMD (the loopShift=0.0 icon-off-orbit /
            // icon-teleport anomaly, rec 04177 idx 43). Force the same CalculatePhysicsStats() refresh
            // GetWorldPos3D itself uses (it sets CoMD = mainBody.position + orbitDriver.pos for a packed
            // orbiting ghost) so the icon lands on the conic THIS frame. The director-drive path needs
            // no equivalent: its reseed runs INSIDE FixedUpdate alongside CalculatePhysicsStats, so its
            // CoMD is never stale (its frames stay < 3 deg in the capture). Gated to the conic-change
            // seam (cheap sma/ecc/epoch compare) so steady-state reseeds add no per-ghost cost.
            Orbit svOrbit = vessel.orbitDriver.orbit;
            if (svOrbit != null)
            {
                bool svHadLast = ghostLastAppliedOrbitElements.TryGetValue(
                    vessel.persistentId, out var svLast);
                // The reseed seeds orbit.epoch = ut + loopEpochShiftSeconds, which advances every live
                // frame at warp, so the epoch facet (1e-3 s) trips on every genuine reseed - exactly the
                // frames CoMD is stale - and an unchanged conic (same recorded point re-applied) skips.
                bool svSeam = GhostOrbitElementsChanged(
                    svHadLast, svLast.sma, svLast.ecc, svLast.epoch,
                    svOrbit.semiMajorAxis, svOrbit.eccentricity, svOrbit.epoch);
                if (svSeam)
                {
                    if (vessel.precalc != null)
                    {
                        vessel.precalc.CalculatePhysicsStats();
                        seamComdRefreshCount++;
                        if (MapRenderTrace.IsEnabled)
                        {
                            double postAngle = SeamIconOffOrbitAngleDeg(
                                vessel, svOrbit, Planetarium.GetUniversalTime());
                            if (!double.IsNaN(postAngle))
                                seamComdMaxOffOrbitDegPostRefresh = System.Math.Max(
                                    seamComdMaxOffOrbitDegPostRefresh, postAngle);
                        }
                    }
                    ghostLastAppliedOrbitElements[vessel.persistentId] =
                        (svOrbit.semiMajorAxis, svOrbit.eccentricity, svOrbit.epoch);
                }
                else
                {
                    seamComdSteadyCount++;
                }

                // Shared-key proof-of-fire (no-op trap guard, PR-#885 lesson): a KSP.log capture of a
                // warp re-fly must show refreshed>0 (the seam path engaged) and, with mapRenderTracing
                // on, maxOffDeg<1 (the icon re-snapped onto its conic). steady>0 confirms the seam gate
                // is filtering rather than refreshing every frame. The IsVerboseEnabled guard keeps the
                // string.Format off the per-ghost per-frame path in normal (verbose-off) play - the
                // counters above are int/double increments, the only steady-state cost.
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited(Tag, "seam-comd-refresh-summary",
                        string.Format(ic,
                            "Seam CoMD re-snap summary: refreshed={0} steady={1} maxOffDegPostRefresh={2:F2} "
                            + "(state-vector reseed re-snaps the icon CoMD onto its conic at live UT)",
                            seamComdRefreshCount, seamComdSteadyCount, seamComdMaxOffOrbitDegPostRefresh),
                        5.0);
            }
        }

        /// <summary>
        /// Rebuilds the ghost's orbit renderer when the applied body changed, and records the body
        /// just applied as the one Parsek last applied to this ghost.
        /// </summary>
        internal static void RebuildRendererAndRecordStateVectorBody(
            Vessel vessel, CelestialBody body, bool soiChanged)
        {
            if (soiChanged && vessel.orbitRenderer != null)
            {
                vessel.orbitRenderer.drawMode = OrbitRendererBase.DrawMode.REDRAW_AND_RECALCULATE;
                vessel.orbitRenderer.enabled = false;
                vessel.orbitRenderer.enabled = true;
            }

            // Remember the body we just applied (see GhostOrbitBodyChanged / ghostLastAppliedOrbitBody).
            ghostLastAppliedOrbitBody[vessel.persistentId] = body.name;
        }

        /// <summary>
        /// The rate-limited update-state-vector line of a completed update, and the last-known
        /// frame stash for the ghost's recording index.
        /// </summary>
        internal static void LogStateVectorUpdateAndStash(
            int recordingIndex, IPlaybackTrajectory traj, TrajectoryPoint point, double ut,
            Vessel vessel, CelestialBody body, StateVectorWorldFrame resolution, Vector3d worldPos,
            Vector3d? anchorPosForLog, Vector3d? localOffsetForLog, string stateVectorUpdateReason,
            bool inertialPathFired, double inertialLonForLog, string inertialDeclineReason)
        {
            // Per-recording rate-limited line — one entry per recording per ~5s
            // window so a long warp pass leaves a readable trace without spam.
            string updateKey = string.Format(ic,
                "state-vector-update-{0}",
                traj?.RecordingId ?? recordingIndex.ToString(ic));
            var done = NewDecisionFields("update-state-vector");
            done.RecordingId = traj?.RecordingId;
            done.RecordingIndex = recordingIndex;
            done.VesselName = traj?.VesselName;
            done.Source = "StateVector";
            done.Branch = MapResolutionBranch(resolution.Branch);
            done.Body = body.name;
            done.WorldPos = worldPos;
            done.GhostPid = vessel.persistentId;
            done.AnchorPid = resolution.AnchorPid;
            done.AnchorPos = anchorPosForLog;
            done.LocalOffset = localOffsetForLog;
            done.StateVecAlt = point.altitude;
            done.StateVecSpeed = point.velocity.magnitude;
            done.UT = ut;
            // For the orbit-raise gap glide, annotate which frame seeded the orbit (inertial
            // reconstruction vs body-fixed live-rotation fallback) and the reconstructed inertial
            // longitude, so each driven frame self-identifies in the post-hoc log.
            done.Reason = ShouldSeedGapGlideInertial(stateVectorUpdateReason, resolution.Branch)
                ? string.Format(ic,
                    "{0} seedFrame={1} inertialLon={2:F4}{3}",
                    stateVectorUpdateReason,
                    inertialPathFired ? "inertial" : "body-fixed",
                    inertialLonForLog,
                    inertialPathFired ? "" : (" decline=" + (inertialDeclineReason ?? "(null)")))
                : stateVectorUpdateReason;
            ParsekLog.VerboseRateLimited(Tag, updateKey, BuildGhostMapDecisionLine(done), 5.0);

            StashLastKnownFrame(recordingIndex, new LastKnownGhostFrame
            {
                RecordingId = traj?.RecordingId,
                VesselName = traj?.VesselName,
                GhostPid = vessel.persistentId,
                Source = "StateVector",
                Branch = MapResolutionBranch(resolution.Branch),
                Body = body.name,
                WorldPos = worldPos,
                AnchorPid = resolution.AnchorPid,
                LastUT = ut
            });
        }

        /// <summary>
        /// True when the ghost orbit body Parsek is about to apply differs from the body it last applied
        /// to this ghost (or nothing has been applied yet). Gates the orbit-renderer rebuild + "SOI
        /// change" log so they fire once per genuine Parsek-driven body change, not every frame KSP
        /// transiently re-transitions the unloaded ghost's <c>OrbitDriver.referenceBody</c> between
        /// reseeds (the cause of the transfer-leg orbit-line blink). Pure. See
        /// <see cref="ghostLastAppliedOrbitBody"/>.
        /// </summary>
        internal static bool GhostOrbitBodyChanged(string lastAppliedBodyName, string newBodyName)
        {
            return string.IsNullOrEmpty(lastAppliedBodyName)
                || !string.Equals(lastAppliedBodyName, newBodyName, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the orbit elements about to be applied to a ghost differ from the last-applied
        /// triple for it (or nothing was applied yet, or any NEW element is non-finite). Gates the
        /// seam CoMD re-snap in <see cref="UpdateGhostOrbitFromStateVectors"/> so it fires once per
        /// genuine conic change, not on every steady-state reseed of the same recorded point - keeping
        /// the per-ghost per-frame cost flat (the visual-efficiency invariant). Pure / Unity-free.
        /// Epsilons: sma relative 1e-6 (metre-scale orbits), ecc absolute 1e-9, epoch absolute 1e-3 s.
        /// A non-finite NEW element counts as CHANGED so the gate never suppresses and strands a stale
        /// icon (the downstream CalculatePhysicsStats tolerates it and the next FixedUpdate corrects).
        /// </summary>
        internal static bool GhostOrbitElementsChanged(
            bool hadLast, double lastSma, double lastEcc, double lastEpoch,
            double newSma, double newEcc, double newEpoch)
        {
            if (!hadLast)
                return true;
            if (double.IsNaN(newSma) || double.IsInfinity(newSma)
                || double.IsNaN(newEcc) || double.IsInfinity(newEcc)
                || double.IsNaN(newEpoch) || double.IsInfinity(newEpoch))
                return true;
            double smaTol = System.Math.Max(1.0, System.Math.Abs(lastSma) * 1e-6);
            return System.Math.Abs(newSma - lastSma) > smaTol
                || System.Math.Abs(newEcc - lastEcc) > 1e-9
                || System.Math.Abs(newEpoch - lastEpoch) > 1e-3;
        }

        /// <summary>
        /// Pure angle (deg) between the icon's body-relative world position and the conic's
        /// body-relative position at the same UT - the SAME quantity MapRenderProbe's icon-off-orbit
        /// anomaly measures. Returns NaN for a degenerate / unresolved input (either vector below a
        /// 1 m floor) so the proof summary skips it instead of logging a phantom 90 deg (the angle of
        /// a zero vector). Unity-math only (<see cref="Vector3d.Angle"/>); xUnit-tested directly.
        /// </summary>
        internal static double IconVsOrbitAngleDeg(Vector3d bodyRelIcon, Vector3d bodyRelOrbitAtUT)
        {
            if (bodyRelIcon.sqrMagnitude <= 1.0 || bodyRelOrbitAtUT.sqrMagnitude <= 1.0)
                return double.NaN;
            double a = Vector3d.Angle(bodyRelIcon, bodyRelOrbitAtUT);
            return double.IsNaN(a) ? double.NaN : a;
        }

        /// <summary>
        /// Post-refresh proof metric: angle between the ghost icon's actual body-relative world
        /// position (<see cref="Vessel.GetWorldPos3D"/> - referenceBody.position, i.e. the cached CoMD
        /// just refreshed by CalculatePhysicsStats) and the conic's body-relative position at
        /// <paramref name="currentUT"/> (the phase the line draws). ~0 after the re-snap. Mirrors
        /// MapRenderProbe.OrbitRelativePositionYup (getRelativePositionAtUT + Swizzle) so the metric
        /// agrees with the probe's anomaly. NaN-safe; called only under MapRenderTrace.IsEnabled.
        /// </summary>
        internal static double SeamIconOffOrbitAngleDeg(Vessel vessel, Orbit orbit, double currentUT)
        {
            if (vessel == null || orbit == null || orbit.referenceBody == null)
                return double.NaN;
            Vector3d bodyRelIcon = vessel.GetWorldPos3D() - orbit.referenceBody.position;
            Vector3d rel = orbit.getRelativePositionAtUT(currentUT);
            rel.Swizzle();
            return IconVsOrbitAngleDeg(bodyRelIcon, rel);
        }

        /// <summary>
        /// Pure gate for the orbit-raise gap glide's inertial-frame reconstruction inside
        /// <see cref="UpdateGhostOrbitFromStateVectors"/>. The inertial branch fires ONLY when the
        /// state-vector update came from the gap-glide call sites (reason
        /// <c>"orbit-raise-gap-points"</c>, passed only by the flight + TS gap branches) AND the
        /// world position was resolved through the Absolute branch (body-fixed geographic
        /// lat/lon/alt). Every other reason (SOI-gap checkpoint, terminal fallback, null) and every
        /// other branch (relative anchor-local offsets, body-fixed-primary debris, orbital-checkpoint)
        /// keeps the unchanged body-fixed live-rotation seed. The orbital-vacuum-vs-launch-ascent
        /// discrimination is enforced upstream by <see cref="ShouldDriveGapFromPoints"/> (which
        /// requires an OrbitSegment bracket on both sides); the launch ascent has no preceding orbit
        /// segment so it never reaches the gap-glide reason. Pure.
        /// </summary>
        internal static bool ShouldSeedGapGlideInertial(string stateVectorUpdateReason, string resolutionBranch)
        {
            return string.Equals(stateVectorUpdateReason, "orbit-raise-gap-points", System.StringComparison.Ordinal)
                && string.Equals(resolutionBranch, "absolute", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Shared: apply an OrbitSegment's Keplerian elements to a ghost vessel's OrbitDriver.
        /// Handles body resolution, orbit construction, SOI transitions, and logging.
        /// </summary>
        /// <summary>
        /// Creation-frame icon fix (2026-06-12 retest): pv.Load positions the proto at the RAW
        /// recorded-epoch orbit, and the per-frame effUT icon drive (GhostOrbitIconDrivePatch on
        /// OrbitDriver.updateFromParameters) only repositions it on the NEXT OrbitDriver tick - so
        /// the icon's first rendered frame sat up to ~143 degrees off its line (a visible blip at
        /// high warp, flagged by the tracer's creation-frame icon-off-orbit/teleport anomalies).
        /// One explicit drive call AFTER the orbit + loop epoch shift are registered routes through
        /// the same Harmony patch and lands the icon at the loop effUT before the frame renders.
        /// </summary>
        private static void ForceImmediateIconDrive(Vessel vessel, string context)
        {
            if (vessel == null || vessel.orbitDriver == null)
                return;
            vessel.orbitDriver.updateFromParameters();
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Forced first icon drive after {0} create: pid={1}",
                    context, vessel.persistentId));
        }
    }
}
