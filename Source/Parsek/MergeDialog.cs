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
                            RecordingStore.CommitPending();
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
                            RecordingStore.CommitPending();
                            CrewReservationManager.ReserveSnapshotCrew();
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
                    "Parsek — Merge Recording",
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
                        RecordingStore.CommitPending();
                        CrewReservationManager.ReserveSnapshotCrew();
                        CrewReservationManager.SwapReservedCrewInFlight();
                        ClearPendingFlag();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged — vessel will appear!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
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
                        RecordingStore.CommitPending();
                        ClearPendingFlag();
                        ReplayFlightResultsIfPending();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
                    {
                        DiscardChain(pending, chainId);
                        ClearPendingFlag();
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
            ParsekLog.Verbose("MergeDialog",
                $"Chain dialog: branchCount={branchCount}, totalSegments={totalSegments}, " +
                $"destroyed={pending.VesselDestroyed}, hasSnapshot={pending.VesselSnapshot != null}");
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
            string header = $"Vessel: {pending.VesselName}\n" +
                $"Points: {pending.Points.Count}\n" +
                $"Duration: {duration.ToString("F1", CultureInfo.InvariantCulture)}s\n" +
                $"Distance from launch: {pending.DistanceFromLaunch.ToString("F0", CultureInfo.InvariantCulture)}m\n\n";

            switch (recommended)
            {
                case MergeDefault.GhostOnly:
                    return header + (pending.VesselDestroyed
                        ? "Your vessel was destroyed. Recording captured."
                        : "Recording captured.");

                case MergeDefault.Persist:
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

            // Multi-vessel trees get the per-vessel row dialog
            if (allLeaves.Count > 1)
            {
                ShowMultiVesselTreeDialog(tree);
                return;
            }

            var spawnableLeaves = tree.GetSpawnableLeaves();

            int survivingCount;
            int destroyedCount;
            string message = BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out survivingCount, out destroyedCount);

            ParsekLog.Info("MergeDialog",
                $"Tree merge dialog: tree='{tree.TreeName}', recordings={tree.Recordings.Count}, " +
                $"allLeaves={allLeaves.Count}, spawnable={survivingCount}, destroyed={destroyedCount}");

            // Buttons — capture in locals for lambda closures
            int spawnCount = survivingCount;

            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton("Merge to Timeline", () =>
                {
                    // Mark tree recordings for force-spawn if active vessel shares PID
                    // (after revert, pad vessel has same PID — dedup would skip spawn)
                    var av = FlightGlobals.ActiveVessel;
                    if (av != null && av.persistentId != 0)
                        MarkForceSpawnOnTreeRecordings(tree, av.persistentId);
                    RecordingStore.CommitPendingTree();
                    CrewReservationManager.ReserveSnapshotCrew();
                    CrewReservationManager.SwapReservedCrewInFlight();
                    ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                    OnTreeCommitted?.Invoke();
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
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
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

        internal static string GetLeafSituationText(Recording leaf)
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

        #region Extracted helpers

        /// <summary>
        /// Pure function: compute the tree merge dialog message text from tree data.
        /// Extracts duration, destroyed/surviving counts, per-leaf summaries, and
        /// assembles the full dialog body. Used by ShowTreeDialog.
        /// </summary>
        internal static string BuildTreeDialogMessage(
            RecordingTree tree,
            List<Recording> allLeaves,
            List<Recording> spawnableLeaves,
            out int survivingCount,
            out int destroyedCount)
        {
            survivingCount = spawnableLeaves != null ? spawnableLeaves.Count : 0;
            destroyedCount = CountDestroyedLeaves(allLeaves);
            double duration = ComputeTreeDurationRange(tree);

            // Build vessel count text
            string vesselCountText;
            if (destroyedCount > 0)
                vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")} ({destroyedCount} destroyed)";
            else
                vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")}";

            // Build per-leaf summary
            string activeRecordingId = tree != null ? tree.ActiveRecordingId : null;
            string treeName = tree != null ? tree.TreeName : "";
            var sb = new StringBuilder();
            for (int i = 0; i < (allLeaves != null ? allLeaves.Count : 0); i++)
            {
                var leaf = allLeaves[i];
                string situationText = GetLeafSituationText(leaf);
                string marker = (leaf.RecordingId == activeRecordingId) ? "  <-- you are here" : "";
                sb.AppendLine($"  {leaf.VesselName} \u2014 {situationText}{marker}");
            }

            // Assemble message
            string header = $"\"{treeName}\" \u2014 {vesselCountText}, {FormatDuration(duration)}\n\n";
            string footer;
            if (survivingCount > 0)
                footer = "\nAll surviving vessels will appear after ghost playback.";
            else
                footer = "\nAll vessels were lost. Ghosts will replay the mission.";

            return header + sb.ToString() + footer;
        }

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

        /// <summary>
        /// Pure function: count how many leaves have TerminalState.Destroyed.
        /// </summary>
        internal static int CountDestroyedLeaves(List<Recording> leaves)
        {
            if (leaves == null) return 0;
            int count = 0;
            for (int i = 0; i < leaves.Count; i++)
            {
                if (leaves[i].TerminalStateValue.HasValue
                    && leaves[i].TerminalStateValue.Value == TerminalState.Destroyed)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Marks ForceSpawnNewVessel on tree recordings whose VesselPersistentId matches
        /// the active vessel's PID and that haven't been spawned yet. This prevents PID
        /// dedup from skipping spawn when the pad vessel shares a PID with the recording's
        /// vessel (common after revert). Pure static for testability.
        /// </summary>
        /// <param name="tree">The recording tree to scan.</param>
        /// <param name="activePid">The active vessel's persistentId (0 = skip).</param>
        /// <returns>Number of recordings marked.</returns>
        internal static int MarkForceSpawnOnTreeRecordings(RecordingTree tree, uint activePid)
        {
            if (tree == null || activePid == 0)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"MarkForceSpawnOnTreeRecordings: skip — tree={tree != null}, activePid={activePid}");
                return 0;
            }

            int count = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.VesselPersistentId == activePid && !rec.VesselSpawned)
                {
                    rec.ForceSpawnNewVessel = true;
                    count++;
                    ParsekLog.Verbose("MergeDialog",
                        $"MarkForceSpawnOnTreeRecordings: marked '{rec.VesselName}' " +
                        $"(id={rec.RecordingId}, pid={rec.VesselPersistentId})");
                }
            }

            if (count > 0)
                ParsekLog.Info("MergeDialog",
                    $"MarkForceSpawnOnTreeRecordings: marked {count} recording(s) " +
                    $"with ForceSpawnNewVessel (activePid={activePid}, tree='{tree.TreeName}')");
            else
                ParsekLog.Verbose("MergeDialog",
                    $"MarkForceSpawnOnTreeRecordings: no recordings matched activePid={activePid} " +
                    $"in tree '{tree.TreeName}' ({tree.Recordings.Count} recordings)");

            return count;
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
        /// Builds the per-vessel summary text for the tree dialog, including
        /// persist/ghost-only status indicators per vessel row.
        /// Pure static for testability.
        /// </summary>
        internal static string BuildVesselRowsText(
            List<Recording> allLeaves,
            Dictionary<string, bool> decisions,
            string activeRecordingId)
        {
            if (allLeaves == null || allLeaves.Count == 0)
                return "";

            var sb = new StringBuilder();
            for (int i = 0; i < allLeaves.Count; i++)
            {
                var leaf = allLeaves[i];
                string situationText = GetLeafSituationText(leaf);
                string marker = (leaf.RecordingId == activeRecordingId) ? "  <-- you are here" : "";

                bool persist = false;
                if (decisions != null && decisions.ContainsKey(leaf.RecordingId))
                    persist = decisions[leaf.RecordingId];

                bool canToggle = CanPersistVessel(leaf);
                string persistLabel = persist ? "[Persist]" : "[Ghost-only]";
                if (!canToggle)
                    persistLabel += " (locked)";

                sb.AppendLine($"  {leaf.VesselName} \u2014 {situationText} {persistLabel}{marker}");
            }
            return sb.ToString();
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

        /// <summary>
        /// Shows the tree merge dialog with per-vessel rows when the tree has
        /// multiple leaf recordings. Each row shows vessel name, status, and
        /// persist/ghost-only decision. Single-leaf trees use the simpler dialog.
        /// </summary>
        internal static void ShowMultiVesselTreeDialog(RecordingTree tree)
        {
            var allLeaves = tree.GetAllLeaves();
            var spawnableLeaves = tree.GetSpawnableLeaves();
            var decisions = BuildDefaultVesselDecisions(tree);

            int survivingCount;
            int destroyedCount;
            string headerMessage = BuildTreeDialogMessage(
                tree, allLeaves, spawnableLeaves,
                out survivingCount, out destroyedCount);

            // Replace the per-leaf summary with the richer per-vessel rows
            string vesselRows = BuildVesselRowsText(allLeaves, decisions, tree.ActiveRecordingId);

            // Build the composite message: header (up to first leaf line) + vessel rows + footer
            double duration = ComputeTreeDurationRange(tree);
            string vesselCountText;
            if (destroyedCount > 0)
                vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")} ({destroyedCount} destroyed)";
            else
                vesselCountText = $"{survivingCount} vessel{(survivingCount != 1 ? "s" : "")}";

            string footer;
            if (survivingCount > 0)
                footer = "\nSurviving vessels marked [Persist] will be spawned after ghost playback.\n" +
                         "Destroyed or recovered vessels are ghost-only (visual replay only).";
            else
                footer = "\nAll vessels were lost. Ghosts will replay the mission.";

            string message = $"\"{tree.TreeName}\" \u2014 {vesselCountText}, {FormatDuration(duration)}\n\n" +
                             vesselRows + footer;

            ParsekLog.Info("MergeDialog",
                $"Multi-vessel tree dialog: tree='{tree.TreeName}', leaves={allLeaves.Count}, " +
                $"surviving={survivingCount}, destroyed={destroyedCount}");

            // Capture for lambda closures
            var capturedDecisions = decisions;
            var capturedTree = tree;
            int spawnCount = survivingCount;

            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton("Commit All", () =>
                {
                    // Mark tree recordings for force-spawn if active vessel shares PID
                    // (after revert, pad vessel has same PID — dedup would skip spawn)
                    var av = FlightGlobals.ActiveVessel;
                    if (av != null && av.persistentId != 0)
                        MarkForceSpawnOnTreeRecordings(capturedTree, av.persistentId);
                    ApplyVesselDecisions(capturedTree, capturedDecisions);
                    RecordingStore.CommitPendingTree();
                    CrewReservationManager.ReserveSnapshotCrew();
                    CrewReservationManager.SwapReservedCrewInFlight();
                    ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                    OnTreeCommitted?.Invoke();

                    int persistCount = 0;
                    foreach (var val in capturedDecisions.Values)
                        if (val) persistCount++;

                    if (persistCount > 0)
                        ParsekLog.ScreenMessage(
                            $"Tree merged \u2014 {persistCount} vessel(s) will appear after ghost playback", 3f);
                    else
                        ParsekLog.ScreenMessage("Tree merged to timeline!", 3f);
                    ParsekLog.Info("MergeDialog",
                        $"User chose: Commit All (tree='{capturedTree.TreeName}', " +
                        $"persist={persistCount}, total={capturedDecisions.Count})");
                }),
                new DialogGUIButton("Discard", () =>
                {
                    foreach (var rec in capturedTree.Recordings.Values)
                    {
                        if (rec.VesselSnapshot != null)
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    }
                    RecordingStore.DiscardPendingTree();
                    ClearPendingFlag();
                    ReplayFlightResultsIfPending();
                    ParsekLog.ScreenMessage("Recording tree discarded", 2f);
                    ParsekLog.Info("MergeDialog",
                        $"User chose: Tree Discard (tree='{capturedTree.TreeName}', " +
                        $"recordings={capturedTree.Recordings.Count})");
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
    }
}
