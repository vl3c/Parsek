using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Predicate truth-table tests for
    /// <see cref="LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible"/>
    /// — PR 3c of the recording &amp; ghost policies refactor.
    ///
    /// Per the plan §3c §"Test seam": the gate's positioner-side wiring in
    /// <c>ParsekFlight.InterpolateAndPositionRecordedRelative</c> can't
    /// be exercised by the resolver-level harness (Step 1 §"Scope reduction").
    /// The pure predicate is extracted into <see cref="LegacyDebrisShadowGate"/>
    /// so xUnit can validate the four-condition truth table without a
    /// Unity runtime.
    ///
    /// Each test exercises one predicate branch:
    /// <list type="bullet">
    /// <item><description>Returns true for IsDebris=true, DebrisParentRecordingId=null, Relative section, non-empty shadow.</description></item>
    /// <item><description>Returns false on null trajectory.</description></item>
    /// <item><description>Returns false for non-debris (condition #1).</description></item>
    /// <item><description>Returns false for v12+ debris with DebrisParentRecordingId set (condition #2).</description></item>
    /// <item><description>Returns false for Absolute section (condition #3).</description></item>
    /// <item><description>Returns false for null shadow list (condition #4a).</description></item>
    /// <item><description>Returns false for empty shadow list (condition #4b).</description></item>
    /// </list>
    /// </summary>
    public class LegacyDebrisShadowGateTests
    {
        [Fact]
        public void Returns_True_For_LegacyDebris_With_RelativeSection_And_PopulatedShadow()
        {
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: null);
            TrackSection section = MakeRelativeSection(shadowFrames: 5);

            Assert.True(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
        }

        [Fact]
        public void Returns_False_When_Trajectory_Is_Null()
        {
            TrackSection section = MakeRelativeSection(shadowFrames: 5);

            Assert.False(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(null, section));
        }

        [Fact]
        public void Returns_False_For_NonDebris()
        {
            // Condition #1 fails: traj.IsDebris is false, so the gate must
            // never fire for non-debris recordings. This is what protects
            // the existing resolver path for non-debris from the gate.
            var traj = MakeFake(isDebris: false, debrisParentRecordingId: null);
            TrackSection section = MakeRelativeSection(shadowFrames: 5);

            Assert.False(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
        }

        [Fact]
        public void Returns_False_For_V12Plus_Debris_With_DebrisParentRecordingId_Set()
        {
            // Condition #2 fails: v12+ debris (where PR 3b has populated the
            // parent-anchor field) skips the gate, so the resolver chain
            // handles its playback per the parent-anchor contract (Decision §7);
            // if the parent anchor misses, the debris retires rather than falling
            // back to the absolute shadow.
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: "parent-id");
            TrackSection section = MakeRelativeSection(shadowFrames: 5);

            Assert.False(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
            Assert.True(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj));
        }

        [Fact]
        public void Returns_True_When_DebrisParentRecordingId_Is_EmptyString()
        {
            // Defensive: the plan's predicate uses `string.IsNullOrEmpty`, so
            // an empty-string DebrisParentRecordingId is treated identically
            // to null (legacy-debris). This shouldn't normally happen — the
            // recorder either sets a valid id (PR 3b) or leaves it null —
            // but if a corrupt or migrated save lands with "" for some
            // reason, the gate still fires and the absolute-shadow path
            // runs. Whitespace-only strings pass through the same way.
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: "");
            TrackSection section = MakeRelativeSection(shadowFrames: 5);

            Assert.True(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
        }

        [Fact]
        public void Returns_False_For_Absolute_Section()
        {
            // Condition #3 fails: Absolute sections need no special handling —
            // the resolver goes directly to the absolute frames anyway.
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: null);
            TrackSection section = MakeAbsoluteSection(shadowFrames: 5);

            Assert.False(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
        }

        [Fact]
        public void Returns_False_For_OrbitalCheckpoint_Section()
        {
            // Condition #3 also rejects OrbitalCheckpoint sections (defensive —
            // ReferenceFrame has three values; only Relative qualifies).
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: null);
            TrackSection section = MakeRelativeSection(shadowFrames: 5);
            section.referenceFrame = ReferenceFrame.OrbitalCheckpoint;

            Assert.False(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
        }

        [Fact]
        public void Returns_False_For_Null_AbsoluteFrames()
        {
            // Condition #4a fails: shadow data unavailable. Graceful no-op.
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: null);
            TrackSection section = MakeRelativeSection(shadowFrames: 0);
            section.absoluteFrames = null;

            Assert.False(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
        }

        [Fact]
        public void Returns_False_For_Empty_AbsoluteFrames()
        {
            // Condition #4b fails: shadow list is non-null but empty. No data
            // to render against — fall through to the existing resolver path
            // (which will then exercise the post-failure shadow path itself
            // and similarly fall through to RetireUnresolvedRecordedRelative).
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: null);
            TrackSection section = MakeRelativeSection(shadowFrames: 0);
            // Construction may have left absoluteFrames null when shadowFrames=0.
            section.absoluteFrames = new List<TrajectoryPoint>();

            Assert.False(LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, section));
        }

        [Fact]
        public void ParentAnchoredDebrisRetirePolicy_ReturnsFalse_For_LegacyDebris()
        {
            var traj = MakeFake(isDebris: true, debrisParentRecordingId: null);

            Assert.False(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj));
        }

        [Fact]
        public void ParentAnchoredDebrisRetirePolicy_ReturnsFalse_For_NonDebris()
        {
            var traj = MakeFake(isDebris: false, debrisParentRecordingId: "parent-id");

            Assert.False(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(traj));
        }

        [Fact]
        public void ParentAnchoredDebrisRetirePolicy_ReturnsFalse_For_NullTrajectory()
        {
            Assert.False(DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(null));
        }

        // --- Helpers ---

        private sealed class FakeTrajectory : IPlaybackTrajectory
        {
            public List<TrajectoryPoint> Points { get; set; } = new List<TrajectoryPoint>();
            public List<OrbitSegment> OrbitSegments { get; set; } = new List<OrbitSegment>();
            public bool HasOrbitSegments => false;
            public List<TrackSection> TrackSections { get; set; } = new List<TrackSection>();
            public double StartUT => 0;
            public double EndUT => 10;
            public int RecordingFormatVersion => RecordingStore.CurrentRecordingFormatVersion;
            public List<PartEvent> PartEvents { get; set; } = new List<PartEvent>();
            public List<FlagEvent> FlagEvents { get; set; } = new List<FlagEvent>();
            public ConfigNode GhostVisualSnapshot => null;
            public ConfigNode VesselSnapshot => null;
            public string VesselName { get; set; } = "fake";
            public string RecordingId { get; set; } = "fake-id";
            public bool LoopPlayback => false;
            public double LoopIntervalSeconds => 0;
            public LoopTimeUnit LoopTimeUnit => LoopTimeUnit.Sec;
            public uint LoopAnchorVesselId => 0;
            public double LoopStartUT => 0;
            public double LoopEndUT => 0;
            public TerminalState? TerminalStateValue => null;
            public SurfacePosition? SurfacePos => null;
            public double TerrainHeightAtEnd => double.NaN;
            public bool PlaybackEnabled => true;
            public bool IsDebris { get; set; }
            public string DebrisParentRecordingId { get; set; }
            public int LoopSyncParentIdx { get; set; } = -1;
            public string TerminalOrbitBody => null;
            public double TerminalOrbitSemiMajorAxis => 0;
            public double TerminalOrbitEccentricity => 0;
            public double TerminalOrbitInclination => 0;
            public double TerminalOrbitLAN => 0;
            public double TerminalOrbitArgumentOfPeriapsis => 0;
            public double TerminalOrbitMeanAnomalyAtEpoch => 0;
            public double TerminalOrbitEpoch => 0;
            public RecordingEndpointPhase EndpointPhase => RecordingEndpointPhase.Unknown;
            public string EndpointBodyName => null;
        }

        private static FakeTrajectory MakeFake(bool isDebris, string debrisParentRecordingId)
        {
            return new FakeTrajectory
            {
                IsDebris = isDebris,
                DebrisParentRecordingId = debrisParentRecordingId,
            };
        }

        private static TrackSection MakeRelativeSection(int shadowFrames)
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.ExoBallistic,
                source = TrackSectionSource.Active,
                startUT = 0.0,
                endUT = 10.0,
                anchorRecordingId = "wrong-anchor-id",
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                absoluteFrames = shadowFrames > 0 ? new List<TrajectoryPoint>() : null,
            };
            for (int i = 0; i < shadowFrames; i++)
            {
                section.absoluteFrames.Add(new TrajectoryPoint
                {
                    ut = i,
                    latitude = 1.0 + i,
                    longitude = 2.0 + i,
                    altitude = 3.0 + i,
                    bodyName = "Kerbin",
                    rotation = UnityEngine.Quaternion.identity,
                });
            }
            return section;
        }

        private static TrackSection MakeAbsoluteSection(int shadowFrames)
        {
            var section = MakeRelativeSection(shadowFrames);
            section.referenceFrame = ReferenceFrame.Absolute;
            return section;
        }
    }
}
