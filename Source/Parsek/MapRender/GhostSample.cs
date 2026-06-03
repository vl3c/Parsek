namespace Parsek.MapRender
{
    /// <summary>
    /// The pure output of <c>ChainSampler.Sample(chain, liveUT)</c> (design §6.3 / §6.10): what the
    /// ghost is doing at a given live UT, after the span clock has mapped live UT → assembled-chain
    /// UT and the chain has been classified.
    ///
    /// World-space position / orbit-line geometry are NOT resolved here — they are produced
    /// downstream by the scene/treatment from (FrameBodyName, the segment's payload, DriveUT) at draw
    /// time, so the sampler stays free of KSP world-space. The conic (when present) is reachable via
    /// <see cref="Segment"/>.Payload.Conic.
    /// </summary>
    internal readonly struct GhostSample
    {
        internal Coverage Coverage { get; }
        /// <summary>Valid only when <see cref="Coverage"/> == InSegment.</summary>
        internal RenderSegment Segment { get; }
        /// <summary>Index into the chain's segments, or -1.</summary>
        internal int SegmentIndex { get; }
        /// <summary>The active treatment (Segment.Treatment when InSegment, else None).</summary>
        internal Treatment Treatment { get; }
        /// <summary>Body frame of the active segment (null when not InSegment).</summary>
        internal string FrameBodyName { get; }
        /// <summary>Assembled-chain UT to drive the icon/conic at for this sample.</summary>
        internal double DriveUT { get; }

        private GhostSample(
            Coverage coverage, RenderSegment segment, int segmentIndex,
            Treatment treatment, string frameBodyName, double driveUT)
        {
            Coverage = coverage;
            Segment = segment;
            SegmentIndex = segmentIndex;
            Treatment = treatment;
            FrameBodyName = frameBodyName;
            DriveUT = driveUT;
        }

        internal static GhostSample InSegment(RenderSegment segment, int index, double driveUT)
            => new GhostSample(Coverage.InSegment, segment, index, segment.Treatment, segment.FrameBodyName, driveUT);

        /// <summary>Mid-chain gap (e.g. a flexible SOI seam the clock landed inside). The director holds the last intent.</summary>
        internal static GhostSample Gap(double driveUT)
            => new GhostSample(Coverage.InInteriorGap, default(RenderSegment), -1, Treatment.None, null, driveUT);

        /// <summary>Before launch / past end / inter-cycle tail. The director renders nothing.</summary>
        internal static GhostSample Outside()
            => new GhostSample(Coverage.OutsideWindow, default(RenderSegment), -1, Treatment.None, null, 0.0);
    }
}
