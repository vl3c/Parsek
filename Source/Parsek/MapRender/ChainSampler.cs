namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 2 (design §6.3): the single, pure <c>(chain, liveUT) → GhostSample</c> resolver that
    /// collapses the old duplicated "(recording, UT) → where/what" resolvers into one.
    ///
    /// It REUSES the span clock through the existing, already-tested entry point
    /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/> (which internally runs
    /// <c>DecideUnitMemberRender</c> → <c>TryComputeSpanLoopUT</c>, including loiter-cut decompression
    /// and the relaunch schedule). That function is pure and scene-agnostic, so the same mapping
    /// serves flight-map and the Tracking Station. For a non-looped member (not in any unit) it
    /// returns liveUT unchanged — the identity mapping the design requires for exact recordings.
    ///
    /// The sampler stays Unity-world-free: it resolves the assembled-chain UT, classifies coverage,
    /// and hands the segment + DriveUT downstream; world projection is the scene/treatment's job.
    /// </summary>
    internal static class ChainSampler
    {
        internal static GhostSample Sample(GhostRenderChain chain, double liveUT, GhostPlaybackLogic.LoopUnitSet units)
        {
            if (chain == null)
                return GhostSample.Outside();

            // Map live UT → assembled-chain UT via the reused span clock. The member's assembled
            // window is the chain's [WindowStartUT, WindowEndUT]; renderHidden = the span clock parked
            // the member outside its window (pre-launch / inter-cycle tail) → render nothing.
            double sampleUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                chain.CommittedIndex,
                chain.WindowStartUT,
                chain.WindowEndUT,
                liveUT,
                units,
                out bool renderHidden);

            if (renderHidden)
                return GhostSample.Outside();

            Coverage coverage = chain.ClassifyCoverage(sampleUT, out RenderSegment seg, out int index);
            switch (coverage)
            {
                case Coverage.InSegment:
                    return GhostSample.InSegment(seg, index, sampleUT);
                case Coverage.InInteriorGap:
                    return GhostSample.Gap(sampleUT);
                default:
                    return GhostSample.Outside();
            }
        }
    }
}
