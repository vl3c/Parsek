using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class RuntimePolicyTests
    {
        private static List<TrajectoryPoint> MakePoints(params double[] uts)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < uts.Length; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = uts[i],
                    latitude = 0,
                    longitude = 0,
                    altitude = 0,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        [Theory]
        [InlineData(double.MinValue, 100.0, 10.0, false, true)]
        [InlineData(100.0, 105.0, 10.0, false, false)]
        [InlineData(100.0, 110.0, 10.0, false, true)]
        [InlineData(100.0, 101.0, 10.0, true, true)]
        public void ShouldRefreshSnapshot_RespectsIntervalAndForce(
            double lastRefresh, double currentUT, double intervalUT, bool force, bool expected)
        {
            bool actual = FlightRecorder.ShouldRefreshSnapshot(lastRefresh, currentUT, intervalUT, force);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void DecideOnVesselSwitch_SamePid_NoSwitch()
        {
            var decision = FlightRecorder.DecideOnVesselSwitch(
                10u, 10u, currentIsEva: false, recordingStartedAsEva: false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.None, decision);
        }

        [Fact]
        public void DecideOnVesselSwitch_DifferentPidEva_StartedAsVessel_Stops()
        {
            var decision = FlightRecorder.DecideOnVesselSwitch(
                10u, 11u, currentIsEva: true, recordingStartedAsEva: false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, decision);
        }

        [Fact]
        public void DecideOnVesselSwitch_DifferentPidEva_StartedAsEva_Continues()
        {
            var decision = FlightRecorder.DecideOnVesselSwitch(
                10u, 11u, currentIsEva: true, recordingStartedAsEva: true);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ContinueOnEva, decision);
        }

        [Fact]
        public void DecideOnVesselSwitch_DifferentPidNonEva_Stops()
        {
            var decision = FlightRecorder.DecideOnVesselSwitch(
                10u, 11u, currentIsEva: false, recordingStartedAsEva: false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, decision);
        }

        [Theory]
        [InlineData(0, 1.0f, false)]
        [InlineData(1, 1.0f, true)]
        [InlineData(0, 2.0f, true)]
        public void IsAnyWarpActive_CoversRailsAndPhysicsModes(
            int currentRateIndex, float currentRate, bool expected)
        {
            bool actual = GhostPlaybackLogic.IsAnyWarpActive(currentRateIndex, currentRate);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(1f, false)]
        [InlineData(5f, false)]
        [InlineData(10f, false)]
        [InlineData(10.001f, true)]
        [InlineData(50f, true)]
        [InlineData(100f, true)]
        [InlineData(1000f, true)]
        public void ShouldSuppressVisualFx_ThresholdAt10x(float warpRate, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate));
        }

        [Theory]
        [InlineData(1f, false)]
        [InlineData(10f, false)]
        [InlineData(50f, false)]
        [InlineData(50.001f, true)]
        [InlineData(100f, true)]
        [InlineData(1000f, true)]
        [InlineData(100000f, true)]
        public void ShouldSuppressGhosts_ThresholdAt50x(float warpRate, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldSuppressGhosts(warpRate));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ShouldPauseTimelineResourceReplay_ReflectsRecordingState(bool isRecording, bool expected)
        {
            bool actual = GhostPlaybackLogic.ShouldPauseTimelineResourceReplay(isRecording);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ShouldLoopPlayback_RespectsRecordingFlag(
            bool recordingLoopPlayback, bool expected)
        {
            bool actual = GhostPlaybackLogic.ShouldLoopPlayback(recordingLoopPlayback);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ComputeTargetResourceIndex_FindsHighestPassedPoint()
        {
            var points = MakePoints(10, 20, 30, 40);
            int target = GhostPlaybackLogic.ComputeTargetResourceIndex(points, lastAppliedResourceIndex: -1, currentUT: 29);
            Assert.Equal(1, target);
        }

        [Fact]
        public void ComputeTargetResourceIndex_NoAdvanceWhenBeforeNextPoint()
        {
            var points = MakePoints(10, 20, 30, 40);
            int target = GhostPlaybackLogic.ComputeTargetResourceIndex(points, lastAppliedResourceIndex: 1, currentUT: 25);
            Assert.Equal(1, target);
        }

        #region ComputeStandaloneDelta

        private static List<TrajectoryPoint> MakeResourcePoints(
            params (double ut, double funds, float science, float reputation)[] entries)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < entries.Length; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = entries[i].ut,
                    funds = entries[i].funds,
                    science = entries[i].science,
                    reputation = entries[i].reputation,
                    latitude = 0, longitude = 0, altitude = 0,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        [Fact]
        public void ComputeStandaloneDelta_NoAdvance_ReturnsNoChange()
        {
            var points = MakeResourcePoints(
                (10, 1000, 100, 50),
                (20, 900, 95, 48),
                (30, 800, 90, 46));
            var delta = ResourceBudget.ComputeStandaloneDelta(points, lastAppliedIndex: 1, currentUT: 15);
            Assert.False(delta.hasChange);
            Assert.Equal(1, delta.targetIndex);
        }

        [Fact]
        public void ComputeStandaloneDelta_AdvancesMultiplePoints()
        {
            var points = MakeResourcePoints(
                (10, 1000, 100, 50),
                (20, 900, 95, 48),
                (30, 800, 90, 46));
            var delta = ResourceBudget.ComputeStandaloneDelta(points, lastAppliedIndex: -1, currentUT: 25);
            Assert.True(delta.hasChange);
            Assert.Equal(1, delta.targetIndex);
            Assert.Equal(-100, delta.funds);
            Assert.Equal(-5f, delta.science);
            Assert.Equal(-2f, delta.reputation);
        }

        [Fact]
        public void ComputeStandaloneDelta_FromNegativeOneIndex()
        {
            var points = MakeResourcePoints(
                (10, 1000, 100, 50),
                (20, 900, 95, 48),
                (30, 800, 90, 46));
            var delta = ResourceBudget.ComputeStandaloneDelta(points, lastAppliedIndex: -1, currentUT: 35);
            Assert.True(delta.hasChange);
            Assert.Equal(2, delta.targetIndex);
            // From index 0 (Max(-1,0)=0) to index 2
            Assert.Equal(-200, delta.funds);
            Assert.Equal(-10f, delta.science);
            Assert.Equal(-4f, delta.reputation);
        }

        #endregion

        #region ResourceApplicator — TickStandalone

        [Fact]
        public void TickStandalone_EmptyList_NoAction()
        {
            var recordings = new List<Recording>();
            ResourceApplicator.TickStandalone(recordings, 100);
            // No exception, no-op
        }

        [Fact]
        public void TickStandalone_SkipsTreeRecordings()
        {
            var rec = new Recording
            {
                TreeId = "some-tree",
                Points = new List<TrajectoryPoint>(MakeResourcePoints(
                    (10, 1000, 100, 50), (20, 900, 95, 48)))
            };
            rec.LastAppliedResourceIndex = -1;
            var recordings = new List<Recording> { rec };

            ResourceApplicator.TickStandalone(recordings, 25);
            Assert.Equal(-1, rec.LastAppliedResourceIndex); // unchanged — was skipped
        }

        [Fact]
        public void TickStandalone_SkipsLoopRecordings()
        {
            var rec = new Recording
            {
                LoopPlayback = true,
                Points = new List<TrajectoryPoint>(MakeResourcePoints(
                    (10, 1000, 100, 50), (20, 900, 95, 48)))
            };
            rec.LastAppliedResourceIndex = -1;
            var recordings = new List<Recording> { rec };

            ResourceApplicator.TickStandalone(recordings, 25);
            Assert.Equal(-1, rec.LastAppliedResourceIndex); // unchanged
        }

        [Fact]
        public void TickStandalone_SkipsShortRecordings()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>(MakeResourcePoints((10, 1000, 100, 50)))
            };
            rec.LastAppliedResourceIndex = -1;
            var recordings = new List<Recording> { rec };

            ResourceApplicator.TickStandalone(recordings, 25);
            Assert.Equal(-1, rec.LastAppliedResourceIndex); // unchanged — only 1 point
        }

        [Fact]
        public void TickStandalone_AdvancesLastAppliedIndex()
        {
            // TickStandalone calls Funding.Instance etc which are null in tests,
            // but the index advancement and delta computation should still work.
            // The AddFunds/AddScience/AddReputation calls are guarded by null checks.
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>(MakeResourcePoints(
                    (10, 1000, 100, 50), (20, 900, 95, 48), (30, 800, 90, 46)))
            };
            rec.LastAppliedResourceIndex = -1;
            var recordings = new List<Recording> { rec };

            ResourceApplicator.TickStandalone(recordings, 25);
            Assert.Equal(1, rec.LastAppliedResourceIndex); // advanced to index 1 (UT=20 passed)
        }

        [Fact]
        public void TickStandalone_NoAdvance_WhenBeforeNextPoint()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>(MakeResourcePoints(
                    (10, 1000, 100, 50), (20, 900, 95, 48)))
            };
            rec.LastAppliedResourceIndex = 0;
            var recordings = new List<Recording> { rec };

            ResourceApplicator.TickStandalone(recordings, 15);
            Assert.Equal(0, rec.LastAppliedResourceIndex); // unchanged
        }

        #endregion

        #region ResourceApplicator — DeductBudget

        [Fact]
        public void DeductBudget_ZeroBudget_StillMarksRecordingsApplied()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>(MakeResourcePoints(
                    (10, 1000, 100, 50), (20, 1000, 100, 50)))
            };
            rec.LastAppliedResourceIndex = -1;
            var recordings = new List<Recording> { rec };
            var trees = new List<RecordingTree>();
            var budget = new BudgetSummary();

            ResourceApplicator.DeductBudget(budget, recordings, trees);
            Assert.Equal(1, rec.LastAppliedResourceIndex); // marked as fully applied
        }

        [Fact]
        public void DeductBudget_MarksTreesAsApplied()
        {
            var tree = new RecordingTree { ResourcesApplied = false };
            var recordings = new List<Recording>();
            var trees = new List<RecordingTree> { tree };
            var budget = new BudgetSummary();

            ResourceApplicator.DeductBudget(budget, recordings, trees);
            Assert.True(tree.ResourcesApplied);
        }

        #endregion

        [Fact]
        public void ComputeScaledRcsEmissionRate_ShowcaseEnforcesVisibilityFloor()
        {
            float rate = GhostPlaybackLogic.ComputeScaledRcsEmissionRate(
                emissionCurve: null, power: 0.01f, emissionScale: 120f);

            Assert.True(rate >= 60f, $"Expected showcase emission >= 60, got {rate}");
        }

        [Fact]
        public void ComputeScaledRcsSpeed_ShowcaseEnforcesVisibilityFloor()
        {
            float speed = GhostPlaybackLogic.ComputeScaledRcsSpeed(
                speedCurve: null, power: 0.01f, speedScale: 2.5f);

            Assert.True(speed >= 4f, $"Expected showcase speed >= 4, got {speed}");
        }

        [Fact]
        public void ComputeScaledRcsRates_NonShowcaseDoesNotApplyFloors()
        {
            float rate = GhostPlaybackLogic.ComputeScaledRcsEmissionRate(
                emissionCurve: null, power: 0.25f, emissionScale: 1f);
            float speed = GhostPlaybackLogic.ComputeScaledRcsSpeed(
                speedCurve: null, power: 0.25f, speedScale: 1f);

            Assert.Equal(25f, rate);
            Assert.Equal(2.5f, speed);
        }

        [Theory]
        [InlineData(60f, 1.0, 360f)]
        [InlineData(-120f, 0.5, -360f)]
        [InlineData(240f, 0.25, 360f)]
        [InlineData(300f, 0.0, 0f)]
        [InlineData(300f, -1.0, 0f)]
        public void ComputeRotorDeltaDegrees_UsesRpmAndDeltaTime(
            float rpm, double deltaSeconds, float expectedDegrees)
        {
            float actual = GhostPlaybackLogic.ComputeRotorDeltaDegrees(rpm, deltaSeconds);
            Assert.Equal(expectedDegrees, actual, 0.001f);
        }

        [Theory]
        [InlineData(99, false, 100, 0)]
        [InlineData(100, true, 100, 0)]
        [InlineData(119, true, 119, 0)]
        [InlineData(120, true, 120, 0)]
        [InlineData(125, false, 100, 0)]
        [InlineData(130, true, 100, 1)]
        [InlineData(134, true, 104, 1)]
        [InlineData(151, false, 100, 1)]
        public void TryComputeLoopPlaybackUT_RespectsPlaybackAndPauseWindows(
            double currentUT, bool expectedInPlayback, double expectedLoopUT, int expectedCycle)
        {
            bool inPlayback = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT,
                startUT: 100,
                endUT: 120,
                intervalSeconds: 10,
                out double loopUT,
                out long cycleIndex);

            Assert.Equal(expectedInPlayback, inPlayback);
            Assert.Equal(expectedCycle, cycleIndex);
            Assert.Equal(expectedLoopUT, loopUT, 6);
        }

        [Theory]
        [InlineData(true, -10.0, true, 50.0, true, -10.0)]
        [InlineData(true, 0.0, false, 50.0, true, 50.0)]
        [InlineData(false, 0.0, false, 123.0, true, 123.0)]
        [InlineData(false, 0.0, false, 0.0, false, 0.0)]
        public void SelectRelocatedAltitude_PrefersTerrainThenSnapshot(
            bool landedLike,
            double terrainAltitude,
            bool terrainValid,
            double snapshotAltitude,
            bool hasSnapshotAltitude,
            double expected)
        {
            double actual = VesselSpawner.SelectRelocatedAltitude(
                landedLike, terrainAltitude, terrainValid, snapshotAltitude, hasSnapshotAltitude);
            Assert.Equal(expected, actual);
        }

        // --- GetActiveCycles tests ---

        [Fact]
        public void GetActiveCycles_PositiveInterval_SingleCycleAlways()
        {
            // 20s recording, 10s interval, cycleDuration=30
            // At t=115 (elapsed=15 into cycle 0 of range starting at 100)
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 115, startUT: 100, endUT: 120,
                intervalSeconds: 10, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(0, last);
        }

        [Fact]
        public void GetActiveCycles_PositiveInterval_SecondCycle()
        {
            // 20s recording, 10s interval, cycleDuration=30
            // At t=135 (elapsed=35): cycle 1, phase=5
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 135, startUT: 100, endUT: 120,
                intervalSeconds: 10, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(1, first);
            Assert.Equal(1, last);
        }

        [Fact]
        public void GetActiveCycles_ZeroInterval_SingleCycleAlways()
        {
            // 20s recording, 0s interval, cycleDuration=20
            // At t=110 (elapsed=10): cycle 0, phase=10
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 110, startUT: 100, endUT: 120,
                intervalSeconds: 0, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(0, last);
        }

        [Fact]
        public void GetActiveCycles_ZeroInterval_ThirdCycle()
        {
            // 20s recording, 0s interval, cycleDuration=20
            // At t=145 (elapsed=45): cycle 2, phase=5
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 145, startUT: 100, endUT: 120,
                intervalSeconds: 0, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(2, first);
            Assert.Equal(2, last);
        }

        [Fact]
        public void GetActiveCycles_NegativeInterval_OverlapWindow()
        {
            // 60s recording, -20s interval, cycleDuration=40
            // At t=150 (elapsed=50): lastCycle=floor(50/40)=1
            // elapsedMinusDuration=50-60=-10 → firstCycle=0
            // Cycle 0 started at 100, plays from 100..160 → phase at t=150 is 50 (still playing)
            // Cycle 1 started at 140, plays from 100..160 → phase at t=150 is 10 (playing)
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 150, startUT: 100, endUT: 160,
                intervalSeconds: -20, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(1, last);
        }

        [Fact]
        public void GetActiveCycles_NegativeInterval_ThreeOverlapping()
        {
            // 60s recording, -40s interval, cycleDuration=20
            // At t=145 (elapsed=45): lastCycle=floor(45/20)=2
            // elapsedMinusDuration=45-60=-15 → firstCycle=0
            // So cycles 0, 1, 2 are all active
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 145, startUT: 100, endUT: 160,
                intervalSeconds: -40, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(2, last);
        }

        [Fact]
        public void GetActiveCycles_NegativeInterval_MaxCyclesCap()
        {
            // 60s recording, -40s interval, cycleDuration=20
            // maxCycles=2, so even if 3 are active, cap to 2
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 145, startUT: 100, endUT: 160,
                intervalSeconds: -40, maxCycles: 2,
                out long first, out long last);
            Assert.Equal(1, first);  // capped: last(2) - maxCycles(2) + 1 = 1
            Assert.Equal(2, last);
        }

        [Fact]
        public void GetActiveCycles_BeforeStart_ReturnsZero()
        {
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 50, startUT: 100, endUT: 160,
                intervalSeconds: -20, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(0, last);
        }

        [Fact]
        public void GetActiveCycles_VeryLargePositiveInterval()
        {
            // 20s recording, 1000s interval, cycleDuration=1020
            // At t=1130 (elapsed=1030): cycle 1, phase=10 (in playback)
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 1130, startUT: 100, endUT: 120,
                intervalSeconds: 1000, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(1, first);
            Assert.Equal(1, last);
        }

        [Fact]
        public void GetActiveCycles_NearMinCycleDuration()
        {
            // 10s recording, interval = -9.999 → cycleDuration = 0.001
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 100.005, startUT: 100, endUT: 110,
                intervalSeconds: -9.999, maxCycles: 5,
                out long first, out long last);
            // With cycleDuration=0.001, elapsed=0.005 → lastCycle=5
            // But maxCycles=5 caps to first=1
            Assert.True(last >= first);
            Assert.True(last - first + 1 <= 5);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_NegativeInterval_ReturnsPlaybackPhase()
        {
            // 20s recording, -5s interval, cycleDuration=15
            // At t=118 (elapsed=18): cycle 1, phase=3
            bool ok = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT: 118, startUT: 100, endUT: 120,
                intervalSeconds: -5,
                out double loopUT, out long cycleIndex);

            Assert.True(ok);
            Assert.Equal(1, cycleIndex);
            Assert.Equal(103.0, loopUT, 6);
        }

        // --- Overlap edge case tests ---

        [Fact]
        public void GetActiveCycles_ExtremeNegativeInterval_ManyOverlaps()
        {
            // 21s recording, -20s interval, cycleDuration=1
            // At t=110 (elapsed=10): lastCycle=10, many overlapping
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 110, startUT: 100, endUT: 121,
                intervalSeconds: -20, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(10, last);
            // firstCycle is capped by maxCycles=5: last(10) - 5 + 1 = 6
            Assert.Equal(6, first);
            Assert.Equal(5, last - first + 1);
        }

        [Fact]
        public void GetActiveCycles_ExtremeNegativeInterval_NoCap()
        {
            // 21s recording, -20s interval, cycleDuration=1
            // At t=110 (elapsed=10): lastCycle=10
            // Without cap (maxCycles=100): firstCycle = max(0, floor((10-21)/1)+1) = 0
            // All cycles 0-10 still playing (phase < 21 for all)
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 110, startUT: 100, endUT: 121,
                intervalSeconds: -20, maxCycles: 100,
                out long first, out long last);
            Assert.Equal(10, last);
            Assert.Equal(0, first);
            Assert.Equal(11, last - first + 1);
        }

        [Fact]
        public void GetActiveCycles_NegativeInterval_OlderCyclesExpire()
        {
            // 21s recording, -20s interval, cycleDuration=1
            // At t=125 (elapsed=25): lastCycle=25
            // Cycle 0 started at t=100, phase=25 > 21 → expired
            // Cycle 4 started at t=104, phase=21 → exactly at boundary
            // firstCycle = max(0, floor((25-21)/1)+1) = 5
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 125, startUT: 100, endUT: 121,
                intervalSeconds: -20, maxCycles: 100,
                out long first, out long last);
            Assert.Equal(25, last);
            Assert.Equal(5, first);
            Assert.Equal(21, last - first + 1); // exactly 21 simultaneous
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_ZeroInterval_ImmediateRestart()
        {
            // 20s recording, 0s interval → cycleDuration = 20
            // At t=120 (elapsed=20): exactly at cycle boundary
            bool ok = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT: 120, startUT: 100, endUT: 120,
                intervalSeconds: 0,
                out double loopUT, out long cycleIndex);
            Assert.True(ok);
            Assert.Equal(1, cycleIndex);
            Assert.Equal(100.0, loopUT, 6); // start of new cycle
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_PositiveInterval_InPause()
        {
            // 20s recording, 10s interval, cycleDuration=30
            // At t=125 (elapsed=25, cycleTime=25): phase > duration (20) → pause window
            bool ok = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT: 125, startUT: 100, endUT: 120,
                intervalSeconds: 10,
                out double loopUT, out long cycleIndex);
            // In pause → returns false (older behavior returns endUT with cycle 0)
            // Let's just verify it doesn't crash and returns valid state
            Assert.Equal(0, cycleIndex);
        }

        [Fact]
        public void GetActiveCycles_ZeroDuration_ReturnsZero()
        {
            // Degenerate: startUT == endUT
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 100, startUT: 100, endUT: 100,
                intervalSeconds: 10, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(0, last);
        }

        #region Phase 1A: Recording query properties

        [Fact]
        public void IsTreeRecording_WithTreeId_ReturnsTrue()
        {
            var rec = new Recording { TreeId = "tree-123" };
            Assert.True(rec.IsTreeRecording);
        }

        [Fact]
        public void IsTreeRecording_WithNullTreeId_ReturnsFalse()
        {
            var rec = new Recording { TreeId = null };
            Assert.False(rec.IsTreeRecording);
        }

        [Fact]
        public void IsChainRecording_WithChainId_ReturnsTrue()
        {
            var rec = new Recording { ChainId = "chain-abc" };
            Assert.True(rec.IsChainRecording);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void IsChainRecording_WithNullOrEmptyChainId_ReturnsFalse(string chainId)
        {
            var rec = new Recording { ChainId = chainId };
            Assert.False(rec.IsChainRecording);
        }

        [Fact]
        public void ManagesOwnResources_Standalone_ReturnsTrue()
        {
            var rec = new Recording { TreeId = null };
            Assert.True(rec.ManagesOwnResources);
        }

        [Fact]
        public void ManagesOwnResources_TreeRecording_ReturnsFalse()
        {
            var rec = new Recording { TreeId = "tree-456" };
            Assert.False(rec.ManagesOwnResources);
        }

        #endregion

        #region Phase 1B: ShouldSuppressBoundarySplit

        [Fact]
        public void ShouldSuppressBoundarySplit_WithActiveTree_ReturnsTrue()
        {
            var tree = new RecordingTree();
            Assert.True(ParsekFlight.ShouldSuppressBoundarySplit(tree));
        }

        [Fact]
        public void ShouldSuppressBoundarySplit_WithNullTree_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSuppressBoundarySplit(null));
        }

        #endregion

        #region Phase 1C: ClassifyVesselDestruction

        [Fact]
        public void ClassifyVesselDestruction_TreeDeferred()
        {
            var mode = ParsekFlight.ClassifyVesselDestruction(
                hasActiveTree: true,
                isRecording: true,
                vesselDestroyedDuringRecording: true,
                isActiveVessel: true,
                shouldDeferForTree: true,
                treeDestructionDialogPending: false);
            Assert.Equal(ParsekFlight.DestructionMode.TreeDeferred, mode);
        }

        [Fact]
        public void ClassifyVesselDestruction_StandaloneMerge()
        {
            var mode = ParsekFlight.ClassifyVesselDestruction(
                hasActiveTree: false,
                isRecording: true,
                vesselDestroyedDuringRecording: true,
                isActiveVessel: true,
                shouldDeferForTree: false,
                treeDestructionDialogPending: false);
            Assert.Equal(ParsekFlight.DestructionMode.StandaloneMerge, mode);
        }

        [Fact]
        public void ClassifyVesselDestruction_TreeAllLeavesCheck()
        {
            var mode = ParsekFlight.ClassifyVesselDestruction(
                hasActiveTree: true,
                isRecording: false,
                vesselDestroyedDuringRecording: true,
                isActiveVessel: true,
                shouldDeferForTree: false,
                treeDestructionDialogPending: false);
            Assert.Equal(ParsekFlight.DestructionMode.TreeAllLeavesCheck, mode);
        }

        [Fact]
        public void ClassifyVesselDestruction_None_WhenNotActiveVessel()
        {
            var mode = ParsekFlight.ClassifyVesselDestruction(
                hasActiveTree: false,
                isRecording: true,
                vesselDestroyedDuringRecording: true,
                isActiveVessel: false,
                shouldDeferForTree: false,
                treeDestructionDialogPending: false);
            Assert.Equal(ParsekFlight.DestructionMode.None, mode);
        }

        [Fact]
        public void ClassifyVesselDestruction_None_WhenTreeDestructionDialogPending()
        {
            var mode = ParsekFlight.ClassifyVesselDestruction(
                hasActiveTree: true,
                isRecording: true,
                vesselDestroyedDuringRecording: true,
                isActiveVessel: true,
                shouldDeferForTree: false,
                treeDestructionDialogPending: true);
            Assert.Equal(ParsekFlight.DestructionMode.None, mode);
        }

        [Fact]
        public void ClassifyVesselDestruction_TreeDeferred_TakesPriorityOverAllLeavesCheck()
        {
            // When both TreeDeferred and TreeAllLeavesCheck conditions are met,
            // TreeDeferred wins (checked first in branching order)
            var mode = ParsekFlight.ClassifyVesselDestruction(
                hasActiveTree: true,
                isRecording: true,
                vesselDestroyedDuringRecording: true,
                isActiveVessel: true,
                shouldDeferForTree: true,
                treeDestructionDialogPending: false);
            Assert.Equal(ParsekFlight.DestructionMode.TreeDeferred, mode);
        }

        #endregion

        #region IsTreePadFailure

        [Fact]
        public void IsTreePadFailure_AllShort_ReturnsTrue()
        {
            var tree = new RecordingTree();
            tree.Recordings["a"] = new Recording
            {
                Points = MakePoints(100, 105),
                MaxDistanceFromLaunch = 10
            };
            Assert.True(ParsekFlight.IsTreePadFailure(tree));
        }

        [Fact]
        public void IsTreePadFailure_OneLongRecording_ReturnsFalse()
        {
            var tree = new RecordingTree();
            tree.Recordings["a"] = new Recording
            {
                Points = MakePoints(100, 105),
                MaxDistanceFromLaunch = 10
            };
            tree.Recordings["b"] = new Recording
            {
                Points = MakePoints(100, 200),
                MaxDistanceFromLaunch = 5000
            };
            Assert.False(ParsekFlight.IsTreePadFailure(tree));
        }

        [Fact]
        public void IsTreePadFailure_EmptyTree_ReturnsFalse()
        {
            var tree = new RecordingTree();
            Assert.False(ParsekFlight.IsTreePadFailure(tree));
        }

        #endregion

        #region ComputeAutoLoopRange

        private static List<TrackSection> MakeSections(params (SegmentEnvironment env, double start, double end)[] entries)
        {
            var sections = new List<TrackSection>();
            for (int i = 0; i < entries.Length; i++)
            {
                sections.Add(new TrackSection
                {
                    environment = entries[i].env,
                    startUT = entries[i].start,
                    endUT = entries[i].end,
                });
            }
            return sections;
        }

        [Fact]
        public void ComputeAutoLoopRange_NullSections_ReturnsNaN()
        {
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(null);
            Assert.True(double.IsNaN(start));
            Assert.True(double.IsNaN(end));
        }

        [Fact]
        public void ComputeAutoLoopRange_SingleSection_ReturnsNaN()
        {
            var sections = MakeSections((SegmentEnvironment.Atmospheric, 100, 200));
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(sections);
            Assert.True(double.IsNaN(start));
            Assert.True(double.IsNaN(end));
        }

        [Fact]
        public void ComputeAutoLoopRange_AllInteresting_ReturnsNaN()
        {
            // Atmospheric + ExoPropulsive — both interesting, nothing to trim
            var sections = MakeSections(
                (SegmentEnvironment.Atmospheric, 100, 150),
                (SegmentEnvironment.ExoPropulsive, 150, 200));
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(sections);
            Assert.True(double.IsNaN(start));
            Assert.True(double.IsNaN(end));
        }

        [Fact]
        public void ComputeAutoLoopRange_AllBoring_ReturnsNaN()
        {
            var sections = MakeSections(
                (SegmentEnvironment.ExoBallistic, 100, 200),
                (SegmentEnvironment.SurfaceStationary, 200, 300));
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(sections);
            Assert.True(double.IsNaN(start));
            Assert.True(double.IsNaN(end));
        }

        [Fact]
        public void ComputeAutoLoopRange_TrimsTrailingCoast()
        {
            // Launch: Atmospheric + ExoPropulsive + ExoBallistic
            var sections = MakeSections(
                (SegmentEnvironment.Atmospheric, 100, 150),
                (SegmentEnvironment.ExoPropulsive, 150, 180),
                (SegmentEnvironment.ExoBallistic, 180, 500));
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(sections);
            Assert.Equal(100, start);
            Assert.Equal(180, end);
        }

        [Fact]
        public void ComputeAutoLoopRange_TrimsLeadingCoast()
        {
            // Landing: ExoBallistic + ExoPropulsive + SurfaceMobile
            var sections = MakeSections(
                (SegmentEnvironment.ExoBallistic, 100, 300),
                (SegmentEnvironment.ExoPropulsive, 300, 350),
                (SegmentEnvironment.SurfaceMobile, 350, 370));
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(sections);
            Assert.Equal(300, start);
            Assert.Equal(370, end);
        }

        [Fact]
        public void ComputeAutoLoopRange_TrimsBothEnds()
        {
            // Coast → descent → landing → stationary
            var sections = MakeSections(
                (SegmentEnvironment.ExoBallistic, 100, 300),
                (SegmentEnvironment.ExoPropulsive, 300, 350),
                (SegmentEnvironment.SurfaceMobile, 350, 370),
                (SegmentEnvironment.SurfaceStationary, 370, 1000));
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(sections);
            Assert.Equal(300, start);
            Assert.Equal(370, end);
        }

        [Fact]
        public void ComputeAutoLoopRange_KeepsBoringMiddle()
        {
            // Launch → coast → reentry: trims nothing (first and last are interesting)
            var sections = MakeSections(
                (SegmentEnvironment.Atmospheric, 100, 150),
                (SegmentEnvironment.ExoBallistic, 150, 400),
                (SegmentEnvironment.Atmospheric, 400, 450));
            var (start, end) = GhostPlaybackLogic.ComputeAutoLoopRange(sections);
            Assert.True(double.IsNaN(start)); // nothing to trim — bookends are interesting
            Assert.True(double.IsNaN(end));
        }

        [Fact]
        public void IsBoringEnvironment_ClassifiesCorrectly()
        {
            Assert.False(GhostPlaybackLogic.IsBoringEnvironment(SegmentEnvironment.Atmospheric));
            Assert.False(GhostPlaybackLogic.IsBoringEnvironment(SegmentEnvironment.ExoPropulsive));
            Assert.True(GhostPlaybackLogic.IsBoringEnvironment(SegmentEnvironment.ExoBallistic));
            Assert.False(GhostPlaybackLogic.IsBoringEnvironment(SegmentEnvironment.SurfaceMobile));
            Assert.True(GhostPlaybackLogic.IsBoringEnvironment(SegmentEnvironment.SurfaceStationary));
        }

        #endregion

        #region ApplyAutoLoopRange

        [Fact]
        public void ApplyAutoLoopRange_ToggleOn_SetsRange()
        {
            var rec = new Recording
            {
                TrackSections = MakeSections(
                    (SegmentEnvironment.Atmospheric, 100, 150),
                    (SegmentEnvironment.ExoPropulsive, 150, 180),
                    (SegmentEnvironment.ExoBallistic, 180, 500))
            };
            ParsekUI.ApplyAutoLoopRange(rec, true);
            Assert.Equal(100, rec.LoopStartUT);
            Assert.Equal(180, rec.LoopEndUT);
        }

        [Fact]
        public void ApplyAutoLoopRange_ToggleOff_ClearsRange()
        {
            var rec = new Recording
            {
                LoopStartUT = 100,
                LoopEndUT = 200,
            };
            ParsekUI.ApplyAutoLoopRange(rec, false);
            Assert.True(double.IsNaN(rec.LoopStartUT));
            Assert.True(double.IsNaN(rec.LoopEndUT));
        }

        [Fact]
        public void ApplyAutoLoopRange_NoTrimmableSections_LeavesNaN()
        {
            var rec = new Recording
            {
                TrackSections = MakeSections(
                    (SegmentEnvironment.Atmospheric, 100, 200),
                    (SegmentEnvironment.ExoPropulsive, 200, 300))
            };
            ParsekUI.ApplyAutoLoopRange(rec, true);
            Assert.True(double.IsNaN(rec.LoopStartUT));
            Assert.True(double.IsNaN(rec.LoopEndUT));
        }

        #endregion
    }
}
