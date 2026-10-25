using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure core of the Real Spawn Control time jump that keeps the player's group beside
    /// the ghost (docs/dev/design-realspawn-time-jump-move.md; plan Step 1.1).
    /// </summary>
    [Collection("Sequential")]
    public class TimeJumpPlacementTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRadius = 600000.0;
        private readonly List<string> logLines = new List<string>();

        public TimeJumpPlacementTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            TimeJumpManager.ResetPlacementForTesting();
        }

        public void Dispose()
        {
            TimeJumpManager.ResetPlacementForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------- helpers ----------------

        private static void CircularState(double radius, double phaseRad, double incRad,
            out Vector3d r, out Vector3d v)
        {
            double speed = Math.Sqrt(KerbinMu / radius);
            var rp = new Vector3d(radius * Math.Cos(phaseRad), radius * Math.Sin(phaseRad), 0.0);
            var vp = new Vector3d(-speed * Math.Sin(phaseRad), speed * Math.Cos(phaseRad), 0.0);
            r = RotateX(rp, incRad);
            v = RotateX(vp, incRad);
        }

        private static Vector3d RotateX(Vector3d p, double a)
        {
            return new Vector3d(p.x, p.y * Math.Cos(a) - p.z * Math.Sin(a), p.y * Math.Sin(a) + p.z * Math.Cos(a));
        }

        private static void Propagate(Vector3d r, Vector3d v, double fromUT, double toUT,
            out Vector3d r1, out Vector3d v1)
        {
            Assert.True(TwoBodyOrbit.TryCreate(r, v, KerbinMu, fromUT, out TwoBodyOrbit orbit));
            orbit.GetStateAtUT(toUT, out r1, out v1);
        }

        /// <summary>A reference at periapsis of an orbit with eccentricity e and periapsis radius rp.</summary>
        private static void EccentricState(double rp, double e, out Vector3d r, out Vector3d v)
        {
            double speed = Math.Sqrt(KerbinMu * (1.0 + e) / rp);
            r = RotateX(new Vector3d(rp, 0.0, 0.0), 0.3);
            v = RotateX(new Vector3d(0.0, speed, 0.0), 0.3);
        }

        private static void AssertVec(Vector3d expected, Vector3d actual, double tol, string label)
        {
            Assert.True((expected - actual).magnitude <= tol,
                label + ": expected " + expected + " got " + actual + " diff=" + (expected - actual).magnitude);
        }

        // ---------------- frame and map ----------------

        [Fact]
        public void TryBuildFrame_IsOrthonormalAndRotatesAtTheOrbitRate()
        {
            CircularState(700000.0, 0.4, 0.1, out Vector3d r, out Vector3d v);
            Assert.True(TimeJumpPlacement.TryBuildFrame(r, v, out LocalFrame f));
            Assert.Equal(1.0, f.ur.magnitude, 9);
            Assert.Equal(1.0, f.ut.magnitude, 9);
            Assert.Equal(1.0, f.un.magnitude, 9);
            Assert.Equal(0.0, Vector3d.Dot(f.ur, f.ut), 9);
            Assert.Equal(0.0, Vector3d.Dot(f.ur, f.un), 9);
            Assert.True(Vector3d.Dot(f.ut, v) > 0.0, "along-track points along the motion");
            Assert.Equal(Math.Sqrt(KerbinMu / Math.Pow(700000.0, 3)), f.omega.magnitude, 12);
        }

        [Fact]
        public void TryBuildFrame_RefusesRadialOrZeroVelocity()
        {
            var r = new Vector3d(700000.0, 0.0, 0.0);
            Assert.False(TimeJumpPlacement.TryBuildFrame(r, new Vector3d(100.0, 0.0, 0.0), out _));
            Assert.False(TimeJumpPlacement.TryBuildFrame(r, Vector3d.zero, out _));
            Assert.False(TimeJumpPlacement.TryBuildFrame(Vector3d.zero, new Vector3d(0, 2000, 0), out _));
            Assert.False(TimeJumpPlacement.TryBuildFrame(
                new Vector3d(double.NaN, 0, 0), new Vector3d(0, 2000, 0), out _));
        }

        // catches: a map that does not give back the T0 local components at T1, for any
        // jump length or orbit shape (the core promise of the move).
        [Theory]
        [InlineData(0.0, 10.0)]
        [InlineData(0.0, 600.0)]
        [InlineData(0.0, 1200.0)]
        [InlineData(0.0, 21600.0)]
        [InlineData(0.05, 10.0)]
        [InlineData(0.05, 600.0)]
        [InlineData(0.05, 1200.0)]
        [InlineData(0.05, 21600.0)]
        public void CaptureThenPlace_GivesBackTheLocalComponentsAtT1(double ecc, double jump)
        {
            Vector3d rRef0, vRef0;
            if (ecc == 0.0)
                CircularState(700000.0, 0.2, 0.05, out rRef0, out vRef0);
            else
                EccentricState(700000.0, ecc, out rRef0, out vRef0);
            const double t0 = 1000.0;
            double t1 = t0 + jump;
            Propagate(rRef0, vRef0, t0, t1, out Vector3d rRef1, out Vector3d vRef1);
            Assert.True(TimeJumpPlacement.TryBuildFrame(rRef0, vRef0, out LocalFrame f0));
            Assert.True(TimeJumpPlacement.TryBuildFrame(rRef1, vRef1, out LocalFrame f1));

            // Along-track, radial and normal offsets, with a relative velocity.
            Vector3d dr = f0.ut * 140.0 + f0.ur * -12.0 + f0.un * 7.5;
            Vector3d dv = f0.ut * 0.3 + f0.ur * -0.1 + f0.un * 0.05;
            Vector3d r = rRef0 + dr;
            Vector3d v = vRef0 + dv + Vector3d.Cross(f0.omega, dr);

            RelativeState s0 = TimeJumpPlacement.Capture(f0, rRef0, vRef0, r, v);
            AssertVec(new Vector3d(-12.0, 140.0, 7.5), s0.Offset, 1e-6, "offset at T0");
            AssertVec(new Vector3d(-0.1, 0.3, 0.05), s0.Velocity, 1e-6, "velocity at T0");

            TimeJumpPlacement.Place(f1, rRef1, vRef1, s0, out Vector3d rPlaced, out Vector3d vPlaced);
            RelativeState s1 = TimeJumpPlacement.Capture(f1, rRef1, vRef1, rPlaced, vPlaced);
            AssertVec(s0.Offset, s1.Offset, 1e-6, "offset at T1");
            AssertVec(s0.Velocity, s1.Velocity, 1e-6, "velocity at T1");
            // The distance to the reference is kept (the ghost stays the same distance away).
            Assert.Equal(dr.magnitude, (rPlaced - rRef1).magnitude, 6);
        }

        // catches: a frame whose rotation term is wrong: a co-orbital deputy (same circular
        // orbit, leading phase) has zero rotating-frame velocity and keeps its offset under
        // its own natural motion.
        [Fact]
        public void CoOrbitalDeputy_HasNoRelativeVelocity_AndNaturalMotionKeepsItsOffset()
        {
            CircularState(700000.0, 0.2, 0.05, out Vector3d rRef0, out Vector3d vRef0);
            CircularState(700000.0, 0.2 + 2.0e-4, 0.05, out Vector3d r0, out Vector3d v0);
            Assert.True(TimeJumpPlacement.TryBuildFrame(rRef0, vRef0, out LocalFrame f0));
            RelativeState s0 = TimeJumpPlacement.Capture(f0, rRef0, vRef0, r0, v0);
            Assert.True(s0.Velocity.magnitude < 1e-6, "co-orbital deputy rel velocity " + s0.Velocity.magnitude);
            Assert.InRange(s0.Offset.y, 139.0, 141.0);

            Propagate(rRef0, vRef0, 0.0, 1200.0, out Vector3d rRef1, out Vector3d vRef1);
            Propagate(r0, v0, 0.0, 1200.0, out Vector3d r1, out Vector3d v1);
            Assert.True(TimeJumpPlacement.TryBuildFrame(rRef1, vRef1, out LocalFrame f1));
            RelativeState s1 = TimeJumpPlacement.Capture(f1, rRef1, vRef1, r1, v1);
            AssertVec(s0.Offset, s1.Offset, 0.05, "natural co-orbital offset");
        }

        // ---------------- group ----------------

        [Theory]
        [InlineData(249.0, 1.9, false, false, true, true)]
        [InlineData(250.0, 2.0, false, false, true, true)]
        [InlineData(251.0, 1.9, false, false, true, false)]
        [InlineData(249.0, 2.1, false, false, true, false)]
        [InlineData(20.0, 0.1, true, false, true, false)]   // landed never joins
        [InlineData(20.0, 0.1, false, true, true, false)]   // ghost map vessel never joins
        [InlineData(20.0, 0.1, false, false, false, false)] // other body
        public void IsGroupMember_UsesTheSpawnRadiusAndSpeedGate(
            double distance, double speed, bool surface, bool ghostMap, bool sameBody, bool expected)
        {
            Assert.Equal(expected,
                TimeJumpPlacement.IsGroupMember(distance, speed, surface, ghostMap, sameBody));
        }

        [Theory]
        [InlineData(Vessel.Situations.ORBITING, true)]
        [InlineData(Vessel.Situations.SUB_ORBITAL, true)]
        [InlineData(Vessel.Situations.ESCAPING, true)]
        [InlineData(Vessel.Situations.FLYING, true)]
        [InlineData(Vessel.Situations.LANDED, false)]
        [InlineData(Vessel.Situations.SPLASHED, false)]
        [InlineData(Vessel.Situations.PRELAUNCH, false)]
        public void IsOrbitalPlayerVessel_KeepsSurfaceVesselsOnTheOldJump(
            Vessel.Situations situation, bool expected)
        {
            Assert.Equal(expected, TimeJumpPlacement.IsOrbitalPlayerVessel(situation));
        }

        // ---------------- burns ----------------

        private static double BurnAcross(double impulseAlongTrack, double planeAngleRad, double jump)
        {
            CircularState(700000.0, 0.0, 0.05, out Vector3d r0, out Vector3d v0);
            const double t0 = 0.0;
            Propagate(r0, v0, t0, t0 + 10.0, out Vector3d rb, out Vector3d vb);
            Vector3d vAfter = vb + vb.normalized * impulseAlongTrack;
            if (planeAngleRad != 0.0)
            {
                // Rotate the velocity about the radial axis: a pure plane change.
                Vector3d k = rb.normalized;
                vAfter = vAfter * Math.Cos(planeAngleRad)
                    + Vector3d.Cross(k, vAfter) * Math.Sin(planeAngleRad)
                    + k * Vector3d.Dot(k, vAfter) * (1.0 - Math.Cos(planeAngleRad));
            }
            Propagate(rb, vAfter, t0 + 10.0, t0 + jump, out Vector3d r1, out Vector3d v1);
            return TimeJumpPlacement.EstimateBurnDeltaV(r0, v0, r1, v1, KerbinMu);
        }

        [Fact]
        public void EstimateBurn_SmallTrimAcrossALongJump_ReadsTheTrimNotTheDrift()
        {
            double dv = BurnAcross(0.8, 0.0, 1200.0);
            Assert.InRange(dv, 0.75, 0.85);
        }

        [Fact]
        public void EstimateBurn_OrbitRaise_Refuses()
        {
            double dv = BurnAcross(30.0, 0.0, 1200.0);
            Assert.InRange(dv, 28.0, 32.0);
            Assert.Equal(MoveRefusal.Burns, TimeJumpPlacement.EvaluateGates(BaseInputs(dv)).Refusal);
        }

        [Theory]
        [InlineData(1.99, true)]
        [InlineData(2.01, false)]
        public void EstimateBurn_PlaneChange_AtTheEdge(double dvPlane, bool passes)
        {
            double speed = Math.Sqrt(KerbinMu / 700000.0);
            double angle = 2.0 * Math.Asin(dvPlane / (2.0 * speed));
            double dv = BurnAcross(0.0, angle, 1200.0);
            Assert.InRange(dv, dvPlane - 0.005, dvPlane + 0.005);
            Assert.Equal(passes, TimeJumpPlacement.EvaluateGates(BaseInputs(dv)).Passed);
        }

        [Fact]
        public void EstimateBurn_PureCoast_IsZero()
        {
            Assert.True(BurnAcross(0.0, 0.0, 1200.0) < 1e-3);
            EccentricState(700000.0, 0.05, out Vector3d r0, out Vector3d v0);
            Propagate(r0, v0, 0.0, 3000.0, out Vector3d r1, out Vector3d v1);
            Assert.True(TimeJumpPlacement.EstimateBurnDeltaV(r0, v0, r1, v1, KerbinMu) < 1e-3);
        }

        [Fact]
        public void EstimateBurn_BadInput_RefusesAsInfinite()
        {
            var r = new Vector3d(700000, 0, 0);
            var v = new Vector3d(0, 2200, 0);
            Assert.True(double.IsPositiveInfinity(TimeJumpPlacement.EstimateBurnDeltaV(r, v, r, v, 0.0)));
            Assert.True(double.IsPositiveInfinity(TimeJumpPlacement.EstimateBurnDeltaV(
                new Vector3d(double.NaN, 0, 0), v, r, v, KerbinMu)));
        }

        // ---------------- gates ----------------

        private static MoveGateInputs BaseInputs(double burn = 0.0)
        {
            return new MoveGateInputs
            {
                ActiveBody = "Kerbin",
                ReferenceBodyT0 = "Kerbin",
                ReferenceBodyT1 = "Kerbin",
                BurnDeltaV = burn,
            };
        }

        [Fact]
        public void EvaluateGates_PassesAClearCoast()
        {
            MoveGateVerdict v = TimeJumpPlacement.EvaluateGates(BaseInputs());
            Assert.True(v.Passed);
            Assert.Equal(string.Empty, v.Detail);
        }

        [Fact]
        public void EvaluateGates_EachRefusal_WithItsDetail()
        {
            MoveGateInputs i = BaseInputs();
            i.ActiveInsideAtmosphere = true;
            Assert.Equal("who=active", TimeJumpPlacement.EvaluateGates(i).Detail);

            i = BaseInputs();
            i.MemberInsideAtmosphere = true;
            i.MemberInsideAtmosphereName = "Probe";
            MoveGateVerdict v = TimeJumpPlacement.EvaluateGates(i);
            Assert.Equal(MoveRefusal.Atmosphere, v.Refusal);
            Assert.Equal("who=member name=Probe", v.Detail);

            i = BaseInputs();
            i.ReferenceInsideAtmosphereT1 = true;
            Assert.Equal("who=reference-t1", TimeJumpPlacement.EvaluateGates(i).Detail);

            i = BaseInputs();
            i.ReferenceBodyT1 = "Mun";
            v = TimeJumpPlacement.EvaluateGates(i);
            Assert.Equal(MoveRefusal.BodyChange, v.Refusal);
            Assert.Equal("active=Kerbin refT0=Kerbin refT1=Mun", v.Detail);

            i = BaseInputs();
            i.ActiveBody = "Mun";
            Assert.Equal(MoveRefusal.BodyChange, TimeJumpPlacement.EvaluateGates(i).Refusal);

            i = BaseInputs(2.5);
            v = TimeJumpPlacement.EvaluateGates(i);
            Assert.Equal(MoveRefusal.Burns, v.Refusal);
            Assert.Equal("dv=2.50 max=2.0", v.Detail);

            i = BaseInputs(double.NaN);
            Assert.Equal(MoveRefusal.Burns, TimeJumpPlacement.EvaluateGates(i).Refusal);

            i = BaseInputs();
            i.FrameDegenerate = true;
            Assert.Equal(MoveRefusal.Degenerate, TimeJumpPlacement.EvaluateGates(i).Refusal);

            i = BaseInputs();
            i.DestinationTaken = true;
            i.DestinationBlocker = "Station";
            i.DestinationGap = -3.25;
            v = TimeJumpPlacement.EvaluateGates(i);
            Assert.Equal(MoveRefusal.DestinationTaken, v.Refusal);
            Assert.Equal("blocker=Station gap=-3.3", v.Detail);
        }

        [Fact]
        public void EvaluateGates_FirstFailureWins_InTheDesignOrder()
        {
            MoveGateInputs i = BaseInputs(10.0);
            i.ReferenceInsideAtmosphereT0 = true;
            i.ReferenceBodyT1 = "Mun";
            i.FrameDegenerate = true;
            i.DestinationTaken = true;
            Assert.Equal(MoveRefusal.Atmosphere, TimeJumpPlacement.EvaluateGates(i).Refusal);
            i.ReferenceInsideAtmosphereT0 = false;
            Assert.Equal(MoveRefusal.BodyChange, TimeJumpPlacement.EvaluateGates(i).Refusal);
            i.ReferenceBodyT1 = "Kerbin";
            Assert.Equal(MoveRefusal.Burns, TimeJumpPlacement.EvaluateGates(i).Refusal);
            i.BurnDeltaV = 0.0;
            Assert.Equal(MoveRefusal.Degenerate, TimeJumpPlacement.EvaluateGates(i).Refusal);
            i.FrameDegenerate = false;
            Assert.Equal(MoveRefusal.DestinationTaken, TimeJumpPlacement.EvaluateGates(i).Refusal);
        }

        [Theory]
        [InlineData(true, 69999.0, 70000.0, true)]
        [InlineData(true, 70001.0, 70000.0, false)]
        [InlineData(false, 100.0, 0.0, false)]
        [InlineData(true, double.NaN, 70000.0, true)]
        public void IsInsideAtmosphere_BelowTheTop_AndUnknownRefuses(
            bool atmo, double alt, double depth, bool expected)
        {
            Assert.Equal(expected, TimeJumpPlacement.IsInsideAtmosphere(atmo, alt, depth));
        }

        [Fact]
        public void DestinationConflict_ReportsTheWorstOverlap()
        {
            var placed = new List<PlacedBody>
            {
                new PlacedBody { Position = new Vector3d(0, 0, 0), Radius = 5, Name = "A" },
                new PlacedBody { Position = new Vector3d(30, 0, 0), Radius = 3, Name = "Probe" },
            };
            var clear = new List<PlacedBody>
            {
                new PlacedBody { Position = new Vector3d(100, 0, 0), Radius = 10, Name = "Far" },
            };
            Assert.False(TimeJumpPlacement.TryFindDestinationConflict(placed, clear, 5.0, out _, out double gap0));
            Assert.True(double.IsPositiveInfinity(gap0));

            var others = new List<PlacedBody>
            {
                new PlacedBody { Position = new Vector3d(44, 0, 0), Radius = 4, Name = "Near" },
                new PlacedBody { Position = new Vector3d(0, 8, 0), Radius = 2, Name = "Touching" },
            };
            Assert.True(TimeJumpPlacement.TryFindDestinationConflict(placed, others, 5.0,
                out string blocker, out double gap));
            Assert.Equal("Touching", blocker);
            Assert.Equal(8.0 - 5.0 - 2.0 - 5.0, gap, 9);
        }

        // ---------------- spawn room slide ----------------

        [Fact]
        public void Slide_NoOverlap_IsZero()
        {
            SlideResult s = TimeJumpPlacement.ComputeSpawnRoomSlide(140.0, shift => false);
            Assert.True(s.Cleared);
            Assert.Equal(0.0, s.ShiftMeters);
            Assert.Equal(1, s.StepsTried);
        }

        [Fact]
        public void Slide_SmallestClearingShiftPlusMargin_AwayFromTheReference()
        {
            // Overlapping while the shift is under 12 m away from the reference.
            SlideResult ahead = TimeJumpPlacement.ComputeSpawnRoomSlide(140.0, shift => shift < 12.0);
            Assert.True(ahead.Cleared);
            Assert.Equal(17.0, ahead.ShiftMeters);

            // Zero along-track offset slides behind the reference.
            SlideResult behind = TimeJumpPlacement.ComputeSpawnRoomSlide(0.0, shift => shift > -12.0);
            Assert.True(behind.Cleared);
            Assert.Equal(-17.0, behind.ShiftMeters);
        }

        [Fact]
        public void Slide_MarginLandingInASecondOverlap_FallsBackToTheBareClear()
        {
            SlideResult s = TimeJumpPlacement.ComputeSpawnRoomSlide(-10.0,
                shift => shift > -12.0 || (shift < -15.0 && shift > -30.0));
            Assert.True(s.Cleared);
            Assert.Equal(-12.0, s.ShiftMeters);
        }

        [Fact]
        public void Slide_PastTheCap_DoesNotClear()
        {
            SlideResult s = TimeJumpPlacement.ComputeSpawnRoomSlide(50.0, shift => true);
            Assert.False(s.Cleared);
            Assert.Equal(251, s.StepsTried);
        }

        // ---------------- attitude ----------------

        private static float AngleDeg(Quaternion a, Quaternion b)
        {
            float dot = Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            if (dot > 1f) dot = 1f;
            return (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }

        [Fact]
        public void Attitude_EncodeThenDecodeAtTheSameState_GivesItBack()
        {
            CircularState(700000.0, 0.7, 0.2, out Vector3d r, out Vector3d v);
            Quaternion q = TrajectoryMath.PureNormalize(new Quaternion(0.2f, -0.4f, 0.1f, 0.85f));
            Quaternion ofr = TimeJumpPlacement.EncodeAttitude(q, r, v);
            Quaternion back = TimeJumpPlacement.DecodeAttitude(ofr, r, v);
            Assert.True(AngleDeg(q, back) < 0.05f, "round trip error " + AngleDeg(q, back));
        }

        // catches: an attitude carried as a world quaternion (or with a Zup rotation applied
        // to it): rotating the whole world between encode and decode must rotate the decoded
        // attitude by exactly that rotation, so the vessel keeps its pose in the frame.
        [Fact]
        public void Attitude_AWorldRotationBetweenEncodeAndDecode_Cancels()
        {
            CircularState(700000.0, 0.7, 0.2, out Vector3d rZup, out Vector3d vZup);
            Quaternion q = TrajectoryMath.PureNormalize(new Quaternion(0.2f, -0.4f, 0.1f, 0.85f));
            Quaternion ofr = TimeJumpPlacement.EncodeAttitude(q, rZup, vZup);

            // A world rotation W (about world +y by 40 degrees), applied to the WORLD
            // vectors; the reference's Zup vectors are its swizzle.
            double half = 20.0 * Math.PI / 180.0;
            Quaternion w = new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
            Vector3d rWorld = TrajectoryMath.SwizzleZupBodyRelativeToWorld(rZup);
            Vector3d vWorld = TrajectoryMath.SwizzleZupBodyRelativeToWorld(vZup);
            Vector3d r1Zup = TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                (Vector3d)TrajectoryMath.PureRotateVector(w, (Vector3)rWorld));
            Vector3d v1Zup = TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                (Vector3d)TrajectoryMath.PureRotateVector(w, (Vector3)vWorld));

            Quaternion decoded = TimeJumpPlacement.DecodeAttitude(ofr, r1Zup, v1Zup);
            Quaternion expected = TrajectoryMath.PureNormalize(TrajectoryMath.PureMultiply(w, q));
            Assert.True(AngleDeg(expected, decoded) < 0.1f, "world rotation error " + AngleDeg(expected, decoded));
        }

        // ---------------- log lines and small helpers ----------------

        [Fact]
        public void FormatPlacementLines_AreInvariantAndCarryThePinnedTokens()
        {
            var s0 = new RelativeState { Offset = new Vector3d(-1.26, 140.04, 0.5), Velocity = new Vector3d(0.3, 0.4, 0) };
            var s1 = new RelativeState { Offset = new Vector3d(-1.26, 140.04, 0.5), Velocity = new Vector3d(0.3, 0.4, 0) };
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal(
                    "Placed vessel: pid=42 name=Kerbal X offsetT0=(-1.3,140.0,0.5) offsetT1=(-1.3,140.0,0.5) dvRel=0.50",
                    TimeJumpPlacement.FormatPlacedVesselLine(42u, "Kerbal X", s0, s1));
                Assert.Equal(
                    "Placement jump: ref=rec-1 kind=Leaves T0=428.0 T1=706.2 placeUT=706.21 group=2 moved=1,2"
                    + " refGapToDrawn=0.4m slide=0.0m recorder=OnRailsRecapture bgRecaptured=0",
                    TimeJumpPlacement.FormatPlacementSummary("rec-1", "Leaves", 428.0, 706.19, 706.21,
                        new List<uint> { 1u, 2u }, 0.4, 0.0, "OnRailsRecapture", 0));
                Assert.Equal(
                    "Placement unpacked: pid=7 name=P ut=707.00 refDistanceT0=140.0m refDistanceNow=141.5m refDrift=1.5m"
                    + " toActiveT0=n/am toActiveNow=23.0m toActiveDrift=n/am attitudeErrDeg=0.3 reasserted=false",
                    TimeJumpPlacement.FormatUnpackedLine(7u, "P", 707.0, 140.0, 141.5, double.NaN, 23.0, 0.3, false));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
            Assert.Contains("moved=none", TimeJumpPlacement.FormatPlacementSummary(
                null, null, 0, 0, 0, null, double.NaN, 0, null, 0));
        }

        [Theory]
        [InlineData(721.19, 428.0, 706.19)]
        [InlineData(440.0, 428.0, 440.0)] // inside the lead window: the event itself
        public void ResolveJumpLeadTarget_IsApplyJumpLeadWithoutTheLog(double eventUT, double now, double expected)
        {
            logLines.Clear();
            Assert.Equal(expected, TimeJumpManager.ResolveJumpLeadTarget(eventUT, now), 9);
            Assert.Empty(logLines);
            Assert.Equal(expected, TimeJumpManager.ApplyJumpLead(eventUT, now), 9);
            Assert.Contains(logLines, l => l.Contains("[TimeJump]") && l.Contains("ApplyJumpLead:"));
        }

        [Theory]
        [InlineData(10, 1.0f, 10, 1.0f, false)]
        [InlineData(10, 1.0f, 11, 1.0f, false)] // no fixed update yet
        [InlineData(10, 1.0f, 10, 1.02f, false)] // same frame
        [InlineData(10, 1.0f, 11, 1.02f, true)]
        public void IsPlacementDue_WaitsForANewFrameAfterAFixedUpdate(
            int armedFrame, float armedFixed, int frame, float fixedTime, bool expected)
        {
            Assert.Equal(expected, TimeJumpManager.IsPlacementDue(armedFrame, armedFixed, frame, fixedTime));
        }

        [Fact]
        public void DropPendingPlacement_WithNothingArmed_IsSilent()
        {
            logLines.Clear();
            TimeJumpManager.DropPendingPlacement("scene-change");
            Assert.DoesNotContain(logLines, l => l.Contains("Placement jump dropped"));
            Assert.False(TimeJumpManager.HasPendingPlacement);
        }

        [Fact]
        public void TryFindLeavesSegment_RefusesWithoutACoveringSegment()
        {
            var rec = new Recording { RecordingId = "r" };
            Assert.False(TimeJumpReference.TryFindLeavesSegment(rec, 100.0, 200.0, out _, out _, out string reason));
            Assert.Equal("no-segment-at-t0", reason);
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 0.0, endUT = 150.0, bodyName = "Kerbin" });
            Assert.False(TimeJumpReference.TryFindLeavesSegment(rec, 100.0, 200.0, out _, out _, out reason));
            Assert.Equal("t1-past-segment", reason);
            Assert.False(TimeJumpReference.TryFindLeavesSegment(null, 100.0, 120.0, out _, out _, out reason));
            Assert.Equal("no-recording", reason);
        }
    }
}
