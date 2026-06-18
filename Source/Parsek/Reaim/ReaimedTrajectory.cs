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
    // Render contract: the re-aim ghost renders its TRAJECTORY from OrbitSegments. Points and
    // TrackSections stay EMPTY because the recorded per-frame samples for the heliocentric leg trace the
    // PRE-re-aim path (aimed at the target's ORIGINAL position); the synthesized OrbitSegment replaces that
    // phase, and re-timing the surface ascent/landing tracks onto the synthesized spans is a deferred
    // refinement.
    //
    // PartEvents ARE delegated (this was the review-M1 concern, now obsolete): the assembled segments are
    // placed on the RECORDED-span clock - ReaimSegmentAssembler.ShiftInTime shifts the transfer epoch into
    // recorded time so it lines up with the recorded escape/capture legs - so a recorded-UT engine/staging
    // event resolves coherently against the right span. And the ghost MUST see the real engine events: the
    // engine-FX/audio orphan auto-start fires whenever a recording reports ZERO engine events (assuming a
    // debris booster firing continuously), so empty PartEvents made it misfire on the MAIN ship and loop
    // every engine through the coast (the "engine sounds on the transfer segment" playtest bug).
    //
    // ONE EXCEPTION - the parking-departure F2 capture re-time (review FIX #2): when the F2 path shifts the
    // capture leg EARLIER (the Hohmann tof is shorter than the recorded tof), the resolver re-times the
    // in-capture PartEvents (ut >= RecordedArrivalUT) by the SAME shift via ReaimSegmentAssembler
    // .ShiftCapturePartEvents and passes them through the (inner, segments, retimedPartEvents) overload, so a
    // capture-phase event still aligns with the shifted capture OrbitSegment. PRE-capture events
    // (transfer / launch / park) are unchanged. Direct transfers + non-shifted windows pass null and keep the
    // plain delegated list (byte-identical). FlagEvents stay empty (a surface-planted flag is not part of the
    // transfer replay).
    internal sealed class ReaimedTrajectory : IPlaybackTrajectory
    {
        private readonly IPlaybackTrajectory inner;
        private readonly List<OrbitSegment> segments;
        private readonly List<PartEvent> partEventsOverride; // re-timed in-capture events (FIX #2), or null => delegate
        private readonly double spanStartUT;
        private readonly double spanEndUT;
        private static readonly List<TrajectoryPoint> EmptyPoints = new List<TrajectoryPoint>();
        private static readonly List<TrackSection> EmptySections = new List<TrackSection>();
        private static readonly List<FlagEvent> EmptyFlagEvents = new List<FlagEvent>();

        /// <summary>
        /// Wraps <paramref name="inner"/> (the recorded trajectory) with the per-window
        /// <paramref name="assembledSegments"/> (absolute-UT, from ReaimSegmentAssembler). The span is
        /// derived from the assembled list's first start / last end. PartEvents are delegated to
        /// <paramref name="inner"/> at their recorded UTs.
        /// </summary>
        internal ReaimedTrajectory(IPlaybackTrajectory inner, List<OrbitSegment> assembledSegments)
            : this(inner, assembledSegments, null)
        {
        }

        /// <summary>
        /// Re-aim adapter overload that ALSO substitutes the PartEvents list (review FIX #2). When the
        /// parking-departure F2 path shifts the capture leg earlier (the Hohmann tof is shorter than the
        /// recorded tof), the resolver re-times the in-capture PartEvents by the SAME shift and passes them
        /// as <paramref name="retimedPartEvents"/> so a capture-phase event (capture-burn / staging /
        /// decouple) stays aligned with the shifted capture OrbitSegment - the engine consumes PartEvents for
        /// FX, so an unshifted event would fire off the shifted geometry. Null (the other constructor) keeps
        /// the delegated recorded-UT events (direct path / no shift).
        /// </summary>
        internal ReaimedTrajectory(
            IPlaybackTrajectory inner, List<OrbitSegment> assembledSegments, List<PartEvent> retimedPartEvents)
        {
            this.inner = inner;
            this.segments = assembledSegments ?? new List<OrbitSegment>();
            this.partEventsOverride = retimedPartEvents;
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
        public List<PartEvent> PartEvents => partEventsOverride
            ?? (inner != null ? inner.PartEvents : EmptyPartEventsFallback);
        public List<FlagEvent> FlagEvents => EmptyFlagEvents;
        private static readonly List<PartEvent> EmptyPartEventsFallback = new List<PartEvent>();
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
