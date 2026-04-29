using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Unit tests for <see cref="TrajectoryMath.CatmullRomFit"/> (design doc
    /// §6.1 Stage 1 smoothing, §17.3.1 sidecar layout). Covers fit success,
    /// endpoint preservation, longitude wrap, and rejection of degenerate
    /// inputs. Pure math — no shared static state, no log capture.
    /// </summary>
    public class SmoothingSplineTests
    {
        private static TrajectoryPoint MakePoint(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        [Fact]
        public void Fit_LinearRamp_RoundTripsExactly()
        {
            // What makes it fail: a smoother that flattens real dynamics would
            // not reproduce a linear ramp at the recorded knot UTs — Catmull-Rom
            // must pass through every input sample by construction.
            var samples = new List<TrajectoryPoint>();
            for (int i = 0; i < 10; i++)
            {
                samples.Add(MakePoint(ut: i, lat: i * 0.1, lon: i * 0.2, alt: 1000.0 + i * 50.0));
            }

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, tension: 0.5, out string failure);
            Assert.Null(failure);
            Assert.True(spline.IsValid);

            // Tolerance reflects float32 storage of controls (lat/lon/alt are
            // stored as float in SmoothingSpline so the round-trip widens by
            // single-precision epsilon scaled by the magnitude). 1e-5 deg is
            // sub-millimetre on Kerbin's surface and well below visual
            // fidelity thresholds.
            for (int i = 0; i < samples.Count; i++)
            {
                Vector3d v = TrajectoryMath.CatmullRomFit.Evaluate(in spline, samples[i].ut);
                Assert.InRange(System.Math.Abs(v.x - samples[i].latitude), 0.0, 1e-5);
                Assert.InRange(System.Math.Abs(v.y - samples[i].longitude), 0.0, 1e-5);
                Assert.InRange(System.Math.Abs(v.z - samples[i].altitude), 0.0, 1e-3);
            }
        }

        [Fact]
        public void Fit_PreservesEndpointSamples()
        {
            // What makes it fail: anchor placement at section boundaries
            // depends on bit-exact endpoint preservation; if Evaluate at the
            // first or last knot drifts off the input, anchor fidelity erodes.
            var samples = new List<TrajectoryPoint>
            {
                MakePoint(0.0, 1.0, 10.0, 1000.0),
                MakePoint(1.0, 2.5, 11.0, 1100.0),
                MakePoint(2.0, 0.5, 12.5, 950.0),
                MakePoint(3.0, 3.0, 13.0, 1200.0),
                MakePoint(4.0, 1.5, 14.0, 1050.0),
                MakePoint(5.0, 2.0, 15.0, 1150.0),
                MakePoint(6.0, 0.0, 16.0, 990.0),
                MakePoint(7.0, 2.5, 17.0, 1110.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out _);
            Assert.True(spline.IsValid);

            Vector3d first = TrajectoryMath.CatmullRomFit.Evaluate(in spline, samples[0].ut);
            Vector3d last = TrajectoryMath.CatmullRomFit.Evaluate(in spline, samples[samples.Count - 1].ut);

            // Floats: lat/lon controls survive the double->float round-trip
            // to ~5 decimal places; altitude (in metres) stays sub-millimetre
            // for the test scale.
            Assert.Equal(samples[0].latitude, first.x, 5);
            Assert.Equal(samples[0].longitude, first.y, 5);
            Assert.Equal(samples[0].altitude, first.z, 3);
            Assert.Equal(samples[samples.Count - 1].latitude, last.x, 5);
            Assert.Equal(samples[samples.Count - 1].longitude, last.y, 5);
            Assert.Equal(samples[samples.Count - 1].altitude, last.z, 3);
        }

        [Fact]
        public void Fit_RejectsLessThanFourSamples()
        {
            // What makes it fail: Catmull-Rom needs four control points for
            // the standard tangent estimation; accepting fewer would silently
            // produce zero-derivative endpoints and bias the spline.
            var samples = new List<TrajectoryPoint>
            {
                MakePoint(0.0, 1.0, 10.0, 1000.0),
                MakePoint(1.0, 2.0, 11.0, 1100.0),
                MakePoint(2.0, 3.0, 12.0, 1200.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out string failure);
            Assert.False(spline.IsValid);
            Assert.NotNull(failure);
            Assert.Contains("4", failure);
        }

        [Fact]
        public void Fit_NaNInputProducesFailureReason()
        {
            // What makes it fail: a NaN sample leaking into the spline would
            // poison every downstream Evaluate and propagate to ghost
            // placement; rejection must be eager and explicit.
            var samples = new List<TrajectoryPoint>
            {
                MakePoint(0.0, 1.0, 10.0, 1000.0),
                MakePoint(1.0, double.NaN, 11.0, 1100.0),
                MakePoint(2.0, 3.0, 12.0, 1200.0),
                MakePoint(3.0, 4.0, 13.0, 1300.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out string failure);
            Assert.False(spline.IsValid);
            Assert.NotNull(failure);
            Assert.True(failure.Contains("NaN") || failure.Contains("non-finite"),
                $"failure reason should mention NaN or non-finite, got: {failure}");
        }

        [Fact]
        public void Fit_NonMonotonicUTRejected()
        {
            // What makes it fail: a non-monotonic UT means the segment table
            // is malformed; fitting through it would produce undefined
            // bracketing in Evaluate and silent visual artifacts.
            var samples = new List<TrajectoryPoint>
            {
                MakePoint(0.0, 1.0, 10.0, 1000.0),
                MakePoint(2.0, 2.0, 11.0, 1100.0),
                MakePoint(1.0, 3.0, 12.0, 1200.0), // out of order
                MakePoint(3.0, 4.0, 13.0, 1300.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out string failure);
            Assert.False(spline.IsValid);
            Assert.NotNull(failure);
            Assert.Contains("monotonic", failure);
        }

        [Fact]
        public void Fit_LongitudeWrapsCorrectly()
        {
            // What makes it fail: without antimeridian unwrap, fitting
            // through 178 -> -179 would treat the deltas as a -357 deg jump
            // and the spline would loop "the long way around" the planet —
            // visible as a vessel teleporting across the globe between
            // sample ticks.
            var samples = new List<TrajectoryPoint>
            {
                MakePoint(0.0, 1.0, 175.0, 1000.0),
                MakePoint(1.0, 1.0, 178.0, 1000.0),
                MakePoint(2.0, 1.0, -179.0, 1000.0),
                MakePoint(3.0, 1.0, -176.0, 1000.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out _);
            Assert.True(spline.IsValid);

            // Evaluate between samples 1 and 2 — UT ~1.5. The fitted longitude
            // should fall in a small neighborhood of the antimeridian,
            // i.e. either > 178 or < -178 (depending on how much the spline
            // has progressed across the seam). A non-wrapping fit would
            // produce a longitude near 0 (the average of 178 and -179 the
            // wrong way around), which is unambiguously wrong.
            Vector3d midpoint = TrajectoryMath.CatmullRomFit.Evaluate(in spline, 1.5);
            bool nearAntimeridian = (midpoint.y >= 178.0 && midpoint.y <= 180.0)
                                 || (midpoint.y <= -178.0 && midpoint.y >= -180.0);
            Assert.True(nearAntimeridian,
                $"Expected longitude near antimeridian, got {midpoint.y}");
        }

        [Fact]
        public void Evaluate_OutsideRangeClampsToEndpoint()
        {
            // What makes it fail: extrapolation past either endpoint would
            // let downstream renderers walk off the recorded segment, which
            // violates HR-7 (no smoothing across hard discontinuities) when
            // the segment ends at a structural event boundary.
            var samples = new List<TrajectoryPoint>
            {
                MakePoint(10.0, 1.0, 10.0, 1000.0),
                MakePoint(11.0, 2.0, 11.0, 1100.0),
                MakePoint(12.0, 3.0, 12.0, 1200.0),
                MakePoint(13.0, 4.0, 13.0, 1300.0),
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, 0.5, out _);
            Assert.True(spline.IsValid);

            Vector3d before = TrajectoryMath.CatmullRomFit.Evaluate(in spline, 5.0);
            Vector3d atFirst = TrajectoryMath.CatmullRomFit.Evaluate(in spline, samples[0].ut);
            Vector3d after = TrajectoryMath.CatmullRomFit.Evaluate(in spline, 100.0);
            Vector3d atLast = TrajectoryMath.CatmullRomFit.Evaluate(in spline, samples[samples.Count - 1].ut);

            // Clamp must be bit-exact between the two evaluations: passing
            // `ut < knots[0]` and `ut == knots[0]` both go through the
            // first-endpoint branch, so values must be identical even at
            // float precision.
            Assert.Equal(atFirst.x, before.x);
            Assert.Equal(atFirst.y, before.y);
            Assert.Equal(atFirst.z, before.z);
            Assert.Equal(atLast.x, after.x);
            Assert.Equal(atLast.y, after.y);
            Assert.Equal(atLast.z, after.z);
        }

        // --- Phase 8: Fit-with-flags ---

        [Fact]
        public void Fit_WithFlags_SkipsRejectedSamples()
        {
            // What makes it fail: rejected samples leak into knot/control
            // arrays and the spline interpolates through them, defeating
            // the entire Phase 8 outlier-rejection mechanism.
            var samples = new List<TrajectoryPoint>();
            for (int i = 0; i < 10; i++)
                samples.Add(MakePoint(ut: i, lat: i * 0.1, lon: i * 0.2, alt: 1000 + i * 50));
            // Mark sample index 5 as rejected.
            bool[] perSample = new bool[samples.Count];
            perSample[5] = true;
            var flags = new OutlierFlags
            {
                SectionIndex = 0,
                ClassifierMask = 1,
                PackedBitmap = OutlierFlags.BuildPackedBitmap(perSample),
                RejectedCount = 1,
                SampleCount = samples.Count,
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, tension: 0.5,
                out string failure, rejected: flags);
            Assert.Null(failure);
            Assert.True(spline.IsValid);
            // Knot count should be sampleCount - 1 (one rejected sample dropped).
            Assert.Equal(samples.Count - 1, spline.KnotsUT.Length);
            // The rejected sample's UT must NOT be in the knots list.
            for (int k = 0; k < spline.KnotsUT.Length; k++)
                Assert.NotEqual(samples[5].ut, spline.KnotsUT[k]);
        }

        [Fact]
        public void Fit_WithFlags_AfterRejectionTooFewSamples_ReturnsInvalid()
        {
            var samples = new List<TrajectoryPoint>();
            for (int i = 0; i < 5; i++)
                samples.Add(MakePoint(ut: i, lat: i, lon: i, alt: 1000 + i));
            // Reject indices 1,2,3 → 2 kept ≥ 4? No.
            bool[] perSample = new bool[5];
            perSample[1] = true; perSample[2] = true; perSample[3] = true;
            var flags = new OutlierFlags
            {
                SectionIndex = 0,
                ClassifierMask = 1,
                PackedBitmap = OutlierFlags.BuildPackedBitmap(perSample),
                RejectedCount = 3,
                SampleCount = 5,
            };

            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, tension: 0.5,
                out string failure, rejected: flags);
            Assert.False(spline.IsValid);
            Assert.NotNull(failure);
            Assert.Contains("after-rejection", failure);
        }

        [Fact]
        public void Fit_WithoutFlags_LegacyBehaviorUnchanged()
        {
            // Backward-compat regression pin.
            var samples = new List<TrajectoryPoint>();
            for (int i = 0; i < 10; i++)
                samples.Add(MakePoint(ut: i, lat: i, lon: i, alt: 1000 + i));
            var spline = TrajectoryMath.CatmullRomFit.Fit(samples, tension: 0.5, out string failure);
            Assert.Null(failure);
            Assert.True(spline.IsValid);
            Assert.Equal(samples.Count, spline.KnotsUT.Length);
        }
    }
}
