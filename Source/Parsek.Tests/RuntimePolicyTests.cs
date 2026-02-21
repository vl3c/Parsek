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
            bool actual = ParsekFlight.IsAnyWarpActive(currentRateIndex, currentRate);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ShouldPauseTimelineResourceReplay_ReflectsRecordingState(bool isRecording, bool expected)
        {
            bool actual = ParsekFlight.ShouldPauseTimelineResourceReplay(isRecording);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        public void ShouldLoopPlayback_RequiresGlobalAndRecordingFlags(
            bool globalLoopingEnabled, bool recordingLoopPlayback, bool expected)
        {
            bool actual = ParsekFlight.ShouldLoopPlayback(globalLoopingEnabled, recordingLoopPlayback);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ComputeTargetResourceIndex_FindsHighestPassedPoint()
        {
            var points = MakePoints(10, 20, 30, 40);
            int target = ParsekFlight.ComputeTargetResourceIndex(points, lastAppliedResourceIndex: -1, currentUT: 29);
            Assert.Equal(1, target);
        }

        [Fact]
        public void ComputeTargetResourceIndex_NoAdvanceWhenBeforeNextPoint()
        {
            var points = MakePoints(10, 20, 30, 40);
            int target = ParsekFlight.ComputeTargetResourceIndex(points, lastAppliedResourceIndex: 1, currentUT: 25);
            Assert.Equal(1, target);
        }

        [Fact]
        public void ComputeScaledRcsEmissionRate_ShowcaseEnforcesVisibilityFloor()
        {
            float rate = ParsekFlight.ComputeScaledRcsEmissionRate(
                emissionCurve: null, power: 0.01f, emissionScale: 120f);

            Assert.True(rate >= 60f, $"Expected showcase emission >= 60, got {rate}");
        }

        [Fact]
        public void ComputeScaledRcsSpeed_ShowcaseEnforcesVisibilityFloor()
        {
            float speed = ParsekFlight.ComputeScaledRcsSpeed(
                speedCurve: null, power: 0.01f, speedScale: 2.5f);

            Assert.True(speed >= 4f, $"Expected showcase speed >= 4, got {speed}");
        }

        [Fact]
        public void ComputeScaledRcsRates_NonShowcaseDoesNotApplyFloors()
        {
            float rate = ParsekFlight.ComputeScaledRcsEmissionRate(
                emissionCurve: null, power: 0.25f, emissionScale: 1f);
            float speed = ParsekFlight.ComputeScaledRcsSpeed(
                speedCurve: null, power: 0.25f, speedScale: 1f);

            Assert.Equal(25f, rate);
            Assert.Equal(2.5f, speed);
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
            bool inPlayback = ParsekFlight.TryComputeLoopPlaybackUT(
                currentUT,
                startUT: 100,
                endUT: 120,
                pauseSeconds: 10,
                out double loopUT,
                out int cycleIndex);

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
    }
}
