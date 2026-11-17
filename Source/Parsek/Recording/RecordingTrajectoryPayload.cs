using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// What one trajectory sidecar decodes to, without a <see cref="Recording"/>: the six
    /// lists, the header facts and the decode stats. <see cref="TrajectorySidecarBinary.Decode"/>
    /// fills it; <see cref="TrajectorySidecarBinary.Read"/> decodes into one whose lists are
    /// the recording's own (<see cref="OverListsOf"/>), so a read keeps the recording's list
    /// instances unless the flat-fallback heal replaced one.
    ///
    /// <para>Public fields, not properties: a value no consumer reads yet adds no method to
    /// the architecture suite's unreached count.</para>
    /// </summary>
    internal sealed class RecordingTrajectoryPayload
    {
        public List<TrajectoryPoint> Points = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments = new List<OrbitSegment>();
        public List<PartEvent> PartEvents = new List<PartEvent>();
        public List<FlagEvent> FlagEvents = new List<FlagEvent>();
        public List<SegmentEvent> SegmentEvents = new List<SegmentEvent>();
        public List<TrackSection> TrackSections = new List<TrackSection>();

        // Header facts, copied from the probe the decode ran under.
        public int FormatVersion;
        public int SchemaGeneration;
        public int SidecarEpoch;
        public string ProbeRecordingId;

        // The file's section-authoritative flag: the flat lists were rebuilt from the
        // sections instead of read from the file.
        public bool SectionAuthoritative;

        public RecordingTrajectoryDecodeStats Stats;

        /// <summary>
        /// A payload whose six lists ARE <paramref name="rec"/>'s list instances (a null
        /// list stays null), so decoding into it fills the recording's lists in place.
        /// </summary>
        internal static RecordingTrajectoryPayload OverListsOf(Recording rec)
        {
            var payload = new RecordingTrajectoryPayload();
            payload.Points = rec.Points;
            payload.OrbitSegments = rec.OrbitSegments;
            payload.PartEvents = rec.PartEvents;
            payload.FlagEvents = rec.FlagEvents;
            payload.SegmentEvents = rec.SegmentEvents;
            payload.TrackSections = rec.TrackSections;
            return payload;
        }
    }

    /// <summary>
    /// Counts one decode gathers; <see cref="TrajectorySidecarBinary.Read"/> logs them.
    /// </summary>
    internal struct RecordingTrajectoryDecodeStats
    {
        // Sparse point lists (points, frames and body-fixed frames of every section).
        public int SparsePointLists;
        public int SparsePoints;
        public int DefaultedBody;
        public int DefaultedFunds;
        public int DefaultedScience;
        public int DefaultedReputation;

        // Flat path: whether the flat-fallback heal replaced Points and / or OrbitSegments,
        // and the counts as read, before the heal.
        public bool HealedFlatFallback;
        public int PreHealPointCount;
        public int PreHealOrbitSegmentCount;

        // Section-authoritative path: boundary copies the rebuild collapsed.
        public int DedupedPointCopies;
        public int DedupedOrbitSegmentCopies;

        // Flat points dropped as non-monotonic, the last step of the decode. The point
        // count before the drop is Points.Count + DroppedNonMonotonicPoints.
        public int DroppedNonMonotonicPoints;
    }
}
