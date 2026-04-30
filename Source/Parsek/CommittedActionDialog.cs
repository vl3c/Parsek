using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static dialog helper for blocking actions that conflict with
    /// committed timeline events. Shows a PopupDialog explaining why.
    /// </summary>
    internal static class CommittedActionDialog
    {
        internal static void ShowBlocked(string actionDescription, string reason, string resourceDetail)
        {
            string message = actionDescription + "\n\n" + reason;
            if (!string.IsNullOrEmpty(resourceDetail))
                message += "\n\n" + resourceDetail;

            ParsekLog.Info("CommittedAction",
                $"Blocked action: {actionDescription} - {reason}" +
                (!string.IsNullOrEmpty(resourceDetail) ? $" ({resourceDetail})" : ""));

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekResourceBlock",
                    message,
                    "Action Blocked",
                    HighLogic.UISkin,
                    new[] { new DialogGUIButton("OK", () => { }) }
                ),
                false,
                HighLogic.UISkin
            );
        }
    }
}
