using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Coverage for the pure InvokeRewindToLaunch decision core
    /// (<see cref="TestCommandRewindToLaunch"/>): the two-phase completion decider, the
    /// target-resolution helper, the gate-refusal message shape, and the terminal OK
    /// payload. Fails if a mid-reload poll prematurely terminates, a FAILED load is read as
    /// success or an OK is reported while the deferred post-load adjustment is still in
    /// flight (the two cases the three-part CompleteOk conjunction exists for), the
    /// auto-select guesses among several committed trees, or the gate reason is not
    /// surfaced verbatim.
    ///
    /// <para>Sibling of <see cref="TestCommandInvokeRewindTests"/>, which covers the OTHER
    /// (Re-Fly) mechanism.</para>
    /// </summary>
    public class TestCommandRewindToLaunchTests
    {
        private const double Budget = 300.0;

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- DecideRewindToLaunchCompletion -----

        [Fact]
        public void StillRewinding_WithinBudget_StillWaiting()
        {
            // Mid-straddle: HandleRewindOnLoad has not reached its EndRewind() tail yet.
            Assert.Equal(RewindToLaunchCompletionDecision.StillWaiting,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    5.0, isRewinding: true, sceneIsSpaceCenter: false,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void StillRewinding_AtSpaceCenter_WithinBudget_StillWaiting()
        {
            // Arrived at the destination scene but OnLoad has not cleared the flags: the
            // SPACECENTER half alone is NOT the success.
            Assert.Equal(RewindToLaunchCompletionDecision.StillWaiting,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    5.0, isRewinding: true, sceneIsSpaceCenter: true,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void FlagsCleared_AtSpaceCenter_DeferredDrained_CompleteOk()
        {
            // All three halves: flags cleared, destination scene settled, and the deferred
            // post-load adjustment has drained.
            Assert.Equal(RewindToLaunchCompletionDecision.CompleteOk,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    5.0, isRewinding: false, sceneIsSpaceCenter: true,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void FlagsCleared_AtSpaceCenter_DeferredPending_WithinBudget_StillWaiting()
        {
            // HandleRewindOnLoad arms RewindUTAdjustmentPending +
            // BeginRewindResourceAdjustment IMMEDIATELY BEFORE the EndRewind() that clears
            // IsRewinding, and only ApplyRewindResourceAdjustment (~2s later) sets the
            // adjusted UT and runs the ledger recalc. Reporting OK here would hand the next
            // verb a Space Center still sitting at the pre-rewind future UT.
            Assert.Equal(RewindToLaunchCompletionDecision.StillWaiting,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    5.0, isRewinding: false, sceneIsSpaceCenter: true,
                    deferredAdjustmentPending: true, Budget));
        }

        [Fact]
        public void FlagsCleared_AtSpaceCenter_DeferredPending_BudgetExpired_RewindTimeout()
        {
            // A deferred adjustment that never drains (host destroyed mid-wait) must
            // terminate rather than hold the FIFO head forever: the unconditional budget
            // check sits above the new SPACECENTER StillWaiting branch too.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindTimeout,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget + 10.0, isRewinding: false, sceneIsSpaceCenter: true,
                    deferredAdjustmentPending: true, Budget));
        }

        [Fact]
        public void FlagsCleared_NotAtSpaceCenter_WithinBudget_RewindFailed()
        {
            // RecordingStore.ResetRewindFlags ran on a load-failure path inside
            // ExecuteRewindSaveLoad, which leaves the scene exactly where it was (FLIGHT).
            // The !isRewinding half ALONE would read this as a success - which is precisely
            // why CompleteOk is a conjunction.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindFailed,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    5.0, isRewinding: false, sceneIsSpaceCenter: false,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void StillRewinding_BudgetExpired_RewindTimeout()
        {
            // The budget check is UNCONDITIONAL and sits ABOVE the still-pending straddle
            // (the ordering DecideRewindCompletion was corrected to), so a reload that never
            // completes terminates instead of holding the FIFO head forever.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindTimeout,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget + 10.0, isRewinding: true, sceneIsSpaceCenter: false,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void TimeoutWinsOverStillWaiting_AtBudgetBoundary()
        {
            // Boundary is >= budget, matching DeferralBudget.ShouldTimeout.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindTimeout,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget, isRewinding: true, sceneIsSpaceCenter: false,
                    deferredAdjustmentPending: false, Budget));
            Assert.Equal(RewindToLaunchCompletionDecision.StillWaiting,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget - 0.001, isRewinding: true, sceneIsSpaceCenter: false,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void FlagsCleared_NotAtSpaceCenter_BudgetExpired_RewindTimeout()
        {
            // Past the budget the timeout wins over the fast failure: the ordering puts the
            // unconditional budget check above both remaining branches.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindTimeout,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget + 10.0, isRewinding: false, sceneIsSpaceCenter: false,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void CompleteOk_WinsOverBudgetExpiry()
        {
            // The success conjunction is checked FIRST, so a slow-but-successful reload
            // reports OK rather than a spurious timeout.
            Assert.Equal(RewindToLaunchCompletionDecision.CompleteOk,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget + 10.0, isRewinding: false, sceneIsSpaceCenter: true,
                    deferredAdjustmentPending: false, Budget));
        }

        [Fact]
        public void DeferredPending_NeverCompletesOk_WhateverTheOtherHalvesSay()
        {
            // The third half is a hard veto on CompleteOk: no combination of the other two
            // readings may report success while the deferred adjustment is still in flight.
            foreach (bool rewinding in new[] { false, true })
                foreach (bool spaceCenter in new[] { false, true })
                    Assert.NotEqual(RewindToLaunchCompletionDecision.CompleteOk,
                        TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                            5.0, rewinding, spaceCenter,
                            deferredAdjustmentPending: true, Budget));
        }

        // ----- ResolveTarget -----

        [Fact]
        public void ResolveTarget_ExplicitId_Found_Selected()
        {
            var t = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_a", "tree_b" }, "tree_b");
            Assert.Equal(RewindToLaunchTargetOutcome.Selected, t.Outcome);
            Assert.Equal("tree_b", t.TreeId);
            Assert.Null(t.RefusalReason);
        }

        [Fact]
        public void ResolveTarget_ExplicitId_NotFound_UnknownTree()
        {
            var t = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_a" }, "tree_zzz");
            Assert.Equal(RewindToLaunchTargetOutcome.UnknownTree, t.Outcome);
            Assert.Equal("unknown-tree", t.RefusalReason);
            Assert.Null(t.TreeId);
        }

        [Fact]
        public void ResolveTarget_ExplicitId_EmptyList_UnknownTree()
        {
            // An explicit arg is never re-interpreted as "auto-select": naming a tree that
            // does not exist is unknown-tree, not no-committed-tree.
            var t = TestCommandRewindToLaunch.ResolveTarget(new List<string>(), "tree_a");
            Assert.Equal(RewindToLaunchTargetOutcome.UnknownTree, t.Outcome);
            Assert.Equal("unknown-tree", t.RefusalReason);
        }

        [Fact]
        public void ResolveTarget_NoArg_NoTrees_NoCommittedTree()
        {
            var t = TestCommandRewindToLaunch.ResolveTarget(new List<string>(), null);
            Assert.Equal(RewindToLaunchTargetOutcome.NoCommittedTree, t.Outcome);
            Assert.Equal("no-committed-tree", t.RefusalReason);
        }

        [Fact]
        public void ResolveTarget_NoArg_SingleTree_Selected()
        {
            var t = TestCommandRewindToLaunch.ResolveTarget(new List<string> { "tree_only" }, null);
            Assert.Equal(RewindToLaunchTargetOutcome.Selected, t.Outcome);
            Assert.Equal("tree_only", t.TreeId);
        }

        [Fact]
        public void ResolveTarget_NoArg_MultipleTrees_AmbiguousTree()
        {
            // Guessing would silently unwind the wrong flight, irreversibly.
            var t = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_a", "tree_b" }, null);
            Assert.Equal(RewindToLaunchTargetOutcome.AmbiguousTree, t.Outcome);
            Assert.Equal("ambiguous-tree", t.RefusalReason);
            Assert.Null(t.TreeId);
        }

        [Fact]
        public void ResolveTarget_EmptyArgIsTreatedAsAbsent()
        {
            // ArgOrNull yields null for a missing key, but an explicitly empty value must
            // not become an id lookup for "".
            var t = TestCommandRewindToLaunch.ResolveTarget(new List<string> { "tree_only" }, "");
            Assert.Equal(RewindToLaunchTargetOutcome.Selected, t.Outcome);
            Assert.Equal("tree_only", t.TreeId);
        }

        [Fact]
        public void ResolveTarget_NullList_IsHandled()
        {
            Assert.Equal(RewindToLaunchTargetOutcome.NoCommittedTree,
                TestCommandRewindToLaunch.ResolveTarget(null, null).Outcome);
            Assert.Equal(RewindToLaunchTargetOutcome.UnknownTree,
                TestCommandRewindToLaunch.ResolveTarget(null, "tree_a").Outcome);
        }

        // ----- ResolveTarget: the `tree=latest` keyword -----
        //
        // WHY THE KEYWORD EXISTS, so a future reader does not "simplify" it away: a
        // step-sequence lane that produces its own rewind subject in-run (StartRecording
        // -> CommitTree) cannot NAME it, because a fresh tree's id is a runtime Guid and
        // the harness has exactly one spec-side substitution (${runSave}) with no way to
        // feed a prior step's payload into a later step's args. On a host that already
        // carries committed trees the auto-select then refuses `ambiguous-tree`, which
        // left Rewind-to-Launch undriveable by any step-sequence lane
        // (ROUTE-REWIND-TO-LAUNCH-UNREACHABLE-ON-COMMITTED-FIXTURES, blocker 2).

        [Fact]
        public void ResolveTarget_Latest_PicksTheMostRecentlyCommittedTree()
        {
            // LAST-IN-LIST IS THE DEFINITION, not an approximation:
            // RecordingStore.CommittedTrees is append-ordered on commit, and the applier
            // hands the ids in that order. Three entries, so "last" cannot be confused
            // with "only" or with "second".
            var t = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_a", "tree_b", "tree_fresh" },
                TestCommandRewindToLaunch.LatestTreeKeyword);
            Assert.Equal(RewindToLaunchTargetOutcome.Selected, t.Outcome);
            Assert.Equal("tree_fresh", t.TreeId);
            Assert.Equal(RewindToLaunchTargetResolution.LatestKeyword, t.Resolution);
            Assert.Null(t.RefusalReason);
        }

        [Fact]
        public void ResolveTarget_Latest_OverASingleTree_SelectsIt()
        {
            var t = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_only" }, "latest");
            Assert.Equal(RewindToLaunchTargetOutcome.Selected, t.Outcome);
            Assert.Equal("tree_only", t.TreeId);
            Assert.Equal(RewindToLaunchTargetResolution.LatestKeyword, t.Resolution);
        }

        [Fact]
        public void ResolveTarget_Latest_WithNoCommittedTree_IsNoCommittedTree()
        {
            // Deliberately NOT unknown-tree: an empty save is a world-state fact, and
            // blaming the argument for it would send a lane author hunting a typo. Same
            // verdict the bare no-arg call gives for the same world.
            foreach (var ids in new[] { new List<string>(), null })
            {
                var t = TestCommandRewindToLaunch.ResolveTarget(ids, "latest");
                Assert.Equal(RewindToLaunchTargetOutcome.NoCommittedTree, t.Outcome);
                Assert.Equal("no-committed-tree", t.RefusalReason);
                Assert.Null(t.TreeId);
                Assert.Equal(RewindToLaunchTargetResolution.None, t.Resolution);
            }
        }

        [Fact]
        public void ResolveTarget_Latest_IsCaseInsensitive_WhileIdsStayOrdinal()
        {
            var upper = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_a", "tree_b" }, "LATEST");
            Assert.Equal(RewindToLaunchTargetOutcome.Selected, upper.Outcome);
            Assert.Equal("tree_b", upper.TreeId);

            // Ids themselves are NOT case-folded: a case-shifted id is still unknown.
            var id = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_a" }, "TREE_A");
            Assert.Equal(RewindToLaunchTargetOutcome.UnknownTree, id.Outcome);
        }

        [Fact]
        public void ResolveTarget_ExplicitIdStillWins_EvenForATreeNamedLatest()
        {
            // THE ORDERING GUARANTEE. The keyword is tested only AFTER the exact-id scan
            // fails, so the id path is untouched by this addition. Real ids are 32-hex
            // Guid "N" strings so the collision is unreachable in practice, and the
            // ordering makes it harmless if it ever were not.
            var t = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "latest", "tree_b" }, "latest");
            Assert.Equal(RewindToLaunchTargetOutcome.Selected, t.Outcome);
            Assert.Equal("latest", t.TreeId);
            Assert.Equal(RewindToLaunchTargetResolution.ExplicitId, t.Resolution);
        }

        [Fact]
        public void ResolveTarget_TheKeywordDoesNotRelaxTheAmbiguityRule()
        {
            // "The operator did not say" and "the operator said: the newest one" are
            // different intents. Adding the keyword must not make the BARE call start
            // guessing - that refusal is the whole reason the verb is safe to drive.
            var bare = TestCommandRewindToLaunch.ResolveTarget(
                new List<string> { "tree_a", "tree_b", "tree_c" }, null);
            Assert.Equal(RewindToLaunchTargetOutcome.AmbiguousTree, bare.Outcome);
            Assert.Equal("ambiguous-tree", bare.RefusalReason);

            // And no OTHER non-id word is quietly accepted alongside it.
            foreach (var word in new[] { "newest", "last", "active", "first", "latest2" })
            {
                var t = TestCommandRewindToLaunch.ResolveTarget(
                    new List<string> { "tree_a", "tree_b" }, word);
                Assert.Equal(RewindToLaunchTargetOutcome.UnknownTree, t.Outcome);
            }
        }

        [Fact]
        public void ResolveTarget_ResolutionIsReportedForEverySelectingPath()
        {
            // The applier logs `resolvedBy=`, so every Selected path must carry a
            // resolution and every refusal must carry None - otherwise a collected log
            // would read `resolvedBy=None` on a successful rewind.
            Assert.Equal(RewindToLaunchTargetResolution.ExplicitId,
                TestCommandRewindToLaunch.ResolveTarget(
                    new List<string> { "tree_a", "tree_b" }, "tree_a").Resolution);
            Assert.Equal(RewindToLaunchTargetResolution.AutoSingle,
                TestCommandRewindToLaunch.ResolveTarget(
                    new List<string> { "tree_only" }, null).Resolution);
            Assert.Equal(RewindToLaunchTargetResolution.LatestKeyword,
                TestCommandRewindToLaunch.ResolveTarget(
                    new List<string> { "tree_a", "tree_b" }, "latest").Resolution);
            Assert.Equal(RewindToLaunchTargetResolution.None,
                TestCommandRewindToLaunch.ResolveTarget(
                    new List<string> { "tree_a", "tree_b" }, null).Resolution);
            Assert.Equal(RewindToLaunchTargetResolution.None,
                TestCommandRewindToLaunch.ResolveTarget(
                    new List<string> { "tree_a" }, "nope").Resolution);
        }

        // ----- GateRefusalMsg -----

        [Fact]
        public void GateRefusalMsg_SurfacesReasonVerbatimBehindPrefix()
        {
            // The reasons are RecordingStore.CanRewind's own strings.
            Assert.Equal("rewind-gate Rewind save file missing",
                TestCommandRewindToLaunch.GateRefusalMsg("Rewind save file missing"));
            Assert.Equal("rewind-gate Merge or discard pending tree first",
                TestCommandRewindToLaunch.GateRefusalMsg("Merge or discard pending tree first"));
            Assert.Equal("rewind-gate ", TestCommandRewindToLaunch.GateRefusalMsg(null));
        }

        [Fact]
        public void GateRefusalMsg_PrefixIsDistinctFromTheReFlyGate()
        {
            // The two gates have disjoint reason vocabularies; a spec must be able to tell
            // which mechanism refused.
            Assert.NotEqual(TestCommandInvokeRewind.GateRefusalMsg("x"),
                TestCommandRewindToLaunch.GateRefusalMsg("x"));
        }

        // ----- BuildCompletePayload -----

        [Fact]
        public void CompletePayload_CarriesRewoundRecTreeAdjustedUt()
        {
            var p = TestCommandRewindToLaunch.BuildCompletePayload("rec_ab", "tree_ab", 1234.56);
            Assert.Equal("true", Val(p, "rewound"));
            Assert.Equal("rec_ab", Val(p, "rec"));
            Assert.Equal("tree_ab", Val(p, "tree"));
            Assert.Equal("1234.6", Val(p, "adjustedUT"));
            Assert.Equal(new[] { "rewound", "rec", "tree", "adjustedUT" },
                p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void CompletePayload_NullIdsBecomeEmptyStrings()
        {
            var p = TestCommandRewindToLaunch.BuildCompletePayload(null, null, 0.0);
            Assert.Equal(string.Empty, Val(p, "rec"));
            Assert.Equal(string.Empty, Val(p, "tree"));
            Assert.Equal("0.0", Val(p, "adjustedUT"));
        }

        [Fact]
        public void CompletePayload_AdjustedUt_IsInvariantCulture()
        {
            // A comma-locale thread must still emit a dot decimal separator (the wire
            // format is parsed by the Python harness).
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandRewindToLaunch.BuildCompletePayload("r", "t", 9876543.21);
                Assert.Equal("9876543.2", Val(p, "adjustedUT"));
                Assert.DoesNotContain(",", Val(p, "adjustedUT"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }
    }
}
