using System.Collections.Generic;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §6: the NEW first-class composable trajectory unit — a polymorphic object owning
    /// a contiguous run of trajectory with ONE provenance, ONE anchor frame, ONE treatment choice, and
    /// its seam contracts. This is the "heart of the design": the GMAT command-owns-behaviour pattern
    /// replacing the <c>RenderSegment</c> struct + <c>SegmentKind</c> enum + assembler <c>if/else</c> +
    /// director <c>switch</c> + 5-boolean predicates. Adding a phase becomes one new subclass, not a
    /// five-site edit.
    ///
    /// <para><b>NEW, additive, NOT wired in Phase 1.</b> The factory that builds these from env-class
    /// (faithful) or the <c>ReaimMissionPlan</c> (re-aimed) is Phase 2; the spine consuming them is
    /// Phase 3. The concrete subclasses delegate geometry to the existing kernels (<c>OrbitSegment</c>,
    /// <c>OrbitArcSampler</c>, the span clock) — this is a re-typing, not a re-implementation.</para>
    /// </summary>
    internal abstract class TrajectoryPhase
    {
        /// <summary>
        /// Stable RUNTIME render-layer identity (design §6). NOT the persisted
        /// <c>Mission.ExcludedIntervalKeys</c> / <c>&lt;head&gt;</c>/<c>segN</c> selection keys — those
        /// stay a composition-layer persistence concern (design §4).
        /// </summary>
        internal PhaseId Id { get; }

        /// <summary>The phase's gameplay kind (design §6) — the authoritative phase vocabulary that
        /// replaces the cosmetic <c>SegmentKind</c>.</summary>
        internal PhaseKind Kind { get; }

        /// <summary>Where the geometry came from (design §5.1) — stamped by the producer, never re-derived.</summary>
        internal SegmentProvenance Provenance { get; }

        /// <summary>Which frame the phase lives in (design §5.2).</summary>
        internal AnchorFrame Anchor { get; }

        /// <summary>Phase start in the ASSEMBLED-chain clock.</summary>
        internal double StartUt { get; }

        /// <summary>Phase end in the ASSEMBLED-chain clock.</summary>
        internal double EndUt { get; }

        /// <summary>The seam joining the PRECEDING phase to this one (null = chain start / no neighbour).</summary>
        internal PhaseSeam LeadingSeam { get; }

        /// <summary>The seam joining this phase to the FOLLOWING one (null = chain end / no neighbour).</summary>
        internal PhaseSeam TrailingSeam { get; }

        private protected TrajectoryPhase(
            PhaseId id,
            PhaseKind kind,
            SegmentProvenance provenance,
            AnchorFrame anchor,
            double startUt,
            double endUt,
            PhaseSeam leadingSeam,
            PhaseSeam trailingSeam)
        {
            Id = id;
            Kind = kind;
            Provenance = provenance;
            Anchor = anchor;
            StartUt = startUt;
            EndUt = endUt;
            LeadingSeam = leadingSeam;
            TrailingSeam = trailingSeam;
        }

        /// <summary>The phase's duration in the assembled-chain clock.</summary>
        internal double DurationSeconds => EndUt - StartUt;

        /// <summary>
        /// design §6: how the phase is drawn — was <c>ChainAssembler</c>'s orbit-vs-polyline if/else,
        /// now polymorphic per phase. Returns the existing live <see cref="Treatment"/>
        /// (<c>StockConic</c> / <c>TracedPath</c>); the future <c>SuppressedMarker</c> tier is a director
        /// fallback applied to a no-bounds/no-conic frame, not a phase-declared treatment (design §8 /
        /// §11.4), so it is NOT a new <see cref="Treatment"/> value here in Phase 1.
        /// </summary>
        internal abstract Treatment ResolveTreatment();

        /// <summary>
        /// design §6: does this phase own <paramref name="ut"/> (assembled-chain clock)? Default is the
        /// half-open interval <c>[StartUt, EndUt)</c> so a UT shared with the next phase's start belongs
        /// to the LATER phase (mirroring <see cref="GhostRenderChain.LocateSegmentIndex"/>). The CHAIN
        /// is responsible for the inclusive end of the LAST phase. NO current subclass overrides this -
        /// in particular <see cref="HoldPhase"/> deliberately does NOT: its [StartUt, EndUt) base
        /// interval IS the warp-skipped span it must cover, so the base implementation is already correct
        /// for it (review N2; do not add a HoldPhase override). A future subclass overrides only if its
        /// coverage genuinely differs from its [StartUt, EndUt) bounds.
        /// </summary>
        internal virtual bool CoversUt(double ut)
        {
            if (double.IsNaN(ut) || double.IsInfinity(ut))
                return false;
            return ut >= StartUt && ut < EndUt;
        }

        /// <summary>
        /// design §6: the geometry the phase contributes for the current frame. In Phase 1 this is the
        /// typed-but-unwired contract; the concrete subclasses return the same <see cref="RenderSegment"/>
        /// the legacy assembler would (so Phase 2 can byte-parity it), delegating to the existing
        /// kernels. A phase with no geometry this frame (e.g. <see cref="HoldPhase"/>) yields nothing.
        /// </summary>
        internal abstract IEnumerable<RenderSegment> Emit(SampleContext ctx);

        // Review N6: the ChainSampler's per-frame InSegment projection used to run Emit(ctx) EVERY frame
        // per ghost - an iterator allocation on the path that went hot at the cutover flip. Emit is
        // UT-INDEPENDENT (no implementation reads ctx.SampleUt) and the anchor-derived frame body is
        // fixed at construction, so the FIRST emitted segment is cacheable once per phase instance.
        // Phases are immutable after construction (the ShadowRenderDriver caches whole PhaseChains per
        // pid signature), so no invalidation is needed. If a future Emit implementation starts reading
        // ctx.SampleUt this cache MUST go - ChainSamplerTests pins the UT-independence so that change
        // fails loudly instead of silently serving a stale frame.
        private bool firstEmitCached;
        private bool firstEmitHasGeometry;
        private RenderSegment firstEmitSegment;

        /// <summary>
        /// The phase's first emitted <see cref="RenderSegment"/>, computed once (context: StartUt + the
        /// anchor body fallback, the SAME projection <c>GeometryParityComparator.ProjectGeometry</c>
        /// uses) and cached. False when the phase emits no geometry (<see cref="HoldPhase"/>).
        /// </summary>
        internal bool TryGetFirstEmittedSegmentCached(out RenderSegment seg)
        {
            if (!firstEmitCached)
            {
                string frameBody = (Anchor is AnchorFrame.BodyAnchor body) ? body.BodyName : null;
                var ctx = new SampleContext(StartUt, frameBody);
                foreach (RenderSegment s in Emit(ctx))
                {
                    firstEmitSegment = s;
                    firstEmitHasGeometry = true;
                    break;
                }
                // Marked cached only AFTER a successful enumeration: if a future Emit threw, the next
                // call rethrows (like the old per-frame inline projection did) instead of silently
                // serving "no geometry" forever (review follow-up nit).
                firstEmitCached = true;
            }
            seg = firstEmitSegment;
            return firstEmitHasGeometry;
        }

        public override string ToString()
            => string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} prov={2} anchor={3} UT={4:F1}-{5:F1} treat={6}",
                PhaseKindTokens.ToToken(Kind), Id, SegmentProvenanceTokens.ToToken(Provenance),
                Anchor == null ? "?" : Anchor.ToToken(), StartUt, EndUt, ResolveTreatment());
    }

    /// <summary>
    /// design §6: the NEW authoritative phase vocabulary, replacing the cosmetic 10-value
    /// <c>SegmentKind</c> (of which only <c>Transfer</c>/<c>Loiter</c>/<c>Surface</c> were ever
    /// assigned).
    ///
    /// <para><b>Old <c>SegmentKind</c> → new <see cref="PhaseKind"/> mapping</b> (design §6):
    /// <list type="bullet">
    ///   <item><c>Ascent</c> → <see cref="Ascent"/></item>
    ///   <item><c>Loiter</c> → <see cref="DepartureLoiter"/> or <see cref="ArrivalLoiter"/> (the
    ///     departure-vs-arrival split is NEW, from <c>EnvironmentToPhase</c> + role)</item>
    ///   <item><c>Eject</c> → <see cref="SoiDeparture"/></item>
    ///   <item><c>Transfer</c> → <see cref="HeliocentricTransfer"/></item>
    ///   <item><c>Approach</c> / <c>ArrivalOrbit</c> → <see cref="SoiArrival"/></item>
    ///   <item><c>ArrivalLoiter</c> → <see cref="ArrivalLoiter"/></item>
    ///   <item><c>Landing</c> → <see cref="Descent"/></item>
    ///   <item><c>Surface</c> → <see cref="Surface"/></item>
    ///   <item><c>Other</c> → context-dependent (an interior gap becomes <see cref="Hold"/>, promoted
    ///     from the invisible <c>InInteriorGap</c> clock insertion)</item>
    /// </list></para>
    /// </summary>
    internal enum PhaseKind
    {
        Unknown = 0,
        Ascent = 1,
        DepartureLoiter = 2,
        SoiDeparture = 3,
        HeliocentricTransfer = 4,
        SoiArrival = 5,
        ArrivalLoiter = 6,
        Descent = 7,
        Surface = 8,
        /// <summary>Promoted from the invisible <c>InInteriorGap</c> clock insertion (design §6).</summary>
        Hold = 9,
    }

    /// <summary>Grep-stable lowercase tokens for <see cref="PhaseKind"/>.</summary>
    internal static class PhaseKindTokens
    {
        internal static string ToToken(PhaseKind kind)
        {
            switch (kind)
            {
                case PhaseKind.Ascent: return "ascent";
                case PhaseKind.DepartureLoiter: return "departure-loiter";
                case PhaseKind.SoiDeparture: return "soi-departure";
                case PhaseKind.HeliocentricTransfer: return "heliocentric-transfer";
                case PhaseKind.SoiArrival: return "soi-arrival";
                case PhaseKind.ArrivalLoiter: return "arrival-loiter";
                case PhaseKind.Descent: return "descent";
                case PhaseKind.Surface: return "surface";
                case PhaseKind.Hold: return "hold";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// design §6: the per-frame context handed to <see cref="TrajectoryPhase.Emit"/>. Phase 1 holds the
    /// minimal slice the typed subclasses need to mirror the legacy assembler's geometry: the assembled
    /// UT being sampled and the resolved frame body name (the v1 <see cref="AnchorFrame.BodyAnchor"/>
    /// payload). It is intentionally small and Unity-free; later phases widen it (the live trajectory
    /// view, the orbit sampler) as the wiring lands.
    /// </summary>
    internal readonly struct SampleContext
    {
        /// <summary>The assembled-chain UT being sampled this frame.</summary>
        internal double SampleUt { get; }
        /// <summary>The resolved body-frame name for the phase (the v1 BodyAnchor payload).</summary>
        internal string FrameBodyName { get; }

        internal SampleContext(double sampleUt, string frameBodyName)
        {
            SampleUt = sampleUt;
            FrameBodyName = frameBodyName;
        }
    }
}
