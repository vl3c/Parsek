using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The two-phase EvaBoard completion outcome (M-C2, F2). Board is confirmed only when
    /// the EVA vessel is destroyed, the kerbal is in the target part crew, the target is the
    /// active vessel, Parsek's board-merge is quiescent, and the scene settled.
    /// </summary>
    internal enum BoardCompletionDecision
    {
        /// <summary>The board (crew move + EVA teardown + focus switch + board-merge
        /// quiescence + settle) has not fully landed yet: keep polling.</summary>
        StillWaiting,

        /// <summary>All five conjuncts hold: terminal OK.</summary>
        CompleteOk,

        /// <summary>The budget expired without the crew moving (a silently-refused stock
        /// board: CanBoard off, capacity raced away, or a pending science-data prompt):
        /// terminal ERROR (msg=board-timeout). Never a false OK on a silent refusal.</summary>
        BoardTimeout,
    }

    /// <summary>
    /// Pure decision helpers for the two-phase <c>EvaBoard</c> seam verb (M-C2,
    /// design-autotest-eva-missions.md). The Unity applier calls
    /// <c>KerbalEVA.BoardPart(part)</c> (void, refuses via screen message only), then polls
    /// <see cref="DecideBoardCompletion"/> over five conjuncts. Kept pure so the
    /// silent-stock-refusal guard, the lost-kerbal guard, and the F2 board-merge-window
    /// guard are xUnit-covered without a live board.
    /// </summary>
    internal static class TestCommandEvaBoard
    {
        /// <summary>The seam's honesty bound (metres) over stock <c>BoardPart</c>'s missing
        /// distance check: a distant call would teleport-board, so the verb imposes its own
        /// proximity precondition. 10 m inclusive (OQ1: live-tune).</summary>
        internal const double DefaultBoardRangeMeters = 10.0;

        /// <summary>True when the kerbal-to-target-part distance is within the bound
        /// (inclusive). The seam's proximity precondition over the stock API's missing
        /// distance check.</summary>
        internal static bool IsWithinBoardRange(double distanceMeters, double bound)
            => distanceMeters <= bound;

        /// <summary>Convenience overload against the default 10 m bound.</summary>
        internal static bool IsWithinBoardRange(double distanceMeters)
            => IsWithinBoardRange(distanceMeters, DefaultBoardRangeMeters);

        /// <summary>
        /// Decide the two-phase EvaBoard completion (F2). CompleteOk requires ALL FIVE
        /// conjuncts: the EVA vessel is destroyed, the kerbal's name is in the target part's
        /// crew (the effect confirmation, since BoardPart is void), the TARGET is the active
        /// vessel (KSP's post-board focus switch completed), Parsek's board-merge is quiescent
        /// (crew-aboard + vessel-gone is true BEFORE OnVesselSwitchComplete sets
        /// ChainToVesselPending and before HandleTreeBoardMerge runs; a next FIFO command in
        /// that window corrupts the merge), and the scene settled. Positive completion first,
        /// then budget, StillWaiting as the default.
        /// </summary>
        internal static BoardCompletionDecision DecideBoardCompletion(
            double elapsed, bool evaVesselGone, bool crewAboardTarget,
            bool targetIsActiveVessel, bool boardMergeQuiescent,
            bool sceneSettled, double budget)
        {
            if (evaVesselGone && crewAboardTarget && targetIsActiveVessel
                && boardMergeQuiescent && sceneSettled)
                return BoardCompletionDecision.CompleteOk;
            if (elapsed >= budget)
                return BoardCompletionDecision.BoardTimeout;
            return BoardCompletionDecision.StillWaiting;
        }

        /// <summary>Terminal completion payload once the kerbal is aboard the target.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string kerbal, uint boardedPid)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("boardedPid",
                    boardedPid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
    }
}
