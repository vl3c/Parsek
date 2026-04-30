using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// Phase 11 of Rewind-to-Staging (design §3.5 invariant 7 / §6.10 /
    /// §10.1): when an entire <see cref="RecordingTree"/> is discarded,
    /// purge every piece of cross-recording state that referenced the
    /// tree's recordings. Tree discard is the ONLY purge path for these
    /// append-only lists — supersede relations and ledger tombstones are
    /// never removed individually.
    ///
    /// <para>
    /// Purged per tree:
    /// <list type="bullet">
    ///   <item><description><see cref="RewindPoint"/>s whose <c>BranchPointId</c> belongs to a branch point in the tree (scenario entry + quicksave file + <see cref="BranchPoint.RewindPointId"/> back-ref).</description></item>
    ///   <item><description><see cref="RecordingSupersedeRelation"/>s where either endpoint's recording id belongs to the tree.</description></item>
    ///   <item><description><see cref="LedgerTombstone"/>s whose target <see cref="GameAction"/> carries a <see cref="GameAction.RecordingId"/> in the tree.</description></item>
    ///   <item><description>Crew reservations owned by recordings in the tree (via <see cref="CrewReservationManager.RecomputeAfterTombstones"/> after the tombstone list shrinks).</description></item>
    ///   <item><description><see cref="ParsekScenario.ActiveReFlySessionMarker"/> + <see cref="ParsekScenario.ActiveMergeJournal"/> if the active re-fly session is scoped to the discarded tree.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Callers invoke <see cref="PurgeTree"/> BEFORE the tree's recordings
    /// are removed from <see cref="RecordingStore.CommittedRecordings"/> so
    /// the purge can still resolve ids to recordings for the in-tree set.
    /// </para>
    /// </summary>
    internal static class TreeDiscardPurge
    {
        private const string RewindTag = "Rewind";
        private const string SupersedeTag = "Supersede";
        private const string LedgerSwapTag = "LedgerSwap";
        private const string CrewTag = "CrewReservations";
        private const string SessionTag = "ReFlySession";
        private const string JournalTag = "MergeJournal";

        /// <summary>
        /// Test seam: mirrors <see cref="RewindPointReaper.DeleteQuicksaveForTesting"/>.
        /// Non-null overrides the real file-delete.
        /// </summary>
        internal static Func<string, bool> DeleteQuicksaveForTesting;

        /// <summary>
        /// Test seam: counter of every <see cref="PurgeTree"/> invocation,
        /// incremented unconditionally at the top of the method (before the
        /// null/empty guards or scenario lookup). Lets tests detect whether
        /// <see cref="PurgeTree"/> was even ATTEMPTED irrespective of whether
        /// the body ran to completion. Reset via
        /// <see cref="ResetCallCountForTesting"/> or <see cref="ResetTestOverrides"/>.
        /// </summary>
        internal static int PurgeTreeCountForTesting;

        /// <summary>Resets the <see cref="PurgeTreeCountForTesting"/> counter.</summary>
        internal static void ResetCallCountForTesting()
        {
            PurgeTreeCountForTesting = 0;
        }

        /// <summary>Clears all test seams.</summary>
        internal static void ResetTestOverrides()
        {
            DeleteQuicksaveForTesting = null;
            PurgeTreeCountForTesting = 0;
        }

        /// <summary>
        /// Purges all Phase 11 state tied to the tree identified by
        /// <paramref name="treeId"/>. No-op (with Verbose log) when the
        /// scenario instance is missing or the tree id is blank / unknown.
        /// </summary>
        internal static void PurgeTree(string treeId)
        {
            // Unconditional attempt counter — increments even on early-return
            // guard hits below, so tests can assert "PurgeTree was never
            // called" via PurgeTreeCountForTesting == 0.
            PurgeTreeCountForTesting++;

            if (string.IsNullOrEmpty(treeId))
            {
                ParsekLog.Verbose(RewindTag, "PurgeTree: empty treeId — nothing to purge");
                return;
            }

            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose(RewindTag,
                    $"PurgeTree: no ParsekScenario instance — skipping purge for tree={treeId}");
                return;
            }

            RecordingTree tree = FindTree(treeId);
            if (tree == null)
            {
                ParsekLog.Verbose(RewindTag,
                    $"PurgeTree: tree={treeId} not found in committed or pending — nothing to purge");
                return;
            }

            // Collect the tree's membership sets once up-front so the
            // individual purge passes can answer "is X in the tree?" in O(1).
            var branchPointIds = CollectBranchPointIds(tree);
            var recordingIds = CollectRecordingIds(tree);

            int rpsPurged = PurgeRewindPoints(scenario, branchPointIds, treeId);
            int supersedesPurged = PurgeSupersedeRelations(scenario, recordingIds, treeId);
            int tombstonesPurged = PurgeLedgerTombstones(scenario, recordingIds, treeId);

            // Recompute reservations whenever tombstones shrunk (kerbals in
            // the tree may have had death-tombstones that now vanish; ELS
            // changes).
            if (tombstonesPurged > 0)
            {
                CrewReservationManager.RecomputeAfterTombstones();
                ParsekLog.Info(CrewTag,
                    $"PurgeTree: recomputed reservations after {tombstonesPurged.ToString(CultureInfo.InvariantCulture)} tombstone(s) purged for tree={treeId}");
            }
            else
            {
                ParsekLog.Verbose(CrewTag,
                    $"PurgeTree: no tombstones purged for tree={treeId} — reservations untouched");
            }

            bool markerCleared = ClearMarkerIfScopedToTree(scenario, treeId);
            bool journalCleared = ClearJournalIfScopedToTree(scenario, treeId);

            if (supersedesPurged > 0) scenario.BumpSupersedeStateVersion();
            if (tombstonesPurged > 0) scenario.BumpTombstoneStateVersion();

            ParsekLog.Info(RewindTag,
                $"PurgeTree: tree={treeId} rps={rpsPurged.ToString(CultureInfo.InvariantCulture)} " +
                $"supersedes={supersedesPurged.ToString(CultureInfo.InvariantCulture)} " +
                $"tombstones={tombstonesPurged.ToString(CultureInfo.InvariantCulture)} " +
                $"markerCleared={markerCleared.ToString()} journalCleared={journalCleared.ToString()}");
        }

        // ------------------------------------------------------------------
        // Tree lookup + membership collection
        // ------------------------------------------------------------------

        private static RecordingTree FindTree(string treeId)
        {
            var committed = RecordingStore.CommittedTrees;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var t = committed[i];
                    if (t == null) continue;
                    if (string.Equals(t.Id, treeId, StringComparison.Ordinal))
                        return t;
                }
            }

            var pending = RecordingStore.PendingTree;
            if (pending != null && string.Equals(pending.Id, treeId, StringComparison.Ordinal))
                return pending;

            return null;
        }

        private static HashSet<string> CollectBranchPointIds(RecordingTree tree)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.BranchPoints == null) return set;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (!string.IsNullOrEmpty(bp.Id))
                    set.Add(bp.Id);
            }
            return set;
        }

        private static HashSet<string> CollectRecordingIds(RecordingTree tree)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.Recordings == null) return set;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null) continue;
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    set.Add(rec.RecordingId);
            }
            return set;
        }

        // ------------------------------------------------------------------
        // RP purge
        // ------------------------------------------------------------------

        private static int PurgeRewindPoints(
            ParsekScenario scenario, HashSet<string> branchPointIds, string treeId)
        {
            if (scenario.RewindPoints == null || scenario.RewindPoints.Count == 0)
                return 0;

            int purged = 0;
            for (int i = scenario.RewindPoints.Count - 1; i >= 0; i--)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null) continue;
                if (string.IsNullOrEmpty(rp.BranchPointId)) continue;
                if (!branchPointIds.Contains(rp.BranchPointId)) continue;

                // Best-effort quicksave delete.
                TryDeleteQuicksaveFile(rp);

                // Remove scenario entry.
                scenario.RewindPoints.RemoveAt(i);
                RecordingsTableUI.ClearRewindSlotCanInvokeLogState(rp.RewindPointId);

                // Clear back-ref on the owning BranchPoint (the BP stays in
                // the tree and will be removed when the tree is discarded
                // downstream; clearing the back-ref makes the in-tree state
                // consistent if anything queries it between PurgeTree and
                // the actual tree removal).
                ClearBranchPointBackref(rp);

                ParsekLog.Info(RewindTag,
                    $"Purged rp={rp.RewindPointId ?? "<no-id>"} " +
                    $"bp={rp.BranchPointId ?? "<no-bp>"} tree={treeId}");
                purged++;
            }

            return purged;
        }

        private static bool TryDeleteQuicksaveFile(RewindPoint rp)
        {
            var hook = DeleteQuicksaveForTesting;
            if (hook != null)
            {
                try { return hook(rp?.RewindPointId ?? ""); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(RewindTag,
                        $"PurgeTree quicksave delete hook threw for rp={rp?.RewindPointId ?? "<no-id>"}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            if (rp == null || string.IsNullOrEmpty(rp.RewindPointId))
                return true;

            string relPath = RecordingPaths.BuildRewindPointRelativePath(rp.RewindPointId);
            if (string.IsNullOrEmpty(relPath))
                return true;

            string root = null;
            string saveFolder = null;
            try { root = KSPUtil.ApplicationRootPath; } catch { root = null; }
            try { saveFolder = HighLogic.SaveFolder; } catch { saveFolder = null; }
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                return true;

            string absolute = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, relPath));
            try
            {
                if (File.Exists(absolute))
                {
                    File.Delete(absolute);
                    ParsekLog.Verbose(RewindTag,
                        $"PurgeTree deleted rewind quicksave rp={rp.RewindPointId} path={absolute}");
                }
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(RewindTag,
                    $"PurgeTree failed to delete rewind quicksave rp={rp.RewindPointId} path={absolute}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void ClearBranchPointBackref(RewindPoint rp)
        {
            if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return;
            string rpId = rp.RewindPointId;
            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree == null || tree.BranchPoints == null) continue;
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        var bp = tree.BranchPoints[b];
                        if (bp == null) continue;
                        if (!string.Equals(bp.RewindPointId, rpId, StringComparison.Ordinal))
                            continue;
                        bp.RewindPointId = null;
                    }
                }
            }
            var pending = RecordingStore.PendingTree;
            if (pending != null && pending.BranchPoints != null)
            {
                for (int b = 0; b < pending.BranchPoints.Count; b++)
                {
                    var bp = pending.BranchPoints[b];
                    if (bp == null) continue;
                    if (!string.Equals(bp.RewindPointId, rpId, StringComparison.Ordinal))
                        continue;
                    bp.RewindPointId = null;
                }
            }
        }

        // ------------------------------------------------------------------
        // Supersede relations
        // ------------------------------------------------------------------

        private static int PurgeSupersedeRelations(
            ParsekScenario scenario, HashSet<string> recordingIds, string treeId)
        {
            if (scenario.RecordingSupersedes == null || scenario.RecordingSupersedes.Count == 0)
                return 0;
            if (recordingIds == null || recordingIds.Count == 0)
                return 0;

            int purged = 0;
            for (int i = scenario.RecordingSupersedes.Count - 1; i >= 0; i--)
            {
                var rel = scenario.RecordingSupersedes[i];
                if (rel == null) continue;
                bool oldIn = !string.IsNullOrEmpty(rel.OldRecordingId)
                             && recordingIds.Contains(rel.OldRecordingId);
                bool newIn = !string.IsNullOrEmpty(rel.NewRecordingId)
                             && recordingIds.Contains(rel.NewRecordingId);
                if (!oldIn && !newIn) continue;

                scenario.RecordingSupersedes.RemoveAt(i);
                ParsekLog.Info(SupersedeTag,
                    $"Purged supersede relation={rel.RelationId ?? "<no-id>"} " +
                    $"old={rel.OldRecordingId ?? "<no-id>"} new={rel.NewRecordingId ?? "<no-id>"} tree={treeId}");
                purged++;
            }
            return purged;
        }

        // ------------------------------------------------------------------
        // Ledger tombstones
        // ------------------------------------------------------------------

        private static int PurgeLedgerTombstones(
            ParsekScenario scenario, HashSet<string> recordingIds, string treeId)
        {
            if (scenario.LedgerTombstones == null || scenario.LedgerTombstones.Count == 0)
                return 0;
            if (recordingIds == null || recordingIds.Count == 0)
                return 0;

            // Build an ActionId -> RecordingId index from Ledger.Actions so we
            // can classify tombstones by their target's owning recording.
            // Allowlisted raw read: this classification has no ELS semantics.
            var actionToRecording = new Dictionary<string, string>(StringComparer.Ordinal);
            var actions = Ledger.Actions;
            if (actions != null)
            {
                for (int a = 0; a < actions.Count; a++)
                {
                    var action = actions[a];
                    if (action == null) continue;
                    if (string.IsNullOrEmpty(action.ActionId)) continue;
                    actionToRecording[action.ActionId] = action.RecordingId;
                }
            }

            int purged = 0;
            int unresolved = 0;
            for (int i = scenario.LedgerTombstones.Count - 1; i >= 0; i--)
            {
                var t = scenario.LedgerTombstones[i];
                if (t == null) continue;

                string recId;
                if (!actionToRecording.TryGetValue(t.ActionId ?? "", out recId))
                {
                    // Tombstone whose target action is no longer in the
                    // ledger — could happen after #432-style pruning. Fall
                    // back to RetiringRecordingId: a tombstone was emitted by
                    // a merge that wrote RetiringRecordingId as the NEW
                    // recording, so if that id is in the tree the tombstone
                    // should also be purged.
                    unresolved++;
                    if (!string.IsNullOrEmpty(t.RetiringRecordingId)
                        && recordingIds.Contains(t.RetiringRecordingId))
                    {
                        scenario.LedgerTombstones.RemoveAt(i);
                        ParsekLog.Info(LedgerSwapTag,
                            $"Purged tombstone={t.TombstoneId ?? "<no-id>"} " +
                            $"actionId={t.ActionId ?? "<no-id>"} " +
                            $"retiring={t.RetiringRecordingId} tree={treeId} (via retiringRecordingId)");
                        purged++;
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(recId)) continue;
                if (!recordingIds.Contains(recId)) continue;

                scenario.LedgerTombstones.RemoveAt(i);
                ParsekLog.Info(LedgerSwapTag,
                    $"Purged tombstone={t.TombstoneId ?? "<no-id>"} " +
                    $"actionId={t.ActionId ?? "<no-id>"} " +
                    $"actionRecording={recId} tree={treeId}");
                purged++;
            }

            if (unresolved > 0)
            {
                ParsekLog.Verbose(LedgerSwapTag,
                    $"PurgeTree: {unresolved.ToString(CultureInfo.InvariantCulture)} tombstone(s) had action(s) not in Ledger; classified via RetiringRecordingId fallback");
            }
            return purged;
        }

        // ------------------------------------------------------------------
        // Marker / journal
        // ------------------------------------------------------------------

        private static bool ClearMarkerIfScopedToTree(ParsekScenario scenario, string treeId)
        {
            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null) return false;
            if (!string.Equals(marker.TreeId, treeId, StringComparison.Ordinal))
                return false;

            string sessionId = marker.SessionId ?? "<no-id>";
            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.BumpSupersedeStateVersion();
            ParsekLog.Info(SessionTag,
                $"End reason=treeDiscarded sess={sessionId} tree={treeId}");
            return true;
        }

        private static bool ClearJournalIfScopedToTree(ParsekScenario scenario, string treeId)
        {
            var journal = scenario.ActiveMergeJournal;
            if (journal == null) return false;
            if (!string.Equals(journal.TreeId, treeId, StringComparison.Ordinal))
                return false;

            string journalId = journal.JournalId ?? "<no-id>";
            scenario.ActiveMergeJournal = null;
            ParsekLog.Info(JournalTag,
                $"Aborted journal={journalId} tree={treeId} reason=treeDiscarded");
            return true;
        }
    }
}
