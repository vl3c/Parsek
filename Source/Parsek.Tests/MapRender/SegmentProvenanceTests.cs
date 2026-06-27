using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for <see cref="SegmentProvenance"/> + <see cref="SegmentProvenanceTokens"/>
    /// (design §5.1). The provenance enum collapses the overloaded <c>OrbitSegment.isPredicted</c>;
    /// the load-bearing decisions are the grep-stable token (trace lines) and the two predicates the
    /// rest of the model leans on: <c>IsInMemoryOnly</c> (the §13 "never reach disk" gate) and
    /// <c>IsFaithfulReplay</c> (the §14 faithful-vs-synthesized oracle-mode split).
    ///
    /// Each assertion states the bug it catches: a wrong token would mis-grep a trace line; a wrong
    /// <c>IsInMemoryOnly</c> would let a synthesized arc be persisted (the immutability HARD RULE
    /// violation); a wrong <c>IsFaithfulReplay</c> would diff a faithful member against the intended
    /// arc instead of the recorded source.
    /// </summary>
    public class SegmentProvenanceTests
    {
        // SegmentProvenance is internal, so a public [Theory] cannot take it as a parameter (CS0051).
        // Pass the underlying int (a compile-time constant; the enum is visible to the test assembly via
        // InternalsVisibleTo) and cast back inside - the same idiom GhostRenderChainTests uses for the
        // internal Coverage enum.
        [Theory]
        [InlineData((int)SegmentProvenance.Recorded, "recorded")]
        [InlineData((int)SegmentProvenance.FinalizedPredicted, "finalized-predicted")]
        [InlineData((int)SegmentProvenance.Synthesized, "synthesized")]
        [InlineData((int)SegmentProvenance.FaithfulFallback, "faithful-fallback")]
        [InlineData((int)SegmentProvenance.Unknown, "unknown")]
        public void ToToken_IsGrepStable(int provenance, string expected)
        {
            Assert.Equal(expected, SegmentProvenanceTokens.ToToken((SegmentProvenance)provenance));
        }

        [Theory]
        // Only Synthesized is in-memory-only (the §13 never-persist gate). FinalizedPredicted IS
        // persisted (written once then immutable) so it must NOT be flagged in-memory-only.
        [InlineData((int)SegmentProvenance.Synthesized, true)]
        [InlineData((int)SegmentProvenance.Recorded, false)]
        [InlineData((int)SegmentProvenance.FinalizedPredicted, false)]
        [InlineData((int)SegmentProvenance.FaithfulFallback, false)]
        [InlineData((int)SegmentProvenance.Unknown, false)]
        public void IsInMemoryOnly_FlagsExactlySynthesized(int provenance, bool expected)
        {
            Assert.Equal(expected, SegmentProvenanceTokens.IsInMemoryOnly((SegmentProvenance)provenance));
        }

        [Theory]
        // Faithful replay = Recorded OR FaithfulFallback (oracle FAITHFUL mode). Synthesized and
        // FinalizedPredicted are NOT recorded-verbatim replays.
        [InlineData((int)SegmentProvenance.Recorded, true)]
        [InlineData((int)SegmentProvenance.FaithfulFallback, true)]
        [InlineData((int)SegmentProvenance.Synthesized, false)]
        [InlineData((int)SegmentProvenance.FinalizedPredicted, false)]
        [InlineData((int)SegmentProvenance.Unknown, false)]
        public void IsFaithfulReplay_SplitsForOracleMode(int provenance, bool expected)
        {
            Assert.Equal(expected, SegmentProvenanceTokens.IsFaithfulReplay((SegmentProvenance)provenance));
        }
    }
}
