using System;
using System.Collections.Generic;
using Xunit;
using Parsek;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // Pure S4 arrival re-stitch math (docs/dev/plans/reaim-s4-arrival-restitch.md): the spin-axis
    // rotation carrying the recorded destination-relative SOI-entry bearing onto the re-aimed
    // transfer's actual entry bearing, and the matching descent-trigger congruence offset that keeps
    // the touchdown at the RECORDED body-fixed site (the ratified product decision).
    //
    // Frame convention under test: the .xzy-unswizzled WORLD frame, whose reference-plane normal
    // (= the zero-tilt body spin axis) is +Y, NOT +Z (the PR #1196 .z-vs-.y world-frame trap).
    // In-plane components are x and z; prograde-positive bearing = atan2(-z, x) degrees; positive
    // rotation is the prograde sense, shared by prograde orbits and prograde body spin.
    public class ArrivalRestitchTests
    {
        private const double DunaTrot = 65517.859375;

        private static double Wrap180(double deg)
        {
            double d = deg % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d <= -180.0) d += 360.0;
            return d;
        }

        // --- ComputeRestitchRotationDeg: the signed in-plane bearing difference ---

        [Fact]
        public void Rotation_IdenticalDirections_IsZero()
        {
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(1.0e7, 0.0, 0.0), new Vector3d(2.0e7, 0.0, 0.0),
                out double latRec, out double latNew);
            Assert.Equal(0.0, theta, 9);
            Assert.Equal(0.0, latRec, 9);
            Assert.Equal(0.0, latNew, 9);
        }

        [Fact]
        public void Rotation_QuarterTurnPrograde_IsPlus90()
        {
            // recorded along +x, new along -z: +90 deg prograde (about +Y) carries recorded onto
            // new (for a prograde orbit at +x the velocity points -z: Cross(r, v) = +Y).
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(1.0e7, 0.0, 0.0), new Vector3d(0.0, 0.0, -1.0e7),
                out _, out _);
            Assert.Equal(90.0, theta, 9);
        }

        [Fact]
        public void Rotation_QuarterTurnRetrograde_IsMinus90()
        {
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(0.0, 0.0, -1.0e7), new Vector3d(1.0e7, 0.0, 0.0),
                out _, out _);
            Assert.Equal(-90.0, theta, 9);
        }

        [Fact]
        public void Rotation_Antipodal_IsHalfTurn()
        {
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(1.0e7, 0.0, 0.0), new Vector3d(-1.0e7, 0.0, 0.0),
                out _, out _);
            Assert.Equal(180.0, Math.Abs(theta), 9);
        }

        [Fact]
        public void Rotation_MagnitudesIrrelevant_OnlyBearingsCount()
        {
            double a = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(1.0, 0.0, -1.0), new Vector3d(-5.0e8, 0.0, -5.0e8), out _, out _);
            Assert.Equal(90.0, a, 9);
        }

        [Fact]
        public void Rotation_ReportsOutOfPlaneLatitudes()
        {
            // 45 deg out of plane (+Y) on the new side; recorded in plane. The rotation is still
            // the in-plane bearing difference; the latitude is reported as the residual diagnostic.
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(1.0e7, 0.0, 0.0), new Vector3d(1.0e7, 1.0e7, 0.0),
                out double latRec, out double latNew);
            Assert.Equal(0.0, theta, 9);
            Assert.Equal(0.0, latRec, 9);
            Assert.Equal(45.0, latNew, 6);
        }

        [Theory]
        [InlineData(double.NaN, 0.0, 0.0)]
        [InlineData(0.0, double.NaN, 0.0)]
        [InlineData(0.0, 0.0, double.NaN)]
        public void Rotation_NaNComponent_Declines(double x, double y, double z)
        {
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(x == 0.0 ? 1.0e7 : x, y, z), new Vector3d(1.0e7, 0.0, 0.0),
                out _, out _);
            Assert.True(double.IsNaN(theta));
        }

        [Fact]
        public void Rotation_ZeroVector_Declines()
        {
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                Vector3d.zero, new Vector3d(1.0e7, 0.0, 0.0), out _, out _);
            Assert.True(double.IsNaN(theta));
        }

        [Fact]
        public void Rotation_NearPolarEntry_Declines()
        {
            // 80 deg latitude > MaxEntryLatitudeDeg (60): the in-plane bearing is ill-conditioned
            // and the profile is outside the Supported near-equatorial landing shape - fail closed.
            double y = 1.0e7 * Math.Tan(80.0 * Math.PI / 180.0);
            double theta = ArrivalRestitch.ComputeRestitchRotationDeg(
                new Vector3d(1.0e7, y, 0.0), new Vector3d(1.0e7, 0.0, 0.0), out _, out _);
            Assert.True(double.IsNaN(theta));
        }

        // --- VelocityBearingResidualDeg: the measure-first diagnostic ---

        [Fact]
        public void VelocityResidual_PerfectlyRotatedVelocity_IsZero()
        {
            // new velocity = recorded velocity rotated by +90 prograde: residual after the +90
            // re-stitch is 0.
            double r = ArrivalRestitch.VelocityBearingResidualDeg(
                new Vector3d(3000.0, 0.0, 0.0), new Vector3d(0.0, 0.0, -3000.0), 90.0);
            Assert.Equal(0.0, r, 9);
        }

        [Fact]
        public void VelocityResidual_ReportsTheKink()
        {
            // new velocity 30 deg past the rotated recorded velocity: residual +30 (bearing 120
            // in the prograde atan2(-z, x) sense = x-component cos120, z-component -sin120).
            double vx = 3000.0 * Math.Cos(120.0 * Math.PI / 180.0);
            double vz = -3000.0 * Math.Sin(120.0 * Math.PI / 180.0);
            double r = ArrivalRestitch.VelocityBearingResidualDeg(
                new Vector3d(3000.0, 0.0, 0.0), new Vector3d(vx, 0.0, vz), 90.0);
            Assert.Equal(30.0, r, 6);
        }

        // --- SiteAlignOffsetSeconds: theta -> body-rotation wait ---

        [Fact]
        public void Offset_PositiveRotation_IsProportionalWait()
        {
            // +90 deg -> a quarter rotation period.
            double s = ArrivalRestitch.SiteAlignOffsetSeconds(90.0, DunaTrot);
            Assert.Equal(DunaTrot / 4.0, s, 6);
        }

        [Fact]
        public void Offset_NegativeRotation_WrapsIntoForwardWait()
        {
            // -90 deg = the site must rotate 270 deg further (the offset congruence is forward-only).
            double s = ArrivalRestitch.SiteAlignOffsetSeconds(-90.0, DunaTrot);
            Assert.Equal(0.75 * DunaTrot, s, 6);
        }

        [Fact]
        public void Offset_ZeroRotation_IsZero()
        {
            Assert.Equal(0.0, ArrivalRestitch.SiteAlignOffsetSeconds(0.0, DunaTrot), 9);
        }

        [Theory]
        [InlineData(double.NaN, DunaTrot)]
        [InlineData(45.0, double.NaN)]
        [InlineData(45.0, 0.0)]
        [InlineData(45.0, -100.0)]
        [InlineData(double.PositiveInfinity, DunaTrot)]
        public void Offset_DegenerateInputs_FallBackToZero(double deg, double trot)
        {
            Assert.Equal(0.0, ArrivalRestitch.SiteAlignOffsetSeconds(deg, trot), 9);
        }

        [Fact]
        public void Offset_AlwaysInOneRotationWindow()
        {
            foreach (double deg in new[] { 1.0, 359.0, 721.0, -1.0, -719.0, 180.0 })
            {
                double s = ArrivalRestitch.SiteAlignOffsetSeconds(deg, DunaTrot);
                Assert.InRange(s, 0.0, DunaTrot - 1e-9);
            }
        }

        // --- The connectivity invariant (the failing-first synthetic landing fixture) ---
        //
        // Bearing arithmetic model (circular equatorial parking, the Supported profile): a spin-axis
        // rotation of the arrival chain by theta moves the deorbit point's inertial bearing by theta;
        // the body-fixed site's inertial bearing advances at 360/T_rot deg/s. With the congruence
        // offset the trigger fires when the site has rotated theta PAST the recorded deorbit
        // configuration - i.e. exactly under the rotated deorbit point. The descent head at the
        // trigger is exactly recordedDeorbitUT, so the clip (and the touchdown lat/lon) replay
        // verbatim: the recorded-site invariant.

        private const double Cadence = 4_000_000.0;
        private const double SpanStart = 2_570_000_000.0;
        private const double PhaseAnchor = 3_900_000_000.0;
        private const double Deorbit = 2_570_541_342.0;
        private const double DescentEnd = Deorbit + 900.0;
        private const double Tpark = 4000.0;
        private const double CaptureShift = -2_800_000.0;

        private static double TriggerFor(double offsetSeconds, long cycle = 0)
        {
            DescentTrigger.ComputeDescentTiming(
                cycle, PhaseAnchor, Cadence, SpanStart, Deorbit, DunaTrot, CaptureShift,
                null, out _, out _, out double triggerUT, offsetSeconds);
            return triggerUT;
        }

        [Fact]
        public void Connectivity_RotatedDeorbitPoint_MeetsSiteAtOffsetTrigger()
        {
            // theta = +37 deg: the rotated parking conic's deorbit point sits 37 deg past its
            // recorded bearing. Site inertial bearing advance since the recorded deorbit:
            // 360 * (trigger - Deorbit) / T_rot (mod 360). With the offset congruence this must
            // equal theta, i.e. the site is exactly under the rotated deorbit point at the trigger.
            const double theta = 37.0;
            double offset = ArrivalRestitch.SiteAlignOffsetSeconds(theta, DunaTrot);
            double trigger = TriggerFor(offset);
            double siteAdvanceDeg = 360.0 * (((trigger - Deorbit) % DunaTrot + DunaTrot) % DunaTrot) / DunaTrot;
            Assert.Equal(theta, siteAdvanceDeg, 4);
        }

        [Fact]
        public void Connectivity_NegativeTheta_AlsoMeets()
        {
            const double theta = -101.5;
            double offset = ArrivalRestitch.SiteAlignOffsetSeconds(theta, DunaTrot);
            double trigger = TriggerFor(offset);
            double siteAdvanceDeg = 360.0 * (((trigger - Deorbit) % DunaTrot + DunaTrot) % DunaTrot) / DunaTrot;
            Assert.Equal(0.0, Wrap180(theta - siteAdvanceDeg), 4);
        }

        [Fact]
        public void Connectivity_ComposesWithP4LoiterCuts()
        {
            // The P4 destination-loiter trim acts on the loop CLOCK (a launch-side cut shifts entryUT
            // earlier via CompressSpanUT); the S4 offset acts on the rotation CONGRUENCE. With both
            // engaged the trigger must still satisfy: at/after entry, within one rotation of it, and
            // congruent to (deorbit + offset) mod T_rot - i.e. the site still meets the rotated
            // deorbit point, cuts or no cuts.
            const double theta = 37.0;
            // A smaller shift than the class fixture so conicEnd (= Deorbit + shift) stays INSIDE the
            // span (a cut can only compress recorded time between SpanStart and conicEnd).
            const double shift = -400_000.0;
            double conicEnd = Deorbit + shift;
            Assert.True(conicEnd > SpanStart);
            double offset = ArrivalRestitch.SiteAlignOffsetSeconds(theta, DunaTrot);
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                // A launch-side loiter cut fully before conicEnd, so CompressSpanUT subtracts it.
                new GhostPlaybackLogic.LoopCut { StartUT = SpanStart + 10_000.0, LengthSeconds = 50_000.0 },
            };
            DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, Deorbit, DunaTrot, shift,
                cuts, out _, out double entryUT, out double triggerUT, offset);
            Assert.False(double.IsNaN(triggerUT));
            Assert.InRange(triggerUT, entryUT, entryUT + DunaTrot);
            double congruence = (((triggerUT - Deorbit - offset) % DunaTrot) + DunaTrot) % DunaTrot;
            Assert.True(Math.Min(congruence, DunaTrot - congruence) < 1e-3, $"congruence={congruence}");
            // And the cut moved the entry earlier than the uncut clock (the P4 knob did act).
            DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, Deorbit, DunaTrot, shift,
                null, out _, out double entryNoCut, out _, offset);
            Assert.True(entryUT < entryNoCut);
        }

        [Fact]
        public void Connectivity_ZeroOffset_IsShippedBehavior()
        {
            // Offset 0 must reproduce the shipped congruence exactly (byte-identical-off).
            double withDefault;
            DescentTrigger.ComputeDescentTiming(
                0, PhaseAnchor, Cadence, SpanStart, Deorbit, DunaTrot, CaptureShift,
                null, out _, out _, out withDefault);
            Assert.Equal(withDefault, TriggerFor(0.0), 9);
        }

        [Fact]
        public void RecordedSiteInvariant_DescentHeadAtTrigger_IsRecordedDeorbit()
        {
            // The headline assertion: under ANY engaged offset, the descent head at the trigger is
            // exactly recordedDeorbitUT - the body-fixed clip replays verbatim, so the touchdown
            // lat/lon is the RECORDED site. The offset only re-times; it never re-sites.
            foreach (double theta in new[] { 0.0, 37.0, -101.5, 179.9 })
            {
                double offset = ArrivalRestitch.SiteAlignOffsetSeconds(theta, DunaTrot);
                double trigger = TriggerFor(offset);
                var phase = DescentTrigger.ComputeDescentMemberHead(
                    trigger, 0, PhaseAnchor, Cadence, SpanStart, Deorbit, DescentEnd,
                    DunaTrot, Tpark, CaptureShift, null, out double head, offset);
                Assert.Equal(DescentTrigger.DescentHeadPhase.Descent, phase);
                Assert.Equal(Deorbit, head, 6);
            }
        }

        [Fact]
        public void RecordedSiteInvariant_LoiterHeadReachesConicEndAtTrigger()
        {
            // Position-continuity across the handoff is preserved under the offset: just before the
            // trigger the loiter head approaches conicEnd (the deorbit point on the shifted conic).
            const double theta = 37.0;
            double offset = ArrivalRestitch.SiteAlignOffsetSeconds(theta, DunaTrot);
            double trigger = TriggerFor(offset);
            double justBefore = trigger - 1.0;
            var phase = DescentTrigger.ComputeDescentMemberHead(
                justBefore, 0, PhaseAnchor, Cadence, SpanStart, Deorbit, DescentEnd,
                DunaTrot, Tpark, CaptureShift, null, out double head, offset);
            Assert.Equal(DescentTrigger.DescentHeadPhase.Loiter, phase);
            Assert.Equal(Deorbit + CaptureShift - 1.0, head, 6);
        }

        [Fact]
        public void Offset_NaN_SanitizedToShippedBehavior_NeverHidesDescent()
        {
            // A non-finite offset must degrade to the shipped congruence (offset 0), never to a
            // NaN trigger (which would silently hide the descent - worse than the shipped seam).
            double t = DescentTrigger.ComputeRotationAlignedTriggerUT(
                3_900_000_100.0, Deorbit, DunaTrot, double.NaN);
            double t0 = DescentTrigger.ComputeRotationAlignedTriggerUT(
                3_900_000_100.0, Deorbit, DunaTrot);
            Assert.Equal(t0, t, 9);
        }

        // --- The rotated-chain geometry (assembler application) ---

        private static OrbitSegment Seg(
            string body, double startUT, double endUT, double lan, bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = body,
                startUT = startUT,
                endUT = endUT,
                longitudeOfAscendingNode = lan,
                inclination = 0.5,
                eccentricity = 1.2,
                semiMajorAxis = -1.0e6,
                argumentOfPeriapsis = 10.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                isPredicted = predicted,
            };
        }

        [Fact]
        public void Assembler_RotatesOnlyTheArrivalChain()
        {
            const double departure = 2_567_000_000.0;
            const double arrival = 2_570_000_000.0;
            var members = new List<OrbitSegment>
            {
                Seg("Kerbin", departure - 20000.0, departure, 40.0),   // escape - untouched
                Seg("Sun", departure, arrival, 100.0),                  // heliocentric - replaced
                Seg("Duna", arrival, arrival + 50000.0, 80.0),          // approach hyperbola - rotated
                Seg("Duna", arrival + 50000.0, Deorbit, 85.0),          // capture/parking - rotated
                Seg("Duna", Deorbit + 1000.0, Deorbit + 2000.0, 90.0, predicted: true), // predicted - untouched
            };
            OrbitSegment transfer = Seg("Sun", departure, arrival, 123.0);
            const double theta = 37.0;

            List<OrbitSegment> outSegs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                members, transfer, "Sun", departure, arrival,
                double.NaN, double.NaN, 0.0, 0.0, "Duna", theta);

            Assert.NotNull(outSegs);
            // The Kerbin escape leg is untouched.
            Assert.Contains(outSegs, s => s.bodyName == "Kerbin" && s.longitudeOfAscendingNode == 40.0);
            // Every non-predicted Duna arrival-chain segment turned by theta.
            Assert.Contains(outSegs, s => s.bodyName == "Duna" && !s.isPredicted
                && Math.Abs(s.longitudeOfAscendingNode - (80.0 + theta)) < 1e-9);
            Assert.Contains(outSegs, s => s.bodyName == "Duna" && !s.isPredicted
                && Math.Abs(s.longitudeOfAscendingNode - (85.0 + theta)) < 1e-9);
            // The predicted tail is untouched.
            Assert.Contains(outSegs, s => s.bodyName == "Duna" && s.isPredicted
                && s.longitudeOfAscendingNode == 90.0);
        }

        [Fact]
        public void Assembler_RotationComposesWithCaptureShift()
        {
            const double departure = 2_567_000_000.0;
            const double arrival = 2_570_000_000.0;
            const double shift = -120000.0;
            const double theta = -20.0;
            var members = new List<OrbitSegment>
            {
                Seg("Sun", departure, arrival, 100.0),
                Seg("Duna", arrival, arrival + 50000.0, 80.0),
            };
            OrbitSegment transfer = Seg("Sun", departure, arrival, 123.0);

            List<OrbitSegment> outSegs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                members, transfer, "Sun", departure, arrival,
                double.NaN, double.NaN, 0.0, shift, "Duna", theta);

            OrbitSegment duna = outSegs.Find(s => s.bodyName == "Duna");
            Assert.Equal(arrival + shift, duna.startUT, 6);                 // shifted
            Assert.Equal(60.0, duna.longitudeOfAscendingNode, 6);           // 80 - 20, wrapped to [0,360)
        }

        [Fact]
        public void Assembler_ZeroRotation_ByteIdenticalToShippedPath()
        {
            const double departure = 2_567_000_000.0;
            const double arrival = 2_570_000_000.0;
            var members = new List<OrbitSegment>
            {
                Seg("Sun", departure, arrival, 100.0),
                Seg("Duna", arrival, arrival + 50000.0, 80.0),
            };
            OrbitSegment transfer = Seg("Sun", departure, arrival, 123.0);

            List<OrbitSegment> withParam = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                members, transfer, "Sun", departure, arrival,
                double.NaN, double.NaN, 0.0, -5000.0, "Duna", 0.0);
            List<OrbitSegment> withoutParam = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                members, transfer, "Sun", departure, arrival,
                double.NaN, double.NaN, 0.0, -5000.0, "Duna");

            Assert.Equal(withoutParam.Count, withParam.Count);
            for (int i = 0; i < withParam.Count; i++)
            {
                Assert.Equal(withoutParam[i].longitudeOfAscendingNode, withParam[i].longitudeOfAscendingNode);
                Assert.Equal(withoutParam[i].startUT, withParam[i].startUT);
                Assert.Equal(withoutParam[i].endUT, withParam[i].endUT);
                Assert.Equal(withoutParam[i].epoch, withParam[i].epoch);
            }
        }
    }
}
