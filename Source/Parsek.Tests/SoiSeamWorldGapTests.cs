using System;
using System.Collections.Generic;
using Parsek.Tests.Generators;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// The two cross-body SOI seams of the looped-interplanetary corpus (the REAL flown
    /// duna-direct elements, <see cref="LoopedInterplanetaryFixture"/>), the recording lane
    /// S1.8's <c>SoiCrossingPlaybackInGameTests.SoiSeamWorldPositionsAreContinuous</c> reads,
    /// evaluated headless through <see cref="TwoBodyOrbit"/> in the STORED element frame with
    /// stock's Kerbin and Duna ephemerides (todo BG-SOI-SEAM-WORLD-GAP, the S1.8 half). Pins:
    /// <list type="bullet">
    /// <item>each seam's after segment epoch is the true crossing (the non-Sun side sits on
    /// its body's SOI radius there), and the handoff fired 0.015 s / 0.048 s later;</item>
    /// <item>at the seam UT the two sides agree to well under a metre: the recorder authored
    /// both conics correctly;</item>
    /// <item>the cell's first readings, 10,146.3 m (Kerbin -> Sun) and 7,284.0 m (Sun -> Duna),
    /// are the probe's heliocentric travel over the 1 s its two probe instants were apart, not
    /// a seam gap.</item>
    /// </list>
    /// The body constants are the values the game itself holds, not the rounded cfg figures:
    /// the stock prefab stores the orbit elements as floats (mean anomaly 3.14f, Duna's
    /// eccentricity and inclination) and mu_Sun is 1.1723327948324905e18 (asset-extracted, the
    /// value the B22 / V13 specs use). With the rounded 1.1723328e18 and double 3.14 the same-UT
    /// comparison reads 1.3 km / 2.1 km of along-track ephemeris drift that the game does not
    /// have; with float elements alone it still reads 97 m / 159 m. The in-game cell resolves
    /// both sides against the live bodies, so none of that reaches it.
    /// </summary>
    public class SoiSeamWorldGapTests
    {
        private const double SunMu = 1.1723327948324905e18;
        private const double KerbinMu = 3.5316e12;
        private const double DunaMu = 3.0136321e11;
        private const double KerbinSoiRadius = 84159286.48;
        private const double DunaSoiRadius = 47921949.37;

        private static OrbitSegment KerbinAroundSun() => new OrbitSegment
        {
            semiMajorAxis = 13599840256.0,
            meanAnomalyAtEpoch = (double)3.14f,
            epoch = 0.0,
            bodyName = "Sun",
        };

        private static OrbitSegment DunaAroundSun() => new OrbitSegment
        {
            semiMajorAxis = 20726155264.0,
            eccentricity = (double)0.051f,
            inclination = (double)0.06f,
            longitudeOfAscendingNode = 135.5,
            argumentOfPeriapsis = 0.0,
            meanAnomalyAtEpoch = (double)3.14f,
            epoch = 0.0,
            bodyName = "Sun",
        };

        private static List<OrbitSegment> CorpusSegments()
        {
            RecordingBuilder builder = LoopedInterplanetaryFixture.BuildRecording();
            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(builder.BuildV3Metadata(), rec);
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(builder.BuildTrajectoryNode(), rec);
            return rec.OrbitSegments;
        }

        private static (OrbitSegment before, OrbitSegment after, double seamUT) Seam(int index)
        {
            List<OrbitSegment> segments = CorpusSegments();
            var seams = TrajectoryMath.FindCrossBodySoiSeams(segments);
            Assert.Equal(2, seams.Count);
            var s = seams[index];
            return (segments[s.beforeIndex], segments[s.afterIndex], s.seamUT);
        }

        private static double MuOf(string body)
        {
            switch (body)
            {
                case "Sun": return SunMu;
                case "Kerbin": return KerbinMu;
                case "Duna": return DunaMu;
            }
            throw new ArgumentException(body);
        }

        private static void State(OrbitSegment seg, double mu, double ut,
            out Vector3d position, out Vector3d velocity)
        {
            Assert.True(TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, mu, out TwoBodyOrbit orbit));
            orbit.GetStateAtUT(ut, out position, out velocity);
        }

        // Sun-relative state of the probe as the segment's own conic places it (the frame
        // getTruePositionAtUT anchors on, minus the Sun's own world position, which cancels).
        private static Vector3d Heliocentric(OrbitSegment seg, double ut, out Vector3d velocity)
        {
            State(seg, MuOf(seg.bodyName), ut, out Vector3d pos, out Vector3d vel);
            if (seg.bodyName == "Sun")
            {
                velocity = vel;
                return pos;
            }
            OrbitSegment body = seg.bodyName == "Kerbin" ? KerbinAroundSun() : DunaAroundSun();
            State(body, SunMu, ut, out Vector3d bodyPos, out Vector3d bodyVel);
            velocity = bodyVel + vel;
            return bodyPos + pos;
        }

        [Theory]
        [InlineData(0, "Kerbin", "Sun", KerbinSoiRadius, 0.015)]
        [InlineData(1, "Sun", "Duna", DunaSoiRadius, 0.048)]
        public void TheAfterSegmentEpoch_IsTheTrueCrossing_AndTheHandoffFiredJustAfterIt(
            int seamIndex, string fromBody, string toBody, double soiRadius, double lagSeconds)
        {
            var (before, after, seamUT) = Seam(seamIndex);
            Assert.Equal(fromBody, before.bodyName);
            Assert.Equal(toBody, after.bodyName);

            OrbitSegment local = before.bodyName == "Sun" ? after : before;
            State(local, MuOf(local.bodyName), after.epoch, out Vector3d fromLocalBody, out _);

            Assert.InRange(fromLocalBody.magnitude, soiRadius - 5.0, soiRadius + 5.0);
            Assert.InRange(seamUT - after.epoch, lagSeconds - 0.001, lagSeconds + 0.001);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void AtTheSeamUT_TheTwoRecordedSides_AgreeToUnderAMetre(int seamIndex)
        {
            var (before, after, seamUT) = Seam(seamIndex);
            Vector3d beforePos = Heliocentric(before, seamUT, out Vector3d beforeVel);
            Vector3d afterPos = Heliocentric(after, seamUT, out Vector3d afterVel);

            double gap = (afterPos - beforePos).magnitude;
            double velocityGap = (afterVel - beforeVel).magnitude;
            Assert.True(gap < 1.0, "seam gap " + gap + " m");
            Assert.True(velocityGap < 0.01, "seam velocity gap " + velocityGap + " m/s");
            // Far inside the in-game cell's tolerance, which evaluates at this one UT.
            Assert.True(gap < InGameTests.SoiCrossingPlaybackInGameTests.SeamContinuityToleranceMeters / 1000.0);
        }

        [Theory]
        [InlineData(0, 10146.3)]
        [InlineData(1, 7284.0)]
        public void OneSecondApart_InTheSunAnchoredFrame_ReadsTheFirstFlightsGap(
            int seamIndex, double readingMeters)
        {
            var (before, after, seamUT) = Seam(seamIndex);

            // The first cell evaluated the before side at seam - 0.5 s and the after side at
            // seam + 0.5 s; getTruePositionAtUT is anchored on the Sun.
            Vector3d early = Heliocentric(before, seamUT - 0.5, out _);
            Vector3d late = Heliocentric(after, seamUT + 0.5, out Vector3d lateVel);
            double gap = (late - early).magnitude;

            Assert.InRange(gap, readingMeters - 0.5, readingMeters + 0.5);
            // It is the probe's heliocentric speed times the 1 s spread.
            Assert.InRange(lateVel.magnitude, readingMeters - 0.5, readingMeters + 0.5);
            // The cell's tolerance now rejects that spread: an evaluation that drifts off the
            // seam instant reds instead of reading as continuity.
            Assert.True(gap > InGameTests.SoiCrossingPlaybackInGameTests.SeamContinuityToleranceMeters);
        }

        [Fact]
        public void ASideEvaluatedAgainstTheWrongBody_IsFarAboveTheTolerance()
        {
            // The defect class the cell exists for: the Kerbin-side conic read as if it were
            // Sun-relative (the body anchor dropped) lands a whole Kerbin orbit radius away.
            var (before, after, seamUT) = Seam(0);
            State(before, KerbinMu, seamUT, out Vector3d unanchored, out _);
            Vector3d afterPos = Heliocentric(after, seamUT, out _);

            Assert.True((afterPos - unanchored).magnitude
                > 1.0e6 * InGameTests.SoiCrossingPlaybackInGameTests.SeamContinuityToleranceMeters);
        }
    }
}
