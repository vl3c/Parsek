using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // TryResolvePlaybackWorldPosition and its resolvers (no placement, no host state).
    internal sealed partial class ParsekFlightGhostPositioner
    {
        /// <summary>
        internal static string FormatStandaloneCoverageRange(double startUT, double endUT)
        {
            if (double.IsNaN(startUT) || double.IsNaN(endUT))
                return "(none)";
            return "[" + startUT.ToString("R", CultureInfo.InvariantCulture)
                + "," + endUT.ToString("R", CultureInfo.InvariantCulture) + "]";
        }

        private static void LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
            IPlaybackTrajectory traj,
            int recordingIndex,
            double playbackUT,
            string callsite,
            string routeReason,
            DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            string recordingId = traj?.RecordingId ?? "(none)";
            string key = "playback-world-parent-debris-body-fixed-failed-closed|"
                + recordingId + "|" + recordingIndex.ToString(CultureInfo.InvariantCulture)
                + "|" + (callsite ?? "(unknown)");
            ParsekLog.WarnRateLimited(
                "Playback",
                key,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: body-fixed primary failed closed for parent-anchored debris; recorded-relative fallback suppressed recording=#{1} recordingId={2} vessel=\"{3}\" playbackUT={4} routeReason={5} coverageReason={6} sectionIndex={7} sectionUT={8} relativeFrames={9} bodyFixedFrames={10} anchorRec={11}",
                    callsite ?? "(unknown)",
                    recordingIndex.ToString(CultureInfo.InvariantCulture),
                    recordingId,
                    traj?.VesselName ?? "(unknown)",
                    playbackUT.ToString("R", CultureInfo.InvariantCulture),
                    routeReason ?? "(unknown)",
                    diagnostic.Reason ?? "(unknown)",
                    diagnostic.SectionIndex.ToString(CultureInfo.InvariantCulture),
                    FormatStandaloneCoverageRange(diagnostic.SectionStartUT, diagnostic.SectionEndUT),
                    FormatStandaloneCoverageRange(diagnostic.FirstRelativeFrameUT, diagnostic.LastRelativeFrameUT),
                    FormatStandaloneCoverageRange(diagnostic.FirstBodyFixedFrameUT, diagnostic.LastBodyFixedFrameUT),
                    diagnostic.AnchorRecordingId ?? "(none)"),
                5.0);
        }

        internal bool TryResolvePlaybackWorldPosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (traj == null)
                return false;

            int cachedIndex = state != null ? state.playbackIndex : 0;
            if (GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic retireDiagnostic))
            {
                LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                    traj,
                    index,
                    playbackUT,
                    "TryResolvePlaybackWorldPosition",
                    "body-fixed-primary-unavailable",
                    retireDiagnostic);
                return false;
            }

            bool skipRecordedRelativeResolverForAuthoredFrameGap =
                GhostPlaybackEngine.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic);
            bool authoredGapHasShadow =
                skipRecordedRelativeResolverForAuthoredFrameGap
                && diagnostic.BodyFixedFramesCoverUT;

            if (!authoredGapHasShadow
                && TryResolveOrbitTailWorldPosition(
                    traj, playbackUT, index * 10000, out worldPos))
            {
                return true;
            }

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
                if (sectionIdx >= 0)
                {
                    var section = traj.TrackSections[sectionIdx];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        var target = new RelativeSectionPlaybackTarget(
                            traj.RecordingId,
                            sectionIdx,
                            section);
                        if (!skipRecordedRelativeResolverForAuthoredFrameGap
                            && TryResolveRelativeWorldPosition(
                                section.frames ?? traj.Points,
                                playbackUT,
                                target,
                                traj.RecordingFormatVersion,
                                out worldPos))
                        {
                            return true;
                        }
                        if ((!skipRecordedRelativeResolverForAuthoredFrameGap
                                || diagnostic.BodyFixedFramesCoverUT)
                            && ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                                section, playbackUT, out _, out _))
                        {
                            int absoluteCachedIndex = 0;
                            bool bodyFixedResolved = TryResolvePointWorldPosition(
                                section.bodyFixedFrames,
                                ref absoluteCachedIndex,
                                playbackUT,
                                out worldPos);
                            if (!bodyFixedResolved && skipRecordedRelativeResolverForAuthoredFrameGap)
                            {
                                LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                                    traj,
                                    index,
                                    playbackUT,
                                    "TryResolvePlaybackWorldPosition",
                                    "body-fixed-primary-position-failed",
                                    diagnostic);
                            }
                            return bodyFixedResolved;
                        }
                        if (skipRecordedRelativeResolverForAuthoredFrameGap)
                        {
                            LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                                traj,
                                index,
                                playbackUT,
                                "TryResolvePlaybackWorldPosition",
                                diagnostic.BodyFixedFramesCoverUT
                                    ? "body-fixed-primary-position-failed"
                                    : "body-fixed-primary-unavailable",
                                diagnostic);
                        }
                        // Relative sections store metre offsets in the
                        // lat/lon/alt fields. If their anchor cannot resolve,
                        // do not fall through to absolute/interpolated
                        // surface playback or checkpoint recovery.
                        return false;
                    }
                    else if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                        && section.frames != null
                        && section.frames.Count > 0
                        && TryResolvePointWorldPosition(
                            section.frames,
                            ref cachedIndex,
                            playbackUT,
                            out worldPos))
                    {
                        LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);
                        return true;
                    }
                    else if (ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                        traj.TrackSections,
                        sectionIdx,
                        out List<TrajectoryPoint> bodyFixedFrames,
                        out _))
                    {
                        // Section-local absolute frames are authoritative at
                        // frame boundaries. The flat Points list can contain
                        // adjacent Relative metre-offset samples; interpreting
                        // those as lat/lon/alt causes planet-scale spikes.
                        int sectionCachedIndex = 0;
                        return TryResolvePointWorldPosition(
                            bodyFixedFrames,
                            ref sectionCachedIndex,
                            playbackUT,
                            out worldPos);
                    }
                }
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, playbackUT);
            if (TryResolveInterpolatedWorldPosition(
                    traj.Points, traj.OrbitSegments, ref cachedIndex,
                    playbackUT, index * 10000, surfaceSkip, out worldPos,
                    traj.TrackSections))
            {
                return true;
            }

            if (traj.SurfacePos.HasValue)
                return TryResolveSurfaceWorldPosition(traj.SurfacePos.Value, out worldPos);

            return false;
        }

        bool TryResolveInterpolatedWorldPosition(
            List<TrajectoryPoint> points,
            List<OrbitSegment> segments,
            ref int cachedIndex,
            double targetUT,
            int orbitCacheBase,
            bool skipOrbitSegments,
            out Vector3d worldPos,
            List<TrackSection> flatPointSections = null)
        {
            if (!skipOrbitSegments
                && TryResolveOrbitWorldPosition(segments, targetUT, orbitCacheBase, out worldPos))
            {
                return true;
            }

            return TryResolvePointWorldPosition(
                points, ref cachedIndex, targetUT, out worldPos, flatPointSections);
        }

        internal static bool TryFindAdjacentAbsolutePlaybackFrame(
            List<TrackSection> trackSections,
            int adjacentIdx,
            bool searchForward,
            double requireBeforeUT,
            double requireAfterUT,
            string bodyName,
            out TrajectoryPoint frame)
        {
            frame = default(TrajectoryPoint);
            if (trackSections == null
                || adjacentIdx < 0
                || adjacentIdx >= trackSections.Count)
            {
                return false;
            }

            List<TrajectoryPoint> adjacentFrames =
                GetAbsolutePlaybackFrameList(trackSections[adjacentIdx]);
            if (adjacentFrames == null || adjacentFrames.Count == 0)
                return false;

            if (searchForward)
            {
                for (int i = 0; i < adjacentFrames.Count; i++)
                {
                    TrajectoryPoint candidate = adjacentFrames[i];
                    if (IsEligibleBoundaryBridgeFrame(
                            candidate,
                            requireBeforeUT,
                            requireAfterUT,
                            bodyName))
                    {
                        frame = candidate;
                        return true;
                    }
                }
            }
            else
            {
                for (int i = adjacentFrames.Count - 1; i >= 0; i--)
                {
                    TrajectoryPoint candidate = adjacentFrames[i];
                    if (IsEligibleBoundaryBridgeFrame(
                            candidate,
                            requireBeforeUT,
                            requireAfterUT,
                            bodyName))
                    {
                        frame = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static List<TrajectoryPoint> GetAbsolutePlaybackFrameList(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.Absolute)
                return section.frames;
            if (section.referenceFrame == ReferenceFrame.Relative)
                return section.bodyFixedFrames;
            return null;
        }

        internal static bool IsEligibleBoundaryBridgeFrame(
            TrajectoryPoint candidate,
            double requireBeforeUT,
            double requireAfterUT,
            string bodyName)
        {
            const double epsilonSeconds = 1e-6;
            if (!double.IsNaN(requireBeforeUT)
                && !(candidate.ut + epsilonSeconds < requireBeforeUT))
            {
                return false;
            }

            if (!double.IsNaN(requireAfterUT)
                && !(candidate.ut > requireAfterUT + epsilonSeconds))
            {
                return false;
            }

            return string.IsNullOrEmpty(bodyName)
                || string.IsNullOrEmpty(candidate.bodyName)
                || string.Equals(candidate.bodyName, bodyName, StringComparison.Ordinal);
        }

        bool TryResolvePointWorldPosition(
            List<TrajectoryPoint> points,
            ref int cachedIndex,
            double targetUT,
            out Vector3d worldPos,
            List<TrackSection> flatPointSections = null)
        {
            worldPos = Vector3d.zero;
            if (points == null || points.Count == 0)
                return false;

            TrajectoryPoint before;
            TrajectoryPoint after;
            float t;
            if (points.Count == 1)
            {
                before = points[0];
                after = points[0];
                if (flatPointSections != null
                    && !TryDispatchFlatPointEndpoints(
                        flatPointSections, ref before, ref after, targetUT, null, "world-resolve-flat-points"))
                {
                    return false;
                }
                return TryResolveTrajectoryPointWorldPosition(before, out worldPos);
            }

            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);
            if (!hasSegment)
                after = before;
            if (flatPointSections != null
                && !TryDispatchFlatPointEndpoints(
                    flatPointSections, ref before, ref after, targetUT, null, "world-resolve-flat-points"))
            {
                return false;
            }
            if (!hasSegment)
            {
                return points != null
                    && points.Count > 0
                    && TryResolveTrajectoryPointWorldPosition(before, out worldPos);
            }

            if (t == 0f && before.ut == after.ut)
                return TryResolveTrajectoryPointWorldPosition(before, out worldPos);

            CelestialBody bodyBefore = FindBodyByNameOrNull(FlightGlobals.Bodies, before.bodyName);
            CelestialBody bodyAfter = FindBodyByNameOrNull(FlightGlobals.Bodies, after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            worldPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = posBefore;
            return true;
        }

        bool TryResolveTrajectoryPointWorldPosition(TrajectoryPoint point, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, point.bodyName);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);
            return true;
        }

        bool TryResolveRelativeWorldPosition(
            List<TrajectoryPoint> frames,
            double targetUT,
            RelativeSectionPlaybackTarget target,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (!TrySelectRelativeOffset(frames, targetUT, out double dx, out double dy, out double dz))
                return false;
            return TryResolveRelativeOffsetWorldPosition(
                dx,
                dy,
                dz,
                target,
                targetUT,
                recordingFormatVersion,
                out worldPos);
        }

        /// <summary>
        /// The anchor-local offset of RELATIVE <paramref name="frames"/> at
        /// <paramref name="targetUT"/>: interpolated inside the samples, held at the first or
        /// last one outside them. The caller applies it to the pose the anchor has at
        /// <paramref name="targetUT"/>, never at the UT of the held sample.
        /// </summary>
        internal static bool TrySelectRelativeOffset(
            List<TrajectoryPoint> frames,
            double targetUT,
            out double dx,
            out double dy,
            out double dz)
        {
            dx = dy = dz = 0.0;
            if (frames == null || frames.Count == 0)
                return false;

            int cachedIndex = 0;
            int indexBefore = frames.Count < 2
                ? -1
                : TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);
            TrajectoryPoint before = frames[indexBefore < 0 ? 0 : indexBefore];
            TrajectoryPoint after = indexBefore < 0 ? before : frames[indexBefore + 1];
            double segmentDuration = after.ut - before.ut;
            float t = segmentDuration <= 0.0001
                ? 0f
                : Mathf.Clamp01((float)((targetUT - before.ut) / segmentDuration));

            dx = before.latitude + (after.latitude - before.latitude) * t;
            dy = before.longitude + (after.longitude - before.longitude) * t;
            dz = before.altitude + (after.altitude - before.altitude) * t;
            return true;
        }

        bool TryResolveRelativeOffsetWorldPosition(
            double dx,
            double dy,
            double dz,
            RelativeSectionPlaybackTarget target,
            double targetUT,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            ParsekFlight.RelativeAnchorPose anchorPose;
            if (!TryResolveRecordedRelativeAnchorPose(
                    target,
                    targetUT,
                    out anchorPose,
                    out RelativeAnchorResolveFailure failure))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "anchor-pose-unresolved");
                GhostRenderTrace.EmitRelativeResolver(
                    target.RecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-relative-offset",
                    reason,
                    0u,
                    target.AnchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                return false;
            }

            worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPose.worldPos,
                anchorPose.worldRotation,
                dx,
                dy,
                dz);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = anchorPose.worldPos;
            GhostRenderTrace.EmitRelativeResolver(
                target.RecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-relative-offset",
                "resolved",
                0u,
                target.AnchorRecordingId,
                success: true,
                fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                anchorPosition: anchorPose.worldPos,
                anchorRotation: anchorPose.worldRotation,
                localOffset: new Vector3d(dx, dy, dz),
                outputPosition: worldPos);
            return true;
        }

        bool TryResolveSurfaceWorldPosition(SurfacePosition surfacePos, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, surfacePos.body);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                surfacePos.latitude, surfacePos.longitude, surfacePos.altitude);
            return true;
        }
    }
}
