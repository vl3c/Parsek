using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the ENCOUNTER-GEOMETRY instrument's pure core
    /// (<see cref="SeamEndpointOracle"/>): at a recorded SOI handoff, is the arc the pipeline is
    /// actually rendering inside the destination body's sphere of influence, or does it dead-end
    /// outside it?
    ///
    /// <para>The two measured populations the tolerance is calibrated between are pinned here as
    /// LITERAL numbers, so the calibration is a test rather than a comment:</para>
    /// <list type="bullet">
    ///   <item><b>DEFECT (2026-06-15 looped re-aim forensics):</b> Sun-&gt;Duna 49.19 Mm against a
    ///     47,921,949 m sphere (ratio 1.027) and Kerbin-&gt;Sun 87.76 Mm against an 84,159,286 m
    ///     sphere (ratio 1.043). Both must raise.</item>
    ///   <item><b>HEALTHY (S1.8 <c>S1.8-soi-crossing-playback</c>, live-proven on the committed
    ///     duna-direct fixture):</b> the two flown seams resolve continuous to 10,146.3 m and
    ///     7,284.0 m, against a 25 km pin. None of those may raise - that is the whole justification
    ///     for the 0.005 slack over the ideal 1.0 boundary.</item>
    /// </list>
    ///
    /// <para>Pure math: no Unity, no shared static state, so this class does NOT need the Sequential
    /// collection.</para>
    /// </summary>
    public class SeamEndpointOracleTests
    {
        // The two spheres the measured numbers were taken against (stock KSP 1.12).
        private const double KerbinSoi = 84159286.0;
        private const double DunaSoi = 47921949.0;

        [Fact]
        public void TheMeasuredDunaDefectRaises()
        {
            // 2026-06-15: Sun->Duna jump 49.19 Mm = 1.027x Duna's SOI.
            var r = SeamEndpointOracle.Evaluate(49190000.0, DunaSoi);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OutsideSoi);
            Assert.InRange(r.Ratio, 1.026, 1.028);
            Assert.Equal(SeamEndpointOracle.DefaultRatioTolerance, r.RatioTolerance);
            Assert.Equal(49190000.0, r.EndpointDistanceMeters);
            Assert.Equal(DunaSoi, r.SoiRadiusMeters);
        }

        [Fact]
        public void TheMeasuredKerbinDefectRaises()
        {
            // 2026-06-15: Kerbin->Sun jump 87.76 Mm = 1.043x Kerbin's SOI.
            var r = SeamEndpointOracle.Evaluate(87760000.0, KerbinSoi);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OutsideSoi);
            Assert.InRange(r.Ratio, 1.042, 1.044);
        }

        [Fact]
        public void AFaithfulLoopMissingTheDestinationEntirelyRaises()
        {
            // The far end of the defect family: an arc replayed at a loop shift the destination has
            // moved on from misses by interplanetary distances, not by a fraction of a sphere.
            var r = SeamEndpointOracle.Evaluate(2.0e11, DunaSoi);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OutsideSoi);
            Assert.True(r.Ratio > 1000.0);
        }

        [Theory]
        // Deep inside the sphere (a captured arc well past entry).
        [InlineData(1.0e6, DunaSoi)]
        // Just inside.
        [InlineData(DunaSoi * 0.999, DunaSoi)]
        // EXACTLY on the sphere - the textbook correct encounter, ratio 1.0.
        [InlineData(DunaSoi, DunaSoi)]
        public void AnArcThatReachesTheSphereDoesNotRaise(double dist, double soi)
        {
            var r = SeamEndpointOracle.Evaluate(dist, soi);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OutsideSoi);
            Assert.True(r.Ratio <= 1.0);
        }

        [Theory]
        // S1.8 MEASURED healthy seam continuity: 10,146.3 m (Kerbin->Sun) and 7,284.0 m (Sun->Duna).
        [InlineData(KerbinSoi + 10146.3, KerbinSoi)]
        [InlineData(DunaSoi + 7284.0, DunaSoi)]
        // ...and the 25 km PIN those measurements are gated against, on the tighter sphere.
        [InlineData(DunaSoi + 25000.0, DunaSoi)]
        public void TheMeasuredHealthySeamContinuityDoesNotRaise(double dist, double soi)
        {
            var r = SeamEndpointOracle.Evaluate(dist, soi);

            Assert.True(r.HasMeasurement);
            Assert.False(
                r.OutsideSoi,
                "a MEASURED healthy seam must not raise - this is the entire justification for the "
                + "0.005 slack over the ideal 1.0 boundary");
            // ...and it must clear by a real margin, not by a hair: the healthy band is ~10x below
            // the tolerance, which is ~5x below the smallest measured defect.
            Assert.True(r.Ratio < 1.001);
        }

        [Fact]
        public void TheIdealBoundaryIsExactlyOneWhenTheCallerPinsIt()
        {
            // The brief's ideal threshold, available to any caller that wants it: a correct encounter
            // is <= 1.0 BY DEFINITION, so with tolerance 1.0 exactly-on-the-sphere passes and the
            // smallest overshoot fails.
            var onSphere = SeamEndpointOracle.Evaluate(DunaSoi, DunaSoi, ratioTolerance: 1.0);
            Assert.True(onSphere.HasMeasurement);
            Assert.False(onSphere.OutsideSoi);
            Assert.Equal(1.0, onSphere.Ratio);
            Assert.Equal(1.0, onSphere.RatioTolerance);

            var justOutside = SeamEndpointOracle.Evaluate(DunaSoi + 1000.0, DunaSoi, ratioTolerance: 1.0);
            Assert.True(justOutside.HasMeasurement);
            Assert.True(justOutside.OutsideSoi);
        }

        [Fact]
        public void TheDefaultToleranceBoundaryIsInclusive()
        {
            // Exactly AT the default tolerance is not outside; a clear step beyond it is.
            double atTolerance = DunaSoi * SeamEndpointOracle.DefaultRatioTolerance;
            var at = SeamEndpointOracle.Evaluate(atTolerance, DunaSoi);
            Assert.True(at.HasMeasurement);
            Assert.False(at.OutsideSoi);

            var beyond = SeamEndpointOracle.Evaluate(DunaSoi * 1.01, DunaSoi);
            Assert.True(beyond.HasMeasurement);
            Assert.True(beyond.OutsideSoi);
        }

        [Theory]
        [InlineData(double.NaN, DunaSoi)]
        [InlineData(double.PositiveInfinity, DunaSoi)]
        [InlineData(double.NegativeInfinity, DunaSoi)]
        // A negative distance is impossible from a magnitude; treat it as a broken input, not a pass.
        [InlineData(-1.0, DunaSoi)]
        [InlineData(1.0e9, double.NaN)]
        [InlineData(1.0e9, double.PositiveInfinity)]
        // A zero / negative sphere is an unresolved or missing body, not "everything is outside".
        [InlineData(1.0e9, 0.0)]
        [InlineData(1.0e9, -1.0)]
        public void DegenerateInputsProduceNoMeasurementAndNeverRaise(double dist, double soi)
        {
            var r = SeamEndpointOracle.Evaluate(dist, soi);

            Assert.False(r.HasMeasurement);
            Assert.False(r.OutsideSoi);
            Assert.Equal(0.0, r.Ratio);
            Assert.Equal(0.0, r.EndpointDistanceMeters);
            Assert.Equal(0.0, r.SoiRadiusMeters);
            // The tolerance is still reported so a caller can log what it would have compared against.
            Assert.Equal(SeamEndpointOracle.DefaultRatioTolerance, r.RatioTolerance);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        // Below 1.0 would call a REAL encounter (inside the sphere) a defect - reject it.
        [InlineData(0.5)]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void AnUnusableToleranceFallsBackToTheDefault(double tolerance)
        {
            var r = SeamEndpointOracle.Evaluate(DunaSoi * 0.5, DunaSoi, tolerance);

            Assert.True(r.HasMeasurement);
            Assert.Equal(SeamEndpointOracle.DefaultRatioTolerance, r.RatioTolerance);
            Assert.False(r.OutsideSoi);
        }

        [Fact]
        public void TheToleranceSeparatesTheTwoMeasuredPopulations()
        {
            // The calibration claim itself, as an assertion rather than prose: the tolerance sits
            // strictly above every measured healthy reading and strictly below every measured defect.
            double worstHealthy = (DunaSoi + 25000.0) / DunaSoi;      // the S1.8 25 km pin
            double smallestDefect = 49190000.0 / DunaSoi;             // the 2026-06-15 Duna reading

            Assert.True(worstHealthy < SeamEndpointOracle.DefaultRatioTolerance);
            Assert.True(SeamEndpointOracle.DefaultRatioTolerance < smallestDefect);
            // ...with real margin on both sides, not a knife edge.
            Assert.True(SeamEndpointOracle.DefaultRatioTolerance - 1.0 > (worstHealthy - 1.0) * 5.0);
            Assert.True(smallestDefect - 1.0 > (SeamEndpointOracle.DefaultRatioTolerance - 1.0) * 4.0);
        }

        // --- PASS ACCOUNTING: the anti-vacuity half ---
        //
        // The instrument shipped with the raise above and no accounting, and five re-flights then
        // produced zero raises that nobody could interpret: sound geometry, or a lens that never
        // evaluated a seam? (It was the second - the drive clock sat in each recording's LAST
        // OrbitSegment, so every frame bailed on `no-cross-body-successor`.) These cells pin the two
        // pure pieces of the fix: the emit GUARD, whose whole job is to keep the first emit off a
        // ghostless frame, and the summary GRAMMAR, which has to read like the faithful-parity sibling.

        [Fact]
        public void AGhostlessPassEmitsNoSummary()
        {
            // THE load-bearing case. A pass where the lens was handed nothing has zero evaluated and
            // zero skip reasons, and MUST NOT emit: the summary rides a shared 5 s rate-limit key whose
            // window opens on the first CALL, so a ghostless emit at scene entry would swallow every
            // real reading for the following five seconds - the measured probe-frame-summary failure
            // mode on the V4 / V6M / V6T / V7M / V7T lanes, which quit 2.2-3.8 s after entry.
            Assert.False(SeamEndpointOracle.ShouldEmitPassSummary(0, 0));
        }

        [Theory]
        // Measured clean: ghosts reached the lens and every one produced a check.
        [InlineData(3, 0)]
        // The five-re-flight shape: nothing evaluated, but the lens WAS offered ghosts and skipped
        // them. This is the reading that was missing, so it must emit.
        [InlineData(0, 1)]
        [InlineData(2, 4)]
        public void APassThatTouchedTheLensEmits(int evaluated, int distinctSkipReasons)
        {
            Assert.True(SeamEndpointOracle.ShouldEmitPassSummary(evaluated, distinctSkipReasons));
        }

        [Fact]
        public void TheSummaryMirrorsTheFaithfulParityFieldGrammar()
        {
            string line = SeamEndpointOracle.FormatPassSummary(
                4, 1,
                new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, int>(
                        "no-cross-body-successor", 7),
                    new System.Collections.Generic.KeyValuePair<string, int>("body-mismatch", 2),
                });

            // `<lens> summary <counter>=<n> ...` then zero-or-more ` skip.<reason>=<n>`, exactly the
            // shape of `faithful-parity summary sampled=.. overTolerance=.. skip.<reason>=<n>`.
            Assert.Equal(
                "seam-endpoint summary evaluated=4 outsideSoi=1 "
                + "skip.no-cross-body-successor=7 skip.body-mismatch=2",
                line);
        }

        [Fact]
        public void TheAllSkippedPassRendersItsReasonsAndAZeroEvaluated()
        {
            // The measured five-re-flight reading, rendered: this exact line is what would have made
            // the offline .prec reconstruction unnecessary.
            string line = SeamEndpointOracle.FormatPassSummary(
                0, 0,
                new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, int>(
                        "no-cross-body-successor", 2),
                });

            Assert.Equal(
                "seam-endpoint summary evaluated=0 outsideSoi=0 skip.no-cross-body-successor=2",
                line);
        }

        [Fact]
        public void APassWithNoSkipsRendersJustTheTwoCounters()
        {
            Assert.Equal(
                "seam-endpoint summary evaluated=2 outsideSoi=0",
                SeamEndpointOracle.FormatPassSummary(
                    2, 0, new System.Collections.Generic.KeyValuePair<string, int>[0]));
            // A null tally is the same line, not a throw: the emitter must never be the thing that
            // takes the probe down.
            Assert.Equal(
                "seam-endpoint summary evaluated=2 outsideSoi=0",
                SeamEndpointOracle.FormatPassSummary(2, 0, null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AnUnnamedSkipReasonIsBucketedNotDropped(string reason)
        {
            // Counts must add up to the ghost-frames offered, so an unnamed bucket is still rendered.
            string line = SeamEndpointOracle.FormatPassSummary(
                0, 0,
                new[] { new System.Collections.Generic.KeyValuePair<string, int>(reason, 3) });

            Assert.Equal(
                "seam-endpoint summary evaluated=0 outsideSoi=0 skip."
                + SeamEndpointOracle.PassSummaryUnknownSkipReason + "=3",
                line);
        }

        [Fact]
        public void EveryCaptureSkipReasonRendersAsItsOwnBucket()
        {
            // The eleven reasons ComputeSeamEndpointGeometry can produce plus the one the ORACLE owns
            // (`no-soi-measurement`, a sample with no usable ratio - the Sun as a destination). Pinned
            // as a set so a reason added to the capture without a bucket is visible here; the counting
            // itself lives in the Unity sampler and is not reachable headlessly.
            var tally = new System.Collections.Generic.List<
                System.Collections.Generic.KeyValuePair<string, int>>();
            string[] reasons =
            {
                "no-rendered-orbit", "no-recId", "no-recording-or-segments", "no-covering-segment",
                "no-covering-body", "body-mismatch", "no-cross-body-successor", "seam-ut-not-finite",
                "seam-behind-clock", "to-body-unresolved", "propagation-threw", "no-soi-measurement",
            };
            foreach (string r in reasons)
                tally.Add(new System.Collections.Generic.KeyValuePair<string, int>(r, 1));

            string line = SeamEndpointOracle.FormatPassSummary(0, 0, tally);

            Assert.True(SeamEndpointOracle.ShouldEmitPassSummary(0, tally.Count));
            foreach (string r in reasons)
                Assert.Contains(" skip." + r + "=1", line);
        }
    }
}
