using System.Globalization;

namespace Parsek.MapRender
{
    // Phase 0 data model for the map/TS render rewrite.
    // See docs/dev/design-map-ts-render-architecture.md §6.2 / §6.10 and
    // docs/dev/plans/map-ts-render-rewrite-phases.md (Phase 0).
    //
    // These are runtime-only render-view types: nothing here is persisted. A
    // RenderSegment is a render-oriented view over existing playback data
    // (OrbitSegment / recorded Points), NOT a copy of it.

    /// <summary>How a segment is drawn. Exactly one treatment is active per ghost per frame.</summary>
    internal enum Treatment
    {
        None = 0,
        /// <summary>Stock KSP object: proto-vessel icon + KSP orbit line. MANAGED vs KSP.</summary>
        StockConic = 1,
        /// <summary>Our drawn polyline of recorded points + marker/label icon. Fully owned.</summary>
        TracedPath = 2,
    }

    /// <summary>The gameplay role of a segment within the assembled chain.</summary>
    internal enum SegmentKind
    {
        Other = 0,
        Ascent = 1,
        Loiter = 2,
        Eject = 3,
        Transfer = 4,
        Approach = 5,
        ArrivalOrbit = 6,
        ArrivalLoiter = 7,
        Landing = 8,
        Surface = 9,
    }

    /// <summary>Boundary character between two consecutive segments.</summary>
    internal enum SeamKind
    {
        /// <summary>No seam (chain start/end, or no neighbour).</summary>
        None = 0,
        /// <summary>Must connect cleanly (sub-chain internal, ascent↔orbit, orbit↔landing).</summary>
        Rigid = 1,
        /// <summary>Tolerated, off-camera discontinuity at an SOI boundary (design §3.3).</summary>
        FlexibleSoi = 2,
    }

    /// <summary>
    /// Three-valued coverage of a UT against a chain (design §6.3). The sampler maps live UT
    /// to assembled-chain UT first, then classifies.
    /// </summary>
    internal enum Coverage
    {
        /// <summary>Inside a real segment — render it.</summary>
        InSegment = 0,
        /// <summary>Between two chain segments (e.g. a flexible-seam UT gap). Hold last intent (§6.4).</summary>
        InInteriorGap = 1,
        /// <summary>Before launch / past end / inter-cycle tail. Render nothing (retire).</summary>
        OutsideWindow = 2,
    }

    /// <summary>
    /// What a segment draws. A StockConic segment carries the <see cref="OrbitSegment"/> (a recorded
    /// orbit OR the generated transfer). A TracedPath segment carries no conic; the treatment reads
    /// the source trajectory's recorded Points / TrackSection frames within the segment's
    /// [StartUT, EndUT] window (the assembled-chain window equals the recorded window for the
    /// faithful members that own traced paths).
    /// </summary>
    internal readonly struct SegmentPayload
    {
        private readonly OrbitSegment conic;
        internal bool HasConic { get; }
        /// <summary>Valid only when <see cref="HasConic"/>.</summary>
        internal OrbitSegment Conic => conic;

        private SegmentPayload(OrbitSegment c, bool hasConic)
        {
            conic = c;
            HasConic = hasConic;
        }

        internal static SegmentPayload ForConic(OrbitSegment seg) => new SegmentPayload(seg, true);
        /// <summary>A traced-path segment: no conic; the treatment reads recorded points by UT window.</summary>
        internal static SegmentPayload Traced => new SegmentPayload(default(OrbitSegment), false);
    }

    /// <summary>
    /// One contiguous stretch of a ghost's path with a single <see cref="Treatment"/> and a single
    /// body frame (design §6.2). UTs are in the ASSEMBLED-chain clock (post-trim, post-reanchor).
    /// Ordered within a <see cref="GhostRenderChain"/> by <see cref="StartUT"/>.
    ///
    /// Frame is a body name (string) for v1. Per the §15.3 probe (2026-06-02) transfer-phase
    /// debris cannot be produced in v1 scope, so a segment is always body-anchored. If scope later
    /// grows to multi-hop / gravity-assist re-aim where a child rides a parent's *generated*
    /// conic, widen this to a small discriminated frame type (body | parent-generated-conic) — the
    /// assembler MUST never hand a parent-anchored child a re-aimed segment list (add the debug
    /// assertion in ChainAssembler), so that failure stays loud rather than silently body-framed.
    /// </summary>
    internal readonly struct RenderSegment
    {
        internal SegmentKind Kind { get; }
        internal Treatment Treatment { get; }
        internal double StartUT { get; }
        internal double EndUT { get; }
        internal string FrameBodyName { get; }
        internal SegmentPayload Payload { get; }
        /// <summary>True for the re-aimed (synthesized) transfer.</summary>
        internal bool IsGenerated { get; }
        internal SeamKind LeadingSeam { get; }
        internal SeamKind TrailingSeam { get; }

        internal RenderSegment(
            SegmentKind kind,
            Treatment treatment,
            double startUT,
            double endUT,
            string frameBodyName,
            SegmentPayload payload,
            bool isGenerated = false,
            SeamKind leadingSeam = SeamKind.None,
            SeamKind trailingSeam = SeamKind.None)
        {
            Kind = kind;
            Treatment = treatment;
            StartUT = startUT;
            EndUT = endUT;
            FrameBodyName = frameBodyName;
            Payload = payload;
            IsGenerated = isGenerated;
            LeadingSeam = leadingSeam;
            TrailingSeam = trailingSeam;
        }

        internal double DurationSeconds => EndUT - StartUT;

        public override string ToString()
            => string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} UT={2:F1}-{3:F1} body={4}{5}{6}",
                Kind, Treatment, StartUT, EndUT, FrameBodyName ?? "?",
                IsGenerated ? " gen" : string.Empty,
                TrailingSeam == SeamKind.FlexibleSoi ? " soiSeam" : string.Empty);
    }
}
