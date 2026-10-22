using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI;
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
    /// carries the reason. The marks are the editor's Launch button, the picker's Launch
    /// button and the launch-site flyout's per-site Launch buttons
    /// (<see cref="LaunchSiteButtonsMark"/>), greyed with the same reason through the same decision
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

        /// <summary>Pure: the backstop's manifest choice (the crew dialog's, else the ship's).</summary>
        internal static T PickManifest<T>(T dialogManifest, T shipManifest) where T : class
        {
            return dialogManifest ?? shipManifest;
        }

        /// <summary>Pure: what a launch-site flyout Launch button must be set to. Stock never
        /// writes these buttons' <c>interactable</c> (decompiled <c>EditorLaunchPadItem</c>), so
        /// false while refused, true when Parsek greyed it and the refusal lifted, else null.</summary>
        internal static bool? SiteButtonInteractable(bool refused, bool disabledByParsek)
        {
            if (refused) return false;
            if (disabledByParsek) return true;
            return null;
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

        /// <summary>
        /// The crew manifest the mark prices the ship with: the one
        /// <c>EditorLogic.launchVessel(siteName)</c> installs right before stock's pre-flight
        /// check (decompiled 1.12.5: <c>CrewAssignmentDialog.Instance.GetManifest()</c>, a clone),
        /// so the mark and the <c>CanAffordLaunchTest</c> backstop price the same crew
        /// inventory. When the dialog has none, stock falls back to
        /// <c>CrewRoster.DefaultCrewForVessel</c>, which can hire; the mark does not call that and
        /// reads the current <c>ShipConstruction.ShipManifest</c> instead (only crew inventory
        /// costs can differ, and only then).
        /// </summary>
        internal static VesselCrewManifest ManifestForLaunchCost()
        {
            VesselCrewManifest dialog = null;
            try
            {
                if (CrewAssignmentDialog.Instance != null) dialog = CrewAssignmentDialog.Instance.GetManifest();
            }
            catch (Exception)
            {
                dialog = null;
            }
            return LaunchReservation.PickManifest(dialog, ShipConstruction.ShipManifest);
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
                    double cost = editor.ship.GetShipCosts(out dry, out fuel, ManifestForLaunchCost());
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
                LaunchSiteButtonsMark.Remember(editor, greyed, r);
                LaunchSiteButtonsMark.ApplyAll(reason);
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
                LaunchSiteButtonsMark.Remember(__instance, greyed, r);
                LaunchSiteButtonsMark.ApplyAll("craft-selected");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(LaunchReservation.Tag, "ksc-launch-mark-failed",
                    "Space Center Launch mark failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}

namespace Parsek.Patches
{
    /// <summary>
    /// The launch-site flyout's per-site Launch buttons (<c>UILaunchsiteController</c>'s
    /// private <c>launchPadItems</c>, each an internal <c>EditorLaunchPadItem</c> whose
    /// <c>buttonLaunch</c> calls <c>EditorLogic.launchVessel(siteName)</c> in the editor and
    /// <c>VesselSpawnDialog.ButtonLaunch()</c> at the Space Center; KSP 1.12.5, decompiled).
    /// Both paths reach the <c>CanAffordLaunchTest</c> backstop, so the buttons carry the same
    /// decision as the main Launch button (pairing rule): the editor mark and the Space Center
    /// picker postfix <see cref="Remember"/> their decision and apply it to every flyout, and
    /// the flyout's pointer-enter (the moment it slides open) re-applies it. Stock never
    /// writes these buttons' <c>interactable</c>, so Parsek gives back only what it greyed.
    /// </summary>
    internal static class LaunchSiteButtonsMark
    {
        private static UnityEngine.Object owner;
        private static bool refused;
        private static PurchaseRefusal refusal;
        private static readonly ConditionalWeakTable<Button, object> greyedByParsek =
            new ConditionalWeakTable<Button, object>();
        private static readonly object Marker = new object();
        // Buttons Parsek has greyed and not yet given back. Zero with no refusal means there
        // is nothing to do, so a career with no cost ahead never scans for flyouts.
        private static int greyedCount;

        /// <summary>The current scene's decision (the editor, or the picker's selected craft).</summary>
        internal static void Remember(UnityEngine.Object decisionOwner, bool isRefused, PurchaseRefusal r)
        {
            owner = decisionOwner;
            refused = isRefused;
            refusal = r;
        }

        /// <summary>The remembered decision, while its owner is alive; allowed otherwise.</summary>
        private static bool Current(out PurchaseRefusal r)
        {
            r = refusal;
            return refused && owner != null;
        }

        /// <summary>Applies the remembered decision to every live flyout.</summary>
        internal static void ApplyAll(string source)
        {
            PurchaseRefusal ignored;
            if (!Current(out ignored) && greyedCount == 0) return;
            UILaunchsiteController[] controllers = UnityEngine.Object.FindObjectsOfType<UILaunchsiteController>();
            if (controllers == null) return;
            for (int i = 0; i < controllers.Length; i++)
                Apply(controllers[i], source);
            // Every live flyout was given back above; a tracked button not found was destroyed
            // with its flyout.
            if (!Current(out ignored))
                greyedCount = 0;
        }

        /// <summary>Applies the remembered decision to one flyout's per-site Launch buttons.</summary>
        internal static void Apply(UILaunchsiteController controller, string source)
        {
            if (controller == null) return;
            if (ParsekGameModeGate.CheckInert("LaunchSiteButtonsMark.Apply")) return; // S9 game-mode gate
            try
            {
                PurchaseRefusal r;
                bool isRefused = Current(out r);
                if (!isRefused && greyedCount == 0) return;
                IList items = Traverse.Create(controller).Field("launchPadItems").GetValue<IList>();
                if (items == null) return;
                int greyed = 0, given = 0;
                string why = isRefused ? ClickReservationGate.PurchaseReason(r) : null;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i] as Component;
                    if (item == null) continue;
                    Button button = Traverse.Create(item).Field("buttonLaunch").GetValue<Button>();
                    if (button == null) continue;
                    object mark;
                    bool wasGreyed = greyedByParsek.TryGetValue(button, out mark);
                    bool? write = LaunchReservation.SiteButtonInteractable(isRefused, wasGreyed);
                    if (write.HasValue && button.interactable != write.Value)
                    {
                        button.interactable = write.Value;
                        if (write.Value) given++; else greyed++;
                    }
                    if (isRefused && !wasGreyed) { greyedByParsek.Add(button, Marker); greyedCount++; }
                    else if (!isRefused && wasGreyed) { greyedByParsek.Remove(button); if (greyedCount > 0) greyedCount--; }
                    StockUiReasonTooltip.Sync(button, isRefused, why, controller, "LaunchSiteLaunch");
                }
                if (greyed > 0 || given > 0)
                    ParsekLog.Info(LaunchReservation.Tag, "Launch-site flyout buttons (" + (source ?? "?") + "): disabled="
                        + greyed.ToString(CultureInfo.InvariantCulture) + " givenBack="
                        + given.ToString(CultureInfo.InvariantCulture) + " of "
                        + items.Count.ToString(CultureInfo.InvariantCulture)
                        + (isRefused ? " why=\"" + why + "\"" : ""));
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(LaunchReservation.Tag, "launch-site-mark-failed",
                    "Launch-site flyout mark failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        internal static void ResetForTesting()
        {
            owner = null;
            refused = false;
            refusal = default(PurchaseRefusal);
            greyedCount = 0;
        }
    }

    /// <summary>The launch-site flyout slides open on pointer enter: its per-site Launch
    /// buttons get the current launch decision then (<see cref="LaunchSiteButtonsMark"/>).</summary>
    [HarmonyPatch]
    internal static class LaunchSiteFlyoutPointerEnterPatch
    {
        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(typeof(UILaunchsiteController), "launchSitesSelectable_onPointerEnter");
            if (method == null)
                ParsekLog.Warn(LaunchReservation.Tag,
                    "UILaunchsiteController.launchSitesSelectable_onPointerEnter not found - the flyout's per-site Launch " +
                    "buttons are greyed only by the editor poll and a craft selection; stock's CanAffordLaunchTest backstop still refuses");
            return method;
        }

        static void Postfix(UILaunchsiteController __instance)
        {
            LaunchSiteButtonsMark.Apply(__instance, "flyout-open");
        }
    }
}
