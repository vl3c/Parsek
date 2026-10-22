using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI.Screens;
using PreFlightTests;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek.Patches
{
    /// <summary>
    /// The launch half of the click reservation (committed-value rule, design 3.13 rule 3):
    /// a launch is refused only for its cost, when that cost is above the funds the
    /// committed timeline leaves spendable now (<see cref="ClickReservation"/>). Every stock
    /// launch path runs stock's <c>PreFlightTests.CanAffordLaunchTest</c> (KSP 1.12.5,
    /// decompiled: the editor's <c>EditorLogic.GetStockPreFlightCheck</c> from
    /// <c>launchVessel(siteName)</c>, and the Space Center launchpad / runway picker's
    /// <c>LaunchSiteFacility.launchChecks</c>, reached from <c>VesselSpawnDialog.ButtonLaunch</c>
    /// and a double-click on a craft row), so its <c>Test()</c> is the backstop: it fails
    /// where Parsek refuses and its stock dialog (title, abort button, no proceed option)
    /// carries the reason. The marks are the editor's Launch button and the picker's Launch
    /// button, greyed with the same reason through the same decision
    /// (<see cref="EvaluateLaunchNow"/>), over the same cost stock's test reads
    /// (<c>ShipConstruct.GetShipCosts</c> with the ship manifest; <c>ShipTemplate.totalCost</c>).
    /// </summary>
    internal static class LaunchReservation
    {
        internal const string Tag = "LaunchReserve";

        /// <summary>The one launch decision: the click reservation over the launch cost.</summary>
        internal static PurchaseRefusal EvaluateLaunchNow(double cost)
        {
            return ClickReservationGate.EvaluatePurchaseNow(StockPool.Funds, cost, "launch");
        }

        /// <summary>Pure: stock's affordability test result after Parsek's refusal.</summary>
        internal static bool LaunchTestResult(bool stockResult, PurchaseRefusal r)
        {
            return stockResult && !r.Refused;
        }

        /// <summary>Pure: stock's launch-test description with the reason appended for a
        /// refused launch, unchanged otherwise.</summary>
        internal static string ComposeWarningDescription(string stockDescription, PurchaseRefusal r)
        {
            if (!r.Refused) return stockDescription;
            string reason = ClickReservationGate.PurchaseReason(r);
            return string.IsNullOrEmpty(stockDescription) ? reason : stockDescription + "\n\n" + reason;
        }

        /// <summary>Pure: what a launch button must be set to: false while refused, stock's own
        /// value when Parsek greyed it before and the refusal lifted, else null (leave it).</summary>
        internal static bool? LaunchButtonInteractable(bool stockInteractable, bool refused, bool disabledByParsek)
        {
            if (stockInteractable && refused) return false;
            if (disabledByParsek) return stockInteractable;
            return null;
        }

        // Tests that Parsek failed, with their refusal, for the description postfix.
        private static ConditionalWeakTable<CanAffordLaunchTest, RefusalBox> refusedTests =
            new ConditionalWeakTable<CanAffordLaunchTest, RefusalBox>();

        private sealed class RefusalBox
        {
            internal PurchaseRefusal Refusal;
        }

        internal static void RememberRefused(CanAffordLaunchTest test, PurchaseRefusal r)
        {
            if (test == null) return;
            refusedTests.Remove(test);
            if (r.Refused) refusedTests.Add(test, new RefusalBox { Refusal = r });
        }

        internal static bool TryGetRefused(CanAffordLaunchTest test, out PurchaseRefusal r)
        {
            r = default(PurchaseRefusal);
            RefusalBox box;
            if (test == null || !refusedTests.TryGetValue(test, out box)) return false;
            r = box.Refusal;
            return true;
        }

        internal static void ResetForTesting()
        {
            refusedTests = new ConditionalWeakTable<CanAffordLaunchTest, RefusalBox>();
        }
    }

    /// <summary>The launch backstop: stock's affordability test fails where Parsek refuses the launch.</summary>
    [HarmonyPatch(typeof(CanAffordLaunchTest), nameof(CanAffordLaunchTest.Test))]
    internal static class CanAffordLaunchTestPatch
    {
        private static readonly AccessTools.FieldRef<CanAffordLaunchTest, float> LaunchCostRef =
            AccessTools.FieldRefAccess<CanAffordLaunchTest, float>("launchCost");

        static void Postfix(CanAffordLaunchTest __instance, ref bool __result)
        {
            if (ParsekGameModeGate.CheckInert("CanAffordLaunchTestPatch.Postfix")) return; // S9 game-mode gate
            if (__instance == null || !__result) return;
            try
            {
                double cost = LaunchCostRef(__instance);
                PurchaseRefusal r = LaunchReservation.EvaluateLaunchNow(cost);
                LaunchReservation.RememberRefused(__instance, r);
                bool result = LaunchReservation.LaunchTestResult(__result, r);
                if (result != __result)
                {
                    ParsekLog.Info(LaunchReservation.Tag, "Launch refused, reserved for later costs: cost="
                        + r.Cost.ToString("R", CultureInfo.InvariantCulture) + " spendable="
                        + r.Spendable.ToString("R", CultureInfo.InvariantCulture)
                        + " - stock's CanAffordLaunchTest fails with the reason");
                    __result = result;
                }
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(LaunchReservation.Tag, "launch-test-failed",
                    "Launch reservation check failed (" + ex.GetType().Name + ": " + ex.Message + ") - stock's result kept");
            }
        }
    }

    /// <summary>The refused launch's stock dialog says why.</summary>
    [HarmonyPatch(typeof(CanAffordLaunchTest), nameof(CanAffordLaunchTest.GetWarningDescription))]
    internal static class CanAffordLaunchTestDescriptionPatch
    {
        static void Postfix(CanAffordLaunchTest __instance, ref string __result)
        {
            PurchaseRefusal r;
            if (!LaunchReservation.TryGetRefused(__instance, out r)) return;
            __result = LaunchReservation.ComposeWarningDescription(__result, r);
        }
    }

    /// <summary>
    /// The editor's Launch button: greyed with the reason while the ship's cost is refused.
    /// Stock writes <c>launchBtn.interactable</c> only in <c>EditorLogic.UpdateUI</c> (from the
    /// <c>EDITOR_LAUNCH</c> input lock), so the mark is re-applied after it, on every ship
    /// change and on a one-second poll (the clock and the pools move too).
    /// </summary>
    [HarmonyPatch(typeof(EditorLogic), nameof(EditorLogic.UpdateUI))]
    internal static class EditorLaunchButtonUpdateUiPatch
    {
        static void Postfix(EditorLogic __instance)
        {
            EditorLaunchMark.Apply(__instance, "UpdateUI");
        }
    }

    /// <summary>The editor Launch mark's driver: ship changes and a one-second poll.</summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    internal sealed class EditorLaunchMark : MonoBehaviour
    {
        private const float PollSeconds = 1f;
        private float nextPoll;
        private static bool disabledByParsek;

        private void Start()
        {
            disabledByParsek = false;
            GameEvents.onEditorShipModified.Add(OnShipModified);
        }

        private void OnDestroy()
        {
            GameEvents.onEditorShipModified.Remove(OnShipModified);
        }

        private void OnShipModified(ShipConstruct ship)
        {
            Apply(EditorLogic.fetch, "ship-modified");
        }

        private void Update()
        {
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + PollSeconds;
            Apply(EditorLogic.fetch, "poll");
        }

        internal static void Apply(EditorLogic editor, string reason)
        {
            if (editor == null || editor.launchBtn == null) return;
            if (ParsekGameModeGate.CheckInert("EditorLaunchMark.Apply")) return; // S9 game-mode gate
            try
            {
                Button button = editor.launchBtn;
                bool stockInteractable = InputLockManager.IsUnlocked(ControlTypes.EDITOR_LAUNCH);
                PurchaseRefusal r = default(PurchaseRefusal);
                if (stockInteractable && EditorLogic.fetch != null && EditorLogic.fetch.ship != null
                    && EditorLogic.fetch.ship.parts != null && EditorLogic.fetch.ship.parts.Count > 0)
                {
                    float dry, fuel;
                    double cost = editor.ship.GetShipCosts(out dry, out fuel, ShipConstruction.ShipManifest);
                    r = LaunchReservation.EvaluateLaunchNow(cost);
                }
                bool? write = LaunchReservation.LaunchButtonInteractable(stockInteractable, r.Refused, disabledByParsek);
                if (write.HasValue && button.interactable != write.Value)
                    button.interactable = write.Value;
                bool greyed = stockInteractable && r.Refused;
                if (greyed != disabledByParsek)
                    ParsekLog.Info(LaunchReservation.Tag, "Editor Launch button " + (greyed ? "disabled" : "given back")
                        + " (" + (reason ?? "?") + ")" + (greyed ? " why=\"" + ClickReservationGate.PurchaseReason(r) + "\"" : ""));
                disabledByParsek = greyed;
                StockUiReasonTooltip.Sync(button, greyed, greyed ? ClickReservationGate.PurchaseReason(r) : null,
                    editor, "EditorLaunch");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(LaunchReservation.Tag, "editor-launch-mark-failed",
                    "Editor Launch mark failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// The Space Center launchpad / runway craft picker's Launch button: stock sets it from
    /// the selected craft's validity in the private <c>VesselSpawnDialog.UpdateVesselInfoPanel</c>;
    /// the postfix greys it with the reason when the craft's cost (<c>ShipTemplate.totalCost</c>,
    /// the cost <c>LaunchSiteFacility.launchChecks</c> hands stock's affordability test) is
    /// refused.
    /// </summary>
    [HarmonyPatch]
    internal static class VesselSpawnDialogLaunchButtonPatch
    {
        private static readonly AccessTools.FieldRef<VesselSpawnDialog, Button> ButtonLaunchRef =
            AccessTools.FieldRefAccess<VesselSpawnDialog, Button>("buttonLaunch");
        private static bool disabledByParsek;

        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(typeof(VesselSpawnDialog), "UpdateVesselInfoPanel");
            if (method == null)
                ParsekLog.Warn(LaunchReservation.Tag,
                    "VesselSpawnDialog.UpdateVesselInfoPanel not found - the Space Center Launch button is not greyed; " +
                    "stock's CanAffordLaunchTest backstop still refuses");
            return method;
        }

        static void Postfix(VesselSpawnDialog __instance, object dataItem)
        {
            if (ParsekGameModeGate.CheckInert("VesselSpawnDialogLaunchButtonPatch.Postfix")) return; // S9 game-mode gate
            if (__instance == null || dataItem == null) return;
            try
            {
                Button button = ButtonLaunchRef(__instance);
                if (button == null) return;
                var item = Traverse.Create(dataItem);
                bool valid = item.Field("isValid").GetValue<bool>();
                PurchaseRefusal r = default(PurchaseRefusal);
                if (valid)
                {
                    ShipTemplate template = item.Property("template").GetValue<ShipTemplate>();
                    if (template != null)
                        r = LaunchReservation.EvaluateLaunchNow(template.totalCost);
                }
                bool? write = LaunchReservation.LaunchButtonInteractable(valid, r.Refused, disabledByParsek);
                if (write.HasValue && button.interactable != write.Value)
                    button.interactable = write.Value;
                bool greyed = valid && r.Refused;
                if (greyed != disabledByParsek)
                    ParsekLog.Info(LaunchReservation.Tag, "Space Center Launch button " + (greyed ? "disabled" : "given back")
                        + (greyed ? " why=\"" + ClickReservationGate.PurchaseReason(r) + "\"" : ""));
                disabledByParsek = greyed;
                StockUiReasonTooltip.Sync(button, greyed, greyed ? ClickReservationGate.PurchaseReason(r) : null,
                    __instance, "SpaceCenterLaunch");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(LaunchReservation.Tag, "ksc-launch-mark-failed",
                    "Space Center Launch mark failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}
