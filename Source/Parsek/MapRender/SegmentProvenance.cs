namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §5.1: the render-view PROVENANCE of a phase's geometry — where the geometry
    /// came from. Collapses the overloaded <c>OrbitSegment.isPredicted</c> (re-aim synthetic = false,
    /// recorded-extrapolated tail = true) into one explicit token.
    ///
    /// <para>This is a NEW, additive enum. It is NOT wired into the live pipeline in Phase 1 and the
    /// persisted <c>OrbitSegment</c> struct is unchanged (design §13). Provenance is stamped
    /// <b>by the producer</b> that emits the phase at emit time, never re-derived downstream by
    /// reference equality (the brittle <c>ReferenceEquals(effective, recorded)</c> the model replaces).</para>
    ///
    /// <para><b>Immutability contract (design §7 / §13):</b> <see cref="Synthesized"/> is a derived,
    /// IN-MEMORY-ONLY flag and must never reach disk. The two write epochs stay distinct:
    /// finalize-time-predicted (<see cref="FinalizedPredicted"/>, written once then immutable) vs
    /// playback-time-synthesized (<see cref="Synthesized"/>, never written).</para>
    /// </summary>
    internal enum SegmentProvenance
    {
        /// <summary>Default / unset.</summary>
        Unknown = 0,

        /// <summary>Exact recorded geometry, immutable.</summary>
        Recorded = 1,

        /// <summary>Predicted-tail written ONCE at scene exit, then immutable.</summary>
        FinalizedPredicted = 2,

        /// <summary>Re-aim / re-time / re-rotate output. IN-MEMORY ONLY, never persisted.</summary>
        Synthesized = 3,

        /// <summary>A producer was unsupported, so exact recorded replay was chosen instead.</summary>
        FaithfulFallback = 4,
    }

    /// <summary>
    /// Pure token helpers for <see cref="SegmentProvenance"/> — grep-stable lowercase tokens for the
    /// (Phase 8) tracer lines and a single decision the rest of the model leans on: only
    /// <see cref="SegmentProvenance.Synthesized"/> geometry is in-memory-only and must never be
    /// persisted (design §13).
    /// </summary>
    internal static class SegmentProvenanceTokens
    {
        /// <summary>Grep-stable lowercase token for trace lines.</summary>
        internal static string ToToken(SegmentProvenance provenance)
        {
            switch (provenance)
            {
                case SegmentProvenance.Recorded: return "recorded";
                case SegmentProvenance.FinalizedPredicted: return "finalized-predicted";
                case SegmentProvenance.Synthesized: return "synthesized";
                case SegmentProvenance.FaithfulFallback: return "faithful-fallback";
                default: return "unknown";
            }
        }

        /// <summary>
        /// True iff the geometry is in-memory-only and must NEVER be serialized (design §13). Exactly
        /// <see cref="SegmentProvenance.Synthesized"/> today; a single chokepoint so a future
        /// "no synthesized phase reaches disk" assertion can route through one predicate.
        /// </summary>
        internal static bool IsInMemoryOnly(SegmentProvenance provenance)
            => provenance == SegmentProvenance.Synthesized;

        /// <summary>
        /// True iff the geometry replays a recorded source verbatim (no re-aim transform):
        /// <see cref="SegmentProvenance.Recorded"/> or <see cref="SegmentProvenance.FaithfulFallback"/>.
        /// The parity oracle diffs these in FAITHFUL mode (rendered == recorded); the others diff in
        /// SYNTHESIZED mode (rendered == intended arc). See design §14.
        /// </summary>
        internal static bool IsFaithfulReplay(SegmentProvenance provenance)
            => provenance == SegmentProvenance.Recorded
               || provenance == SegmentProvenance.FaithfulFallback;
    }
}
