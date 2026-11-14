using System.Collections.Generic;

namespace Parsek
{
    internal enum RecordingVisualKind
    {
        Normal = 0,
        StaticPlaceholder = 1,
        StationaryTail = 2
    }

    internal static class RecordingVisualClassifier
    {
        internal static RecordingVisualKind Classify(
            Recording rec, IReadOnlyList<Recording> allRecordings)
        {
            if (IsStaticPlaceholder(rec))
                return RecordingVisualKind.StaticPlaceholder;

            if (IsStationaryTail(rec, allRecordings))
                return RecordingVisualKind.StationaryTail;

            return RecordingVisualKind.Normal;
        }

        internal static bool IsStaticPlaceholder(Recording rec)
        {
            if (rec == null || !rec.SurfacePos.HasValue)
                return false;

            if (rec.Points != null && rec.Points.Count >= 2)
                return false;

            if (HasOrbitPayload(rec))
                return false;

            return !HasAnimatedTrackSectionPayload(rec);
        }

        internal static bool IsStationaryTail(
            Recording rec, IReadOnlyList<Recording> allRecordings)
        {
            if (rec == null)
                return false;

            if (rec.Points == null || rec.Points.Count < 2)
                return false;

            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
                return false;

            if (HasOrbitPayload(rec))
                return false;

            if (!IsLeafRecordingForClassification(rec, allRecordings))
                return false;

            bool hasSurfaceStationarySection = false;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                SegmentEnvironment environment = rec.TrackSections[i].environment;
                if (!GhostPlaybackLogic.IsBoringEnvironment(environment))
                    return false;

                if (environment == SegmentEnvironment.SurfaceStationary)
                    hasSurfaceStationarySection = true;
            }

            if (!hasSurfaceStationarySection)
                return false;

            return !HasNonInertEvents(rec);
        }

        private static bool HasOrbitPayload(Recording rec)
        {
            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                return true;

            if (rec.TrackSections == null)
                return false;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection section = rec.TrackSections[i];
                if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                    && section.checkpoints != null
                    && section.checkpoints.Count > 0)
                    return true;
            }

            return false;
        }

        private static bool HasAnimatedTrackSectionPayload(Recording rec)
        {
            if (rec.TrackSections == null)
                return false;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection section = rec.TrackSections[i];
                if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    if (section.checkpoints != null && section.checkpoints.Count >= 2)
                        return true;
                    continue;
                }

                if (section.frames != null && section.frames.Count >= 2)
                    return true;

                if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count >= 2)
                    return true;
            }

            return false;
        }

        private static bool HasNonInertEvents(Recording rec)
        {
            if (rec.PartEvents != null)
            {
                for (int i = 0; i < rec.PartEvents.Count; i++)
                {
                    if (!RecordingOptimizer.IsInertPartEventForTailTrim(rec.PartEvents[i]))
                        return true;
                }
            }

            if (rec.SegmentEvents != null && rec.SegmentEvents.Count > 0)
                return true;

            if (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
                return true;

            return false;
        }

        private static bool IsLeafRecordingForClassification(
            Recording rec, IReadOnlyList<Recording> allRecordings)
        {
            if (rec.ChildBranchPointId != null
                && !GhostPlaybackLogic.IsEffectiveLeafForVessel(rec))
                return false;

            // Mirrors RecordingOptimizer.IsLeafRecording's chain-successor rule so
            // visual labels stay aligned with optimizer boring-tail eligibility.
            if (!string.IsNullOrEmpty(rec.ChainId)
                && rec.ChainIndex >= 0
                && allRecordings != null)
            {
                for (int i = 0; i < allRecordings.Count; i++)
                {
                    Recording other = allRecordings[i];
                    if (other == null || ReferenceEquals(other, rec))
                        continue;

                    if (other.ChainId == rec.ChainId
                        && other.ChainBranch == 0
                        && other.ChainIndex > rec.ChainIndex)
                        return false;
                }
            }

            return true;
        }
    }
}
