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
