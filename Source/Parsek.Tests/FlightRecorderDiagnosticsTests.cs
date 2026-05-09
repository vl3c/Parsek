using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class FlightRecorderDiagnosticsTests
    {
        [Fact]
        public void FormatPendingJointChildPartOriginSeedIds_NullOrEmpty_ReturnsNone()
        {
            Assert.Equal(
                "none",
                FlightRecorder.FormatPendingJointChildPartOriginSeedIdsForDiagnostics(null));
            Assert.Equal(
                "none",
                FlightRecorder.FormatPendingJointChildPartOriginSeedIdsForDiagnostics(new uint[0]));
        }

        [Fact]
        public void FormatPendingJointChildPartOriginSeedIds_UnderCap_ListsIds()
        {
            string summary = FlightRecorder.FormatPendingJointChildPartOriginSeedIdsForDiagnostics(
                new uint[] { 100, 200, 300 },
                maxIds: 8);

            Assert.Equal("100,200,300", summary);
        }

        [Fact]
        public void FormatPendingJointChildPartOriginSeedIds_OverCap_AppendsSuffix()
        {
            string summary = FlightRecorder.FormatPendingJointChildPartOriginSeedIdsForDiagnostics(
                new uint[] { 100, 200, 300 },
                maxIds: 2);

            Assert.Equal("100,200,...", summary);
        }

        [Fact]
        public void FormatPendingJointChildPartOriginSeedIds_MaxBelowOne_StillListsOneId()
        {
            string summary = FlightRecorder.FormatPendingJointChildPartOriginSeedIdsForDiagnostics(
                new uint[] { 100, 200 },
                maxIds: 0);

            Assert.Equal("100,...", summary);
        }

        [Fact]
        public void PendingJointChildPartOriginSeedIdsContainPid_MatchesExactPidOnly()
        {
            var ids = new List<uint> { 123, 456 };

            Assert.True(FlightRecorder.PendingJointChildPartOriginSeedIdsContainPidForDiagnostics(ids, 123));
            Assert.False(FlightRecorder.PendingJointChildPartOriginSeedIdsContainPidForDiagnostics(ids, 12));
            Assert.False(FlightRecorder.PendingJointChildPartOriginSeedIdsContainPidForDiagnostics(ids, 0));
            Assert.False(FlightRecorder.PendingJointChildPartOriginSeedIdsContainPidForDiagnostics(null, 123));
        }

        [Fact]
        public void ComputeRelativeLocalRotationFromAbsolutePointRotation_UsesSurfaceRelativePointRotation()
        {
            Quaternion bodyWorldRotation = TrajectoryMath.PureAngleAxis(30f, Vector3.up);
            Quaternion pointSurfaceRelativeRotation = TrajectoryMath.PureAngleAxis(45f, Vector3.forward);
            Quaternion anchorWorldRotation = TrajectoryMath.PureAngleAxis(10f, Vector3.right);
            Quaternion expectedFocusWorldRotation = TrajectoryMath.PureMultiply(
                bodyWorldRotation,
                pointSurfaceRelativeRotation);

            Quaternion storedRelativeRotation =
                FlightRecorder.ComputeRelativeLocalRotationFromAbsolutePointRotation(
                    pointSurfaceRelativeRotation,
                    bodyWorldRotation,
                    anchorWorldRotation);
            Quaternion reconstructedWorldRotation =
                TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchorWorldRotation,
                    storedRelativeRotation);

            Assert.True(
                TrajectoryMath.ComputeQuaternionAngleDegrees(
                    expectedFocusWorldRotation,
                    reconstructedWorldRotation) < 0.001f);
        }
    }
}
