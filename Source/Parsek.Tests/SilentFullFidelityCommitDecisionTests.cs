using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the pure routing predicate
    /// <see cref="ParsekScenario.ShouldSilentFullFidelityCommit"/> that decides
    /// whether an outside-Flight auto-commit routes through the dialog's
    /// full-fidelity <see cref="MergeDialog.MergeCommit"/> (spawn-at-end
    /// preserved) or the lightweight ghost-only commit. See
    /// docs/dev/plans/silent-full-fidelity-autocommit.md.
    /// </summary>
    public class SilentFullFidelityCommitDecisionTests
    {
        [Theory]
        [InlineData(GameScenes.SPACECENTER)]
        [InlineData(GameScenes.TRACKSTATION)]
        public void Qualifies_AutoMerge_Finalized_NoReFly_RealScene_ReturnsTrue(GameScenes scene)
        {
            Assert.True(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: true,
                pendingState: PendingTreeState.Finalized,
                reFlyActive: false,
                loadedScene: scene));
        }

        [Fact]
        public void Disqualifies_AutoMergeOff_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: false,
                pendingState: PendingTreeState.Finalized,
                reFlyActive: false,
                loadedScene: GameScenes.SPACECENTER));
        }

        [Fact]
        public void Disqualifies_ReFlyActive_ReturnsFalse()
        {
            // A silent MergeCommit would supersede the re-fly (irreversible
            // timeline mutation) — must stay dialog/journal-gated, so it falls
            // to the ghost-only path instead.
            Assert.False(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: true,
                pendingState: PendingTreeState.Finalized,
                reFlyActive: true,
                loadedScene: GameScenes.SPACECENTER));
        }

        [Theory]
        [InlineData(PendingTreeState.Limbo)]
        [InlineData(PendingTreeState.LimboVesselSwitch)]
        public void Disqualifies_NonFinalizedResumeStash_ReturnsFalse(PendingTreeState state)
        {
            // Limbo stashes are resume-flow trees, never heavier-committed.
            Assert.False(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: true,
                pendingState: state,
                reFlyActive: false,
                loadedScene: GameScenes.SPACECENTER));
        }

        [Fact]
        public void Disqualifies_MainMenu_ReturnsFalse()
        {
            // MAINMENU = game unloading; spawn-at-end never runs and a quicksave
            // during unload is unsafe, so keep the lightweight ghost-only commit.
            Assert.False(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: true,
                pendingState: PendingTreeState.Finalized,
                reFlyActive: false,
                loadedScene: GameScenes.MAINMENU));
        }
    }
}
