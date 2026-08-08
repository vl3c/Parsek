using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision cells for the StartLoopPlayback seam verb (the player-workflow
    /// lane's warp-to-next-launch promotion): the ordered pre-flight refusal
    /// classification, the finite-relaunch-UT guard, and the terminal payload's
    /// lead-aware UT pair. The Unity applier is a thin walk down the production path
    /// (TryBuildLoopUnitForSelection -> ComputeNextRelaunchUT -> FastForwardToEventUT)
    /// and is exercised live by the player-loop lane's flights.
    ///
    /// The COMPLETION decision is deliberately not re-tested here: the verb reuses
    /// TestCommandTimeJump.DecideJumpCompletion verbatim (one-sided reached-latch +
    /// settle window + budget catch-all), which TestCommandTimeJumpTests already
    /// covers. A second copy of those cells would only assert that the reuse is a
    /// reuse.
    /// </summary>
    public class TestCommandStartLoopPlaybackTests
    {
        [Theory]
        // Ordering is the contract: a missing arg must not read as "unknown tree".
        [InlineData(null, false, false, "tree-arg-missing")]
        [InlineData("", true, true, "tree-arg-missing")]
        [InlineData("tree-1", false, false, "unknown-tree")]
        [InlineData("tree-1", true, false, "loop-not-armed")]
        [InlineData("tree-1", true, true, null)]
        public void Rejection_classification_is_ordered(
            string treeArg, bool missionFound, bool loopArmed, string want)
        {
            Assert.Equal(want,
                TestCommandStartLoopPlayback.ClassifyRejection(treeArg, missionFound, loopArmed));
        }

        [Theory]
        [InlineData(0.0, true)]
        [InlineData(24_300_000.0, true)]
        [InlineData(-5.0, true)]              // finiteness only; the forward-jump gate is separate
        [InlineData(double.NaN, false)]       // ComputeNextRelaunchUT's "not aligned" sentinel
        [InlineData(double.PositiveInfinity, false)]
        [InlineData(double.NegativeInfinity, false)]
        public void Relaunch_ut_must_be_finite(double relaunchUt, bool want)
        {
            Assert.Equal(want, TestCommandStartLoopPlayback.IsUsableRelaunchUt(relaunchUt));
        }

        [Fact]
        public void Complete_payload_carries_both_uts_invariant_culture()
        {
            // reachedUt sits BELOW relaunchUt by the 15 s launch lead: the jump lands
            // the player just before lift-off, so a consumer must expect the gap
            // rather than read it as drift.
            var payload = TestCommandStartLoopPlayback.BuildCompletePayload(
                24_300_000.5, 24_299_985.5, "Duna Direct", "tree-7");

            Assert.Equal(4, payload.Count);
            Assert.Equal("relaunchUt", payload[0].Key);
            Assert.Equal("24300000.5", payload[0].Value);
            Assert.Equal("reachedUt", payload[1].Key);
            Assert.Equal("24299985.5", payload[1].Value);
            Assert.Equal("mission", payload[2].Key);
            Assert.Equal("Duna Direct", payload[2].Value);
            Assert.Equal("tree", payload[3].Key);
            Assert.Equal("tree-7", payload[3].Value);
        }

        [Fact]
        public void Complete_payload_null_identity_is_empty_not_null()
        {
            var payload = TestCommandStartLoopPlayback.BuildCompletePayload(1.0, 1.0, null, null);
            Assert.Equal(string.Empty, payload[2].Value);
            Assert.Equal(string.Empty, payload[3].Value);
        }
    }
}
