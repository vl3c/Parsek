using System;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 0 / migration §2 - the HEADLESS REGRESSION SCENARIO SET for the recorded-vs-rendered
    /// parity oracle (<see cref="RenderParityOracle"/>). Each test builds a representative synthetic
    /// geometry pair (a "recorded"/"intended" reference curve + the geometry the pipeline "rendered")
    /// and asserts the oracle's verdict, so every later phase has an objective "did I break rendering?"
    /// gate over a fixed set of situations from the design §11.5 coverage matrix.
    ///
    /// <para><b>What this suite is and is NOT.</b> The oracle's headless contract takes ALREADY-FRAMED
    /// flat <c>double[]</c> XYZ triples in one consistent metres frame (the Unity sampler in
    /// <see cref="MapRenderProbe"/> supplies them in game). So this suite models each matrix row's
    /// geometry directly: an orbit via a tiny PURE Kepler-circle/ellipse sampler, an ascent/descent via a
    /// vertical metre profile, a heliocentric arc via a wide circle, etc. The LIVE Unity capture path
    /// (reading <c>OrbitDriver.orbit</c> / <c>GetWorldPos3D</c>) is validated separately by the in-game
    /// <c>RenderParitySamplerFixtureTest</c> (capture-harness) + the <c>parity-baseline</c> in-game test;
    /// those are the "green but blind" guard the migration plan calls out. This suite is the diff-verdict
    /// gate per matrix row, NOT a second capture-path test.</para>
    ///
    /// <para><b>Two halves per situation.</b> A KNOWN-GOOD pair (recorded == "rendered", sampled the same
    /// way, incl. a loop-shifted faithful ghost) must read <c>OverTolerance == false</c> (zero drift); a
    /// deliberately-DRIFTED pair (rendered perturbed beyond tolerance: a rotated/offset arc, a mis-applied
    /// loop shift, a wrong-body orbit) must read <c>OverTolerance == true</c>. Both
    /// <see cref="RenderParityOracle.ParityMode"/> values are exercised (faithful = rendered-vs-recorded;
    /// synthesized = rendered-vs-INTENDED arc, the re-aim cases) so neither mode is left uncovered.</para>
    ///
    /// <para>Pure math, no Unity ECalls, no shared static state - so NOT in the Sequential collection.
    /// See <c>docs/dev/plans/map-ts-render-phase0-regression-traceability.md</c> for the scenario ->
    /// §11.5-matrix-row mapping (the DoD traceability artifact this suite anchors).</para>
    /// </summary>
    public class RenderParityRegressionScenarioTests
    {
        // ===================================================================================
        //  Pure synthetic-geometry builders (a recording's geometry as flat XYZ triples)
        // ===================================================================================

        // A circular orbit of the given radius in the body-relative XZ plane (Y = out-of-plane), sampled
        // at `count` evenly-spaced true anomalies starting at `phase0` radians. This is the headless
        // analogue of the in-game OrbitRelativePositionYup sample of a circular equatorial orbit: every
        // point has magnitude == radius and (for inc=0) zero Y. The recorded and rendered sides build the
        // SAME curve at the SAME phases for a faithful pair.
        private static double[] CircleXZ(double radius, int count, double phase0 = 0.0)
        {
            var flat = new double[count * 3];
            for (int i = 0; i < count; i++)
            {
                double theta = phase0 + (2.0 * Math.PI * i) / count;
                flat[i * 3] = radius * Math.Cos(theta);   // x
                flat[i * 3 + 1] = 0.0;                     // y (equatorial)
                flat[i * 3 + 2] = radius * Math.Sin(theta); // z
            }
            return flat;
        }

        // A circular equatorial orbit position function in body-relative Y-up metres at a set of UTs - the
        // headless analogue of OrbitRelativePositionYup on a circular orbit. The mean anomaly at UT `t` is
        // omega * (t - epoch), so a loop-shifted ghost whose epoch is baked with +shift evaluated at the
        // live clock lands on the SAME angle as the raw-epoch recorded orbit evaluated at (t - shift). This
        // lets the loop-shift row ACTUALLY apply a shift (via RenderGeometrySampler.ShiftSampleUTs) and
        // prove the "shift the clock, not the shape" invariant headlessly, rather than diffing a circle
        // against an identical copy of itself.
        private static double[] CircleAtUTs(double radius, double omega, double epoch, double[] uts)
        {
            var flat = new double[uts.Length * 3];
            for (int i = 0; i < uts.Length; i++)
            {
                double theta = omega * (uts[i] - epoch);
                flat[i * 3] = radius * Math.Cos(theta);
                flat[i * 3 + 1] = 0.0;
                flat[i * 3 + 2] = radius * Math.Sin(theta);
            }
            return flat;
        }

        // An eccentric orbit in the XZ plane: r(theta) = a(1-e^2)/(1+e cos theta), sampled at `count`
        // evenly-spaced true anomalies. Used for the aerobraking / eccentric-orbit rows where the arc
        // shape (not just radius) must match.
        private static double[] EllipseXZ(double sma, double ecc, int count, double phase0 = 0.0)
        {
            var flat = new double[count * 3];
            double p = sma * (1.0 - ecc * ecc);
            for (int i = 0; i < count; i++)
            {
                double theta = phase0 + (2.0 * Math.PI * i) / count;
                double r = p / (1.0 + ecc * Math.Cos(theta));
                flat[i * 3] = r * Math.Cos(theta);
                flat[i * 3 + 1] = 0.0;
                flat[i * 3 + 2] = r * Math.Sin(theta);
            }
            return flat;
        }

        // A near-vertical ascent/descent profile: a column of points climbing/falling in +Y over a small
        // downrange +X drift. The atmospheric portions (ascent, descent-to-landing) draw as a traced
        // polyline, not a conic, so the geometry is a line of metres, not a circle.
        private static double[] VerticalProfile(
            double startAltitude, double endAltitude, double downrange, int count)
        {
            var flat = new double[count * 3];
            for (int i = 0; i < count; i++)
            {
                double t = count == 1 ? 0.0 : (double)i / (count - 1);
                flat[i * 3] = downrange * t;                                  // x downrange
                flat[i * 3 + 1] = startAltitude + (endAltitude - startAltitude) * t; // y altitude
                flat[i * 3 + 2] = 0.0;
            }
            return flat;
        }

        // Rotate every point of a flat XYZ set about the Y axis by `deg` degrees. Models the documented
        // "looped re-aim / icon-off-orbit rotation" draw bug: the rendered arc is the recorded arc spun
        // about the body axis, so it has the same shape but is angularly offset - the exact failure the
        // oracle must flag (a continuous nearest-point projection still sees the perpendicular gap).
        private static double[] RotateAboutY(double[] xyz, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            var outp = new double[xyz.Length];
            int n = xyz.Length / 3;
            for (int i = 0; i < n; i++)
            {
                double x = xyz[i * 3], y = xyz[i * 3 + 1], z = xyz[i * 3 + 2];
                outp[i * 3] = c * x + s * z;
                outp[i * 3 + 1] = y;
                outp[i * 3 + 2] = -s * x + c * z;
            }
            return outp;
        }

        // Uniformly translate a flat XYZ set by (dx,dy,dz) metres.
        private static double[] Translate(double[] xyz, double dx, double dy, double dz)
        {
            var outp = new double[xyz.Length];
            int n = xyz.Length / 3;
            for (int i = 0; i < n; i++)
            {
                outp[i * 3] = xyz[i * 3] + dx;
                outp[i * 3 + 1] = xyz[i * 3 + 1] + dy;
                outp[i * 3 + 2] = xyz[i * 3 + 2] + dz;
            }
            return outp;
        }

        // Append the points of a second flat XYZ array onto a copy of the first (the headless analogue of the
        // synth probe anchoring the live ICON point onto the end of the rendered orbit-sample set - the
        // rendCount = sampleCount + 1 case). Both arrays are length-3 multiples by construction.
        private static double[] AppendPoint(double[] xyz, double[] extra)
        {
            var outp = new double[xyz.Length + extra.Length];
            Array.Copy(xyz, 0, outp, 0, xyz.Length);
            Array.Copy(extra, 0, outp, xyz.Length, extra.Length);
            return outp;
        }

        // Scenario constants (body-relative metres, KSP-scale).
        private const double LkoRadius = 700_000.0;       // ~low Kerbin orbit
        private const double MunOrbitRadius = 12_000_000.0; // Munar-distance arc
        private const double HelioRadius = 1.3e10;         // heliocentric transfer scale
        private const int SampleCount = 24;

        // ===================================================================================
        //  FAITHFUL known-good rows -> zero drift
        // ===================================================================================

        [Fact]
        public void Faithful_AscentToOrbit_KnownGood_ZeroDrift()
        {
            // Row: faithful ascent -> orbit. Recorded == rendered (a circular LKO arc).
            double[] recorded = CircleXZ(LkoRadius, SampleCount);
            double[] rendered = CircleXZ(LkoRadius, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.True(r.MaxDeviationMeters <= 1e-3);
        }

        [Fact]
        public void Faithful_AtmosphericAscent_KnownGood_ZeroDrift()
        {
            // Row: atmospheric ascent (a traced polyline, not a conic). Recorded == rendered profile.
            double[] recorded = VerticalProfile(0.0, 70_000.0, 30_000.0, SampleCount);
            double[] rendered = VerticalProfile(0.0, 70_000.0, 30_000.0, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Faithful_DescentToLanding_KnownGood_ZeroDrift()
        {
            // Row: atmospheric descent to landing/splashdown. Recorded == rendered descent profile.
            double[] recorded = VerticalProfile(60_000.0, 0.0, 20_000.0, SampleCount);
            double[] rendered = VerticalProfile(60_000.0, 0.0, 20_000.0, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Faithful_SoiCrossing_KnownGood_ZeroDrift()
        {
            // Row: single-level SOI transition (Kerbin -> Sun). Faithful rendering draws the recorded
            // post-crossing heliocentric arc verbatim - the diff is taken AFTER the crossing in the new
            // reference body's own frame (the probe suppresses the cross-body delta via bodyChanged), so a
            // faithful SOI leg reads zero drift on the heliocentric segment.
            double[] recorded = CircleXZ(HelioRadius, SampleCount, phase0: 0.3);
            double[] rendered = CircleXZ(HelioRadius, SampleCount, phase0: 0.3);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Faithful_BgOnRailsAllOrbital_KnownGood_ZeroDrift()
        {
            // Row: BG-on-rails recorded vessel (all-orbital chain, no Descent/Surface phase). The faithful
            // render is the recorded orbit arc; recorded == rendered.
            double[] recorded = CircleXZ(MunOrbitRadius, SampleCount);
            double[] rendered = CircleXZ(MunOrbitRadius, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Faithful_AerobrakingEccentric_KnownGood_ZeroDrift()
        {
            // Row: aerobraking (many periapsis grazes) - alternating conic/traced. The eccentric conic
            // portion's arc SHAPE (not just radius) must match; recorded == rendered ellipse.
            double[] recorded = EllipseXZ(MunOrbitRadius, 0.6, SampleCount);
            double[] rendered = EllipseXZ(MunOrbitRadius, 0.6, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Faithful_LoopShiftedGhost_SameOrbit_ZeroDrift()
        {
            // Row: loop a single recording. A loop-shifted FAITHFUL ghost renders the SAME orbit shape; the
            // loop shift only sets WHERE on the orbit the icon sits, not the orbit's geometry, so the diff
            // is ~0. This ACTUALLY applies the shift (it does not diff a circle against an identical copy):
            // the RENDERED orbit's epoch is baked with +shift and sampled at the LIVE-clock UTs (mirroring
            // StockConicTreatment.SeedAndDriveLive: epoch = seg.epoch + loopShift), and the RECORDED
            // reference is the raw-epoch orbit sampled at the recorded-clock UTs (liveUT - shift, via
            // RenderGeometrySampler.ShiftSampleUTs). Both therefore land on the SAME mean-anomaly angle, so
            // a correct "shift the clock, not the shape" pipeline reads zero drift. (This is the headless
            // analogue of the production probe's BuildPhaseMatchedReferenceOrbit + same-UT sampling; the
            // in-game RenderParityBaselineTest loop-shifted variant proves the live path.)
            const double omega = 2.0 * Math.PI / 5400.0; // ~1.5 h circular period
            const double epoch = 100_000.0;
            const double shift = 1300.0;                  // a sizeable fraction of the period
            const double liveCenterUT = epoch + 4.0 * 5400.0; // four loops later

            double[] renderedUTs = RenderGeometrySampler.BuildSampleUTs(
                liveCenterUT, 5400.0 * 0.25, SampleCount);
            // Recorded reference sampled at the recorded-clock UTs (liveUT - shift), raw epoch.
            double[] recordedUTs = RenderGeometrySampler.ShiftSampleUTs(renderedUTs, shift);
            // Rendered orbit epoch baked with +shift, sampled at the live-clock UTs.
            double[] rendered = CircleAtUTs(LkoRadius, omega, epoch + shift, renderedUTs);
            double[] recorded = CircleAtUTs(LkoRadius, omega, epoch, recordedUTs);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);

            // Negative control: drop the shift cancellation (sample the raw-epoch recorded orbit at the
            // LIVE clock instead of the recorded clock) and the loop phase no longer cancels - the two
            // orbits trace different arcs and the oracle MUST flag drift. This is exactly the BLOCKER the
            // production probe's phase-matched epoch fixes; here it proves the row is a real shift, not a
            // tautological circle-vs-itself.
            double[] recordedUnshifted = CircleAtUTs(LkoRadius, omega, epoch, renderedUTs);
            var rBug = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recordedUnshifted, rendered);
            Assert.True(rBug.HasMeasurement);
            Assert.True(rBug.OverTolerance);
        }

        [Fact]
        public void Faithful_DifferentSampleDensity_SameCurve_ZeroDrift()
        {
            // Row: faithful orbit at DIFFERENT sample densities (the common recorded-points-list vs dense
            // Vectrosity-line case). The oracle's nearest-point projection is count-independent: a recorded
            // reference point projects onto the rendered POLYLINE, so as long as the rendered line is
            // sampled finely enough to approximate the curve (a dense Vectrosity line, like the stock orbit
            // renderer draws), a coarser recorded reference reads ~0 drift. (A pathologically coarse
            // rendered N-gon is NOT a faithful draw of a circle - its chords cut megametres inside the arc
            // - and the oracle correctly flags that; that is a drift case, not a known-good one.)
            double[] recorded = CircleXZ(LkoRadius, 9);   // coarse recorded points
            double[] rendered = CircleXZ(LkoRadius, 256);  // dense rendered polyline (Vectrosity-like)

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.True(r.CountMismatch); // recorded (9) vs rendered (256)
        }

        // ===================================================================================
        //  FAITHFUL drifted rows -> drift flagged
        // ===================================================================================

        [Fact]
        public void Faithful_LoopRotationBug_OffOrbitArc_FlagsDrift()
        {
            // Row (drift): the documented looped-re-aim / icon-off-orbit ROTATION bug. The rendered arc is
            // the recorded arc spun 20 deg about the body axis - same shape, angularly offset. The oracle
            // must flag it (a faithful member is drawing the wrong geometry).
            double[] recorded = CircleXZ(LkoRadius, SampleCount);
            double[] rendered = RotateAboutY(recorded, 20.0);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
        }

        [Fact]
        public void Faithful_WrongBodyOrbit_DifferentScale_FlagsDrift()
        {
            // Row (drift): the rendered orbit is reseeded onto the WRONG body / wrong elements - here a
            // Mun-scale orbit drawn where an LKO orbit was recorded. The radii differ by ~17x, far beyond
            // any scale-derived tolerance.
            double[] recorded = CircleXZ(LkoRadius, SampleCount);
            double[] rendered = CircleXZ(MunOrbitRadius, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
        }

        [Fact]
        public void Faithful_MisAppliedLoopShift_PhaseOffset_FlagsDrift()
        {
            // Row (drift): a MIS-applied loop shift. Unlike the correct loop case (same shape, same phase
            // set), a wrong shift samples the rendered orbit at a different ROTATED phase set whose curve
            // no longer overlays the recorded one - modelled here as a large rotation (a half-turn) so the
            // rendered arc spans the opposite side of the body. (A pure phase slide along the SAME closed
            // circle is invisible to a nearest-point diff; the visible loop-shift bug is a wrong-orbit /
            // wrong-rotation reseed, which this rotation models.)
            double[] recorded = EllipseXZ(LkoRadius, 0.5, SampleCount);
            double[] rendered = RotateAboutY(EllipseXZ(LkoRadius, 0.5, SampleCount), 90.0);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
        }

        [Fact]
        public void Faithful_DescentArcOffset_FlagsDrift()
        {
            // Row (drift): a descent rendered with a large lateral offset (a mis-stitched deorbit arc
            // landing off the recorded ground track). 5 km off a ~60 km-scale descent is well over
            // tolerance.
            double[] recorded = VerticalProfile(60_000.0, 0.0, 20_000.0, SampleCount);
            double[] rendered = Translate(recorded, 0.0, 0.0, 5_000.0);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
        }

        [Fact]
        public void Faithful_AerobrakingWrongEccentricity_FlagsDrift()
        {
            // Row (drift): the eccentric aerobraking conic rendered with the WRONG eccentricity - same sma
            // but ecc 0.6 recorded vs 0.2 rendered, so periapsis/apoapsis differ by megametres.
            double[] recorded = EllipseXZ(MunOrbitRadius, 0.6, SampleCount);
            double[] rendered = EllipseXZ(MunOrbitRadius, 0.2, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
        }

        // ===================================================================================
        //  SYNTHESIZED known-good rows -> zero drift (rendered == the producer's INTENDED arc)
        // ===================================================================================

        [Fact]
        public void Synthesized_ReaimedLoopIntendedArc_KnownGood_ZeroDrift()
        {
            // Row: re-aim with a synodic target move. In SYNTHESIZED mode the reference is the producer's
            // INTENDED re-aimed arc (NOT the recorded transfer), and the oracle validates DRAW-FIDELITY
            // (rendered == intended), not solve-correctness. A correct draw of the intended heliocentric
            // transfer reads zero drift.
            double[] intended = EllipseXZ(HelioRadius, 0.25, SampleCount);
            double[] rendered = EllipseXZ(HelioRadius, 0.25, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, intended, rendered);

            Assert.Equal(RenderParityOracle.ParityMode.Synthesized, r.Mode);
            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Synthesized_MixedFaithfulReaimedMembers_IntendedArc_ZeroDrift()
        {
            // Row: mixed faithful + re-aimed members. The re-aimed member's intended arc (a Mun-scale
            // departure conic) drawn faithfully reads zero drift in synthesized mode.
            double[] intended = CircleXZ(MunOrbitRadius, SampleCount, phase0: 0.7);
            double[] rendered = CircleXZ(MunOrbitRadius, SampleCount, phase0: 0.7);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, intended, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Synthesized_DescentReStitchIntendedG1Arc_KnownGood_ZeroDrift()
        {
            // Row: Phase 6 descent re-stitch gates in SYNTHESIZED mode (the stitcher intentionally CHANGES
            // the deorbit geometry vs the recorded arc, so faithful-vs-recorded is the wrong axis). A
            // correct draw of the stitcher's intended G1 deorbit arc reads zero drift against the intended
            // reference.
            double[] intendedDeorbit = VerticalProfile(70_000.0, 0.0, 40_000.0, SampleCount);
            double[] rendered = VerticalProfile(70_000.0, 0.0, 40_000.0, SampleCount);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, intendedDeorbit, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void Synthesized_LoopShiftedReaimedSeed_PhaseMatched_ZeroDrift()
        {
            // BLOCKER row: a LOOPED re-aimed (synthesized) member. The producer DRIVES the rendered conic at
            // epoch = seg.epoch + loopShift (StockConicTreatment.SeedAndDriveLive) and propagates it at the
            // LIVE clock, so the synthesized reference (the producer's intended seed) MUST be built with the
            // SAME loop-shift epoch bake (MapRenderProbe.BuildPhaseMatchedReferenceOrbit, the BLOCKER fix) and
            // sampled at the SAME live-clock UTs - otherwise it traces a different mean-anomaly half-arc and
            // false-fires on a CORRECT draw. This is the headless analogue of the production probe's phase-
            // matched synthesized reference (the in-game SynthesizedParity_LoopShiftedGhost_PhaseMatched_
            // ZeroDrift drives the real seam). It ACTUALLY applies the shift (both sides baked +shift, sampled
            // at the live UTs), so a correct "shift the clock, not the shape" pipeline reads zero drift.
            const double omega = 2.0 * Math.PI / 5400.0; // ~1.5 h circular period
            const double epoch = 100_000.0;
            const double shift = 1300.0;                  // a sizeable fraction of the period
            const double liveCenterUT = epoch + 4.0 * 5400.0; // four loops later

            double[] renderedUTs = RenderGeometrySampler.BuildSampleUTs(
                liveCenterUT, 5400.0 * 0.25, SampleCount);
            // BOTH the rendered orbit AND the intended-seed reference are baked with epoch + shift and sampled
            // at the SAME live-clock UTs (the synthesized path samples both at currentUT, unlike the faithful
            // path which can remap the recorded UTs): a faithful loop draw of the intended seed reads ~0.
            double[] rendered = CircleAtUTs(LkoRadius, omega, epoch + shift, renderedUTs);
            double[] intendedPhaseMatched = CircleAtUTs(LkoRadius, omega, epoch + shift, renderedUTs);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, intendedPhaseMatched, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);

            // LOAD-BEARING negative control: drop the phase-match (build the intended seed at the RAW epoch -
            // the pre-fix BuildOrbitFromSegment behavior) and sample it at the SAME live UTs. The loop phase
            // no longer cancels, so the reference and the rendered conic trace different arcs and the oracle
            // MUST flag drift. This is exactly the BLOCKER the production probe's phase-matched epoch fixes;
            // it proves the row is a real shift, not a tautological circle-vs-itself.
            double[] intendedRawEpoch = CircleAtUTs(LkoRadius, omega, epoch, renderedUTs);
            var rBug = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, intendedRawEpoch, rendered);
            Assert.True(rBug.HasMeasurement);
            Assert.True(rBug.OverTolerance);
        }

        [Fact]
        public void Synthesized_ReaimedParkingOrbit_StaleEffUtSkew_ZeroDrift()
        {
            // CLASS 2 FP row (the live synth 50km outlier: maxDev=50022 tol=904 scale=903559 refCount=9
            // rendCount=10 with IDENTICAL intended/rendered orbit elements - sma=379874 ecc=0.0043 body=Duna).
            // Identical elements + epochs CANNOT truly drift 50km; the skew was the oracle sampling the two
            // sides at different clocks. The synth lens runs ONLY under the epoch-bake path, which propagates
            // the rendered conic at the LIVE clock (currentUT), so the FIX has the production probe pass
            // currentUT (not the reseed-stale icon-drive clock offEffUT) as the rendered sample clock - both
            // sides then sample at currentUT (the effUT parameter is retained so the in-game raw-epoch synth
            // fixture can still pass its own icon clock). This models that production sampling: a Duna parking
            // circle, 9 samples across a quarter-period half-arc, both sides at currentUT, plus the live icon
            // anchor point (the rendCount=10 tenth point).
            const double radius = 380_000.0;           // ~Duna parking, matching the live iconR=380617
            const double period = 2680.0;              // a fast parking-orbit period
            const double omega = 2.0 * Math.PI / period;
            const double epoch = 50_000.0;
            const double shift = 0.0;                   // the live emit's intendedSeg/rendOrbit were IDENTICAL
            const double currentUT = epoch + 6.0 * period;
            const int count = 9;                        // matches the live refCount=9
            const double halfSpan = period * 0.25;
            const double effSkew = 56.0;                // ~2 frames stale at warp (SeedFreshnessFrames)

            double[] referenceUTs = RenderGeometrySampler.BuildSampleUTs(currentUT, halfSpan, count);
            double[] reference = CircleAtUTs(radius, omega, epoch + shift, referenceUTs);

            // POST-FIX rendered: sampled at the SAME currentUT clock as the reference (the fix), then anchored
            // with one live icon point at currentUT - effSkew (the icon still rides the drive clock; it can only
            // ADD a vertex, never inflate the ref->rendered nearest distance). The faithful draw reads ~0.
            double[] renderedUTs = RenderGeometrySampler.BuildSampleUTs(currentUT, halfSpan, count);
            double[] renderedCurve = CircleAtUTs(radius, omega, epoch + shift, renderedUTs);
            double[] iconPoint = CircleAtUTs(
                radius, omega, epoch + shift, new[] { currentUT - effSkew });
            double[] rendered = AppendPoint(renderedCurve, iconPoint);

            double scale = RenderParityOracle.EstimateScaleFromPoints(reference);
            double tol = RenderParityOracle.ToleranceForScale(scale);
            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered, tol);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);

            // LOAD-BEARING negative control (proves the FP was REAL, not a tautology): re-run the PRE-FIX
            // sampling - the rendered CURVE at the stale drive clock (currentUT - effSkew) while the reference
            // stays at currentUT. The two arcs cover non-overlapping spans and the trailing endpoint reads a
            // ~50km chord on the fast parking orbit (the live maxDev=50022), so the oracle MUST flag drift.
            // This locks in that the stale-clock SKEW, not a real element error, was the FP source.
            double[] preFixRenderedUTs = RenderGeometrySampler.BuildSampleUTs(
                currentUT - effSkew, halfSpan, count);
            double[] preFixRendered = CircleAtUTs(radius, omega, epoch + shift, preFixRenderedUTs);
            var rPreFix = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Synthesized, reference, preFixRendered, tol);
            Assert.True(rPreFix.HasMeasurement);
            Assert.True(rPreFix.OverTolerance);
            // The pre-fix skew lands in the tens-of-km band (the live ~50km), not a sub-km nibble: confirm the
            // negative control is exercising a drift of comparable scale to the live FP, not a knife-edge.
            Assert.True(rPreFix.MaxDeviationMeters > 10_000.0);
        }

        [Fact]
        public void Synthesized_ReaimedParkingOrbit_WrongElements_FlagsDrift()
        {
            // CLASS 2 non-blinding guard: the fix samples both sides at currentUT, which removes the oracle's
            // dependence on the drive clock. This negative control proves the fix does NOT also hide a REAL
            // element regression: same currentUT clock on both sides (post-fix), but the rendered parking orbit
            // carries the sma+5000 element bug (radius 385km vs the intended 380km). A genuine ~5km radial
            // element error must STILL flag OverTolerance (~5km >> the ~904m scale-derived tol).
            const double radius = 380_000.0;
            const double wrongRadius = 385_000.0;       // the sma+5000 element regression
            const double period = 2680.0;
            const double omega = 2.0 * Math.PI / period;
            const double epoch = 50_000.0;
            const double currentUT = epoch + 6.0 * period;
            const int count = 9;
            const double halfSpan = period * 0.25;

            double[] referenceUTs = RenderGeometrySampler.BuildSampleUTs(currentUT, halfSpan, count);
            double[] reference = CircleAtUTs(radius, omega, epoch, referenceUTs);
            // Both sides at currentUT (the fix), but the rendered orbit has the wrong radius (wrong sma).
            double[] renderedCurve = CircleAtUTs(wrongRadius, omega, epoch, referenceUTs);
            double[] iconPoint = CircleAtUTs(wrongRadius, omega, epoch, new[] { currentUT });
            double[] rendered = AppendPoint(renderedCurve, iconPoint);

            double scale = RenderParityOracle.EstimateScaleFromPoints(reference);
            double tol = RenderParityOracle.ToleranceForScale(scale);
            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered, tol);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);   // the 5km radial element error dwarfs the ~904m tolerance
        }

        // ===================================================================================
        //  SYNTHESIZED drifted rows -> drift flagged (rendered != the producer's intended arc)
        // ===================================================================================

        [Fact]
        public void Synthesized_ReaimedArc_DrawnRotated_FlagsDrift()
        {
            // Row (drift): the re-aimed member's intended arc drawn with a draw-fidelity bug - the
            // rendered transfer is rotated 15 deg off the intended arc (a draw regression, NOT a solve
            // error). Synthesized mode catches it.
            double[] intended = EllipseXZ(HelioRadius, 0.25, SampleCount);
            double[] rendered = RotateAboutY(intended, 15.0);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, intended, rendered);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
        }

        [Fact]
        public void Synthesized_DescentReStitch_SeamDiscontinuity_FlagsDrift()
        {
            // Row (drift): the descent re-stitch draws a deorbit arc that diverges from the stitcher's
            // intended G1 arc (the rendered arc lands 8 km off the intended ground track) - the draw-
            // fidelity failure the Phase 6 synthesized gate exists to catch, distinct from the
            // rigid-seam-tangent-discontinuity anomaly the stitcher itself raises.
            double[] intendedDeorbit = VerticalProfile(70_000.0, 0.0, 40_000.0, SampleCount);
            double[] rendered = Translate(intendedDeorbit, 0.0, 0.0, 8_000.0);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, intendedDeorbit, rendered);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
        }

        // ===================================================================================
        //  Cross-cutting: tolerance is per-scenario (scale-derived), and the no-measurement
        //  paths never raise a false drift (the documented-buggy rows are tracked as
        //  expected-to-change and excluded from the zero-drift baseline; see the traceability doc).
        // ===================================================================================

        [Fact]
        public void SameAbsoluteOffset_DriftOnLko_NotOnHeliocentric_PerScenarioTolerance()
        {
            // Row: the SAME absolute rendered offset is a drift on an LKO-scale arc but within tolerance
            // on a heliocentric-scale arc - the per-scenario, scale-derived tolerance the migration plan
            // requires (a blanket metre tolerance would either mask the LKO drift or false-fire helio).
            const double offset = 5_000.0; // 5 km

            double[] lkoRef = CircleXZ(LkoRadius, SampleCount);
            double[] lkoRend = Translate(lkoRef, 0.0, 0.0, offset);
            double[] helioRef = CircleXZ(HelioRadius, SampleCount);
            double[] helioRend = Translate(helioRef, 0.0, 0.0, offset);

            var lko = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, lkoRef, lkoRend);
            var helio = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, helioRef, helioRend);

            Assert.True(lko.OverTolerance);     // 5 km > ~0.1% of the LKO arc extent (~1.4 km)
            Assert.False(helio.OverTolerance);  // 5 km < ~0.1% of the heliocentric extent (~2.6e7 m)
        }

        [Fact]
        public void NoMeasurement_NeverFalseDrift_RegressionSafety()
        {
            // Row: a degenerate scenario (an empty / not-yet-resolved rendered set, e.g. a ghost mid-
            // reseed) must report NO measurement and NO drift, never a false regression. This is the
            // safety property the whole regression set leans on: a hole reads as "could not measure",
            // not "passed" and not "broke".
            double[] recorded = CircleXZ(LkoRadius, SampleCount);

            var empty = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, Array.Empty<double>());
            Assert.False(empty.HasMeasurement);
            Assert.False(empty.OverTolerance);

            // All-non-finite rendered (a NaN-flung reseed) is also no-measurement, no false drift.
            double[] allNaN = new double[recorded.Length];
            for (int i = 0; i < allNaN.Length; i++)
                allNaN[i] = double.NaN;
            var nan = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, recorded, allNaN);
            Assert.False(nan.HasMeasurement);
            Assert.False(nan.OverTolerance);
        }
    }
}
