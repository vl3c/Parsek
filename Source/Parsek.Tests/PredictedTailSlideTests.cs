using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling L5: when the predicted tail starts more than
    /// <see cref="GhostPlaybackEngine.PredictedOrbitTailBridgeMaxGapSeconds"/> after the last
    /// recorded point, the ghost slides from that point to the tail start (no freeze, no jump,
    /// no backward propagation of a possibly post-burn orbit, no flames from a stale velocity).
    /// The flight mesh, the flight-map and Tracking Station markers and the KSC ghost share the
    /// pure slide decision and math; the live body reads are in-game only.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTailSlideTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private const double R = 700000.0;
        private readonly List<string> logLines = new List<string>();

        public PredictedTailSlideTests()
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

        /// <summary>Recorded points to UT 200, a predicted tail from 290 (gap 90 s) to 400.</summary>
        private static Recording CrashWithLateTail()
        {
            var rec = new Recording
            {
                RecordingId = "late-tail",
                VesselName = "LateTail",
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitEndUT = 400.0,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 190.0, latitude = 1.0, longitude = 2.0, altitude = 59000.0,
                bodyName = "Kerbin", rotation = Quaternion.identity
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200.0, latitude = 3.0, longitude = 4.0, altitude = 60000.0,
                bodyName = "Kerbin", rotation = Quaternion.identity
            });
            rec.OrbitSegments.Add(KerbinCircular(290.0, 400.0));
            return rec;
        }

        // ---- the decision ----

        [Fact]
        public void TryFindPredictedOrbitTailSlide_InsideALongGap_SlidesToTheTailStart()
        {
            Recording rec = CrashWithLateTail();

            Assert.True(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(
                rec, 250.0, out OrbitSegment seg, out int idx, out double lastUT));
            Assert.Equal(0, idx);
            Assert.Equal(290.0, seg.startUT, 6);
            Assert.Equal(200.0, lastUT, 6);
            Assert.False(GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(rec, 250.0, out _, out _));
            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(rec, 250.0));
        }

        [Fact]
        public void TryFindPredictedOrbitTailSlide_OutsideTheGap_False()
        {
            Recording rec = CrashWithLateTail();

            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 195.0, out _, out _, out _));
            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 200.0, out _, out _, out _));
            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 290.0, out _, out _, out _));
            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 350.0, out _, out _, out _));
        }

        [Fact]
        public void TryFindPredictedOrbitTailSlide_GapInsideTheBridgeCap_StaysABridge()
        {
            Recording rec = CrashWithLateTail();
            rec.OrbitSegments[0] = KerbinCircular(220.0, 400.0);

            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 210.0, out _, out _, out _));
            Assert.True(GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(rec, 210.0, out _, out _));
        }

        [Fact]
        public void TryFindPredictedOrbitTailSlide_NextSegmentRecorded_NoSlide()
        {
            Recording rec = CrashWithLateTail();
            rec.OrbitSegments[0] = KerbinCircular(290.0, 400.0, predicted: false);

            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 250.0, out _, out _, out _));
        }

        [Fact]
        public void TryFindPredictedOrbitTailSlide_LastPointInARelativeSection_NoSlide()
        {
            Recording rec = CrashWithLateTail();
            rec.TrackSections.Add(new TrackSection
            {
                startUT = 180.0,
                endUT = 200.0,
                referenceFrame = ReferenceFrame.Relative,
            });

            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 250.0, out _, out _, out _));
        }

        [Fact]
        public void TryFindPredictedOrbitTailSlide_TailAroundAnotherBody_NoSlide()
        {
            Recording rec = CrashWithLateTail();
            OrbitSegment seg = rec.OrbitSegments[0];
            seg.bodyName = "Mun";
            rec.OrbitSegments[0] = seg;

            Assert.False(GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(rec, 250.0, out _, out _, out _));
        }

        [Fact]
        public void IsInsidePointsOrTail_InsideTheSlideGap_True()
        {
            Recording rec = CrashWithLateTail();
            Assert.True(GhostPlaybackLogic.IsInsidePointsOrTail(rec, 250.0));
            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.None,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 250.0, suppressedIds: null));
        }

        [Fact]
        public void TryGetTailSlideAnchorPoints_PreviousPointTooFarBack_NoStartVelocity()
        {
            Recording rec = CrashWithLateTail();
            TrajectoryPoint early = rec.Points[0];
            early.ut = 100.0;
            rec.Points[0] = early;

            Assert.True(GhostPlaybackLogic.TryGetTailSlideAnchorPoints(
                rec, rec.OrbitSegments[0], out TrajectoryPoint last, out _, out bool hasPrev));
            Assert.Equal(200.0, last.ut, 6);
            Assert.False(hasPrev);
        }

        // ---- the math ----

        [Theory]
        [InlineData(200.0, 0.0)]
        [InlineData(245.0, 0.5)]
        [InlineData(290.0, 1.0)]
        [InlineData(150.0, 0.0)]
        [InlineData(400.0, 1.0)]
        public void ResolveTailSlideFraction_SpansTheGap(double ut, double expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ResolveTailSlideFraction(200.0, 290.0, ut), 9);
        }

        private static Vector3d OnCircle(double angleRad)
        {
            return new Vector3d(Math.Cos(angleRad), 0.0, Math.Sin(angleRad)) * R;
        }

        private static Vector3d CircleVelocity(double angleRad, double angularRate)
        {
            return new Vector3d(-Math.Sin(angleRad), 0.0, Math.Cos(angleRad)) * (R * angularRate);
        }

        [Fact]
        public void TryComputeTailSlideOffset_MatchesBothEnds()
        {
            Vector3d r0 = OnCircle(0.0);
            Vector3d r1 = OnCircle(0.5) * 0.95;
            Assert.True(GhostPlaybackLogic.TryComputeTailSlideOffset(
                r0, CircleVelocity(0.0, 0.001), true, r1, CircleVelocity(0.5, 0.001), true,
                90.0, 0.0, out Vector3d a));
            Assert.True(GhostPlaybackLogic.TryComputeTailSlideOffset(
                r0, CircleVelocity(0.0, 0.001), true, r1, CircleVelocity(0.5, 0.001), true,
                90.0, 1.0, out Vector3d b));
            Assert.True((a - r0).magnitude < 1e-6);
            Assert.True((b - r1).magnitude < 1e-6);
        }

        [Fact]
        public void TryComputeTailSlideOffset_ConsistentCircularMotion_FollowsTheCircle()
        {
            // A ghost on a circular orbit with matching endpoint velocities: the slide is the arc
            // itself (radius held, angle advancing at the orbit's rate), not a chord through it.
            double rate = 0.6 / 300.0;
            for (int k = 1; k < 10; k++)
            {
                double s = k / 10.0;
                Assert.True(GhostPlaybackLogic.TryComputeTailSlideOffset(
                    OnCircle(0.0), CircleVelocity(0.0, rate), true,
                    OnCircle(0.6), CircleVelocity(0.6, rate), true,
                    300.0, s, out Vector3d p));
                Assert.Equal(R, p.magnitude, 0);
                double angle = Math.Atan2(p.z, p.x);
                Assert.Equal(0.6 * s, angle, 6);
            }
        }

        [Fact]
        public void TryComputeTailSlideOffset_LongGapHalfAnOrbit_StaysOnTheRadius()
        {
            // A chord across half an orbit would pass through the planet; the slide keeps the
            // radius and goes the way the ghost was moving.
            double rate = Math.PI / 1200.0;
            Assert.True(GhostPlaybackLogic.TryComputeTailSlideOffset(
                OnCircle(0.0), CircleVelocity(0.0, rate), true,
                OnCircle(Math.PI - 1e-3), CircleVelocity(Math.PI - 1e-3, rate), true,
                1200.0, 0.5, out Vector3d mid));
            Assert.Equal(R, mid.magnitude, 0);
            Assert.True(mid.z > 0.9 * R, "midpoint " + mid + " is the arc's top, not the centre");
        }

        [Fact]
        public void TryComputeTailSlideOffset_MovingTheLongWay_TakesTheLongArc()
        {
            // Both ends move clockwise while the short arc is anticlockwise: the ghost went
            // round the long way, and the slide follows it.
            double rate = -(2.0 * Math.PI - 1.0) / 2000.0;
            Assert.True(GhostPlaybackLogic.TryComputeTailSlideOffset(
                OnCircle(0.0), CircleVelocity(0.0, rate), true,
                OnCircle(1.0), CircleVelocity(1.0, rate), true,
                2000.0, 0.5, out Vector3d mid));
            double angle = Math.Atan2(mid.z, mid.x);
            double expected = -(2.0 * Math.PI - 1.0) / 2.0;
            Assert.Equal(expected, angle, 2);
        }

        [Fact]
        public void TryComputeTailSlideOffset_Descending_RadiusNeverOvershootsEitherEnd()
        {
            // Inconsistent endpoint velocities (a fast plunge at the start, a climb at the end)
            // must not swing the radius outside [r1, r0].
            Vector3d r0 = OnCircle(0.0) * 1.1;
            Vector3d r1 = OnCircle(0.2) * 0.9;
            Vector3d v0 = new Vector3d(-5000.0, 0.0, 1500.0);
            Vector3d v1 = new Vector3d(800.0, 0.0, 300.0);
            double prevAngle = 0.0;
            for (int k = 0; k <= 50; k++)
            {
                double s = k / 50.0;
                Assert.True(GhostPlaybackLogic.TryComputeTailSlideOffset(
                    r0, v0, true, r1, v1, true, 120.0, s, out Vector3d p));
                Assert.InRange(p.magnitude, 0.9 * R - 1e-3, 1.1 * R + 1e-3);
                double angle = Math.Atan2(p.z, p.x);
                Assert.True(angle >= prevAngle - 1e-9, "the slide never runs backwards");
                prevAngle = angle;
            }
        }

        [Fact]
        public void TryComputeTailSlideOffset_NoStartVelocity_StillMatchesTheEnds()
        {
            Vector3d r0 = OnCircle(0.0);
            Vector3d r1 = OnCircle(0.3);
            Assert.True(GhostPlaybackLogic.TryComputeTailSlideOffset(
                r0, Vector3d.zero, false, r1, CircleVelocity(0.3, 0.001), true, 60.0, 0.5, out Vector3d mid));
            Assert.Equal(R, mid.magnitude, 0);
        }

        [Fact]
        public void TryComputeTailSlideOffset_Degenerate_Declines()
        {
            Assert.False(GhostPlaybackLogic.TryComputeTailSlideOffset(
                Vector3d.zero, Vector3d.zero, false, OnCircle(0.3), Vector3d.zero, false, 60.0, 0.5, out _));
            Assert.False(GhostPlaybackLogic.TryComputeTailSlideOffset(
                OnCircle(0.0), Vector3d.zero, false, OnCircle(0.3), Vector3d.zero, false, 0.0, 0.5, out _));
            // Opposite directions with no velocity to choose the plane.
            Assert.False(GhostPlaybackLogic.TryComputeTailSlideOffset(
                OnCircle(0.0), Vector3d.zero, false, OnCircle(Math.PI), Vector3d.zero, false, 60.0, 0.5, out _));
        }

        [Fact]
        public void MonotoneHermite_ClampsSlopesAgainstTheSecant()
        {
            // A slope against the secant is zeroed: the curve never dips below the start.
            for (int k = 0; k <= 20; k++)
            {
                double v = GhostPlaybackLogic.MonotoneHermite(0.0, 1.0, -50.0, 50.0, k / 20.0);
                Assert.InRange(v, -1e-12, 1.0 + 1e-12);
            }
        }

        // ---- the KSC ghost ----

        private static ParsekKSC.KscSurfaceLookup FakeSurface()
        {
            // A body-fixed point maps onto the 700 km sphere at its longitude.
            return (string bodyName, double lat, double lon, double alt,
                out Vector3d worldPos, out Quaternion bodyRot) =>
            {
                bodyRot = Quaternion.identity;
                double l = lon * Math.PI / 180.0;
                worldPos = FakeBodyPos + new Vector3d(Math.Cos(l), 0.0, Math.Sin(l)) * R;
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

        private static ParsekKSC.KscPoseResolution KscPoseAt(Recording rec, double ut)
        {
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;
            Assert.True(ParsekKSC.TryInterpolateKscPlaybackPose(
                rec, ref cachedIndex, ref cachedKey, ut,
                FakeSurface(), NoAnchor, out ParsekKSC.KscPoseResolution pose, FakeBody()));
            return pose;
        }

        [Fact]
        public void KscPose_InsideTheGap_SlidesFromTheLastPointToTheTailStart()
        {
            Recording rec = CrashWithLateTail();
            Vector3d lastWorld = FakeBodyPos + new Vector3d(Math.Cos(4.0 * Math.PI / 180.0), 0.0,
                Math.Sin(4.0 * Math.PI / 180.0)) * R;
            Assert.True(GhostPlaybackLogic.TryComputeTailWorldOffset(
                rec.OrbitSegments[0], KerbinMu, 290.0, out Vector3d startOffset, out _));
            Vector3d tailStartWorld = FakeBodyPos + startOffset;

            ParsekKSC.KscPoseResolution first = KscPoseAt(rec, 200.01);
            ParsekKSC.KscPoseResolution mid = KscPoseAt(rec, 245.0);
            ParsekKSC.KscPoseResolution end = KscPoseAt(rec, 289.99);

            Assert.Equal(ParsekKSC.KscOrbitTailSlideBranch, mid.Branch);
            Assert.True((first.WorldPos - lastWorld).magnitude < 200.0,
                "first gap frame at " + first.WorldPos + " vs last point " + lastWorld);
            Assert.True((end.WorldPos - tailStartWorld).magnitude < 200.0,
                "last gap frame at " + end.WorldPos + " vs tail start " + tailStartWorld);
            Assert.True((mid.WorldPos - lastWorld).magnitude > 1000.0, "the ghost moves across the gap");
            Assert.True((mid.WorldPos - tailStartWorld).magnitude > 1000.0, "the ghost moves across the gap");
            Assert.Equal(R, (mid.WorldPos - FakeBodyPos).magnitude, 0);
            Assert.Contains(logLines, l => l.Contains("[KSCGhost]") && l.Contains("KSC predicted tail slide"));
        }

        [Fact]
        public void KscPose_PastTheTailStart_RidesTheTailAgain()
        {
            Recording rec = CrashWithLateTail();
            ParsekKSC.KscPoseResolution pose = KscPoseAt(rec, 300.0);
            Assert.Equal(ParsekKSC.KscOrbitTailBranch, pose.Branch);
        }

        // ---- the flight positioner wiring (live-only path) ----

        [Fact]
        public void FlightPositioner_RoutesTheGapToTheSlide_AndCarriesNoVelocity()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string orbit = System.IO.File.ReadAllText(System.IO.Path.Combine(
                root, "Source", "Parsek", "Controllers", "ParsekFlightGhostPositioner.Orbit.cs"));
            int entry = orbit.IndexOf("void IGhostPositioner.PositionFromOrbit", StringComparison.Ordinal);
            int slide = orbit.IndexOf("PositionGhostOnPredictedTailSlide(", entry, StringComparison.Ordinal);
            int fallback = orbit.IndexOf("for (int i = 0; i < traj.OrbitSegments.Count; i++)", entry, StringComparison.Ordinal);
            Assert.True(entry >= 0 && slide > entry && slide < fallback,
                "PositionFromOrbit must route a slide gap before the covering-segment fallback");

            int body = orbit.IndexOf("private void PositionGhostOnPredictedTailSlide(", StringComparison.Ordinal);
            Assert.True(body >= 0);
            string slideBody = orbit.Substring(body, 4000);
            Assert.Contains("seg.startUT,", slideBody);
            Assert.Contains("Vector3.zero, seg.bodyName, body.GetAltitude(worldPos)", slideBody);
        }
    }
}
