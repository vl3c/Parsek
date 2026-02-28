using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for the post-revert merge dialog.
    /// </summary>
    public static class MergeDialog
    {
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

            DialogGUIButton[] buttons = null;

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
                            ParsekLog.ScreenMessage("Recording merged to timeline!", 3f);
                            ParsekLog.Info("MergeDialog", "User chose: Merge to Timeline (vessel destroyed)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
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
                            ParsekLog.ScreenMessage("Recording merged — vessel will appear after ghost playback", 3f);
                            ParsekLog.Info("MergeDialog", "User chose: Merge to Timeline (deferred spawn)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ParsekLog.ScreenMessage("Recording discarded", 2f);
                            ParsekLog.Info("MergeDialog", "User chose: Discard");
                        })
                    };
                    break;
            }

            string message = BuildMergeMessage(pending, duration, recommended);

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
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged — vessel will appear!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
                    {
                        DiscardChain(pending, chainId);
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
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged!", 3f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
                    {
                        DiscardChain(pending, chainId);
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) discarded", 2f);
                        ParsekLog.Info("MergeDialog", $"User chose: Chain Discard ({totalSegments} segments)");
                    })
                };
            }

            int branchCount = 1 + (chainSiblings != null
                ? new HashSet<int>(chainSiblings.Select(s => s.ChainBranch)).Count
                : 0);
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
                $"Duration: {duration:F1}s\n" +
                $"Distance from launch: {pending.DistanceFromLaunch:F0}m\n\n";

            switch (recommended)
            {
                case RecordingStore.MergeDefault.GhostOnly:
                    return header + "Your vessel was destroyed. Recording captured.";

                default: // Persist
                    string situation = pending.DistanceFromLaunch < 100.0
                        ? "Your vessel returned near the launch site after traveling " +
                          $"{pending.MaxDistanceFromLaunch:F0}m."
                        : $"Your vessel is {pending.VesselSituation}.";
                    return header + situation + "\nIt will persist in the timeline.";
            }
        }
    }
}
