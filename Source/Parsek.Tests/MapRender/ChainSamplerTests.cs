using System.Collections.Generic;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-2 guard for the sampler's OWN logic: the renderHidden→Outside routing and the
    /// coverage→GhostSample mapping, plus the non-looped identity mapping (an empty LoopUnitSet
    /// leaves sampleUT == liveUT). The span-clock math itself (loop shift, decompression, schedule)
    /// is covered by the existing GhostPlaybackLogic span-clock tests and exercised in-game; here we
    /// only assert the sampler routes a non-member member by identity and classifies correctly.
    ///
    /// A regression here would mis-route a frame: e.g. drawing during a pre-launch/tail window,
    /// retiring a ghost that is merely mid-gap (should hold), or losing the DriveUT.
    /// </summary>
    public class ChainSamplerTests
    {
        private static RenderSegment Seg(double start, double end)
            => new RenderSegment(SegmentKind.Loiter, Treatment.StockConic, start, end, "Kerbin", SegmentPayload.Traced);

        private static GhostRenderChain Chain()
        {
            var segs = new List<RenderSegment> { Seg(100, 110), Seg(120, 130) }; // gap [110,120]
            return new GhostRenderChain("rec-S", committedIndex: 0, instanceKey: 0,
                segments: segs, windowStartUT: 100, windowEndUT: 130);
        }

        private static GhostPlaybackLogic.LoopUnitSet NonLooped => GhostPlaybackLogic.LoopUnitSet.Empty;

        [Fact]
        public void NullChain_IsOutside()
        {
            Assert.Equal(Coverage.OutsideWindow, ChainSampler.Sample(null, 105, NonLooped).Coverage);
        }

        [Fact]
        public void NonLooped_InSegment_IdentityDriveUT()
        {
            var s = ChainSampler.Sample(Chain(), 105, NonLooped);
            Assert.Equal(Coverage.InSegment, s.Coverage);
            Assert.Equal(105, s.DriveUT);              // identity: sampleUT == liveUT for a non-member
            Assert.Equal(Treatment.StockConic, s.Treatment);
            Assert.Equal("Kerbin", s.FrameBodyName);
            Assert.Equal(0, s.SegmentIndex);
        }

        [Fact]
        public void NonLooped_MidGap_Holds_NotRetire()
        {
            var s = ChainSampler.Sample(Chain(), 115, NonLooped);
            Assert.Equal(Coverage.InInteriorGap, s.Coverage); // hold last intent, do not retire
            Assert.Equal(Treatment.None, s.Treatment);
        }

        [Fact]
        public void NonLooped_PastEnd_IsOutside()
        {
            var s = ChainSampler.Sample(Chain(), 200, NonLooped);
            Assert.Equal(Coverage.OutsideWindow, s.Coverage);
        }
    }
}
