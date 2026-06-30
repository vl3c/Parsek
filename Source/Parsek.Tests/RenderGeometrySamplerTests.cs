using System;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-0 unit coverage for the PURE part of the recorded-vs-rendered parity SAMPLER
    /// (<see cref="RenderGeometrySampler"/>): the body-relative reframing, the Vector3d -&gt; flat
    /// double[] flattening the oracle consumes, the even sample-UT spread across the orbit's visible
    /// span, and the loop-shift (renderedUT - shift) recorded-clock UT mapping. These are the only
    /// genuinely-new pure primitives between a raw Unity sample and
    /// <see cref="RenderParityOracle.ComputeDrift"/>; the Unity reads (Orbit.getPositionAtUT,
    /// GetWorldPos3D) stay in MapRenderProbe and are guarded by the separate in-game capture-harness
    /// fixture. No Unity ECalls here (Vector3d is a plain primitive struct), no shared static state.
    /// </summary>
    public class RenderGeometrySamplerTests
    {
        private const double Eps = 1e-9;

        // ---- ToBodyRelative ----

        [Fact]
        public void ToBodyRelative_SubtractsBodyPosition()
        {
            var world = new Vector3d(1000.0, 2000.0, 3000.0);
            var body = new Vector3d(100.0, 200.0, 300.0);

            Vector3d rel = RenderGeometrySampler.ToBodyRelative(world, body);

            Assert.Equal(900.0, rel.x, 6);
            Assert.Equal(1800.0, rel.y, 6);
            Assert.Equal(2700.0, rel.z, 6);
        }

        [Fact]
        public void ToBodyRelative_NonFiniteWorld_ReturnsNaN()
        {
            var world = new Vector3d(double.NaN, 0.0, 0.0);
            var body = new Vector3d(1.0, 2.0, 3.0);

            Vector3d rel = RenderGeometrySampler.ToBodyRelative(world, body);

            Assert.True(double.IsNaN(rel.x));
            Assert.True(double.IsNaN(rel.y));
            Assert.True(double.IsNaN(rel.z));
        }

        [Fact]
        public void ToBodyRelative_NonFiniteBody_ReturnsNaN()
        {
            var world = new Vector3d(1.0, 2.0, 3.0);
            var body = new Vector3d(0.0, double.PositiveInfinity, 0.0);

            Vector3d rel = RenderGeometrySampler.ToBodyRelative(world, body);

            Assert.True(double.IsNaN(rel.x));
            Assert.True(double.IsNaN(rel.y));
            Assert.True(double.IsNaN(rel.z));
        }

        // ---- RenderedScaledVertexToBodyRelative (the polyline parity-capture inverse) ----
        //
        // FP CLASS 3 ROOT CAUSE (live: 37 mode=polyline parity-drift emits on visually-correct Duna legs,
        // maxDev ~244-358 m floor + intermittent multi-km / ~40 km spikes, the SAME maxDev recurring across
        // legs of wildly different scale/body/point-count = a constant per-leg additive residual, NOT a
        // per-leg geometric mis-draw). The DRAW builds each polyline vertex strobe-free in the registered
        // scaled-body frame:
        //     scaledVertex = bodyCentreScaled + (world - bodyPos) * invScale
        // deliberately avoiding ScaledSpace.totalOffset (the scaled-origin recenter that oscillates every
        // render frame). The OLD capture inverse, ScaledSpace.ScaledToLocalSpace(scaledVertex) - bodyPos =
        // (scaledVertex + totalOffset)*scaleFactor - bodyPos, round-trips through that totalOffset and leaves
        // a CONSTANT residual E = ScaledToLocalSpace(bodyCentreScaled) - bodyPos on every reconstructed
        // point: a steady float-quantized floor (~250-360 m at scaleFactor 6000) with out-of-phase spikes.
        // The new helper inverts in the draw's OWN frame, (scaledVertex - bodyCentreScaled)*scaleFactor,
        // cancelling both bodyCentreScaled and totalOffset so a faithful leg reconstructs to ~0.
        //
        // These tests model the FULL draw -> capture -> oracle round-trip headlessly (the live path's Unity
        // reads are the only thing not exercised here) and prove BOTH halves of the Phase-9 contract: (a) the
        // FP no longer fires, AND (b) a real mis-draw of comparable scale STILL fires OverTolerance.

        private const double ScaleFactor = 6000.0; // stock ScaledSpace.scaleFactor
        private const double InvScale = 1.0 / ScaleFactor;

        // Build the scaled-space vertex the renderer's strobe-free draw path produces for a body-relative
        // world offset `bodyRelWorld`, against scaled-body centre `bodyCentreScaled` (= scaledXform.position).
        private static Vector3d DrawScaledVertex(Vector3d bodyRelWorld, Vector3d bodyCentreScaled)
        {
            return bodyCentreScaled + bodyRelWorld * InvScale;
        }

        // The OLD (buggy) capture inverse: ScaledSpace.ScaledToLocalSpace(s) - bodyPos, with
        // ScaledToLocalSpace(s) = (s + totalOffset) * scaleFactor. Modelled directly (the production
        // ScaledSpace is a Unity singleton) so the test reproduces the exact false-positive residual.
        private static Vector3d OldCaptureInverse(
            Vector3d scaledVertex, Vector3d totalOffset, Vector3d bodyPos)
        {
            return (scaledVertex + totalOffset) * ScaleFactor - bodyPos;
        }

        [Fact]
        public void RenderedScaledVertexToBodyRelative_RoundTripsDrawExactly()
        {
            // The helper is the exact inverse of the draw against the same centre: a body-relative offset
            // drawn to a scaled vertex reconstructs to itself, independent of where the scaled-body centre is.
            var bodyCentreScaled = new Vector3d(123456.789, -98765.4321, 55555.5);
            var bodyRel = new Vector3d(42000.0, -17000.0, 9000.0);

            Vector3d scaledVertex = DrawScaledVertex(bodyRel, bodyCentreScaled);
            Vector3d recovered = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                scaledVertex, bodyCentreScaled, ScaleFactor);

            Assert.Equal(bodyRel.x, recovered.x, 6);
            Assert.Equal(bodyRel.y, recovered.y, 6);
            Assert.Equal(bodyRel.z, recovered.z, 6);
        }

        [Theory]
        [InlineData(double.NaN, 0.0, 0.0)]
        [InlineData(0.0, double.PositiveInfinity, 0.0)]
        public void RenderedScaledVertexToBodyRelative_NonFiniteVertex_ReturnsNaN(
            double vx, double vy, double vz)
        {
            Vector3d r = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                new Vector3d(vx, vy, vz), new Vector3d(1.0, 2.0, 3.0), ScaleFactor);
            Assert.True(double.IsNaN(r.x) && double.IsNaN(r.y) && double.IsNaN(r.z));
        }

        [Fact]
        public void RenderedScaledVertexToBodyRelative_NonFiniteCentreOrScale_ReturnsNaN()
        {
            var v = new Vector3d(1.0, 2.0, 3.0);
            Vector3d badCentre = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                v, new Vector3d(double.NaN, 0.0, 0.0), ScaleFactor);
            Assert.True(double.IsNaN(badCentre.x));

            Vector3d badScale = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                v, new Vector3d(1.0, 2.0, 3.0), double.PositiveInfinity);
            Assert.True(double.IsNaN(badScale.x));
        }

        // A representative Duna-scale body-fixed descent leg's recorded body-relative track (the parity
        // REFERENCE), as a short arc of metre-scale offsets a few hundred km from the body centre. Mirrors
        // the live leg=0/1 scale~223 km / 357 km Duna descent legs.
        private static Vector3d[] DunaDescentLegBodyRel(int count)
        {
            var pts = new Vector3d[count];
            // ~200 km radius surface point, sweeping a small arc + descending in altitude.
            const double r = 320_000.0;
            for (int i = 0; i < count; i++)
            {
                double t = count == 1 ? 0.0 : (double)i / (count - 1);
                double theta = 0.35 * t; // ~20 deg arc
                double alt = 50_000.0 * (1.0 - t); // 50 km -> 0
                double rr = r + alt;
                pts[i] = new Vector3d(rr * Math.Cos(theta), 14_000.0 * t, rr * Math.Sin(theta));
            }
            return pts;
        }

        private static double[] FlattenV(Vector3d[] pts)
        {
            var flat = new double[pts.Length * 3];
            for (int i = 0; i < pts.Length; i++)
            {
                flat[i * 3] = pts[i].x;
                flat[i * 3 + 1] = pts[i].y;
                flat[i * 3 + 2] = pts[i].z;
            }
            return flat;
        }

        [Fact]
        public void PolylineRoundTrip_FaithfulLeg_OldInverseFalseFires_NewInverseClean()
        {
            // FULL draw -> capture -> oracle round-trip for a FAITHFUL (correctly-drawn) Duna descent leg.
            // The draw produces points3 = DrawScaledVertex(bodyRel) exactly (rendered == recorded by
            // construction), so a correct capture must read ~0 drift.
            Vector3d[] recordedRel = DunaDescentLegBodyRel(74);
            // A non-trivial scaled-body centre + a non-zero scaled-origin totalOffset (the live strobe value).
            var bodyCentreScaled = new Vector3d(220_000.0, -310_000.0, 95_000.0);
            var totalOffset = new Vector3d(140.0, -90.0, 60.0); // ~hundreds of scaled units (the recenter)

            // DRAW each recorded point to a scaled vertex (this is the strobe-free production draw).
            var scaledVerts = new Vector3d[recordedRel.Length];
            for (int i = 0; i < recordedRel.Length; i++)
                scaledVerts[i] = DrawScaledVertex(recordedRel[i], bodyCentreScaled);

            double[] recordedFlat = FlattenV(recordedRel);

            // --- OLD capture inverse: re-introduces totalOffset -> constant residual -> FALSE parity-drift.
            var oldRenderedRel = new Vector3d[scaledVerts.Length];
            for (int i = 0; i < scaledVerts.Length; i++)
                oldRenderedRel[i] = OldCaptureInverse(scaledVerts[i], totalOffset, Vector3d.zero);
            var oldResult = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, recordedFlat, FlattenV(oldRenderedRel));
            Assert.True(oldResult.HasMeasurement);
            Assert.True(oldResult.OverTolerance); // reproduces the live false positive

            // The residual is a CONSTANT additive offset == ScaledToLocalSpace(bodyCentreScaled) - bodyPos
            // (here bodyPos = 0), i.e. (bodyCentreScaled + totalOffset)*scaleFactor - 0, applied to every
            // point - the scale/point-count-independent fingerprint from the live histogram.
            Vector3d expectedResidual = (bodyCentreScaled + totalOffset) * ScaleFactor;
            double residualMag = Math.Sqrt(
                expectedResidual.x * expectedResidual.x
                + expectedResidual.y * expectedResidual.y
                + expectedResidual.z * expectedResidual.z);
            Assert.True(oldResult.MaxDeviationMeters > residualMag * 0.99);

            // --- NEW capture inverse: invert in the draw's own centre -> cancels both -> ~0 drift (no FP).
            var newRenderedRel = new Vector3d[scaledVerts.Length];
            for (int i = 0; i < scaledVerts.Length; i++)
                newRenderedRel[i] = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                    scaledVerts[i], bodyCentreScaled, ScaleFactor);
            var newResult = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, recordedFlat, FlattenV(newRenderedRel));
            Assert.True(newResult.HasMeasurement);
            Assert.False(newResult.OverTolerance); // the false positive is gone
            Assert.True(newResult.MaxDeviationMeters <= 1.0); // reconstructs to the recorded track
        }

        [Fact]
        public void PolylineRoundTrip_RealMisDraw_NewInverseStillFires()
        {
            // NON-BLINDING half (the Phase-9 contract): a GENUINE mis-draw of comparable scale to the FP
            // (the live FP floor was ~250-360 m; this lateral mis-draw is ~2 km, of the same order, and well
            // above the ~0.1% scale-derived tolerance for this ~320 km arc) must STILL fire OverTolerance
            // through the new inverse - the fix kills the artifact without hiding a real deviation.
            Vector3d[] recordedRel = DunaDescentLegBodyRel(74);
            var bodyCentreScaled = new Vector3d(220_000.0, -310_000.0, 95_000.0);

            // The leg is drawn 2 km off its recorded track in +Y (a mis-stitched / off-track draw). The
            // DRAW still uses the same centre, but the underlying body-relative geometry is wrong.
            var misDrawnRel = new Vector3d[recordedRel.Length];
            for (int i = 0; i < recordedRel.Length; i++)
                misDrawnRel[i] = recordedRel[i] + new Vector3d(0.0, 2_000.0, 0.0);

            var scaledVerts = new Vector3d[misDrawnRel.Length];
            for (int i = 0; i < misDrawnRel.Length; i++)
                scaledVerts[i] = DrawScaledVertex(misDrawnRel[i], bodyCentreScaled);

            // Capture with the NEW inverse: reconstructs the (wrong) body-relative track faithfully, so the
            // 2 km real deviation survives and the oracle fires.
            var newRenderedRel = new Vector3d[scaledVerts.Length];
            for (int i = 0; i < scaledVerts.Length; i++)
                newRenderedRel[i] = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                    scaledVerts[i], bodyCentreScaled, ScaleFactor);

            var result = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, FlattenV(recordedRel), FlattenV(newRenderedRel));
            Assert.True(result.HasMeasurement);
            Assert.True(result.OverTolerance); // a real mis-draw is NOT hidden by the fix
            Assert.True(result.MaxDeviationMeters > 1_900.0);
        }

        [Fact]
        public void PolylineRoundTrip_StrobeSpikeFrame_OldInverseSpikes_NewInverseStable()
        {
            // The live ~40 km maxDev spikes are the SAME FP at a frame where the scaled transform and
            // totalOffset are read out of phase (a larger totalOffset swing). The new inverse is immune to
            // totalOffset entirely, so the spike vanishes while a real drift would still register. Model a
            // big strobe swing and confirm the old inverse spikes far past any tolerance while the new one
            // stays ~0.
            Vector3d[] recordedRel = DunaDescentLegBodyRel(74);
            var bodyCentreScaled = new Vector3d(220_000.0, -310_000.0, 95_000.0);
            var bigStrobe = new Vector3d(750.0, -400.0, 300.0); // full-swing totalOffset (the draw comment)

            var scaledVerts = new Vector3d[recordedRel.Length];
            for (int i = 0; i < recordedRel.Length; i++)
                scaledVerts[i] = DrawScaledVertex(recordedRel[i], bodyCentreScaled);

            double[] recordedFlat = FlattenV(recordedRel);

            var oldRel = new Vector3d[scaledVerts.Length];
            for (int i = 0; i < scaledVerts.Length; i++)
                oldRel[i] = OldCaptureInverse(scaledVerts[i], bigStrobe, Vector3d.zero);
            var oldResult = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, recordedFlat, FlattenV(oldRel));
            Assert.True(oldResult.OverTolerance);
            Assert.True(oldResult.MaxDeviationMeters > 1_000_000.0); // tens of MM (the strobe at full swing)

            var newRel = new Vector3d[scaledVerts.Length];
            for (int i = 0; i < scaledVerts.Length; i++)
                newRel[i] = RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                    scaledVerts[i], bodyCentreScaled, ScaleFactor);
            var newResult = RenderParityOracle.ComputeDriftScaleDerived(
                RenderParityOracle.ParityMode.Synthesized, recordedFlat, FlattenV(newRel));
            Assert.False(newResult.OverTolerance);
            Assert.True(newResult.MaxDeviationMeters <= 1.0);
        }

        // ---- Flatten ----

        [Fact]
        public void Flatten_ProducesXYZTriples()
        {
            var pts = new[]
            {
                new Vector3d(1.0, 2.0, 3.0),
                new Vector3d(4.0, 5.0, 6.0),
            };

            double[] flat = RenderGeometrySampler.Flatten(pts, pts.Length);

            Assert.Equal(6, flat.Length);
            Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 }, flat);
        }

        [Fact]
        public void Flatten_RespectsCountCap_ReusesOversizedBuffer()
        {
            // A 4-element scratch buffer but only the first 2 are live.
            var pts = new[]
            {
                new Vector3d(1.0, 1.0, 1.0),
                new Vector3d(2.0, 2.0, 2.0),
                new Vector3d(9.0, 9.0, 9.0),
                new Vector3d(9.0, 9.0, 9.0),
            };

            double[] flat = RenderGeometrySampler.Flatten(pts, 2);

            Assert.Equal(6, flat.Length);
            Assert.Equal(new[] { 1.0, 1.0, 1.0, 2.0, 2.0, 2.0 }, flat);
        }

        [Fact]
        public void Flatten_NullOrZeroCount_ReturnsEmpty()
        {
            Assert.Empty(RenderGeometrySampler.Flatten(null, 5));
            Assert.Empty(RenderGeometrySampler.Flatten(new Vector3d[0], 5));
            Assert.Empty(RenderGeometrySampler.Flatten(new[] { new Vector3d(1, 1, 1) }, 0));
        }

        [Fact]
        public void Flatten_PreservesNonFinite_NotZeroed()
        {
            var pts = new[] { new Vector3d(double.NaN, double.PositiveInfinity, 5.0) };

            double[] flat = RenderGeometrySampler.Flatten(pts, 1);

            Assert.True(double.IsNaN(flat[0]));
            Assert.True(double.IsInfinity(flat[1]));
            Assert.Equal(5.0, flat[2], 6);
        }

        [Fact]
        public void FlattenSingle_OnePoint()
        {
            double[] flat = RenderGeometrySampler.FlattenSingle(new Vector3d(7.0, 8.0, 9.0));
            Assert.Equal(new[] { 7.0, 8.0, 9.0 }, flat);
        }

        // ---- BuildSampleUTs ----

        [Fact]
        public void BuildSampleUTs_EvenSpread_EndpointsAndCentre()
        {
            double[] uts = RenderGeometrySampler.BuildSampleUTs(1000.0, 100.0, 5);

            Assert.Equal(5, uts.Length);
            Assert.Equal(900.0, uts[0], 6);   // centre - half
            Assert.Equal(950.0, uts[1], 6);
            Assert.Equal(1000.0, uts[2], 6);  // centre
            Assert.Equal(1050.0, uts[3], 6);
            Assert.Equal(1100.0, uts[4], 6);  // centre + half
        }

        [Fact]
        public void BuildSampleUTs_CountOne_IsCentreOnly()
        {
            double[] uts = RenderGeometrySampler.BuildSampleUTs(1234.0, 500.0, 1);
            Assert.Single(uts);
            Assert.Equal(1234.0, uts[0], 6);
        }

        [Fact]
        public void BuildSampleUTs_NonPositiveCountOrNonFinite_ReturnsEmpty()
        {
            Assert.Empty(RenderGeometrySampler.BuildSampleUTs(1000.0, 100.0, 0));
            Assert.Empty(RenderGeometrySampler.BuildSampleUTs(double.NaN, 100.0, 5));
            Assert.Empty(RenderGeometrySampler.BuildSampleUTs(1000.0, double.PositiveInfinity, 5));
        }

        [Fact]
        public void BuildSampleUTs_NegativeHalfSpan_TreatedAsMagnitude()
        {
            double[] a = RenderGeometrySampler.BuildSampleUTs(0.0, -50.0, 3);
            double[] b = RenderGeometrySampler.BuildSampleUTs(0.0, 50.0, 3);
            Assert.Equal(b, a);
        }

        // ---- ShiftSampleUTs ----

        [Fact]
        public void ShiftSampleUTs_SubtractsShift()
        {
            var rendered = new[] { 1000.0, 1100.0, 1200.0 };

            double[] recorded = RenderGeometrySampler.ShiftSampleUTs(rendered, 240000.0);

            Assert.Equal(new[] { 1000.0 - 240000.0, 1100.0 - 240000.0, 1200.0 - 240000.0 }, recorded);
        }

        [Fact]
        public void ShiftSampleUTs_ZeroShift_Unchanged()
        {
            var rendered = new[] { 1.0, 2.0, 3.0 };
            double[] recorded = RenderGeometrySampler.ShiftSampleUTs(rendered, 0.0);
            Assert.Equal(rendered, recorded);
        }

        [Fact]
        public void ShiftSampleUTs_NonFiniteShift_ReturnsCopyUnchanged()
        {
            var rendered = new[] { 1.0, 2.0 };
            double[] recorded = RenderGeometrySampler.ShiftSampleUTs(rendered, double.NaN);
            Assert.Equal(rendered, recorded);
            Assert.NotSame(rendered, recorded); // a NEW array, never the recorded source aliased
        }

        [Fact]
        public void ShiftSampleUTs_Null_ReturnsEmpty()
        {
            Assert.Empty(RenderGeometrySampler.ShiftSampleUTs(null, 10.0));
        }

        // ---- End-to-end with the oracle: a loop-shifted faithful pair reads ~0 drift ----

        [Fact]
        public void FaithfulLoopShiftedPair_FlattensToZeroDrift()
        {
            // Two identical "orbits" sampled at matching clocks (rendered live UTs, recorded = live - shift)
            // produce the SAME body-relative geometry, so the oracle measures ~0 drift. This is the loop-
            // shift invariant the probe relies on: a faithful loop ghost is NOT flagged.
            const double shift = 240000.0;
            double[] renderedUTs = RenderGeometrySampler.BuildSampleUTs(1000.0, 100.0, 5);
            double[] recordedUTs = RenderGeometrySampler.ShiftSampleUTs(renderedUTs, shift);

            // Model "the same orbit" as a deterministic function of the orbit PHASE (clock minus its own
            // epoch). Rendered phase uses the live epoch; recorded phase uses the shifted epoch; both land on
            // the identical phase set, hence identical positions.
            var renderedPts = new Vector3d[renderedUTs.Length];
            var recordedPts = new Vector3d[recordedUTs.Length];
            for (int i = 0; i < renderedUTs.Length; i++)
            {
                renderedPts[i] = PhasePoint(renderedUTs[i] - 1000.0);
                recordedPts[i] = PhasePoint(recordedUTs[i] - (1000.0 - shift));
            }

            double[] referenceFlat = RenderGeometrySampler.Flatten(recordedPts, recordedPts.Length);
            double[] renderedFlat = RenderGeometrySampler.Flatten(renderedPts, renderedPts.Length);

            var r = RenderParityOracle.ComputeDrift(
                RenderParityOracle.ParityMode.Faithful, referenceFlat, renderedFlat, toleranceMeters: 1.0);

            Assert.True(r.HasMeasurement);
            Assert.False(r.OverTolerance);
            Assert.True(r.MaxDeviationMeters <= 1e-6);
        }

        // A simple deterministic position-from-phase model (a circle of radius 700 km in the XY plane), so
        // two clocks at the same phase land on the same point.
        private static Vector3d PhasePoint(double phaseSeconds)
        {
            const double radius = 700000.0;
            const double period = 3600.0;
            double theta = (phaseSeconds / period) * 2.0 * System.Math.PI;
            return new Vector3d(radius * System.Math.Cos(theta), radius * System.Math.Sin(theta), 0.0);
        }
    }
}
