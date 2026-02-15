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
