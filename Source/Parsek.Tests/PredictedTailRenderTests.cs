using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost follows its orbit tail past the last recorded point on every surface that used
    /// to clamp to that point: the Tracking Station marker (time gate + position), the KSC ghost,
    /// and the loop path (which lost the tail continuity blend and teleported at the tail start
    /// every cycle). The flight-map marker shares <see cref="GhostPlaybackLogic.TryFindTailSegment"/>; its live body
    /// read is in-game only.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTailRenderTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private readonly List<string> logLines = new List<string>();

        public PredictedTailRenderTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static OrbitSegment KerbinCircular(double startUT, double endUT, bool predicted = true)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                eccentricity = 0.0,
                inclination = 0.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 0.0,
                isPredicted = predicted,
            };
        }

        /// <summary>A crash flight: recorded points to UT 200, a predicted coast to 300.</summary>
        private static Recording CrashWithPredictedTail()
        {
            var rec = new Recording
            {
                RecordingId = "crash-tail",
                VesselName = "Crash",
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitEndUT = 300.0,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100.0, latitude = 1.0, longitude = 2.0, altitude = 50000.0,
                bodyName = "Kerbin", rotation = Quaternion.identity
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200.0, latitude = 3.0, longitude = 4.0, altitude = 60000.0,
                bodyName = "Kerbin", rotation = Quaternion.identity
            });
            rec.OrbitSegments.Add(KerbinCircular(200.0, 300.0));
            return rec;
        }

        // ---- R1: the Tracking Station marker time gate ----

        [Fact]
        public void ClassifyAtmosphericMarkerSkip_CrashTail_DrawsPastTheLastPoint()
        {
            Recording rec = CrashWithPredictedTail();

            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.None,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 250.0, suppressedIds: null));
        }

        [Fact]
        public void ClassifyAtmosphericMarkerSkip_PastTheRecordingEnd_StillOutsideTimeRange()
        {
            Recording rec = CrashWithPredictedTail();

            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.OutsideTimeRange,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 350.0, suppressedIds: null));
            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.OutsideTimeRange,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 50.0, suppressedIds: null));
        }

        [Fact]
        public void IsInsidePointsOrTail_NoTail_EndsAtTheLastPoint()
        {
            Recording rec = CrashWithPredictedTail();
            rec.OrbitSegments.Clear();
            rec.ExplicitEndUT = double.NaN;

            Assert.True(GhostPlaybackLogic.IsInsidePointsOrTail(rec, 150.0));
            Assert.True(GhostPlaybackLogic.IsInsidePointsOrTail(rec, 200.0));
            Assert.False(GhostPlaybackLogic.IsInsidePointsOrTail(rec, 200.5));
        }

        [Fact]
        public void TryFindTailSegment_InsideThePoints_IsNotATail()
        {
            Recording rec = CrashWithPredictedTail();
            Assert.False(GhostPlaybackLogic.TryFindTailSegment(rec, 150.0, out _));
            Assert.True(GhostPlaybackLogic.TryFindTailSegment(rec, 250.0, out OrbitSegment seg));
            Assert.Equal(200.0, seg.startUT, 6);
            Assert.False(GhostPlaybackLogic.TryFindTailSegment(rec, 300.5, out _));
        }

        [Fact]
        public void TryComputeTailWorldOffset_CircularOrbit_StaysOnTheOrbitRadius()
        {
            OrbitSegment seg = KerbinCircular(200.0, 300.0);

            Assert.True(GhostPlaybackLogic.TryComputeTailWorldOffset(
                seg, KerbinMu, 210.0, out Vector3d a, out Vector3d va));
            Assert.True(GhostPlaybackLogic.TryComputeTailWorldOffset(
                seg, KerbinMu, 290.0, out Vector3d b, out _));

            Assert.Equal(700000.0, a.magnitude, 0);
            Assert.Equal(700000.0, b.magnitude, 0);
            Assert.True((a - b).magnitude > 1000.0, "the ghost moves along the tail");
            Assert.Equal(Math.Sqrt(KerbinMu / 700000.0), va.magnitude, 0);
        }

        [Fact]
        public void TryComputeTailWorldOffset_NoGravParameter_Declines()
        {
            Assert.False(GhostPlaybackLogic.TryComputeTailWorldOffset(
                KerbinCircular(200.0, 300.0), 0.0, 210.0, out _, out _));
        }

        // ---- R3: the KSC ghost follows the tail ----

        private static ParsekKSC.KscSurfaceLookup FakeSurface()
        {
            return (string bodyName, double lat, double lon, double alt,
                out Vector3d worldPos, out Quaternion bodyRot) =>
            {
                bodyRot = Quaternion.identity;
                worldPos = new Vector3d(lat * 100.0, lon * 100.0, alt);
                return bodyName == "Kerbin";
            };
        }

        private static readonly Vector3d FakeBodyPos = new Vector3d(1.0e6, 2.0e6, 3.0e6);

        private static ParsekKSC.KscBodyStateLookup FakeBody()
        {
            return (string bodyName, out Vector3d bodyWorldPos, out double mu) =>
            {
                bodyWorldPos = FakeBodyPos;
                mu = KerbinMu;
                return bodyName == "Kerbin";
            };
        }

        private static bool NoAnchor(Recording rec, TrackSection section, int sectionIndex,
            double targetUT, out ParsekKSC.KscAnchorFrame anchor)
        {
            anchor = default(ParsekKSC.KscAnchorFrame);
            return false;
        }

        [Fact]
        public void KscPose_PastTheLastPoint_RidesTheOrbitTail()
        {
            Recording rec = CrashWithPredictedTail();
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec, ref cachedIndex, ref cachedKey, 280.0,
                FakeSurface(), NoAnchor, out ParsekKSC.KscPoseResolution pose, FakeBody());

            Assert.True(resolved);
            Assert.Equal(ParsekKSC.KscOrbitTailBranch, pose.Branch);
            // Past the continuity blend: exactly on the conic around the body.
            Assert.Equal(700000.0, (pose.WorldPos - FakeBodyPos).magnitude, 0);
            Assert.Contains(logLines, l => l.Contains("[KSCGhost]")
                && l.Contains("KSC orbit tail playback resolved")
                && l.Contains("predicted=True"));
        }

        [Fact]
        public void KscPose_FirstTailFrame_StartsAtTheLastRecordedPoint()
        {
            // The continuity blend eases off the last body-fixed point, so the tail start does
            // not jump from the last sample onto the conic.
            Recording rec = CrashWithPredictedTail();
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec, ref cachedIndex, ref cachedKey, 200.001,
                FakeSurface(), NoAnchor, out ParsekKSC.KscPoseResolution pose, FakeBody());

            Assert.True(resolved);
            var lastPointWorld = new Vector3d(300.0, 400.0, 60000.0);
            Assert.True((pose.WorldPos - lastPointWorld).magnitude < 50.0,
                "first tail frame at " + pose.WorldPos + " vs last point " + lastPointWorld);
        }

        [Fact]
        public void KscPose_WithoutBodyLookup_KeepsThePointBehaviour()
        {
            // The pre-tail contract (no body seam): the last point holds.
            Recording rec = CrashWithPredictedTail();
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec, ref cachedIndex, ref cachedKey, 280.0,
                FakeSurface(), NoAnchor, out ParsekKSC.KscPoseResolution pose);

            Assert.True(resolved);
            Assert.NotEqual(ParsekKSC.KscOrbitTailBranch, pose.Branch);
        }

        [Fact]
        public void KscPose_TailAroundAnotherBody_IsNonKerbin()
        {
            Recording rec = CrashWithPredictedTail();
            var seg = rec.OrbitSegments[0];
            seg.bodyName = "Mun";
            rec.OrbitSegments[0] = seg;
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec, ref cachedIndex, ref cachedKey, 280.0,
                FakeSurface(), NoAnchor, out ParsekKSC.KscPoseResolution pose, FakeBody());

            Assert.False(resolved);
            Assert.Equal("non-kerbin", pose.FailureReason);
        }

        // ---- R4: the loop path rides the tail through PositionFromOrbit ----

        [Fact]
        public void ShouldPositionLoopFromOrbitTail_PastTheLastPoint_True()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 200.0);
            traj.EndUTOverride = 300.0;
            traj.OrbitSegments.Add(KerbinCircular(200.0, 300.0));

            Assert.False(GhostPlaybackEngine.ShouldPositionLoopFromOrbitTail(traj, 150.0));
            Assert.True(GhostPlaybackEngine.ShouldPositionLoopFromOrbitTail(traj, 250.0));
        }

        [Fact]
        public void LoopDispatch_RoutesTheTailToPositionFromOrbit_AndLoopStatesSkipTheOffsetCache()
        {
            // Source gate (live-only path): the loop dispatcher sends a tail frame to the
            // trajectory-carrying orbit path, and a loop cycle does not reuse a continuity offset
            // cached under an earlier cycle's body rotation.
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string loop = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Ghost", "GhostPlaybackEngine.LoopPlayback.cs"));
            int gate = loop.IndexOf("else if (ShouldPositionLoopFromOrbitTail(traj, loopUT))", StringComparison.Ordinal);
            Assert.True(gate >= 0, "loop dispatcher lost the orbit-tail branch");
            int call = loop.IndexOf("positioner.PositionFromOrbit(index, traj, state, loopUT);", gate, StringComparison.Ordinal);
            int loopCall = loop.IndexOf("positioner.PositionLoop(index, traj, state, loopUT, suppressFx);", gate, StringComparison.Ordinal);
            Assert.True(call > gate && call < loopCall, "the tail branch must call PositionFromOrbit before the PositionLoop fallback");

            string orbit = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Controllers", "ParsekFlightGhostPositioner.Orbit.cs"));
            Assert.Contains("cacheContinuityOffset: state.loopCycleIndex < 0", orbit);
        }

        [Fact]
        public void ShouldPositionLoopFromOrbitTail_NoTail_False()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 200.0);
            Assert.False(GhostPlaybackEngine.ShouldPositionLoopFromOrbitTail(traj, 250.0));
        }
    }
}
