using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the units the Tracking Station ghost refresh
    /// (<c>GhostMapPresence.RefreshTrackingStationGhosts</c>) is composed of. The end-to-end
    /// refresh contract is pinned by <see cref="TrackingStationRefreshCharacterizationTests"/>;
    /// these cells pin each unit's own outputs, including the stores it must leave alone.
    /// A cell that enters a unit whose callees reach KSP orbit / body / vessel methods is a
    /// <see cref="UnityEntryFactAttribute"/>.
    /// </summary>
    [Collection("Sequential")]
    public class TrackingStationRefreshPhaseTests : IDisposable
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;
        private readonly GameScenes priorScene;

        public TrackingStationRefreshPhaseTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            priorScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.TRACKSTATION;
            kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            GhostMapPresence.FindBodyByNameForTesting = name =>
                string.Equals(name, "Kerbin", StringComparison.Ordinal) ? kerbin : null;
            GhostMapPresence.CurrentUTNow = () => 500.0;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = priorScene;
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GhostPlaybackLogic.LoopUnitSet Empty => GhostPlaybackLogic.LoopUnitSet.Empty;

        private bool Logged(string fragment) => logLines.Exists(l => l.Contains(fragment));

        private void AcceptTailSeed()
        {
            OrbitSeedResolver.TailSeedResolverForTesting =
                (IPlaybackTrajectory traj, CelestialBody body, double currentUT, TailSeedUse use,
                    out TailDerivedOrbitSeed seed) =>
                {
                    seed = GhostMapEndpointTailTests.AcceptedTailSeed(
                        tailUT: 453.66,
                        latestStoredSegmentEndUT: 150.0,
                        rotationDriftSeconds: currentUT - 453.66);
                    return true;
                };
        }

        private static Recording EndpointRecording()
        {
            return GhostMapEndpointTailTests.BuildEndpointRecording(
                GhostMapEndpointTailTests.Segment(startUT: 120.0, endUT: 150.0));
        }

        private static List<Recording> List(params Recording[] recs) => new List<Recording>(recs);

        // ---------------------------------------------------------------- teardown reason

        [Fact]
        public void Teardown_NullCommitted_ReturnsCommittedNullWithLiveUT()
        {
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                0, null, 500.0, Empty, out Recording rec, out double effUT);
            Assert.Equal("tracking-station-committed-null", reason);
            Assert.Null(rec);
            Assert.Equal(500.0, effUT);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        [InlineData(4)]
        public void Teardown_IndexOutsideList_ReturnsIndexStale(int idx)
        {
            var committed = List(TrackingStationRefreshCharacterizationTests.SegmentRecording("t0"));
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                idx, committed, 500.0, Empty, out Recording rec, out double effUT);
            Assert.Equal("tracking-station-index-stale", reason);
            Assert.Null(rec);
            Assert.Equal(500.0, effUT);
        }

        [Fact]
        public void Teardown_OverlapRecording_ReturnsHandoffWithTheRecording()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("t1");
            r.LoopPlayback = true;
            r.LoopIntervalSeconds = 60;
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                0, List(r), 500.0, Empty, out Recording rec, out _);
            Assert.Equal("overlap-handoff-to-per-instance", reason);
            Assert.Same(r, rec);
        }

        [Fact]
        public void Teardown_LoopMemberOutsideWindow_ReturnsOutOfWindow()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("t2");
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                0, List(r), 3500.0, TrackingStationRefreshCharacterizationTests.SingleMemberUnit(0),
                out Recording rec, out _);
            Assert.Equal("mission-loop-out-of-window", reason);
            Assert.Same(r, rec);
        }

        [Fact]
        public void Teardown_LoopMemberInWindow_NullWithTheLoopSampleUT()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("t3");
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                0, List(r), 2500.0, TrackingStationRefreshCharacterizationTests.SingleMemberUnit(0),
                out Recording rec, out double effUT);
            Assert.Null(reason);
            Assert.Same(r, rec);
            Assert.Equal(500.0, effUT);
        }

        // The re-aim landing fixture of DescentTriggerTests: member 5 is the destination transfer member,
        // member 7 a ride-along left at the launch body. The Done instant (2,050 s after the trigger, past
        // the 2,000 s clip) is inside both members' windows on the raw loop clock.
        private static List<Recording> DescentUnitCommitted()
        {
            var committed = new List<Recording>();
            for (int k = 0; k < 8; k++)
                committed.Add(TrackingStationRefreshCharacterizationTests.SegmentRecording(
                    "d" + k.ToString(Ic)));
            return committed;
        }

        private static double DescentUnitDoneUT()
            => Parsek.Reaim.DescentTrigger.ComputeRotationAlignedTriggerUT(
                1_000_000.0 + 30_000_000.0 - 2_860_000.0, 30_000_000.0, 65517.859375) + 2_050.0;

        [Fact]
        public void Teardown_TransferMemberAfterLanding_ReturnsOutOfWindow_BeforeTheLoiterGapClamp()
        {
            // The refresh tears the transfer member's ghost down on the hidden answer, so it never reaches
            // ResolveTrackingStationSegmentLookupUT (the loiter-gap clamp + the parking-conic line hold).
            var committed = DescentUnitCommitted();
            double doneUT = DescentUnitDoneUT();
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                5, committed, doneUT, DescentTriggerTests.BuildDescentUnit(engage: true),
                out Recording rec, out double effUT);
            Assert.Equal("mission-loop-out-of-window", reason);
            Assert.Same(committed[5], rec);
            Assert.Equal(doneUT, effUT); // hidden: the live UT, not a head wrapped onto the parking conic
        }

        [Fact]
        public void Teardown_RideAlongAfterLanding_NullWithTheLoopSampleUT()
        {
            var committed = DescentUnitCommitted();
            double doneUT = DescentUnitDoneUT();
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                7, committed, doneUT, DescentTriggerTests.BuildDescentUnit(engage: true),
                out Recording rec, out double effUT);
            Assert.Null(reason);
            Assert.Same(committed[7], rec);
            Assert.Equal(doneUT - 1_000_000.0, effUT, 6);
        }

        [Fact]
        public void Teardown_TransferMember_UnitWithoutDescentTrigger_NullAtTheSameInstant()
        {
            var committed = DescentUnitCommitted();
            double doneUT = DescentUnitDoneUT();
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                5, committed, doneUT, DescentTriggerTests.BuildDescentUnit(engage: false),
                out _, out double effUT);
            Assert.Null(reason);
            Assert.Equal(doneUT - 1_000_000.0, effUT, 6);
        }

        [Fact]
        public void Teardown_PlainRecording_NullWithTheLiveUT()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("t4");
            string reason = GhostMapPresence.ResolveTrackingStationRefreshTeardownReason(
                0, List(r), 512.5, Empty, out Recording rec, out double effUT);
            Assert.Null(reason);
            Assert.Same(r, rec);
            Assert.Equal(512.5, effUT);
        }

        // ---------------------------------------------------------------- segment lookup UT

        [UnityEntryFact]
        public void SegmentLookup_OutsideLoiterGap_ReturnsEffUTAndClearsTheLineHold()
        {
            GhostMapPresence.StampParkingConicLineHold(77u, 9999.0);
            double lookup = GhostMapPresence.ResolveTrackingStationSegmentLookupUT(
                0, 77u, TrackingStationRefreshCharacterizationTests.SegmentRecording("u0"),
                432.0, 500.0, Empty);
            Assert.Equal(432.0, lookup);
            Assert.False(GhostMapPresence.TryGetParkingConicLineHold(77u, 0.0, out _));
            Assert.Empty(logLines);
        }

        [UnityEntryFact]
        public void SegmentLookup_TransferMemberInLoiterGap_HoldsAtTheParkingConicEnd()
        {
            GhostPlaybackLogic.LoopUnitSet units = DescentTriggerTests.BuildDescentUnit(engage: true);
            double conicEnd = GhostPlaybackLogic.ResolveLoiterGapConicEndUT(units, 5);
            Assert.False(double.IsNaN(conicEnd));
            double effUT = conicEnd + 1000.0;

            double lookup = GhostMapPresence.ResolveTrackingStationSegmentLookupUT(
                5, 78u, TrackingStationRefreshCharacterizationTests.SegmentRecording("u1"),
                effUT, effUT, units);

            Assert.Equal(conicEnd, lookup);
            Assert.True(Logged("TS loiter-gap clamp: member=5 effUT="
                + effUT.ToString("F1", Ic) + " held segment-lookup UT at parkingConicEnd="
                + conicEnd.ToString("F1", Ic)));
        }

        [UnityEntryFact]
        public void SegmentLookup_OwnerOfADescentUnit_IsNotClamped()
        {
            GhostPlaybackLogic.LoopUnitSet units = DescentTriggerTests.BuildDescentUnit(engage: true);
            double effUT = GhostPlaybackLogic.ResolveLoiterGapConicEndUT(units, 5) + 1000.0;
            double lookup = GhostMapPresence.ResolveTrackingStationSegmentLookupUT(
                0, 79u, TrackingStationRefreshCharacterizationTests.SegmentRecording("u2"),
                effUT, effUT, units);
            Assert.Equal(effUT, lookup);
            Assert.False(Logged("TS loiter-gap clamp"));
        }

        // ---------------------------------------------------------------- covering segment

        [UnityEntryFact]
        public void Covering_SegmentAtUT_CoversAndDoesNotGlide()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("c0");
            OrbitSegment? seg = GhostMapPresence.ResolveTrackingStationCoveringSegment(
                0, r, r.OrbitSegments, 500.0, 500.0, false, out bool covers, out bool glide);
            Assert.True(seg.HasValue);
            Assert.Equal(400.0, seg.Value.startUT);
            Assert.True(covers);
            Assert.False(glide);
            Assert.True(Logged("TS covering-segment CHANGED: member=0 effUT=500.0 body=Kerbin covered=True source=Segment"));
        }

        [UnityEntryFact]
        public void Covering_LookupUTDrivesTheQueryAndEffUTTheLog()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("c1");
            OrbitSegment? seg = GhostMapPresence.ResolveTrackingStationCoveringSegment(
                3, r, r.OrbitSegments, 500.0, 950.0, false, out bool covers, out _);
            Assert.True(seg.HasValue);
            Assert.True(covers);
            Assert.True(Logged("member=3 effUT=950.0 body=Kerbin covered=True"));
        }

        [UnityEntryFact]
        public void Covering_NullRecordingOrSegments_NoSegment()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("c2");
            Assert.False(GhostMapPresence.ResolveTrackingStationCoveringSegment(
                0, null, r.OrbitSegments, 500.0, 500.0, false, out bool covers1, out bool glide1).HasValue);
            Assert.False(GhostMapPresence.ResolveTrackingStationCoveringSegment(
                1, r, null, 500.0, 500.0, false, out bool covers2, out bool glide2).HasValue);
            Assert.False(covers1 || glide1 || covers2 || glide2);
        }

        [UnityEntryFact]
        public void Covering_StateVectorGhost_NeverGlidesAndLogsStateVector()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("c3");
            GhostMapPresence.ResolveTrackingStationCoveringSegment(
                0, r, r.OrbitSegments, 500.0, 500.0, true, out bool covers, out bool glide);
            Assert.True(covers);
            Assert.False(glide);
            Assert.True(Logged("covered=True source=StateVector"));
        }

        // ---------------------------------------------------------------- checkpoint point

        [UnityEntryFact]
        public void Checkpoint_NoCoveringSegment_ResolvesThePointWithoutTheDeferLine()
        {
            var r = TrackingStationRefreshCharacterizationTests.CheckpointRecording("k0", false);
            int cached = -1;
            bool from = GhostMapPresence.TryResolveTrackingStationCheckpointPoint(
                0, 90u, r, 350.0, null, false, false, ref cached, out TrajectoryPoint point);
            Assert.True(from);
            Assert.Equal("Kerbin", point.bodyName);
            Assert.NotEqual(-1, cached);
            Assert.False(Logged("deferring to covering OrbitSegment"));
        }

        [UnityEntryFact]
        public void Checkpoint_CoveredBySegment_LogsTheDeferLine()
        {
            var r = TrackingStationRefreshCharacterizationTests.CheckpointRecording("k1", true);
            OrbitSegment? seg = r.OrbitSegments[0];
            int cached = -1;
            bool from = GhostMapPresence.TryResolveTrackingStationCheckpointPoint(
                4, 91u, r, 500.0, seg, true, false, ref cached, out _);
            Assert.True(from);
            Assert.True(Logged("TS orbit source: OrbitalCheckpoint section at effUT=500.0 deferring to covering "
                + "OrbitSegment (idx=4 pid=91 segUT=400.0-600.0 body=Kerbin)"));
        }

        [UnityEntryFact]
        public void Checkpoint_StateVectorGhost_DoesNotLogTheDefer()
        {
            var r = TrackingStationRefreshCharacterizationTests.CheckpointRecording("k2", true);
            int cached = -1;
            Assert.True(GhostMapPresence.TryResolveTrackingStationCheckpointPoint(
                0, 92u, r, 500.0, r.OrbitSegments[0], true, true, ref cached, out _));
            Assert.False(Logged("deferring to covering OrbitSegment"));
        }

        [UnityEntryFact]
        public void Checkpoint_NoCheckpointSection_False()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("k3");
            int cached = 7;
            Assert.False(GhostMapPresence.TryResolveTrackingStationCheckpointPoint(
                0, 93u, r, 500.0, r.OrbitSegments[0], true, false, ref cached, out _));
            Assert.Equal(7, cached);
            Assert.Empty(logLines);
        }

        // ---------------------------------------------------------------- removal reason

        [UnityEntryFact]
        public void Removal_Suppressed_ChildStarted()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("r0");
            string reason = GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                r, true, false, true, false, false, 500.0, 500.0, 0.0, r.OrbitSegments,
                out bool acceptTerminal);
            Assert.Equal("tracking-station-child-started", reason);
            Assert.False(acceptTerminal);
        }

        [UnityEntryFact]
        public void Removal_CheckpointCountsAsStateVector()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("r1");
            Assert.Equal("tracking-station-state-vector-expired",
                GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                    r, false, false, true, false, true, 950.0, 950.0, 0.0, r.OrbitSegments, out _));
            Assert.Null(GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                r, false, false, true, false, true, 500.0, 950.0, 0.0, r.OrbitSegments, out _));
            Assert.Equal("tracking-station-state-vector-expired",
                GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                    r, false, false, true, true, false, 950.0, 950.0, 0.0, r.OrbitSegments, out _));
        }

        [UnityEntryFact]
        public void Removal_ExpiryUsesTheLookupUT()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("r2");
            // Lookup inside the segment keeps the ghost even though effUT is past it.
            Assert.Null(GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                r, false, false, true, false, false, 500.0, 950.0, 0.0, r.OrbitSegments, out _));
            Assert.Equal("tracking-station-expired",
                GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                    r, false, false, true, false, false, 300.0, 300.0, 0.0, r.OrbitSegments, out _));
        }

        [UnityEntryFact]
        public void Removal_EndpointTailRescuesTheExpiry()
        {
            AcceptTailSeed();
            var r = EndpointRecording();
            Assert.Null(GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                r, false, false, true, false, false, 700.0, 700.0, 0.0, r.OrbitSegments,
                out bool acceptTerminal));
            Assert.False(acceptTerminal);
        }

        [UnityEntryFact]
        public void Removal_LoopShiftArmsTerminalSynthesisForASameBodyMember()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("r3");
            GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                r, false, false, true, false, false, 500.0, 500.0, 2000.0, r.OrbitSegments,
                out bool sameBody);
            Assert.True(sameBody);

            var cross = TrackingStationRefreshCharacterizationTests.SegmentRecording("r4");
            cross.TerminalOrbitBody = "Mun";
            GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                cross, false, false, true, false, false, 500.0, 500.0, 2000.0, cross.OrbitSegments,
                out bool crossBody);
            Assert.False(crossBody);
        }

        [UnityEntryFact]
        public void Removal_MaterializedKeptOnlyForALoopMemberInWindow()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("r5");
            Assert.Equal("tracking-station-spawned-real-vessel",
                GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                    r, false, true, true, false, false, 500.0, 500.0, 0.0, r.OrbitSegments, out _));
            Assert.Null(GhostMapPresence.ResolveTrackingStationRefreshRemovalReason(
                r, false, true, true, false, false, 500.0, 500.0, 2000.0, r.OrbitSegments, out _));
        }

        // ---------------------------------------------------------------- gap glide

        [UnityEntryFact]
        public void GapGlide_BracketedPoint_DrivesAndKeepsTheGhost()
        {
            var r = TrackingStationRefreshCharacterizationTests.Orbiting("g0");
            r.Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 350, bodyName = "Kerbin", altitude = 90000 },
                new TrajectoryPoint { ut = 450, bodyName = "Kerbin", altitude = 150000 },
            };
            TrackingStationRefreshCharacterizationTests.Ghost(2, 9300u, (200, 300));
            int cached = -1;
            bool handled = GhostMapPresence.TryDriveTrackingStationGapFromPoints(
                2, r, 400.0, 0.0, ref cached, out string reason);
            Assert.True(handled);
            Assert.Null(reason);
            Assert.Equal(cached, TrackingStationRefreshCharacterizationTests.SvCachedIndices[2]);
            Assert.True(Logged("TS gap-points glide: member=2 effUT=400.0 body=Kerbin"));
            Assert.True(Logged("reFlySuppressed=False"));
        }

        [UnityEntryFact]
        public void GapGlide_NoBracketingPoint_FallsThroughAndStoresTheIndex()
        {
            var r = TrackingStationRefreshCharacterizationTests.Orbiting("g1");
            r.Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 420, bodyName = "Kerbin", altitude = 150000 },
                new TrajectoryPoint { ut = 900, bodyName = "Kerbin", altitude = 200000 },
            };
            int cached = -1;
            bool handled = GhostMapPresence.TryDriveTrackingStationGapFromPoints(
                1, r, 400.0, 0.0, ref cached, out string reason);
            Assert.False(handled);
            Assert.Null(reason);
            Assert.True(TrackingStationRefreshCharacterizationTests.SvCachedIndices.ContainsKey(1));
            Assert.True(Logged("TS gap-points glide skipped (no bracketing point): member=1 effUT=400.0"));
        }

        // ---------------------------------------------------------------- state vector

        [UnityEntryFact]
        public void StateVector_NoBracketingPoint_Expired()
        {
            var r = TrackingStationRefreshCharacterizationTests.PointsOnlyRecording("v0");
            int cached = 0;
            Assert.Equal("tracking-station-state-vector-expired",
                GhostMapPresence.RefreshTrackingStationStateVectorGhost(0, r, 50.0, 0.0, ref cached));
            Assert.True(TrackingStationRefreshCharacterizationTests.SvCachedIndices.ContainsKey(0));
        }

        [UnityEntryFact]
        public void StateVector_BelowThreshold_Removed()
        {
            var r = TrackingStationRefreshCharacterizationTests.PointsOnlyRecording("v1", 100, 50);
            int cached = -1;
            Assert.Equal("below-state-vector-threshold",
                GhostMapPresence.RefreshTrackingStationStateVectorGhost(0, r, 500.0, 0.0, ref cached));
        }

        [UnityEntryFact]
        public void StateVector_AboveThreshold_UpdatesInPlace()
        {
            var r = TrackingStationRefreshCharacterizationTests.PointsOnlyRecording("v2");
            TrackingStationRefreshCharacterizationTests.Ghost(0, 9200u, null, r);
            int cached = -1;
            Assert.Null(GhostMapPresence.RefreshTrackingStationStateVectorGhost(0, r, 500.0, 0.0, ref cached));
            Assert.True(Logged("update-state-vector-miss: rec=v2 idx=0"));
        }

        // ---------------------------------------------------------------- segment

        [UnityEntryFact]
        public void Segment_NoBoundsNoCovering_KeepsWithoutApplying()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("s0");
            TrackingStationRefreshCharacterizationTests.Ghost(0, 9100u);
            Assert.Null(GhostMapPresence.RefreshTrackingStationSegmentGhost(
                0, r, 300.0, null, false, default((double, double)), 0.0, r.OrbitSegments, false));
            Assert.Empty(logLines);
        }

        [UnityEntryFact]
        public void Segment_BoundsUnchanged_DoesNotReapply()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("s1");
            TrackingStationRefreshCharacterizationTests.Ghost(0, 9101u, (400, 600));
            Assert.Null(GhostMapPresence.RefreshTrackingStationSegmentGhost(
                0, r, 500.0, r.OrbitSegments[0], true, (400.0, 600.0), 0.0, r.OrbitSegments, false));
            Assert.False(Logged("map orbit applied"));
        }

        [UnityEntryFact]
        public void Segment_BoundsShiftedByTheLoop_Reapplies()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("s2");
            TrackingStationRefreshCharacterizationTests.Ghost(0, 9102u, (400, 600));
            Assert.Null(GhostMapPresence.RefreshTrackingStationSegmentGhost(
                0, r, 500.0, r.OrbitSegments[0], true, (400.0, 600.0), 2000.0, r.OrbitSegments, false));
            Assert.True(Logged("map orbit applied: rec=0 source=Segment body=Kerbin"));
            Assert.Equal((2400.0, 2600.0), GhostMapPresence.ghostBodyFrameOrbitBounds[9102u]);
        }

        [UnityEntryFact]
        public void Segment_NoCoveringWithAFutureSegment_GapBetweenSegments()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("s3");
            Assert.Equal("gap-between-orbit-segments",
                GhostMapPresence.RefreshTrackingStationSegmentGhost(
                    0, r, 300.0, null, true, (100.0, 200.0), 0.0, r.OrbitSegments, false));
        }

        [UnityEntryFact]
        public void Segment_NoCoveringNoFuture_Expired()
        {
            var r = TrackingStationRefreshCharacterizationTests.SegmentRecording("s4");
            r.TerminalStateValue = TerminalState.Landed;
            Assert.Equal("tracking-station-expired",
                GhostMapPresence.RefreshTrackingStationSegmentGhost(
                    0, r, 950.0, null, true, (400.0, 600.0), 0.0, r.OrbitSegments, false));
        }

        // ---------------------------------------------------------------- endpoint tail

        [UnityEntryFact]
        public void EndpointTail_NonLoopCoveringInTerminalRegion_Overrides()
        {
            AcceptTailSeed();
            var r = EndpointRecording();
            OrbitSegment? seg = r.OrbitSegments[0];
            var source = GhostMapPresence.TrackingStationGhostSource.Segment;
            GhostMapPresence.ResolveTrackingStationEndpointTailOverride(
                r, 135.7, 0.0, r.OrbitSegments, false, ref seg, ref source, out bool future);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
            Assert.True(seg.HasValue);
            Assert.Equal(4_547_677.0, seg.Value.semiMajorAxis);
            Assert.False(future);
        }

        [UnityEntryFact]
        public void EndpointTail_LoopMemberCovering_KeepsTheSegment()
        {
            AcceptTailSeed();
            var r = EndpointRecording();
            OrbitSegment? seg = r.OrbitSegments[0];
            var source = GhostMapPresence.TrackingStationGhostSource.Segment;
            GhostMapPresence.ResolveTrackingStationEndpointTailOverride(
                r, 135.7, 2000.0, r.OrbitSegments, true, ref seg, ref source, out _);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
            Assert.Equal(120.0, seg.Value.startUT);
        }

        [UnityEntryFact]
        public void EndpointTail_LoopMemberNoCovering_OnlyWithTerminalSynthesis()
        {
            AcceptTailSeed();
            var r = EndpointRecording();
            OrbitSegment? seg = null;
            var source = GhostMapPresence.TrackingStationGhostSource.Segment;
            GhostMapPresence.ResolveTrackingStationEndpointTailOverride(
                r, 550.0, 2000.0, r.OrbitSegments, false, ref seg, ref source, out _);
            Assert.False(seg.HasValue);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);

            GhostMapPresence.ResolveTrackingStationEndpointTailOverride(
                r, 550.0, 2000.0, r.OrbitSegments, true, ref seg, ref source, out _);
            Assert.True(seg.HasValue);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.EndpointTail, source);
        }

        [UnityEntryFact]
        public void EndpointTail_NoCoveringWithAFutureSegment_NoFallback()
        {
            AcceptTailSeed();
            var r = EndpointRecording();
            OrbitSegment? seg = null;
            var source = GhostMapPresence.TrackingStationGhostSource.Segment;
            GhostMapPresence.ResolveTrackingStationEndpointTailOverride(
                r, 110.0, 0.0, r.OrbitSegments, false, ref seg, ref source, out bool future);
            Assert.True(future);
            Assert.False(seg.HasValue);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.Segment, source);
        }

        // ---------------------------------------------------------------- removals

        [UnityEntryFact]
        public void Removals_Null_DoesNothing()
        {
            TrackingStationRefreshCharacterizationTests.Ghost(0, 9400u, (1, 2));
            GhostMapPresence.ApplyTrackingStationGhostRemovals(null, 500.0);
            Assert.True(TrackingStationRefreshCharacterizationTests.GhostsByIndex.ContainsKey(0));
            Assert.Empty(logLines);
        }

        [UnityEntryFact]
        public void Removals_EachInOrder_TearsDownAndClearsTheStateVectorStores()
        {
            var owner = TrackingStationRefreshCharacterizationTests.PointsOnlyRecording("x0");
            TrackingStationRefreshCharacterizationTests.Ghost(3, 9403u, null, owner);
            TrackingStationRefreshCharacterizationTests.SvCachedIndices[3] = 1;
            TrackingStationRefreshCharacterizationTests.Ghost(1, 9401u, (1, 2));
            var toRemove = new List<(int idx, string reason)> { (3, "first-reason"), (1, "second-reason") };

            GhostMapPresence.ApplyTrackingStationGhostRemovals(toRemove, 500.0);

            Assert.Empty(TrackingStationRefreshCharacterizationTests.GhostsByIndex);
            Assert.Empty(TrackingStationRefreshCharacterizationTests.SvTrajectories);
            Assert.Empty(TrackingStationRefreshCharacterizationTests.SvCachedIndices);
            int first = logLines.FindIndex(l => l.Contains("Removed tracking-station ghost #3")
                && l.Contains("UT 500.0 reason=first-reason"));
            int second = logLines.FindIndex(l => l.Contains("Removed tracking-station ghost #1")
                && l.Contains("reason=second-reason"));
            Assert.True(first >= 0 && second > first);
        }
    }
}
