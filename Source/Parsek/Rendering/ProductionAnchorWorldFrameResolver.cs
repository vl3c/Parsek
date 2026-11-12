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
                    ResolveAbsoluteWorldPosition,
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

        public bool TryResolveSoiBoundaryWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            // Kepler propagation of the post-SOI checkpoint at the boundary UT.
            return TryResolveCheckpointSideWorldPos(rec, sectionIndex, side, boundaryUT, out worldPos);
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

        private static bool TryResolveCheckpointSideWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null || rec.TrackSections == null) return false;
            if (sectionIndex < 0 || sectionIndex >= rec.TrackSections.Count) return false;

            // sectionIndex points at the ABSOLUTE side. The adjacent
            // OrbitalCheckpoint section is the previous index (side=Start)
            // or next index (side=End).
            int ckIdx = (side == AnchorSide.Start) ? sectionIndex - 1 : sectionIndex + 1;
            if (ckIdx < 0 || ckIdx >= rec.TrackSections.Count) return false;

            TrackSection ckSection = rec.TrackSections[ckIdx];
            if (ckSection.referenceFrame != ReferenceFrame.OrbitalCheckpoint) return false;

            // Pick the OrbitSegment that brackets the boundary UT and
            // evaluate Kepler at it. The shared helper handles the
            // partial-first / partial-last checkpoint endpoint fallback
            // (see TrajectoryMath.EvaluateOrbitSegmentAtUT).
            Vector3d? maybePos = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                ckSection.checkpoints, boundaryUT, ResolveBody);
            if (!maybePos.HasValue) return false;
            worldPos = maybePos.Value;
            return IsFinite(worldPos);
        }

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

        private static bool TryBuildRelativeAnchorResolverContext(
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
                absoluteWorldPositionResolver: ResolveAbsoluteWorldPosition,
                bodyWorldRotationResolver: ResolveBodyWorldRotation,
                orbitalCheckpointPoseResolver: TryResolveOrbitalAnchorPose,
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

        private static Vector3d ResolveAbsoluteWorldPosition(TrajectoryPoint point)
        {
            CelestialBody body = ResolveBody(point.bodyName);
            if (body == null)
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            return body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
        }

        private static Quaternion ResolveBodyWorldRotation(TrajectoryPoint point)
        {
            CelestialBody body = ResolveBody(point.bodyName);
            return body != null && body.bodyTransform != null
                ? body.bodyTransform.rotation
                : Quaternion.identity;
        }

        // The shared pose with this resolver's own body lookup (ResolveBody above), which
        // catches every exception rather than only a failed FlightGlobals initializer.
        private static bool TryResolveOrbitalAnchorPose(
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
            return ParsekFlight.TryComputeOrbitalCheckpointAnchorPose(
                recording,
                segment,
                ResolveBody(segment.bodyName),
                sectionIndex,
                ut,
                out pose);
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
