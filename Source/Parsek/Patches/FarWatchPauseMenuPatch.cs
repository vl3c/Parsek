using System;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// The pause menu during a far watch (design-far-watch.md 9.3): Save, Load, Revert, Space
    /// Center, Tracking Station, Quit and Main Menu are greyed through stock's own interactable
    /// condition, with stock's own <c>tooltipText</c> carrying "Stop watching first ([ or ])".
    /// The condition reads the far watch live, so the buttons come back the moment it ends.
    /// </summary>
    [HarmonyPatch(typeof(PauseMenu), "draw")]
    internal static class FarWatchPauseMenuPatch
    {
        internal static readonly string[] BlockedLabelKeys =
        {
            "#autoLOC_360545", // Revert
            "#autoLOC_360553", // Load
            "#autoLOC_360569", // Save
            "#autoLOC_8004249", // Save (alternate label)
            "#autoLOC_360586", // Space Center
            "#autoLOC_360600", // Tracking Station
            "#autoLOC_360632", // Quit (opens stock's leave-scenario popup, which saves)
            "#autoLOC_360644", // Main Menu
        };

        static void Postfix(DialogGUIBase[] __result)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchPauseMenuPatch.Postfix")) return; // S9 game-mode gate
            if (__result == null || HighLogic.LoadedScene != GameScenes.FLIGHT)
                return;
            try
            {
                string[] labels = ResolveLabels();
                int wrapped = 0;
                for (int i = 0; i < __result.Length; i++)
                    wrapped += Wrap(__result[i], labels, 0);
                ParsekLog.Verbose("FarWatch", "pause menu buttons wrapped for the far-watch block count="
                    + wrapped.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("FarWatch", "pause menu wrap threw " + ex.GetType().Name);
            }
        }

        private static string[] ResolveLabels()
        {
            var labels = new string[BlockedLabelKeys.Length];
            for (int i = 0; i < BlockedLabelKeys.Length; i++)
            {
                string t;
                try { t = KSP.Localization.Localizer.Format(BlockedLabelKeys[i]); }
                catch (Exception) { t = null; }
                labels[i] = t;
            }
            return labels;
        }

        private static int Wrap(DialogGUIBase node, string[] labels, int depth)
        {
            if (node == null || depth > 8)
                return 0;
            int n = 0;
            var button = node as DialogGUIButton;
            if (button != null && IsBlockedLabel(button.OptionText, labels))
            {
                Func<bool> original = button.OptionInteractableCondition;
                string originalTip = button.tooltipText;
                DialogGUIButton b = button;
                button.OptionInteractableCondition = () =>
                {
                    bool blocked = IsFarWatchRunning();
                    b.tooltipText = blocked ? FarWatchShipSafety.StopWatchingFirstHoverText : originalTip;
                    return !blocked && (original == null || original());
                };
                n++;
            }
            if (node.children != null)
            {
                for (int k = 0; k < node.children.Count; k++)
                    n += Wrap(node.children[k], labels, depth + 1);
            }
            return n;
        }

        /// <summary>Pure: a pause-menu label is one of the blocked ones (stock may wrap it in a color tag).</summary>
        internal static bool IsBlockedLabel(string optionText, string[] labels)
        {
            if (string.IsNullOrEmpty(optionText) || labels == null)
                return false;
            for (int i = 0; i < labels.Length; i++)
            {
                string l = labels[i];
                if (!string.IsNullOrEmpty(l) && optionText.IndexOf(l, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsFarWatchRunning()
        {
            ParsekFlight f = ParsekFlight.Instance;
            return f != null && f.FarWatch != null && f.FarWatch.IsRunning;
        }
    }
}
