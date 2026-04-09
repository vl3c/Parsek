using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        /// Called from button callbacks to replay KSP's flight results dialog
        /// that was intercepted by the Harmony patch. Only replays if there's
        /// a pending message (i.e., we intercepted a Display call).
        /// </summary>
        internal static void ReplayFlightResultsIfPending()
        {
            Patches.FlightResultsPatch.ReplayFlightResults();
        }

        public static void Show(Recording pending)
        {
            if (pending == null)
            {
                ParsekLog.Warn("MergeDialog", "Cannot show merge dialog: pending recording is null");
                return;
            }

            // Detect if this is a chain recording with committed siblings
            bool isChain = !string.IsNullOrEmpty(pending.ChainId);
            List<Recording> chainSiblings = isChain
                ? RecordingStore.GetChainRecordings(pending.ChainId)
                : null;
            int chainSegmentCount = (chainSiblings != null ? chainSiblings.Count : 0) + 1; // +1 for pending

            if (isChain && chainSiblings != null)
            {
                ShowChainDialog(pending, chainSiblings, chainSegmentCount);
                return;
            }

            if (isChain && chainSiblings == null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"Pending recording references chain='{pending.ChainId}' but no siblings were found; falling back to standalone dialog");
            }

            // Non-chain: use existing dialog
            ShowStandaloneDialog(pending);
        }

        static void ShowStandaloneDialog(Recording pending)
        {
            double duration = pending.EndUT - pending.StartUT;
            var recommended = RecordingStore.GetRecommendedAction(
                pending.VesselDestroyed, pending.VesselSnapshot != null);

            ParsekLog.Info("MergeDialog", $"Merge dialog: " +
                $"destroyed={pending.VesselDestroyed}, hasSnapshot={pending.VesselSnapshot != null}, " +
                $"recommended={recommended}");

            DialogGUIButton[] buttons;

            switch (recommended)
            {
                case MergeDefault.GhostOnly:
                    // Vessel destroyed or no snapshot — no vessel to persist
                    buttons = new[]
                    {
                        new DialogGUIButton("Merge to Timeline", () =>
                        {
                            if (pending.VesselSnapshot != null)
                                CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            pending.VesselSnapshot = null;
                            string recId = pending.RecordingId;
                            double startUT = pending.StartUT;
                            double endUT = pending.EndUT;
                            RecordingStore.CommitPending();
                            LedgerOrchestrator.OnRecordingCommitted(recId, startUT, endUT);
                            ClearPendingFlag();
                            ReplayFlightResultsIfPending();
                            ParsekLog.ScreenMessage("Recording merged to timeline!", 3f);
                            ParsekLog.Info("MergeDialog", "User chose: Merge to Timeline (vessel destroyed)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ClearPendingFlag();
                            ReplayFlightResultsIfPending();
                            ParsekLog.ScreenMessage("Recording discarded", 2f);
                            ParsekLog.Info("MergeDialog", "User chose: Discard");
                        })
                    };
                    break;

                case MergeDefault.Persist:
                    // Vessel intact with snapshot — persist in timeline
                    buttons = new[]
                    {
                        new DialogGUIButton("Merge to Timeline", () =>
                        {
                            // Defer spawn — vessel appears when ghost finishes at EndUT
                            string recId = pending.RecordingId;
                            double startUT = pending.StartUT;
                            double endUT = pending.EndUT;
                            RecordingStore.CommitPending();
                            LedgerOrchestrator.OnRecordingCommitted(recId, startUT, endUT);
                            CrewReservationManager.SwapReservedCrewInFlight();
                            ClearPendingFlag();
                            ReplayFlightResultsIfPending();
                            ParsekLog.ScreenMessage("Recording merged — vessel will appear after ghost playback", 3f);
                            ParsekLog.Info("MergeDialog", "User chose: Merge to Timeline (deferred spawn)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ClearPendingFlag();
                            ReplayFlightResultsIfPending();
                            ParsekLog.ScreenMessage("Recording discarded", 2f);
                            ParsekLog.Info("MergeDialog", "User chose: Discard");
                        })
                    };
                    break;

                default:
                    ParsekLog.Warn("MergeDialog", $"Unexpected MergeDefault value: {recommended}");
                    buttons = new DialogGUIButton[0];
                    break;
            }

            string message = BuildMergeMessage(pending, duration, recommended);

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

        static void ShowChainDialog(Recording pending,
            List<Recording> chainSiblings, int totalSegments)
        {
            string chainId = pending.ChainId;
            double duration = pending.EndUT - pending.StartUT;

            ParsekLog.Info("MergeDialog", $"Chain merge dialog: chain={chainId}, segments={totalSegments}, " +
                $"hasSnapshot={pending.VesselSnapshot != null}");

            DialogGUIButton[] buttons;

            if (pending.VesselSnapshot != null && !pending.VesselDestroyed)
            {
                buttons = new[]
                {
                    new DialogGUIButton("Merge to Timeline", () =>
                    {
                        string recId = pending.RecordingId;
                        double startUT = pending.StartUT;
                        double endUT = pending.EndUT;
                        RecordingStore.CommitPending();
                        LedgerOrchestrator.OnRecordingCommitted(recId, startUT, endUT);
                        CrewReservationManager.SwapReservedCrewInFlight();
                        ClearPendingFlag();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged — vessel will appear!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard", () =>
                    {
                        DiscardChain(pending, chainId);
                        ClearPendingFlag();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) discarded", 2f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Discard ({totalSegments} segments)");
                    })
                };
            }
            else
            {
                // No vessel to spawn (EVA final segment or destroyed)
                buttons = new[]
                {
                    new DialogGUIButton("Merge to Timeline", () =>
                    {
                        if (pending.VesselSnapshot != null)
                            CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                        pending.VesselSnapshot = null;
                        NullChainSiblingSnapshots(chainSiblings);
                        string recId = pending.RecordingId;
                        double startUT = pending.StartUT;
                        double endUT = pending.EndUT;
                        RecordingStore.CommitPending();
                        LedgerOrchestrator.OnRecordingCommitted(recId, startUT, endUT);
                        ClearPendingFlag();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard", () =>
                    {
                        DiscardChain(pending, chainId);
                        ClearPendingFlag();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) discarded", 2f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Discard ({totalSegments} segments)");
                    })
                };
            }

            string message = $"{pending.VesselName} - {FormatDuration(duration)}";

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

        /// <summary>
        /// Unreserves crew and nulls VesselSnapshot on siblings without granting recovery funds.
        /// Used by the Merge-to-Timeline path (ghost-only, no vessel persistence).
        /// </summary>
        static void NullChainSiblingSnapshots(List<Recording> siblings)
        {
            if (siblings == null) return;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].VesselSnapshot != null)
                {
                    CrewReservationManager.UnreserveCrewInSnapshot(siblings[i].VesselSnapshot);
                    siblings[i].VesselSnapshot = null;
                    ParsekLog.Info("MergeDialog", $"Chain sibling #{i} snapshot nulled (no recovery)");
                }
            }
        }

        static void DiscardChain(Recording pending, string chainId)
        {
            int unreservedCount = 0;

            // Unreserve crew from pending
            if (pending.VesselSnapshot != null)
            {
                CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                unreservedCount++;
            }

            // Unreserve crew from all committed chain siblings
            var siblings = RecordingStore.GetChainRecordings(chainId);
            if (siblings != null)
            {
                for (int i = 0; i < siblings.Count; i++)
                {
                    if (siblings[i].VesselSnapshot != null)
                    {
                        CrewReservationManager.UnreserveCrewInSnapshot(siblings[i].VesselSnapshot);
                        unreservedCount++;
                    }
                }
            }

            // Remove committed chain recordings and discard pending
            RecordingStore.RemoveChainRecordings(chainId);
            RecordingStore.DiscardPending();
            ParsekLog.Info("MergeDialog",
                $"Discarded chain '{chainId}': unreservedSnapshots={unreservedCount}, siblingCount={siblings?.Count ?? 0}");
        }

        internal static string BuildMergeMessage(Recording pending, double duration,
            MergeDefault recommended)
        {
            return $"{pending.VesselName} - {FormatDuration(duration)}";
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
                    ReplayFlightResultsIfPending();
                    OnTreeCommitted?.Invoke();
                    if (spawnCount > 0)
                        ParsekLog.ScreenMessage(
                            $"Merged - {spawnCount} vessel(s) will appear after ghost playback", 3f);
                    else
                        ParsekLog.ScreenMessage("Merged to timeline!", 3f);
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
                    ReplayFlightResultsIfPending();
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
        /// Determines whether a recording's vessel can be persisted (spawned as real vessel).
        /// Returns false for destroyed, recovered, docked, or boarded vessels,
        /// and for recordings with no vessel snapshot.
        /// Pure static for testability.
        /// </summary>
        internal static bool CanPersistVessel(Recording rec)
        {
            if (rec == null)
                return false;

            if (rec.VesselSnapshot == null)
                return false;

            if (rec.TerminalStateValue.HasValue)
            {
                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
                    || ts == TerminalState.Docked || ts == TerminalState.Boarded)
                    return false;
            }

            return true;
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
                bool canPersist = CanPersistVessel(leaf);
                decisions[leaf.RecordingId] = canPersist;
                ParsekLog.Verbose("MergeDialog",
                    $"BuildDefaultVesselDecisions: leaf='{leaf.RecordingId}' vessel='{leaf.VesselName}' " +
                    $"terminal={leaf.TerminalStateValue?.ToString() ?? "null"} " +
                    $"hasSnapshot={leaf.VesselSnapshot != null} canPersist={canPersist}");
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
