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
