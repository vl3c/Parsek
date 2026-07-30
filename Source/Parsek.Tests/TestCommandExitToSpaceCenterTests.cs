using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;
using DialogVariant = Parsek.SceneExitInterceptor.DialogVariant;

namespace Parsek.Tests
{
    /// <summary>
    /// R12 coverage for the <c>ExitToSpaceCenter</c> wedge guard, refusal wording, terminal
    /// payload and two-phase completion.
    ///
    /// <para>The guard is the load-bearing part. Parsek's own <c>HighLogic.LoadScene</c>
    /// prefix blocks a flight exit and spawns a <c>ControlTypes.All</c>-locking modal
    /// whenever a merge decision is outstanding, and nothing re-invokes <c>LoadScene</c>
    /// except that dialog's own <c>postChoice</c> - so a driven exit into that state does
    /// not fail, it WEDGES until the step budget expires, and <c>AnswerMergeDialog</c>
    /// cannot answer a plain whole-tree popup (it is <c>markerLive</c>-gated to the re-fly
    /// one). Every cell below pins one live state to the verdict the verb must return
    /// BEFORE initiating anything.</para>
    ///
    /// <para>Pure cells only - no collection attribute needed (no shared static state; the
    /// live wrappers <c>ShouldShowDialogBeforeSceneChangeLive</c> /
    /// <c>ShouldShowPendingTreeDialogBeforeSceneChangeLive</c> read KSP singletons and are
    /// exercised in-game, so the guard takes their RESULTS as inputs).</para>
    /// </summary>
    public class TestCommandExitToSpaceCenterTests
    {
        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- The clean route: the only shape that may initiate an exit. -----

        [Fact]
        public void Gate_ActiveTreeNoDialogWanted_Proceeds()
        {
            // autoMerge ON with a live tree and no re-fly / session: the interceptor
            // returns None and passes the exit through, and the tree auto-commits on the
            // transition. This is the supported v1 shape.
            Assert.Equal(ExitGateDecision.Proceed,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: true, switchSegmentSessionArmed: false,
                    activeTreeVariant: DialogVariant.None,
                    pendingTreeVariant: DialogVariant.None));
        }

        [Fact]
        public void Gate_NothingLive_Proceeds()
        {
            // No tree, no pending tree, no session: an ordinary exit with nothing to merge.
            Assert.Equal(ExitGateDecision.Proceed,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: false, switchSegmentSessionArmed: false,
                    activeTreeVariant: DialogVariant.None,
                    pendingTreeVariant: DialogVariant.None));
        }

        [Fact]
        public void Gate_PendingTreeUnderAutoMerge_Proceeds()
        {
            // THE CL-1 SHAPE. The active recorded vessel was destroyed, so the tree is
            // stashed as PENDING and activeTree is null; under autoMerge the pending
            // variant is None, the exit passes through, and the destination scene's OnLoad
            // auto-commits. This is precisely the transition that had no seam verb.
            Assert.Equal(ExitGateDecision.Proceed,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: false, switchSegmentSessionArmed: false,
                    activeTreeVariant: DialogVariant.None,
                    pendingTreeVariant: DialogVariant.None));
        }

        // ----- Every wedging state, one cell per DialogVariant per branch. -----

        [Theory]
        [InlineData("RegularMerge")]   // autoMerge OFF with a live tree
        [InlineData("ReFlyAttempt")]   // a live re-fly marker, which wins even under autoMerge
        public void Gate_ActiveTreeDialogWanted_Refuses(string variantName)
        {
            var variant = ParseVariant(variantName);
            Assert.Equal(ExitGateDecision.RefusedActiveTreeDialog,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: true, switchSegmentSessionArmed: false,
                    activeTreeVariant: variant,
                    pendingTreeVariant: DialogVariant.None));
        }

        [Theory]
        [InlineData("RegularMerge")]
        [InlineData("ReFlyAttempt")]
        public void Gate_PendingTreeDialogWanted_Refuses(string variantName)
        {
            var variant = ParseVariant(variantName);
            Assert.Equal(ExitGateDecision.RefusedPendingTreeDialog,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: false, switchSegmentSessionArmed: false,
                    activeTreeVariant: DialogVariant.None,
                    pendingTreeVariant: variant));
        }

        [Fact]
        public void Gate_SwitchSegmentSessionNoActiveTree_RefusesEvenWithBothVariantsNone()
        {
            // The prefix's Bug-C branch routes the dialog to the SESSION'S tree without
            // consulting the decision matrix at all - autoMerge does NOT suppress it. A
            // guard built only on ShouldShowDialogBeforeSceneChangeLive would read None
            // here and wedge, which is exactly why the session is an input of its own.
            Assert.Equal(ExitGateDecision.RefusedSwitchSegmentSession,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: false, switchSegmentSessionArmed: true,
                    activeTreeVariant: DialogVariant.None,
                    pendingTreeVariant: DialogVariant.None));
        }

        [Fact]
        public void Gate_SwitchSegmentSession_OutranksThePendingTreeBranch()
        {
            // Both sub-paths of the prefix's no-active-tree branch spawn a dialog (session
            // tree if it resolves, pending tree otherwise), so either way this refuses -
            // but the SESSION is the one reported, because that is the modal the prefix
            // reaches first.
            Assert.Equal(ExitGateDecision.RefusedSwitchSegmentSession,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: false, switchSegmentSessionArmed: true,
                    activeTreeVariant: DialogVariant.None,
                    pendingTreeVariant: DialogVariant.RegularMerge));
        }

        [Fact]
        public void Gate_ActiveTreeBranch_IgnoresThePendingVariant()
        {
            // With a live active tree the prefix never reaches its pending branch, so a
            // stale pending variant must not decide anything here.
            Assert.Equal(ExitGateDecision.Proceed,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: true, switchSegmentSessionArmed: false,
                    activeTreeVariant: DialogVariant.None,
                    pendingTreeVariant: DialogVariant.RegularMerge));
        }

        [Fact]
        public void Gate_ActiveTreeWithSessionArmed_ReportsTheActiveTreeVariant()
        {
            // A session armed alongside a LIVE tree forces the matrix to RegularMerge
            // regardless of autoMerge (ShouldShowDialogBeforeSceneChange's
            // switchSegmentActive seam), and the prefix takes its step-(4)/(6) path, not
            // the Bug-C branch. So the refusal is the ACTIVE-TREE one.
            Assert.Equal(ExitGateDecision.RefusedActiveTreeDialog,
                TestCommandExitToSpaceCenter.DecideExitGate(
                    hasActiveTree: true, switchSegmentSessionArmed: true,
                    activeTreeVariant: DialogVariant.RegularMerge,
                    pendingTreeVariant: DialogVariant.None));
        }

        // ----- Refusal wording: the msg a spec author reads. -----

        [Fact]
        public void VariantToken_NamesTheModalThatWouldHaveSpawned()
        {
            Assert.Equal("RegularMerge", TestCommandExitToSpaceCenter.VariantToken(
                ExitGateDecision.RefusedActiveTreeDialog,
                DialogVariant.RegularMerge, DialogVariant.None));
            Assert.Equal("ReFlyAttempt", TestCommandExitToSpaceCenter.VariantToken(
                ExitGateDecision.RefusedActiveTreeDialog,
                DialogVariant.ReFlyAttempt, DialogVariant.None));
            Assert.Equal("RegularMerge", TestCommandExitToSpaceCenter.VariantToken(
                ExitGateDecision.RefusedPendingTreeDialog,
                DialogVariant.None, DialogVariant.RegularMerge));
            // The Bug-C branch has no DialogVariant of its own.
            Assert.Equal("SwitchSegmentSession", TestCommandExitToSpaceCenter.VariantToken(
                ExitGateDecision.RefusedSwitchSegmentSession,
                DialogVariant.None, DialogVariant.None));
        }

        [Fact]
        public void VariantToken_Proceed_IsEmpty()
        {
            Assert.Equal(string.Empty, TestCommandExitToSpaceCenter.VariantToken(
                ExitGateDecision.Proceed, DialogVariant.None, DialogVariant.None));
        }

        [Fact]
        public void RefusalMsg_CarriesStableReasonPlusVariant()
        {
            Assert.Equal("dialog-required variant=RegularMerge",
                TestCommandExitToSpaceCenter.RefusalMsg("RegularMerge"));
            Assert.Equal("dialog-required variant=SwitchSegmentSession",
                TestCommandExitToSpaceCenter.RefusalMsg("SwitchSegmentSession"));
            // The reason prefix is what a spec matches on; it must not move.
            Assert.StartsWith(TestCommandExitToSpaceCenter.DialogRequiredReason,
                TestCommandExitToSpaceCenter.RefusalMsg("ReFlyAttempt"));
        }

        [Fact]
        public void RefusalMsg_NullVariant_StillWellFormed()
        {
            Assert.Equal("dialog-required variant=", TestCommandExitToSpaceCenter.RefusalMsg(null));
        }

        // ----- Terminal payload + two-phase completion. -----

        [Fact]
        public void CompletePayload_CarriesScene()
        {
            var p = TestCommandExitToSpaceCenter.BuildCompletePayload("SPACECENTER");
            Assert.Equal("SPACECENTER", Val(p, "scene"));
            Assert.Equal(new[] { "scene" }, p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void CompletePayload_NullScene_EmptyString()
        {
            Assert.Equal(string.Empty, Val(TestCommandExitToSpaceCenter.BuildCompletePayload(null), "scene"));
        }

        private const double Budget = 120.0;

        [Fact]
        public void Completion_SettledSpaceCenterWithGame_CompleteOk()
        {
            Assert.Equal(LoadCompletionDecision.CompleteOk,
                TestCommandExitToSpaceCenter.DecideExitCompletion(
                    5.0, TestCommandScene.SpaceCenter, currentGameNonNull: true, Budget));
        }

        [Fact]
        public void Completion_StillInFlight_StillWaiting()
        {
            // The blocked-transition case: the modal took the exit, the scene never
            // changed. It must keep waiting until the budget converts it to a terminal,
            // never read OK from the scene it started in.
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandExitToSpaceCenter.DecideExitCompletion(
                    5.0, TestCommandScene.Flight, currentGameNonNull: true, Budget));
        }

        [Fact]
        public void Completion_SpaceCenterNoGameYet_StillWaiting()
        {
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandExitToSpaceCenter.DecideExitCompletion(
                    5.0, TestCommandScene.SpaceCenter, currentGameNonNull: false, Budget));
        }

        [Fact]
        public void Completion_TrackingStation_DoesNotComplete()
        {
            // The verb's destination is fixed; landing anywhere else is not success.
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandExitToSpaceCenter.DecideExitCompletion(
                    5.0, TestCommandScene.TrackingStation, currentGameNonNull: true, Budget));
        }

        [Fact]
        public void Completion_ReturnedToMenu_FastFailureAheadOfBudget()
        {
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandExitToSpaceCenter.DecideExitCompletion(
                    2.0, TestCommandScene.MainMenu, currentGameNonNull: true, Budget));
            // ...and it still wins past the budget (the more actionable signal).
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandExitToSpaceCenter.DecideExitCompletion(
                    Budget + 10.0, TestCommandScene.MainMenu, currentGameNonNull: false, Budget));
        }

        [Fact]
        public void Completion_StuckInFlightPastBudget_Timeout()
        {
            // A transition the guard cleared but something else blocked anyway must
            // terminate, not hold the FIFO head to the harness run budget.
            Assert.Equal(LoadCompletionDecision.LoadTimeout,
                TestCommandExitToSpaceCenter.DecideExitCompletion(
                    Budget, TestCommandScene.Flight, currentGameNonNull: true, Budget));
        }

        // ----- Registration: the verb's own dispatch surface. -----

        [Fact]
        public void Budget_IsTheDrivenSceneExitBudget_NotTheDefault()
        {
            Assert.Equal(DeferralBudget.ExitToSpaceCenterSeconds,
                DeferralBudget.BudgetSeconds("ExitToSpaceCenter"));
            Assert.NotEqual(DeferralBudget.DefaultSeconds,
                DeferralBudget.BudgetSeconds("ExitToSpaceCenter"));
        }

        // The DialogVariant enum is internal and nested, so a public [InlineData]
        // signature cannot carry it; theories pass the name and map it here (the same
        // stringify idiom TestCommandDispatchStateTests uses for VerbSceneRequirement).
        private static DialogVariant ParseVariant(string name)
        {
            switch (name)
            {
                case "None": return DialogVariant.None;
                case "RegularMerge": return DialogVariant.RegularMerge;
                case "ReFlyAttempt": return DialogVariant.ReFlyAttempt;
                default: throw new Xunit.Sdk.XunitException($"unknown DialogVariant name {name}");
            }
        }
    }
}
