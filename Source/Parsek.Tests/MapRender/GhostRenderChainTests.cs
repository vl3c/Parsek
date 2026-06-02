using System.Collections.Generic;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-0 guard for the render-chain locate / coverage primitive that the sampler (Phase 2)
    /// and director (Phase 3) depend on. Builds a chain with two rigid-adjacent segments, an
    /// interior gap, and a final segment, then sweeps UTs.
    ///
    /// Each assertion states the bug it catches: a wrong locate would mis-route a frame to the
    /// wrong segment (wrong treatment / position); a wrong coverage tri-state would either
    /// double-draw, blink a ghost off inside a gap (should hold), or fail to retire it past the end.
    /// See docs/dev/design-map-ts-render-architecture.md §6.2/§6.3.
    /// </summary>
    public class GhostRenderChainTests
    {
        private static RenderSegment Seg(double start, double end, SegmentKind kind = SegmentKind.Loiter)
            => new RenderSegment(kind, Treatment.StockConic, start, end, "Kerbin", SegmentPayload.Traced);

        // seg0 [0,10), seg1 [10,20) (rigid-adjacent), gap [20,30), seg2 [30,40] (last, inclusive end). Window [0,40].
        private static GhostRenderChain BuildChain()
        {
            var segs = new List<RenderSegment>
            {
                Seg(0, 10, SegmentKind.Ascent),
                Seg(10, 20, SegmentKind.Loiter),
                Seg(30, 40, SegmentKind.Landing),
            };
            return new GhostRenderChain("rec-A", committedIndex: 3, instanceKey: 0,
                segments: segs, windowStartUT: 0, windowEndUT: 40);
        }

        // Coverage is an internal enum, so a public [Theory] method cannot take it as a parameter
        // (CS0051). Pass its underlying int (a compile-time constant; the enum is visible to the test
        // assembly via InternalsVisibleTo) and cast back inside.
        [Theory]
        // ut, expected coverage, expected segment index (-1 when not InSegment)
        [InlineData(-1.0, (int)Coverage.OutsideWindow, -1)]   // before window -> retire, not a phantom seg0
        [InlineData(0.0, (int)Coverage.InSegment, 0)]          // window/segment start is inclusive
        [InlineData(5.0, (int)Coverage.InSegment, 0)]
        [InlineData(10.0, (int)Coverage.InSegment, 1)]         // shared rigid boundary belongs to the later segment
        [InlineData(19.9, (int)Coverage.InSegment, 1)]
        [InlineData(20.0, (int)Coverage.InInteriorGap, -1)]    // exclusive end of a non-last segment with a following gap
        [InlineData(25.0, (int)Coverage.InInteriorGap, -1)]    // mid-gap -> hold, never retire/blink
        [InlineData(30.0, (int)Coverage.InSegment, 2)]
        [InlineData(35.0, (int)Coverage.InSegment, 2)]
        [InlineData(40.0, (int)Coverage.InSegment, 2)]         // last segment end is inclusive
        [InlineData(40.001, (int)Coverage.OutsideWindow, -1)]  // past end -> retire
        [InlineData(100.0, (int)Coverage.OutsideWindow, -1)]
        public void ClassifyCoverage_ResolvesSegmentsGapsAndWindow(double ut, int expectedCoverage, int expectedIndex)
        {
            Coverage expected = (Coverage)expectedCoverage;
            var chain = BuildChain();
            var cov = chain.ClassifyCoverage(ut, out RenderSegment seg, out int index);
            Assert.Equal(expected, cov);
            Assert.Equal(expectedIndex, index);
            if (expected == Coverage.InSegment)
                Assert.Equal(chain.Segments[expectedIndex].StartUT, seg.StartUT);
        }

        [Fact]
        public void LocateSegmentIndex_MatchesClassify_ForInSegmentUTs()
        {
            var chain = BuildChain();
            // locate is coverage-agnostic about the window; inside a segment the two must agree
            Assert.Equal(0, chain.LocateSegmentIndex(5.0));
            Assert.Equal(1, chain.LocateSegmentIndex(10.0));
            Assert.Equal(2, chain.LocateSegmentIndex(40.0));
            Assert.Equal(-1, chain.LocateSegmentIndex(25.0)); // gap -> no segment
        }

        [Fact]
        public void EmptyChain_IsAlwaysGapOrOutside_NeverThrows()
        {
            var chain = new GhostRenderChain("rec-empty", 0, 0,
                segments: new List<RenderSegment>(), windowStartUT: 0, windowEndUT: 10);
            Assert.Equal(-1, chain.LocateSegmentIndex(5.0));
            Assert.Equal(Coverage.InInteriorGap, chain.ClassifyCoverage(5.0, out _, out _));   // in window, no segment
            Assert.Equal(Coverage.OutsideWindow, chain.ClassifyCoverage(50.0, out _, out _));
        }
    }
}
