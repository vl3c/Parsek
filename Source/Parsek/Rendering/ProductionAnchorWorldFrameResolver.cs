using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Rendering
{
    /// <summary>
    /// Production <see cref="IAnchorWorldFrameResolver"/> — backed by live
    /// KSP API (<see cref="FlightGlobals"/>, <see cref="CelestialBody.GetWorldSurfacePosition"/>,
    /// <see cref="Orbit.getPositionAtUT"/>). Each method composes existing
    /// helpers (<see cref="TrajectoryMath.ResolveRelativePlaybackPosition"/>,
    /// <see cref="TrajectoryMath.FindOrbitSegment"/>) — no new physics math
    /// here, just the dispatch glue Phase 6 needs to translate stored
    /// <see cref="AnchorCandidate"/> metadata into world-frame ε references.
    ///
    /// <para>
    /// The resolver never throws on missing data: each TryResolve* returns
    /// <c>false</c> + an empty position when its inputs are unavailable
    /// (no live vessel, no checkpoint, no body-fixed primary). The propagator
    /// then logs the failure and leaves ε = 0 for that slot.
    /// </para>
    /// </summary>
    internal sealed class ProductionAnchorWorldFrameResolver : IAnchorWorldFrameResolver
    {
        // The live world this resolver reads: the body lookup, the placement of a body-fixed
        // sample (the body's rotation at the rebuild) and the body's world rotation. Tests hand
        // in a modelled world through the internal constructor.
        private readonly Func<string, CelestialBody> bodyLookup;
        private readonly Func<TrajectoryPoint, Vector3d> absoluteWorldPosition;
        private readonly Func<TrajectoryPoint, Quaternion> bodyWorldRotation;

        public ProductionAnchorWorldFrameResolver()
            : this(ResolveBody, null, null)
        {
        }

        internal ProductionAnchorWorldFrameResolver(
            Func<string, CelestialBody> bodyLookup,
            Func<TrajectoryPoint, Vector3d> absoluteWorldPosition,
            Func<TrajectoryPoint, Quaternion> bodyWorldRotation)
        {
            this.bodyLookup = bodyLookup ?? ResolveBody;
            this.absoluteWorldPosition = absoluteWorldPosition ?? ResolveAbsoluteWorldPositionFrom(this.bodyLookup);
            this.bodyWorldRotation = bodyWorldRotation ?? ResolveBodyWorldRotationFrom(this.bodyLookup);
        }

        public bool TryResolveRelativeBoundaryWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null || rec.TrackSections == null) return false;
            if (sectionIndex < 0 || sectionIndex >= rec.TrackSections.Count) return false;

            // The candidate's sectionIndex is the ABSOLUTE side of the
            // boundary by AnchorCandidateBuilder construction. The adjacent
            // RELATIVE section is the previous index when side==Start, and
            // the next index when side==End.
            int relIdx = (side == AnchorSide.Start) ? sectionIndex - 1 : sectionIndex + 1;
            if (relIdx < 0 || relIdx >= rec.TrackSections.Count) return false;

            TrackSection relSection = rec.TrackSections[relIdx];
            if (relSection.referenceFrame != ReferenceFrame.Relative) return false;

            RelativeAnchorResolveFailure chainFailure = default;
            bool boundaryHasExactBodyFixedPrimary = TryFindExactBoundaryFrameSample(
                relSection.bodyFixedFrames,
                boundaryUT,
                side,
                emitLog: false,
                pt: out _);
            if (relSection.frames == null || relSection.frames.Count == 0)
            {
                chainFailure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "relative-boundary-frames-missing",
                    rec.RecordingId,
                    relSection.anchorRecordingId,
                    boundaryUT,
                    relIdx);
            }
            else if (!TryFindBoundaryFrameSample(
                         relSection.frames,
                         boundaryUT,
                         side,
                         out TrajectoryPoint pt))
            {
                chainFailure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.OutOfSectionRange,
                    "relative-boundary-frame-missing",
                    rec.RecordingId,
                    relSection.anchorRecordingId,
                    boundaryUT,
                    relIdx);
            }
            else if (!TryBuildRelativeAnchorResolverContext(
                         rec,
                         out RelativeAnchorResolverContext context))
            {
                chainFailure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "focus-tree-missing",
                    rec.RecordingId,
                    relSection.anchorRecordingId,
                    pt.ut,
                    relIdx);
            }
            else if (TryResolveKnownRelativeBoundaryPose(
                         context,
                         rec,
                         relSection,
                         relIdx,
                         pt.ut,
                         out AnchorPose pose,
                         out chainFailure))
            {
                worldPos = pose.WorldPos;
                if (IsFinite(worldPos))
                    return true;

                chainFailure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PoseNonFinite,
                    "relative-pose-nonfinite",
                    rec.RecordingId,
                    relSection.anchorRecordingId,
                    pt.ut,
                    relIdx);
            }

            if (!chainFailure.HasFailure)
            {
                chainFailure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "relative-boundary-chain-unresolved",
                    rec.RecordingId,
                    relSection.anchorRecordingId,
                    boundaryUT,
                    relIdx);
            }

            if (TryResolveRelativeBoundaryShadowWorldPos(
                    rec,
                    relSection,
                    boundaryUT,
                    side,
                    absoluteWorldPosition,
                    out worldPos))
            {
                ParsekLog.VerboseRateLimited("Pipeline-Anchor",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "relative-boundary-shadow-fallback|{0}|{1}|{2}",
                        rec.RecordingId ?? "(none)",
                        relIdx,
                        side),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "relative-boundary-shadow-fallback recordingId={0} relSectionIndex={1} side={2} boundaryUT={3:R} anchorRecordingId={4}",
                        rec.RecordingId ?? "(none)",
                        relIdx,
                        side,
                        boundaryUT,
                        relSection.anchorRecordingId ?? "(missing)")
                        + string.Format(
                            CultureInfo.InvariantCulture,
                            " chainOutcome={0} chainReason={1}",
                            chainFailure.Outcome,
                            RelativeAnchorResolveFailure.ReasonOrFallback(chainFailure, "(none)")),
                    5.0);
                return true;
            }

            ParsekLog.WarnRateLimited("Pipeline-Anchor",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "relative-boundary-chain-unresolved|{0}|{1}|{2}",
                    rec.RecordingId ?? "(none)",
                    relIdx,
                    side),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "relative-boundary-chain-unresolved recordingId={0} relSectionIndex={1} side={2} boundaryUT={3:R} anchorRecordingId={4} outcome={5} reason={6} boundaryHasAbsoluteShadow={7}",
                    rec.RecordingId ?? "(none)",
                    relIdx,
                    side,
                    boundaryUT,
                    relSection.anchorRecordingId ?? "(missing)",
                    chainFailure.Outcome,
                    RelativeAnchorResolveFailure.ReasonOrFallback(chainFailure, "(none)"),
                    boundaryHasExactBodyFixedPrimary),
                5.0);
            return false;
        }

        internal static bool TryResolveKnownRelativeBoundaryPose(
            RelativeAnchorResolverContext context,
            Recording rec,
            TrackSection relSection,
            int relIdx,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            return RelativeAnchorResolver.TryResolveRelativeSectionPose(
                context,
                rec,
                relSection,
                relIdx,
                ut,
                new HashSet<string>(StringComparer.Ordinal),
                out pose,
                out failure);
        }

        public bool TryResolveLoopAnchorWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double sampleUT, out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null) return false;
            if (rec.LoopAnchorVesselId == 0u) return false;
            Vessel anchor = TryFindVesselByPid(rec.LoopAnchorVesselId);
            if (anchor == null) return false;
            try
            {
                // Match the recorder's live-anchor frame: vesselTransform.position
                // (FlightRecorder / BackgroundRecorder TryResolveAnchorPoseForCandidate
                // and TryResolveLoopLiveAnchorPose). GetWorldPos3D (CoMD) would
                // disagree with the recorded loop offsets by the parent's
                // CoM-to-vesselTransform vector.
                worldPos = anchor.transform != null
                    ? (Vector3d)anchor.transform.position
                    : anchor.GetWorldPos3D();
            }
            catch
            {
                return false;
            }
            return IsFinite(worldPos);
        }

        // --- helpers -----------------------------------------------------

        internal static bool TryResolveRelativeBoundaryShadowWorldPos(
            Recording rec,
            TrackSection relSection,
            double boundaryUT,
            AnchorSide side,
            Func<TrajectoryPoint, Vector3d> absoluteWorldPositionResolver,
            out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null)
                return false;
            if (relSection.bodyFixedFrames == null || relSection.bodyFixedFrames.Count == 0)
                return false;
            if (absoluteWorldPositionResolver == null)
                return false;
            if (!TryFindExactBoundaryFrameSample(
                    relSection.bodyFixedFrames,
                    boundaryUT,
                    side,
                    emitLog: true,
                    pt: out TrajectoryPoint shadow))
                return false;

            try
            {
                worldPos = absoluteWorldPositionResolver(shadow);
            }
            catch
            {
                worldPos = default;
                return false;
            }

            return IsFinite(worldPos);
        }

        private static bool TryFindExactBoundaryFrameSample(
            List<TrajectoryPoint> samples,
            double boundaryUT,
            AnchorSide side,
            bool emitLog,
            out TrajectoryPoint pt)
        {
            pt = default;
            if (samples == null || samples.Count == 0)
                return false;

            const double epsilonSeconds = DebrisRelativeCoveragePrimitives.UtEpsilon;
            int best = -1;
            double bestDist = double.PositiveInfinity;
            for (int i = 0; i < samples.Count; i++)
            {
                double dist = Math.Abs(samples[i].ut - boundaryUT);
                if (dist <= epsilonSeconds && dist < bestDist)
                {
                    best = i;
                    bestDist = dist;
                }
            }

            if (best < 0)
                return false;

            pt = samples[best];
            if (emitLog)
            {
                ParsekLog.Verbose("Pipeline-Smoothing", string.Format(CultureInfo.InvariantCulture,
                    "boundary body-fixed sample selected: side={0} ut={1} sampleUT={2} delta={3}",
                    side,
                    boundaryUT.ToString("R", CultureInfo.InvariantCulture),
                    pt.ut.ToString("R", CultureInfo.InvariantCulture),
                    (pt.ut - boundaryUT).ToString("R", CultureInfo.InvariantCulture)));
            }
            return true;
        }

        private static bool TryFindBoundaryFrameSample(
            List<TrajectoryPoint> samples, double boundaryUT, AnchorSide side, out TrajectoryPoint pt)
        {
            pt = default;
            if (samples == null || samples.Count == 0) return false;
            // Phase 9 (design doc §12, §15.17, §18 Phase 9): prefer a
            // StructuralEventSnapshot-flagged sample at the boundary UT over the
            // legacy closest-sample path. Recordings without the flag (format ≤ v9)
            // miss every flagged-sample lookup and fall through to today's nearest-
            // neighbour behaviour. Same tolerance reasoning as
            // RenderSessionState.Phase9FlaggedTolerance — 1.0s is conservative
            // relative to a physics tick.
            const double Phase9FlaggedTolerance = 1.0;
            int flaggedIdx = AnchorCandidateBuilder.TryFindFlaggedSampleAtUT(
                samples, boundaryUT, Phase9FlaggedTolerance);
            if (flaggedIdx >= 0)
            {
                pt = samples[flaggedIdx];
                ParsekLog.Verbose("Pipeline-Smoothing", string.Format(CultureInfo.InvariantCulture,
                    "boundary frame sample selected: side={0} ut={1} flagged=true " +
                    "sampleUT={2} delta={3}",
                    side,
                    boundaryUT.ToString("R", CultureInfo.InvariantCulture),
                    pt.ut.ToString("R", CultureInfo.InvariantCulture),
                    (pt.ut - boundaryUT).ToString("R", CultureInfo.InvariantCulture)));
                return true;
            }

            // Find sample closest to boundaryUT. For exact alignment (the
            // common case — the boundary is itself a recorded sample) this
            // returns the matching point directly.
            int best = 0;
            double bestDist = Math.Abs(samples[0].ut - boundaryUT);
            for (int i = 1; i < samples.Count; i++)
            {
                double d = Math.Abs(samples[i].ut - boundaryUT);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            pt = samples[best];
            ParsekLog.Verbose("Pipeline-Smoothing", string.Format(CultureInfo.InvariantCulture,
                "boundary frame sample selected: side={0} ut={1} flagged=false " +
                "sampleUT={2} delta={3}",
                side,
                boundaryUT.ToString("R", CultureInfo.InvariantCulture),
                pt.ut.ToString("R", CultureInfo.InvariantCulture),
                (pt.ut - boundaryUT).ToString("R", CultureInfo.InvariantCulture)));
            return true;
        }

        private static CelestialBody ResolveBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            try
            {
                return FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == bodyName);
            }
            catch
            {
                return null;
            }
        }

        private bool TryBuildRelativeAnchorResolverContext(
            Recording rec,
            out RelativeAnchorResolverContext context)
        {
            context = default;
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;

            if (!TryFindFocusTree(rec, out RecordingTree focusTree))
                return false;

            context = new RelativeAnchorResolverContext(
                focusTree,
                focusRecordingId: rec.RecordingId,
                focusTreeId: focusTree?.Id,
                activeReFlyMarker: ParsekScenario.Instance?.ActiveReFlySessionMarker,
                pendingTree: PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null,
                absoluteWorldPositionResolver: absoluteWorldPosition,
                bodyWorldRotationResolver: bodyWorldRotation,
                orbitalCheckpointPoseResolver: TryResolveOrbitalAnchorPoseAtBoundaryInstant,
                tryResolveLiveAnchorTransform: ParsekFlight.TryGetLiveAnchorTransformDelegate());
            return true;
        }

        private static bool TryFindFocusTree(Recording rec, out RecordingTree focusTree)
        {
            focusTree = null;
            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings == null)
                        continue;
                    if ((!string.IsNullOrEmpty(rec.TreeId)
                            && string.Equals(tree.Id, rec.TreeId, StringComparison.Ordinal))
                        || tree.Recordings.ContainsKey(rec.RecordingId))
                    {
                        focusTree = tree;
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTreeSlot.HasPendingTree
                ? PendingTreeSlot.PendingTree
                : null;
            if (pending?.Recordings != null
                && (pending.Recordings.ContainsKey(rec.RecordingId)
                    || (!string.IsNullOrEmpty(rec.TreeId)
                        && string.Equals(pending.Id, rec.TreeId, StringComparison.Ordinal))))
            {
                focusTree = pending;
                return true;
            }

            return false;
        }

        private static Func<TrajectoryPoint, Vector3d> ResolveAbsoluteWorldPositionFrom(
            Func<string, CelestialBody> bodies)
        {
            return point =>
            {
                CelestialBody body = bodies(point.bodyName);
                if (body == null)
                    return new Vector3d(double.NaN, double.NaN, double.NaN);
                return body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
            };
        }

        private static Func<TrajectoryPoint, Quaternion> ResolveBodyWorldRotationFrom(
            Func<string, CelestialBody> bodies)
        {
            return point =>
            {
                CelestialBody body = bodies(point.bodyName);
                return body != null && body.bodyTransform != null
                    ? body.bodyTransform.rotation
                    : Quaternion.identity;
            };
        }

        /// <summary>
        /// The orbital-checkpoint anchor pose this resolver hands its relative-anchor context: the
        /// anchor's pose at the boundary UT <paramref name="ut"/>, placed in the same world as
        /// every other point this resolver and the propagator place (a body-fixed point put down
        /// with the body's rotation at the rebuild, <see cref="absoluteWorldPosition"/>).
        /// The recorders' and playback's shared pose
        /// (<see cref="ParsekFlight.TryComputeOrbitalCheckpointAnchorPose"/>) places the conic
        /// through the CURRENT <c>Planetarium.Zup</c> and body position, which is the anchor's pose
        /// only when <paramref name="ut"/> is the current game time; a boundary UT is in the past,
        /// so that pose against a body-fixed point placed now carried the body's rotation since
        /// the boundary into the epsilon (todo REFLY-ANCHOR-EPSILON-1300KM). Same segment pick as
        /// the shared pose; false when no segment covers <paramref name="ut"/> or its body does not
        /// resolve.
        /// </summary>
        private bool TryResolveOrbitalAnchorPoseAtBoundaryInstant(
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            out AnchorPose pose)
        {
            pose = default;
            int index = TrajectoryMath.FindFirstOrbitSegmentIndexClosed(section.checkpoints, ut);
            if (index < 0)
                return false;

            OrbitSegment segment = section.checkpoints[index];
            CelestialBody body = bodyLookup(segment.bodyName);
            if (object.ReferenceEquals(body, null))
                return false;
            return TryComputeBoundaryInstantOrbitalAnchorPose(
                recording,
                segment,
                AnchorPropagator.ReadBodyRotationCore(body),
                absoluteWorldPosition,
                sectionIndex,
                ut,
                out pose);
        }

        /// <summary>
        /// The pose of <paramref name="segment"/>'s conic at <paramref name="ut"/>, in the world
        /// <paramref name="place"/> puts a body-fixed point down in. The conic's state at
        /// <paramref name="ut"/> comes from <see cref="TrajectoryMath.BoundaryFrame"/> as body-fixed
        /// points of <paramref name="ut"/> (no <c>Planetarium.Zup</c>) and is placed; the velocity
        /// goes through the same placement as the difference of two placed points, so position,
        /// velocity and radial all ride one rigid map. The rotation is then the shared pose's own
        /// (<see cref="ParsekFlight.ComputeOrbitalRotation"/>, with the spin-forward branch
        /// transcribed because it reads a stock <c>Orbit</c>), from the mapped state. When
        /// <paramref name="ut"/> is the current game time and <paramref name="place"/> is the live
        /// surface placement this is the shared pose. Unity calls only for the rotation.
        /// </summary>
        internal static bool TryComputeBoundaryInstantOrbitalAnchorPose(
            Recording recording,
            OrbitSegment segment,
            TrajectoryMath.BoundaryFrame.BodyRotation body,
            Func<TrajectoryPoint, Vector3d> place,
            int sectionIndex,
            double ut,
            out AnchorPose pose)
        {
            pose = default;
            if (place == null)
                return false;
            if (!TryPlaceOrbitSegmentState(segment, body, ut, ut, place, out Vector3d worldPos, out Vector3d worldVel))
                return false;
            if (!TryPlaceBodyFixed(place, segment.bodyName, 0.0, 0.0, -body.Radius, out Vector3d bodyCentre))
                return false;

            Quaternion rotation;
            if (TrajectoryMath.IsSpinning(segment))
            {
                // ComputeOrbitalRotation's spin-forward branch, with the start-UT state carried
                // by the same map as the boundary state. The boundary attitude is that
                // function's orbital-frame branch at the start state (the spin branch's own
                // look rotation times the recorded attitude; it differs only below 0.03 m/s).
                if (!TryPlaceOrbitSegmentState(segment, body, segment.startUT, ut, place,
                        out Vector3d posAtStart, out Vector3d velAtStart))
                    return false;
                Quaternion boundaryWorldRot = ParsekFlight.ComputeOrbitalRotation(
                    segment,
                    null,
                    segment.startUT,
                    TrajectoryMath.SwizzleZupBodyRelativeToWorld(velAtStart),
                    posAtStart,
                    bodyCentre,
                    Quaternion.identity,
                    sectionIndex,
                    hasOfr: true,
                    spinning: false).ghostRot;
                double dt = ut - segment.startUT;
                Vector3 worldAxis = boundaryWorldRot * segment.angularVelocity;
                float angle = (float)((double)segment.angularVelocity.magnitude * dt * Mathf.Rad2Deg);
                rotation = ParsekFlight.OrbitalAngleAxis(angle, worldAxis) * boundaryWorldRot;
            }
            else
            {
                // ComputeOrbitalRotation takes the Zup velocity by contract and lifts it to world.
                rotation = ParsekFlight.ComputeOrbitalRotation(
                    segment,
                    null,
                    ut,
                    TrajectoryMath.SwizzleZupBodyRelativeToWorld(worldVel),
                    worldPos,
                    bodyCentre,
                    Quaternion.identity,
                    sectionIndex,
                    TrajectoryMath.HasOrbitalFrameRotation(segment),
                    spinning: false).ghostRot;
            }

            pose = new AnchorPose(worldPos, rotation, sectionIndex, recording?.RecordingId);
            return IsFinite(worldPos);
        }

        /// <summary>
        /// The conic's state at <paramref name="stateUT"/>, seen in the body-fixed frame of
        /// <paramref name="lowerUT"/>, placed: the position directly, the velocity as the placed
        /// difference of the position and the ahead point
        /// (<see cref="TrajectoryMath.BoundaryFrame.TryOrbitSegmentStateBodyFixed"/>).
        /// </summary>
        private static bool TryPlaceOrbitSegmentState(
            OrbitSegment segment,
            TrajectoryMath.BoundaryFrame.BodyRotation body,
            double stateUT,
            double lowerUT,
            Func<TrajectoryPoint, Vector3d> place,
            out Vector3d worldPos,
            out Vector3d worldVel)
        {
            worldPos = default;
            worldVel = Vector3d.zero;
            if (!TrajectoryMath.BoundaryFrame.TryOrbitSegmentStateBodyFixed(
                    segment, body, stateUT, lowerUT, out TrajectoryMath.BoundaryFrame.BodyFixedState state))
                return false;
            if (!TryPlaceBodyFixed(place, segment.bodyName, state.Lat, state.Lon, state.Alt, out worldPos))
                return false;
            if (state.Speed <= 0.0)
                return true;
            if (!TryPlaceBodyFixed(place, segment.bodyName, state.AheadLat, state.AheadLon, state.AheadAlt,
                    out Vector3d ahead))
                return false;
            worldVel = (ahead - worldPos) * (state.Speed / state.Step);
            return IsFinite(worldVel);
        }

        private static bool TryPlaceBodyFixed(
            Func<TrajectoryPoint, Vector3d> place,
            string bodyName,
            double lat,
            double lon,
            double alt,
            out Vector3d worldPos)
        {
            try
            {
                worldPos = place(new TrajectoryPoint
                {
                    bodyName = bodyName,
                    latitude = lat,
                    longitude = lon,
                    altitude = alt,
                });
            }
            catch
            {
                worldPos = default;
                return false;
            }
            return IsFinite(worldPos);
        }

        private static Vessel TryFindVesselByPid(uint pid)
        {
            try
            {
                if (!FlightGlobals.ready) return null;
                var vessels = FlightGlobals.Vessels;
                if (vessels == null) return null;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && v.persistentId == pid) return v;
                }
            }
            catch
            {
                // FlightGlobals.Vessels can transiently mutate; treat as miss.
            }
            return null;
        }

        private static bool IsFinite(Vector3d v)
        {
            return !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z)
                || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
        }
    }
}
