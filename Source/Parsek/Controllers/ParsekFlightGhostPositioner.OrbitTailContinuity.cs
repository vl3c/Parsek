using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // The predicted orbit tail continuity offset.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        private bool TryResolvePredictedOrbitTailContinuityOffset(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            Orbit orbit,
            double playbackUT,
            out Vector3d weightedOffset,
            out Vector3d rawOffset,
            out double weight,
            out double anchorUT,
            out double blendSeconds,
            out string anchorSource)
        {
            weightedOffset = Vector3d.zero;
            rawOffset = Vector3d.zero;
            weight = 0.0;
            anchorUT = double.NaN;
            blendSeconds = double.NaN;
            anchorSource = null;

            if (!ShouldApplyPredictedOrbitTailContinuity(traj, segment, playbackUT))
                return false;

            anchorUT = traj.Points[traj.Points.Count - 1].ut;
            blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                anchorUT, segment.startUT);
            weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                anchorUT, playbackUT, blendSeconds);
            if (weight <= 1e-6)
                return false;

            string cacheKey = BuildPredictedOrbitTailContinuityCacheKey(traj, segment, anchorUT);
            bool cacheHit = false;
            if (predictedOrbitTailContinuityCache.TryGetValue(
                    cacheKey, out ParsekFlight.PredictedOrbitTailContinuityCacheEntry cacheEntry)
                && ParsekFlight.IsFiniteVector3d(cacheEntry.rawOffset))
            {
                rawOffset = cacheEntry.rawOffset;
                anchorSource = cacheEntry.anchorSource;
                cacheHit = true;
            }
            else
            {
                if (!TryResolvePredictedOrbitTailContinuityAnchorWorldPosition(
                        traj, out Vector3d anchorWorld, out double resolvedAnchorUT, out anchorSource))
                {
                    return false;
                }

                if (Math.Abs(resolvedAnchorUT - anchorUT) > 1e-6)
                {
                    anchorUT = resolvedAnchorUT;
                    blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                        anchorUT, segment.startUT);
                    weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                        anchorUT, playbackUT, blendSeconds);
                    if (weight <= 1e-6)
                        return false;
                    cacheKey = BuildPredictedOrbitTailContinuityCacheKey(traj, segment, anchorUT);
                }

                Vector3d orbitAtAnchor = orbit.getPositionAtUT(anchorUT);
                rawOffset = anchorWorld - orbitAtAnchor;
                if (!ParsekFlight.IsFiniteVector3d(rawOffset))
                    return false;

                double candidateRawMeters = rawOffset.magnitude;
                if (candidateRawMeters <= 0.01)
                    return false;

                predictedOrbitTailContinuityCache[cacheKey] =
                    new ParsekFlight.PredictedOrbitTailContinuityCacheEntry
                    {
                        rawOffset = rawOffset,
                        anchorSource = anchorSource
                    };
            }

            weightedOffset = rawOffset * weight;
            if (!ParsekFlight.IsFiniteVector3d(weightedOffset))
                return false;

            double rawMeters = rawOffset.magnitude;
            if (rawMeters <= 0.01)
                return false;

            ParsekLog.VerboseRateLimited(
                "Playback",
                "orbit-tail-continuity|" + (traj.RecordingId ?? "<no-rec>"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Predicted orbit-tail continuity: rec={0} vessel=\"{1}\" playbackUT={2:F3} anchorUT={3:F3} segmentUT=[{4:F3},{5:F3}] source={6} rawOffset={7} rawMeters={8:F2} weight={9:F3} blendSeconds={10:F3} appliedOffset={11}",
                    ShortRecordingId(traj.RecordingId),
                    traj.VesselName ?? "?",
                    playbackUT,
                    anchorUT,
                    segment.startUT,
                    segment.endUT,
                    anchorSource ?? "<none>",
                    GhostRenderTrace.FormatVector3d(rawOffset),
                    rawMeters,
                    weight,
                    blendSeconds,
                    GhostRenderTrace.FormatVector3d(weightedOffset)
                    + " cache=" + (cacheHit ? "hit" : "miss")),
                5.0);
            return true;
        }

        internal static string BuildPredictedOrbitTailContinuityCacheKey(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            double anchorUT)
        {
            string recordingId = traj?.RecordingId;
            if (string.IsNullOrEmpty(recordingId))
                recordingId = traj?.VesselName ?? "<no-recording-id>";

            return string.Join("|",
                recordingId,
                segment.bodyName ?? "<no-body>",
                anchorUT.ToString("R", CultureInfo.InvariantCulture),
                segment.startUT.ToString("R", CultureInfo.InvariantCulture),
                segment.endUT.ToString("R", CultureInfo.InvariantCulture),
                segment.epoch.ToString("R", CultureInfo.InvariantCulture),
                segment.semiMajorAxis.ToString("R", CultureInfo.InvariantCulture),
                segment.eccentricity.ToString("R", CultureInfo.InvariantCulture),
                segment.inclination.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static bool ShouldApplyPredictedOrbitTailContinuity(
            IPlaybackTrajectory traj, OrbitSegment segment, double playbackUT)
        {
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;
            if (!segment.isPredicted)
                return false;

            double lastPointUT = traj.Points[traj.Points.Count - 1].ut;
            if (playbackUT <= lastPointUT + 1e-6)
                return false;

            double blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                lastPointUT, segment.startUT);
            return playbackUT <= lastPointUT + blendSeconds + 1e-6;
        }

        private bool TryResolvePredictedOrbitTailContinuityAnchorWorldPosition(
            IPlaybackTrajectory traj,
            out Vector3d anchorWorld,
            out double anchorUT,
            out string source)
        {
            anchorWorld = Vector3d.zero;
            anchorUT = double.NaN;
            source = null;
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;

            TrajectoryPoint lastPoint = traj.Points[traj.Points.Count - 1];
            anchorUT = lastPoint.ut;

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, anchorUT);
                bool exactSection = sectionIdx >= 0;
                if (sectionIdx < 0)
                    sectionIdx = FindNearestTrackSectionIndexForContinuityAnchor(
                        traj.TrackSections, anchorUT);

                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                {
                    TrackSection section = traj.TrackSections[sectionIdx];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        if (ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                                section, anchorUT, out _, out _))
                        {
                            int absoluteCachedIndex = 0;
                            if (TryResolvePointWorldPosition(
                                    section.bodyFixedFrames,
                                    ref absoluteCachedIndex,
                                    anchorUT,
                                    out anchorWorld))
                            {
                                source = "relative-body-fixed-primary";
                                return true;
                            }
                        }

                        if (exactSection)
                            return false;
                    }

                    if (section.referenceFrame == ReferenceFrame.Absolute
                        && ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                            traj.TrackSections,
                            sectionIdx,
                            out List<TrajectoryPoint> bodyFixedFrames,
                            out _))
                    {
                        int absoluteCachedIndex = 0;
                        if (TryResolvePointWorldPosition(
                                bodyFixedFrames,
                                ref absoluteCachedIndex,
                                anchorUT,
                                out anchorWorld))
                        {
                            source = "absolute-section";
                            return true;
                        }
                    }

                    if (exactSection && section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                        return false;
                }
            }

            if (TryResolveTrajectoryPointWorldPosition(lastPoint, out anchorWorld))
            {
                source = "flat-point";
                return true;
            }

            return false;
        }

        internal static int FindNearestTrackSectionIndexForContinuityAnchor(
            List<TrackSection> trackSections, double anchorUT)
        {
            if (trackSections == null || trackSections.Count == 0)
                return -1;

            if (anchorUT < trackSections[0].startUT)
                return 0;
            for (int i = trackSections.Count - 1; i >= 0; i--)
            {
                TrackSection section = trackSections[i];
                if (anchorUT >= section.startUT - 1e-6)
                    return i;
            }
            return trackSections.Count - 1;
        }
    }
}
