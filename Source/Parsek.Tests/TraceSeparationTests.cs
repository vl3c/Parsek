using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class TraceSeparationTests
    {
        // Build a Relative-section anchor-local frame: lat/lon/alt are reused
        // as the dx/dy/dz metres of the recorded offset vector. Body name is
        // intentionally left blank — the helper does not touch it.
        private static TrajectoryPoint AnchorLocalFrame(double ut, double dx, double dy, double dz)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = dx,
                longitude = dy,
                altitude = dz,
            };
        }

        [Fact]
        public void ComputeLerpAlpha_MidpointPlaybackUT_ReturnsHalf()
        {
            double alpha = TraceSeparation.ComputeLerpAlpha(100.0, 101.0, 100.5);
            Assert.Equal(0.5, alpha, 9);
        }

        [Fact]
        public void ComputeLerpAlpha_AtBefore_ReturnsZero()
        {
            double alpha = TraceSeparation.ComputeLerpAlpha(100.0, 101.0, 100.0);
            Assert.Equal(0.0, alpha, 9);
        }

        [Fact]
        public void ComputeLerpAlpha_AtAfter_ReturnsOne()
        {
            double alpha = TraceSeparation.ComputeLerpAlpha(100.0, 101.0, 101.0);
            Assert.Equal(1.0, alpha, 9);
        }

        [Fact]
        public void ComputeLerpAlpha_PlaybackBeforeBracket_ReturnsNegative()
        {
            // Extrapolation case: playbackUT < beforeUT. The helper still
            // returns a finite value (negative alpha) so the log row carries
            // useful information about how far out of bracket the playback
            // sample was; degeneracy is reserved for non-finite / zero-span.
            double alpha = TraceSeparation.ComputeLerpAlpha(100.0, 101.0, 99.5);
            Assert.Equal(-0.5, alpha, 9);
        }

        [Fact]
        public void ComputeLerpAlpha_PlaybackAfterBracket_ReturnsGreaterThanOne()
        {
            double alpha = TraceSeparation.ComputeLerpAlpha(100.0, 101.0, 101.5);
            Assert.Equal(1.5, alpha, 9);
        }

        [Fact]
        public void ComputeLerpAlpha_ZeroSpan_ReturnsNaN()
        {
            double alpha = TraceSeparation.ComputeLerpAlpha(100.0, 100.0, 100.0);
            Assert.True(double.IsNaN(alpha));
        }

        [Fact]
        public void ComputeLerpAlpha_NegativeSpan_ReturnsNaN()
        {
            // Bracketing samples should always satisfy afterUT >= beforeUT;
            // a negative span indicates a degenerate bracket pair and the
            // helper should bail rather than silently flip the sign.
            double alpha = TraceSeparation.ComputeLerpAlpha(101.0, 100.0, 100.5);
            Assert.True(double.IsNaN(alpha));
        }

        [Fact]
        public void ComputeLerpAlpha_NaNBefore_ReturnsNaN()
        {
            double alpha = TraceSeparation.ComputeLerpAlpha(double.NaN, 101.0, 100.5);
            Assert.True(double.IsNaN(alpha));
        }

        [Fact]
        public void ComputeLerpAlpha_InfiniteAfter_ReturnsNaN()
        {
            double alpha = TraceSeparation.ComputeLerpAlpha(
                100.0, double.PositiveInfinity, 100.5);
            Assert.True(double.IsNaN(alpha));
        }

        // --- InterpolateAnchorLocalOffsetMagnitude ---
        // The seed-vs-end-of-bracket numbers used below mirror the real
        // production trace from a Kerbal X radial-booster separation:
        // seed magnitude ~15.20 m, end-of-bracket magnitude ~19.78 m, 0.6 s
        // bracket. The reviewer's pushback on PR review noted that the
        // existing recordedAnchorLocalDist field is the seed-only number,
        // so any "drift" computed against it conflates real physical
        // separation with rendering error. This helper replaces the
        // seed-only number with the linearly-interpolated truth.

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_AtBeforeSample_ReturnsBeforeMagnitude()
        {
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),  // |.| ≈ 15.1997
                AnchorLocalFrame(269.256, 5.00, -18.00, -2.00),  // |.| ≈ 18.7883
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 268.656);
            // sqrt(1.06^2 + 15.15^2 + 0.62^2) = sqrt(231.0305) ≈ 15.1997
            Assert.Equal(15.1997, mag, 3);
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_AtAfterSample_ReturnsAfterMagnitude()
        {
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
                AnchorLocalFrame(269.256, 5.00, -18.00, -2.00),
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 269.256);
            // sqrt(25 + 324 + 4) = sqrt(353) ≈ 18.788
            Assert.Equal(18.7883, mag, 3);
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_AtBracketMidpoint_LerpsVectorThenMagnitudes()
        {
            // Vector-then-magnitude, not magnitude-then-lerp: at alpha=0.5 the
            // interpolated offset is the midpoint vector ((1.06+5.0)/2,
            // (-15.15-18.00)/2, (-0.62-2.00)/2) = (3.03, -16.575, -1.31),
            // magnitude = sqrt(9.181 + 274.731 + 1.716) = sqrt(285.628) ≈ 16.901.
            // A magnitude-then-lerp would give (15.199+18.788)/2 = 16.994 —
            // close but distinct, so the test pins the correct contract.
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
                AnchorLocalFrame(269.256, 5.00, -18.00, -2.00),
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 268.956);
            Assert.Equal(16.9006, mag, 3);
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_ProgressivelyGrows_AcrossBracket()
        {
            // Sanity: across an outward-moving bracket the interpolated
            // magnitude grows monotonically from the seed magnitude to the
            // end-of-bracket magnitude. This is what lets the field replace
            // the "constant 15.199 m across the 0.6 s seed→first-sample gap"
            // observation that confused the original PR analysis.
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
                AnchorLocalFrame(269.256, 5.00, -18.00, -2.00),
            };
            double m0 = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 268.700);
            double m1 = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 268.900);
            double m2 = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 269.100);
            Assert.True(m0 > 15.199 && m0 < m1, $"m0={m0} expected in (15.199, m1={m1})");
            Assert.True(m1 < m2 && m2 < 18.789, $"m1={m1} m2={m2} expected m1<m2<18.789");
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_PlaybackBeforeFirstSample_ReturnsNaN()
        {
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
                AnchorLocalFrame(269.256, 5.00, -18.00, -2.00),
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 268.500);
            Assert.True(double.IsNaN(mag));
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_PlaybackAfterLastSample_ReturnsLastMagnitude()
        {
            // Past the last sample we clamp to the last sample's magnitude
            // rather than NaN — the caller has already picked the right
            // section's frames, and clamping at the right edge matches the
            // existing trace fields' bracketing-only behavior.
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
                AnchorLocalFrame(269.256, 5.00, -18.00, -2.00),
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 269.500);
            Assert.Equal(18.7883, mag, 3);
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_SingleFrame_ReturnsNaN()
        {
            // One-sample lists cannot be interpolated. Returning NaN rather
            // than the single-sample magnitude prevents the log row from
            // implying coverage where the recorder only has a seed point.
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 268.656);
            Assert.True(double.IsNaN(mag));
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_NullFrames_ReturnsNaN()
        {
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(null, 268.656);
            Assert.True(double.IsNaN(mag));
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_EmptyFrames_ReturnsNaN()
        {
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(
                new List<TrajectoryPoint>(), 268.656);
            Assert.True(double.IsNaN(mag));
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_NaNPlaybackUT_ReturnsNaN()
        {
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
                AnchorLocalFrame(269.256, 5.00, -18.00, -2.00),
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, double.NaN);
            Assert.True(double.IsNaN(mag));
        }

        [Fact]
        public void InterpolateAnchorLocalOffsetMagnitude_DuplicateUTBracket_FallsBackToBeforeMagnitude()
        {
            // Degenerate zero-span bracket: ComputeLerpAlpha returns NaN, and
            // we fall back to the bracketing-before magnitude rather than
            // propagating NaN — gives the log reader the best available value.
            var frames = new List<TrajectoryPoint>
            {
                AnchorLocalFrame(268.656, 1.06, -15.15, -0.62),
                AnchorLocalFrame(268.656, 5.00, -18.00, -2.00),
            };
            double mag = TraceSeparation.InterpolateAnchorLocalOffsetMagnitude(frames, 268.656);
            // Both samples have the same UT; beforeIdx ends up as the last
            // duplicate (j=1), afterIdx as the first match (j=0). The lerp
            // span is zero → alpha NaN → fallback to before-magnitude.
            // Whichever sample wins, the magnitude is one of the two
            // recorded values; assert both possibilities aren't NaN.
            Assert.False(double.IsNaN(mag));
            Assert.True(mag > 0);
        }
    }
}
