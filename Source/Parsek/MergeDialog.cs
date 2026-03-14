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

        public static void Show(RecordingStore.Recording pending)
        {
            if (pending == null)
            {
                ParsekLog.Warn("MergeDialog", "Cannot show merge dialog: pending recording is null");
                return;
            }

            // Detect if this is a chain recording with committed siblings
            bool isChain = !string.IsNullOrEmpty(pending.ChainId);
            List<RecordingStore.Recording> chainSiblings = isChain
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

        static void ShowStandaloneDialog(RecordingStore.Recording pending)
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
                case RecordingStore.MergeDefault.GhostOnly:
                    // Vessel destroyed or no snapshot — no vessel to persist
                    buttons = new[]
                    {
                        new DialogGUIButton("Merge to Timeline", () =>
                        {
                            if (pending.VesselSnapshot != null)
                                ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            pending.VesselSnapshot = null;
                            RecordingStore.CommitPending();
                            ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                        ReplayFlightResultsIfPending();
                            ReplayFlightResultsIfPending();
                            ParsekLog.ScreenMessage("Recording merged to timeline!", 3f);
                            ParsekLog.Info("MergeDialog", "User chose: Merge to Timeline (vessel destroyed)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                        ReplayFlightResultsIfPending();
                            ReplayFlightResultsIfPending();
                            ParsekLog.ScreenMessage("Recording discarded", 2f);
                            ParsekLog.Info("MergeDialog", "User chose: Discard");
                        })
                    };
                    break;

                case RecordingStore.MergeDefault.Persist:
                    // Vessel intact with snapshot — persist in timeline
                    buttons = new[]
                    {
                        new DialogGUIButton("Merge to Timeline", () =>
                        {
                            // Defer spawn — vessel appears when ghost finishes at EndUT
                            RecordingStore.CommitPending();
                            ParsekScenario.ReserveSnapshotCrew();
                            ParsekScenario.SwapReservedCrewInFlight();
                            ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                        ReplayFlightResultsIfPending();
                            ReplayFlightResultsIfPending();
                            ParsekLog.ScreenMessage("Recording merged — vessel will appear after ghost playback", 3f);
                            ParsekLog.Info("MergeDialog", "User chose: Merge to Timeline (deferred spawn)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                        ReplayFlightResultsIfPending();
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
                    "Parsek — Merge Recording",
                    HighLogic.UISkin,
                    buttons
                ),
                false,
                HighLogic.UISkin
            );
        }

        static void ShowChainDialog(RecordingStore.Recording pending,
            List<RecordingStore.Recording> chainSiblings, int totalSegments)
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
                        RecordingStore.CommitPending();
                        ParsekScenario.ReserveSnapshotCrew();
                        ParsekScenario.SwapReservedCrewInFlight();
                        ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged — vessel will appear!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
                    {
                        DiscardChain(pending, chainId);
                        ClearPendingFlag();
                    ReplayFlightResultsIfPending();
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
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                        pending.VesselSnapshot = null;
                        NullChainSiblingSnapshots(chainSiblings);
                        RecordingStore.CommitPending();
                        ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
                    {
                        DiscardChain(pending, chainId);
                        ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) discarded", 2f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Discard ({totalSegments} segments)");
                    })
                };
            }

            var branchSet = new HashSet<int> { pending.ChainBranch };
            if (chainSiblings != null)
                foreach (var s in chainSiblings) branchSet.Add(s.ChainBranch);
            int branchCount = branchSet.Count;
            string segmentLabel = branchCount > 1
                ? $"Mission chain ({totalSegments} segments, {branchCount} vessels tracked)"
                : $"Mission chain ({totalSegments} segments)";

            string message = $"{segmentLabel}\n" +
                $"Vessel: {pending.VesselName}\n" +
                $"Duration: {duration.ToString("F1", CultureInfo.InvariantCulture)}s\n" +
                $"Distance: {pending.DistanceFromLaunch.ToString("F0", CultureInfo.InvariantCulture)}m\n\n";

            if (pending.VesselSnapshot != null && !pending.VesselDestroyed)
                message += "The final vessel will persist in the timeline.";
            else if (pending.VesselDestroyed)
                message += "The vessel was destroyed. All segments will replay as ghosts.";
            else
                message += "All segments will replay as ghosts.";

            LockInput();
            PopupDialog.DismissPopup("ParsekMerge");
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekMerge",
                    message,
                    "Parsek — Merge Mission Chain",
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
        static void NullChainSiblingSnapshots(List<RecordingStore.Recording> siblings)
        {
            if (siblings == null) return;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].VesselSnapshot != null)
                {
                    ParsekScenario.UnreserveCrewInSnapshot(siblings[i].VesselSnapshot);
                    siblings[i].VesselSnapshot = null;
                    ParsekLog.Info("MergeDialog", $"Chain sibling #{i} snapshot nulled (no recovery)");
                }
            }
        }

        static void DiscardChain(RecordingStore.Recording pending, string chainId)
        {
            int unreservedCount = 0;

            // Unreserve crew from pending
            if (pending.VesselSnapshot != null)
            {
                ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
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
                        ParsekScenario.UnreserveCrewInSnapshot(siblings[i].VesselSnapshot);
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

        internal static string BuildMergeMessage(RecordingStore.Recording pending, double duration,
            RecordingStore.MergeDefault recommended)
        {
            string header = $"Vessel: {pending.VesselName}\n" +
                $"Points: {pending.Points.Count}\n" +
                $"Duration: {duration.ToString("F1", CultureInfo.InvariantCulture)}s\n" +
                $"Distance from launch: {pending.DistanceFromLaunch.ToString("F0", CultureInfo.InvariantCulture)}m\n\n";

            switch (recommended)
            {
                case RecordingStore.MergeDefault.GhostOnly:
                    return header + (pending.VesselDestroyed
                        ? "Your vessel was destroyed. Recording captured."
                        : "Recording captured.");

                case RecordingStore.MergeDefault.Persist:
                    string situation = pending.DistanceFromLaunch < 100.0
                        ? "Your vessel returned near the launch site after traveling " +
                          pending.MaxDistanceFromLaunch.ToString("F0", CultureInfo.InvariantCulture) + "m."
                        : $"Your vessel is {pending.VesselSituation}.";
                    return header + situation + "\nIt will persist in the timeline.";

                default:
                    return header;
            }
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

            var allLeaves = tree.GetAllLeaves();
            var spawnableLeaves = tree.GetSpawnableLeaves();

            // Compute total duration across all recordings in the tree
            double minStartUT = double.MaxValue;
            double maxEndUT = double.MinValue;
            foreach (var rec in tree.Recordings.Values)
            {
                double start = rec.StartUT;
                double end = rec.EndUT;
                if (start < minStartUT) minStartUT = start;
                if (end > maxEndUT) maxEndUT = end;
            }
            double duration = (minStartUT < double.MaxValue && maxEndUT > double.MinValue)
                ? maxEndUT - minStartUT
                : 0;

            // Count destroyed leaves
            int destroyedCount = 0;
            for (int i = 0; i < allLeaves.Count; i++)
            {
                if (allLeaves[i].TerminalStateValue.HasValue
                    && allLeaves[i].TerminalStateValue.Value == TerminalState.Destroyed)
                    destroyedCount++;
            }

            int survivingCount = spawnableLeaves.Count;

            ParsekLog.Info("MergeDialog",
                $"Tree merge dialog: tree='{tree.TreeName}', recordings={tree.Recordings.Count}, " +
                $"allLeaves={allLeaves.Count}, spawnable={survivingCount}, destroyed={destroyedCount}");

            // Build vessel count text
            string vesselCountText;
            if (destroyedCount > 0)
                vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")} ({destroyedCount} destroyed)";
            else
                vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")}";

            // Build per-leaf summary
            var sb = new StringBuilder();
            for (int i = 0; i < allLeaves.Count; i++)
            {
                var leaf = allLeaves[i];
                string situationText = GetLeafSituationText(leaf);
                string marker = (leaf.RecordingId == tree.ActiveRecordingId) ? "  <-- you are here" : "";
                sb.AppendLine($"  {leaf.VesselName} \u2014 {situationText}{marker}");
            }

            // Assemble message
            string header = $"\"{tree.TreeName}\" \u2014 {vesselCountText}, {FormatDuration(duration)}\n\n";
            string footer;
            if (survivingCount > 0)
                footer = "\nAll surviving vessels will appear after ghost playback.";
            else
                footer = "\nAll vessels were lost. Ghosts will replay the mission.";

            string message = header + sb.ToString() + footer;

            // Buttons — capture in locals for lambda closures
            int spawnCount = survivingCount;

            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton("Merge to Timeline", () =>
                {
                    RecordingStore.CommitPendingTree();
                    ParsekScenario.ReserveSnapshotCrew();
                    ParsekScenario.SwapReservedCrewInFlight();
                    ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                    if (spawnCount > 0)
                        ParsekLog.ScreenMessage(
                            $"Tree merged \u2014 {spawnCount} vessel(s) will appear after ghost playback", 3f);
                    else
                        ParsekLog.ScreenMessage("Tree merged to timeline!", 3f);
                    ParsekLog.Info("MergeDialog",
                        $"User chose: Tree Merge (tree='{tree.TreeName}', " +
                        $"recordings={tree.Recordings.Count}, spawnable={spawnCount})");
                }),
                new DialogGUIButton("Discard", () =>
                {
                    foreach (var rec in tree.Recordings.Values)
                    {
                        if (rec.VesselSnapshot != null)
                            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    }
                    RecordingStore.DiscardPendingTree();
                    ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                    ParsekLog.ScreenMessage("Recording tree discarded", 2f);
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
                    "Parsek \u2014 Merge Recording Tree",
                    HighLogic.UISkin,
                    buttons
                ),
                false,
                HighLogic.UISkin
            );
        }

        internal static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "0s";
            if (seconds < 0) seconds = 0;
            if (seconds < 60)
                return ((int)seconds).ToString(CultureInfo.InvariantCulture) + "s";
            if (seconds < 3600)
            {
                int m = (int)(seconds / 60);
                int s = (int)(seconds % 60);
                return s > 0
                    ? m.ToString(CultureInfo.InvariantCulture) + "m " +
                      s.ToString(CultureInfo.InvariantCulture) + "s"
                    : m.ToString(CultureInfo.InvariantCulture) + "m";
            }
            int h = (int)(seconds / 3600);
            int min = (int)((seconds % 3600) / 60);
            return min > 0
                ? h.ToString(CultureInfo.InvariantCulture) + "h " +
                  min.ToString(CultureInfo.InvariantCulture) + "m"
                : h.ToString(CultureInfo.InvariantCulture) + "h";
        }

        internal static string GetLeafSituationText(RecordingStore.Recording leaf)
        {
            if (leaf.TerminalStateValue.HasValue)
            {
                switch (leaf.TerminalStateValue.Value)
                {
                    case TerminalState.Orbiting:
                        return "Orbiting " + (leaf.TerminalOrbitBody ?? "unknown");
                    case TerminalState.Landed:
                        return "Landed on " + (leaf.TerminalPosition.HasValue
                            ? leaf.TerminalPosition.Value.body : "unknown");
                    case TerminalState.Splashed:
                        return "Splashed on " + (leaf.TerminalPosition.HasValue
                            ? leaf.TerminalPosition.Value.body : "unknown");
                    case TerminalState.SubOrbital:
                        return "Sub-orbital, " + (leaf.TerminalOrbitBody ?? "unknown");
                    case TerminalState.Destroyed:
                        return "Destroyed";
                    case TerminalState.Recovered:
                        return "Recovered";
                    case TerminalState.Docked:
                        return "Docked";
                    case TerminalState.Boarded:
                        return "Boarded";
                    default:
                        return "Unknown";
                }
            }

            // Fallback for legacy recordings or recordings without terminal state
            if (!string.IsNullOrEmpty(leaf.VesselSituation))
                return leaf.VesselSituation;

            return "Unknown";
        }
    }
}
