using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Per-window IPlaybackTrajectory adapter for a re-aimed ghost (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3). Wraps the recording and presents the
    // ReaimSegmentAssembler's per-window absolute-UT OrbitSegment list in place of the recorded
    // trajectory, delegating identity / visual / loop-config to the wrapped recording so the ghost
    // still looks like the recorded vessel.
    //
    // EAGER (review C3): the assembled segment list is computed ONCE by the caller for the window and
    // passed to the constructor; the OrbitSegments getter is a pure field return. No Lambert /
    // CalculatePatch ever runs inside a getter (those are on the per-frame hot path).
    //
    // v1 render contract: the re-aim ghost renders from OrbitSegments ONLY. Points, TrackSections,
    // PartEvents, and FlagEvents are presented EMPTY because they are recorded at the original
    // timeline's UTs and cannot be coherently mixed with the absolute-UT synthesized transfer (review
    // M1 - an event bound to a recorded UT would resolve against the wrong segment). This means v1 has
    // no surface-ascent/landing replay or staging FX on the re-aim ghost; the dominant visual (the
    // interplanetary transfer arc + capture orbit) is what renders. Re-timing the ascent/arrival
    // surface tracks + events onto the synthesized spans is a deferred refinement.
    internal sealed class ReaimedTrajectory : IPlaybackTrajectory
    {
        private readonly IPlaybackTrajectory inner;
        private readonly List<OrbitSegment> segments;
        private readonly double spanStartUT;
        private readonly double spanEndUT;
        private static readonly List<TrajectoryPoint> EmptyPoints = new List<TrajectoryPoint>();
        private static readonly List<TrackSection> EmptySections = new List<TrackSection>();
        private static readonly List<PartEvent> EmptyPartEvents = new List<PartEvent>();
        private static readonly List<FlagEvent> EmptyFlagEvents = new List<FlagEvent>();

        /// <summary>
        /// Wraps <paramref name="inner"/> (the recorded trajectory) with the per-window
        /// <paramref name="assembledSegments"/> (absolute-UT, from ReaimSegmentAssembler). The span is
        /// derived from the assembled list's first start / last end.
        /// </summary>
        internal ReaimedTrajectory(IPlaybackTrajectory inner, List<OrbitSegment> assembledSegments)
        {
            this.inner = inner;
            this.segments = assembledSegments ?? new List<OrbitSegment>();
            if (this.segments.Count > 0)
            {
                spanStartUT = this.segments[0].startUT;
                spanEndUT = this.segments[this.segments.Count - 1].endUT;
            }
            else
            {
                spanStartUT = inner != null ? inner.StartUT : 0.0;
                spanEndUT = inner != null ? inner.EndUT : 0.0;
            }
        }

        // === Re-aimed trajectory shape (overridden) ===
        public List<OrbitSegment> OrbitSegments => segments;
        public bool HasOrbitSegments => segments.Count > 0;
        public List<TrajectoryPoint> Points => EmptyPoints;
        public List<TrackSection> TrackSections => EmptySections;
        public List<PartEvent> PartEvents => EmptyPartEvents;
        public List<FlagEvent> FlagEvents => EmptyFlagEvents;
        public double StartUT => spanStartUT;
        public double EndUT => spanEndUT;

        // === Delegated identity / visual / config (the ghost still looks + identifies as the recorded vessel) ===
        public int RecordingFormatVersion => inner.RecordingFormatVersion;
        public ConfigNode GhostVisualSnapshot => inner.GhostVisualSnapshot;
        public ConfigNode VesselSnapshot => inner.VesselSnapshot;
        public string VesselName => inner.VesselName;
        public string RecordingId => inner.RecordingId;
        public bool LoopPlayback => inner.LoopPlayback;
        public double LoopIntervalSeconds => inner.LoopIntervalSeconds;
        public LoopTimeUnit LoopTimeUnit => inner.LoopTimeUnit;
        public uint LoopAnchorVesselId => inner.LoopAnchorVesselId;
        public double LoopStartUT => inner.LoopStartUT;
        public double LoopEndUT => inner.LoopEndUT;
        public TerminalState? TerminalStateValue => inner.TerminalStateValue;
        public SurfacePosition? SurfacePos => inner.SurfacePos;
        public double TerrainHeightAtEnd => inner.TerrainHeightAtEnd;
        public bool PlaybackEnabled => inner.PlaybackEnabled;
        public bool IsDebris => inner.IsDebris;
        public string ParentAnchorRecordingId => inner.ParentAnchorRecordingId;
        public int LoopSyncParentIdx { get => inner.LoopSyncParentIdx; set => inner.LoopSyncParentIdx = value; }
        public string TerminalOrbitBody => inner.TerminalOrbitBody;
        public double TerminalOrbitSemiMajorAxis => inner.TerminalOrbitSemiMajorAxis;
        public double TerminalOrbitEccentricity => inner.TerminalOrbitEccentricity;
        public double TerminalOrbitInclination => inner.TerminalOrbitInclination;
        public double TerminalOrbitLAN => inner.TerminalOrbitLAN;
        public double TerminalOrbitArgumentOfPeriapsis => inner.TerminalOrbitArgumentOfPeriapsis;
        public double TerminalOrbitMeanAnomalyAtEpoch => inner.TerminalOrbitMeanAnomalyAtEpoch;
        public double TerminalOrbitEpoch => inner.TerminalOrbitEpoch;
        public RecordingEndpointPhase EndpointPhase => inner.EndpointPhase;
        public string EndpointBodyName => inner.EndpointBodyName;
    }
}
