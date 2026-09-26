using System;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// KSC facility menu block at the control: stock's protected
    /// <c>KSCFacilityContextMenu.OnFacilityValuesModified</c> fills the buttons (from
    /// <c>CreateWindowContent</c> on open, and again on every structure collapse / repair
    /// event) and sets Upgrade interactable unless the facility is at its top level. The
    /// postfix disables Upgrade, with the explanation in a stock tooltip, for a facility the
    /// committed timeline upgrades later. <c>onFacilityContextMenuSpawn</c> fires before the
    /// buttons are filled, so it is not a decoration point. <see cref="FacilityUpgradeSpendPatch"/>
    /// and <see cref="FacilityUpgradePatch"/> stay the click backstops.
    /// </summary>
    [HarmonyPatch]
    internal static class FacilityMenuUpgradeBlockPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "KSCFacilityContextMenu.OnFacilityValuesModified() not found - the facility menu Upgrade button " +
                    "will not be disabled (FacilityUpgradeSpendPatch still refuses the click)");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(KSCFacilityContextMenu), "OnFacilityValuesModified", Type.EmptyTypes);
        }

        static void Postfix(KSCFacilityContextMenu __instance)
        {
            if (ParsekGameModeGate.CheckInert("FacilityMenuUpgradeBlockPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                StockUiFacilityDecoration.Apply(__instance, "values modified");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "facility-menu-block-failed",
                    "Facility menu Upgrade block failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}
