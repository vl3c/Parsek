using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure math and struct tests for RELATIVE frame recording.
    /// These tests do NOT call any methods that log via ParsekLog,
    /// so they are safe to run in parallel without test contamination.
    /// </summary>
    public class RelativeRecordingTests
    {
        #region ComputeRelativeOffset -- basic math

        [Fact]
        public void ComputeRelativeOffset_IdenticalPositions_ReturnsZero()
        {
            var pos = new Vector3d(1000, 2000, 3000);
            var result = TrajectoryMath.ComputeRelativeOffset(pos, pos);

            Assert.Equal(0.0, result.x, 10);
            Assert.Equal(0.0, result.y, 10);
            Assert.Equal(0.0, result.z, 10);
        }

        [Fact]
        public void ComputeRelativeOffset_FocusAhead_PositiveOffset()
        {
            var focus = new Vector3d(100, 200, 300);
            var anchor = new Vector3d(0, 0, 0);
            var result = TrajectoryMath.ComputeRelativeOffset(focus, anchor);

            Assert.Equal(100.0, result.x, 10);
            Assert.Equal(200.0, result.y, 10);
            Assert.Equal(300.0, result.z, 10);
        }

        [Fact]
        public void ComputeRelativeOffset_FocusBehind_NegativeOffset()
        {
            var focus = new Vector3d(-50, -100, -150);
            var anchor = new Vector3d(0, 0, 0);
            var result = TrajectoryMath.ComputeRelativeOffset(focus, anchor);

            Assert.Equal(-50.0, result.x, 10);
            Assert.Equal(-100.0, result.y, 10);
            Assert.Equal(-150.0, result.z, 10);
        }

        [Fact]
        public void ComputeRelativeOffset_BothNonZero_CorrectDifference()
        {
            var focus = new Vector3d(1000, 2000, 3000);
            var anchor = new Vector3d(990, 1990, 2990);
            var result = TrajectoryMath.ComputeRelativeOffset(focus, anchor);

            Assert.Equal(10.0, result.x, 10);
            Assert.Equal(10.0, result.y, 10);
            Assert.Equal(10.0, result.z, 10);
        }

        [Fact]
        public void ComputeRelativeOffset_LargePositions_SmallOffset()
        {
            // Simulates two vessels near KSC -- large absolute coords, small offset
            var focus = new Vector3d(600000.5, 0.3, 600000.2);
            var anchor = new Vector3d(600000.0, 0.0, 600000.0);
            var result = TrajectoryMath.ComputeRelativeOffset(focus, anchor);

            Assert.Equal(0.5, result.x, 5);
            Assert.Equal(0.3, result.y, 5);
            Assert.Equal(0.2, result.z, 5);
        }

        [Fact]
        public void ComputeRelativeOffset_OffsetMagnitude_MatchesDistance()
        {
            var focus = new Vector3d(3, 4, 0);
            var anchor = new Vector3d(0, 0, 0);
            var result = TrajectoryMath.ComputeRelativeOffset(focus, anchor);

            Assert.Equal(5.0, result.magnitude, 10);
        }

        #endregion

        #region ComputeRelativeOffset -- symmetry

        [Fact]
        public void ComputeRelativeOffset_Antisymmetric()
        {
            // offset(A, B) == -offset(B, A)
            var a = new Vector3d(100, 200, 300);
            var b = new Vector3d(10, 20, 30);

            var ab = TrajectoryMath.ComputeRelativeOffset(a, b);
            var ba = TrajectoryMath.ComputeRelativeOffset(b, a);

            Assert.Equal(ab.x, -ba.x, 10);
            Assert.Equal(ab.y, -ba.y, 10);
            Assert.Equal(ab.z, -ba.z, 10);
        }

        #endregion

        #region TrackSection RELATIVE metadata

        [Fact]
        public void TrackSection_RelativeFrame_StoresAnchorVesselId()
        {
            var section = new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                anchorVesselId = 42u,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };

            Assert.Equal(ReferenceFrame.Relative, section.referenceFrame);
            Assert.Equal(42u, section.anchorVesselId);
        }

        [Fact]
        public void TrackSection_AbsoluteFrame_AnchorVesselIdDefaultsToZero()
        {
            var section = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };

            Assert.Equal(0u, section.anchorVesselId);
        }

        #endregion

        #region Offset stored in TrajectoryPoint matches computed offset

        [Fact]
        public void RelativePoint_OffsetMatchesComputedValue()
        {
            var focusPos = new Vector3d(600100, 50, 600200);
            var anchorPos = new Vector3d(600000, 0, 600000);

            var offset = TrajectoryMath.ComputeRelativeOffset(focusPos, anchorPos);

            // Simulate storing in point
            var point = new TrajectoryPoint
            {
                latitude = offset.x,
                longitude = offset.y,
                altitude = offset.z,
                bodyName = "Kerbin"
            };

            Assert.Equal(100.0, point.latitude, 5);
            Assert.Equal(50.0, point.longitude, 5);
            Assert.Equal(200.0, point.altitude, 5);
        }

        #endregion
    }

    /// <summary>
    /// Integration tests for RELATIVE mode transitions and logging.
    /// These tests call AnchorDetector methods which log via ParsekLog,
    /// so they run in the Sequential collection to avoid log contamination.
    /// </summary>
    [Collection("Sequential")]
    public class RelativeRecordingIntegrationTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RelativeRecordingIntegrationTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region AnchorDetector + mode transition integration

        [Fact]
        public void AnchorDetection_TransitionLogic_EnterRelativeWhenClose()
        {
            // Simulate the per-frame decision logic
            bool isRelativeMode = false;
            uint currentAnchorPid = 0;
            uint focusedPid = 1u;

            var vessels = new List<(uint pid, Vector3d position)>
            {
                (1u, new Vector3d(0, 0, 0)),        // focused vessel
                (42u, new Vector3d(2000, 0, 0))      // anchor at 2000m
            };

            var (anchorPid, anchorDist) = AnchorDetector.FindNearestAnchor(
                focusedPid, new Vector3d(0, 0, 0), vessels, null);

            bool shouldBeRelative = anchorPid != 0 &&
                AnchorDetector.ShouldUseRelativeFrame(anchorDist, isRelativeMode);

            Assert.True(shouldBeRelative);
            Assert.Equal(42u, anchorPid);

            // Apply transition
            if (shouldBeRelative && !isRelativeMode)
            {
                isRelativeMode = true;
                currentAnchorPid = anchorPid;
            }

            Assert.True(isRelativeMode);
            Assert.Equal(42u, currentAnchorPid);
        }

        [Fact]
        public void AnchorDetection_TransitionLogic_ExitRelativeWhenFar()
        {
            bool isRelativeMode = true;
            uint currentAnchorPid = 42u;
            uint focusedPid = 1u;

            // Anchor at 3000m -- beyond exit threshold (2500m)
            var vessels = new List<(uint pid, Vector3d position)>
            {
                (1u, new Vector3d(0, 0, 0)),
                (42u, new Vector3d(3000, 0, 0))
            };

            var (anchorPid, anchorDist) = AnchorDetector.FindNearestAnchor(
                focusedPid, new Vector3d(0, 0, 0), vessels, null);

            bool shouldBeRelative = anchorPid != 0 &&
                AnchorDetector.ShouldUseRelativeFrame(anchorDist, isRelativeMode);

            Assert.False(shouldBeRelative);

            // Apply transition
            if (!shouldBeRelative && isRelativeMode)
            {
                isRelativeMode = false;
                currentAnchorPid = 0;
            }

            Assert.False(isRelativeMode);
            Assert.Equal(0u, currentAnchorPid);
        }

        [Fact]
        public void AnchorDetection_TransitionLogic_HysteresisStaysRelative()
        {
            // Already relative, anchor at 2400m (between entry 2300 and exit 2500)
            // Should stay relative due to hysteresis
            bool isRelativeMode = true;
            uint currentAnchorPid = 42u;
            uint focusedPid = 1u;

            var vessels = new List<(uint pid, Vector3d position)>
            {
                (1u, new Vector3d(0, 0, 0)),
                (42u, new Vector3d(2400, 0, 0))
            };

            var (anchorPid, anchorDist) = AnchorDetector.FindNearestAnchor(
                focusedPid, new Vector3d(0, 0, 0), vessels, null);

            bool shouldBeRelative = anchorPid != 0 &&
                AnchorDetector.ShouldUseRelativeFrame(anchorDist, isRelativeMode);

            Assert.True(shouldBeRelative);
            // State should remain unchanged
            Assert.True(isRelativeMode);
            Assert.Equal(42u, currentAnchorPid);
        }

        [Fact]
        public void AnchorDetection_TransitionLogic_NoAnchor_StaysAbsolute()
        {
            bool isRelativeMode = false;
            uint focusedPid = 1u;

            // Only the focused vessel in scene
            var vessels = new List<(uint pid, Vector3d position)>
            {
                (1u, new Vector3d(0, 0, 0))
            };

            var (anchorPid, anchorDist) = AnchorDetector.FindNearestAnchor(
                focusedPid, new Vector3d(0, 0, 0), vessels, null);

            // anchorPid is 0 -- short-circuit, shouldBeRelative is false
            bool shouldBeRelative = anchorPid != 0 &&
                AnchorDetector.ShouldUseRelativeFrame(anchorDist, isRelativeMode);

            Assert.False(shouldBeRelative);
            Assert.False(isRelativeMode);
        }

        [Fact]
        public void AnchorDetection_TransitionLogic_TreeVesselsExcluded_StaysAbsolute()
        {
            bool isRelativeMode = false;
            uint focusedPid = 1u;

            // Close vessel at 500m, but it's in the tree
            var vessels = new List<(uint pid, Vector3d position)>
            {
                (1u, new Vector3d(0, 0, 0)),
                (42u, new Vector3d(500, 0, 0))
            };
            var treePids = new HashSet<uint> { 42u };

            var (anchorPid, anchorDist) = AnchorDetector.FindNearestAnchor(
                focusedPid, new Vector3d(0, 0, 0), vessels, treePids);

            // Tree vessel excluded, no other anchor
            Assert.Equal(0u, anchorPid);

            bool shouldBeRelative = anchorPid != 0 &&
                AnchorDetector.ShouldUseRelativeFrame(anchorDist, isRelativeMode);

            Assert.False(shouldBeRelative);
        }

        #endregion

        #region Log assertions for mode transitions

        [Fact]
        public void AnchorDetector_EntryTransition_LogsRelativeEntry()
        {
            logLines.Clear();

            // Trigger entry: vessel at 2000m, not currently relative
            AnchorDetector.ShouldUseRelativeFrame(2000.0, false);

            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("RELATIVE entry") &&
                l.Contains("2000.0m"));
        }

        [Fact]
        public void AnchorDetector_ExitTransition_LogsRelativeExit()
        {
            logLines.Clear();

            // Trigger exit: vessel at 3000m, currently relative
            AnchorDetector.ShouldUseRelativeFrame(3000.0, true);

            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("RELATIVE exit") &&
                l.Contains("3000.0m"));
        }

        [Fact]
        public void AnchorDetector_StayAbsolute_NoTransitionLog()
        {
            logLines.Clear();

            // No transition: vessel at 5000m, not currently relative
            AnchorDetector.ShouldUseRelativeFrame(5000.0, false);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("RELATIVE entry") || l.Contains("RELATIVE exit"));
        }

        [Fact]
        public void AnchorDetector_StayRelative_NoTransitionLog()
        {
            logLines.Clear();

            // No transition: vessel at 1000m, currently relative
            AnchorDetector.ShouldUseRelativeFrame(1000.0, true);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("RELATIVE entry") || l.Contains("RELATIVE exit"));
        }

        [Fact]
        public void AnchorDetector_FindNearestAnchor_LogsAnchorPid()
        {
            logLines.Clear();

            var vessels = new List<(uint, Vector3d)>
            {
                (42u, new Vector3d(500, 0, 0))
            };
            AnchorDetector.FindNearestAnchor(1u, new Vector3d(0, 0, 0), vessels, null);

            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]") &&
                l.Contains("anchorPid=42"));
        }

        #endregion
    }
}
