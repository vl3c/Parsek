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
    /// success (the case the two-part CompleteOk conjunction exists for), the auto-select
    /// guesses among several committed trees, or the gate reason is not surfaced verbatim.
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
                    5.0, isRewinding: true, sceneIsSpaceCenter: false, Budget));
        }

        [Fact]
        public void StillRewinding_AtSpaceCenter_WithinBudget_StillWaiting()
        {
            // Arrived at the destination scene but OnLoad has not cleared the flags: the
            // SPACECENTER half alone is NOT the success.
            Assert.Equal(RewindToLaunchCompletionDecision.StillWaiting,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    5.0, isRewinding: true, sceneIsSpaceCenter: true, Budget));
        }

        [Fact]
        public void FlagsCleared_AtSpaceCenter_CompleteOk()
        {
            Assert.Equal(RewindToLaunchCompletionDecision.CompleteOk,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    5.0, isRewinding: false, sceneIsSpaceCenter: true, Budget));
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
                    5.0, isRewinding: false, sceneIsSpaceCenter: false, Budget));
        }

        [Fact]
        public void StillRewinding_BudgetExpired_RewindTimeout()
        {
            // The budget check is UNCONDITIONAL and sits ABOVE the still-pending straddle
            // (the ordering DecideRewindCompletion was corrected to), so a reload that never
            // completes terminates instead of holding the FIFO head forever.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindTimeout,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget + 10.0, isRewinding: true, sceneIsSpaceCenter: false, Budget));
        }

        [Fact]
        public void TimeoutWinsOverStillWaiting_AtBudgetBoundary()
        {
            // Boundary is >= budget, matching DeferralBudget.ShouldTimeout.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindTimeout,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget, isRewinding: true, sceneIsSpaceCenter: false, Budget));
            Assert.Equal(RewindToLaunchCompletionDecision.StillWaiting,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget - 0.001, isRewinding: true, sceneIsSpaceCenter: false, Budget));
        }

        [Fact]
        public void FlagsCleared_NotAtSpaceCenter_BudgetExpired_RewindTimeout()
        {
            // Past the budget the timeout wins over the fast failure: the ordering puts the
            // unconditional budget check above both remaining branches.
            Assert.Equal(RewindToLaunchCompletionDecision.RewindTimeout,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget + 10.0, isRewinding: false, sceneIsSpaceCenter: false, Budget));
        }

        [Fact]
        public void CompleteOk_WinsOverBudgetExpiry()
        {
            // The success conjunction is checked FIRST, so a slow-but-successful reload
            // reports OK rather than a spurious timeout.
            Assert.Equal(RewindToLaunchCompletionDecision.CompleteOk,
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    Budget + 10.0, isRewinding: false, sceneIsSpaceCenter: true, Budget));
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
