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

        #region Format-v6 RELATIVE position contract — captured-log regression

        // Regression: pins the format-v6 RELATIVE-frame position contract that
        // FlightRecorder.ApplyRelativeOffset implements (Source/Parsek/FlightRecorder.cs:5502-5543).
        //
        // The recorder calls ComputeRelativeLocalOffset(focusWorld, anchorWorld, anchorRotation)
        // and stores the resulting (dx, dy, dz) in TrajectoryPoint.latitude/longitude/altitude.
        // Playback resolves the world position via ApplyRelativeLocalOffset using the same
        // anchor rotation. The fields are NOT body-fixed lat/lon/alt for v6 RELATIVE sections —
        // the labels are misleading; values are anchor-local Cartesian metres.
        //
        // The captured-log scenario (logs/2026-04-25_1314_marker-validator-fix/parsek/Recordings/
        // b85acd51ea7f4005bb5d879207749e8c.prec, first TRACK_SECTION) exhibited two co-orbiting
        // vessels (focus pid=2708531065 / anchor pid=95506284, both split at UT=1627.16 from a
        // common parent) with world-frame velocity ~2920 m/s but recorded latitude/longitude
        // values changing on a 100 m scale over 9 s. That's correct under this contract: the
        // anchor moves in lockstep with the focus, so anchor-local offset stays small while
        // world-frame displacement is ~26 km.

        [Fact]
        public void RecorderContract_V6RelativeStoresAnchorLocalOffset_ReplaysToFocusWorldPos()
        {
            // Two co-orbiting vessels mid-flight: large absolute world position, large shared
            // velocity (modelled as anchor rotation aligning the local frame with the velocity
            // direction). The world-frame separation is small relative to the absolute coords.
            var anchorWorld = new Vector3d(600100.5, 50.25, 600200.7);
            var focusWorld = new Vector3d(600200.4, 75.5, 600250.2);
            // Anchor rotation: arbitrary non-identity orientation so the local frame doesn't
            // accidentally coincide with world axes (catches any "v6 forgets to rotate" bug).
            Quaternion anchorRot = TrajectoryMath.PureAngleAxis(37f, new Vector3(0.3f, 0.7f, 0.5f).normalized);

            // Recorder side: ComputeRelativeLocalOffset is what ApplyRelativeOffset calls
            // when UsesRelativeLocalFrameContract(version) is true (FlightRecorder.cs:5516).
            Vector3d offset = TrajectoryMath.ComputeRelativeLocalOffset(focusWorld, anchorWorld, anchorRot);

            // Stored values match the captured-log shape: small magnitude in metres.
            // (The world-frame displacement is sqrt(99.9^2 + 25.25^2 + 49.5^2) ≈ 114 m,
            //  which is the same order of magnitude as the captured log's first-point |offset|=309m.)
            Assert.True(offset.magnitude < 1000.0,
                $"v6 RELATIVE offset must be metre-scale for co-located vessels; got {offset.magnitude:F2}m");

            // The TrajectoryPoint that gets serialised carries (offset.x, offset.y, offset.z)
            // in latitude/longitude/altitude — see FlightRecorder.cs:5533-5535.
            var storedPoint = new TrajectoryPoint
            {
                latitude = offset.x,
                longitude = offset.y,
                altitude = offset.z,
                bodyName = "Kerbin"
            };

            // Playback side: ResolveRelativePlaybackPosition with v6 must round-trip back
            // to the focus world position. Uses RelativeLocalFrameFormatVersion (= current
            // format version 6) so the v6 anchor-local branch fires.
            Vector3d reconstructed = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorWorld,
                anchorRot,
                storedPoint.latitude,
                storedPoint.longitude,
                storedPoint.altitude,
                RecordingStore.RelativeLocalFrameFormatVersion);

            Assert.Equal(focusWorld.x, reconstructed.x, 3);
            Assert.Equal(focusWorld.y, reconstructed.y, 3);
            Assert.Equal(focusWorld.z, reconstructed.z, 3);
        }

        [Fact]
        public void RecorderContract_V6RelativeOffsetIndependentOfAnchorWorldVelocity()
        {
            // The contract is: anchor-local Cartesian offset. Two anchors at different world
            // positions but with the same rotation must produce the same offset for the same
            // anchor->focus world displacement. This pins the "world-velocity should not leak
            // into the recorded offset" property — even if anchor moves at orbital velocity
            // between two captures, the anchor-local offset only depends on the relative
            // world position and the anchor rotation.
            Quaternion anchorRot = TrajectoryMath.PureAngleAxis(45f, Vector3.up);
            var displacement = new Vector3d(50, -25, 100);

            var anchorA = new Vector3d(0, 0, 0);
            var focusA = anchorA + displacement;
            Vector3d offsetA = TrajectoryMath.ComputeRelativeLocalOffset(focusA, anchorA, anchorRot);

            // Anchor moved 30 km in world space (one tick at orbital velocity). Same rotation,
            // same relative displacement. Stored offset must be identical.
            var anchorB = new Vector3d(30000, 0, 0);
            var focusB = anchorB + displacement;
            Vector3d offsetB = TrajectoryMath.ComputeRelativeLocalOffset(focusB, anchorB, anchorRot);

            Assert.Equal(offsetA.x, offsetB.x, 4);
            Assert.Equal(offsetA.y, offsetB.y, 4);
            Assert.Equal(offsetA.z, offsetB.z, 4);
        }

        [Fact]
        public void RecorderContract_V6RelativeFieldsAreNotBodyFixedLatLonAlt()
        {
            // Defensive regression: if a future refactor mistakes RELATIVE-frame TrajectoryPoint
            // fields for body-fixed lat/lon/alt (the legacy ABSOLUTE-frame contract), the values
            // returned by ComputeRelativeLocalOffset will fall outside legitimate KSP lat/lon
            // ranges (lat in [-90, 90], lon in [-180, 180]). This pins that the recorder is
            // free to write |dx| and |dy| above 180 — and the captured log demonstrates this
            // with lat=-270.69 and lon=-149.22 in the first sample of the b85acd51 recording.
            //
            // Any future code path that interprets RELATIVE-frame point.latitude as a degrees
            // value WILL go badly wrong; this test stands as a tripwire for that confusion.
            Quaternion anchorRot = Quaternion.identity;
            var anchor = new Vector3d(0, 0, 0);
            // Focus at 300 m along the local x-axis — a normal RELATIVE-mode separation.
            var focus = new Vector3d(300, 0, 0);

            Vector3d offset = TrajectoryMath.ComputeRelativeLocalOffset(focus, anchor, anchorRot);

            // dx = 300 metres. If treated as a latitude in degrees, that would be nonsensical
            // (lat must be in [-90, 90]). The contract is metres, not degrees.
            Assert.Equal(300.0, offset.x, 3);
            Assert.True(System.Math.Abs(offset.x) > 90.0,
                "v6 RELATIVE dx exceeds the legitimate latitude-degrees range, " +
                "confirming the field is metres-of-anchor-local-offset, not body-fixed lat. " +
                "Captured log b85acd51… first sample stored lat=-270.69, lon=-149.22 — " +
                "values that would be absurd as degrees but are correct as metres.");
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
