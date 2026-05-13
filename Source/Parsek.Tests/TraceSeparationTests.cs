using Xunit;

namespace Parsek.Tests
{
    public class TraceSeparationTests
    {
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
    }
}
