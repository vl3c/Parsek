using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    public static class RestorePointDialog
    {
        internal static void ShowPicker()
        {
            var rps = RestorePointStore.RestorePoints;
            if (rps == null || rps.Count == 0) return;

            // Sort by UT ascending
            var sorted = new List<RestorePoint>(rps);
            sorted.Sort((a, b) => a.UT.CompareTo(b.UT));

            // Build message
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Select a launch point to return to:\n");

            for (int i = 0; i < sorted.Count; i++)
            {
                var rp = sorted[i];
                string date = KSPUtil.dateTimeFormatter.PrintDateCompact(rp.UT, true);
                string funds = rp.Funds.ToString("N0", CultureInfo.InvariantCulture);
                string sci = rp.Science.ToString("F1", CultureInfo.InvariantCulture);
                string rep = rp.Reputation.ToString("F1", CultureInfo.InvariantCulture);
                sb.AppendLine($"{i + 1}. {rp.Label}");
                sb.AppendLine($"   {date}  |  Funds: {funds}  Science: {sci}  Rep: {rep}");

                if (!rp.SaveFileExists)
                    sb.AppendLine("   (save file missing)");
                sb.AppendLine();
            }

            sb.AppendLine("All committed recordings will replay as ghosts.");
            sb.AppendLine("Uncommitted progress since the selected point will be lost.");

            // Build buttons — one "Go Back" + one "Delete" per RP, plus Cancel
            var buttons = new List<DialogGUIButton>();
            for (int i = 0; i < sorted.Count; i++)
            {
                var capturedRp = sorted[i];
                if (capturedRp.SaveFileExists)
                {
                    buttons.Add(new DialogGUIButton($"Go Back: {capturedRp.Label}", () => ShowConfirmation(capturedRp)));
                }
                buttons.Add(new DialogGUIButton($"Delete: {capturedRp.Label}", () => ShowDeleteConfirmation(capturedRp)));
            }
            buttons.Add(new DialogGUIButton("Cancel", () => { }));

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRestorePointPicker",
                    sb.ToString(),
                    "Parsek \u2014 Go Back in Time",
                    HighLogic.UISkin,
                    buttons.ToArray()
                ),
                false,
                HighLogic.UISkin
            );
        }

        internal static void ShowConfirmation(RestorePoint rp)
        {
            string date = KSPUtil.dateTimeFormatter.PrintDateCompact(rp.UT, true);
            int futureCount = RestorePointStore.CountFutureRecordings(rp.UT);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Going back to {date} (\"{rp.Label}\").\n");
            sb.AppendLine($"\u2022 {futureCount} future recording(s) will replay as ghost(s)");
            sb.AppendLine("\u2022 Game state (funds, tech, facilities) will revert to this launch point");
            sb.AppendLine("\u2022 Any uncommitted progress will be lost");

            var capturedRp = rp;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRestorePointConfirm",
                    sb.ToString(),
                    "Parsek \u2014 Confirm Go Back",
                    HighLogic.UISkin,
                    new DialogGUIButton("Confirm", () =>
                    {
                        ParsekLog.Info("RestorePoint",
                            string.Format(CultureInfo.InvariantCulture,
                                "User confirmed go-back to UT {0}: \"{1}\"", capturedRp.UT, capturedRp.Label));
                        RestorePointStore.InitiateGoBack(capturedRp);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("RestorePoint", "User cancelled go-back confirmation");
                    })
                ),
                false,
                HighLogic.UISkin
            );
        }

        internal static void ShowDeleteConfirmation(RestorePoint rp)
        {
            string message = $"Delete restore point \"{rp.Label}\"?\nThe save file will be removed.";

            var capturedRp = rp;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRestorePointDelete",
                    message,
                    "Parsek \u2014 Delete Restore Point",
                    HighLogic.UISkin,
                    new DialogGUIButton("Delete", () =>
                    {
                        ParsekLog.Info("RestorePoint", $"User deleted restore point: \"{capturedRp.Label}\" (id={capturedRp.Id})");
                        RestorePointStore.DeleteRestorePoint(capturedRp.Id);
                        // Re-show picker if more restore points exist
                        if (RestorePointStore.HasRestorePoints)
                            ShowPicker();
                    }),
                    new DialogGUIButton("Cancel", () => { })
                ),
                false,
                HighLogic.UISkin
            );
        }
    }
}
