using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins <see cref="ReputationSeedMembership"/>: the row set the inside-seed flag
    /// applies to, the origin arm that decides the stamp, and the career-start arm that
    /// flips a stamp back out.
    ///
    /// <para>
    /// The three cells nobody may delete: the row set covers every type
    /// <c>ReputationModule.ProcessAction</c> moves reputation for (a missing one is a row
    /// that keeps double-applying), it EXCLUDES ReputationInitial (a seed inside itself is
    /// a zeroed career), and the two origin arms are never both true for one origin (the
    /// producer would undo its own stamp).
    /// </para>
    /// </summary>
    public class ReputationSeedMembershipTests
    {
        // ---- the row set ----------------------------------------------------

        // The set is exactly ReputationModule.ProcessAction's rep-moving switch arms.
        // Pinned per member so a new GameActionType forces an answer rather than
        // defaulting into "never inside the seed" unnoticed.
        [Fact]
        public void EveryGameActionType_HasAnAnswer()
        {
            var expected = new Dictionary<GameActionType, bool>
            {
                { GameActionType.ReputationEarning, true },
                { GameActionType.ReputationPenalty, true },
                { GameActionType.MilestoneAchievement, true },
                { GameActionType.ContractComplete, true },
                { GameActionType.ContractFail, true },
                { GameActionType.ContractCancel, true },
                { GameActionType.StrategyActivate, true },
            };

            foreach (GameActionType type in
                System.Enum.GetValues(typeof(GameActionType)))
            {
                bool want;
                if (!expected.TryGetValue(type, out want))
                    want = false;

                Assert.Equal(want, ReputationSeedMembership.IsReputationAffectingRow(type));
            }
        }

        // The seed row is the value being reasoned about; it cannot be inside itself, and
        // suppressing it would zero the career's whole reputation baseline.
        [Fact]
        public void ReputationInitial_IsNotAReputationAffectingRow()
        {
            Assert.False(ReputationSeedMembership.IsReputationAffectingRow(
                GameActionType.ReputationInitial));
            Assert.False(ReputationSeedMembership.ShouldSkipAsInsideSeed(
                GameActionType.ReputationInitial, true));
        }

        // The module-side gate is an AND: the flag alone never suppresses a row that
        // moves no reputation, and a rep row without the flag always applies.
        [Fact]
        public void ShouldSkipAsInsideSeed_NeedsBothTheFlagAndTheRowType()
        {
            Assert.True(ReputationSeedMembership.ShouldSkipAsInsideSeed(
                GameActionType.MilestoneAchievement, true));
            Assert.False(ReputationSeedMembership.ShouldSkipAsInsideSeed(
                GameActionType.MilestoneAchievement, false));
            Assert.False(ReputationSeedMembership.ShouldSkipAsInsideSeed(
                GameActionType.FundsEarning, true));
        }

        // ---- the inside-seed rule ------------------------------------------


        // A seed already on the books when this commit started was fixed before this
        // flight was filed, so nothing this commit files can be inside it.
        [Fact]
        public void IsInsideReputationSeed_PreExistingSeed_IsOutside()
        {
            Assert.False(ReputationSeedMembership.IsInsideReputationSeed(
                ReputationSeedOrigin.PreExisting));
        }

        // The live pool this commit read had already taken the hit.
        [Fact]
        public void IsInsideReputationSeed_LivePoolSeedThisCommit_IsInside()
        {
            Assert.True(ReputationSeedMembership.IsInsideReputationSeed(
                ReputationSeedOrigin.CreatedThisCommitFromLivePool));
        }

        // A career-start baseline predates the flight entirely.
        [Fact]
        public void IsInsideReputationSeed_CareerBaselineSeedThisCommit_IsOutside()
        {
            Assert.False(ReputationSeedMembership.IsInsideReputationSeed(
                ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline));
        }

        // The refusal fallback DECLINES the live pool and seeds career start (0), so it
        // is grouped with the baseline branch, not with the live-pool one. Grouping it
        // the other way would drop the penalty from a timeline seeded at career start -
        // the unmodeled-penalty failure the rule exists to prevent.
        [Fact]
        public void IsInsideReputationSeed_RefusalFallbackSeedThisCommit_IsOutside()
        {
            Assert.False(ReputationSeedMembership.IsInsideReputationSeed(
                ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback));
        }

        // The deferred case: no seed yet, so the seed will be captured LATER, off a live
        // pool this death has already lowered.
        [Fact]
        public void IsInsideReputationSeed_SeedNotYetCaptured_IsInside()
        {
            Assert.True(ReputationSeedMembership.IsInsideReputationSeed(
                ReputationSeedOrigin.NotYetCaptured));
        }

        // The rule is a pure function of the ORIGIN and of nothing else. Stated as one
        // cell so a future input (a UT, a clock, a live pool read) has to break it.
        [Fact]
        public void IsInsideReputationSeed_CoversEveryOrigin()
        {
            var expected = new Dictionary<ReputationSeedOrigin, bool>
            {
                { ReputationSeedOrigin.NotYetCaptured, true },
                { ReputationSeedOrigin.PreExisting, false },
                { ReputationSeedOrigin.CreatedThisCommitFromLivePool, true },
                { ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline, false },
                { ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback, false },
            };

            foreach (ReputationSeedOrigin origin in
                System.Enum.GetValues(typeof(ReputationSeedOrigin)))
            {
                Assert.True(expected.ContainsKey(origin),
                    "new ReputationSeedOrigin '" + origin + "' has no inside-seed answer");
                Assert.Equal(expected[origin],
                    ReputationSeedMembership.IsInsideReputationSeed(origin));
            }
        }

        // ---- the re-stamp rule ---------------------------------------------

        // The refusal fallback seeds career start (0), which contains no death, so every
        // row stamped inside on the deferred expectation has to come back out. This is
        // the CL-2-pod-impact-ledger sequence: stamp inside at the commit, seed from the
        // refusal branch 4 ms later.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_RefusalFallback_Flips()
        {
            Assert.True(ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback));
        }

        // Same for the career-start baseline: its value predates the flight entirely.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_CareerBaseline_Flips()
        {
            Assert.True(ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline));
        }

        // THE MIRROR, and the case that must never flip: a live-pool seed already
        // contains every death filed before it. Flipping those rows subtracts the same
        // penalty twice.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_LivePool_DoesNotFlip()
        {
            Assert.False(ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.CreatedThisCommitFromLivePool));
        }

        // Neither of these creates a seed in the call being answered for, so neither
        // invalidates anything: PreExisting found one already there, NotYetCaptured made
        // none. NotYetCaptured is in particular the origin that WROTE the stamps being
        // repaired - answering true here would flip them the moment they were written.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_NoSeedCreated_DoesNotFlip()
        {
            Assert.False(ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.PreExisting));
            Assert.False(ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(
                ReputationSeedOrigin.NotYetCaptured));
        }

        // Exhaustive over the enum, exactly like the inside-seed cell above, so a new
        // origin must state which side of the re-stamp it falls on.
        [Fact]
        public void CareerStartSeedInvalidatesInsideStamps_CoversEveryOrigin()
        {
            var expected = new Dictionary<ReputationSeedOrigin, bool>
            {
                { ReputationSeedOrigin.NotYetCaptured, false },
                { ReputationSeedOrigin.PreExisting, false },
                { ReputationSeedOrigin.CreatedThisCommitFromLivePool, false },
                { ReputationSeedOrigin.CreatedThisCommitFromCareerBaseline, true },
                { ReputationSeedOrigin.CreatedThisCommitFromRefusalFallback, true },
            };

            foreach (ReputationSeedOrigin origin in
                System.Enum.GetValues(typeof(ReputationSeedOrigin)))
            {
                Assert.True(expected.ContainsKey(origin),
                    "new ReputationSeedOrigin '" + origin + "' has no re-stamp answer");
                Assert.Equal(expected[origin],
                    ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(origin));
            }
        }

        // The two rules answer the same enum and must not both claim a row: a row is
        // stamped inside on an origin, and re-stamped outside on an origin - never both
        // for the same origin, or the producer would undo its own stamp.
        [Fact]
        public void InsideSeedAndReStamp_NeverBothTrueForOneOrigin()
        {
            foreach (ReputationSeedOrigin origin in
                System.Enum.GetValues(typeof(ReputationSeedOrigin)))
            {
                Assert.False(
                    ReputationSeedMembership.IsInsideReputationSeed(origin)
                    && ReputationSeedMembership.CareerStartSeedInvalidatesInsideStamps(origin),
                    "origin '" + origin + "' both stamps rows inside and invalidates them");
            }
        }
    }
}
