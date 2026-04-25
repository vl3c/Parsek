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
        public void DecideOnVesselSwitch_DifferentPidEva_StartedAsVessel_TransitionsToBackground()
        {
            var decision = FlightRecorder.DecideOnVesselSwitch(
                10u, 11u, currentIsEva: true, recordingStartedAsEva: false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, decision);
        }

        [Fact]
        public void DecideOnVesselSwitch_DifferentPidEva_StartedAsEva_Continues()
        {
            var decision = FlightRecorder.DecideOnVesselSwitch(
                10u, 11u, currentIsEva: true, recordingStartedAsEva: true);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ContinueOnEva, decision);
        }

        [Fact]
        public void DecideOnVesselSwitch_DifferentPidNonEva_TransitionsToBackground()
        {
            var decision = FlightRecorder.DecideOnVesselSwitch(
                10u, 11u, currentIsEva: false, recordingStartedAsEva: false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, decision);
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

        // Phase F: ComputeStandaloneDelta region removed alongside the standalone
        // resource applier (ApplyResourceDeltas in ParsekFlight). The ledger drives
        // funds/science/reputation now; per-recording delta replay is gone.

        [Fact]
        public void ComputePendingWatchHoldSeconds_ExtendsHoldForFutureContinuationAt1x()
        {
            float holdSeconds = GhostPlaybackLogic.ComputePendingWatchHoldSeconds(
                3f, currentUT: 53.68, continuationActivationUT: 83.04, warpRate: 1f);

            Assert.InRange(holdSeconds, 31f, 33f);
        }

        [Fact]
        public void ComputePendingWatchHoldSeconds_UsesWarpRateForEstimatedRealTime()
        {
            float holdSeconds = GhostPlaybackLogic.ComputePendingWatchHoldSeconds(
                3f, currentUT: 53.68, continuationActivationUT: 83.04, warpRate: 5f);

            Assert.InRange(holdSeconds, 8f, 9f);
        }

        [Fact]
        public void ComputePendingWatchHoldSeconds_NoFutureGap_LeavesBaseHold()
        {
            float holdSeconds = GhostPlaybackLogic.ComputePendingWatchHoldSeconds(
                3f, currentUT: 83.04, continuationActivationUT: 83.04, warpRate: 1f);

            Assert.Equal(3f, holdSeconds);
        }

        [Fact]
        public void ComputePendingWatchHoldWindow_PendingContinuationUsesSharedRealtimeBase()
        {
            GhostPlaybackLogic.ComputePendingWatchHoldWindow(
                3f,
                currentRealtime: 10f,
                currentUT: 53.68,
                continuationActivationUT: 83.04,
                warpRate: 5f,
                out float holdUntilRealTime,
                out float holdMaxRealTime);

            Assert.Equal(18f, holdUntilRealTime);
            Assert.Equal(55f, holdMaxRealTime);
        }

        [Fact]
        public void ComputePendingWatchHoldWindow_PendingContinuationCapsInitialDeadlineAtMax()
        {
            GhostPlaybackLogic.ComputePendingWatchHoldWindow(
                3f,
                currentRealtime: 10f,
                currentUT: 53.68,
                continuationActivationUT: 200.0,
                warpRate: 0.1f,
                out float holdUntilRealTime,
                out float holdMaxRealTime);

            Assert.Equal(55f, holdUntilRealTime);
            Assert.Equal(55f, holdMaxRealTime);
        }

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

        // #381: duration=20, period=30 → cycleDuration=30, pause tail 10s (period > duration).
        // This is the classic single-ghost "loop with gap" shape.
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
                intervalSeconds: 30,
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

        // --- GetActiveCycles tests (#381 launch-to-launch semantics) ---
        // cycleDuration = max(intervalSeconds, MinCycleDuration=5 since #443).

        [Fact]
        public void GetActiveCycles_PeriodGreaterThanDuration_SingleCycle()
        {
            // 20s recording, period=30 (greater than duration), cycleDuration=30
            // At t=115 (elapsed=15 into cycle 0)
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 115, startUT: 100, endUT: 120,
                intervalSeconds: 30, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(0, last);
        }

        [Fact]
        public void GetActiveCycles_PeriodGreaterThanDuration_SecondCycle()
        {
            // 20s recording, period=30, cycleDuration=30
            // At t=135 (elapsed=35): cycle 1, phase=5
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 135, startUT: 100, endUT: 120,
                intervalSeconds: 30, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(1, first);
            Assert.Equal(1, last);
        }

        [Fact]
        public void GetActiveCycles_PeriodEqualsDuration_BackToBack()
        {
            // 20s recording, period=20 (equal to duration), cycleDuration=20.
            // At t=110 (elapsed=10): lastCycle=0, firstCycle=0 (no overlap yet).
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 110, startUT: 100, endUT: 120,
                intervalSeconds: 20, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(0, last);
        }

        [Fact]
        public void GetActiveCycles_PeriodEqualsDuration_SecondCycle()
        {
            // 20s recording, period=20, cycleDuration=20.
            // At t=130 (elapsed=30): lastCycle=1 (floor(30/20)), phase=10.
            // elapsedMinusDuration=10, firstCycle=floor(10/20)+1=1.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 130, startUT: 100, endUT: 120,
                intervalSeconds: 20, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(1, first);
            Assert.Equal(1, last);
        }

        [Fact]
        public void GetActiveCycles_PeriodShorterThanDuration_TwoOverlapping()
        {
            // #381: 60s recording, period=40 (shorter than duration) → cycleDuration=40.
            // At t=150 (elapsed=50): lastCycle=floor(50/40)=1.
            // elapsedMinusDuration = 50-60 = -10 → firstCycle=0.
            // Cycles 0 (started 100, phase=50) and 1 (started 140, phase=10) both playing.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 150, startUT: 100, endUT: 160,
                intervalSeconds: 40, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(1, last);
        }

        [Fact]
        public void GetActiveCycles_PeriodShorterThanDuration_ThreeOverlapping()
        {
            // #381: 60s recording, period=20 → cycleDuration=20.
            // At t=145 (elapsed=45): lastCycle=floor(45/20)=2.
            // elapsedMinusDuration=45-60=-15 → firstCycle=0.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 145, startUT: 100, endUT: 160,
                intervalSeconds: 20, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(2, last);
        }

        [Fact]
        public void GetActiveCycles_PeriodShorterThanDuration_MaxCyclesCap()
        {
            // #381: 60s recording, period=20 → cycleDuration=20. maxCycles=2 caps.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 145, startUT: 100, endUT: 160,
                intervalSeconds: 20, maxCycles: 2,
                out long first, out long last);
            Assert.Equal(1, first);  // capped: last(2) - maxCycles(2) + 1 = 1
            Assert.Equal(2, last);
        }

        [Fact]
        public void GetActiveCycles_BeforeStart_ReturnsZero()
        {
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 50, startUT: 100, endUT: 160,
                intervalSeconds: 20, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(0, first);
            Assert.Equal(0, last);
        }

        [Fact]
        public void GetActiveCycles_VeryLargePeriod()
        {
            // 20s recording, period=1000 → cycleDuration=1000. Pause tail 980s.
            // At t=1110 (elapsed=1010): lastCycle=1, phase=10 (in playback).
            // elapsedMinusDuration=990, firstCycle=floor(990/1000)+1=1.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 1110, startUT: 100, endUT: 120,
                intervalSeconds: 1000, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(1, first);
            Assert.Equal(1, last);
        }

        [Fact]
        public void GetActiveCycles_BelowMinCycleDuration_ClampsToFive()
        {
            // #443: 10s recording, period=0.001 → cycleDuration clamps to MinCycleDuration=5.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 100.005, startUT: 100, endUT: 110,
                intervalSeconds: 0.001, maxCycles: 5,
                out long first, out long last);
            // cycleDuration=5, elapsed=0.005 → lastCycle=0, firstCycle=0.
            Assert.Equal(0, last);
            Assert.Equal(0, first);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_PeriodShorterThanDuration_Overlap()
        {
            // #381: duration=20, period=5 → cycleDuration=5. At t=118, elapsed=18.
            // cycle=floor(18/5)=3, phase=3. No pause (period <= duration). loopUT=103.
            bool ok = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT: 118, startUT: 100, endUT: 120,
                intervalSeconds: 5,
                out double loopUT, out long cycleIndex);

            Assert.True(ok);
            Assert.Equal(3, cycleIndex);
            Assert.Equal(103.0, loopUT, 6);
        }

        // --- Overlap edge case tests (#381) ---

        [Fact]
        public void GetActiveCycles_ShortPeriod_ManyOverlaps_Capped()
        {
            // #443: 105s recording, period=5 (= MinCycleDuration) → cycleDuration=5.
            // At t=150 (elapsed=50): lastCycle=floor(50/5)=10.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 150, startUT: 100, endUT: 205,
                intervalSeconds: 5, maxCycles: 5,
                out long first, out long last);
            Assert.Equal(10, last);
            // cap: last(10) - 5 + 1 = 6
            Assert.Equal(6, first);
            Assert.Equal(5, last - first + 1);
        }

        [Fact]
        public void GetActiveCycles_ShortPeriod_NoCap()
        {
            // #443: 105s recording, period=5 → cycleDuration=5.
            // At t=150 (elapsed=50): lastCycle=10. elapsedMinusDuration=-55 → firstCycle=0.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 150, startUT: 100, endUT: 205,
                intervalSeconds: 5, maxCycles: 100,
                out long first, out long last);
            Assert.Equal(10, last);
            Assert.Equal(0, first);
            Assert.Equal(11, last - first + 1);
        }

        [Fact]
        public void GetActiveCycles_ShortPeriod_OlderCyclesExpire()
        {
            // #443: 105s recording, period=5 → cycleDuration=5.
            // At t=225 (elapsed=125): lastCycle=floor(125/5)=25.
            // elapsedMinusDuration = 125-105 = 20 → firstCycle = floor(20/5)+1 = 5.
            GhostPlaybackLogic.GetActiveCycles(
                currentUT: 225, startUT: 100, endUT: 205,
                intervalSeconds: 5, maxCycles: 100,
                out long first, out long last);
            Assert.Equal(25, last);
            Assert.Equal(5, first);
            Assert.Equal(21, last - first + 1); // exactly 21 simultaneous
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_ZeroInterval_ClampsToMinAndRestarts()
        {
            // #443: period=0 clamps to MinCycleDuration=5. duration=20.
            // At t=120 elapsed=20, cycle=floor(20/5)=4 phase=0.
            bool ok = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT: 120, startUT: 100, endUT: 120,
                intervalSeconds: 0,
                out double loopUT, out long cycleIndex);
            Assert.True(ok);
            Assert.Equal(4, cycleIndex);
            Assert.Equal(100.0, loopUT, 6); // start of new cycle
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_PeriodGreaterThanDuration_InPause()
        {
            // duration=20, period=30 → cycleDuration=30, pause tail 10s.
            // At t=125 (elapsed=25, cycleTime=25): phase > duration (20) → pause → return false.
            bool ok = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT: 125, startUT: 100, endUT: 120,
                intervalSeconds: 30,
                out double loopUT, out long cycleIndex);
            Assert.False(ok);
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

        // Phase F: ManagesOwnResources removed alongside the standalone resource
        // applier and tree lump-sum applier. The ledger is now the single source of
        // truth for funds/science/reputation; ResourceBudget sums every recording
        // uniformly, so the standalone-vs-tree gate no longer exists.

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
        public void ClassifyVesselDestruction_NoTree_ReturnsNone()
        {
            // With always-tree mode, hasActiveTree=false never happens during recording.
            // StandaloneMerge was removed — this input now returns None.
            var mode = ParsekFlight.ClassifyVesselDestruction(
                hasActiveTree: false,
                isRecording: true,
                vesselDestroyedDuringRecording: true,
                isActiveVessel: true,
                shouldDeferForTree: false,
                treeDestructionDialogPending: false);
            Assert.Equal(ParsekFlight.DestructionMode.None, mode);
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

        #region ClassifyPostDestructionMergeResolution

        [Fact]
        public void HasPendingPostDestructionCrashResolution_ReturnsTrue_ForPendingSplit()
        {
            bool result = ParsekFlight.HasPendingPostDestructionCrashResolution(
                activeDestroyed: true,
                pendingSplitInProgress: true,
                hasPendingBreakup: false);

            Assert.True(result);
        }

        [Fact]
        public void HasPendingPostDestructionCrashResolution_ReturnsTrue_ForPendingBreakup()
        {
            bool result = ParsekFlight.HasPendingPostDestructionCrashResolution(
                activeDestroyed: true,
                pendingSplitInProgress: false,
                hasPendingBreakup: true);

            Assert.True(result);
        }

        [Fact]
        public void HasPendingPostDestructionCrashResolution_ReturnsFalse_WhenActiveNotDestroyed()
        {
            bool result = ParsekFlight.HasPendingPostDestructionCrashResolution(
                activeDestroyed: false,
                pendingSplitInProgress: true,
                hasPendingBreakup: true);

            Assert.False(result);
        }

        [Fact]
        public void ClassifyPostDestructionMergeResolution_FinalizeWhenAllLeavesTerminal()
        {
            var result = ParsekFlight.ClassifyPostDestructionMergeResolution(
                activeDestroyed: true,
                allLeavesTerminal: true,
                onlyDebrisBlockersRemain: false,
                pendingCrashResolution: false);

            Assert.Equal(ParsekFlight.PostDestructionMergeResolution.FinalizeNow, result);
        }

        [Fact]
        public void ClassifyPostDestructionMergeResolution_FinalizesWhenOnlyDebrisBlockersRemain()
        {
            var result = ParsekFlight.ClassifyPostDestructionMergeResolution(
                activeDestroyed: true,
                allLeavesTerminal: false,
                onlyDebrisBlockersRemain: true,
                pendingCrashResolution: false);

            Assert.Equal(ParsekFlight.PostDestructionMergeResolution.FinalizeNow, result);
        }

        [Fact]
        public void ClassifyPostDestructionMergeResolution_WaitsWhilePendingCrashResolutionFinishes()
        {
            var result = ParsekFlight.ClassifyPostDestructionMergeResolution(
                activeDestroyed: true,
                allLeavesTerminal: false,
                onlyDebrisBlockersRemain: true,
                pendingCrashResolution: true);

            Assert.Equal(ParsekFlight.PostDestructionMergeResolution.WaitForPendingCrashResolution, result);
        }

        [Fact]
        public void ClassifyPostDestructionMergeResolution_PendingCrashResolutionWinsOverTerminalLeaves()
        {
            var result = ParsekFlight.ClassifyPostDestructionMergeResolution(
                activeDestroyed: true,
                allLeavesTerminal: true,
                onlyDebrisBlockersRemain: false,
                pendingCrashResolution: true);

            Assert.Equal(ParsekFlight.PostDestructionMergeResolution.WaitForPendingCrashResolution, result);
        }

        [Fact]
        public void ClassifyPostDestructionMergeResolution_CancelsWhenRealSurvivorStillExists()
        {
            var result = ParsekFlight.ClassifyPostDestructionMergeResolution(
                activeDestroyed: true,
                allLeavesTerminal: false,
                onlyDebrisBlockersRemain: false,
                pendingCrashResolution: false);

            Assert.Equal(ParsekFlight.PostDestructionMergeResolution.AbortAndKeepRecording, result);
        }

        [Fact]
        public void ClassifyPostDestructionMergeResolution_CancelsWhenNotCrashAndNotTerminal()
        {
            var result = ParsekFlight.ClassifyPostDestructionMergeResolution(
                activeDestroyed: false,
                allLeavesTerminal: false,
                onlyDebrisBlockersRemain: true,
                pendingCrashResolution: false);

            Assert.Equal(ParsekFlight.PostDestructionMergeResolution.AbortAndKeepRecording, result);
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

        #region State-vector orbit thresholds

        [Theory]
        [InlineData(2000, 100, 0, true)]       // Airless body, above thresholds
        [InlineData(1500.1, 60.1, 0, true)]    // Airless, just above thresholds
        [InlineData(1500, 60, 0, false)]       // Airless, at thresholds (not above)
        [InlineData(1000, 100, 0, false)]      // Airless, below altitude threshold
        [InlineData(2000, 50, 0, false)]       // Airless, below speed threshold
        [InlineData(0, 0, 0, false)]           // On the ground
        [InlineData(80000, 2000, 70000, true)] // Kerbin, above atmosphere (70km)
        [InlineData(60000, 2000, 70000, false)]// Kerbin, IN atmosphere — rejected
        [InlineData(71000, 60.1, 70000, true)] // Kerbin, just above atmosphere
        public void ShouldCreateStateVectorOrbit_ThresholdBehavior(double alt, double speed, double atmos, bool expected)
        {
            Assert.Equal(expected, ParsekPlaybackPolicy.ShouldCreateStateVectorOrbit(alt, speed, atmos));
        }

        [Theory]
        [InlineData(100, 10, 0, true)]         // Airless, below both thresholds
        [InlineData(499, 100, 0, true)]        // Airless, below altitude (OR logic)
        [InlineData(2000, 29, 0, true)]        // Airless, below speed (OR logic)
        [InlineData(500, 30, 0, false)]        // Airless, at thresholds (not below)
        [InlineData(1000, 60, 0, false)]       // Airless, above both removal thresholds
        [InlineData(60000, 2000, 70000, true)] // Kerbin, IN atmosphere — immediate remove
        [InlineData(80000, 2000, 70000, false)]// Kerbin, above atmosphere — keep
        public void ShouldRemoveStateVectorOrbit_ThresholdBehavior(double alt, double speed, double atmos, bool expected)
        {
            Assert.Equal(expected, ParsekPlaybackPolicy.ShouldRemoveStateVectorOrbit(alt, speed, atmos));
        }

        [Fact]
        public void StateVectorThresholds_HysteresisGap_AirlessBody()
        {
            // Airless body: vessel at 1000m and 50 m/s — in hysteresis dead zone
            Assert.False(ParsekPlaybackPolicy.ShouldCreateStateVectorOrbit(1000, 50, 0));
            Assert.False(ParsekPlaybackPolicy.ShouldRemoveStateVectorOrbit(1000, 50, 0));
        }

        [Fact]
        public void StateVectorThresholds_AtmosphereOverridesAltitude()
        {
            // At 50km on Kerbin (atmos=70km): high speed, but IN atmosphere → no create
            Assert.False(ParsekPlaybackPolicy.ShouldCreateStateVectorOrbit(50000, 2000, 70000));
            // Same point triggers removal
            Assert.True(ParsekPlaybackPolicy.ShouldRemoveStateVectorOrbit(50000, 2000, 70000));
        }

        #endregion

        #region RELATIVE frame guard

        [Fact]
        public void IsInRelativeFrame_NullTrackSections_ReturnsFalse()
        {
            var traj = new Recording { TrackSections = null };
            Assert.False(ParsekPlaybackPolicy.IsInRelativeFrame(traj, 100));
        }

        [Fact]
        public void IsInRelativeFrame_EmptyTrackSections_ReturnsFalse()
        {
            var traj = new Recording { TrackSections = new List<TrackSection>() };
            Assert.False(ParsekPlaybackPolicy.IsInRelativeFrame(traj, 100));
        }

        [Fact]
        public void IsInRelativeFrame_AbsoluteSection_ReturnsFalse()
        {
            var traj = new Recording
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 50, endUT = 200
                    }
                }
            };
            Assert.False(ParsekPlaybackPolicy.IsInRelativeFrame(traj, 100));
        }

        [Fact]
        public void IsInRelativeFrame_RelativeSection_ReturnsTrue()
        {
            var traj = new Recording
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 50, endUT = 200
                    }
                }
            };
            Assert.True(ParsekPlaybackPolicy.IsInRelativeFrame(traj, 100));
        }

        [Fact]
        public void IsInRelativeFrame_UTOutsideSection_ReturnsFalse()
        {
            var traj = new Recording
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 50, endUT = 200
                    }
                }
            };
            Assert.False(ParsekPlaybackPolicy.IsInRelativeFrame(traj, 300));
        }

        // -----------------------------------------------------------------
        // P1 review fix (#547): the flight-scene state-vector update path
        // must NOT feed a Relative-frame TrajectoryPoint.altitude (which is
        // the anchor-local dz, typically near 0) into
        // ShouldRemoveStateVectorOrbit. Pre-fix, dz=-0.4 + small-velocity
        // tripped the threshold and re-deferred the ghost; pending-create
        // then could not re-create it for a Relative section and the ghost
        // disappeared. The gate combination tested below documents the
        // joint behaviour the fix relies on.
        // -----------------------------------------------------------------

        [Fact]
        public void RelativeFrameGuard_DzBelowAltitudeThreshold_WouldTripRemovalWithoutGate()
        {
            // Synthetic recording matching the captured 2026-04-25_1314
            // playtest's first Relative TrackSection: dz ~ -0.4 m, world-
            // frame velocity ~2920 m/s. The dz value, when treated as
            // geographic altitude, is below every CreateStateVector
            // threshold AND below the airless-body removal threshold,
            // proving the threshold WOULD remove the ghost if the gate
            // weren't bypassed.
            var traj = new Recording
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 1658.96,
                        endUT = 1668.14
                    }
                }
            };
            const double currentUT = 1662.0;
            const double dzAsAltitude = -0.31; // anchor-local dz, NOT geographic alt
            const double worldVelocityMag = 2920.0;
            const double airlessAtmosphereDepth = 0;

            Assert.True(
                ParsekPlaybackPolicy.IsInRelativeFrame(traj, currentUT),
                "current UT lies inside the Relative-frame section");
            Assert.True(
                ParsekPlaybackPolicy.ShouldRemoveStateVectorOrbit(
                    dzAsAltitude, worldVelocityMag, airlessAtmosphereDepth),
                "without the IsInRelativeFrame gate, dz~0 would trip the " +
                "altitude threshold even though the vessel is mid-flight " +
                "at 2920 m/s — this is the bug shape the gate suppresses");
        }

        [Fact]
        public void RelativeFrameGuard_AbsoluteFrame_StillEvaluatesThreshold()
        {
            // Discriminator: an Absolute-frame point with the same low alt
            // legitimately trips the threshold. The gate's job is to skip
            // the threshold ONLY for Relative frames; Absolute behaviour
            // must be unchanged.
            var traj = new Recording
            {
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Absolute,
                        startUT = 1658.96,
                        endUT = 1668.14
                    }
                }
            };
            const double currentUT = 1662.0;

            Assert.False(
                ParsekPlaybackPolicy.IsInRelativeFrame(traj, currentUT),
                "Absolute section: gate must NOT bypass the threshold check");
            Assert.True(
                ParsekPlaybackPolicy.ShouldRemoveStateVectorOrbit(
                    altitude: -0.31, speed: 2920.0, atmosphereDepth: 0),
                "Absolute frame: alt~0 below threshold legitimately removes");
        }

        #endregion
    }
}
