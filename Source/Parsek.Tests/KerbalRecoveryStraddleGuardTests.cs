using System.Collections.Generic;
using System.Linq;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless pins for <see cref="KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths"/>,
    /// the precondition guard added after RF-12W's first flight.
    ///
    /// <para><b>What it is guarding against, and why the cell needed it twice.</b>
    /// <c>KerbalRecoveryOnSupersede</c> asserts section 7.16: every eligible kerbal-death action
    /// in the supersede subtree is tombstoned by the merge, and each of those kerbals is
    /// no longer Dead in the roster. Subtree membership is NOT the whole eligibility
    /// test - <c>CommitTombstones</c> applies a second screen, <c>PreRewindTombstoneGuard</c>,
    /// which KEEPS an in-subtree action whose UT precedes the rewind cutoff because that
    /// part of the timeline is the part the merge keeps. A crewed flight rewound after
    /// launch always produces such a row (the crew boards at launch, before any rewind
    /// point taken later), so the cell's expected set was a strict SUPERSET of what the
    /// merge can write and it red against documented, headlessly-pinned product
    /// behaviour. Measured on RF-12W 2026-09-09: <c>PreRewindTombstoneGuard: keep
    /// action=act_482bbdec... ut=29.94 cutoffUT=131.54</c>, then the cell failing on
    /// "must be tombstoned after merge". That is the SECOND unmet precondition this cell
    /// has asserted against; the first (an unflown provisional) got the
    /// <c>ValidateSupersedeTarget</c> guard on 2026-08-04.</para>
    ///
    /// <para>The partition is pure so the decision is pinned here rather than only in a
    /// flight, exactly as PR #1661 pinned <c>MergeInterruptionRecoveryTest</c>'s
    /// <c>TryBuildUnconcludedReFlySkip</c>. The guard deliberately does NOT re-derive the
    /// <c>UT &lt; cutoff</c> comparison: the cell calls the product predicate and passes
    /// its answer in, so a private second convention cannot drift from the merge.</para>
    /// </summary>
    public class KerbalRecoveryStraddleGuardTests
    {
        private static KerbalRecoveryOnSupersedeTest.StraddlingDeathRow Row(
            string actionId, string kerbal, bool kept)
            => new KerbalRecoveryOnSupersedeTest.StraddlingDeathRow(actionId, kerbal, kept);

        private const double Cutoff = 131.54;

        [Fact]
        public void AllRowsPostRewind_IsTheOrdinaryCase_AndEverythingIsInScope()
        {
            var rows = new List<KerbalRecoveryOnSupersedeTest.StraddlingDeathRow>
            {
                Row("act_a", "Bill Kerman", kept: false),
                Row("act_b", "Bob Kerman", kept: false),
            };

            bool ok = KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths(
                rows, Cutoff, out var actions, out var kerbals, out string skip);

            Assert.True(ok);
            Assert.Null(skip);
            Assert.Equal(new[] { "act_a", "act_b" }, actions.OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "Bill Kerman", "Bob Kerman" }, kerbals.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void AKeptRowIsDroppedFromTheActionSet_ButTheRunContinues()
        {
            // The CL-3-shaped mix: one kerbal boarded before the rewind, another after.
            // The merge retires only the second, and only the second can be recovered -
            // but the cell still has a subject, so it must RUN rather than skip.
            var rows = new List<KerbalRecoveryOnSupersedeTest.StraddlingDeathRow>
            {
                Row("act_pre", "Bill Kerman", kept: true),
                Row("act_post", "Valentina Kerman", kept: false),
            };

            bool ok = KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths(
                rows, Cutoff, out var actions, out var kerbals, out string skip);

            Assert.True(ok);
            Assert.Null(skip);
            Assert.Equal(new[] { "act_post" }, actions.ToArray());
            Assert.Equal(new[] { "Valentina Kerman" }, kerbals.ToArray());
            Assert.DoesNotContain("act_pre", actions);
            Assert.DoesNotContain("Bill Kerman", kerbals);
        }

        [Fact]
        public void AKerbalWithEvenOneKeptRowIsNotRecoverable_ThoughTheirOtherRowsStillAre()
        {
            // THE CELL THAT ENCODES THE ASYMMETRY. The two outputs answer different
            // questions: `actions` is per-ACTION (a post-cutoff row IS retired, so
            // asserting on it is fair), while `kerbals` is per-KERBAL - a kept row goes on
            // holding a permanent Dead reservation no matter how many of that kerbal's
            // other rows are retired. RF-12W's log says it verbatim: `Recomputed after
            // tombstones: 2 reservations remain (permanent=2 temporary=0)`.
            var rows = new List<KerbalRecoveryOnSupersedeTest.StraddlingDeathRow>
            {
                Row("act_pre", "Bill Kerman", kept: true),
                Row("act_post", "Bill Kerman", kept: false),
                Row("act_other", "Valentina Kerman", kept: false),
            };

            bool ok = KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths(
                rows, Cutoff, out var actions, out var kerbals, out string skip);

            Assert.True(ok);
            Assert.Contains("act_post", actions);
            Assert.DoesNotContain("act_pre", actions);
            Assert.DoesNotContain("Bill Kerman", kerbals);
            Assert.Equal(new[] { "Valentina Kerman" }, kerbals.ToArray());
        }

        [Fact]
        public void EveryRowStraddling_SkipsAndNamesTheKeptIdsTheKerbalsAndTheCutoff()
        {
            // THE RF-12W SHAPE, and the reason the guard exists. Both kerbals boarded at
            // launch, so both rows precede the cutoff, so the recovery invariant has no
            // subject at all. Skipping is the house rule (a FLIGHT test that cannot
            // self-set-up names the required context) and it is strictly better than a
            // half-assertion wearing a whole test's name.
            var rows = new List<KerbalRecoveryOnSupersedeTest.StraddlingDeathRow>
            {
                Row("act_482bbdec", "Bill Kerman", kept: true),
                Row("act_1c9f0a2b", "Bob Kerman", kept: true),
            };

            bool ok = KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths(
                rows, Cutoff, out var actions, out var kerbals, out string skip);

            Assert.False(ok);
            Assert.Empty(actions);
            Assert.Empty(kerbals);
            Assert.NotNull(skip);
            // The skip must be DIAGNOSTIC, not just a refusal: a reader has to be able to
            // tell it from an empty subtree without opening the log.
            Assert.Contains("STRADDLES", skip);
            Assert.Contains("act_482bbdec", skip);
            Assert.Contains("act_1c9f0a2b", skip);
            Assert.Contains("Bill Kerman", skip);
            Assert.Contains("Bob Kerman", skip);
            Assert.Contains("131.54", skip);
            Assert.Contains("PreRewindTombstoneGuard", skip);
        }

        [Fact]
        public void AnEmptySubtreeKeepsItsOwnDistinctSkipText()
        {
            // The pre-existing refusal, preserved verbatim in meaning: "there were no
            // death rows" and "every death row straddles" are different diagnoses and a
            // reader must not have to guess which one fired.
            bool ok = KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths(
                new List<KerbalRecoveryOnSupersedeTest.StraddlingDeathRow>(),
                Cutoff, out var actions, out var kerbals, out string skip);

            Assert.False(ok);
            Assert.Empty(actions);
            Assert.Empty(kerbals);
            Assert.Contains("No kerbal-death actions in supersede subtree", skip);
            Assert.DoesNotContain("STRADDLES", skip);
        }

        [Fact]
        public void ANullRowListIsTheEmptyCase_NotAThrow()
        {
            bool ok = KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths(
                null, Cutoff, out var actions, out var kerbals, out string skip);

            Assert.False(ok);
            Assert.Empty(actions);
            Assert.Empty(kerbals);
            Assert.Contains("No kerbal-death actions in supersede subtree", skip);
        }

        [Fact]
        public void RowsWithNoIdOrNoNameNeitherThrowNorCountAsSubjects()
        {
            // The ledger can carry a row with a blank ActionId or KerbalName (the old
            // gather guarded both with IsNullOrEmpty). A blank name must not become a
            // phantom recoverable kerbal, and a blank id must not become a phantom
            // tombstone subject that the assertion loop then cannot find.
            var rows = new List<KerbalRecoveryOnSupersedeTest.StraddlingDeathRow>
            {
                Row("", "", kept: false),
                Row(null, null, kept: true),
                Row("act_real", "Jebediah Kerman", kept: false),
            };

            bool ok = KerbalRecoveryOnSupersedeTest.TryPartitionSubtreeDeaths(
                rows, Cutoff, out var actions, out var kerbals, out string skip);

            Assert.True(ok);
            Assert.Equal(new[] { "act_real" }, actions.ToArray());
            Assert.Equal(new[] { "Jebediah Kerman" }, kerbals.ToArray());
            Assert.Null(skip);
        }
    }
}
