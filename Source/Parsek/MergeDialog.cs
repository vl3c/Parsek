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

        /// <summary>
        /// Called from button callbacks to resolve deferred stock flight results.
        /// Replays the intercepted stock dialog if one was captured; otherwise
        /// just clears any pre-capture suppression latch.
        /// </summary>
        internal static void ResolveDeferredFlightResults()
        {
            Patches.FlightResultsPatch.ReplayFlightResults();
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

            var decisions = BuildDefaultVesselDecisions(tree);
            double duration = ComputeTreeDurationRange(tree);
            string message = $"{tree.TreeName} - {FormatDuration(duration)}";

            int spawnCount = 0;
            foreach (var val in decisions.Values)
                if (val) spawnCount++;

            ParsekLog.Info("MergeDialog",
                $"Tree merge dialog: tree='{tree.TreeName}', recordings={tree.Recordings.Count}, " +
                $"spawnable={spawnCount}");

            if (spawnCount == 0 && decisions.Count > 0)
                message += "\n\nNo flight branches produced a vessel that can continue flying. " +
                           "The recordings will play back as ghosts, but no vessel will be placed.";

            var capturedDecisions = decisions;

            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton("Merge to Timeline", () =>
                {
                    ApplyVesselDecisions(tree, capturedDecisions);
                    RecordingStore.CommitPendingTree();
                    RecordingStore.RunOptimizationPass();
                    // #292: Refresh quicksave so subsequent F9 quickloads include the
                    // recording IDs added by this merge (otherwise F9 loads a stale
                    // quicksave from before the merge and silently drops them).
                    RecordingStore.RefreshQuicksaveAfterMerge(
                        "merge dialog Tree Merge", tree.Recordings.Count);
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
                    CrewReservationManager.SwapReservedCrewInFlight();

                    ClearPendingFlag();
                    ResolveDeferredFlightResults();
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
                }),
                new DialogGUIButton("Discard", () =>
                {
                    foreach (var rec in tree.Recordings.Values)
                    {
                        if (rec.VesselSnapshot != null)
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    }
                    RecordingStore.DiscardPendingTree();
                    ClearPendingFlag();
                    ResolveDeferredFlightResults();
                    ParsekLog.ScreenMessage("Recording discarded", 2f);
                    ParsekLog.Info("MergeDialog",
                        $"User chose: Tree Discard (tree='{tree.TreeName}', " +
                        $"recordings={tree.Recordings.Count})");
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
                    "Parsek - Merge to Timeline",
                    HighLogic.UISkin,
                    buttons
                ),
                false,
                HighLogic.UISkin
            );
        }

        internal static string FormatDuration(double seconds)
            => ParsekTimeFormat.FormatDuration(seconds);

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
                isChainLoopingOrDisabled: false,
                treeContext: treeContext);
            return needsSpawn;
        }

        /// <summary>
        /// Builds default persist/ghost-only decisions for all leaf recordings in a tree.
        /// Surviving vessels default to persist (true), destroyed/recovered default to ghost-only (false).
        /// Keys are RecordingId. Pure static for testability.
        /// </summary>
        internal static Dictionary<string, bool> BuildDefaultVesselDecisions(RecordingTree tree)
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

            return decisions;
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
