using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// M6 "Record Supply Run" helper (design section 17: "v1 should
    /// automatically prompt after eligible committed runs"). When a tree
    /// commit produces a tree that <see cref="RouteCandidateFinder"/>
    /// classifies as an ELIGIBLE route candidate (not a near-miss), this arms
    /// a one-time, NON-BLOCKING prompt: a ScreenMessage announces it at commit
    /// time, and the main Parsek window shows a banner with Open Logistics /
    /// Dismiss actions until the player acts (drawn by
    /// <see cref="ParsekUI"/>). Deliberately not a modal PopupDialog - the
    /// commit-time "Create Supply Route?" modal was removed in the three-
    /// section Logistics window rework because it interrupted gameplay; this
    /// helper only points at the window's existing Candidates section.
    ///
    /// At-most-once contract: the prompt fires at most once per tree, ever,
    /// via the RouteStore-persisted prompted-tree-id set
    /// (<see cref="RouteStore.PromptedCandidateTreeIds"/>, the sparse
    /// <c>PROMPTED_ROUTE_CANDIDATES</c> sibling of the dismissed set - NOT a
    /// RecordingTree schema field). A dismissed tree
    /// (<see cref="RouteStore.DismissedCandidateTreeIds"/>) is never prompted;
    /// the banner's Dismiss also adds to the dismissed set so the tree leaves
    /// the Candidates section too. No prompt fires during an in-game test
    /// batch (<see cref="ParsekScenario.ActiveTestBatchMarker"/>) or a
    /// restore window (<see cref="ParsekFlight.restoringActiveTree"/> /
    /// <see cref="RecordingStore.HasCommittedTreeRestoreAttempt"/>).
    ///
    /// ERS/ELS gate-safe: reads only the committed tree handed in by the
    /// commit hook plus RouteStore accessors and the gate-safe
    /// <see cref="RouteCandidateFinder"/>; no raw committed-recording-list or
    /// ledger-action read anywhere in this file.
    /// </summary>
    internal static class RouteRunPrompt
    {
        private const string Tag = "Route";

        /// <summary>
        /// Tree id of the pending (not yet acted-on) banner prompt. Session
        /// RAM only - it survives scene changes but not a reload; the
        /// persisted prompted set guarantees the at-most-once contract either
        /// way. Null when no prompt is pending.
        /// </summary>
        internal static string PendingPromptTreeId { get; private set; }

        /// <summary>Display label (tree name) for the pending prompt banner.</summary>
        internal static string PendingPromptLabel { get; private set; }

        /// <summary>True while a banner prompt is pending in this session.</summary>
        internal static bool HasPendingPrompt => !string.IsNullOrEmpty(PendingPromptTreeId);

        /// <summary>
        /// Pure prompt decision (unit-tested truth table). True only for the
        /// FIRST commit of an ELIGIBLE candidate tree outside a test batch /
        /// restore window that the player has not dismissed.
        /// <paramref name="reason"/> names the outcome for the log either way.
        /// </summary>
        internal static bool ShouldPrompt(
            string treeId,
            bool isEligibleCandidate,
            ICollection<string> promptedTreeIds,
            ICollection<string> dismissedTreeIds,
            bool testBatchActive,
            bool restoreWindowActive,
            out string reason)
        {
            if (string.IsNullOrEmpty(treeId))
            {
                reason = "no-tree-id";
                return false;
            }
            if (testBatchActive)
            {
                reason = "test-batch-active";
                return false;
            }
            if (restoreWindowActive)
            {
                reason = "restore-window-active";
                return false;
            }
            if (dismissedTreeIds != null && dismissedTreeIds.Contains(treeId))
            {
                reason = "dismissed";
                return false;
            }
            if (promptedTreeIds != null && promptedTreeIds.Contains(treeId))
            {
                reason = "already-prompted";
                return false;
            }
            if (!isEligibleCandidate)
            {
                reason = "not-eligible-candidate";
                return false;
            }
            reason = "eligible-first-commit";
            return true;
        }

        /// <summary>
        /// Production commit hook: resolves the live suppression gates and
        /// dispatches to <see cref="NotifyTreeCommittedCore"/>. Called from
        /// <see cref="MergeDialog.MergeCommit"/> and
        /// <see cref="ParsekFlight.CommitTreeFlight"/> after the tree has
        /// moved into committed storage (so the finder sees its final sealed
        /// state). Never throws into the commit path.
        /// </summary>
        internal static void NotifyTreeCommitted(RecordingTree tree)
        {
            bool testBatchActive;
            bool restoreWindowActive;
            try
            {
                var scenario = ParsekScenario.Instance;
                testBatchActive = !object.ReferenceEquals(null, scenario)
                    && scenario.ActiveTestBatchMarker != null;
                restoreWindowActive = ParsekFlight.restoringActiveTree
                    || RecordingStore.HasCommittedTreeRestoreAttempt;
            }
            catch (System.Exception ex)
            {
                // A gate probe failing must not turn into a phantom prompt.
                ParsekLog.Warn(Tag,
                    $"RouteRunPrompt gate probe threw {ex.GetType().Name}: {ex.Message} - suppressing prompt");
                return;
            }
            NotifyTreeCommittedCore(tree, testBatchActive, restoreWindowActive);
        }

        /// <summary>
        /// Gate-parameterized core (test seam). Classifies the committed tree
        /// through the SAME finder pass the Logistics window's Candidates
        /// section uses - <see cref="RouteCandidateFinder.DeriveCandidates"/>
        /// over a single-tree list (fully-sealed + analysis-eligible +
        /// not-already-promoted-to-a-route + not-dismissed) - so a near-miss
        /// (including a not-yet-sealed tree) never prompts. On a positive
        /// decision: marks the tree prompted (persisted), arms the banner, and
        /// announces via ScreenMessage.
        /// </summary>
        internal static void NotifyTreeCommittedCore(
            RecordingTree tree, bool testBatchActive, bool restoreWindowActive)
        {
            string treeId = tree?.Id;
            bool eligible = false;
            if (tree != null)
            {
                try
                {
                    eligible = RouteCandidateFinder.DeriveCandidates(
                        new List<RecordingTree> { tree },
                        RouteStore.CommittedRoutes,
                        RouteStore.DismissedCandidateTreeIds).Count > 0;
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"RouteRunPrompt candidate classification threw {ex.GetType().Name}: " +
                        $"{ex.Message} tree={treeId ?? "<null>"} - treating as not eligible");
                    eligible = false;
                }
            }

            if (!ShouldPrompt(treeId, eligible,
                    RouteStore.PromptedCandidateTreeIds,
                    RouteStore.DismissedCandidateTreeIds,
                    testBatchActive, restoreWindowActive,
                    out string reason))
            {
                ParsekLog.Verbose(Tag,
                    $"RouteRunPrompt skipped tree={treeId ?? "<null>"} reason={reason} " +
                    $"eligible={(eligible ? "yes" : "no")}");
                return;
            }

            string label = tree.TreeName ?? "<unnamed>";
            RouteStore.MarkCandidatePrompted(treeId, label);
            PendingPromptTreeId = treeId;
            PendingPromptLabel = label;
            ParsekLog.Info(Tag,
                $"RouteRunPrompt armed tree={treeId} name='{label}' reason={reason}");
            ParsekLog.ScreenMessage(
                "This flight qualifies as a Supply Route - open Logistics to create it", 5f);
        }

        /// <summary>
        /// Clear the pending banner (player acted, the candidate was promoted
        /// to a route, or the tree was dismissed elsewhere). Safe no-op when
        /// nothing is pending.
        /// </summary>
        internal static void ClearPendingPrompt(string reason)
        {
            if (!HasPendingPrompt)
                return;
            ParsekLog.Info(Tag,
                $"RouteRunPrompt cleared tree={PendingPromptTreeId} reason={reason ?? "<none>"}");
            PendingPromptTreeId = null;
            PendingPromptLabel = null;
        }

        /// <summary>
        /// Clear the pending banner only when it targets
        /// <paramref name="treeId"/> (e.g. the Logistics window promoted that
        /// candidate to a route while the banner was still up).
        /// </summary>
        internal static void ClearPendingPromptIfTree(string treeId, string reason)
        {
            if (HasPendingPrompt && string.Equals(PendingPromptTreeId, treeId, System.StringComparison.Ordinal))
                ClearPendingPrompt(reason);
        }

        /// <summary>Test seam: reset the pending-prompt statics.</summary>
        internal static void ResetForTesting()
        {
            PendingPromptTreeId = null;
            PendingPromptLabel = null;
        }
    }
}
