using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class PlaybackOrbitDiagnostics
    {
        private const double PayloadEndEpsilon = 1e-6;
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal static bool TryGetRecordedPayloadEndUT(IPlaybackTrajectory traj, out double endUT)
        {
            endUT = 0.0;
            if (traj == null)
                return false;

            bool found = false;

            if (traj.Points != null && traj.Points.Count > 0)
            {
                endUT = traj.Points[traj.Points.Count - 1].ut;
                found = true;
            }

            if (TryGetTrackSectionPayloadEndUT(traj.TrackSections, out double trackSectionEndUT))
            {
                if (!found || trackSectionEndUT > endUT)
                    endUT = trackSectionEndUT;
                found = true;
            }

            return found;
        }

        internal static bool TryBuildPlaybackPredictedTailLog(
            int recordingIndex,
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            double ut,
            out string key,
            out string message)
        {
            key = null;
            message = null;

            if (!TryGetRecordedPayloadEndUT(traj, out double payloadEndUT)
                || ut <= payloadEndUT + PayloadEndEpsilon)
                return false;

            int segmentIndex = FindOrbitSegmentIndex(traj?.OrbitSegments, segment);
            if (segmentIndex < 0)
                return false;

            key = "predicted-tail-" + ResolveRecordingKey(recordingIndex, traj);
            message = string.Format(ic,
                "Predicted-tail orbit playback rec={0} index={1} body={2} segmentIndex={3} " +
                "ut={4:F2} payloadEndUT={5:F2} segmentUT={6:F2}-{7:F2}",
                ResolveRecordingLabel(traj),
                recordingIndex,
                segment.bodyName ?? "(null)",
                segmentIndex,
                ut,
                payloadEndUT,
                segment.startUT,
                segment.endUT);
            return true;
        }

        internal static bool TryBuildMapPredictedTailLog(
            int recordingIndex,
            uint vesselPid,
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            double ut,
            double visibleStartUT,
            double visibleEndUT,
            bool carriedAcrossGap,
            out string key,
            out string message)
        {
            key = null;
            message = null;

            if (!TryGetRecordedPayloadEndUT(traj, out double payloadEndUT)
                || ut <= payloadEndUT + PayloadEndEpsilon)
                return false;

            int segmentIndex = FindOrbitSegmentIndex(traj?.OrbitSegments, segment);
            if (segmentIndex < 0)
                return false;

            key = "predicted-tail-" + ResolveRecordingKey(recordingIndex, traj);
            message = string.Format(ic,
                "Predicted-tail map selection rec={0} index={1} pid={2} body={3} segmentIndex={4} " +
                "ut={5:F2} payloadEndUT={6:F2} segmentUT={7:F2}-{8:F2} windowUT={9:F2}-{10:F2} gapCarry={11}",
                ResolveRecordingLabel(traj),
                recordingIndex,
                vesselPid,
                segment.bodyName ?? "(null)",
                segmentIndex,
                ut,
                payloadEndUT,
                segment.startUT,
                segment.endUT,
                visibleStartUT,
                visibleEndUT,
                carriedAcrossGap);
            return true;
        }

        private static bool TryGetTrackSectionPayloadEndUT(
            List<TrackSection> sections,
            out double endUT)
        {
            endUT = 0.0;
            if (sections == null || sections.Count == 0)
                return false;

            bool found = false;
            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection section = sections[i];
                if (!HasPlayablePayload(section))
                    continue;

                double candidateEndUT = section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                    ? section.checkpoints[section.checkpoints.Count - 1].endUT
                    : section.frames[section.frames.Count - 1].ut;

                if (!found || candidateEndUT > endUT)
                    endUT = candidateEndUT;

                found = true;
            }

            return found;
        }

        private static bool HasPlayablePayload(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                return section.checkpoints != null && section.checkpoints.Count > 0;

            return section.frames != null && section.frames.Count > 0;
        }

        private static int FindOrbitSegmentIndex(List<OrbitSegment> segments, OrbitSegment segment)
        {
            if (segments == null)
                return -1;

            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment candidate = segments[i];
                if (candidate.startUT == segment.startUT
                    && candidate.endUT == segment.endUT
                    && candidate.inclination == segment.inclination
                    && candidate.eccentricity == segment.eccentricity
                    && candidate.semiMajorAxis == segment.semiMajorAxis
                    && candidate.longitudeOfAscendingNode == segment.longitudeOfAscendingNode
                    && candidate.argumentOfPeriapsis == segment.argumentOfPeriapsis
                    && candidate.meanAnomalyAtEpoch == segment.meanAnomalyAtEpoch
                    && candidate.epoch == segment.epoch
                    && string.Equals(candidate.bodyName, segment.bodyName, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ResolveRecordingKey(int recordingIndex, IPlaybackTrajectory traj)
        {
            if (!string.IsNullOrEmpty(traj?.RecordingId))
                return traj.RecordingId;

            return "idx-" + recordingIndex.ToString(ic);
        }

        private static string ResolveRecordingLabel(IPlaybackTrajectory traj)
        {
            if (!string.IsNullOrEmpty(traj?.RecordingId))
                return traj.RecordingId;

            return "(null)";
        }
    }
}
