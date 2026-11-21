using System;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// The two OrbitSegments lane SOI-3 recorded across the background probe's on-rails Mun
    /// -> Kerbin seam (run <c>2026-11-20_2310_SOI-3-bg-probe-mun-exit-on-rails</c>, recording
    /// <c>9a3188810727437db35873666be0c061</c>, elements copied verbatim from its
    /// <c>.prec.txt</c>), evaluated headless through <see cref="TwoBodyOrbit"/> with stock's
    /// Mun and Kerbin ephemerides (todo BG-SOI-SEAM-WORLD-GAP). Every orbit is built in the
    /// STORED element frame, so the comparison is frame-consistent with no live Zup read.
    /// Pins three readings:
    /// <list type="bullet">
    /// <item>the Kerbin segment's epoch is the true crossing (the Mun-side position sits on
    /// the Mun's SOI radius there) and the SOI event fired 0.43 s later;</item>
    /// <item>at the seam UT the two sides agree to under a metre: the recorder authored both
    /// conics correctly;</item>
    /// <item>the in-game cell's first reading, 9127.7 m, is the probe's heliocentric travel
    /// over the 1 s its two probe instants were apart (Kerbin's 9284.5 m/s orbital speed
    /// combined with the probe's 609.8 m/s Kerbin-relative velocity), not a seam gap.</item>
    /// </list>
    /// </summary>
    public class BgSoiSeamWorldGapTests
    {
        private const double SunMu = 1.1723328e18;
        private const double KerbinMu = 3.5316e12;
        private const double MunMu = 6.5138398e10;
        private const double MunSoiRadius = 2429559.1;

        private const double SeamUT = 1317.0898525618541;

        private static OrbitSegment MunSide() => new OrbitSegment
        {
            startUT = 1250.0898525618541,
            endUT = SeamUT,
            inclination = 0.0841149765504683,
            eccentricity = 1.2003708098235362,
            semiMajorAxis = -2495964.8405502844,
            longitudeOfAscendingNode = 141.85390093721276,
            argumentOfPeriapsis = 178.46834292108886,
            meanAnomalyAtEpoch = 0.42752272518316592,
            epoch = 429.95145690908288,
            bodyName = "Mun",
        };

        private static OrbitSegment KerbinSide() => new OrbitSegment
        {
            startUT = SeamUT,
            endUT = 1375.0898525618541,
            inclination = 0.02593044283528621,
            eccentricity = 0.61049372436339455,
            semiMajorAxis = 28626237.639147457,
            longitudeOfAscendingNode = 247.04296716753268,
            argumentOfPeriapsis = 145.00048136848639,
            meanAnomalyAtEpoch = 0.25830725106384184,
            epoch = 1316.6558148764598,
            bodyName = "Kerbin",
        };

        // Stock ephemerides (Kerbin.cfg / Mun.cfg in the 1.12 body definitions).
        private static OrbitSegment MunAroundKerbin() => new OrbitSegment
        {
            semiMajorAxis = 12000000.0,
            meanAnomalyAtEpoch = 1.7,
            epoch = 0.0,
            bodyName = "Kerbin",
        };

        private static OrbitSegment KerbinAroundSun() => new OrbitSegment
        {
            semiMajorAxis = 13599840256.0,
            meanAnomalyAtEpoch = 3.14,
            epoch = 0.0,
            bodyName = "Sun",
        };

        private static void State(OrbitSegment seg, double mu, double ut,
            out Vector3d position, out Vector3d velocity)
        {
            Assert.True(TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, mu, out TwoBodyOrbit orbit));
            orbit.GetStateAtUT(ut, out position, out velocity);
        }

        // Kerbin-relative position of the probe as each side's own segment places it.
        private static Vector3d MunSideKerbinRelative(double ut, out Vector3d velocity)
        {
            State(MunAroundKerbin(), KerbinMu, ut, out Vector3d munPos, out Vector3d munVel);
            State(MunSide(), MunMu, ut, out Vector3d probePos, out Vector3d probeVel);
            velocity = munVel + probeVel;
            return munPos + probePos;
        }

        private static Vector3d KerbinSideKerbinRelative(double ut, out Vector3d velocity)
        {
            State(KerbinSide(), KerbinMu, ut, out Vector3d pos, out velocity);
            return pos;
        }

        private static Vector3d Heliocentric(Vector3d kerbinRelative, double ut)
        {
            State(KerbinAroundSun(), SunMu, ut, out Vector3d kerbinPos, out _);
            return kerbinPos + kerbinRelative;
        }

        [Fact]
        public void TheKerbinSegmentEpoch_IsTheTrueCrossing_AndTheEventFiredJustAfterIt()
        {
            double crossingUT = KerbinSide().epoch;
            State(MunSide(), MunMu, crossingUT, out Vector3d probeFromMun, out _);

            Assert.InRange(probeFromMun.magnitude, MunSoiRadius - 5.0, MunSoiRadius + 5.0);
            Assert.InRange(SeamUT - crossingUT, 0.43, 0.44);
        }

        [Fact]
        public void AtTheSeamUT_TheTwoRecordedSides_AgreeToUnderAMetre()
        {
            Vector3d munSide = MunSideKerbinRelative(SeamUT, out Vector3d munSideVel);
            Vector3d kerbinSide = KerbinSideKerbinRelative(SeamUT, out Vector3d kerbinSideVel);

            Assert.True((munSide - kerbinSide).magnitude < 1.0,
                "seam gap " + (munSide - kerbinSide).magnitude + " m");
            Assert.True((munSideVel - kerbinSideVel).magnitude < 0.05,
                "seam velocity gap " + (munSideVel - kerbinSideVel).magnitude + " m/s");
            // Well inside the in-game cell's tolerance, which now evaluates at this one UT.
            Assert.True((munSide - kerbinSide).magnitude
                < InGameTests.BgSoiCrossingPlaybackInGameTests.SeamContinuityToleranceMeters / 100.0);
        }

        [Fact]
        public void OneSecondApart_InTheSunAnchoredFrame_ReadsTheFirstFlightsGap()
        {
            // The first cell evaluated the Mun side at seam - 0.5 s and the Kerbin side at
            // seam + 0.5 s; getTruePositionAtUT is anchored on the Sun.
            Vector3d before = Heliocentric(MunSideKerbinRelative(SeamUT - 0.5, out _), SeamUT - 0.5);
            Vector3d after = Heliocentric(KerbinSideKerbinRelative(SeamUT + 0.5, out _), SeamUT + 0.5);
            double gap = (after - before).magnitude;

            Assert.InRange(gap, 9127.7 - 5.0, 9127.7 + 5.0);
            // The same spread is what the cell's tolerance now rejects: an evaluation that
            // drifts off the seam instant reds instead of reading as continuity.
            Assert.True(gap > InGameTests.BgSoiCrossingPlaybackInGameTests.SeamContinuityToleranceMeters);

            // Kerbin-relative, the same 1 s spread is only the probe's own ~0.61 km of travel.
            double kerbinRelativeGap = (KerbinSideKerbinRelative(SeamUT + 0.5, out _)
                - MunSideKerbinRelative(SeamUT - 0.5, out _)).magnitude;
            Assert.InRange(kerbinRelativeGap, 600.0, 620.0);
        }
    }
}
