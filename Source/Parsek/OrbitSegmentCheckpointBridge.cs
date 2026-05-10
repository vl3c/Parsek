using System;
using System.Collections.Generic;

namespace Parsek
{
    internal struct OrbitSegmentCheckpointBridgeStats
    {
        public int Added;
        public int SkippedExisting;
        public int SkippedInvalid;
        public int SkippedPredicted;
        public int SkippedAfterPredicted;

        public bool Changed => Added > 0;
    }

    /// <summary>
    /// Keeps packed/on-rails orbital payload in the section model. Flat
    /// Recording.OrbitSegments are a runtime cache; OrbitalCheckpoint TrackSections
    /// are the durable representation for section-authoritative paths.
    /// </summary>
    internal static class OrbitSegmentCheckpointBridge
    {
        private const double UtTolerance = 1e-6;
        private const double ScalarTolerance = 1e-9;
        private const double DistanceTolerance = 1e-6;
        private const double VectorTolerance = 1e-6;

        internal static TrackSection BuildOpenCheckpointSection(double startUT)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = startUT,
                source = TrackSectionSource.Checkpoint,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
        }

        internal static TrackSection BuildClosedCheckpointSection(OrbitSegment segment)
        {
            TrackSection section = BuildOpenCheckpointSection(segment.startUT);
            section.endUT = segment.endUT;
            section.checkpoints.Add(segment);
            return section;
        }

        internal static bool TryAppendClosedCheckpointSection(
            Recording rec,
            OrbitSegment segment,
            bool markDirty,
            out string skipReason)
        {
            skipReason = null;
            if (rec == null)
            {
                skipReason = "no-recording";
                return false;
            }
            if (segment.isPredicted)
            {
                skipReason = "predicted";
                return false;
            }
            if (!IsValidClosedSegment(segment))
            {
                skipReason = "invalid";
                return false;
            }

            if (rec.TrackSections == null)
                rec.TrackSections = new List<TrackSection>();

            if (LastSectionCheckpointMatches(rec.TrackSections, segment))
            {
                skipReason = "duplicate-last";
                return false;
            }
            if (TryAttachToLastEmptyCheckpointSection(rec.TrackSections, segment))
            {
                AppendFlatOrbitCache(rec, segment);
                rec.CachedStats = null;
                rec.CachedStatsPointCount = 0;
                if (markDirty)
                    rec.MarkFilesDirty();
                return true;
            }

            rec.TrackSections.Add(BuildClosedCheckpointSection(segment));
            AppendFlatOrbitCache(rec, segment);
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
            if (markDirty)
                rec.MarkFilesDirty();
            return true;
        }

        internal static OrbitSegmentCheckpointBridgeStats EnsureCheckpointSectionsForTopLevelOrbitSegments(
            Recording rec,
            bool markDirty)
        {
            var stats = new OrbitSegmentCheckpointBridgeStats();
            if (rec == null || rec.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return stats;

            if (rec.TrackSections == null)
                rec.TrackSections = new List<TrackSection>();

            bool sawPredictedSegment = false;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
            {
                OrbitSegment segment = rec.OrbitSegments[i];
                if (segment.isPredicted)
                {
                    sawPredictedSegment = true;
                    stats.SkippedPredicted++;
                    continue;
                }
                if (sawPredictedSegment)
                {
                    stats.SkippedAfterPredicted++;
                    continue;
                }
                if (!IsValidClosedSegment(segment))
                {
                    stats.SkippedInvalid++;
                    continue;
                }
                if (AnyCheckpointMatches(rec.TrackSections, segment))
                {
                    stats.SkippedExisting++;
                    continue;
                }
                if (TryAttachToAnyEmptyCheckpointSection(rec.TrackSections, segment))
                {
                    stats.Added++;
                    continue;
                }

                rec.TrackSections.Add(BuildClosedCheckpointSection(segment));
                stats.Added++;
            }

            if (stats.Added > 0)
            {
                SortTrackSections(rec.TrackSections);
                rec.CachedStats = null;
                rec.CachedStatsPointCount = 0;
                if (markDirty)
                    rec.MarkFilesDirty();
            }

            return stats;
        }

        internal static bool AnyCheckpointMatches(List<TrackSection> sections, OrbitSegment segment)
        {
            if (sections == null)
                return false;

            for (int i = 0; i < sections.Count; i++)
            {
                if (CheckpointSectionMatches(sections[i], segment))
                    return true;
            }

            return false;
        }

        private static bool TryAttachToLastEmptyCheckpointSection(
            List<TrackSection> sections,
            OrbitSegment segment)
        {
            if (sections == null || sections.Count == 0)
                return false;

            return TryAttachToEmptyCheckpointSection(sections, sections.Count - 1, segment);
        }

        private static bool TryAttachToAnyEmptyCheckpointSection(
            List<TrackSection> sections,
            OrbitSegment segment)
        {
            if (sections == null)
                return false;

            for (int i = 0; i < sections.Count; i++)
            {
                if (TryAttachToEmptyCheckpointSection(sections, i, segment))
                    return true;
            }

            return false;
        }

        private static bool TryAttachToEmptyCheckpointSection(
            List<TrackSection> sections,
            int index,
            OrbitSegment segment)
        {
            TrackSection section = sections[index];
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                || !NearlyEqual(section.startUT, segment.startUT, UtTolerance)
                || !NearlyEqual(section.endUT, segment.endUT, UtTolerance)
                || (section.checkpoints != null && section.checkpoints.Count > 0))
            {
                return false;
            }

            section.checkpoints = new List<OrbitSegment> { segment };
            if (section.frames == null)
                section.frames = new List<TrajectoryPoint>();
            section.environment = SegmentEnvironment.ExoBallistic;
            section.source = TrackSectionSource.Checkpoint;
            section.minAltitude = float.NaN;
            section.maxAltitude = float.NaN;
            sections[index] = section;
            return true;
        }

        private static bool LastSectionCheckpointMatches(List<TrackSection> sections, OrbitSegment segment)
        {
            if (sections == null || sections.Count == 0)
                return false;

            return CheckpointSectionMatches(sections[sections.Count - 1], segment);
        }

        private static bool CheckpointSectionMatches(TrackSection section, OrbitSegment segment)
        {
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                || !NearlyEqual(section.startUT, segment.startUT, UtTolerance)
                || !NearlyEqual(section.endUT, segment.endUT, UtTolerance)
                || section.checkpoints == null)
            {
                return false;
            }

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                if (OrbitSegmentNearlyEquals(section.checkpoints[i], segment))
                    return true;
            }

            return false;
        }

        private static bool IsValidClosedSegment(OrbitSegment segment)
        {
            return IsFinite(segment.startUT)
                && IsFinite(segment.endUT)
                && segment.endUT > segment.startUT + UtTolerance;
        }

        private static void AppendFlatOrbitCache(Recording rec, OrbitSegment segment)
        {
            if (rec.OrbitSegments == null)
                rec.OrbitSegments = new List<OrbitSegment>();

            if (rec.OrbitSegments.Count > 0
                && OrbitSegmentNearlyEquals(rec.OrbitSegments[rec.OrbitSegments.Count - 1], segment))
            {
                return;
            }

            rec.OrbitSegments.Add(segment);
        }

        private static void SortTrackSections(List<TrackSection> sections)
        {
            sections.Sort((a, b) =>
            {
                int cmp = a.startUT.CompareTo(b.startUT);
                if (cmp != 0) return cmp;
                cmp = ((int)a.source).CompareTo((int)b.source);
                if (cmp != 0) return cmp;
                return a.endUT.CompareTo(b.endUT);
            });
        }

        private static bool OrbitSegmentNearlyEquals(OrbitSegment a, OrbitSegment b)
        {
            return NearlyEqual(a.startUT, b.startUT, UtTolerance)
                && NearlyEqual(a.endUT, b.endUT, UtTolerance)
                && NearlyEqual(a.inclination, b.inclination, ScalarTolerance)
                && NearlyEqual(a.eccentricity, b.eccentricity, ScalarTolerance)
                && NearlyEqual(a.semiMajorAxis, b.semiMajorAxis, DistanceTolerance)
                && NearlyEqual(a.longitudeOfAscendingNode, b.longitudeOfAscendingNode, ScalarTolerance)
                && NearlyEqual(a.argumentOfPeriapsis, b.argumentOfPeriapsis, ScalarTolerance)
                && NearlyEqual(a.meanAnomalyAtEpoch, b.meanAnomalyAtEpoch, ScalarTolerance)
                && NearlyEqual(a.epoch, b.epoch, UtTolerance)
                && a.bodyName == b.bodyName
                && a.isPredicted == b.isPredicted
                && NearlyEqual(a.orbitalFrameRotation.x, b.orbitalFrameRotation.x, VectorTolerance)
                && NearlyEqual(a.orbitalFrameRotation.y, b.orbitalFrameRotation.y, VectorTolerance)
                && NearlyEqual(a.orbitalFrameRotation.z, b.orbitalFrameRotation.z, VectorTolerance)
                && NearlyEqual(a.orbitalFrameRotation.w, b.orbitalFrameRotation.w, VectorTolerance)
                && NearlyEqual(a.angularVelocity.x, b.angularVelocity.x, VectorTolerance)
                && NearlyEqual(a.angularVelocity.y, b.angularVelocity.y, VectorTolerance)
                && NearlyEqual(a.angularVelocity.z, b.angularVelocity.z, VectorTolerance);
        }

        private static bool NearlyEqual(double a, double b, double tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
