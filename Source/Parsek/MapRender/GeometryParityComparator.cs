using System.Collections.Generic;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 2 (migration plan §4): a PURE, unit-testable byte-parity comparator over the GEOMETRY
    /// fields of a <see cref="PhaseFactory"/>-built <see cref="PhaseChain"/> against the
    /// <see cref="ChainAssembler"/>-built <see cref="GhostRenderChain"/>.
    ///
    /// <para><b>Parity scope (exactly the geometry, nothing else).</b> Per-segment the comparator checks
    /// <c>Treatment</c>, <c>StartUt</c>, <c>EndUt</c>, <c>FrameBodyName</c>, and the conic-element payload
    /// (<c>HasConic</c> + the Kepler elements when present); chain-level it checks <c>WindowStartUt</c>,
    /// <c>WindowEndUt</c>, and <c>IsFaithfulFallback</c> (<c>GhostRenderChain.cs:24-27</c> — they drive
    /// coverage/clip and matter for the trimmed-window route case). <see cref="PhaseKind"/> /
    /// <see cref="SegmentProvenance"/> (and the legacy cosmetic <c>SegmentKind</c> / <c>IsGenerated</c>)
    /// are DELIBERATELY NOT in the parity set — they are validated by the Phase-1 unit tests, not this
    /// gate (design §6 / plan §4).</para>
    ///
    /// <para>The factory's geometry is obtained by projecting each phase through
    /// <see cref="TrajectoryPhase.Emit"/> (a phase with no geometry — <see cref="HoldPhase"/> — yields
    /// nothing, mirroring the assembler which never emits a hold segment). A COUNT mismatch is recorded
    /// for diagnostics but the per-field geometry diff is the GATE: <see cref="Compare"/> returns the
    /// first diverging field (or <see cref="ParityResult.Match"/>). All float compares use an exact
    /// ordinal/bitwise match (byte-parity, not a tolerance) because the geometry is carried through the
    /// factory unchanged from the same assembler call.</para>
    /// </summary>
    internal static class GeometryParityComparator
    {
        /// <summary>The outcome of a geometry parity compare: either a match or the first divergence.</summary>
        internal readonly struct ParityResult
        {
            /// <summary>True iff every checked geometry field matched.</summary>
            internal bool IsMatch { get; }
            /// <summary>The diverging field token (grep-stable), or null on a match.</summary>
            internal string DivergingField { get; }
            /// <summary>The segment ordinal the divergence was found at, or -1 for a chain-level field.</summary>
            internal int SegmentIndex { get; }
            /// <summary>A human-readable "expected vs actual" detail, or null on a match.</summary>
            internal string Detail { get; }
            /// <summary>True when the factory's emitted geometry-segment COUNT differed from the assembler's
            /// (recorded for diagnostics; a count mismatch always also yields a diverging field).</summary>
            internal bool CountMismatch { get; }

            private ParityResult(bool isMatch, string field, int segIndex, string detail, bool countMismatch)
            {
                IsMatch = isMatch;
                DivergingField = field;
                SegmentIndex = segIndex;
                Detail = detail;
                CountMismatch = countMismatch;
            }

            internal static ParityResult Match => new ParityResult(true, null, -1, null, false);

            internal static ParityResult Diverge(string field, int segIndex, string detail, bool countMismatch = false)
                => new ParityResult(false, field, segIndex, detail, countMismatch);

            public override string ToString()
                => IsMatch
                    ? "match"
                    : string.Format(CultureInfo.InvariantCulture,
                        "field={0} seg={1} countMismatch={2} {3}",
                        DivergingField, SegmentIndex, CountMismatch, Detail ?? string.Empty);
        }

        /// <summary>
        /// Compare the factory's <paramref name="factoryChain"/> against the assembler's
        /// <paramref name="assemblerChain"/> on the geometry fields only. Returns
        /// <see cref="ParityResult.Match"/> when every field matches, or the first divergence.
        /// </summary>
        internal static ParityResult Compare(PhaseChain factoryChain, GhostRenderChain assemblerChain)
        {
            // Two nulls are vacuously equal; one null is a divergence.
            if (factoryChain == null && assemblerChain == null)
                return ParityResult.Match;
            if (factoryChain == null)
                return ParityResult.Diverge("chain-null", -1, "factory chain null, assembler non-null");
            if (assemblerChain == null)
                return ParityResult.Diverge("chain-null", -1, "assembler chain null, factory non-null");

            // Chain-level geometry fields (drive coverage/clip; the trimmed-window route case).
            if (!BitEquals(factoryChain.WindowStartUt, assemblerChain.WindowStartUT))
                return ParityResult.Diverge("WindowStartUt", -1,
                    Fmt(factoryChain.WindowStartUt, assemblerChain.WindowStartUT));
            if (!BitEquals(factoryChain.WindowEndUt, assemblerChain.WindowEndUT))
                return ParityResult.Diverge("WindowEndUt", -1,
                    Fmt(factoryChain.WindowEndUt, assemblerChain.WindowEndUT));
            if (factoryChain.IsFaithfulFallback != assemblerChain.IsFaithfulFallback)
                return ParityResult.Diverge("IsFaithfulFallback", -1,
                    Fmt(factoryChain.IsFaithfulFallback, assemblerChain.IsFaithfulFallback));

            // Project the factory's phases to the geometry segments they emit (UT-ordered, mirroring the
            // assembler's ordering). A HoldPhase yields nothing — the assembler never emits a hold, so the
            // projected list still aligns 1:1 with the assembler's segments.
            List<RenderSegment> factorySegs = ProjectGeometry(factoryChain);
            IReadOnlyList<RenderSegment> assemblerSegs = assemblerChain.Segments;

            bool countMismatch = factorySegs.Count != assemblerSegs.Count;

            int n = factorySegs.Count < assemblerSegs.Count ? factorySegs.Count : assemblerSegs.Count;
            for (int i = 0; i < n; i++)
            {
                ParityResult seg = CompareSegment(factorySegs[i], assemblerSegs[i], i, countMismatch);
                if (!seg.IsMatch)
                    return seg;
            }

            // If we got here the overlapping segments matched; a count difference is still a divergence.
            if (countMismatch)
                return ParityResult.Diverge("segment-count", -1,
                    Fmt(factorySegs.Count, assemblerSegs.Count), countMismatch: true);

            return ParityResult.Match;
        }

        /// <summary>
        /// Project a <see cref="PhaseChain"/> into the geometry <see cref="RenderSegment"/>s its phases
        /// emit, UT-ordered. The <see cref="SampleContext"/> carries the phase's resolved frame body so a
        /// conic phase resolves its frame name the same way the assembler did. Pure (no Unity reads).
        /// </summary>
        internal static List<RenderSegment> ProjectGeometry(PhaseChain chain)
        {
            var outSegs = new List<RenderSegment>(chain?.PhaseCount ?? 0);
            if (chain == null)
                return outSegs;
            for (int i = 0; i < chain.PhaseCount; i++)
            {
                TrajectoryPhase phase = chain.Phases[i];
                if (phase == null)
                    continue;
                // The phase's anchor body is the frame the assembler stamped on the segment; pass it as the
                // SampleContext fallback so a conic phase whose anchor/conic body is empty still resolves.
                string frameBody = (phase.Anchor is AnchorFrame.BodyAnchor body) ? body.BodyName : null;
                var ctx = new SampleContext(phase.StartUt, frameBody);
                foreach (RenderSegment seg in phase.Emit(ctx))
                    outSegs.Add(seg);
            }
            return outSegs;
        }

        private static ParityResult CompareSegment(
            RenderSegment factory, RenderSegment assembler, int index, bool countMismatch)
        {
            if (factory.Treatment != assembler.Treatment)
                return ParityResult.Diverge("Treatment", index,
                    Fmt(factory.Treatment, assembler.Treatment), countMismatch);

            if (!BitEquals(factory.StartUT, assembler.StartUT))
                return ParityResult.Diverge("StartUt", index,
                    Fmt(factory.StartUT, assembler.StartUT), countMismatch);

            if (!BitEquals(factory.EndUT, assembler.EndUT))
                return ParityResult.Diverge("EndUt", index,
                    Fmt(factory.EndUT, assembler.EndUT), countMismatch);

            if (!string.Equals(factory.FrameBodyName, assembler.FrameBodyName, System.StringComparison.Ordinal))
                return ParityResult.Diverge("FrameBodyName", index,
                    Fmt(factory.FrameBodyName ?? "(null)", assembler.FrameBodyName ?? "(null)"), countMismatch);

            // Conic-element payload: HasConic must match, and when present every Kepler element.
            if (factory.Payload.HasConic != assembler.Payload.HasConic)
                return ParityResult.Diverge("HasConic", index,
                    Fmt(factory.Payload.HasConic, assembler.Payload.HasConic), countMismatch);

            if (factory.Payload.HasConic)
            {
                ParityResult conic = CompareConic(
                    factory.Payload.Conic, assembler.Payload.Conic, index, countMismatch);
                if (!conic.IsMatch)
                    return conic;
            }

            return ParityResult.Match;
        }

        private static ParityResult CompareConic(
            OrbitSegment factory, OrbitSegment assembler, int index, bool countMismatch)
        {
            if (!BitEquals(factory.startUT, assembler.startUT))
                return ParityResult.Diverge("conic.startUT", index, Fmt(factory.startUT, assembler.startUT), countMismatch);
            if (!BitEquals(factory.endUT, assembler.endUT))
                return ParityResult.Diverge("conic.endUT", index, Fmt(factory.endUT, assembler.endUT), countMismatch);
            if (!BitEquals(factory.inclination, assembler.inclination))
                return ParityResult.Diverge("conic.inclination", index, Fmt(factory.inclination, assembler.inclination), countMismatch);
            if (!BitEquals(factory.eccentricity, assembler.eccentricity))
                return ParityResult.Diverge("conic.eccentricity", index, Fmt(factory.eccentricity, assembler.eccentricity), countMismatch);
            if (!BitEquals(factory.semiMajorAxis, assembler.semiMajorAxis))
                return ParityResult.Diverge("conic.semiMajorAxis", index, Fmt(factory.semiMajorAxis, assembler.semiMajorAxis), countMismatch);
            if (!BitEquals(factory.longitudeOfAscendingNode, assembler.longitudeOfAscendingNode))
                return ParityResult.Diverge("conic.longitudeOfAscendingNode", index, Fmt(factory.longitudeOfAscendingNode, assembler.longitudeOfAscendingNode), countMismatch);
            if (!BitEquals(factory.argumentOfPeriapsis, assembler.argumentOfPeriapsis))
                return ParityResult.Diverge("conic.argumentOfPeriapsis", index, Fmt(factory.argumentOfPeriapsis, assembler.argumentOfPeriapsis), countMismatch);
            if (!BitEquals(factory.meanAnomalyAtEpoch, assembler.meanAnomalyAtEpoch))
                return ParityResult.Diverge("conic.meanAnomalyAtEpoch", index, Fmt(factory.meanAnomalyAtEpoch, assembler.meanAnomalyAtEpoch), countMismatch);
            if (!BitEquals(factory.epoch, assembler.epoch))
                return ParityResult.Diverge("conic.epoch", index, Fmt(factory.epoch, assembler.epoch), countMismatch);
            if (!string.Equals(factory.bodyName, assembler.bodyName, System.StringComparison.Ordinal))
                return ParityResult.Diverge("conic.bodyName", index, Fmt(factory.bodyName ?? "(null)", assembler.bodyName ?? "(null)"), countMismatch);
            // isPredicted is part of the recorded conic shape (it gates below-surface trimming), so it is a
            // geometry-relevant payload field; keep it in the byte-parity set.
            if (factory.isPredicted != assembler.isPredicted)
                return ParityResult.Diverge("conic.isPredicted", index, Fmt(factory.isPredicted, assembler.isPredicted), countMismatch);
            return ParityResult.Match;
        }

        // Exact bitwise double compare (byte-parity, not tolerance). NaN == NaN here (a recorded NaN
        // element must round-trip as NaN), unlike the IEEE == operator.
        private static bool BitEquals(double a, double b)
            => System.BitConverter.DoubleToInt64Bits(a) == System.BitConverter.DoubleToInt64Bits(b);

        private static string Fmt(double factory, double assembler)
            => string.Format(CultureInfo.InvariantCulture, "factory={0:R} assembler={1:R}", factory, assembler);

        private static string Fmt(int factory, int assembler)
            => string.Format(CultureInfo.InvariantCulture, "factory={0} assembler={1}", factory, assembler);

        private static string Fmt(bool factory, bool assembler)
            => string.Format(CultureInfo.InvariantCulture, "factory={0} assembler={1}", factory, assembler);

        private static string Fmt(string factory, string assembler)
            => string.Format(CultureInfo.InvariantCulture, "factory={0} assembler={1}", factory, assembler);

        private static string Fmt(Treatment factory, Treatment assembler)
            => string.Format(CultureInfo.InvariantCulture, "factory={0} assembler={1}", factory, assembler);
    }
}
