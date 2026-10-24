using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using PreFlightTests;

namespace Parsek.Patches
{
    /// <summary>
    /// GC-9: the stock launch-site clear may not recover a vessel a committed future flight
    /// holds (<see cref="ClaimedVesselDisposalGate"/>). Recovering it pays its value, and the
    /// chain tip later spawns the recorded form with the same parts, so the vessel exists
    /// twice and a second recovery pays again.
    ///
    /// <para>Stock paths (decompiled KSP 1.12.5): the editor's pre-flight
    /// <c>LaunchSiteClear</c> test, whose proceed option ("Recover and launch") runs
    /// <c>EditorLogic.proceedWithVesselLaunch</c>, which recovers every vessel the test found;
    /// the Space Center launch-site picker, <c>LaunchSiteFacility.shipSelected</c>, which builds
    /// its own dialog around the same test and whose proceed button runs <c>ClearSite</c> and
    /// then <c>launchChecks</c>; and <c>FlightDriver.Start</c> NEW_FROM_FILE, which recovers
    /// every vessel landed at the site and is reached in stock only through
    /// <c>FlightDriver.StartWithNewLaunch</c> after one of those two checks passed.</para>
    ///
    /// <para>The test fails while a held vessel stands at the site (so a debris-only site,
    /// which stock passes and recovers silently, shows the dialog too), its warning text gains
    /// the hold sentence, and the editor dialog loses its proceed option (stock
    /// <c>PreFlightCheck</c> builds an abort-only dialog for a null proceed option). The Space
    /// Center dialog keeps its proceed label and greys that button through stock's own
    /// <c>DialogGUIBase.OptionInteractableCondition</c>; its click-blocks are the
    /// <c>ClearSite</c> and <c>launchChecks</c> prefixes, which read the same predicate.</para>
    /// </summary>
    internal static class ClaimedVesselLaunchSite
    {
        /// <summary>The stock name of the Space Center launch-site warning dialog.</summary>
        internal const string FacilityDialogName = "LaunchSiteWarning";

        private sealed class Memo
        {
            internal List<ClaimedVesselDisposalGate.HeldVessel> Held;
            internal string StockProceedText;
        }

        private static ConditionalWeakTable<LaunchSiteClear, Memo> memos =
            new ConditionalWeakTable<LaunchSiteClear, Memo>();

        /// <summary>
        /// True while <c>LaunchSiteFacility.shipSelected</c> runs: its dialog builds the
        /// proceed button from <c>GetProceedOption</c>'s text, so the label must stay stock
        /// there (the button is greyed instead).
        /// </summary>
        internal static bool InFacilityDialog;

        internal static void ResetForTesting()
        {
            memos = new ConditionalWeakTable<LaunchSiteClear, Memo>();
            InFacilityDialog = false;
        }

        /// <summary>Pure: the launch-site test's result; it fails while any vessel there is held.</summary>
        internal static bool TestResult(bool stockResult, int heldCount)
        {
            return heldCount > 0 ? false : stockResult;
        }

        /// <summary>
        /// Pure: the proceed option. Null (stock's abort-only dialog) while a vessel is held,
        /// except where the caller builds its own button from the text
        /// (<paramref name="keepStockLabel"/>), which greys it instead.
        /// </summary>
        internal static string ProceedOption(string stockProceed, int heldCount, bool keepStockLabel)
        {
            if (heldCount <= 0 || keepStockLabel) return stockProceed;
            return null;
        }

        /// <summary>Pure: stock's warning text plus one hold sentence per held vessel.</summary>
        internal static string WarningDescription(string stockDescription,
            IList<ClaimedVesselDisposalGate.HeldVessel> held, Func<double, string> formatDate)
        {
            if (held == null || held.Count == 0) return stockDescription;
            var sb = new StringBuilder(stockDescription ?? string.Empty);
            for (int i = 0; i < held.Count; i++)
            {
                ClaimedVesselDisposalGate.HeldVessel h = held[i];
                if (sb.Length > 0) sb.Append(i == 0 ? "\n\n" : "\n");
                sb.Append("'").Append(string.IsNullOrEmpty(h.Name) ? "?" : h.Name).Append("': ");
                sb.Append(ReservationExplanation.ClaimedVesselHold(
                    h.Claiming != null ? h.Claiming.SpawnUT : double.NaN, formatDate));
            }
            return sb.ToString();
        }

        internal static void Remember(LaunchSiteClear test, List<ClaimedVesselDisposalGate.HeldVessel> held)
        {
            if (test == null) return;
            memos.Remove(test);
            if (held != null && held.Count > 0) memos.Add(test, new Memo { Held = held });
        }

        internal static bool TryGetHeld(LaunchSiteClear test, out List<ClaimedVesselDisposalGate.HeldVessel> held)
        {
            held = null;
            Memo memo;
            if (test == null || !memos.TryGetValue(test, out memo)) return false;
            held = memo.Held;
            return held != null && held.Count > 0;
        }

        internal static void RememberProceedText(LaunchSiteClear test, string stockProceed)
        {
            Memo memo;
            if (test != null && memos.TryGetValue(test, out memo)) memo.StockProceedText = stockProceed;
        }

        internal static string RememberedProceedText(LaunchSiteClear test)
        {
            Memo memo;
            return test != null && memos.TryGetValue(test, out memo) ? memo.StockProceedText : null;
        }

        /// <summary>Live: the held vessels among those the test finds at its site.</summary>
        internal static List<ClaimedVesselDisposalGate.HeldVessel> FindHeldAt(LaunchSiteClear test, string site)
        {
            if (test == null) return new List<ClaimedVesselDisposalGate.HeldVessel>();
            List<ProtoVessel> obstructing = null;
            try
            {
                obstructing = test.GetObstructingVessels();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(ClaimedVesselDisposalGate.Tag, "site-obstructions-read-failed",
                    "Launch-site obstructions read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return ClaimedVesselDisposalGate.FindHeldLive(obstructing, site);
        }

        /// <summary>One Info line per held vessel (bounded by the vessels at one site).</summary>
        internal static void LogHeld(string action, List<ClaimedVesselDisposalGate.HeldVessel> held)
        {
            if (held == null) return;
            for (int i = 0; i < held.Count; i++)
                ClaimedVesselDisposalGate.LogBlocked(action, held[i].Pid, held[i].Name, held[i].Claiming);
        }

        /// <summary>
        /// Greys the proceed button of the open Space Center launch-site dialog (the button
        /// whose label is <paramref name="proceedText"/>) through stock's interactable
        /// condition. Returns how many buttons it greyed.
        /// </summary>
        internal static int GreyFacilityProceedButton(string proceedText)
        {
            if (string.IsNullOrEmpty(proceedText)) return 0;
            List<PopupDialog> popups = Traverse.Create(typeof(PopupDialog))
                .Field("instantiatedPopUps").GetValue<List<PopupDialog>>();
            if (popups == null) return 0;
            int greyed = 0;
            for (int i = 0; i < popups.Count; i++)
            {
                PopupDialog popup = popups[i];
                if (popup == null || popup.dialogToDisplay == null) continue;
                if (popup.dialogToDisplay.name != FacilityDialogName) continue;
                DialogGUIBase[] options = popup.dialogToDisplay.Options;
                if (options == null) continue;
                for (int j = 0; j < options.Length; j++)
                {
                    var button = options[j] as DialogGUIButton;
                    if (button == null || button.OptionText != proceedText) continue;
                    // The dialog is modal and the hold cannot lift while it is open.
                    button.OptionInteractableCondition = () => false;
                    greyed++;
                }
            }
            return greyed;
        }
    }

    /// <summary>The launch-site test fails while a vessel standing there is held.</summary>
    [HarmonyPatch(typeof(LaunchSiteClear), nameof(LaunchSiteClear.Test))]
    internal static class ClaimedVesselLaunchSiteTestPatch
    {
        static void Postfix(LaunchSiteClear __instance, ref bool __result)
        {
            if (ParsekGameModeGate.CheckInert("ClaimedVesselLaunchSiteTestPatch.Postfix")) return;
            if (__instance == null) return;
            List<ClaimedVesselDisposalGate.HeldVessel> held =
                ClaimedVesselLaunchSite.FindHeldAt(__instance, "launch-site test");
            ClaimedVesselLaunchSite.Remember(__instance, held);
            if (held.Count == 0) return;
            ClaimedVesselLaunchSite.LogHeld("launch-site clear (stock test fails, no Recover)", held);
            __result = ClaimedVesselLaunchSite.TestResult(__result, held.Count);
        }
    }

    /// <summary>No "Recover and launch" while a vessel at the site is held.</summary>
    [HarmonyPatch(typeof(LaunchSiteClear), nameof(LaunchSiteClear.GetProceedOption))]
    internal static class ClaimedVesselLaunchSiteProceedPatch
    {
        static void Postfix(LaunchSiteClear __instance, ref string __result)
        {
            if (ParsekGameModeGate.CheckInert("ClaimedVesselLaunchSiteProceedPatch.Postfix")) return;
            if (!ClaimedVesselLaunchSite.TryGetHeld(__instance, out List<ClaimedVesselDisposalGate.HeldVessel> held))
                return;
            ClaimedVesselLaunchSite.RememberProceedText(__instance, __result);
            __result = ClaimedVesselLaunchSite.ProceedOption(__result, held.Count,
                ClaimedVesselLaunchSite.InFacilityDialog);
        }
    }

    /// <summary>The refused launch-site dialog says why.</summary>
    [HarmonyPatch(typeof(LaunchSiteClear), nameof(LaunchSiteClear.GetWarningDescription))]
    internal static class ClaimedVesselLaunchSiteDescriptionPatch
    {
        static void Postfix(LaunchSiteClear __instance, ref string __result)
        {
            if (ParsekGameModeGate.CheckInert("ClaimedVesselLaunchSiteDescriptionPatch.Postfix")) return;
            if (!ClaimedVesselLaunchSite.TryGetHeld(__instance, out List<ClaimedVesselDisposalGate.HeldVessel> held))
                return;
            __result = ClaimedVesselLaunchSite.WarningDescription(__result, held,
                ReservationExplanation.DefaultDateFormatter);
        }
    }

    /// <summary>
    /// The Space Center launch-site dialog: keeps the proceed label stock while it is built,
    /// then greys that button for a held vessel.
    /// </summary>
    [HarmonyPatch(typeof(LaunchSiteFacility), "shipSelected")]
    internal static class ClaimedVesselLaunchSiteFacilityDialogPatch
    {
        private static readonly AccessTools.FieldRef<LaunchSiteFacility, LaunchSiteClear> TestRef =
            AccessTools.FieldRefAccess<LaunchSiteFacility, LaunchSiteClear>("launchSiteClearTest");

        static void Prefix()
        {
            ClaimedVesselLaunchSite.InFacilityDialog = true;
        }

        static void Postfix(LaunchSiteFacility __instance)
        {
            ClaimedVesselLaunchSite.InFacilityDialog = false;
            if (ParsekGameModeGate.CheckInert("ClaimedVesselLaunchSiteFacilityDialogPatch.Postfix")) return;
            if (__instance == null) return;
            try
            {
                LaunchSiteClear test = TestRef(__instance);
                if (!ClaimedVesselLaunchSite.TryGetHeld(test, out List<ClaimedVesselDisposalGate.HeldVessel> held))
                    return;
                int greyed = ClaimedVesselLaunchSite.GreyFacilityProceedButton(
                    ClaimedVesselLaunchSite.RememberedProceedText(test));
                if (greyed > 0)
                    ClaimedVesselLaunchSite.LogHeld("Space Center launch-site Recover (greyed)", held);
                else
                    ParsekLog.Warn(ClaimedVesselDisposalGate.Tag,
                        "Space Center launch-site dialog: proceed button not found to grey; the ClearSite"
                        + " and launchChecks blocks still refuse it");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(ClaimedVesselDisposalGate.Tag, "facility-grey-failed",
                    "Space Center launch-site proceed grey failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Finalizer()
        {
            ClaimedVesselLaunchSite.InFacilityDialog = false;
        }
    }

    /// <summary>The Space Center launch-site clear's click-block: no recovery of a held vessel.</summary>
    [HarmonyPatch(typeof(LaunchSiteFacility), "ClearSite")]
    internal static class ClaimedVesselLaunchSiteClearSitePatch
    {
        private static readonly AccessTools.FieldRef<LaunchSiteFacility, LaunchSiteClear> TestRef =
            AccessTools.FieldRefAccess<LaunchSiteFacility, LaunchSiteClear>("launchSiteClearTest");

        static bool Prefix(LaunchSiteFacility __instance)
        {
            if (ParsekGameModeGate.CheckInert("ClaimedVesselLaunchSiteClearSitePatch.Prefix")) return true;
            if (__instance == null) return true;
            List<ClaimedVesselDisposalGate.HeldVessel> held =
                ClaimedVesselLaunchSite.FindHeldAt(TestRef(__instance), "ClearSite");
            if (held.Count == 0) return true;
            ClaimedVesselLaunchSite.LogHeld("Space Center launch-site Recover", held);
            return false;
        }
    }

    /// <summary>
    /// The launch that follows a refused clear: stock's proceed button calls
    /// <c>launchChecks</c> after <c>ClearSite</c>, and <c>FlightDriver.Start</c> would recover
    /// the held vessel itself, so the launch is cancelled while one still stands at the site.
    /// </summary>
    [HarmonyPatch(typeof(LaunchSiteFacility), "launchChecks")]
    internal static class ClaimedVesselLaunchSiteLaunchChecksPatch
    {
        private static readonly AccessTools.FieldRef<LaunchSiteFacility, LaunchSiteClear> TestRef =
            AccessTools.FieldRefAccess<LaunchSiteFacility, LaunchSiteClear>("launchSiteClearTest");
        private static readonly AccessTools.FieldRef<LaunchSiteFacility, bool> AwaitingRef =
            AccessTools.FieldRefAccess<LaunchSiteFacility, bool>("awaitingLaunchClear");
        private static readonly AccessTools.FieldRef<LaunchSiteFacility, List<MissionRecoveryDialog>> DialogsRef =
            AccessTools.FieldRefAccess<LaunchSiteFacility, List<MissionRecoveryDialog>>("missionDialogs");

        static bool Prefix(LaunchSiteFacility __instance)
        {
            if (ParsekGameModeGate.CheckInert("ClaimedVesselLaunchSiteLaunchChecksPatch.Prefix")) return true;
            if (__instance == null) return true;
            // Stock returns early in both cases; nothing launches yet.
            List<MissionRecoveryDialog> dialogs = DialogsRef(__instance);
            if (dialogs != null && dialogs.Count > 0) return true;
            if (!AwaitingRef(__instance)) return true;
            List<ClaimedVesselDisposalGate.HeldVessel> held =
                ClaimedVesselLaunchSite.FindHeldAt(TestRef(__instance), "launchChecks");
            if (held.Count == 0) return true;
            ClaimedVesselLaunchSite.LogHeld("Space Center launch over a claimed vessel (cancelled)", held);
            AwaitingRef(__instance) = false;
            AccessTools.Method(typeof(LaunchSiteFacility), "Cancel")?.Invoke(__instance, null);
            return false;
        }
    }
}
