using HarmonyLib;
using KSP.UI.Dialogs;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on FlightResultsDialog.Display to intercept the crash/mission
    /// report dialog. When autoMerge is off and a recording was just captured, shows
    /// the Parsek merge dialog first, then lets the KSP dialog through after the user
    /// makes a choice.
    /// </summary>
    [HarmonyPatch(typeof(FlightResultsDialog), nameof(FlightResultsDialog.Display))]
    internal static class FlightResultsPatch
    {
        internal enum DisplayInterceptDecision
        {
            Allow = 0,
            AllowBypassReplay = 1,
            SuppressAndCapture = 2,
            SuppressDuplicate = 3,
        }

        /// <summary>
        /// When set, the next Display call is a replay after our merge dialog — let it through.
        /// </summary>
        internal static bool Bypass;

        /// <summary>
        /// When set, the next stock FlightResultsDialog.Display should be intercepted
        /// because Parsek intends to show a merge dialog first.
        /// </summary>
        internal static bool DeferredMergeArmed;

        /// <summary>
        /// Stored outcome message from the suppressed Display call, replayed after merge dialog.
        /// </summary>
        internal static string PendingOutcomeMsg;

        /// <summary>
        /// Active-crash fallback path: the original crash flow is still resolving whether a
        /// later scene-change / OnFlightReady path will materialize a real merge owner.
        /// While this is true, deferred FlightResults state may survive scene change, but it
        /// must be resolved explicitly once the post-load owner state is known.
        /// </summary>
        internal static bool AwaitingSceneChangeMergeOwner;

        internal static DisplayInterceptDecision ClassifyDisplayIntercept(
            bool bypass,
            bool isAutoMerge,
            bool deferredMergeArmed,
            bool hasPendingOutcome)
        {
            if (bypass)
                return DisplayInterceptDecision.AllowBypassReplay;

            if (isAutoMerge)
                return DisplayInterceptDecision.Allow;

            if (hasPendingOutcome)
                return DisplayInterceptDecision.SuppressDuplicate;

            if (deferredMergeArmed)
                return DisplayInterceptDecision.SuppressAndCapture;

            return DisplayInterceptDecision.Allow;
        }

        internal static bool Prefix(string outcomeMsg)
        {
            var decision = ClassifyDisplayIntercept(
                Bypass,
                ParsekScenario.IsAutoMerge,
                DeferredMergeArmed,
                HasPendingResults());

            switch (decision)
            {
                case DisplayInterceptDecision.AllowBypassReplay:
                    Bypass = false;
                    ParsekLog.Verbose("FlightResultsPatch",
                        "Bypass active — letting FlightResultsDialog.Display through");
                    return true;

                case DisplayInterceptDecision.Allow:
                    return true;

                case DisplayInterceptDecision.SuppressDuplicate:
                    ParsekLog.Info("FlightResultsPatch",
                        "Suppressing duplicate FlightResultsDialog.Display while deferred results are pending");
                    return false;

                case DisplayInterceptDecision.SuppressAndCapture:
                    DeferredMergeArmed = false;
                    PendingOutcomeMsg = outcomeMsg;
                    ParsekLog.Info("FlightResultsPatch",
                        $"Intercepted FlightResultsDialog.Display — deferring until merge dialog completes (msg=\"{outcomeMsg}\")");
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Arms deferred FlightResults suppression for a destruction-driven merge flow.
        /// Must be called before KSP raises the stock flight results dialog.
        /// </summary>
        internal static void ArmForDeferredMerge(string reason)
        {
            if (ParsekScenario.IsAutoMerge)
                return;

            if (DeferredMergeArmed)
            {
                ParsekLog.Verbose("FlightResultsPatch",
                    $"ArmForDeferredMerge: already armed ({reason})");
                return;
            }

            DeferredMergeArmed = true;
            ParsekLog.Info("FlightResultsPatch",
                $"Armed deferred FlightResults suppression ({reason})");
        }

        /// <summary>
        /// Cancels a deferred merge flow that will no longer show a merge dialog.
        /// Replays the captured stock dialog immediately if one was already intercepted;
        /// otherwise just disarms the pre-capture suppression state.
        /// </summary>
        internal static void CancelDeferredMerge(string reason)
        {
            if (HasPendingResults())
            {
                ParsekLog.Info("FlightResultsPatch",
                    $"CancelDeferredMerge: replaying captured FlightResults ({reason})");
                ReplayFlightResults(reason);
                return;
            }

            DisarmDeferredMerge(reason);
        }

        /// <summary>
        /// Clears the pre-capture suppression latch while preserving any already-captured
        /// stock result for later replay.
        /// </summary>
        internal static void DisarmDeferredMerge(string reason)
        {
            if (!DeferredMergeArmed)
            {
                AwaitingSceneChangeMergeOwner = false;
                return;
            }

            DeferredMergeArmed = false;
            AwaitingSceneChangeMergeOwner = false;
            ParsekLog.Info("FlightResultsPatch",
                $"Disarmed deferred FlightResults suppression ({reason})");
        }

        internal static void BeginAwaitingSceneChangeMergeOwner(string reason)
        {
            if (ParsekScenario.IsAutoMerge)
                return;

            if (AwaitingSceneChangeMergeOwner)
            {
                ParsekLog.Verbose("FlightResultsPatch",
                    $"BeginAwaitingSceneChangeMergeOwner: already awaiting ({reason})");
                return;
            }

            AwaitingSceneChangeMergeOwner = true;
            ParsekLog.Info("FlightResultsPatch",
                $"Awaiting scene-change merge owner ({reason})");
        }

        internal static void StopAwaitingSceneChangeMergeOwner(string reason)
        {
            if (!AwaitingSceneChangeMergeOwner)
                return;

            AwaitingSceneChangeMergeOwner = false;
            ParsekLog.Info("FlightResultsPatch",
                $"Stopped awaiting scene-change merge owner ({reason})");
        }

        internal static void ResolveAwaitingSceneChangeMergeOwnerOnFlightReady(
            bool mergeOwnerExists,
            string reason)
        {
            if (!AwaitingSceneChangeMergeOwner)
                return;

            AwaitingSceneChangeMergeOwner = false;

            if (mergeOwnerExists)
                return;

            ClearPending(reason);
        }

        internal static bool ShouldPreserveAwaitingSceneChangeOwnerOnSceneChange(
            bool awaitingSceneChangeMergeOwner,
            GameScenes? pendingDestinationScene)
        {
            return awaitingSceneChangeMergeOwner
                && pendingDestinationScene.HasValue
                && pendingDestinationScene.Value != GameScenes.MAINMENU;
        }

        /// <summary>
        /// Re-shows KSP's flight results dialog with the stored outcome message.
        /// Also clears any pre-capture armed state when no message was intercepted.
        /// </summary>
        internal static void ReplayFlightResults(string reason = null)
        {
            string msg = PrepareReplayFlightResults(reason);
            if (string.IsNullOrEmpty(msg))
                return;

            FlightResultsDialog.Display(msg);
        }

        /// <summary>
        /// Pure replay-state transition used by <see cref="ReplayFlightResults"/>.
        /// Clears deferred state, sets <see cref="Bypass"/> when a captured message
        /// exists, and returns the message to replay. Returns null when there is
        /// nothing to replay.
        /// </summary>
        internal static string PrepareReplayFlightResults(string reason = null)
        {
            string msg = PendingOutcomeMsg;
            PendingOutcomeMsg = null;
            DeferredMergeArmed = false;
            AwaitingSceneChangeMergeOwner = false;

            if (string.IsNullOrEmpty(msg))
            {
                if (string.IsNullOrEmpty(reason))
                    ParsekLog.Verbose("FlightResultsPatch",
                        "PrepareReplayFlightResults: no pending message — skipping");
                else
                    ParsekLog.Info("FlightResultsPatch",
                        $"PrepareReplayFlightResults: no pending message — deferred state cleared ({reason})");
                return null;
            }

            Bypass = true;
            if (string.IsNullOrEmpty(reason))
                ParsekLog.Info("FlightResultsPatch",
                    $"Replaying FlightResultsDialog.Display (msg=\"{msg}\")");
            else
                ParsekLog.Info("FlightResultsPatch",
                    $"Replaying FlightResultsDialog.Display ({reason}, msg=\"{msg}\")");
            return msg;
        }

        /// <summary>
        /// Returns true if there is a pending outcome message that was suppressed
        /// and not yet replayed. Used as a safety net in OnFlightReady.
        /// </summary>
        internal static bool HasPendingResults()
        {
            return !string.IsNullOrEmpty(PendingOutcomeMsg);
        }

        /// <summary>
        /// Returns true when a pending tree represents a real merge owner for deferred
        /// stock flight results. Limbo carriers are restore state, not merge dialogs.
        /// </summary>
        internal static bool PendingTreeOwnsReplay(
            bool hasPendingTree,
            PendingTreeState pendingTreeState)
        {
            return hasPendingTree && pendingTreeState == PendingTreeState.Finalized;
        }

        /// <summary>
        /// Returns true when a captured stock dialog should be replayed on flight ready.
        /// Pending-tree merge owners / merge-dialog owners suppress the safety-net replay
        /// because they are responsible for showing Parsek's dialog first.
        /// </summary>
        internal static bool ShouldReplayOnFlightReady(
            bool pendingTreeOwnsReplay,
            bool mergeDialogPending)
        {
            if (!HasPendingResults())
                return false;

            return !pendingTreeOwnsReplay && !mergeDialogPending;
        }

        /// <summary>
        /// Returns true when a captured stock dialog should survive scene change because
        /// another owner (existing pending tree, current scene-exit merge path, or an
        /// already-pending scenario merge dialog) will resolve it later.
        /// </summary>
        internal static bool ShouldPreserveCapturedResultsOnSceneChange(
            bool pendingTreeOwnsReplay,
            bool sceneChangeWillCreateMergeOwner,
            bool mergeDialogPending)
        {
            if (!HasPendingResults())
                return false;

            return pendingTreeOwnsReplay || sceneChangeWillCreateMergeOwner || mergeDialogPending;
        }

        /// <summary>
        /// Clears all deferred flight-results state without replaying it.
        /// Used on scene change to prevent stale crash reports from persisting.
        /// </summary>
        internal static void ClearPending(string reason = null)
        {
            if (!string.IsNullOrEmpty(PendingOutcomeMsg))
            {
                if (string.IsNullOrEmpty(reason))
                    ParsekLog.Info("FlightResultsPatch",
                        $"Cleared pending results (msg=\"{PendingOutcomeMsg}\")");
                else
                    ParsekLog.Info("FlightResultsPatch",
                        $"Cleared pending results ({reason}, msg=\"{PendingOutcomeMsg}\")");
            }
            else if (DeferredMergeArmed)
            {
                if (string.IsNullOrEmpty(reason))
                    ParsekLog.Info("FlightResultsPatch", "Cleared armed deferred FlightResults suppression");
                else
                    ParsekLog.Info("FlightResultsPatch",
                        $"Cleared armed deferred FlightResults suppression ({reason})");
            }

            PendingOutcomeMsg = null;
            DeferredMergeArmed = false;
            AwaitingSceneChangeMergeOwner = false;
        }
    }
}
