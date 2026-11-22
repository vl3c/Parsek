using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling R4b: a looped replay of a predicted path that ends on the ground turns the
    /// path with the planet by the rotation between the recorded epoch and the playback epoch,
    /// so every repeat hits the real crash site. Orbiting tails and non-loop ghosts are not
    /// turned. The flight mesh's live body reads are in-game only; the decision, the rigid
    /// turn and the KSC pose are pinned here.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTailPlanetTurnTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private const double R = 700000.0;
        private readonly List<string> logLines = new List<string>();

        public PredictedTailPlanetTurnTests()
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
                semiMajorAxis = R,
                eccentricity = 0.0,
                isPredicted = predicted,
            };
        }

        private static Recording CrashWithPredictedTail(TerminalState terminal = TerminalState.Destroyed)
        {
            var rec = new Recording
            {
                RecordingId = "loop-crash",
                VesselName = "LoopCrash",
                TerminalStateValue = terminal,
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

        // ---- the decision ----

        [Theory]
        [InlineData(TerminalState.Destroyed, true)]
        [InlineData(TerminalState.Landed, true)]
        [InlineData(TerminalState.Splashed, true)]
        [InlineData(TerminalState.Orbiting, false)]
        [InlineData(TerminalState.SubOrbital, false)]
        [InlineData(TerminalState.Recovered, false)]
        public void IsGroundEndingPredictedTail_ByEnding(TerminalState terminal, bool expected)
        {
            Recording rec = CrashWithPredictedTail(terminal);
            Assert.Equal(expected, GhostPlaybackLogic.IsGroundEndingPredictedTail(rec, rec.OrbitSegments[0]));
        }

        [Fact]
        public void IsGroundEndingPredictedTail_RecordedSegment_False()
        {
            Recording rec = CrashWithPredictedTail();
            rec.OrbitSegments[0] = KerbinCircular(200.0, 300.0, predicted: false);
            Assert.False(GhostPlaybackLogic.IsGroundEndingPredictedTail(rec, rec.OrbitSegments[0]));
        }

        [Fact]
        public void IsGroundEndingPredictedTail_SegmentAroundAnEarlierBody_False()
        {
            // A predicted Mun leg before the Kerbin crash leg is not turned with Kerbin.
            Recording rec = CrashWithPredictedTail();
            OrbitSegment mun = KerbinCircular(200.0, 250.0);
            mun.bodyName = "Mun";
            rec.OrbitSegments[0] = mun;
            rec.OrbitSegments.Add(KerbinCircular(250.0, 300.0));

            Assert.False(GhostPlaybackLogic.IsGroundEndingPredictedTail(rec, rec.OrbitSegments[0]));
            Assert.True(GhostPlaybackLogic.IsGroundEndingPredictedTail(rec, rec.OrbitSegments[1]));
        }

        [Fact]
        public void ResolveTailPlanetTurnSeconds_LoopReplayOfACrash_IsTheReplayOffset()
        {
            Recording rec = CrashWithPredictedTail();
            OrbitSegment seg = rec.OrbitSegments[0];

            Assert.Equal(-7200.0, GhostPlaybackLogic.ResolveTailPlanetTurnSeconds(
                rec, seg, isReplay: true, playbackUT: 250.0, currentUT: 7450.0), 9);
            Assert.Equal(0.0, GhostPlaybackLogic.ResolveTailPlanetTurnSeconds(
                rec, seg, isReplay: false, playbackUT: 250.0, currentUT: 7450.0));
            Assert.Equal(0.0, GhostPlaybackLogic.ResolveTailPlanetTurnSeconds(
                rec, seg, isReplay: true, playbackUT: 250.0, currentUT: 250.0));
            // The replay's phasing derotation of its points is carried into the turn, so the
            // turned tail stays on the points it continues.
            Assert.Equal(-7150.0, GhostPlaybackLogic.ResolveTailPlanetTurnSeconds(
                rec, seg, isReplay: true, playbackUT: 250.0, currentUT: 7450.0,
                bodyFixedShiftSeconds: 50.0), 9);
        }

        [Fact]
        public void ResolveTailPlanetTurnSeconds_OrbitingTail_IsNotTurned()
        {
            Recording rec = CrashWithPredictedTail(TerminalState.Orbiting);
            Assert.Equal(0.0, GhostPlaybackLogic.ResolveTailPlanetTurnSeconds(
                rec, rec.OrbitSegments[0], isReplay: true, playbackUT: 250.0, currentUT: 7450.0));
        }

        // ---- the rigid turn ----

        private static Vector3d AtLongitude(double deg)
        {
            double r = deg * Math.PI / 180.0;
            return new Vector3d(Math.Cos(r), 0.0, Math.Sin(r)) * R;
        }

        private static GhostPlaybackLogic.TailPlanetTurn TurnBy(double deg)
        {
            Assert.True(GhostPlaybackLogic.TryBuildTailPlanetTurn(
                new Vector3d(0.0, R, 0.0), AtLongitude(0.0), AtLongitude(deg), out var turn));
            return turn;
        }

        [Theory]
        [InlineData(30.0)]
        [InlineData(-75.0)]
        [InlineData(170.0)]
        public void TailPlanetTurn_MapsTheEquatorProbeOntoItsTurnedSelf(double deg)
        {
            var turn = TurnBy(deg);
            Vector3d bodyPos = new Vector3d(5.0, -3.0, 2.0);

            Vector3d moved = turn.Apply(bodyPos, bodyPos + AtLongitude(0.0));
            Assert.True((moved - (bodyPos + AtLongitude(deg))).magnitude < 1e-3);

            // Any body-fixed point turns by the same longitude step.
            Vector3d other = turn.Apply(bodyPos, bodyPos + AtLongitude(40.0));
            Assert.True((other - (bodyPos + AtLongitude(40.0 + deg))).magnitude < 1e-3);

            Vector3d back = turn.Unapply(bodyPos, moved);
            Assert.True((back - (bodyPos + AtLongitude(0.0))).magnitude < 1e-3);
        }

        [Theory]
        [InlineData(30.0)]
        [InlineData(-75.0)]
        public void TailPlanetTurn_VectorsAndAttitudesTurnLikePositions(double deg)
        {
            // The quaternion used for velocities and attitudes must turn the same way the
            // Rodrigues position turn does; a sign flip would leave the attitude mirrored.
            var turn = TurnBy(deg);
            Vector3 v = new Vector3(120.0f, 35.0f, -60.0f);
            Vector3 viaQuat = turn.ApplyVector(v);
            Vector3d viaRodrigues = GhostPlaybackLogic.RotateAboutAxis(
                new Vector3d(v.x, v.y, v.z), turn.Axis, turn.AngleRad);
            Assert.True((new Vector3d(viaQuat.x, viaQuat.y, viaQuat.z) - viaRodrigues).magnitude < 1e-2);

            Quaternion turnedAttitude = turn.ApplyRotation(Quaternion.identity);
            Vector3 fwd = TrajectoryMath.PureRotateVector(turnedAttitude, new Vector3(0f, 0f, 1f));
            Vector3d fwdRodrigues = GhostPlaybackLogic.RotateAboutAxis(
                new Vector3d(0.0, 0.0, 1.0), turn.Axis, turn.AngleRad);
            Assert.True((new Vector3d(fwd.x, fwd.y, fwd.z) - fwdRodrigues).magnitude < 1e-4);
        }

        [Fact]
        public void TryBuildTailPlanetTurn_NoTurn_Inactive()
        {
            Assert.False(GhostPlaybackLogic.TryBuildTailPlanetTurn(
                new Vector3d(0.0, R, 0.0), AtLongitude(0.0), AtLongitude(0.0), out var turn));
            Assert.False(turn.Active);
            Vector3d p = new Vector3d(1.0, 2.0, 3.0);
            Assert.True((turn.Apply(Vector3d.zero, p) - p).magnitude < 1e-12);
        }

        // ---- the KSC ghost ----

        private static readonly Vector3d FakeBodyPos = new Vector3d(1.0e6, 2.0e6, 3.0e6);

        private static ParsekKSC.KscSurfaceLookup FakeSurface()
        {
            return (string bodyName, double lat, double lon, double alt,
                out Vector3d worldPos, out Quaternion bodyRot) =>
            {
                bodyRot = Quaternion.identity;
                worldPos = FakeBodyPos + AtLongitude(lon);
                return bodyName == "Kerbin";
            };
        }

        private static ParsekKSC.KscBodyStateLookup FakeBody()
        {
            return (string bodyName, out Vector3d bodyWorldPos, out double mu) =>
            {
                bodyWorldPos = FakeBodyPos;
                mu = KerbinMu;
                return bodyName == "Kerbin";
            };
        }

        // A planet turning 1 degree per 100 s of shift.
        private static ParsekKSC.KscTailTurnLookup FakeTurn(List<double> requested)
        {
            return (string bodyName, double shiftSeconds, out GhostPlaybackLogic.TailPlanetTurn turn) =>
            {
                requested.Add(shiftSeconds);
                return GhostPlaybackLogic.TryBuildTailPlanetTurn(
                    new Vector3d(0.0, R, 0.0), AtLongitude(0.0), AtLongitude(-shiftSeconds / 100.0), out turn);
            };
        }

        private static bool NoAnchor(Recording rec, TrackSection section, int sectionIndex,
            double targetUT, out ParsekKSC.KscAnchorFrame anchor)
        {
            anchor = default(ParsekKSC.KscAnchorFrame);
            return false;
        }

        private static ParsekKSC.KscPoseResolution KscPose(
            Recording rec, double ut, ParsekKSC.KscTailTurnLookup turn, double replayOffset)
        {
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;
            Assert.True(ParsekKSC.TryInterpolateKscPlaybackPose(
                rec, ref cachedIndex, ref cachedKey, ut,
                FakeSurface(), NoAnchor, out ParsekKSC.KscPoseResolution pose, FakeBody(),
                turn, replayOffset));
            return pose;
        }

        [Fact]
        public void KscPose_LoopReplayOfACrashTail_TurnsWithThePlanet()
        {
            Recording rec = CrashWithPredictedTail();
            var requested = new List<double>();
            ParsekKSC.KscPoseResolution turned = KscPose(rec, 280.0, FakeTurn(requested), -1000.0);
            ParsekKSC.KscPoseResolution plain = KscPose(rec, 280.0, null, 0.0);

            Assert.Equal(new List<double> { -1000.0 }, requested);
            Assert.True(GhostPlaybackLogic.TryBuildTailPlanetTurn(
                new Vector3d(0.0, R, 0.0), AtLongitude(0.0), AtLongitude(10.0), out var expected));
            Vector3d want = expected.Apply(FakeBodyPos, plain.WorldPos);
            Assert.True((turned.WorldPos - want).magnitude < 1.0,
                "turned " + turned.WorldPos + " want " + want);
            Assert.Contains(logLines, l => l.Contains("[KSCGhost]") && l.Contains("planetTurnDeg=-10.0000"));
        }

        [Fact]
        public void KscPose_NonReplayOrOrbitingTail_IsNotTurned()
        {
            var requested = new List<double>();
            Recording crash = CrashWithPredictedTail();
            ParsekKSC.KscPoseResolution plain = KscPose(crash, 280.0, null, 0.0);
            ParsekKSC.KscPoseResolution live = KscPose(crash, 280.0, FakeTurn(requested), 0.0);
            Assert.True((live.WorldPos - plain.WorldPos).magnitude < 1e-6);

            Recording orbiting = CrashWithPredictedTail(TerminalState.Orbiting);
            ParsekKSC.KscPoseResolution orbitPlain = KscPose(orbiting, 280.0, null, 0.0);
            ParsekKSC.KscPoseResolution orbitLoop = KscPose(orbiting, 280.0, FakeTurn(requested), -1000.0);
            Assert.True((orbitLoop.WorldPos - orbitPlain.WorldPos).magnitude < 1e-6);
            Assert.Empty(requested);
        }

        // ---- the flight positioner wiring (live-only path) ----

        [Fact]
        public void FlightPositioner_TurnsLoopTailsAndHoldsTheTurnedPoseBodyFixed()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string orbit = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Controllers", "ParsekFlightGhostPositioner.Orbit.cs"));

            Assert.Contains("planetTurnSeconds: planetTurnSeconds);", orbit);
            Assert.Contains("bool isLoopCycle = state != null && state.loopCycleIndex >= 0;", orbit);
            Assert.Contains("Vector3d worldPos = planetTurn.Apply(body.position, rawOrbitWorldPos);", orbit);
            // The Orbit re-apply would rebuild the unturned conic; a turned pose is held body-fixed.
            int turnedReturn = orbit.IndexOf("RegisterBodyFixedPoseEntry(ghost, body, worldPos, recordingId, ut);", StringComparison.Ordinal);
            int orbitEntry = orbit.IndexOf("mode = ParsekFlight.GhostPosMode.Orbit,", StringComparison.Ordinal);
            Assert.True(turnedReturn > 0 && turnedReturn < orbitEntry);

            string continuity = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Controllers", "ParsekFlightGhostPositioner.OrbitTailContinuity.cs"));
            Assert.Contains("Vector3d orbitAtAnchor = planetTurn.Apply(", continuity);
        }
    }
}
