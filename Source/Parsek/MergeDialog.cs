using System.Collections.Generic;
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

            // Non-chain: use existing dialog
            ShowStandaloneDialog(pending);
        }

        static void ShowStandaloneDialog(RecordingStore.Recording pending)
        {
            double duration = pending.EndUT - pending.StartUT;
            var recommended = RecordingStore.GetRecommendedAction(
                pending.DistanceFromLaunch, pending.VesselDestroyed,
                pending.VesselSnapshot != null,
                duration, pending.MaxDistanceFromLaunch);

            ParsekLog.Log($"Merge dialog: distance={pending.DistanceFromLaunch:F0}m, " +
                $"maxDistance={pending.MaxDistanceFromLaunch:F0}m, duration={duration:F1}s, " +
                $"destroyed={pending.VesselDestroyed}, hasSnapshot={pending.VesselSnapshot != null}, " +
                $"recommended={recommended}");

            DialogGUIButton[] buttons;

            switch (recommended)
            {
                case RecordingStore.MergeDefault.MergeOnly:
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
                            ParsekLog.Log("User chose: Merge to Timeline (vessel destroyed)");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ParsekLog.ScreenMessage("Recording discarded", 2f);
                            ParsekLog.Log("User chose: Discard");
                        })
                    };
                    break;

                default: // Recover or Persist — vessel intact with snapshot
                    buttons = new[]
                    {
                        new DialogGUIButton("Merge + Keep Vessel", () =>
                        {
                            // Defer spawn — vessel appears when ghost finishes at EndUT
                            RecordingStore.CommitPending();
                            ParsekScenario.ReserveSnapshotCrew();
                            ParsekScenario.SwapReservedCrewInFlight();
                            ParsekLog.ScreenMessage("Recording merged — vessel will appear after ghost playback", 3f);
                            ParsekLog.Log("User chose: Merge + Keep Vessel (deferred spawn)");
                        }),
                        new DialogGUIButton("Merge + Recover", () =>
                        {
                            RecordingStore.CommitPending();
                            if (pending.VesselSnapshot != null)
                            {
                                ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                                VesselSpawner.RecoverVessel(pending.VesselSnapshot);
                            }
                            // Clear snapshot so ghost despawns normally at EndUT
                            pending.VesselSnapshot = null;
                            ParsekLog.ScreenMessage("Recording merged, vessel recovered!", 3f);
                            ParsekLog.Log("User chose: Merge + Recover");
                        }),
                        new DialogGUIButton("Discard", () =>
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            RecordingStore.DiscardPending();
                            ParsekLog.ScreenMessage("Recording discarded", 2f);
                            ParsekLog.Log("User chose: Discard");
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
            var recommended = RecordingStore.GetRecommendedAction(
                pending.DistanceFromLaunch, pending.VesselDestroyed,
                pending.VesselSnapshot != null,
                duration, pending.MaxDistanceFromLaunch);

            ParsekLog.Log($"Chain merge dialog: chain={chainId}, segments={totalSegments}, " +
                $"recommended={recommended}, hasSnapshot={pending.VesselSnapshot != null}");

            DialogGUIButton[] buttons;

            if (pending.VesselSnapshot != null && !pending.VesselDestroyed)
            {
                buttons = new[]
                {
                    new DialogGUIButton("Merge + Keep Vessel", () =>
                    {
                        RecordingStore.CommitPending();
                        ParsekScenario.ReserveSnapshotCrew();
                        ParsekScenario.SwapReservedCrewInFlight();
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged — vessel will appear!", 3f);
                        ParsekLog.Log($"User chose: Chain Merge + Keep Vessel ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Merge + Recover", () =>
                    {
                        RecordingStore.CommitPending();
                        // Recover and null snapshots on ALL chain segments (siblings + pending)
                        if (pending.VesselSnapshot != null)
                        {
                            ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                            VesselSpawner.RecoverVessel(pending.VesselSnapshot);
                        }
                        pending.VesselSnapshot = null;
                        NullChainSiblingSnapshots(chainSiblings);
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) merged, vessel recovered!", 3f);
                        ParsekLog.Log($"User chose: Chain Merge + Recover ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
                    {
                        DiscardChain(pending, chainId);
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) discarded", 2f);
                        ParsekLog.Log($"User chose: Chain Discard ({totalSegments} segments)");
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
                        ParsekLog.Log($"User chose: Chain Merge to Timeline ({totalSegments} segments)");
                    }),
                    new DialogGUIButton("Discard All", () =>
                    {
                        DiscardChain(pending, chainId);
                        ParsekLog.ScreenMessage($"Mission chain ({totalSegments} segments) discarded", 2f);
                        ParsekLog.Log($"User chose: Chain Discard ({totalSegments} segments)");
                    })
                };
            }

            string message = $"Mission chain ({totalSegments} segments)\n" +
                $"Vessel: {pending.VesselName}\n" +
                $"Duration: {duration:F1}s\n" +
                $"Distance: {pending.DistanceFromLaunch:F0}m\n\n";

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
        /// Unreserves crew and nulls VesselSnapshot on all committed chain siblings.
        /// Used by Recover and Merge-to-Timeline to prevent mid-chain segments from spawning.
        /// </summary>
        static void NullChainSiblingSnapshots(List<RecordingStore.Recording> siblings)
        {
            if (siblings == null) return;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].VesselSnapshot != null)
                {
                    ParsekScenario.UnreserveCrewInSnapshot(siblings[i].VesselSnapshot);
                    VesselSpawner.RecoverVessel(siblings[i].VesselSnapshot);
                    siblings[i].VesselSnapshot = null;
                    ParsekLog.Log($"Chain sibling #{i} snapshot nulled + recovered");
                }
            }
        }

        static void DiscardChain(RecordingStore.Recording pending, string chainId)
        {
            // Unreserve crew from pending
            if (pending.VesselSnapshot != null)
                ParsekScenario.UnreserveCrewInSnapshot(pending.VesselSnapshot);

            // Unreserve crew from all committed chain siblings
            var siblings = RecordingStore.GetChainRecordings(chainId);
            if (siblings != null)
            {
                for (int i = 0; i < siblings.Count; i++)
                {
                    if (siblings[i].VesselSnapshot != null)
                        ParsekScenario.UnreserveCrewInSnapshot(siblings[i].VesselSnapshot);
                }
            }

            // Remove committed chain recordings and discard pending
            RecordingStore.RemoveChainRecordings(chainId);
            RecordingStore.DiscardPending();
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
                case RecordingStore.MergeDefault.Recover:
                    return header + "Your vessel hasn't moved far from the launch site.";

                case RecordingStore.MergeDefault.MergeOnly:
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
