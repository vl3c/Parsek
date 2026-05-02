using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for the post-revert merge dialog.
    /// </summary>
    public static class MergeDialog
    {
        private const string MergeLockId = "ParsekMergeDialog";
        // SplitAtSection writes back-to-back UT bounds; 50ms covers float
        // rounding and one-frame skew without bridging a real inter-recording gap.
        private const double InPlaceChainContinuityToleranceSeconds = 0.05;

        internal enum ReFlyMergeCommitResult
        {
            NotApplicable,
            Completed,
            Interrupted,
        }

        /// <summary>
        /// Fired after a tree is committed via the merge dialog.
        /// ParsekFlight subscribes to re-evaluate ghost chains.
        /// </summary>
        internal static System.Action OnTreeCommitted;

        /// <summary>
        /// Clears the deferred merge dialog flag and removes the input lock.
        /// Called from every button callback.
        /// </summary>
        internal static void ClearPendingFlag()
        {
            ParsekScenario.MergeDialogPending = false;
            InputLockManager.RemoveControlLock(MergeLockId);
        }

        /// <summary>
        /// Blocks all player interaction while the merge dialog is shown.
        /// Prevents entering KSC buildings or other actions during the dialog.
        /// </summary>
        internal static void LockInput()
        {
            InputLockManager.SetControlLock(ControlTypes.All, MergeLockId);
            ParsekLog.Verbose("MergeDialog", "Input lock set");
        }

        // ================================================================
        // Tree merge dialog
        // ================================================================

        internal static void ShowTreeDialog(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "Cannot show tree dialog: tree is null");
                return;
            }

            // Bug fix (refly-suppressed-non-leaf): when a Re-Fly session is
            // active, gather the session-suppressed subtree closure so
            // BuildDefaultVesselDecisions can force ghost-only on every
            // non-leaf recording inside it (parents + chain siblings of the
            // origin). Without this, GetAllLeaves() returns only chain tips,
            // and parent recordings keep their VesselSnapshot — later
            // GhostPlaybackLogic.ShouldSpawnAtRecordingEnd happily spawns
            // them as real vessels alongside the playback ghost.
            var scenarioForSuppression = ParsekScenario.Instance;
            ReFlySessionMarker activeMarker =
                !object.ReferenceEquals(null, scenarioForSuppression)
                    ? scenarioForSuppression.ActiveReFlySessionMarker
                    : null;
            HashSet<string> suppressedRecordingIds = null;
            string activeReFlyTargetId = null;
            if (activeMarker != null)
            {
                activeReFlyTargetId = activeMarker.ActiveReFlyRecordingId;
                try
                {
                    var closure = EffectiveState.ComputeSessionSuppressedSubtree(activeMarker);
                    if (closure != null && closure.Count > 0)
                        suppressedRecordingIds = new HashSet<string>(closure, System.StringComparer.Ordinal);
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("MergeDialog",
                        $"ShowTreeDialog: ComputeSessionSuppressedSubtree threw {ex.GetType().Name}: " +
                        $"{ex.Message} — falling back to leaf-only ghost-only decisions");
                }
            }

            var decisions = BuildDefaultVesselDecisions(
                tree, suppressedRecordingIds, activeReFlyTargetId);
            int spawnCount = 0;
            foreach (var val in decisions.Values)
                if (val) spawnCount++;

            ParsekLog.Info("MergeDialog",
                $"Tree merge dialog: tree='{tree.TreeName}', recordings={tree.Recordings.Count}, " +
                $"spawnable={spawnCount}");

            // Re-fly merge: a marker is active and the dialog is asking the
            // player to lock in the re-flight as the canonical entry. Show
            // the re-flight recording's own vessel name + duration (not the
            // whole-tree summary used for ordinary tree merges) and a one-
            // line warning that the commit cannot be undone. The supersede
            // / ghost-of-retired-attempt / kerbal-deaths-reversed paragraph
            // we used to show was misleading on the in-place continuation
            // path (no separate retired attempt exists, no ghost playback,
            // tombstones not reversed in v1) so we drop the advisory and
            // keep the dialog short and unambiguous instead.
            var reFlyScenario = ParsekScenario.Instance;
            string message;
            if (!object.ReferenceEquals(null, reFlyScenario)
                && reFlyScenario.ActiveReFlySessionMarker != null)
            {
                Recording reFlyRec = FindReFlyRecording(
                    reFlyScenario.ActiveReFlySessionMarker, tree);
                string vesselLabel = reFlyRec != null
                    ? (reFlyRec.VesselName ?? tree.TreeName ?? "<unnamed>")
                    : (tree.TreeName ?? "<unnamed>");
                double reFlyDuration = reFlyRec != null
                    ? System.Math.Max(0.0, reFlyRec.EndUT - reFlyRec.StartUT)
                    : ComputeTreeDurationRange(tree);
                // TMP rich-text alignment: center the headline (vessel name +
                // re-flight duration), one blank line, then the warning text
                // left-aligned. KSP's MultiOptionDialog body renders through
                // TMP which honours the <align> tag; a one-line headline with
                // a paragraph break before the body keeps the dialog short
                // (per playtest feedback the long supersede paragraph that
                // used to live here was confusing on the in-place
                // continuation path and just plain wrong about
                // ghost-of-retired-attempt / kerbal-deaths-reversed).
                message = $"<align=\"center\">{vesselLabel} - {FormatDuration(reFlyDuration)}</align>\n\n" +
                          "<align=\"left\">Commit this Re-Fly attempt permanently to the timeline. " +
                          "This cannot be undone.</align>";
            }
            else
            {
                // Regular tree-merge: just the headline. The spawnable=0
                // advisory ("no flight branches produced a vessel that can
                // continue flying") that used to ride here was over-
                // explanation — when the tree's recordings are all crashed
                // or recovered, ghost-only playback is the obvious outcome
                // and the player already saw it happen. If any recording
                // had survived as a flyable vessel, it would not be sitting
                // in a pending tree at all.
                double duration = ComputeTreeDurationRange(tree);
                message = $"{tree.TreeName} - {FormatDuration(duration)}";
            }

            var capturedDecisions = decisions;
            int capturedSpawnCount = spawnCount;

            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton("Merge to Timeline", () =>
                {
                    MergeCommit(tree, capturedDecisions, capturedSpawnCount);
                }),
                new DialogGUIButton("Discard", () =>
                {
                    MergeDiscard(tree);
                })
            };

            LockInput();
            PopupDialog.DismissPopup("ParsekMerge");
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekMerge",
                    message,
                    "Confirm Merge to Timeline",
                    HighLogic.UISkin,
                    buttons
                ),
                false,
                HighLogic.UISkin
            );
        }

        internal static string FormatDuration(double seconds)
            => ParsekTimeFormat.FormatDuration(seconds);

        /// <summary>
        /// Locate the recording the active re-fly session targets. Tries the
        /// pending tree first (so the lookup works whether the dialog fires
        /// before or after `RecordingStore.CommitPendingTree`), then falls
        /// back to the committed recordings list. Returns null if neither
        /// source has the recording — the caller falls back to whole-tree
        /// metadata.
        /// </summary>
        internal static Recording FindReFlyRecording(
            ReFlySessionMarker marker, RecordingTree pendingTree)
        {
            if (marker == null) return null;
            string targetId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(targetId)) return null;

            if (pendingTree != null && pendingTree.Recordings != null
                && pendingTree.Recordings.TryGetValue(targetId, out Recording fromTree)
                && fromTree != null)
                return fromTree;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, targetId, System.StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        // ================================================================
        // Tree commit / discard — extracted from the dialog button lambdas
        // so that unit tests can exercise the post-button work directly
        // (the lambda itself isn't reachable from outside Unity).
        //
        // Phase C of the ledger / lump-sum reconciliation fix
        // (`docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md`) wires
        // <see cref="RecordingStore.MarkTreeAsApplied"/> into the merge path
        // so committed tree recordings are immediately marked fully applied
        // (the in-flight commit path <see cref="ParsekFlight.CommitTreeFlight"/>
        // already does the equivalent inline; this brings the merge dialog
        // into parity).
        //
        // Discard intentionally does NOT mark applied: the tree is being
        // wiped from storage, so there is no surviving caller that needs
        // its recording indexes advanced.
        // ================================================================

        /// <summary>
        /// Implements the "Merge to Timeline" branch of the dialog.
        /// </summary>
        internal static void MergeCommit(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            int spawnCount)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "MergeCommit: tree is null — nothing to commit");
                return;
            }

            ApplyVesselDecisions(tree, decisions);
            RecordingStore.CommitPendingTree();
            // Phase C/F: mark recordings fully applied after the tree moves
            // from pending to committed state.
            RecordingStore.MarkTreeAsApplied(tree);
            RecordingStore.RunOptimizationPass();
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            CrewReservationManager.SwapReservedCrewInFlight();

            // Phase 8 of Rewind-to-Staging (design §6.6 steps 2-3): if a
            // re-fly session is active, write the supersede relations for the
            // origin subtree and flip the provisional's MergeState AFTER the
            // tree commits (so the provisional has moved from pending-tree
            // storage into the committed list) and BEFORE firing
            // OnTreeCommitted (so downstream chain evaluators see the
            // superseded subtree hidden from ERS).
            ReFlyMergeCommitResult reFlyResult = TryCommitReFlySupersede();

            // #292 + rewind-staging follow-up: refresh quicksave only after the
            // re-fly staged commit has either completed or been bypassed.
            // Interrupted re-fly commits intentionally skip the refresh so F9
            // cannot resurrect a half-committed session from a stale snapshot.
            if (reFlyResult != ReFlyMergeCommitResult.Interrupted)
            {
                RecordingStore.RefreshQuicksaveAfterMerge(
                    "merge dialog Tree Merge", tree.Recordings.Count);
            }

            ClearPendingFlag();
            OnTreeCommitted?.Invoke();
            if (spawnCount > 0)
                ParsekLog.ScreenMessage(
                    $"Merged - {spawnCount} vessel(s) will appear after ghost playback", 3f);
            else
                ParsekLog.ScreenMessage(
                    "Merged to timeline (no surviving vessels)", 3f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Merge (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count}, spawnable={spawnCount})");
        }

        /// <summary>
        /// Implements the "Discard" branch of the dialog. Does NOT call
        /// <see cref="RecordingStore.MarkTreeAsApplied"/>: the tree is
        /// removed from storage by <see cref="RecordingStore.DiscardPendingTree"/>
        /// so there is no surviving caller that needs recording indexes advanced.
        /// </summary>
        internal static void MergeDiscard(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "MergeDiscard: tree is null — nothing to discard");
                return;
            }

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.VesselSnapshot != null)
                    CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
            }

            if (TryDiscardActiveReFlyAttempt(tree))
                return;

            // #466: while the merge/discard choice is pending, mid-flight effects stay live
            // in KSP and patching is deferred. Discard must now rebuild from the committed
            // ledger immediately after the pending tree is removed.
            ParsekScenario.DiscardPendingTreeAndRecalculate("merge dialog discard");
            ClearPendingFlag();
            ParsekLog.ScreenMessage("Recording discarded", 2f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Discard (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count})");
        }

        /// <summary>
        /// Re-Fly-specific Discard branch for the scene-exit merge dialog.
        /// A Re-Fly pending tree often reuses the original committed tree id;
        /// the ordinary tree-discard path would route through
        /// <see cref="TreeDiscardPurge.PurgeTree"/> and purge the mission's
        /// persistent rewind/supersede/tombstone state. This path abandons
        /// only the active session attempt and leaves the committed mission
        /// timeline intact.
        /// </summary>
        internal static bool TryDiscardActiveReFlyAttempt(RecordingTree tree)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
                return false;

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
                return false;

            if (!IsReFlyMarkerScopedToTree(marker, tree))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: active marker sess={marker.SessionId ?? "<no-id>"} " +
                    $"tree={marker.TreeId ?? "<none>"} does not match dialog tree={tree?.Id ?? "<none>"} - " +
                    "falling back to regular tree discard");
                return false;
            }

            string sessionId = marker.SessionId;
            var attemptIds = CollectReFlyAttemptOwnedRecordingIds(tree, marker);

            int removedCommitted = RemoveCommittedAttemptRecordings(attemptIds);
            int purgedEvents = GameStateStore.PurgeEventsForRecordings(
                attemptIds,
                $"MergeDialog Re-Fly discard sess={sessionId ?? "<no-id>"}");
            int deletedFiles = DeleteAttemptRecordingFiles(tree, attemptIds);
            int transientCleared = ClearReFlyAttemptTransientFields(tree, marker, attemptIds);
            bool committedTreeDetached = !CommittedTreeExists(tree.Id);
            bool inPlaceTrimmed = committedTreeDetached
                && TrimInPlaceAttemptBackToOriginRewindPoint(tree, scenario, marker);
            bool rpPromoted = PromoteOriginRewindPointForDiscard(scenario, marker);
            bool restoredCommittedTree = committedTreeDetached
                && RestoreSanitizedPendingTreeIfDetached(tree, marker, attemptIds);

            RecordingStore.PopPendingTree();
            GameStateRecorder.PendingScienceSubjects.Clear();
            RecordingStore.ClearRewindReplayTargetScope();

            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.ActiveMergeJournal = null;
            scenario.BumpSupersedeStateVersion();
            ReFlyRevertButtonGate.Apply("MergeDialog:discard-refly-attempt");
            SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession(sessionId);

            LedgerOrchestrator.RecalculateAndPatch();
            ClearPendingFlag();

            ParsekLog.ScreenMessage("Re-Fly attempt discarded", 2f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Re-Fly Attempt Discard (tree='{tree.TreeName}', " +
                $"treeId={tree.Id ?? "<none>"}, sess={sessionId ?? "<no-id>"}, " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}, " +
                $"active={marker.ActiveReFlyRecordingId ?? "<none>"}, " +
                $"attemptIds={attemptIds.Count}, removedCommitted={removedCommitted}, " +
                $"purgedEvents={purgedEvents}, deletedFiles={deletedFiles}, " +
                $"transientCleared={transientCleared}, inPlaceTrimmed={inPlaceTrimmed}, " +
                $"rpPromoted={rpPromoted}, restoredCommittedTree={restoredCommittedTree})");
            ParsekLog.Info("ReFlySession",
                $"End reason=discardReFlyAttemptFromMergeDialog sess={sessionId ?? "<no-id>"} " +
                $"tree={tree.Id ?? "<none>"} active={marker.ActiveReFlyRecordingId ?? "<none>"} " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}");
            return true;
        }

        private static bool IsReFlyMarkerScopedToTree(
            ReFlySessionMarker marker, RecordingTree tree)
        {
            if (marker == null || tree == null)
                return false;
            if (string.IsNullOrEmpty(marker.TreeId))
                return true;
            return string.Equals(marker.TreeId, tree.Id, System.StringComparison.Ordinal);
        }

        private static HashSet<string> CollectReFlyAttemptOwnedRecordingIds(
            RecordingTree tree, ReFlySessionMarker marker)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            if (marker == null)
                return ids;

            AddAttemptIdIfSafe(ids, marker.ActiveReFlyRecordingId, marker, tree);

            if (tree == null || tree.Recordings == null)
                return ids;

            foreach (var rec in tree.Recordings.Values)
            {
                if (!IsReFlyAttemptOwnedRecording(rec, marker))
                    continue;
                AddAttemptIdIfSafe(ids, rec.RecordingId, marker, tree);
            }

            return ids;
        }

        private static bool IsReFlyAttemptOwnedRecording(
            Recording rec, ReFlySessionMarker marker)
        {
            if (rec == null || marker == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;

            if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                && string.Equals(rec.RecordingId,
                    marker.ActiveReFlyRecordingId, System.StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(marker.SessionId)
                && string.Equals(rec.CreatingSessionId,
                    marker.SessionId, System.StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(marker.RewindPointId)
                && string.Equals(rec.ProvisionalForRpId,
                    marker.RewindPointId, System.StringComparison.Ordinal))
                return true;

            if (rec.MergeState == MergeState.NotCommitted
                && !string.IsNullOrEmpty(rec.SupersedeTargetId))
            {
                if (string.Equals(rec.SupersedeTargetId,
                        marker.OriginChildRecordingId, System.StringComparison.Ordinal))
                    return true;
                if (string.Equals(rec.SupersedeTargetId,
                        marker.SupersedeTargetId, System.StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void AddAttemptIdIfSafe(
            HashSet<string> ids,
            string recordingId,
            ReFlySessionMarker marker,
            RecordingTree dialogTree)
        {
            if (ids == null || string.IsNullOrEmpty(recordingId))
                return;

            if (IsProtectedReFlyRecordingId(recordingId, marker))
            {
                ParsekLog.Verbose("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: protected original rec={recordingId} " +
                    "excluded from attempt discard");
                return;
            }

            if (CommittedTreeContainsRecording(dialogTree?.Id, recordingId))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: rec={recordingId} exists in committed tree " +
                    $"{dialogTree?.Id ?? "<none>"} - excluding from attempt discard to protect mission history");
                return;
            }

            ids.Add(recordingId);
        }

        private static bool IsProtectedReFlyRecordingId(
            string recordingId, ReFlySessionMarker marker)
        {
            if (string.IsNullOrEmpty(recordingId) || marker == null)
                return true;
            if (string.Equals(recordingId,
                    marker.OriginChildRecordingId, System.StringComparison.Ordinal))
                return true;
            if (string.Equals(recordingId,
                    marker.SupersedeTargetId, System.StringComparison.Ordinal))
                return true;
            return false;
        }

        private static bool CommittedTreeContainsRecording(
            string treeId, string recordingId)
        {
            if (string.IsNullOrEmpty(treeId) || string.IsNullOrEmpty(recordingId))
                return false;

            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return false;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null || committedTree.Recordings == null)
                    continue;
                if (!string.Equals(committedTree.Id, treeId, System.StringComparison.Ordinal))
                    continue;
                if (committedTree.Recordings.ContainsKey(recordingId))
                    return true;
            }

            return false;
        }

        private static int RemoveCommittedAttemptRecordings(HashSet<string> attemptIds)
        {
            if (attemptIds == null || attemptIds.Count == 0)
                return 0;

            int removed = 0;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return 0;

            for (int i = committed.Count - 1; i >= 0; i--)
            {
                var rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!attemptIds.Contains(rec.RecordingId))
                    continue;

                if (RecordingStore.RemoveCommittedInternal(rec))
                {
                    removed++;
                    ParsekLog.Info("RecordingStore",
                        $"Removed Re-Fly attempt rec={rec.RecordingId} during merge-dialog discard");
                }
            }

            return removed;
        }

        private static int DeleteAttemptRecordingFiles(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.Recordings == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int deleted = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!attemptIds.Contains(rec.RecordingId))
                    continue;
                RecordingStore.DeleteRecordingFiles(rec);
                deleted++;
            }
            return deleted;
        }

        private static int ClearReFlyAttemptTransientFields(
            RecordingTree tree,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            int cleared = 0;
            cleared += ClearTransientFieldsInRecordings(
                RecordingStore.CommittedRecordings, marker, attemptIds);
            if (tree != null && tree.Recordings != null)
                cleared += ClearTransientFieldsInRecordings(
                    tree.Recordings.Values, marker, attemptIds);
            return cleared;
        }

        private static int ClearTransientFieldsInRecordings(
            IEnumerable<Recording> recordings,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            if (recordings == null || marker == null)
                return 0;

            int cleared = 0;
            foreach (var rec in recordings)
            {
                if (rec == null)
                    continue;

                bool sessionOwned = !string.IsNullOrEmpty(marker.SessionId)
                    && string.Equals(rec.CreatingSessionId,
                        marker.SessionId, System.StringComparison.Ordinal);
                bool rpOwned = !string.IsNullOrEmpty(marker.RewindPointId)
                    && string.Equals(rec.ProvisionalForRpId,
                        marker.RewindPointId, System.StringComparison.Ordinal);
                bool attemptOwned = attemptIds != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && attemptIds.Contains(rec.RecordingId);

                if (!sessionOwned && !rpOwned && !attemptOwned)
                    continue;

                if (!string.IsNullOrEmpty(rec.CreatingSessionId)
                    || !string.IsNullOrEmpty(rec.ProvisionalForRpId)
                    || !string.IsNullOrEmpty(rec.SupersedeTargetId))
                {
                    cleared++;
                }

                rec.CreatingSessionId = null;
                rec.ProvisionalForRpId = null;
                rec.SupersedeTargetId = null;
            }
            return cleared;
        }

        private static bool TrimInPlaceAttemptBackToOriginRewindPoint(
            RecordingTree tree,
            ParsekScenario scenario,
            ReFlySessionMarker marker)
        {
            if (tree == null || tree.Recordings == null || marker == null)
                return false;
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || !string.Equals(marker.ActiveReFlyRecordingId,
                    marker.OriginChildRecordingId, System.StringComparison.Ordinal))
            {
                return false;
            }

            if (!tree.Recordings.TryGetValue(
                    marker.OriginChildRecordingId, out Recording origin)
                || origin == null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"TrimInPlaceAttemptBackToOriginRewindPoint: origin rec={marker.OriginChildRecordingId ?? "<none>"} " +
                    $"missing from tree={tree.Id ?? "<none>"}");
                return false;
            }

            RewindPoint rp = FindRewindPointForMarker(scenario, marker);
            if (rp == null || double.IsNaN(rp.UT) || double.IsInfinity(rp.UT))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TrimInPlaceAttemptBackToOriginRewindPoint: origin RP " +
                    $"rp={marker.RewindPointId ?? "<none>"} missing or invalid - leaving in-place recording untrimmed");
                return false;
            }

            bool trimmed = ParsekScenario.TrimRecordingPastUT(origin, rp.UT);
            if (origin.TerminalStateValue.HasValue
                || origin.TerminalPosition.HasValue
                || origin.EndpointPhase != RecordingEndpointPhase.Unknown)
            {
                origin.TerminalStateValue = null;
                origin.TerminalPosition = null;
                origin.EndpointPhase = RecordingEndpointPhase.Unknown;
                origin.EndpointBodyName = null;
                origin.TerrainHeightAtEnd = double.NaN;
                origin.MarkFilesDirty();
                trimmed = true;
            }

            ParsekLog.Info("MergeDialog",
                $"TrimInPlaceAttemptBackToOriginRewindPoint: rec={origin.RecordingId ?? "<none>"} " +
                $"rp={rp.RewindPointId ?? "<none>"} cutoffUT={rp.UT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"trimmed={trimmed}");
            return trimmed;
        }

        private static bool RestoreSanitizedPendingTreeIfDetached(
            RecordingTree tree,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            if (tree == null)
                return false;
            if (CommittedTreeExists(tree.Id))
                return false;

            int prunedRecordings = RemoveAttemptRecordingsFromTree(tree, attemptIds);
            if (tree.Recordings == null || tree.Recordings.Count == 0)
            {
                ParsekLog.Warn("MergeDialog",
                    $"RestoreSanitizedPendingTreeIfDetached: tree={tree.Id ?? "<none>"} " +
                    "has no recordings after Re-Fly attempt pruning - cannot restore committed tree");
                return false;
            }

            if (string.IsNullOrEmpty(tree.ActiveRecordingId)
                || !tree.Recordings.ContainsKey(tree.ActiveRecordingId))
            {
                tree.ActiveRecordingId = !string.IsNullOrEmpty(marker?.OriginChildRecordingId)
                    && tree.Recordings.ContainsKey(marker.OriginChildRecordingId)
                        ? marker.OriginChildRecordingId
                        : FirstRecordingId(tree);
            }
            if (string.IsNullOrEmpty(tree.RootRecordingId)
                || !tree.Recordings.ContainsKey(tree.RootRecordingId))
            {
                tree.RootRecordingId = tree.ActiveRecordingId ?? FirstRecordingId(tree);
            }

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec != null && string.IsNullOrEmpty(rec.TreeId))
                    rec.TreeId = tree.Id;
            }

            RecordingStore.CommitTree(tree);
            RecordingStore.MarkTreeAsApplied(tree);
            ParsekLog.Info("MergeDialog",
                $"RestoreSanitizedPendingTreeIfDetached: restored committed tree " +
                $"tree={tree.Id ?? "<none>"} recordings={tree.Recordings.Count} " +
                $"prunedAttemptRecordings={prunedRecordings}");
            return true;
        }

        private static int RemoveAttemptRecordingsFromTree(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.Recordings == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int removed = 0;
            foreach (string id in attemptIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                if (tree.Recordings.Remove(id))
                    removed++;
            }

            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    var bp = tree.BranchPoints[i];
                    if (bp == null)
                        continue;
                    RemoveAttemptIds(bp.ParentRecordingIds, attemptIds);
                    RemoveAttemptIds(bp.ChildRecordingIds, attemptIds);
                }
            }

            return removed;
        }

        private static void RemoveAttemptIds(
            List<string> recordingIds, HashSet<string> attemptIds)
        {
            if (recordingIds == null || attemptIds == null || attemptIds.Count == 0)
                return;
            for (int i = recordingIds.Count - 1; i >= 0; i--)
            {
                if (attemptIds.Contains(recordingIds[i]))
                    recordingIds.RemoveAt(i);
            }
        }

        private static string FirstRecordingId(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null)
                return null;
            foreach (var kvp in tree.Recordings)
                return kvp.Key;
            return null;
        }

        private static bool CommittedTreeExists(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return false;
            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return false;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null)
                    continue;
                if (string.Equals(committedTree.Id, treeId, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static RewindPoint FindRewindPointForMarker(
            ParsekScenario scenario, ReFlySessionMarker marker)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null
                || marker == null
                || string.IsNullOrEmpty(marker.RewindPointId))
            {
                return null;
            }

            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null)
                    continue;
                if (string.Equals(rp.RewindPointId,
                        marker.RewindPointId, System.StringComparison.Ordinal))
                    return rp;
            }
            return null;
        }

        private static bool PromoteOriginRewindPointForDiscard(
            ParsekScenario scenario,
            ReFlySessionMarker marker)
        {
            RewindPoint rp = FindRewindPointForMarker(scenario, marker);
            if (rp == null)
                return false;

            bool promoted = rp.SessionProvisional;
            rp.SessionProvisional = false;
            rp.CreatingSessionId = null;
            ParsekLog.Info("ReFlySession",
                $"Origin RP promoted to persistent rp={marker.RewindPointId} " +
                $"sess={marker.SessionId ?? "<no-id>"} reason=discardReFlyAttemptFromMergeDialog");
            return promoted;
        }

        /// <summary>
        /// Phase 8 of Rewind-to-Staging (design §6.6 steps 2-3): if a re-fly
        /// session is active at merge time, write the supersede relations for
        /// the origin subtree and flip the provisional's MergeState. Skipped
        /// silently when no session is active — the regular tree-merge flow
        /// is unchanged.
        /// </summary>
        internal static ReFlyMergeCommitResult TryCommitReFlySupersede()
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose("MergeDialog",
                    "TryCommitReFlySupersede: no scenario instance — skipping re-fly commit path");
                return ReFlyMergeCommitResult.NotApplicable;
            }

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                ParsekLog.Verbose("MergeDialog",
                    "TryCommitReFlySupersede: no active re-fly session marker — " +
                    "regular tree-merge flow, no supersede relations to write");
                return ReFlyMergeCommitResult.NotApplicable;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryCommitReFlySupersede: marker sess={marker.SessionId ?? "<no-id>"} " +
                    "has no ActiveReFlyRecordingId — cannot commit supersede; " +
                    "leaving marker in place for load-time sweep");
                return ReFlyMergeCommitResult.Interrupted;
            }

            Recording provisional = FindCommittedRecording(provisionalId);
            if (provisional == null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryCommitReFlySupersede: provisional rec={provisionalId} " +
                    "not found in committed list after tree commit; " +
                    "leaving marker in place for load-time sweep");
                return ReFlyMergeCommitResult.Interrupted;
            }

            // In-place continuation guard: if the Limbo-restore path kept
            // the origin recording alive across an RP-quicksave reload and
            // the re-fly continued writing into that SAME recording,
            // `RewindInvoker.AtomicMarkerWrite` already detected the case
            // and pointed `marker.ActiveReFlyRecordingId` directly at the
            // origin id (no placeholder created). At merge time
            // `provisional.RecordingId == marker.OriginChildRecordingId`
            // — there is no separate "retired attempt" to supersede.
            // Writing a supersede relation with old==new would create a
            // 1-node cycle that poisons EffectiveRecordingId (WARN
            // `cycle detected` every lookup) and keeps the recording
            // permanently visible to ERS. Skip the journaled merge entirely
            // and do only the finalization the orchestrator would have
            // done around AppendRelations: flip MergeState, clear transient
            // fields, bump versions, durable save. We deliberately skip
            // tombstones in this v1 path — any prior kerbal-death actions
            // credited during the original Destroyed run-through were
            // already finalized in that earlier session, and the same
            // recording's subsequent continuation doesn't retroactively
            // un-do them. If the player needs a deeper unwind they can
            // re-rewind from the RP.
            if (provisional != null
                && !string.IsNullOrEmpty(provisional.RecordingId)
                && string.Equals(provisional.RecordingId,
                    marker.OriginChildRecordingId, System.StringComparison.Ordinal))
            {
                ParsekLog.Info("MergeDialog",
                    $"TryCommitReFlySupersede: in-place continuation detected " +
                    $"(provisional == origin == {provisional.RecordingId}); skipping " +
                    $"journaled supersede merge but still appending supersede rows for " +
                    $"sibling/parent recordings in the closure. " +
                    $"Tombstones from the prior Destroyed run are left as-is in v1.");
                try
                {
                    // Bug fix (in-place-supersede): the subtree closure also
                    // contains chain siblings and parent recordings beyond
                    // the origin itself. The pre-fix path skipped
                    // AppendRelations entirely, so a destroyed-final-state
                    // sibling (e.g. the prior "Kerbal X Probe" Destroyed
                    // chain segment) stayed visible after merge — its
                    // EffectiveState.IsVisible returned true with no
                    // supersede row pointing at it. Call AppendRelations so
                    // every non-self entry in the closure gets a supersede
                    // row pointing at the in-place provisional. The
                    // newly-restored old==new self-skip inside
                    // AppendRelations guards the trivial origin-self entry,
                    // and the existing RelationExists duplicate guard makes
                    // the call safe to re-run on resume.
                    //
                    // Optimizer-split chain-tip resolve (review follow-up):
                    // MergeCommit ran RecordingStore.RunOptimizationPass()
                    // BEFORE we get here. If the in-place continuation
                    // crossed an environment boundary (atmo↔exo), the
                    // optimizer split the head into HEAD + TIP, MOVING
                    // VesselSnapshot + TerminalStateValue from HEAD to TIP
                    // (RecordingOptimizer.SplitAtSection lines 513-514 and
                    // 536-537). HEAD now has TerminalStateValue == null,
                    // which fails ValidateSupersedeTarget's `null TerminalState`
                    // clause inside AppendRelations: throws in DEBUG, returns
                    // an empty subtree in RELEASE. Either way the sibling
                    // supersede rows the in-place fix needs are NOT written.
                    //
                    // Resolve the chain tip via the existing helper used by
                    // EffectiveState.IsUnfinishedFlight for the same reason
                    // (it walks ChainId+ChainBranch+TreeId to the recording
                    // with the largest ChainIndex). Pass the resolved tip to
                    // AppendRelations: validation passes against the tip's
                    // terminal state, the row's `new` side becomes the
                    // tip's id, and the closure entries for HEAD and TIP
                    // are both filtered by the `old==new`-aware
                    // self-skip-by-id check below (we explicitly add HEAD
                    // to the skip set so HEAD is not redirected to TIP via
                    // ERS even if newRecordingId != HEAD.id). For
                    // non-split chains ResolveChainTerminalRecording
                    // returns the input unchanged, preserving the prior
                    // behaviour for un-split in-place merges.
                    if (scenario.RecordingSupersedes == null)
                        scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
                    List<Recording> contiguousInPlaceChain =
                        ResolveContiguousInPlaceChainMembers(provisional);
                    Recording contiguousTip = contiguousInPlaceChain.Count > 0
                        ? contiguousInPlaceChain[contiguousInPlaceChain.Count - 1]
                        : provisional;
                    Recording supersedeTargetRec =
                        EffectiveState.ResolveChainTerminalRecording(provisional);
                    Recording sessionOwnedTip = ResolveSessionOwnedChainTerminalRecording(
                        provisional, marker.SessionId);
                    int sessionOwnedSize = CountSessionOwnedChainMembers(
                        provisional, marker.SessionId);
                    bool sessionOwnedResolved = IsDifferentRecording(
                        sessionOwnedTip, provisional);
                    if (sessionOwnedResolved)
                    {
                        supersedeTargetRec = sessionOwnedTip;
                    }
                    else if (IsDifferentRecording(contiguousTip, provisional))
                    {
                        supersedeTargetRec = contiguousTip;
                        ParsekLog.Info("MergeDialog",
                            $"TryCommitReFlySupersede: in-place continuation resolved " +
                            $"chain tip from contiguous split bounds: head={provisional.RecordingId} " +
                            $"-> tip={contiguousTip.RecordingId} " +
                            $"chainId={contiguousTip.ChainId ?? "<none>"} " +
                            $"chainIndex={contiguousTip.ChainIndex} " +
                            $"members=[{FormatRecordingIds(contiguousInPlaceChain)}] " +
                            $"tolerance={InPlaceChainContinuityToleranceSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}s " +
                            "(session metadata missing or only present on the head)");
                    }
                    supersedeTargetRec = EnsureInPlaceSupersedeTargetHasTerminalState(
                        supersedeTargetRec, marker.SessionId);
                    bool resolvedToDifferentTip = supersedeTargetRec != null
                        && !object.ReferenceEquals(supersedeTargetRec, provisional)
                        && !string.Equals(supersedeTargetRec.RecordingId,
                            provisional.RecordingId, System.StringComparison.Ordinal);
                    if (resolvedToDifferentTip)
                    {
                        ParsekLog.Info("MergeDialog",
                            $"TryCommitReFlySupersede: in-place continuation resolved " +
                            $"chain tip for supersede target: head={provisional.RecordingId} " +
                            $"-> tip={supersedeTargetRec.RecordingId} " +
                            $"chainId={supersedeTargetRec.ChainId ?? "<none>"} " +
                            $"chainIndex={supersedeTargetRec.ChainIndex} " +
                            $"tipTerminal={supersedeTargetRec.TerminalStateValue?.ToString() ?? "null"} " +
                            $"(post-RunOptimizationPass split moved terminal payload to tip; " +
                            $"validating + row-targeting the tip so AppendRelations does not " +
                            $"refuse the head's null-terminal invariant)");
                    }
                    else
                    {
                        ParsekLog.Verbose("MergeDialog",
                            $"TryCommitReFlySupersede: in-place continuation resolver audit " +
                            $"head={provisional.RecordingId} " +
                            $"sessionOwnedResolved={sessionOwnedResolved} " +
                            $"sessionOwnedSize={sessionOwnedSize} " +
                            $"sessionOwnedTip={(sessionOwnedTip != null ? sessionOwnedTip.RecordingId : "<null>")} " +
                            $"contiguousResolved={IsDifferentRecording(contiguousTip, provisional)} " +
                            $"contiguousSize={contiguousInPlaceChain.Count} " +
                            $"contiguousTip={(contiguousTip != null ? contiguousTip.RecordingId : "<null>")}");
                        ParsekLog.Verbose("MergeDialog",
                            $"TryCommitReFlySupersede: in-place continuation supersede target " +
                            $"unchanged from head={provisional.RecordingId} (no chain split, " +
                            $"or head already carries terminal)");
                    }

                    // Chain-skip set: protect only the in-place continuation
                    // segments created by this Re-Fly session. A same
                    // TreeId+ChainId+ChainBranch match alone is too broad:
                    // original optimizer tails from the pre-Re-Fly flight can
                    // share that identity and still need supersede rows to the
                    // new chain tip. The in-place origin is tagged at
                    // AtomicMarkerWrite; optimizer-created split children normally
                    // copy CreatingSessionId. Quickload/restore can lose those
                    // transient tags before merge finalization, so also protect
                    // the contiguous post-optimizer chain discovered from UT
                    // bounds. Older stale tails with the same chain identity are
                    // non-contiguous and still receive supersede rows.
                    var chainSkipSet = new HashSet<string>(System.StringComparer.Ordinal);
                    chainSkipSet.Add(provisional.RecordingId);
                    if (supersedeTargetRec != null
                        && !string.IsNullOrEmpty(supersedeTargetRec.RecordingId))
                    {
                        chainSkipSet.Add(supersedeTargetRec.RecordingId);
                    }
                    for (int i = 0; i < contiguousInPlaceChain.Count; i++)
                    {
                        var member = contiguousInPlaceChain[i];
                        if (member != null && !string.IsNullOrEmpty(member.RecordingId))
                            chainSkipSet.Add(member.RecordingId);
                    }
                    if (!string.IsNullOrEmpty(provisional.ChainId)
                        && !string.IsNullOrEmpty(provisional.TreeId))
                    {
                        var committedSource = RecordingStore.CommittedRecordings;
                        if (committedSource != null)
                        {
                            for (int i = 0; i < committedSource.Count; i++)
                            {
                                var cand = committedSource[i];
                                if (cand == null) continue;
                                if (string.IsNullOrEmpty(cand.RecordingId)) continue;
                                if (!string.Equals(
                                        cand.CreatingSessionId,
                                        marker.SessionId,
                                        System.StringComparison.Ordinal))
                                {
                                    continue;
                                }
                                // Match EnqueueChainSiblings' chain predicate
                                // after proving this candidate belongs to the
                                // current Re-Fly session.
                                if (!string.Equals(cand.TreeId,
                                    provisional.TreeId, System.StringComparison.Ordinal))
                                    continue;
                                if (!string.Equals(cand.ChainId,
                                    provisional.ChainId, System.StringComparison.Ordinal))
                                    continue;
                                if (cand.ChainBranch != provisional.ChainBranch) continue;
                                chainSkipSet.Add(cand.RecordingId);
                            }
                        }
                    }
                    // Verbose audit line so the chain-skip-set decision is
                    // reconstructable from KSP.log alone.
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    string chainSkipMembers = string.Join(",", chainSkipSet);
                    ParsekLog.Verbose("MergeDialog",
                        $"TryCommitReFlySupersede: chain-skip-set: " +
                        $"chainId={provisional.ChainId ?? "<none>"} " +
                        $"chainBranch={provisional.ChainBranch.ToString(ic)} " +
                        $"treeId={provisional.TreeId ?? "<none>"} " +
                        $"members=[{chainSkipMembers}] " +
                        $"head={provisional.RecordingId} " +
                        $"tip={(supersedeTargetRec != null ? supersedeTargetRec.RecordingId : "<none>")} " +
                        $"size={chainSkipSet.Count.ToString(ic)}");

                    int relationsBefore = scenario.RecordingSupersedes.Count;
                    SupersedeCommit.AppendRelations(
                        marker, supersedeTargetRec, scenario,
                        extraSelfSkipRecordingIds: chainSkipSet);
                    int relationsAfter = scenario.RecordingSupersedes.Count;
                    ParsekLog.Info("MergeDialog",
                        $"TryCommitReFlySupersede: in-place continuation supersede append " +
                        $"wrote {relationsAfter - relationsBefore} relation(s) " +
                        $"(before={relationsBefore} after={relationsAfter}; self-link skipped " +
                        $"by AppendRelations old==new guard; full chain " +
                        $"({chainSkipSet.Count} member(s)) skipped via extra-self-skip set so " +
                        $"no in-place chain segment is collapsed under another via supersede)");

                    // FlipMergeStateAndClearTransient with preserveMarker=false
                    // flips MergeState, clears SupersedeTargetId, bumps
                    // SupersedeStateVersion, clears the ActiveReFlySessionMarker
                    // (and bumps again), and logs the End reason=merged line.
                    // That covers all the in-memory scenario mutations that a
                    // normal RunMerge does around AppendRelations + tombstones.
                    // Also clear CreatingSessionId / ProvisionalForRpId on every
                    // protected continuation segment so split children no longer
                    // look like session-scoped zombies to load-time sweep.
                    var committedForTransientClear = RecordingStore.CommittedRecordings;
                    if (committedForTransientClear != null)
                    {
                        for (int i = 0; i < committedForTransientClear.Count; i++)
                        {
                            var cand = committedForTransientClear[i];
                            if (cand == null || string.IsNullOrEmpty(cand.RecordingId))
                                continue;
                            if (!chainSkipSet.Contains(cand.RecordingId))
                                continue;
                            cand.CreatingSessionId = null;
                            cand.ProvisionalForRpId = null;
                        }
                    }
                    SupersedeCommit.FlipMergeStateAndClearTransient(
                        marker, provisional, scenario, preserveMarker: false);

                    // SupersedeCommit owns the safety decision. If it left the
                    // recording CommittedProvisional, the slot is deliberately
                    // still re-flyable (terminal failure, safe stashed stable
                    // leaf, or safe non-focus orbit/suborbit leaf). If it closed
                    // the slot, it already wrote Immutable and any needed seal.
                    if (provisional.MergeState != MergeState.Immutable)
                    {
                        ParsekLog.Info("MergeDialog",
                            $"TryCommitReFlySupersede: in-place continuation retained " +
                            $"MergeState {provisional.MergeState} on {provisional.RecordingId} " +
                            "(slot remains re-flyable)");
                    }
                    ClearStashedSlotForInPlaceCommit(provisional, scenario);

                    // The in-place path bypasses MergeJournalOrchestrator.RunMerge,
                    // so mirror its RP promotion step immediately before the
                    // reaper. Do this after marker/state finalization succeeds:
                    // the local marker still carries SessionId even though the
                    // scenario marker was cleared above.
                    ParsekLog.Info("MergeDialog",
                        $"TryCommitReFlySupersede: in-place continuation tagging " +
                        $"session-provisional RPs for reap after marker clear " +
                        $"sess={marker.SessionId ?? "<no-id>"}");
                    MergeJournalOrchestrator.TagRpsForReap(marker, scenario);

                    // Reap any RP whose slots are now closed. Safety-closed
                    // in-place outcomes are Immutable and eligible here; open
                    // safe stable or terminal-failure outcomes remain for
                    // another retry.
                    int reapedCount;
                    try
                    {
                        reapedCount = RewindPointReaper.ReapOrphanedRPs();
                    }
                    catch (System.Exception reapEx)
                    {
                        // Reap is best-effort: a failure here leaves the RP
                        // around for LoadTimeSweep / next reap pass to
                        // collect, so we log + continue rather than rolling
                        // the merge back. The MergeState flip + marker
                        // clear above are already durable in memory and
                        // will be persisted by the SaveGame below.
                        reapedCount = 0;
                        ParsekLog.Warn("MergeDialog",
                            $"TryCommitReFlySupersede: in-place continuation post-merge reap threw " +
                            $"{reapEx.GetType().Name}: {reapEx.Message} — leaving RP for next sweep");
                    }
                    ParsekLog.Info("MergeDialog",
                        $"TryCommitReFlySupersede: in-place continuation reaped " +
                        $"{reapedCount} orphaned RP(s) post-merge");

                    // Mirror the orchestrator's Durable Save #1 barrier so
                    // the flipped MergeState + cleared marker + reaped RP
                    // survive a quit/reload right after the merge dialog.
                    if (HighLogic.CurrentGame != null
                        && !string.IsNullOrEmpty(HighLogic.SaveFolder))
                    {
                        GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                        ParsekLog.Info("MergeDialog",
                            "TryCommitReFlySupersede: in-place continuation persisted via persistent.sfs");
                    }
                    else
                    {
                        ParsekLog.Verbose("MergeDialog",
                            "TryCommitReFlySupersede: in-place continuation skipped durable save " +
                            "(no HighLogic.CurrentGame / SaveFolder — test harness or pre-scene path)");
                    }
                    return ReFlyMergeCommitResult.Completed;
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Error("MergeDialog",
                        $"TryCommitReFlySupersede: in-place continuation finalization threw " +
                        $"{ex.GetType().Name}: {ex.Message} — marker left in place for load-time sweep");
                    ParsekLog.ScreenMessage(
                        "Merge interrupted — will finish on next load", 3f);
                    return ReFlyMergeCommitResult.Interrupted;
                }
            }

            ParsekLog.Info("MergeDialog",
                $"TryCommitReFlySupersede: invoking MergeJournalOrchestrator for " +
                $"sess={marker.SessionId ?? "<no-id>"} provisional={provisionalId} " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}");

            bool ok;
            try
            {
                ok = MergeJournalOrchestrator.RunMerge(marker, provisional);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("MergeDialog",
                    $"TryCommitReFlySupersede: orchestrator threw {ex.GetType().Name}: {ex.Message} — " +
                    $"journal will drive recovery on next load");
                ParsekLog.ScreenMessage(
                    "Merge interrupted — will finish on next load", 3f);
                return ReFlyMergeCommitResult.Interrupted;
            }

            if (!ok)
            {
                ParsekLog.Error("MergeDialog",
                    $"TryCommitReFlySupersede: orchestrator returned false for " +
                    $"sess={marker.SessionId ?? "<no-id>"} provisional={provisionalId}");
                ParsekLog.ScreenMessage("Merge commit skipped (see log)", 3f);
                return ReFlyMergeCommitResult.Interrupted;
            }

            return ReFlyMergeCommitResult.Completed;
        }

        private static void ClearStashedSlotForInPlaceCommit(
            Recording provisional,
            ParsekScenario scenario)
        {
            if (provisional == null || object.ReferenceEquals(null, scenario))
                return;

            if (provisional.MergeState != MergeState.Immutable)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"TryCommitReFlySupersede: in-place continuation kept stashed " +
                    $"slot rec={provisional.RecordingId ?? "<no-id>"} " +
                    $"mergeState={provisional.MergeState} (slot remains re-flyable)");
                return;
            }

            RewindPoint rp;
            int slotListIndex;
            string rejectReason;
            if (!UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rp, out slotListIndex, out rejectReason))
            {
                ParsekLog.Verbose("MergeDialog",
                    $"TryCommitReFlySupersede: in-place continuation could not " +
                    $"resolve stashed slot to clear rec={provisional.RecordingId ?? "<no-id>"} " +
                    $"reason={rejectReason ?? "<none>"}");
                return;
            }

            if (rp?.ChildSlots == null
                || slotListIndex < 0
                || slotListIndex >= rp.ChildSlots.Count)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"TryCommitReFlySupersede: in-place continuation could not " +
                    $"clear stashed slot rec={provisional.RecordingId ?? "<no-id>"} " +
                    $"rp={rp?.RewindPointId ?? "<no-rp>"} slot={slotListIndex} " +
                    $"reason=slot-index-invalid");
                return;
            }

            var slot = rp.ChildSlots[slotListIndex];
            if (slot == null)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"TryCommitReFlySupersede: in-place continuation could not " +
                    $"clear stashed slot rec={provisional.RecordingId ?? "<no-id>"} " +
                    $"rp={rp.RewindPointId ?? "<no-rp>"} slot={slotListIndex} " +
                    $"reason=slot-null");
                return;
            }

            if (!slot.Stashed)
                return;

            slot.Stashed = false;
            slot.StashedRealTime = null;
            scenario.BumpSupersedeStateVersion();
            ParsekLog.Info("MergeDialog",
                $"TryCommitReFlySupersede: in-place continuation cleared stashed " +
                $"slot={slotListIndex} rec={provisional.RecordingId ?? "<no-id>"} " +
                $"rp={rp.RewindPointId ?? "<no-rp>"} " +
                "(merge is the player's commitment; no separate provisional exists)");
        }

        // Raw committed-list scan by id. Kept local to the merge path so we
        // don't need another allowlist entry; the recording we're hunting for
        // is the provisional that was added by RewindInvoker.AddProvisional
        // (NotCommitted, filtered out of ERS), so ERS routing is not the right
        // lookup.
        private static Recording FindCommittedRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, System.StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        #region Extracted helpers

        /// <summary>
        /// Pure function: compute the total time span across all recordings in a tree.
        /// Returns 0 if the tree has no recordings.
        /// </summary>
        internal static double ComputeTreeDurationRange(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            double minStartUT = double.MaxValue;
            double maxEndUT = double.MinValue;
            foreach (var rec in tree.Recordings.Values)
            {
                double start = rec.StartUT;
                double end = rec.EndUT;
                if (start < minStartUT) minStartUT = start;
                if (end > maxEndUT) maxEndUT = end;
            }

            return (minStartUT < double.MaxValue && maxEndUT > double.MinValue)
                ? maxEndUT - minStartUT
                : 0;
        }

        #endregion

        // ================================================================
        // Per-vessel persist/ghost-only decisions
        // ================================================================

        /// <summary>
        /// Determines whether a recording can actually spawn as a real vessel after merge.
        /// This reuses the same intrinsic spawn policy as timeline playback so the dialog's
        /// default "spawnable" count stays aligned with runtime behavior.
        /// </summary>
        internal static bool CanPersistVessel(Recording rec, RecordingTree treeContext = null)
        {
            if (rec == null)
                return false;

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLooping: false,
                treeContext: treeContext);
            return needsSpawn;
        }

        /// <summary>
        /// Builds default persist/ghost-only decisions for all leaf recordings in a tree.
        /// Surviving vessels default to persist (true), destroyed/recovered default to ghost-only (false).
        /// Keys are RecordingId. Pure static for testability.
        /// </summary>
        internal static Dictionary<string, bool> BuildDefaultVesselDecisions(RecordingTree tree)
            => BuildDefaultVesselDecisions(tree, null, null);

        /// <summary>
        /// Builds default persist/ghost-only decisions for a tree, additionally
        /// forcing every non-leaf recording listed in
        /// <paramref name="suppressedRecordingIds"/> to ghost-only — except the
        /// recording identified by <paramref name="activeReFlyTargetId"/>, which
        /// is the live Re-Fly target the player is currently flying and must
        /// stay spawnable.
        ///
        /// <para>
        /// Bug fix (refly-suppressed-non-leaf): without this branch a parent
        /// recording in the suppressed subtree (e.g. an upper stage that has
        /// child decoupling/breakup branches) keeps <c>VesselSnapshot</c> set,
        /// and <c>GhostPlaybackLogic.ShouldSpawnAtRecordingEnd</c> later spawns
        /// it as a clickable real vessel alongside the playback ghost. The
        /// only branches the leaf walk reaches are chain tips, so non-leaf
        /// suppressed recordings would otherwise be silently retained.
        /// </para>
        /// </summary>
        internal static Dictionary<string, bool> BuildDefaultVesselDecisions(
            RecordingTree tree,
            HashSet<string> suppressedRecordingIds,
            string activeReFlyTargetId)
        {
            var decisions = new Dictionary<string, bool>();
            if (tree == null)
                return decisions;

            var leaves = tree.GetAllLeaves();
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i];
                bool canPersist = CanPersistVessel(leaf, tree);
                decisions[leaf.RecordingId] = canPersist;
                ParsekLog.Verbose("MergeDialog",
                    $"BuildDefaultVesselDecisions: leaf='{leaf.RecordingId}' vessel='{leaf.VesselName}' " +
                    $"terminal={leaf.TerminalStateValue?.ToString() ?? "null"} " +
                    $"hasSnapshot={leaf.VesselSnapshot != null} canPersist={canPersist}");
            }

            // Bug #271: in always-tree mode with breakup-continuous design, the active
            // recording may be non-leaf (it has ChildBranchPointId from breakup branches)
            // but is still the main vessel's recording that should be spawnable. Include
            // it in decisions if it wasn't already covered as a leaf.
            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && !decisions.ContainsKey(tree.ActiveRecordingId))
            {
                Recording activeRec;
                if (tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec))
                {
                    bool canPersist = CanPersistVessel(activeRec, tree);
                    decisions[activeRec.RecordingId] = canPersist;
                    ParsekLog.Verbose("MergeDialog",
                        $"BuildDefaultVesselDecisions: active-nonleaf='{activeRec.RecordingId}' " +
                        $"vessel='{activeRec.VesselName}' " +
                        $"terminal={activeRec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"hasSnapshot={activeRec.VesselSnapshot != null} canPersist={canPersist}");
                }
            }

            // Re-Fly suppressed-subtree pass: for every recording whose id appears
            // in the suppression closure, force ghost-only — even if it is a
            // non-leaf the leaf walk above never visited. The sole exception is
            // the live Re-Fly target itself, which must stay spawnable. Note we
            // walk the closure ids (not tree.Recordings) because the closure is
            // computed against committed recordings and may name records that
            // are not in the pending tree (chain siblings, etc.); we only act
            // when the id ALSO exists in this tree.
            if (suppressedRecordingIds != null && suppressedRecordingIds.Count > 0)
            {
                int forced = 0;
                int skippedActiveTarget = 0;
                int alreadyGhostOnly = 0;
                int notInTree = 0;
                foreach (string suppressedId in suppressedRecordingIds)
                {
                    if (string.IsNullOrEmpty(suppressedId)) continue;
                    if (!string.IsNullOrEmpty(activeReFlyTargetId)
                        && string.Equals(suppressedId, activeReFlyTargetId, System.StringComparison.Ordinal))
                    {
                        skippedActiveTarget++;
                        ParsekLog.Verbose("MergeDialog",
                            $"BuildDefaultVesselDecisions: keeping active Re-Fly target spawnable " +
                            $"id='{suppressedId}' (in suppressed subtree but is the live target)");
                        continue;
                    }
                    Recording rec;
                    if (!tree.Recordings.TryGetValue(suppressedId, out rec) || rec == null)
                    {
                        notInTree++;
                        continue;
                    }
                    bool wasGhostOnly;
                    if (decisions.TryGetValue(suppressedId, out wasGhostOnly) && !wasGhostOnly)
                    {
                        alreadyGhostOnly++;
                        continue;
                    }
                    decisions[suppressedId] = false;
                    forced++;
                    ParsekLog.Info("MergeDialog",
                        $"BuildDefaultVesselDecisions: forcing ghost-only on suppressed " +
                        $"id='{suppressedId}' vessel='{rec.VesselName}' " +
                        $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"isLeaf={rec.ChildBranchPointId == null} " +
                        $"priorDecision={(decisions.ContainsKey(suppressedId) ? "set" : "unset")}");
                }
                ParsekLog.Info("MergeDialog",
                    $"BuildDefaultVesselDecisions: suppressed-subtree pass complete " +
                    $"closureSize={suppressedRecordingIds.Count} forcedGhostOnly={forced} " +
                    $"skippedActiveTarget={skippedActiveTarget} alreadyGhostOnly={alreadyGhostOnly} " +
                    $"notInTree={notInTree} activeTarget='{activeReFlyTargetId ?? "<none>"}'");
            }

            ApplyActiveReFlyParentChainDefaults(tree, decisions, activeReFlyTargetId);

            return decisions;
        }

        internal static Recording ResolveSessionOwnedChainTerminalRecording(
            Recording provisional,
            string sessionId)
        {
            if (provisional == null || string.IsNullOrEmpty(sessionId))
                return provisional;
            if (string.IsNullOrEmpty(provisional.ChainId)
                || string.IsNullOrEmpty(provisional.TreeId))
            {
                return provisional;
            }

            Recording best = null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var cand = committed[i];
                    if (cand == null || string.IsNullOrEmpty(cand.RecordingId))
                        continue;
                    if (!string.Equals(cand.CreatingSessionId, sessionId, System.StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(cand.TreeId, provisional.TreeId, System.StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(cand.ChainId, provisional.ChainId, System.StringComparison.Ordinal))
                        continue;
                    if (cand.ChainBranch != provisional.ChainBranch)
                        continue;
                    if (best == null || cand.ChainIndex > best.ChainIndex)
                        best = cand;
                }
            }

            return best ?? provisional;
        }

        private static int CountSessionOwnedChainMembers(
            Recording provisional,
            string sessionId)
        {
            if (provisional == null || string.IsNullOrEmpty(sessionId))
                return 0;
            if (string.IsNullOrEmpty(provisional.ChainId)
                || string.IsNullOrEmpty(provisional.TreeId))
            {
                return 0;
            }

            int count = 0;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return 0;

            for (int i = 0; i < committed.Count; i++)
            {
                var cand = committed[i];
                if (cand == null || string.IsNullOrEmpty(cand.RecordingId))
                    continue;
                if (!string.Equals(cand.CreatingSessionId, sessionId, System.StringComparison.Ordinal))
                    continue;
                if (!string.Equals(cand.TreeId, provisional.TreeId, System.StringComparison.Ordinal))
                    continue;
                if (!string.Equals(cand.ChainId, provisional.ChainId, System.StringComparison.Ordinal))
                    continue;
                if (cand.ChainBranch != provisional.ChainBranch)
                    continue;
                count++;
            }

            return count;
        }

        internal static List<Recording> ResolveContiguousInPlaceChainMembers(
            Recording provisional)
        {
            var members = new List<Recording>();
            if (provisional == null)
                return members;

            if (!string.IsNullOrEmpty(provisional.RecordingId))
                members.Add(provisional);

            if (string.IsNullOrEmpty(provisional.ChainId)
                || string.IsNullOrEmpty(provisional.TreeId))
            {
                return members;
            }

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                return members;

            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(provisional.RecordingId))
                visited.Add(provisional.RecordingId);

            Recording current = provisional;
            while (current != null)
            {
                Recording next = FindNextContiguousInPlaceChainMember(
                    provisional, current, committed, visited);
                if (next == null)
                    break;

                members.Add(next);
                visited.Add(next.RecordingId);
                current = next;
            }

            return members;
        }

        private static Recording FindNextContiguousInPlaceChainMember(
            Recording head,
            Recording current,
            IReadOnlyList<Recording> committed,
            HashSet<string> visited)
        {
            if (head == null || current == null || committed == null)
                return null;
            if (!TryGetRecordingBounds(current, out _, out double currentEndUT))
                return null;

            Recording best = null;
            double bestDelta = double.MaxValue;
            for (int i = 0; i < committed.Count; i++)
            {
                var candidate = committed[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                if (visited != null && visited.Contains(candidate.RecordingId))
                    continue;
                if (!MatchesInPlaceChain(head, candidate))
                    continue;
                if (candidate.ChainIndex <= current.ChainIndex)
                    continue;
                if (!TryGetRecordingBounds(candidate, out double candidateStartUT, out _))
                    continue;

                double delta = System.Math.Abs(candidateStartUT - currentEndUT);
                if (delta > InPlaceChainContinuityToleranceSeconds)
                    continue;

                if (best == null
                    || delta < bestDelta
                    || (System.Math.Abs(delta - bestDelta) <= 1e-9
                        && candidate.ChainIndex < best.ChainIndex))
                {
                    best = candidate;
                    bestDelta = delta;
                }
            }

            return best;
        }

        private static bool MatchesInPlaceChain(Recording head, Recording candidate)
        {
            if (head == null || candidate == null)
                return false;
            if (string.IsNullOrEmpty(head.TreeId) || string.IsNullOrEmpty(head.ChainId))
                return false;
            if (!string.Equals(candidate.TreeId, head.TreeId, System.StringComparison.Ordinal))
                return false;
            if (!string.Equals(candidate.ChainId, head.ChainId, System.StringComparison.Ordinal))
                return false;
            return candidate.ChainBranch == head.ChainBranch;
        }

        private static bool TryGetRecordingBounds(
            Recording recording,
            out double startUT,
            out double endUT)
        {
            startUT = 0.0;
            endUT = 0.0;
            if (recording == null)
                return false;

            bool hasPayload =
                (recording.Points != null && recording.Points.Count > 0)
                || (recording.TrackSections != null && recording.TrackSections.Count > 0)
                || (recording.OrbitSegments != null && recording.OrbitSegments.Count > 0)
                || (!double.IsNaN(recording.ExplicitStartUT)
                    && !double.IsNaN(recording.ExplicitEndUT));
            if (!hasPayload)
                return false;

            startUT = recording.StartUT;
            endUT = recording.EndUT;
            return IsFinite(startUT) && IsFinite(endUT);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsDifferentRecording(Recording candidate, Recording head)
        {
            if (candidate == null || head == null)
                return false;
            if (object.ReferenceEquals(candidate, head))
                return false;
            return !string.Equals(
                candidate.RecordingId, head.RecordingId,
                System.StringComparison.Ordinal);
        }

        private static string FormatRecordingIds(List<Recording> recordings)
        {
            if (recordings == null || recordings.Count == 0)
                return string.Empty;

            var ids = new List<string>();
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                ids.Add(rec.RecordingId);
            }

            return string.Join(",", ids);
        }

        internal static Recording EnsureInPlaceSupersedeTargetHasTerminalState(
            Recording supersedeTarget,
            string sessionId)
        {
            if (supersedeTarget == null || supersedeTarget.TerminalStateValue.HasValue)
                return supersedeTarget;

            if (supersedeTarget.SceneExitSituation >= 0)
            {
                supersedeTarget.TerminalStateValue =
                    RecordingTree.DetermineTerminalState(supersedeTarget.SceneExitSituation);
                RecordingEndpointResolver.RefreshEndpointDecision(
                    supersedeTarget,
                    "MergeDialog.InPlaceSupersedeTargetSceneExitRepair");
                ParsekLog.Warn("MergeDialog",
                    $"TryCommitReFlySupersede: repaired null terminal on in-place supersede target " +
                    $"id={supersedeTarget.RecordingId} from SceneExitSituation={supersedeTarget.SceneExitSituation} " +
                    $"terminal={supersedeTarget.TerminalStateValue} sess={sessionId ?? "<none>"}");
                return supersedeTarget;
            }

            ParsekLog.Warn("MergeDialog",
                $"TryCommitReFlySupersede: in-place supersede target " +
                $"id={supersedeTarget.RecordingId ?? "<null>"} still has null terminal and no " +
                $"SceneExitSituation repair source (sess={sessionId ?? "<none>"})");
            return supersedeTarget;
        }

        private static void ApplyActiveReFlyParentChainDefaults(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId)
        {
            // This runs only while BuildDefaultVesselDecisions is constructing
            // the dialog's initial defaults. It is allowed to flip a freshly
            // inferred spawnable parent-chain tip to ghost-only; there is no
            // user-edited decision state yet.
            if (tree == null || decisions == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return;

            var parentTips = CollectActiveReFlyParentChainTerminalTipIds(
                tree, activeReFlyTargetId);
            if (parentTips == null || parentTips.Count == 0)
                return;

            int forced = 0;
            int alreadyGhostOnly = 0;
            int missing = 0;

            foreach (string tipId in parentTips)
            {
                if (string.IsNullOrEmpty(tipId)) continue;

                Recording rec;
                if (!tree.Recordings.TryGetValue(tipId, out rec) || rec == null)
                {
                    missing++;
                    continue;
                }

                bool priorDecision;
                bool hadPriorDecision = decisions.TryGetValue(tipId, out priorDecision);
                if (hadPriorDecision && !priorDecision)
                {
                    alreadyGhostOnly++;
                    continue;
                }

                decisions[tipId] = false;
                forced++;
                ParsekLog.Info("MergeDialog",
                    $"BuildDefaultVesselDecisions: defaulting active Re-Fly parent-chain " +
                    $"terminal tip to ghost-only id='{tipId}' vessel='{rec.VesselName}' " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                    $"hasSnapshot={rec.VesselSnapshot != null} " +
                    $"priorDecision={(hadPriorDecision ? "set" : "unset")} " +
                    $"activeTarget='{activeReFlyTargetId}'");
            }

            ParsekLog.Info("MergeDialog",
                $"BuildDefaultVesselDecisions: active Re-Fly parent-chain pass complete " +
                $"candidates={parentTips.Count} forcedGhostOnly={forced} " +
                $"alreadyGhostOnly={alreadyGhostOnly} missing={missing} " +
                $"activeTarget='{activeReFlyTargetId}'");
        }

        internal static HashSet<string> CollectActiveReFlyParentChainTerminalTipIds(
            RecordingTree tree,
            string activeReFlyTargetId)
        {
            var result = new HashSet<string>(System.StringComparer.Ordinal);
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return result;

            Recording activeRec;
            if (!tree.Recordings.TryGetValue(activeReFlyTargetId, out activeRec) || activeRec == null)
                return result;

            HashSet<string> activeChainIds = CollectSameChainRecordingIds(tree, activeRec);
            var parentBranchPointIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string activeChainId in activeChainIds)
            {
                Recording chainMember;
                if (string.IsNullOrEmpty(activeChainId)
                    || !tree.Recordings.TryGetValue(activeChainId, out chainMember)
                    || chainMember == null)
                    continue;
                if (!string.IsNullOrEmpty(chainMember.ParentBranchPointId))
                    parentBranchPointIds.Add(chainMember.ParentBranchPointId);
            }

            foreach (string parentBpId in parentBranchPointIds)
            {
                BranchPoint bp = FindBranchPoint(tree, parentBpId);
                if (bp == null)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: parent bp '{parentBpId}' missing " +
                        $"for activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                if (bp.ParentRecordingIds == null || bp.ParentRecordingIds.Count != 1)
                {
                    int parentCount = bp.ParentRecordingIds != null ? bp.ParentRecordingIds.Count : 0;
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip bp={bp.Id} " +
                        $"parentCount={parentCount} activeTarget='{activeReFlyTargetId}' " +
                        "(not a single-parent old-future branch)");
                    continue;
                }

                string parentId = bp.ParentRecordingIds[0];
                if (string.IsNullOrEmpty(parentId) || activeChainIds.Contains(parentId))
                    continue;

                Recording parentRec;
                if (!tree.Recordings.TryGetValue(parentId, out parentRec) || parentRec == null)
                    continue;

                Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(parentRec, tree);
                if (terminalRec == null || string.IsNullOrEmpty(terminalRec.RecordingId))
                    continue;

                if (activeChainIds.Contains(terminalRec.RecordingId)
                    || string.Equals(terminalRec.RecordingId, activeReFlyTargetId,
                        System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (terminalRec.VesselPersistentId != 0u
                    && activeRec.VesselPersistentId != 0u
                    && terminalRec.VesselPersistentId == activeRec.VesselPersistentId)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip parent terminal " +
                        $"id='{terminalRec.RecordingId}' because it shares vessel pid " +
                        $"{terminalRec.VesselPersistentId} with activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                if (!IsTerminalLinkedToParentBranch(bp, parentRec, terminalRec))
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip parent terminal " +
                        $"id='{terminalRec.RecordingId}' for bp={bp.Id}; terminal is not linked " +
                        "to the active target's parent branch");
                    continue;
                }

                result.Add(terminalRec.RecordingId);
            }

            return result;
        }

        private static HashSet<string> CollectSameChainRecordingIds(
            RecordingTree tree,
            Recording rec)
        {
            var result = new HashSet<string>(System.StringComparer.Ordinal);
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return result;

            result.Add(rec.RecordingId);
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(rec.ChainId))
                return result;

            foreach (var cand in tree.Recordings.Values)
            {
                if (cand == null || string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (!string.Equals(cand.ChainId, rec.ChainId, System.StringComparison.Ordinal)) continue;
                if (cand.ChainBranch != rec.ChainBranch) continue;
                if (!string.IsNullOrEmpty(rec.TreeId)
                    && !string.Equals(cand.TreeId, rec.TreeId, System.StringComparison.Ordinal))
                    continue;
                result.Add(cand.RecordingId);
            }

            return result;
        }

        private static bool IsTerminalLinkedToParentBranch(
            BranchPoint bp,
            Recording parentRec,
            Recording terminalRec)
        {
            if (bp == null || parentRec == null || terminalRec == null)
                return false;
            if (object.ReferenceEquals(parentRec, terminalRec))
                return true;
            if (string.Equals(parentRec.ChildBranchPointId, bp.Id, System.StringComparison.Ordinal))
                return true;
            if (string.Equals(terminalRec.ChildBranchPointId, bp.Id, System.StringComparison.Ordinal))
                return true;
            if (bp.ParentRecordingIds != null)
            {
                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                {
                    if (string.Equals(bp.ParentRecordingIds[i], terminalRec.RecordingId,
                        System.StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        private static BranchPoint FindBranchPoint(RecordingTree tree, string branchPointId)
        {
            if (tree == null || tree.BranchPoints == null || string.IsNullOrEmpty(branchPointId))
                return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp != null && string.Equals(bp.Id, branchPointId, System.StringComparison.Ordinal))
                    return bp;
            }
            return null;
        }

        /// <summary>
        /// Applies vessel decisions to the tree: nulls VesselSnapshot on recordings
        /// that are marked ghost-only (false in decisions dict).
        /// </summary>
        static void ApplyVesselDecisions(RecordingTree tree, Dictionary<string, bool> decisions)
        {
            if (tree == null || decisions == null)
                return;

            foreach (var kvp in decisions)
            {
                if (!kvp.Value) // ghost-only
                {
                    Recording rec;
                    if (tree.Recordings.TryGetValue(kvp.Key, out rec))
                    {
                        if (rec.VesselSnapshot != null)
                        {
                            // Preserve GhostVisualSnapshot for ghost rendering if not already set
                            if (rec.GhostVisualSnapshot == null)
                                rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                            rec.VesselSnapshot = null;
                            ParsekLog.Info("MergeDialog",
                                $"ApplyVesselDecisions: ghost-only for '{rec.VesselName}' (id={kvp.Key}), " +
                                $"spawn snapshot nulled, ghostVisual={rec.GhostVisualSnapshot != null}");
                        }
                    }
                }
            }
        }

    }
}
