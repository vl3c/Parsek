using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for the Phase 6 §7.11 priority resolver
    /// (<see cref="AnchorPriority"/>). The §7.11 ordering is hard-coded;
    /// the test pins each rank value so a silent reorder fails fast.
    /// </summary>
    public class AnchorPriorityTests
    {
        [Fact]
        public void RankOf_LiveSeparation_IsTopPriority()
        {
            // What makes it fail: a §7.11 reorder that demoted LiveSeparation
            // would silently let a lower-fidelity DAG-propagated ε overwrite
            // the live-vessel-anchored ε every Phase-2 session.
            Assert.Equal(1, AnchorPriority.RankOf(AnchorSource.LiveSeparation));
        }

        [Fact]
        public void RankOf_RelativeBoundary_OutranksOrbitalCheckpoint()
        {
            // §7.11: real persistent-vessel reference (RelativeBoundary) wins
            // over analytical-orbit reference (OrbitalCheckpoint).
            int relBound = AnchorPriority.RankOf(AnchorSource.RelativeBoundary);
            int orbCkpt = AnchorPriority.RankOf(AnchorSource.OrbitalCheckpoint);
            Assert.True(relBound < orbCkpt,
                $"RelativeBoundary rank {relBound} should be < OrbitalCheckpoint rank {orbCkpt}");
        }

        [Fact]
        public void RankOf_DockOrMerge_OutranksBubbleEntry()
        {
            // DAG-propagated reference (DockOrMerge) wins over bubble-entry
            // (no live reference).
            int dock = AnchorPriority.RankOf(AnchorSource.DockOrMerge);
            int bubble = AnchorPriority.RankOf(AnchorSource.BubbleEntry);
            Assert.True(dock < bubble,
                $"DockOrMerge rank {dock} should be < BubbleEntry rank {bubble}");
        }

        [Fact]
        public void RankOf_Loop_AndSurfaceContinuous_AreSameAsRelativeBoundary()
        {
            // §7.11: real persistent references all share rank 2.
            int relBound = AnchorPriority.RankOf(AnchorSource.RelativeBoundary);
            Assert.Equal(relBound, AnchorPriority.RankOf(AnchorSource.Loop));
            Assert.Equal(relBound, AnchorPriority.RankOf(AnchorSource.SurfaceContinuous));
        }

        [Fact]
        public void ShouldReplace_HigherPriorityWins()
        {
            // What makes it fail: any swap of the comparator's order would
            // let a lower-priority candidate clobber a higher-priority
            // existing entry — the §7.11 contract is one-way.
            Assert.True(AnchorPriority.ShouldReplace(
                existing: AnchorSource.DockOrMerge,
                candidate: AnchorSource.LiveSeparation));
            Assert.False(AnchorPriority.ShouldReplace(
                existing: AnchorSource.LiveSeparation,
                candidate: AnchorSource.DockOrMerge));
        }

        [Fact]
        public void ShouldReplace_TieGoesToLowerEnumValue()
        {
            // Equal-rank case: RelativeBoundary (enum 2) and Loop (enum 9)
            // both rank 2; the lower enum wins (RelativeBoundary). HR-3:
            // ties must be deterministic, never random.
            Assert.False(AnchorPriority.ShouldReplace(
                existing: AnchorSource.RelativeBoundary,
                candidate: AnchorSource.Loop));
            Assert.True(AnchorPriority.ShouldReplace(
                existing: AnchorSource.Loop,
                candidate: AnchorSource.RelativeBoundary));
        }

        [Fact]
        public void ShouldReplace_SameSource_ReturnsFalse()
        {
            // Idempotent: the same source can't displace itself.
            Assert.False(AnchorPriority.ShouldReplace(
                existing: AnchorSource.LiveSeparation,
                candidate: AnchorSource.LiveSeparation));
            Assert.False(AnchorPriority.ShouldReplace(
                existing: AnchorSource.DockOrMerge,
                candidate: AnchorSource.DockOrMerge));
        }
    }
}
