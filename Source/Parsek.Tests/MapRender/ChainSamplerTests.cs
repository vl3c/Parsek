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

        // ---- Overlap-member parity: the sampler resolves the SAME selected-cycle head-UT the legacy
        //      single head uses. (Integration #2: the Director now renders overlap members; this locks
        //      the parity the fix relies on - the map shows ONE ghost at the span-clock head-UT.)

        // A single-member SELF-OVERLAP unit: span [100,200] with CadenceSeconds == span (the span-raised
        // cadence the single-instance scenes use -> one span instance) but OverlapCadenceSeconds < span
        // (so it IS classified overlap). The member window is the full span. This is exactly what a
        // Kerbin launch-to-orbit mission looped shorter than its length produces on the map.
        private static GhostPlaybackLogic.LoopUnitSet OverlapUnit(
            int idx = 0, double spanStart = 100, double spanEnd = 200,
            double cadence = 100, double overlapCadence = 25, double anchor = 100)
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                idx, new[] { idx }, spanStart, spanEnd, cadenceSeconds: cadence,
                phaseAnchorUT: anchor, overlapCadenceSeconds: overlapCadence);
            // Sanity: this MUST be an overlap unit (OverlapCadenceSeconds < span), else the test is moot.
            Assert.True(GhostPlaybackLogic.UnitMemberOverlaps(unit));
            var unitsByOwner = new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { idx, unit } };
            var ownerByIndex = new Dictionary<int, int> { { idx, idx } };
            return new GhostPlaybackLogic.LoopUnitSet(unitsByOwner, ownerByIndex);
        }

        private static GhostRenderChain OverlapMemberChain(double spanStart = 100, double spanEnd = 200)
        {
            // One segment spanning the whole member window, so any in-window selected-cycle head-UT
            // lands InSegment and its DriveUT is observable.
            var segs = new List<RenderSegment> { Seg(spanStart, spanEnd) };
            return new GhostRenderChain("rec-overlap", committedIndex: 0, instanceKey: 0,
                segments: segs, windowStartUT: spanStart, windowEndUT: spanEnd);
        }

        [Fact]
        public void OverlapMember_SampledUT_EqualsResolveTrackingStationSampleUT()
        {
            // THE PARITY THE FIX RELIES ON: for an overlap member, the UT ChainSampler.Sample resolves
            // (DriveUT) must equal what GhostPlaybackLogic.ResolveTrackingStationSampleUT - the SAME pure
            // span clock the legacy single head uses - returns for that member at the same liveUT. Both
            // run on the unit's span-raised CadenceSeconds, NOT OverlapCadenceSeconds, so they pick the
            // identical selected-cycle head-UT (one ghost on the map, not N instances).
            var units = OverlapUnit();
            var chain = OverlapMemberChain();

            // liveUT 350: elapsed 250, cadence 100 -> cycle 2, phaseInCycle 50 -> loopUT 150 (in window).
            const double liveUT = 350.0;
            double expected = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 0, memberStartUT: 100, memberEndUT: 200, liveUT: liveUT, units, out bool hidden);
            Assert.False(hidden); // selected cycle is inside the member window -> render, not hidden

            var s = ChainSampler.Sample(chain, liveUT, units);
            Assert.Equal(Coverage.InSegment, s.Coverage);
            Assert.Equal(expected, s.DriveUT, 6);      // sampler lands on the legacy single-head UT
            Assert.Equal(150.0, s.DriveUT, 6);         // and that UT is the selected-cycle head (sanity)
        }

        [Theory]
        [InlineData(110.0)] // cycle 0, phase 10 -> loopUT 110
        [InlineData(225.0)] // cycle 1, phase 25 -> loopUT 125
        [InlineData(380.0)] // cycle 2, phase 80 -> loopUT 180
        public void OverlapMember_SampledUT_TracksLegacyHead_AcrossCycles(double liveUT)
        {
            // Across several relaunch cycles the sampler stays glued to the legacy single-head UT: the
            // map ghost rides ONE span instance (the selected cycle), never enumerating instances.
            var units = OverlapUnit();
            var chain = OverlapMemberChain();

            double expected = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 0, memberStartUT: 100, memberEndUT: 200, liveUT: liveUT, units, out bool hidden);
            Assert.False(hidden);

            var s = ChainSampler.Sample(chain, liveUT, units);
            Assert.Equal(Coverage.InSegment, s.Coverage);
            Assert.Equal(expected, s.DriveUT, 6);
        }

        [Fact]
        public void OverlapMember_BeforeSpanStart_IsOutside_LikeLegacyHidden()
        {
            // Before the phase anchor the span clock is unresolved -> ResolveTrackingStationSampleUT
            // reports hidden, and ChainSampler routes the same frame to Outside (render nothing). Parity
            // on the hide path too.
            var units = OverlapUnit(anchor: 100);
            var chain = OverlapMemberChain();

            const double liveUT = 50.0; // before the anchor -> span clock unresolved -> hidden
            GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                i: 0, memberStartUT: 100, memberEndUT: 200, liveUT: liveUT, units, out bool hidden);
            Assert.True(hidden);

            var s = ChainSampler.Sample(chain, liveUT, units);
            Assert.Equal(Coverage.OutsideWindow, s.Coverage);
        }
    }
}
