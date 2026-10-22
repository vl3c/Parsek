using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>The merge-answer choice a scenario asks for. Maps 1:1 to one of the
    /// re-fly merge dialog's buttons by ROLE (order-stable across all
    /// <c>ShowTreeDialog</c> overloads), never by mutable button label text.</summary>
    internal enum MergeAnswerChoice
    {
        /// <summary>The Commit / Merge button (always button index 0).</summary>
        Merge,

        /// <summary>The Discard button (always the last button).</summary>
        Discard,

        /// <summary>The Merge-and-Seal button (present only on a not-yet-sealable re-fly
        /// dialog, the middle button of the 3-button variant).</summary>
        Seal,

        /// <summary>An unrecognized choice string -> REJECTED unknown-choice.</summary>
        Unknown,
    }

    /// <summary>The two-phase <c>AnswerMergeDialog</c> completion outcome (M-C1).</summary>
    internal enum AnswerCompletionDecision
    {
        /// <summary>The answer has not been applied and settled yet: keep holding the head.</summary>
        StillWaiting,

        /// <summary>The chosen button callback ran AND the post-answer scene settled:
        /// terminal OK.</summary>
        CompleteOk,

        /// <summary>The budget expired with the answer never applied: terminal ERROR
        /// (answer-timeout).</summary>
        AnswerTimeout,

        /// <summary>The answer WAS applied (the merge / discard / seal button callback ran
        /// and committed the irreversible effect) but the post-answer scene transition
        /// stalled past the budget: terminal ERROR (answer-applied-scene-stall) whose
        /// payload carries <c>applied=true</c>. Distinguished from AnswerTimeout so the
        /// orchestrator never reads a committed merge as a clean failure - the fact that
        /// the merge landed must not be dropped just because the scene did not settle.</summary>
        AnswerAppliedSceneStall,
    }

    /// <summary>The conclusion-drive decision for the marker-live-no-dialog branch of
    /// <c>AnswerMergeDialog</c> (S4.1-PREFIX-RACE).</summary>
    internal enum ConclusionDriveDecision
    {
        /// <summary>Drive the conclusion scene-exit now (non-FLIGHT scene, or the
        /// in-place-continuation resume has settled and the restored tree is active, so
        /// the scene-exit prefix will intercept and spawn the pre-transition dialog).</summary>
        DriveNow,

        /// <summary>The restored tree is not active yet in FLIGHT: hold the FIFO head and
        /// re-evaluate next safe-point frame. Driving now would race
        /// <c>RewindInvoker.RestoreActiveTreeFromPending</c> and slip past the
        /// scene-exit prefix un-intercepted (tree still pending-Limbo -&gt;
        /// <c>DialogVariant.None</c>).</summary>
        WaitForResume,

        /// <summary>The settle budget expired with the tree never active (a restore
        /// give-up / placeholder-mode attempt): drive anyway and let the deferred
        /// POST-transition dialog conclude the session, exactly as before the wait
        /// existed.</summary>
        DriveUnsettled,
    }

    /// <summary>
    /// Pure decision + mapping helpers for the folded conclude-and-answer
    /// <c>AnswerMergeDialog</c> seam verb (M-C1). The Unity applier on the addon locates
    /// the live <c>PopupDialog</c> by <c>MergeDialog.DialogName</c>, selects the button
    /// by the role this mapper resolves, and invokes its own callback; the completion
    /// contract (answer-applied AND scene-settled, with a post-settle re-scan for the
    /// deferred post-transition dialog) is decided here so it is xUnit-covered.
    /// </summary>
    internal static class TestCommandMergeAnswer
    {
        /// <summary>
        /// Bound on the resume-settle wait before the driven conclusion scene-exit
        /// (S4.1-PREFIX-RACE). The in-place-continuation resume normally lands well
        /// under a second after InvokeRewind's marker completion
        /// (RestoreActiveTreeFromPending's own vessel wait deadline is 3 s), so this
        /// bound only governs the restore-give-up / placeholder-mode attempts where the
        /// tree never becomes active. Must stay at most half of
        /// <see cref="DeferralBudget.AnswerMergeDialogSeconds"/> so the fallback
        /// drive plus the post-answer scene settle still fit in the verb budget
        /// (guarded by a unit test).
        /// </summary>
        internal const double ReFlyResumeSettleBudgetSeconds = 30.0;

        /// <summary>
        /// Decide whether the marker-live-no-dialog branch may DRIVE the re-fly
        /// conclusion scene-exit now (S4.1-PREFIX-RACE). The design's stated contract
        /// (design-autotest-seam-verbs-c1.md, "HasActiveTree dependency") is that the
        /// pre-transition dialog fires because the in-place-continuation resume makes
        /// the restored tree ACTIVE for the whole attempt - but that resume is an
        /// asynchronous coroutine (RewindInvoker.RestoreActiveTreeFromPending runs
        /// after onFlightReady with its own vessel wait), and InvokeRewind's completion
        /// keys on the MARKER landing, which precedes it. A driven exit issued in that
        /// window models a scene exit faster than any human route: the tree is still in
        /// the pending-Limbo slot, so the LoadScene prefix sees no active tree, no
        /// switch session and a non-Finalized pending tree, returns
        /// DialogVariant.None, and the exit slips through un-intercepted to the
        /// deferred POST-transition dialog (live evidence: run 2026-07-28_1939 hit the
        /// prefix, the five 2026-07-30 sweep runs all missed it - a 121 ms knife-edge).
        /// Waiting until the tree is active pins the driven exit to the pre-transition
        /// route the spec documents; the bound keeps a never-settling resume from
        /// wedging the verb (fall back to the deferred-dialog route, as before).
        /// </summary>
        internal static ConclusionDriveDecision DecideConclusionDrive(
            bool sceneIsFlight, bool hasActiveTree, double elapsedSeconds, double settleBudgetSeconds)
        {
            if (!sceneIsFlight || hasActiveTree)
                return ConclusionDriveDecision.DriveNow;
            if (elapsedSeconds < settleBudgetSeconds)
                return ConclusionDriveDecision.WaitForResume;
            return ConclusionDriveDecision.DriveUnsettled;
        }

        /// <summary>Map the wire <c>choice</c> arg to a button role. <c>merge</c> (alias
        /// <c>commit</c>) -> Merge; <c>discard</c> -> Discard; <c>seal</c> -> Seal;
        /// anything else -> Unknown. Case-sensitive, matching the rest of the wire
        /// grammar.</summary>
        internal static MergeAnswerChoice MapChoice(string choice)
        {
            switch (choice)
            {
                case "merge":
                case "commit":
                    return MergeAnswerChoice.Merge;
                case "discard":
                    return MergeAnswerChoice.Discard;
                case "seal":
                    return MergeAnswerChoice.Seal;
                default:
                    return MergeAnswerChoice.Unknown;
            }
        }

        /// <summary>Refusal reason: the dialog offers Discard only (design section 2.12 Rule 5,
        /// the dominance check refused the merge when the dialog opened), so a merge answer
        /// presses nothing.</summary>
        internal const string MergeNotOfferedReason = "merge-not-offered";

        /// <summary>Refusal reason: the merge button was pressed, but the click-time dominance
        /// check refused the commit; the tree stays pending and the dialog is shown again with
        /// Discard only.</summary>
        internal const string MergeRefusedAtClickReason = "merge-refused-at-click";

        /// <summary>The label of the refused-merge dialog's only button (MergeDialog).</summary>
        internal const string RefusedDialogOnlyButtonLabel = "Discard";

        /// <summary>
        /// True when a merge answer meets the refused-merge dialog: exactly one button, and it
        /// is Discard. Pressing button 0 there would discard the attempt while the seam reports
        /// a commit, so the merge answer is refused instead and nothing is pressed.
        /// </summary>
        internal static bool IsMergeNotOffered(MergeAnswerChoice choice, IList<string> buttonLabels)
        {
            if (choice != MergeAnswerChoice.Merge || buttonLabels == null || buttonLabels.Count != 1)
                return false;
            return string.Equals(buttonLabels[0], RefusedDialogOnlyButtonLabel, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when a pressed merge or seal button ended in the click-time dominance refusal:
        /// <c>MergeDialog.MergeDominanceRefusalCount</c> moved during the press.
        /// </summary>
        internal static bool WasMergeRefusedAtClick(
            MergeAnswerChoice choice, int refusalsBefore, int refusalsAfter)
        {
            if (choice != MergeAnswerChoice.Merge && choice != MergeAnswerChoice.Seal)
                return false;
            return refusalsAfter > refusalsBefore;
        }

        /// <summary>True for the two invoke outcomes that are a Parsek refusal of the merge
        /// (terminal REJECTED), as opposed to a seam failure (terminal ERROR).</summary>
        internal static bool IsMergeRefusalReason(string reason)
        {
            return reason == MergeNotOfferedReason || reason == MergeRefusedAtClickReason;
        }

        /// <summary>The <c>result=</c> payload token for an applied choice.</summary>
        internal static string ResultLabel(MergeAnswerChoice choice)
        {
            switch (choice)
            {
                case MergeAnswerChoice.Merge: return "committed";
                case MergeAnswerChoice.Discard: return "discarded";
                case MergeAnswerChoice.Seal: return "sealed";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Decide the two-phase AnswerMergeDialog completion. Scene classification lives in
        /// the pure core here (mirroring <see cref="TestCommandLoadGame.DecideLoadCompletion"/>):
        /// the driven exit is "settled" once it has left both LOADING and FLIGHT (any cleared
        /// non-flight scene - SPACECENTER / TRACKSTATION / MAINMENU). FLIGHT is deliberately
        /// NOT settled even though the pre-transition dialog can be answered while still in
        /// FLIGHT: completion waits for the post-answer scene change. Scene-settle ALONE is
        /// never OK: if the driven exit took the POST-transition path, the deferred dialog
        /// spawns AFTER the scene settles, so a settle-keyed decider would report a false OK
        /// over an orphaned unanswered dialog. The contract is therefore answer-applied AND
        /// the post-answer scene settled; the budget bounds a stuck transition. On a budget
        /// expiry the terminal SPLITS on whether the answer was applied: an APPLIED answer
        /// whose scene transition stalled is AnswerAppliedSceneStall (carrying the applied
        /// fact) so a committed merge is never reported as a clean failure; an unapplied
        /// answer is the plain AnswerTimeout.
        /// </summary>
        internal static AnswerCompletionDecision DecideAnswerCompletion(
            double elapsedSeconds, bool answerApplied, TestCommandScene currentScene, double budgetSeconds)
        {
            bool sceneSettled = currentScene != TestCommandScene.Loading
                && currentScene != TestCommandScene.Flight;
            if (answerApplied && sceneSettled) return AnswerCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds)
                return answerApplied
                    ? AnswerCompletionDecision.AnswerAppliedSceneStall
                    : AnswerCompletionDecision.AnswerTimeout;
            return AnswerCompletionDecision.StillWaiting;
        }

        /// <summary>Terminal OK payload once the answer is applied and the scene settles.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string choice, string result)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("choice", choice ?? string.Empty),
                new KeyValuePair<string, string>("result", result ?? string.Empty),
            };

        /// <summary>Terminal ERROR payload for AnswerAppliedSceneStall: the answer landed
        /// (the irreversible merge / discard / seal committed) but the post-answer scene
        /// stalled. Carries <c>applied=true</c> alongside the choice / result so the
        /// orchestrator sees the committed fact instead of a bare answer-timeout failure.</summary>
        internal static List<KeyValuePair<string, string>> BuildAppliedStallPayload(
            string choice, string result)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("applied", "true"),
                new KeyValuePair<string, string>("choice", choice ?? string.Empty),
                new KeyValuePair<string, string>("result", result ?? string.Empty),
            };
    }
}
