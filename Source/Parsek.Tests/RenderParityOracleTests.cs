using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-0 unit coverage for the recorded-vs-rendered parity oracle's PURE geometry-diff math
    /// (<see cref="RenderParityOracle"/>), the new DISTINCT axis added beside
    /// <see cref="GhostRenderReconciler"/>. Covers both modes (faithful = rendered vs recorded,
    /// synthesized = rendered vs intended arc), within-/over-tolerance drift, mismatched + empty
    /// counts, NaN/Inf inputs (no false positive, mirroring the reconciler), the scale-from-geometry
    /// tolerance helper, and that the now-LIVE `parity-drift` token can flow through the gated
    /// <see cref="MapRenderTrace.EmitAnomaly"/> sink (the token is fired in game by
    /// <c>MapRenderProbe.TrySampleAndEmitFaithfulOrbitParity</c>).
    ///
    /// <para>The diff math is pure (no Unity, no shared static state). The single anomaly-emit test
    /// touches the gated <see cref="MapRenderTrace"/> + <see cref="ParsekLog"/> static state, so the
    /// whole class joins the Sequential collection for safety.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RenderParityOracleTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RenderParityOracleTests()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.FrameCounterOverrideForTesting = () => 7;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.FrameCounterOverrideForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- Helpers ----

        // A simple ascending straight "arc" of n points along +X at a fixed step, optionally offset
        // along +Y so the rendered set is uniformly displaced from the reference by a known distance.
        private static double[] Line(int n, double step, double yOffset = 0.0)
        {
            var a = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                a[i * 3] = i * step;     // x
                a[i * 3 + 1] = yOffset;  // y
                a[i * 3 + 2] = 0.0;      // z
            }
            return a;
        }

        // n points clustered within a tiny extent around the origin (a landed / near-stationary
        // surface-dwell leg whose GetWorldSurfacePosition samples are all (near-)coincident). spreadMeters
        // is the TOTAL bounding-box extent along +X; spreadMeters == 0 gives an exactly-coincident point
        // cloud (bounding-box diagonal 0). The cloud sits at worldOffset along +X so the rendered polyline
        // can be placed a known distance AWAY from it.
        private static double[] PointCluster(int n, double spreadMeters, double worldOffset = 0.0)
        {
            var a = new double[n * 3];
            double step = n > 1 ? spreadMeters / (n - 1) : 0.0;
            for (int i = 0; i < n; i++)
            {
                a[i * 3] = worldOffset + i * step; // x: spans [worldOffset, worldOffset + spreadMeters]
                a[i * 3 + 1] = 0.0;                // y
                a[i * 3 + 2] = 0.0;                // z
            }
            return a;
        }

        // ---- ComputeDrift: within tolerance (no drift) ----

        [Fact]
        public void ComputeDrift_IdenticalGeometry_NoDrift()
        {
            double[] recorded = Line(5, 1000.0);
            double[] rendered = Line(5, 1000.0);

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered, toleranceMeters: 10.0);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.Equal(0.0, r.MaxDeviationMeters, 6);
            Assert.False(r.CountMismatch);
            Assert.Equal(RenderParityOracle.ParityMode.Faithful, r.Mode);
        }

        [Fact]
        public void ComputeDrift_SmallOffsetWithinTolerance_NoDrift()
        {
            // Rendered uniformly 5 m off the reference; tolerance 10 m -> no drift.
            double[] recorded = Line(5, 1000.0, yOffset: 0.0);
            double[] rendered = Line(5, 1000.0, yOffset: 5.0);

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered, toleranceMeters: 10.0);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.True(r.MaxDeviationMeters <= 5.0 + 1e-6);
        }

        // ---- ComputeDrift: point-to-POLYLINE (not point-to-vertex) - the "green but blind" guard ----

        [Fact]
        public void ComputeDrift_ReferencePointNearSegmentInterior_NotVertices_MeasuresSmall()
        {
            // THE point-to-polyline guard (catches a "green but blind" rewrite to point-to-VERTEX): the
            // rendered polyline has vertices ONLY at (0,0,0) and (1000,0,0) - a single 1000 m segment with
            // no vertex in its interior. The reference point sits at (250,0,0), ON that segment but FAR from
            // either vertex (250 m from the nearest vertex). A correct point-to-segment (polyline) diff
            // projects onto the segment interior and measures ~0 m -> within a 10 m tolerance, OverTolerance
            // false. A point-to-VERTEX rewrite would measure 250 m (the nearest-vertex distance) and FALSE-
            // FIRE drift - so this test would go RED on that regression. The whole acceptance axis is "did
            // the rendered ARC match the reference ARC", and an arc is the polyline, not its sample vertices.
            double[] reference = new double[] { 250.0, 0.0, 0.0 }; // on the segment interior, 250 m from both vertices
            double[] rendered = new double[]
            {
                0.0, 0.0, 0.0,
                1000.0, 0.0, 0.0, // a single 1000 m segment; NO vertex near (250,0,0)
            };

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: 10.0);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);                       // ~0 m to the segment interior, not 250 m to a vertex
            Assert.Equal(0.0, r.MaxDeviationMeters, 6);          // projects exactly onto the segment
        }

        // ---- ComputeDrift: over tolerance (drift) ----

        [Fact]
        public void ComputeDrift_LargeOffsetOverTolerance_FlagsDrift()
        {
            // Rendered 500 m off; tolerance 10 m -> drift.
            double[] recorded = Line(5, 1000.0, yOffset: 0.0);
            double[] rendered = Line(5, 1000.0, yOffset: 500.0);

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, recorded, rendered, toleranceMeters: 10.0);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
            Assert.True(r.MaxDeviationMeters >= 500.0 - 1e-6);
        }

        // ---- One-sided deviation: an extra RENDERED vertex can never RAISE the drift (S8) ----

        [Fact]
        public void ComputeDrift_ExtraRenderedVertex_CannotRaiseDeviation_WhyIconAnchorsWereRemoved()
        {
            // THE S8 RATIONALE, pinned: the oracle's deviation is ONE-SIDED - the max over REFERENCE points
            // of the nearest distance to the RENDERED polyline. Appending an extra vertex to the RENDERED
            // set only ADDS nearest-distance candidates (more segments to project onto), so it can only
            // LOWER (or keep) each reference point's distance - it can NEVER raise MaxDeviationMeters. That
            // is exactly why the icon anchors were REMOVED from both parity lenses in S8: an appended icon
            // vertex could never catch an off-arc icon (it cannot raise the deviation), it could only MASK
            // real drift by sitting closer to the reference than the drifted curve. Icon-off-its-own-line
            // detection lives solely in the icon-off-orbit angle check.
            double[] reference = Line(5, 1000.0, yOffset: 0.0);
            double[] rendered = Line(5, 1000.0, yOffset: 300.0); // a deliberate 300 m offset (real drift)

            var without = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: 10.0);
            Assert.True(without.HasMeasurement);
            Assert.True(without.OverTolerance); // the offset is a real measured drift

            // Append ONE far-away extra vertex to the RENDERED flat array (the pre-S8 icon-anchor shape,
            // exaggerated) and recompute: the deviation must not rise.
            double[] renderedPlus = new double[rendered.Length + 3];
            System.Array.Copy(rendered, renderedPlus, rendered.Length);
            renderedPlus[rendered.Length] = 250_000.0;     // far off the arc
            renderedPlus[rendered.Length + 1] = -80_000.0;
            renderedPlus[rendered.Length + 2] = 40_000.0;

            var with = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, renderedPlus, toleranceMeters: 10.0);
            Assert.True(with.HasMeasurement);
            Assert.True(with.MaxDeviationMeters <= without.MaxDeviationMeters + 1e-9);
        }

        // ---- Faithful vs Synthesized: same math, different reference, carried on the result ----

        [Fact]
        public void ComputeDrift_SynthesizedMode_CarriesModeOnResult()
        {
            // In Synthesized mode the reference is the producer's INTENDED arc (here a re-aimed line).
            double[] intended = Line(4, 2000.0);
            double[] rendered = Line(4, 2000.0);

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Synthesized, intended, rendered, toleranceMeters: 50.0);

            Assert.Equal(RenderParityOracle.ParityMode.Synthesized, r.Mode);
            Assert.Equal("synthesized", RenderParityOracle.ParityModeToken(r.Mode));
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void ComputeDrift_SameGeometry_BothModesAgreeOnDeviation()
        {
            // The diff MATH is identical across modes; only the reference's MEANING differs. Same point
            // sets in both modes must produce the same deviation + over-tolerance decision.
            double[] a = Line(6, 1500.0, yOffset: 0.0);
            double[] b = Line(6, 1500.0, yOffset: 120.0);

            var faithful = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, a, b, toleranceMeters: 100.0);
            var synth = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Synthesized, a, b, toleranceMeters: 100.0);

            Assert.Equal(faithful.MaxDeviationMeters, synth.MaxDeviationMeters, 6);
            Assert.Equal(faithful.OverTolerance, synth.OverTolerance);
            Assert.True(faithful.OverTolerance); // 120 m off vs 100 m tol
        }

        // ---- Count-independence: different sampling density, same curve -> no drift ----

        [Fact]
        public void ComputeDrift_DifferentDensitySameCurve_NoDrift_ButCountMismatchRecorded()
        {
            // Reference sampled densely, rendered sparsely, both along the SAME straight curve. The
            // nearest-point projection makes this count-independent: no drift, but the count delta is
            // recorded for logging.
            double[] reference = Line(11, 100.0); // 0..1000 in 100 m steps
            double[] rendered = Line(3, 500.0);   // 0,500,1000 - same line, coarser

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: 1.0);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.True(r.CountMismatch);
            Assert.Equal(11, r.ReferenceCount);
            Assert.Equal(3, r.RenderedCount);
            Assert.True(r.MaxDeviationMeters <= RenderParityOracle.MinToleranceMeters);
        }

        // ---- Empty / null inputs: no measurement, no false anomaly ----

        [Fact]
        public void ComputeDrift_EmptyReference_NoMeasurement()
        {
            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, new double[0], Line(3, 100.0), 10.0);

            Assert.False(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.Equal(0, r.ReferenceCount);
            Assert.Equal(3, r.RenderedCount);
        }

        [Fact]
        public void ComputeDrift_EmptyRendered_NoMeasurement()
        {
            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, Line(3, 100.0), new double[0], 10.0);

            Assert.False(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void ComputeDrift_NullInputs_NoMeasurement_NoThrow()
        {
            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, null, null, 10.0);

            Assert.False(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.Equal(0, r.ReferenceCount);
            Assert.Equal(0, r.RenderedCount);
        }

        [Fact]
        public void ComputeDrift_SynthesizedMode_EmptyRendered_NoMeasurement_CarriesMode()
        {
            // The no-measurement path must behave identically in BOTH modes (it is mode-agnostic), and the
            // mode must still be carried on the no-measurement result so a log line records which reference
            // could not be diffed. Covers the both-modes x no-measurement cell that was Faithful-only.
            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Synthesized, Line(3, 100.0), new double[0], 10.0);

            Assert.Equal(RenderParityOracle.ParityMode.Synthesized, r.Mode);
            Assert.False(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.Equal(3, r.ReferenceCount);
            Assert.Equal(0, r.RenderedCount);
        }

        // ---- NaN / Inf inputs: no false positive (mirror GhostRenderReconciler / RewindReadbackGuard) ----

        [Fact]
        public void ComputeDrift_AllRenderedNonFinite_NoMeasurement_NoFalseAnomaly()
        {
            double[] reference = Line(4, 1000.0);
            double[] rendered = new double[]
            {
                double.NaN, 0.0, 0.0,
                double.PositiveInfinity, 0.0, 0.0,
                0.0, double.NegativeInfinity, 0.0,
            };

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: 1.0);

            Assert.False(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void ComputeDrift_AllReferenceNonFinite_NoMeasurement_NoFalseAnomaly()
        {
            double[] reference = new double[]
            {
                double.NaN, double.NaN, double.NaN,
                double.PositiveInfinity, 0.0, 0.0,
            };
            double[] rendered = Line(4, 1000.0);

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: 1.0);

            Assert.False(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void ComputeDrift_PartialNonFinitePoints_SkippedNotFlagged()
        {
            // A scattered NaN point on each side must be SKIPPED, not read as a giant deviation. The
            // remaining finite points are identical -> no drift.
            double[] reference = new double[]
            {
                0.0, 0.0, 0.0,
                double.NaN, double.NaN, double.NaN, // skipped
                2000.0, 0.0, 0.0,
            };
            double[] rendered = new double[]
            {
                0.0, 0.0, 0.0,
                double.PositiveInfinity, 0.0, 0.0,  // skipped
                2000.0, 0.0, 0.0,
            };

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: 10.0);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.Equal(0.0, r.MaxDeviationMeters, 6);
        }

        [Fact]
        public void ComputeDrift_NonFiniteTolerance_FallsBackToFloor()
        {
            double[] reference = Line(3, 100.0, yOffset: 0.0);
            double[] rendered = Line(3, 100.0, yOffset: 0.5); // 0.5 m off, below the 1 m floor

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: double.NaN);

            Assert.Equal(RenderParityOracle.MinToleranceMeters, r.ToleranceMeters, 6);
            Assert.False(r.OverTolerance); // 0.5 m < 1 m floor
        }

        // ---- Tolerance-from-scale helper ----

        [Fact]
        public void ToleranceForScale_ScalesWithGeometry_NotABlanketConstant()
        {
            // A heliocentric-scale arc (1e10 m) gets a far larger tolerance than an LKO arc (7e5 m).
            double small = RenderParityOracle.ToleranceForScale(700_000.0);
            double large = RenderParityOracle.ToleranceForScale(1.0e10);

            Assert.True(large > small);
            Assert.Equal(700_000.0 * RenderParityOracle.DefaultScaleToleranceFraction, small, 3);
            Assert.Equal(1.0e10 * RenderParityOracle.DefaultScaleToleranceFraction, large, 0);
        }

        [Fact]
        public void ToleranceForScale_DegenerateScale_FallsBackToFloor()
        {
            Assert.Equal(RenderParityOracle.MinToleranceMeters, RenderParityOracle.ToleranceForScale(0.0), 6);
            Assert.Equal(RenderParityOracle.MinToleranceMeters, RenderParityOracle.ToleranceForScale(-5.0), 6);
            Assert.Equal(RenderParityOracle.MinToleranceMeters, RenderParityOracle.ToleranceForScale(double.NaN), 6);
            Assert.Equal(
                RenderParityOracle.MinToleranceMeters,
                RenderParityOracle.ToleranceForScale(double.PositiveInfinity), 6);
        }

        [Fact]
        public void ToleranceForScale_NonFiniteFraction_UsesDefault()
        {
            double withDefault = RenderParityOracle.ToleranceForScale(1.0e8);
            double withNaNFraction = RenderParityOracle.ToleranceForScale(1.0e8, fraction: double.NaN);
            double withNegFraction = RenderParityOracle.ToleranceForScale(1.0e8, fraction: -0.5);

            Assert.Equal(withDefault, withNaNFraction, 3);
            Assert.Equal(withDefault, withNegFraction, 3);
        }

        [Fact]
        public void EstimateScaleFromPoints_BoundingBoxDiagonal()
        {
            // A 3-4-0 box -> diagonal 5.
            double[] pts = new double[]
            {
                0.0, 0.0, 0.0,
                3.0, 4.0, 0.0,
            };
            Assert.Equal(5.0, RenderParityOracle.EstimateScaleFromPoints(pts), 6);
        }

        [Fact]
        public void EstimateScaleFromPoints_SkipsNonFinite_AndHandlesEmpty()
        {
            Assert.Equal(0.0, RenderParityOracle.EstimateScaleFromPoints(null), 6);
            Assert.Equal(0.0, RenderParityOracle.EstimateScaleFromPoints(new double[0]), 6);

            double[] withNan = new double[]
            {
                0.0, 0.0, 0.0,
                double.NaN, 1e9, 0.0, // skipped, must not inflate the box
                6.0, 8.0, 0.0,
            };
            Assert.Equal(10.0, RenderParityOracle.EstimateScaleFromPoints(withNan), 6); // 6-8-0 box -> 10
        }

        // ---- Scale-derived convenience overload ----

        [Fact]
        public void ComputeDriftScaleDerived_LkoDriftFlagged_AtSameAbsoluteOffsetHeliocentricNot()
        {
            // The same ABSOLUTE 2 km rendered offset is a drift on an LKO-scale reference but within
            // tolerance on a heliocentric-scale reference -> exactly the per-scenario behavior a blanket
            // metre tolerance cannot give.
            double[] lkoRef = Line(5, 350_000.0, yOffset: 0.0);     // extent ~1.4e6 m
            double[] lkoRend = Line(5, 350_000.0, yOffset: 2000.0); // 2 km off

            double[] helioRef = Line(5, 5.0e9, yOffset: 0.0);       // extent ~2e10 m
            double[] helioRend = Line(5, 5.0e9, yOffset: 2000.0);   // 2 km off

            var lko = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, lkoRef, lkoRend);
            var helio = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Faithful, helioRef, helioRend);

            Assert.True(lko.OverTolerance);    // 2 km > ~0.1% of 1.4e6 (~1.4 km)
            Assert.False(helio.OverTolerance); // 2 km < ~0.1% of 2e10 (~2e7 m)
        }

        // ---- Degenerate-reference FP guard (FP Class 1): scale~0 surface-dwell leg yields NO measurement,
        //      while a SMALL-but-real arc still fires. The paired (a)+(b) acceptance the cutover gate needs:
        //      the FP no longer fires AND a real drift of comparable scale STILL fires. ----

        [Fact]
        public void ComputeDriftScaleDerived_CoincidentReference_NoMeasurement_NotDrift()
        {
            // (a) FP case: a landed leg whose 18 recorded points are EXACTLY coincident (bounding box
            // collapses to a point, scale == 0). The rendered polyline is a real 357 m away (the live
            // tol=1m/scale=0m parity-drift the oracle false-fired on). With the degenerate-reference guard
            // this is NO MEASUREMENT (not a pass, not an anomaly): a point reference has no curve to diff.
            double[] coincidentRef = PointCluster(18, spreadMeters: 0.0, worldOffset: 0.0);
            double[] renderedFarLine = Line(18, 100.0, yOffset: 357.0); // a real line 357 m off the point

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, coincidentRef, renderedFarLine);

            Assert.False(r.HasMeasurement); // could-not-measure, NOT "passed"
            Assert.False(r.OverTolerance);  // never raises a false anomaly
            Assert.Equal(18, r.ReferenceCount);
        }

        [Fact]
        public void ComputeDriftScaleDerived_NearStationaryReference_BelowFloor_NoMeasurement()
        {
            // (a') The live 83-point Duna dwell that drifted by 16 m total extent (scale=16m, tol floored to
            // 1m, maxDev=357m). Still below MinMeasurableScaleMeters (100 m) -> no measurement. Confirms the
            // floor catches a NEAR-point (not just an exactly-coincident) reference, the other live FP leg.
            double[] nearPointRef = PointCluster(83, spreadMeters: 16.0, worldOffset: 0.0);
            double[] renderedFarLine = Line(83, 50.0, yOffset: 357.0);

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, nearPointRef, renderedFarLine);

            Assert.False(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void ComputeDriftScaleDerived_SmallButRealArc_WrongDraw_StillFires()
        {
            // (b) NON-NEGOTIABLE companion: a SMALL-but-real arc (extent ~2 km, well above the 100 m floor -
            // a short real descent leg, two orders below the live 223 km Duna descent legs) that the
            // renderer draws 500 m off STILL fires OverTolerance. The floor refuses to MEASURE a point; it
            // does NOT blind a real small leg. Proves the guard is a reference-extent gate, not a tolerance
            // widen / a blanket skip that would also miss a real mis-draw at this scale.
            double[] smallRealRef = Line(20, 100.0, yOffset: 0.0);   // extent ~1.9 km (> 100 m floor)
            double[] smallRealRend = Line(20, 100.0, yOffset: 500.0); // drawn 500 m off

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, smallRealRef, smallRealRend);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);                  // 500 m >> tol (~0.1% of ~1.9 km = ~1.9 m)
            Assert.True(r.MaxDeviationMeters >= 499.0);
        }

        [Fact]
        public void ComputeDriftScaleDerived_JustAboveFloor_FaithfulArc_NoDrift_StillMeasures()
        {
            // The floor must not turn a real-but-small FAITHFUL arc into a non-measurement: an arc just
            // above the floor that is drawn correctly still MEASURES (HasMeasurement) and reports no drift.
            // Pins that the floor only refuses POINT references, not small correctly-drawn arcs.
            double[] reference = Line(10, 20.0, yOffset: 0.0);  // extent ~180 m (> 100 m floor)
            double[] rendered = Line(10, 20.0, yOffset: 0.0);   // identical

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
        }

        [Fact]
        public void MinMeasurableScaleMeters_IsStableAndBelowSmallestRealLeg()
        {
            // Lock the floor value + its safety margin: it must be comfortably ABOVE the metre-scale jitter
            // of a "stationary" leg (~16 m live) and far BELOW the smallest leg that actually traces an arc
            // (the live Kerbin ascent leg at ~16.9 km). A later bump that crosses either bound would change
            // which legs the oracle measures and must be a conscious edit, not a silent drift.
            Assert.Equal(100.0, RenderParityOracle.MinMeasurableScaleMeters, 6);
            Assert.True(RenderParityOracle.MinMeasurableScaleMeters > 16.0);     // above live stationary jitter
            Assert.True(RenderParityOracle.MinMeasurableScaleMeters < 16_000.0); // below smallest real arc leg
        }

        // ---- ComputeDriftScaleDerived minToleranceMeters (the drawn-vertex float-noise metrology floor) ----

        [Fact]
        public void ComputeDriftScaleDerived_NoiseFloor_SubFloorDeviation_NoFire_ButFiresWithoutFloor()
        {
            // PAIRED FP-no-fire + load-bearing control in one scenario: a real ~19 km arc rendered a uniform
            // 500 m off. WITHOUT the floor the 0.1% scale tolerance (~19 m) fires - proving the deviation is
            // real relative to the geometry and the floor param is load-bearing, not a tautology. WITH a
            // 4.3 km floor (the drawn Vector3 vertices' float quantization when the scaled body centre sits
            // at a heliocentric ~3e6 scaled units - see DrawnVertexQuantizationFloorMeters) the SAME 500 m is
            // BELOW the instrument's own resolution: float noise, not geometry, so it must NOT fire. The
            // result still MEASURES (HasMeasurement, tolerance = the clamped floor) - honest metrology, not
            // a skip.
            double[] reference = Line(20, 1000.0, yOffset: 0.0);   // extent ~19 km
            double[] rendered = Line(20, 1000.0, yOffset: 500.0);  // drawn 500 m off

            var without = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered);
            Assert.True(without.HasMeasurement);
            Assert.True(without.OverTolerance); // the deviation is real; the param is load-bearing

            var with = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered,
                minToleranceMeters: 4300.0);
            Assert.True(with.HasMeasurement);
            Assert.False(with.OverTolerance);           // sub-floor deviation = float noise, no anomaly
            Assert.Equal(4300.0, with.ToleranceMeters, 6); // tolerance clamped UP to the floor
        }

        [Fact]
        public void ComputeDriftScaleDerived_NoiseFloor_RealMisDrawAboveFloor_StillFires()
        {
            // NON-BLINDING guard (the non-negotiable pairing): a genuine mis-draw LARGER than the noise
            // floor still fires with the floor applied. The floor bounds what the float vertices can
            // resolve; it never hides a mis-draw the instrument CAN see.
            double[] reference = Line(20, 1000.0, yOffset: 0.0);
            double[] rendered = Line(20, 1000.0, yOffset: 20_000.0); // 20 km off >> 4.3 km floor

            var r = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered,
                minToleranceMeters: 4300.0);

            Assert.True(r.HasMeasurement);
            Assert.True(r.OverTolerance);
            Assert.True(r.MaxDeviationMeters >= 19_999.0);
        }

        [Fact]
        public void ComputeDriftScaleDerived_NoiseFloor_NonFiniteOrZero_Ignored()
        {
            // A non-finite floor is treated as 0 (no floor); a zero floor is byte-identical to the
            // pre-param behaviour, so every existing caller is unchanged.
            double[] reference = Line(20, 1000.0, yOffset: 0.0);
            double[] rendered = Line(20, 1000.0, yOffset: 500.0);

            var nan = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered,
                minToleranceMeters: double.NaN);
            var none = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, reference, rendered);

            Assert.True(nan.OverTolerance);
            Assert.Equal(none.ToleranceMeters, nan.ToleranceMeters, 9);
        }

        // ---- Wired-but-inert: the parity-drift token flows through the gated EmitAnomaly sink ----

        [Fact]
        public void ParityDriftToken_EmitsThroughGatedAnomalySink()
        {
            // parity-drift is now WIRED + LIVE: the gated probe sampler
            // (MapRenderProbe.TrySampleAndEmitFaithfulOrbitParity) fires it in game when a faithful ghost's
            // rendered orbit diverges from its recorded reference. This unit test pins the emit SHAPE (the
            // oracle computes the drift, the caller emits the token through the gated sink) headlessly; the
            // end-to-end wiring is proven by the in-game RenderParityBaselineTest.
            double[] reference = Line(4, 1000.0, yOffset: 0.0);
            double[] rendered = Line(4, 1000.0, yOffset: 5000.0);
            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, reference, rendered, toleranceMeters: 10.0);
            Assert.True(r.OverTolerance);

            MapRenderTrace.EmitAnomaly(
                MapRenderTrace.RenderSurface.ProtoOrbitLine, "7", currentUT: 100.0, effUT: 100.0,
                reason: MapRenderTrace.AnomalyParityDrift,
                details: "mode=" + RenderParityOracle.ParityModeToken(r.Mode)
                    + " maxDev=" + MapRenderTrace.FormatDouble(r.MaxDeviationMeters, "F1")
                    + " tol=" + MapRenderTrace.FormatDouble(r.ToleranceMeters, "F1"));

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]") && l.Contains("phase=Anomaly")
                && l.Contains("reason=parity-drift") && l.Contains("mode=faithful"));
        }

        [Fact]
        public void NewAnomalyTokens_AreStableStrings()
        {
            // Lock the five new reason tokens so a later rename can't silently break a grep / LogContract.
            Assert.Equal("parity-drift", MapRenderTrace.AnomalyParityDrift);
            Assert.Equal("rigid-seam-tangent-discontinuity", MapRenderTrace.AnomalyRigidSeamTangentDiscontinuity);
            Assert.Equal("retire-not-held", MapRenderTrace.AnomalyRetireNotHeld);
            Assert.Equal("anchor-resolve-fail", MapRenderTrace.AnomalyAnchorResolveFail);
            Assert.Equal("clock-not-ready", MapRenderTrace.AnomalyClockNotReady);
        }
    }
}
