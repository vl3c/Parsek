using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    internal partial class GhostPlaybackEngine
    {
        internal static bool TryResolvePendingPlaybackInterpolation(
            IPlaybackTrajectory traj, double playbackUT, out InterpolationResult result)
        {
            result = InterpolationResult.Zero;
            if (traj == null)
                return LogPendingPlaybackInterpolationUnresolved(
                    null, playbackUT, "null trajectory");

            if (traj.Points != null && traj.Points.Count >= 2)
            {
                bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, playbackUT);
                bool authoredGapHasShadow =
                    AuthoredFrameGapHasShadowCoverage(traj, playbackUT);
                bool canUseOrbitPrecedence = TryResolvePendingOrbitSegmentInterpolation(
                    traj, playbackUT, out InterpolationResult orbitSegmentResult);
                if (surfaceSkip && canUseOrbitPrecedence)
                {
                    string vesselName = traj.VesselName ?? "Unknown";
                    string branchRecId = traj.RecordingId ?? string.Empty;
                    ParsekLog.VerboseRateLimited("Engine", "pending-playback-branch-1-" + branchRecId,
                        FormattableString.Invariant(
                            $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} surface track section active, skipping orbit precedence"),
                        2.0);
                }

                if (!surfaceSkip && authoredGapHasShadow && canUseOrbitPrecedence)
                {
                    string vesselName = traj.VesselName ?? "Unknown";
                    string branchRecId = traj.RecordingId ?? string.Empty;
                    ParsekLog.VerboseRateLimited("Engine", "pending-playback-branch-2-" + branchRecId,
                        FormattableString.Invariant(
                            $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} skipping orbit precedence: authored-frame gap body-fixed primary available"),
                        2.0);
                }

                if (!surfaceSkip && !authoredGapHasShadow && canUseOrbitPrecedence)
                {
                    result = orbitSegmentResult;
                    return LogPendingPlaybackInterpolationResolved(
                        traj, playbackUT, result, "active orbit segment");
                }

                if (TryResolvePendingRelativeSectionBodyFixedPrimaryInterpolation(
                        traj, playbackUT, out result, out string relativePointSource))
                {
                    return LogPendingPlaybackInterpolationResolved(
                        traj, playbackUT, result, relativePointSource);
                }

                if (!IsRelativeTrackSectionAtUT(traj.TrackSections, playbackUT))
                {
                    if (TryResolvePendingPointInterpolation(
                            traj.Points, playbackUT, out result, out string pointSource))
                    {
                        return LogPendingPlaybackInterpolationResolved(
                            traj, playbackUT, result, pointSource);
                    }
                }
                else
                {
                    string vesselName = traj.VesselName ?? "Unknown";
                    string branchRecId = traj.RecordingId ?? string.Empty;
                    ParsekLog.VerboseRateLimited("Engine", "pending-playback-branch-3-" + branchRecId,
                        FormattableString.Invariant(
                            $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} relative section active with no body-fixed primary, skipping flat relative point metadata"),
                        2.0);
                }
            }

            if (traj.SurfacePos.HasValue && !string.IsNullOrEmpty(traj.SurfacePos.Value.body))
            {
                SurfacePosition surface = traj.SurfacePos.Value;
                result = new InterpolationResult(Vector3.zero, surface.body, surface.altitude);
                return LogPendingPlaybackInterpolationResolved(
                    traj, playbackUT, result, "surface metadata");
            }

            if (traj.Points != null && traj.Points.Count == 1)
            {
                if (ShouldPrimeSinglePointGhostFromOrbit(traj, playbackUT)
                    && TryResolvePendingOrbitSegmentInterpolation(
                        traj, playbackUT, out result))
                {
                    return LogPendingPlaybackInterpolationResolved(
                        traj, playbackUT, result, "single-point orbit segment");
                }

                TrajectoryPoint point = traj.Points[0];
                if (!string.IsNullOrEmpty(point.bodyName))
                {
                    result = new InterpolationResult(point.velocity, point.bodyName, point.altitude);
                    return LogPendingPlaybackInterpolationResolved(
                        traj, playbackUT, result, "single-point fallback");
                }
            }

            if (TryResolvePendingOrbitSegmentInterpolation(
                traj, playbackUT, out result))
            {
                return LogPendingPlaybackInterpolationResolved(
                    traj, playbackUT, result, "fallback orbit segment");
            }

            if (!string.IsNullOrEmpty(traj.EndpointBodyName))
            {
                result = new InterpolationResult(Vector3.zero, traj.EndpointBodyName, 0.0);
                return LogPendingPlaybackInterpolationResolved(
                    traj, playbackUT, result, "endpoint body fallback");
            }

            return LogPendingPlaybackInterpolationUnresolved(
                traj, playbackUT, "no points, surface metadata, orbit segment, or endpoint body");
        }

        private static bool TryResolvePendingRelativeSectionBodyFixedPrimaryInterpolation(
            IPlaybackTrajectory traj,
            double playbackUT,
            out InterpolationResult result,
            out string source)
        {
            result = InterpolationResult.Zero;
            source = null;
            if (!TryFindRelativeTrackSectionAtUT(traj?.TrackSections, playbackUT, out TrackSection section))
                return false;
            if (!ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                    section, playbackUT, out _, out _))
            {
                return false;
            }

            if (!TryResolvePendingPointInterpolation(
                    section.bodyFixedFrames, playbackUT, out result, out string pointSource))
            {
                return false;
            }

            source = "relative body-fixed primary " + pointSource;
            return true;
        }

        private static bool TryResolvePendingPointInterpolation(
            List<TrajectoryPoint> points,
            double playbackUT,
            out InterpolationResult result,
            out string source)
        {
            result = InterpolationResult.Zero;
            source = null;
            if (points == null || points.Count == 0)
                return false;

            if (points.Count == 1)
            {
                TrajectoryPoint point = points[0];
                if (string.IsNullOrEmpty(point.bodyName))
                    return false;

                result = new InterpolationResult(point.velocity, point.bodyName, point.altitude);
                source = "single-point fallback";
                return true;
            }

            int cachedIndex = 0;
            if (!TrajectoryMath.InterpolatePoints(
                    points, ref cachedIndex, playbackUT,
                    out TrajectoryPoint before, out TrajectoryPoint after, out float t))
            {
                if (string.IsNullOrEmpty(before.bodyName))
                    return false;

                result = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                source = "before-start point fallback";
                return true;
            }

            bool useBeforePoint = t == 0f && before.ut == after.ut;
            bool afterEndClamp = playbackUT > after.ut;
            string bodyName = useBeforePoint ? before.bodyName : after.bodyName;
            if (string.IsNullOrEmpty(bodyName))
                return false;

            result = new InterpolationResult(
                useBeforePoint ? before.velocity : Vector3.Lerp(before.velocity, after.velocity, t),
                bodyName,
                useBeforePoint
                    ? before.altitude
                    : TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
            bool crossBodyTransition =
                !useBeforePoint
                && !string.Equals(before.bodyName, after.bodyName, StringComparison.Ordinal);
            source = useBeforePoint
                ? "same-UT point segment"
                : afterEndClamp
                    ? "point after-end clamp"
                    : crossBodyTransition
                        ? FormattableString.Invariant(
                            $"cross-body point transition {before.bodyName ?? "(null)"}->{after.bodyName ?? "(null)"} (using upper-point body)")
                    : "point interpolation";
            return true;
        }

        private static bool IsRelativeTrackSectionAtUT(
            List<TrackSection> trackSections, double playbackUT)
        {
            return TryFindRelativeTrackSectionAtUT(trackSections, playbackUT, out _);
        }

        private static bool TryFindRelativeTrackSectionAtUT(
            List<TrackSection> trackSections, double playbackUT, out TrackSection section)
        {
            section = default(TrackSection);
            if (trackSections == null || trackSections.Count == 0)
                return false;

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(trackSections, playbackUT);
            if (sectionIndex < 0 || sectionIndex >= trackSections.Count)
                return false;

            section = trackSections[sectionIndex];
            return section.referenceFrame == ReferenceFrame.Relative;
        }

        private static bool LogPendingPlaybackInterpolationResolved(
            IPlaybackTrajectory traj, double playbackUT, InterpolationResult result, string source)
        {
            string vesselName = traj?.VesselName ?? "Unknown";
            string bodyName = result.bodyName ?? "(null)";
            string recId = traj?.RecordingId ?? string.Empty;
            ParsekLog.VerboseRateLimited("Engine", "pending-playback-interp-" + recId + "-" + source,
                FormattableString.Invariant(
                    $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} resolved from {source} body='{bodyName}' altitude={result.altitude:F1}"),
                2.0);
            return true;
        }

        private static bool LogPendingPlaybackInterpolationUnresolved(
            IPlaybackTrajectory traj, double playbackUT, string reason)
        {
            string vesselName = traj?.VesselName ?? "Unknown";
            string recId = traj?.RecordingId ?? string.Empty;
            ParsekLog.VerboseRateLimited("Engine", "pending-playback-interp-unres-" + recId + "-" + reason,
                FormattableString.Invariant(
                    $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} unresolved ({reason})"),
                2.0);
            return false;
        }

        private static bool TryResolvePendingOrbitSegmentInterpolation(
            IPlaybackTrajectory traj, double playbackUT, out InterpolationResult result)
        {
            result = InterpolationResult.Zero;
            if (traj?.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, playbackUT);
            if (!seg.HasValue || string.IsNullOrEmpty(seg.Value.bodyName))
                return false;

            if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg.Value))
                return false;

            result = new InterpolationResult(Vector3.zero, seg.Value.bodyName, 0.0);
            return true;
        }
    }
}
