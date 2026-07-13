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
        /// the post-answer scene settled; the budget bounds a stuck transition.
        /// </summary>
        internal static AnswerCompletionDecision DecideAnswerCompletion(
            double elapsedSeconds, bool answerApplied, TestCommandScene currentScene, double budgetSeconds)
        {
            bool sceneSettled = currentScene != TestCommandScene.Loading
                && currentScene != TestCommandScene.Flight;
            if (answerApplied && sceneSettled) return AnswerCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds) return AnswerCompletionDecision.AnswerTimeout;
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
    }
}
