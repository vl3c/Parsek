using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// What <see cref="ParsekScenario.TryRestoreActiveTreeNode"/> does with a saved active tree
    /// that this session discarded (todo QUICKLOAD-INTO-DISCARDED-FLIGHT-RECORDS-AGAIN, owner
    /// ruling 2026-10-06: the resumed flight starts a fresh recording from the loaded state, as
    /// if it had never been discarded).
    /// </summary>
    internal enum DiscardedActiveTreeRestoreAction
    {
        /// <summary>Not provably a discarded tree: the restore runs as before.</summary>
        Restore = 0,
        /// <summary>The tree was discarded: it is not restored, and a fresh recording starts
        /// for the active vessel at flight ready.</summary>
        StartFreshRecording = 1,
        /// <summary>The tree was discarded, and the save's active vessel was not being recorded
        /// (no active recording): it is not restored and nothing starts.</summary>
        DropWithoutRecording = 2,
    }

    /// <summary>What the flight-ready consume does with an armed fresh recording.</summary>
    internal enum FreshRecordingAfterDiscardStartDecision
    {
        NotArmed = 0,
        Start = 1,
        /// <summary>A restore coroutine was scheduled or is running for another tree.</summary>
        SkipRestoreScheduled = 2,
        /// <summary>A recorder already records the active vessel.</summary>
        SkipRecorderLive = 3,
        /// <summary>Another path already installed an active tree.</summary>
        SkipActiveTree = 4,
        SkipNoActiveVessel = 5,
    }

    public partial class ParsekScenario
    {
        private const string DiscardedTreeTrajectoryMissingReason = "trajectory-missing";

        /// <summary>
        /// The one-shot request <see cref="TryRestoreActiveTreeNode"/> leaves for the next
        /// <c>ParsekFlight.OnFlightReady</c> when it declines a discarded tree.
        /// </summary>
        private sealed class FreshRecordingAfterDiscardRequest
        {
            internal string TreeId;
            internal string TreeName;
            internal string ActiveRecordingId;
        }

        private static FreshRecordingAfterDiscardRequest pendingFreshRecordingAfterDiscard;

        internal static bool IsFreshRecordingAfterDiscardArmed => pendingFreshRecordingAfterDiscard != null;

        internal static string FreshRecordingAfterDiscardTreeIdForTesting => pendingFreshRecordingAfterDiscard?.TreeId;

        internal static void ClearFreshRecordingAfterDiscardForTesting()
        {
            pendingFreshRecordingAfterDiscard = null;
        }

        /// <summary>
        /// Pure decision over a hydrated saved active tree. Discard deletes every sidecar of the
        /// tree, so each member then loads <c>trajectory-missing</c> with no points: the shape of
        /// the synthetic-fixture marker, which nothing drops, so the restore used to resume the
        /// recorder into the discarded ids. A missing file alone does not prove a discard (a
        /// copied save folder or deleted files look the same), so the tree id must also be one
        /// <see cref="RecordingStore.DiscardPendingTree"/> deleted sidecars for in this process.
        /// Anything else - a committed copy in memory, a member that hydrated, a member that
        /// failed for another reason (stale epoch, schema, snapshot, corrupt file) - restores
        /// as before, and so does a tree a same-id pending tree still holds (the restore salvages
        /// from it).
        /// </summary>
        internal static DiscardedActiveTreeRestoreAction DecideDiscardedActiveTreeRestore(
            RecordingTree tree,
            bool treeDiscardedThisSession,
            bool committedCopyInMemory,
            bool pendingSameIdInMemory,
            out string reason)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
            {
                reason = "no-members";
                return DiscardedActiveTreeRestoreAction.Restore;
            }
            if (!treeDiscardedThisSession)
            {
                reason = "not-discarded-this-session";
                return DiscardedActiveTreeRestoreAction.Restore;
            }
            if (committedCopyInMemory)
            {
                reason = "committed-copy-in-memory";
                return DiscardedActiveTreeRestoreAction.Restore;
            }
            if (pendingSameIdInMemory)
            {
                reason = "pending-same-id-in-memory";
                return DiscardedActiveTreeRestoreAction.Restore;
            }
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null)
                    continue;
                if (!rec.SidecarLoadFailed)
                {
                    reason = "member-hydrated id=" + (rec.RecordingId ?? kvp.Key ?? "<none>");
                    return DiscardedActiveTreeRestoreAction.Restore;
                }
                if (!string.Equals(rec.SidecarLoadFailureReason, DiscardedTreeTrajectoryMissingReason,
                        StringComparison.Ordinal))
                {
                    reason = "member-failed id=" + (rec.RecordingId ?? kvp.Key ?? "<none>")
                        + " reason=" + (rec.SidecarLoadFailureReason ?? "<unknown>");
                    return DiscardedActiveTreeRestoreAction.Restore;
                }
            }
            if (string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                reason = "discarded-this-session no-active-recording";
                return DiscardedActiveTreeRestoreAction.DropWithoutRecording;
            }
            reason = "discarded-this-session";
            return DiscardedActiveTreeRestoreAction.StartFreshRecording;
        }

        /// <summary>
        /// Runs <see cref="DecideDiscardedActiveTreeRestore"/> for <see cref="TryRestoreActiveTreeNode"/>
        /// right after hydration. On a discarded tree: the tree is not restored, the quickload
        /// resume hints are cleared, an in-memory Limbo stash (the live tree the load abandons)
        /// is popped exactly as the restore would have popped it, and a fresh recording is armed
        /// for flight ready (only when the load lands in FLIGHT, where flight ready consumes it).
        /// Returns true when the caller must return false without restoring.
        /// </summary>
        private static bool TryDeclineDiscardedActiveTree(RecordingTree tree, bool loadedSceneIsFlight)
        {
            bool committedCopy = FindCommittedTreeById(tree.Id, exclude: tree) != null;
            bool pendingSameId = RecordingStore.HasPendingTree
                && RecordingStore.PendingTree != null
                && string.Equals(RecordingStore.PendingTree.Id, tree.Id, StringComparison.Ordinal);
            bool discardedThisSession = RecordingStore.WasTreeDiscardedThisSession(tree.Id);
            DiscardedActiveTreeRestoreAction action = DecideDiscardedActiveTreeRestore(
                tree, discardedThisSession, committedCopy, pendingSameId, out string reason);
            if (action == DiscardedActiveTreeRestoreAction.Restore)
            {
                if (discardedThisSession)
                {
                    ParsekLog.Info("Scenario",
                        $"TryRestoreActiveTreeNode: tree '{tree.TreeName}' id={tree.Id} was discarded this " +
                        $"session but is restored as before (reason={reason})");
                }
                return false;
            }

            pendingActiveTreeResumeRewindSave = null;
            ClearPendingQuickloadResumeContext();
            lastRestoredQuicksaveTreeFacts = null;

            string poppedLimbo = "none";
            if (RecordingStore.HasPendingTree
                && (RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                    || RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch))
            {
                PendingTreeState poppedState = RecordingStore.PendingTreeStateValue;
                RecordingTree popped = RecordingStore.PopPendingTree();
                poppedLimbo = $"'{popped?.TreeName ?? "<null>"}' id={popped?.Id ?? "<none>"} state={poppedState}";
            }

            bool armed = action == DiscardedActiveTreeRestoreAction.StartFreshRecording && loadedSceneIsFlight;
            if (armed)
            {
                pendingFreshRecordingAfterDiscard = new FreshRecordingAfterDiscardRequest
                {
                    TreeId = tree.Id,
                    TreeName = tree.TreeName,
                    ActiveRecordingId = tree.ActiveRecordingId,
                };
            }

            string outcome = armed
                ? "a fresh recording starts for the active vessel at flight ready"
                : action == DiscardedActiveTreeRestoreAction.StartFreshRecording
                    ? "the load lands outside FLIGHT, nothing starts"
                    : "the save's active vessel was not being recorded, nothing starts";
            ParsekLog.Info("Scenario",
                $"TryRestoreActiveTreeNode: saved active tree '{tree.TreeName}' id={tree.Id} was discarded " +
                $"this session (members={tree.Recordings.Count.ToString(CultureInfo.InvariantCulture)}, every " +
                $"trajectory sidecar deleted) - not restoring it; action={action} reason={reason} " +
                $"activeRecId={tree.ActiveRecordingId ?? "<null>"} poppedLimbo={poppedLimbo} - {outcome}");
            RecorderStateLog.RecState("TryRestoreActiveTreeNode:discarded-tree-declined", CaptureScenarioRecorderState());
            return true;
        }

        /// <summary>
        /// <see cref="TryRestorePendingTreeNode"/>'s side of the same rule: a save that still
        /// holds a tree this session discarded as its PENDING tree (a save the discard's own
        /// save refresh did not rewrite) would restore an empty shell for the merge dialog or the
        /// outside-flight auto-commit. It is declined; a pending tree is a finished flight, so
        /// nothing starts. Returns true when the caller must return false without restoring.
        /// </summary>
        private static bool TryDeclineDiscardedPendingTree(RecordingTree tree)
        {
            bool committedCopy = FindCommittedTreeById(tree.Id, exclude: tree) != null;
            bool pendingSameId = RecordingStore.HasPendingTree
                && RecordingStore.PendingTree != null
                && string.Equals(RecordingStore.PendingTree.Id, tree.Id, StringComparison.Ordinal);
            bool discardedThisSession = RecordingStore.WasTreeDiscardedThisSession(tree.Id);
            DiscardedActiveTreeRestoreAction action = DecideDiscardedActiveTreeRestore(
                tree, discardedThisSession, committedCopy, pendingSameId, out string reason);
            if (action == DiscardedActiveTreeRestoreAction.Restore)
            {
                if (discardedThisSession)
                {
                    ParsekLog.Info("Scenario",
                        $"TryRestorePendingTreeNode: tree '{tree.TreeName}' id={tree.Id} was discarded this " +
                        $"session but is restored as before (reason={reason})");
                }
                return false;
            }

            ParsekLog.Info("Scenario",
                $"TryRestorePendingTreeNode: saved pending tree '{tree.TreeName}' id={tree.Id} was discarded " +
                $"this session (members={tree.Recordings.Count.ToString(CultureInfo.InvariantCulture)}, every " +
                $"trajectory sidecar deleted) - not restoring it (reason={reason})");
            return true;
        }

        /// <summary>
        /// Pure gate for the flight-ready consume of an armed fresh recording. A scheduled or
        /// running restore, a live recorder or an already installed active tree each own the
        /// flight, so starting would double-start or bind a second tree.
        /// </summary>
        internal static FreshRecordingAfterDiscardStartDecision DecideFreshRecordingAfterDiscardStart(
            bool armed,
            bool restoreScheduled,
            bool recorderLive,
            bool hasActiveTree,
            bool hasActiveVessel)
        {
            if (!armed) return FreshRecordingAfterDiscardStartDecision.NotArmed;
            if (restoreScheduled) return FreshRecordingAfterDiscardStartDecision.SkipRestoreScheduled;
            if (recorderLive) return FreshRecordingAfterDiscardStartDecision.SkipRecorderLive;
            if (hasActiveTree) return FreshRecordingAfterDiscardStartDecision.SkipActiveTree;
            if (!hasActiveVessel) return FreshRecordingAfterDiscardStartDecision.SkipNoActiveVessel;
            return FreshRecordingAfterDiscardStartDecision.Start;
        }

        /// <summary>
        /// Consumes the request <see cref="TryRestoreActiveTreeNode"/> armed for a discarded tree
        /// (always: a skip clears it too, so it can never fire on a later flight).
        /// <paramref name="startRecording"/> is the scene's normal recorder entry
        /// (<c>ParsekFlight.StartRecording</c>, which builds a new tree with fresh ids when none
        /// is active) and returns whether a recorder is running afterwards. Returns that result,
        /// false when nothing was armed or the start was skipped.
        /// </summary>
        internal static bool ConsumeFreshRecordingAfterDiscard(
            bool restoreScheduled,
            bool recorderLive,
            bool hasActiveTree,
            bool hasActiveVessel,
            Func<bool> startRecording)
        {
            FreshRecordingAfterDiscardRequest request = pendingFreshRecordingAfterDiscard;
            pendingFreshRecordingAfterDiscard = null;
            FreshRecordingAfterDiscardStartDecision decision = DecideFreshRecordingAfterDiscardStart(
                request != null, restoreScheduled, recorderLive, hasActiveTree, hasActiveVessel);
            if (decision == FreshRecordingAfterDiscardStartDecision.NotArmed)
                return false;

            string discarded = $"discarded tree '{request.TreeName}' id={request.TreeId} " +
                $"activeRecId={request.ActiveRecordingId ?? "<null>"}";
            if (decision != FreshRecordingAfterDiscardStartDecision.Start)
            {
                ParsekLog.Info("Scenario",
                    $"Fresh recording after discarded restore skipped: decision={decision} ({discarded})");
                return false;
            }

            bool started = startRecording != null && startRecording();
            if (started)
            {
                ParsekLog.Info("Scenario",
                    $"Fresh recording after discarded restore started: the flight records again as a new " +
                    $"tree ({discarded} stays discarded)");
            }
            else
            {
                ParsekLog.Warn("Scenario",
                    $"Fresh recording after discarded restore: StartRecording left no recorder running " +
                    $"({discarded}); the flight is not recorded until an auto-record trigger fires");
            }
            return started;
        }
    }
}
