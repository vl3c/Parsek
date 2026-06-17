using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 3 of re-aim: the pure per-member heliocentric-leg replacement + the time-shift phase
    // preservation (epoch moves with the UTs). Guards that ONLY the heliocentric (Sun) leg in the
    // transfer window is replaced, body-relative legs are kept, and chained missions (a member with no
    // heliocentric leg) pass through unchanged.
    public class ReaimSegmentAssemblerTests
    {
        private static OrbitSegment Seg(string body, double start, double end, double epoch,
            double sma = 1.0e7, double mEp = 0.5, bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = body, startUT = start, endUT = end, epoch = epoch,
                semiMajorAxis = sma, eccentricity = 0.1, inclination = 5.0,
                longitudeOfAscendingNode = 30.0, argumentOfPeriapsis = 45.0, meanAnomalyAtEpoch = mEp,
                isPredicted = predicted
            };
        }

        [Fact]
        public void ShiftInTime_MovesUTsAndEpochTogether()
        {
            var s = Seg("Kerbin", 100, 200, 150);
            var shifted = ReaimSegmentAssembler.ShiftInTime(s, 1000.0);
            Assert.Equal(1100.0, shifted.startUT, 6);
            Assert.Equal(1200.0, shifted.endUT, 6);
            Assert.Equal(1150.0, shifted.epoch, 6); // epoch moved with the window -> phase preserved
            // Shape + body untouched.
            Assert.Equal(s.semiMajorAxis, shifted.semiMajorAxis, 6);
            Assert.Equal("Kerbin", shifted.bodyName);
        }

        [Fact]
        public void ReplaceHeliocentricLeg_ReplacesOnlySunLeg_KeepsBodyRelativeLegs()
        {
            // A single continuous recording: Kerbin parking [100,600] -> Sun transfer [600,2600] ->
            // Duna capture [2600,5000]. Re-aim replaces ONLY the Sun leg with the re-aimed transfer
            // (placed at [600,2600]); the Kerbin and Duna legs (body-relative, follow their bodies) are
            // kept untouched.
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                Seg("Sun", 600, 2600, 1000, sma: 1.0e9),
                Seg("Duna", 2600, 5000, 3000),
            };
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10); // per-window orientation set by caller

            var segs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", recordedDepartureUT: 600.0, recordedArrivalUT: 2600.0,
                transferRenderStartUT: double.NaN, transferRenderEndUT: double.NaN); // no trim -> full leg

            Assert.NotNull(segs);
            Assert.Equal(3, segs.Count);
            Assert.Equal("Kerbin", segs[0].bodyName);    // kept (body-relative)
            Assert.Equal(100.0, segs[0].startUT, 3);
            Assert.Equal(600.0, segs[0].endUT, 3);
            Assert.Equal("Sun", segs[1].bodyName);       // RE-AIMED transfer at [600,2600]
            Assert.Equal(600.0, segs[1].startUT, 3);
            Assert.Equal(2600.0, segs[1].endUT, 3);
            Assert.Equal(2.0e10, segs[1].semiMajorAxis, 0); // the re-aimed orbit, not the recorded 1e9
            Assert.False(segs[1].isPredicted);
            Assert.Equal("Duna", segs[2].bodyName);      // kept (body-relative), recorded UTs unchanged
            Assert.Equal(2600.0, segs[2].startUT, 3);
            Assert.Equal(5000.0, segs[2].endUT, 3);
        }

        [Fact]
        public void ReplaceHeliocentricLeg_TrimsTransferToInterplanetarySpan()
        {
            // The transfer is rendered only over the interplanetary span (SOI exit -> SOI entry): the
            // in-SOI stubs at the body centers are dropped so the map does not flicker them.
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                Seg("Sun", 600, 2600, 1000, sma: 1.0e9),
                Seg("Duna", 2600, 5000, 3000),
            };
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10);

            // Launch SOI exit at 700, target SOI entry at 2500 (inside the recorded [600,2600] window).
            var segs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", recordedDepartureUT: 600.0, recordedArrivalUT: 2600.0,
                transferRenderStartUT: 700.0, transferRenderEndUT: 2500.0);

            Assert.NotNull(segs);
            Assert.Equal(3, segs.Count);
            Assert.Equal("Sun", segs[1].bodyName);
            Assert.Equal(700.0, segs[1].startUT, 3);   // trimmed to the SOI-exit UT, not 600
            Assert.Equal(2500.0, segs[1].endUT, 3);    // trimmed to the SOI-entry UT, not 2600
            // The body-relative legs keep their recorded UTs, so a gap now sits between the Kerbin leg
            // (ends 600) and the trimmed transfer (starts 700), and between the transfer (ends 2500) and
            // the Duna leg (starts 2600) - where the ghost is hidden (the in-SOI handoff).
            Assert.Equal(600.0, segs[0].endUT, 3);
            Assert.Equal(2600.0, segs[2].startUT, 3);
        }

        [Fact]
        public void ReplaceHeliocentricLeg_InvalidTrimBounds_FallsBackToFullLeg()
        {
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                Seg("Sun", 600, 2600, 1000, sma: 1.0e9),
                Seg("Duna", 2600, 5000, 3000),
            };
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10);

            // renderStart below the recorded departure (out of range) -> fall back to the full leg.
            var segs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", recordedDepartureUT: 600.0, recordedArrivalUT: 2600.0,
                transferRenderStartUT: 500.0, transferRenderEndUT: 2500.0);

            Assert.NotNull(segs);
            Assert.Equal(600.0, segs[1].startUT, 3);   // untrimmed
            Assert.Equal(2600.0, segs[1].endUT, 3);
        }

        [Fact]
        public void ReplaceHeliocentricLeg_MidCourseCorrection_CollapsesBothSunLegs()
        {
            // Two Sun coasts (a mid-course correction between them) collapse into the single re-aimed arc.
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                Seg("Sun", 600, 1500, 800, sma: 1.0e9),    // coast 1
                Seg("Sun", 1600, 2600, 2000, sma: 1.1e9),  // coast 2 (after a burn gap)
                Seg("Duna", 2600, 5000, 3000),
            };
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10);

            var segs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", recordedDepartureUT: 600.0, recordedArrivalUT: 2600.0,
                transferRenderStartUT: double.NaN, transferRenderEndUT: double.NaN);

            Assert.NotNull(segs);
            Assert.Equal(3, segs.Count); // Kerbin + ONE re-aimed Sun arc + Duna (both coasts collapsed)
            Assert.Equal("Kerbin", segs[0].bodyName);
            Assert.Equal("Sun", segs[1].bodyName);
            Assert.Equal(600.0, segs[1].startUT, 3);
            Assert.Equal(2600.0, segs[1].endUT, 3);
            Assert.Equal("Duna", segs[2].bodyName);
        }

        [Fact]
        public void ReplaceHeliocentricLeg_MemberWithNoSunLeg_ReturnsNull()
        {
            // A chained mission's launch member (Kerbin only) or arrival member (Duna only) has no
            // heliocentric leg -> null (stays faithful; body-relative segments follow their bodies).
            var launchMember = new List<OrbitSegment> { Seg("Kerbin", 100, 600, 300) };
            var arrivalMember = new List<OrbitSegment> { Seg("Duna", 2600, 5000, 3000) };
            var transfer = Seg("Sun", 0, 0, 0);

            Assert.Null(ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                launchMember, transfer, "Sun", 600.0, 2600.0, double.NaN, double.NaN));
            Assert.Null(ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                arrivalMember, transfer, "Sun", 600.0, 2600.0, double.NaN, double.NaN));
            Assert.False(ReaimSegmentAssembler.HasHeliocentricLegInWindow(launchMember, "Sun", 600.0, 2600.0));
            Assert.False(ReaimSegmentAssembler.HasHeliocentricLegInWindow(arrivalMember, "Sun", 600.0, 2600.0));
        }

        [Fact]
        public void HasHeliocentricLegInWindow_PredictedOrOutOfWindow_False()
        {
            // A predicted (ballistic-tail) Sun segment does not count; nor does a Sun segment outside the
            // recorded transfer window.
            var predictedSun = new List<OrbitSegment> { Seg("Sun", 600, 2600, 1000, predicted: true) };
            var outOfWindow = new List<OrbitSegment> { Seg("Sun", 9000, 9500, 9100) };
            Assert.False(ReaimSegmentAssembler.HasHeliocentricLegInWindow(predictedSun, "Sun", 600.0, 2600.0));
            Assert.False(ReaimSegmentAssembler.HasHeliocentricLegInWindow(outOfWindow, "Sun", 600.0, 2600.0));
            Assert.True(ReaimSegmentAssembler.HasHeliocentricLegInWindow(
                new List<OrbitSegment> { Seg("Sun", 600, 2600, 1000) }, "Sun", 600.0, 2600.0));
        }

        // ---- CoalesceSameOrbitFragments (loop-only in-memory merge of recorder-split parking fragments) ----

        [Fact]
        public void CoalesceSameOrbitFragments_MergesContiguousSameOrbitFragmentsAcrossGaps()
        {
            // The recorder split one parking coast into 3 same-orbit fragments with sampling gaps
            // (background/foreground switches). They must coalesce into one continuous segment.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 200, 100),
                Seg("Kerbin", 250, 400, 250),  // 50s gap
                Seg("Kerbin", 411, 451, 411),  // 11s gap, a ~40s tail (the s15 spurious fragment)
            };
            var merged = TrajectoryMath.CoalesceSameOrbitFragments(segs);
            Assert.Single(merged);
            Assert.Equal(100.0, merged[0].startUT, 3);  // first fragment's start
            Assert.Equal(451.0, merged[0].endUT, 3);    // last fragment's end (spans the gaps)
            Assert.Equal(100.0, merged[0].epoch, 3);    // first fragment's epoch anchors the merged arc
        }

        [Fact]
        public void CoalesceSameOrbitFragments_KeepsRealManeuverBoundary()
        {
            // Parking fragments (same orbit) then an escape burn (hugely different sma/ecc) stay separate.
            var burn = Seg("Kerbin", 451, 60000, 451, sma: -3.8e6);
            burn.eccentricity = 1.19; // hyperbolic - far outside the equivalence tolerance
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 400, 100),
                Seg("Kerbin", 411, 451, 411),
                burn,
            };
            var merged = TrajectoryMath.CoalesceSameOrbitFragments(segs);
            Assert.Equal(2, merged.Count);
            Assert.Equal(100.0, merged[0].startUT, 3);
            Assert.Equal(451.0, merged[0].endUT, 3);          // the two parking fragments merged
            Assert.Equal(-3.8e6, merged[1].semiMajorAxis, 0); // the burn kept as its own boundary
        }

        [Fact]
        public void CoalesceSameOrbitFragments_DoesNotMergeAcrossPredictedMismatch()
        {
            // A predicted (ballistic-tail) segment must not fold into a non-predicted parking arc even
            // when the elements coincide - the kind classification stays honest.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 400, 100, predicted: false),
                Seg("Kerbin", 411, 451, 411, predicted: true),
            };
            var merged = TrajectoryMath.CoalesceSameOrbitFragments(segs);
            Assert.Equal(2, merged.Count);
        }

        [Fact]
        public void CoalesceSameOrbitFragments_NullOrSingle_PassThrough()
        {
            Assert.Null(TrajectoryMath.CoalesceSameOrbitFragments(null));
            var one = new List<OrbitSegment> { Seg("Kerbin", 100, 200, 100) };
            Assert.Same(one, TrajectoryMath.CoalesceSameOrbitFragments(one));
        }

        // Hot-path allocation guard (forward-render review finding): when NO adjacent pair would merge
        // (the common multi-segment case: a real maneuver boundary, an SOI change, a predicted-vs-faithful
        // boundary), the helper returns the INPUT list BY REFERENCE instead of allocating a fresh copy
        // every frame. The contents are unchanged, so callers (which only read) see byte-identical data.
        [Fact]
        public void CoalesceSameOrbitFragments_NothingMerges_ReturnsInputByReference()
        {
            // Two segments separated by a real escape burn (hyperbolic, far outside the equivalence
            // tolerance): no adjacent pair merges, so the pre-scan returns the same list reference.
            var burn = Seg("Kerbin", 200, 60000, 200, sma: -3.8e6);
            burn.eccentricity = 1.19;
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 200, 100),
                burn,
            };
            var result = TrajectoryMath.CoalesceSameOrbitFragments(segs);
            Assert.Same(segs, result);     // no allocation: same reference returned
            Assert.Equal(2, result.Count); // contents unchanged
        }

        // Predicted/faithful boundary with otherwise-identical elements: still no merge, still by-reference.
        [Fact]
        public void CoalesceSameOrbitFragments_PredictedMismatch_ReturnsInputByReference()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 400, 100, predicted: false),
                Seg("Kerbin", 411, 451, 411, predicted: true),
            };
            var result = TrajectoryMath.CoalesceSameOrbitFragments(segs);
            Assert.Same(segs, result);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void ReplaceHeliocentricLeg_CoalescesFragmentedParkingBeforeTransfer()
        {
            // s15 "Kerbal X" shape: the parking coast arrives as 3 same-orbit Kerbin fragments (recorder
            // split, incl. a short tail), then the Sun transfer. After re-aim, the parking fragments
            // coalesce into one segment so the loop clock never lands the head in the short tail.
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 300, 100),
                Seg("Kerbin", 320, 560, 320),
                Seg("Kerbin", 575, 600, 575),  // the spurious short tail before the transfer
                Seg("Sun", 600, 2600, 1000, sma: 1.0e9),
            };
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10);

            var segs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", recordedDepartureUT: 600.0, recordedArrivalUT: 2600.0,
                transferRenderStartUT: double.NaN, transferRenderEndUT: double.NaN);

            Assert.NotNull(segs);
            Assert.Equal(2, segs.Count);              // one coalesced parking + the transfer (was 4)
            Assert.Equal("Kerbin", segs[0].bodyName);
            Assert.Equal(100.0, segs[0].startUT, 3);
            Assert.Equal(600.0, segs[0].endUT, 3);    // the 575-600 tail is no longer its own segment
            Assert.Equal("Sun", segs[1].bodyName);
            Assert.Equal(600.0, segs[1].startUT, 3);
        }

        // ---- Heliocentric-park LAN re-phase (Increment 1: departure-side render) ----

        [Fact]
        public void RotateLanForParkRephase_AdvancesAndWrapsLan()
        {
            // LAN 30 + 90 -> 120 (no wrap).
            Assert.Equal(120.0, ReaimSegmentAssembler.RotateLanForParkRephase(
                Seg("Sun", 0, 0, 0), 90.0).longitudeOfAscendingNode, 6);
            // LAN 30 + 350 -> 380 -> wrap to 20.
            Assert.Equal(20.0, ReaimSegmentAssembler.RotateLanForParkRephase(
                Seg("Sun", 0, 0, 0), 350.0).longitudeOfAscendingNode, 6);
            // LAN 30 + 768.5 (the window-0 s15 magnitude) -> 798.5 -> wrap to 78.5 (2 full turns out).
            Assert.Equal(78.5, ReaimSegmentAssembler.RotateLanForParkRephase(
                Seg("Sun", 0, 0, 0), 768.5).longitudeOfAscendingNode, 6);
            // Negative delta wraps into [0,360): LAN 30 - 90 -> -60 -> 300.
            Assert.Equal(300.0, ReaimSegmentAssembler.RotateLanForParkRephase(
                Seg("Sun", 0, 0, 0), -90.0).longitudeOfAscendingNode, 6);
            // Zero delta is the identity (byte-identical default path).
            Assert.Equal(30.0, ReaimSegmentAssembler.RotateLanForParkRephase(
                Seg("Sun", 0, 0, 0), 0.0).longitudeOfAscendingNode, 6);
            // Only LAN moves; shape/body untouched.
            var rot = ReaimSegmentAssembler.RotateLanForParkRephase(Seg("Sun", 0, 0, 0, sma: 1.4e10), 90.0);
            Assert.Equal(1.4e10, rot.semiMajorAxis, 0);
            Assert.Equal(5.0, rot.inclination, 6);
            Assert.Equal(45.0, rot.argumentOfPeriapsis, 6);
        }

        [Fact]
        public void ComputeParkDeltaLonDegrees_IsOmegaTimesWindowOffset()
        {
            // omega_parent = 360 / period. Delta_lon = omega * (D_c - RecDep).
            // period = 9,203,545 s (Kerbin), D_c - RecDep = +19,645,697 s (one synodic) -> ~768.49 deg.
            double dLon = ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(
                departureUTForWindow: 2580716597.0, recordedDepartureUT: 2561070900.0,
                launchBodyOrbitPeriodSeconds: 9203545.0);
            Assert.Equal(360.0 * (2580716597.0 - 2561070900.0) / 9203545.0, dLon, 4);
            Assert.True(dLon > 768.0 && dLon < 769.0);
            // Degenerate period -> NaN (fail-closed signal).
            Assert.True(double.IsNaN(ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(1.0, 0.0, 0.0)));
            Assert.True(double.IsNaN(ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(1.0, 0.0, double.NaN)));
            Assert.True(double.IsNaN(ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(1.0, 0.0, double.PositiveInfinity)));
            Assert.True(double.IsNaN(ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(double.NaN, 0.0, 100.0)));
        }

        [Fact]
        public void FindHeliocentricParkInclination_FindsLatestParkBeforeBurn()
        {
            // s15 shape: Kerbin escape -> Sun PARK [600,1500] -> Sun TRANSFER [1500,3000] -> Duna.
            // The park is the Sun coast ENDING at/before the burn (recordedDepartureUT=1500). The in-window
            // transfer (endUT 3000 > 1500) is NOT the park.
            var park = Seg("Sun", 600, 1500, 1000, sma: 1.4e10);
            park.inclination = 1.3;
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                park,
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10), // in-window transfer, not the park
                Seg("Duna", 3000, 5000, 3500),
            };
            Assert.Equal(1.3, ReaimSegmentAssembler.FindHeliocentricParkInclination(member, "Sun", 1500.0), 6);

            // Two solar parks before the burn -> the LATEST-ending one's inclination.
            var early = Seg("Sun", 600, 1000, 700, sma: 1.4e10); early.inclination = 1.0;
            var late = Seg("Sun", 1000, 1500, 1100, sma: 1.4e10); late.inclination = 2.5;
            var twoParks = new List<OrbitSegment> { Seg("Kerbin", 100, 600, 300), early, late };
            Assert.Equal(2.5, ReaimSegmentAssembler.FindHeliocentricParkInclination(twoParks, "Sun", 1500.0), 6);

            // No solar park (direct transfer: only Kerbin + in-window Sun + Duna) -> NaN.
            var direct = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 1500, 800),
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10),
                Seg("Duna", 3000, 5000, 3500),
            };
            Assert.True(double.IsNaN(ReaimSegmentAssembler.FindHeliocentricParkInclination(direct, "Sun", 1500.0)));

            // A predicted Sun coast before the burn does not count (NaN).
            var predictedPark = new List<OrbitSegment> { Seg("Sun", 600, 1500, 1000, sma: 1.4e10, predicted: true) };
            Assert.True(double.IsNaN(ReaimSegmentAssembler.FindHeliocentricParkInclination(predictedPark, "Sun", 1500.0)));
        }

        [Fact]
        public void TryComputeParkRephase_FailsClosedOnNonEquatorialOrDegenerate()
        {
            // Near-equatorial co-orbital park -> rephase with the computed angle.
            Assert.True(ReaimSegmentAssembler.TryComputeParkRephase(
                parkInclinationDeg: 1.3, departureUTForWindow: 2580716597.0,
                recordedDepartureUT: 2561070900.0, launchBodyOrbitPeriodSeconds: 9203545.0,
                out double dLon));
            Assert.True(dLon > 768.0 && dLon < 769.0);

            // At the guard threshold (15 deg) it still engages; just over it fails closed.
            Assert.True(ReaimSegmentAssembler.TryComputeParkRephase(
                ReaimSegmentAssembler.ParkRephaseMaxInclinationDeg, 100.0, 0.0, 9203545.0, out _));
            Assert.False(ReaimSegmentAssembler.TryComputeParkRephase(
                ReaimSegmentAssembler.ParkRephaseMaxInclinationDeg + 0.1, 100.0, 0.0, 9203545.0, out double offDeg));
            Assert.Equal(0.0, offDeg, 6);

            // NaN inclination (no park found) -> fail closed.
            Assert.False(ReaimSegmentAssembler.TryComputeParkRephase(
                double.NaN, 100.0, 0.0, 9203545.0, out _));
            // Degenerate launch-body period -> fail closed.
            Assert.False(ReaimSegmentAssembler.TryComputeParkRephase(
                1.0, 100.0, 0.0, 0.0, out _));
        }

        [Fact]
        public void ReplaceHeliocentricLeg_RotatesOnlyTheSunParkBeforeBurn()
        {
            // s15 shape with a re-aimed transfer. parkDeltaLonDeg=90 rotates ONLY the Sun PARK [600,1500]
            // (before the burn); the in-window transfer (the replacement), the body-relative Kerbin/Duna
            // legs are untouched.
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),                       // escape (LAN 30) - kept
                Seg("Sun", 600, 1500, 1000, sma: 1.4e10),           // PARK (LAN 30) - rotated
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10),         // in-window transfer - replaced
                Seg("Duna", 3000, 5000, 3500),                      // capture (LAN 30) - kept
            };
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10);        // synthesized replacement (LAN 30)

            var segs = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", recordedDepartureUT: 1500.0, recordedArrivalUT: 3000.0,
                transferRenderStartUT: double.NaN, transferRenderEndUT: double.NaN, parkDeltaLonDeg: 90.0);

            Assert.NotNull(segs);
            Assert.Equal(4, segs.Count);
            // Kerbin escape: kept, NOT rotated.
            Assert.Equal("Kerbin", segs[0].bodyName);
            Assert.Equal(30.0, segs[0].longitudeOfAscendingNode, 6);
            // Sun PARK [600,1500]: rotated 30 -> 120.
            Assert.Equal("Sun", segs[1].bodyName);
            Assert.Equal(600.0, segs[1].startUT, 3);
            Assert.Equal(1.4e10, segs[1].semiMajorAxis, 0);
            Assert.Equal(120.0, segs[1].longitudeOfAscendingNode, 6);
            // Sun TRANSFER (the re-aimed replacement at [1500,3000]): NOT rotated (LAN 30, sma 2e10).
            Assert.Equal("Sun", segs[2].bodyName);
            Assert.Equal(1500.0, segs[2].startUT, 3);
            Assert.Equal(2.0e10, segs[2].semiMajorAxis, 0);
            Assert.Equal(30.0, segs[2].longitudeOfAscendingNode, 6);
            // Duna capture: kept, NOT rotated.
            Assert.Equal("Duna", segs[3].bodyName);
            Assert.Equal(30.0, segs[3].longitudeOfAscendingNode, 6);
        }

        [Fact]
        public void FindHeliocentricParkSegment_ReturnsLatestParkBeforeBurn_NullForDirect()
        {
            // Same shape as FindHeliocentricParkInclination_FindsLatestParkBeforeBurn but on the whole-segment
            // accessor (the r1 departure-anchor source): the latest non-predicted Sun coast ENDING at/before
            // the burn (recordedDepartureUT). The in-window transfer (endUT 3000 > 1500) is NOT the park.
            var park = Seg("Sun", 600, 1500, 1000, sma: 1.4e10);
            park.inclination = 1.3;
            park.longitudeOfAscendingNode = 200.0;
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                park,
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10), // in-window transfer, not the park
                Seg("Duna", 3000, 5000, 3500),
            };
            OrbitSegment? found = ReaimSegmentAssembler.FindHeliocentricParkSegment(member, "Sun", 1500.0);
            Assert.True(found.HasValue);
            Assert.Equal(1.3, found.Value.inclination, 6);
            Assert.Equal(1.4e10, found.Value.semiMajorAxis, 0);   // the WHOLE park segment, not just inc
            Assert.Equal(200.0, found.Value.longitudeOfAscendingNode, 6);
            Assert.Equal(600.0, found.Value.startUT, 3);
            Assert.Equal(1500.0, found.Value.endUT, 3);

            // Two solar parks before the burn -> the LATEST-ending one.
            var early = Seg("Sun", 600, 1000, 700, sma: 1.4e10); early.inclination = 1.0;
            var late = Seg("Sun", 1000, 1500, 1100, sma: 1.5e10); late.inclination = 2.5;
            var twoParks = new List<OrbitSegment> { Seg("Kerbin", 100, 600, 300), early, late };
            OrbitSegment? latest = ReaimSegmentAssembler.FindHeliocentricParkSegment(twoParks, "Sun", 1500.0);
            Assert.True(latest.HasValue);
            Assert.Equal(2.5, latest.Value.inclination, 6);
            Assert.Equal(1.5e10, latest.Value.semiMajorAxis, 0);

            // Direct transfer (no pre-burn Sun coast) -> null.
            var direct = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 1500, 800),
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10),
                Seg("Duna", 3000, 5000, 3500),
            };
            Assert.False(ReaimSegmentAssembler.FindHeliocentricParkSegment(direct, "Sun", 1500.0).HasValue);

            // A predicted Sun coast before the burn does not count -> null.
            var predictedPark = new List<OrbitSegment> { Seg("Sun", 600, 1500, 1000, sma: 1.4e10, predicted: true) };
            Assert.False(ReaimSegmentAssembler.FindHeliocentricParkSegment(predictedPark, "Sun", 1500.0).HasValue);
        }

        [Fact]
        public void FindHeliocentricParkInclination_DelegatesToFindHeliocentricParkSegment()
        {
            // The inc accessor must return EXACTLY the segment accessor's inclination (single source) so the
            // near-equatorial guard and the r1 departure-anchor source can never disagree on which seg is
            // "the park". When there is no park, both return the no-data sentinel (NaN / null).
            var park = Seg("Sun", 600, 1500, 1000, sma: 1.4e10);
            park.inclination = 3.7;
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                park,
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10),
                Seg("Duna", 3000, 5000, 3500),
            };
            OrbitSegment? seg = ReaimSegmentAssembler.FindHeliocentricParkSegment(member, "Sun", 1500.0);
            double inc = ReaimSegmentAssembler.FindHeliocentricParkInclination(member, "Sun", 1500.0);
            Assert.True(seg.HasValue);
            Assert.Equal(seg.Value.inclination, inc, 9); // delegated, not a separately-computed value

            var direct = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 1500, 800),
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10),
            };
            Assert.False(ReaimSegmentAssembler.FindHeliocentricParkSegment(direct, "Sun", 1500.0).HasValue);
            Assert.True(double.IsNaN(ReaimSegmentAssembler.FindHeliocentricParkInclination(direct, "Sun", 1500.0)));
        }

        [Fact]
        public void RotateLanForParkRephase_OnlyLanTurns_AllOtherElementsAndUTsUnchanged()
        {
            // The r1 derivation evaluates the LAN-rotated park; that is only correct if RotateLanForParkRephase
            // is a SHAPE-IDENTICAL rotation (recorded data untouched, only the inertial longitude turns). Pin
            // that inc/ecc/sma/argPe/mEp/epoch/startUT/endUT/bodyName/isPredicted are all unchanged and ONLY
            // LAN turns by deltaLonDeg mod 360.
            var seg = new OrbitSegment
            {
                bodyName = "Sun", startUT = 600.0, endUT = 1500.0, epoch = 1000.0,
                inclination = 1.3, eccentricity = 0.03, semiMajorAxis = 1.4e10,
                longitudeOfAscendingNode = 200.0, argumentOfPeriapsis = 47.0, meanAnomalyAtEpoch = 2.71,
                isPredicted = false
            };
            const double delta = 768.5; // > 360, exercises the wrap
            var rot = ReaimSegmentAssembler.RotateLanForParkRephase(seg, delta);

            // ONLY LAN moves, wrapped into [0,360): 200 + 768.5 = 968.5 -> 968.5 - 720 = 248.5.
            double expectedLan = (200.0 + delta) % 360.0;
            Assert.Equal(expectedLan, rot.longitudeOfAscendingNode, 6);
            Assert.NotEqual(seg.longitudeOfAscendingNode, rot.longitudeOfAscendingNode);

            // Every other element + UT + body + flag unchanged.
            Assert.Equal(seg.inclination, rot.inclination, 9);
            Assert.Equal(seg.eccentricity, rot.eccentricity, 9);
            Assert.Equal(seg.semiMajorAxis, rot.semiMajorAxis, 0);
            Assert.Equal(seg.argumentOfPeriapsis, rot.argumentOfPeriapsis, 9);
            Assert.Equal(seg.meanAnomalyAtEpoch, rot.meanAnomalyAtEpoch, 9);
            Assert.Equal(seg.epoch, rot.epoch, 6);
            Assert.Equal(seg.startUT, rot.startUT, 6);
            Assert.Equal(seg.endUT, rot.endUT, 6);
            Assert.Equal(seg.bodyName, rot.bodyName);
            Assert.Equal(seg.isPredicted, rot.isPredicted);
        }

        // ---- DecideDepartureAnchor (the pure override-vs-no-override decision; r1 source gate) ----

        [Fact]
        public void DecideDepartureAnchor_DirectTransfer_LaunchCenterNoEval()
        {
            // departedFromHeliocentricPark == false => the launch-center mode, byte-identical to main:
            // no override, no park rotation, no eval.
            var d = ReaimSegmentAssembler.DecideDepartureAnchor(
                departedFromHeliocentricPark: false, parkInclinationDeg: double.NaN,
                parkReplayUT: 2580716597.0, recordedDepartureUT: 2561070900.0,
                launchBodyOrbitPeriodSeconds: 9203545.0);
            Assert.Equal(ReaimSegmentAssembler.DepartureAnchorMode.LaunchCenter, d.Mode);
            Assert.Equal(0.0, d.ParkDeltaLonDeg, 9);
            Assert.True(double.IsNaN(d.ParkEvalUT));
        }

        [Fact]
        public void DecideDepartureAnchor_ParkingDeparture_RephaseOk_OverrideAtRecordedDepartureUT()
        {
            // parking departure + a near-equatorial park => ParkEndOverride, with the rephase angle and the
            // eval UT pinned to RecordedDepartureUT (BLOCKER 1: r1 is the park-end the icon abuts, which
            // renders at the RECORDED burn UT, NOT the future synodic departure D0 = parkReplayUT).
            const double recDep = 2561070900.0;
            const double replayUT = 2580716597.0; // D0 = recDep + one synodic; must NOT be the eval UT
            var d = ReaimSegmentAssembler.DecideDepartureAnchor(
                departedFromHeliocentricPark: true, parkInclinationDeg: 1.3,
                parkReplayUT: replayUT, recordedDepartureUT: recDep,
                launchBodyOrbitPeriodSeconds: 9203545.0);
            Assert.Equal(ReaimSegmentAssembler.DepartureAnchorMode.ParkEndOverride, d.Mode);
            // The rephase angle is the same value TryComputeParkRephase yields for these inputs.
            ReaimSegmentAssembler.TryComputeParkRephase(1.3, replayUT, recDep, 9203545.0, out double expectedDelta);
            Assert.Equal(expectedDelta, d.ParkDeltaLonDeg, 9);
            // BLOCKER 1: the eval UT is the RECORDED departure, never D0/parkReplayUT.
            Assert.Equal(recDep, d.ParkEvalUT, 3);
            Assert.NotEqual(replayUT, d.ParkEvalUT);
        }

        [Fact]
        public void DecideDepartureAnchor_ParkingDeparture_RephaseDeclined_DeclineNotLaunchCenter()
        {
            // parking departure + a NON-equatorial park (> the guard) => DeclineToFaithful, NOT LaunchCenter.
            // A parking departure must never fall back to the launch-center r1 (that would render the original
            // seam disguised as a fix).
            var nonEquatorial = ReaimSegmentAssembler.DecideDepartureAnchor(
                departedFromHeliocentricPark: true,
                parkInclinationDeg: ReaimSegmentAssembler.ParkRephaseMaxInclinationDeg + 0.1,
                parkReplayUT: 2580716597.0, recordedDepartureUT: 2561070900.0,
                launchBodyOrbitPeriodSeconds: 9203545.0);
            Assert.Equal(ReaimSegmentAssembler.DepartureAnchorMode.DeclineToFaithful, nonEquatorial.Mode);
            Assert.NotEqual(ReaimSegmentAssembler.DepartureAnchorMode.LaunchCenter, nonEquatorial.Mode);
            Assert.Equal(0.0, nonEquatorial.ParkDeltaLonDeg, 9);
            Assert.True(double.IsNaN(nonEquatorial.ParkEvalUT));

            // NaN inclination (no park found) -> also DeclineToFaithful, never LaunchCenter.
            var noPark = ReaimSegmentAssembler.DecideDepartureAnchor(
                departedFromHeliocentricPark: true, parkInclinationDeg: double.NaN,
                parkReplayUT: 2580716597.0, recordedDepartureUT: 2561070900.0,
                launchBodyOrbitPeriodSeconds: 9203545.0);
            Assert.Equal(ReaimSegmentAssembler.DepartureAnchorMode.DeclineToFaithful, noPark.Mode);

            // Degenerate launch-body period -> DeclineToFaithful.
            var degenPeriod = ReaimSegmentAssembler.DecideDepartureAnchor(
                departedFromHeliocentricPark: true, parkInclinationDeg: 1.3,
                parkReplayUT: 2580716597.0, recordedDepartureUT: 2561070900.0,
                launchBodyOrbitPeriodSeconds: 0.0);
            Assert.Equal(ReaimSegmentAssembler.DepartureAnchorMode.DeclineToFaithful, degenPeriod.Mode);
        }

        [Fact]
        public void ReplaceHeliocentricLeg_ZeroParkDelta_LeavesParkVerbatim()
        {
            // The regression fence: parkDeltaLonDeg=0 (direct transfer / Increment-1-disabled) leaves the
            // Sun park's LAN byte-identical to the recorded value (no rotation).
            var member = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 600, 300),
                Seg("Sun", 600, 1500, 1000, sma: 1.4e10),
                Seg("Sun", 1500, 3000, 2000, sma: 1.79e10),
                Seg("Duna", 3000, 5000, 3500),
            };
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10);

            // Default (no parkDeltaLonDeg arg) and explicit 0 are identical.
            var segsDefault = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", 1500.0, 3000.0, double.NaN, double.NaN);
            var segsZero = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, transfer, "Sun", 1500.0, 3000.0, double.NaN, double.NaN, parkDeltaLonDeg: 0.0);

            Assert.Equal(30.0, segsDefault[1].longitudeOfAscendingNode, 6); // park LAN unchanged
            Assert.Equal(30.0, segsZero[1].longitudeOfAscendingNode, 6);
            Assert.Equal("Sun", segsDefault[1].bodyName);
            Assert.Equal(1.4e10, segsDefault[1].semiMajorAxis, 0);
        }
    }
}
