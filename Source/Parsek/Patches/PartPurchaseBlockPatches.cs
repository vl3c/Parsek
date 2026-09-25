using System;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// P1 block at the control: after stock's <c>PartListTooltip.Setup(AvailablePart, ...)</c>
    /// (the editor part list and the R&amp;D part list both build their tooltip through it),
    /// disable the purchase buttons of a part the committed timeline buys later.
    /// </summary>
    [HarmonyPatch]
    internal static class PartListTooltipSetupPartPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "PartListTooltip.Setup(AvailablePart, Callback, RenderTexture) not found - the part purchase " +
                    "button will not be disabled (PartListTooltipController.onPurchase still refuses the click)");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(PartListTooltip), nameof(PartListTooltip.Setup),
                new[] { typeof(AvailablePart), typeof(Callback<PartListTooltip>), typeof(RenderTexture) });
        }

        static void Postfix(PartListTooltip __instance, AvailablePart availablePart)
        {
            try
            {
                StockUiPartPurchase.ApplyTooltipButtons(__instance, availablePart);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "part-tooltip-block-failed",
                    "Part tooltip purchase block failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// The upgrade overload of <c>PartListTooltip.Setup</c> sells a part UPGRADE, not an
    /// entry purchase, so nothing is blocked there; the postfix only gives back purchase
    /// buttons a reused tooltip may still carry disabled from a blocked part.
    /// </summary>
    [HarmonyPatch]
    internal static class PartListTooltipSetupUpgradePatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "PartListTooltip.Setup(AvailablePart, Upgrade, Callback, RenderTexture) not found - an upgrade " +
                    "tooltip reusing a blocked part's tooltip may keep a disabled purchase button");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(PartListTooltip), nameof(PartListTooltip.Setup),
                new[]
                {
                    typeof(AvailablePart), typeof(PartUpgradeHandler.Upgrade),
                    typeof(Callback<PartListTooltip>), typeof(RenderTexture)
                });
        }

        static void Postfix(PartListTooltip __instance)
        {
            try
            {
                StockUiPartPurchase.RestoreTooltipButtons(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "part-tooltip-restore-failed",
                    "Part upgrade tooltip button restore failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// P1 why: the private <c>PartListTooltipController.CreateTooltip</c> calls
    /// <c>Setup</c> and then rewrites the stock <c>textGreyoutMessage</c> label (enabled
    /// and empty for a normal icon, stock's greyout message for a greyed one), so the
    /// reason is written after it.
    /// </summary>
    [HarmonyPatch]
    internal static class PartListTooltipReasonPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "PartListTooltipController.CreateTooltip(PartListTooltip, EditorPartIcon) not found - the part " +
                    "purchase explanation will not show in the tooltip (the refusal dialog still explains)");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(PartListTooltipController), "CreateTooltip",
                new[] { typeof(PartListTooltip), typeof(EditorPartIcon) });
        }

        static void Postfix(PartListTooltip tooltip, EditorPartIcon partIcon)
        {
            try
            {
                StockUiPartPurchase.ApplyTooltipReason(tooltip, partIcon);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "part-tooltip-reason-failed",
                    "Part tooltip purchase explanation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// P1 backstop for the tooltip purchase in BOTH scenes: stock's
    /// <c>PartListTooltipController.onPurchase</c> is the one entry to the private
    /// <c>onPurchaseProceed</c>, whose EDITOR branch writes <c>partsPurchased</c> itself and
    /// never reaches <c>RDTech.PurchasePart</c>. Refused before stock destroys the tooltip
    /// or runs its R&amp;D facility check. Never the <c>OnPartPurchased</c> event: stock's
    /// <c>Funding</c> deducts the entry cost on it, which is too late.
    /// </summary>
    [HarmonyPatch]
    internal static class PartListTooltipPurchasePatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "PartListTooltipController.onPurchase(PartListTooltip) not found - an editor part purchase " +
                    "the committed timeline makes later will not be refused");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(PartListTooltipController), "onPurchase",
                new[] { typeof(PartListTooltip) });
        }

        static bool Prefix(AvailablePart ___partInfo)
        {
            return !StockUiPartPurchase.TryBlockPurchase(___partInfo, "tooltip purchase", showDialog: true);
        }
    }

    /// <summary>
    /// P1 backstop for the R&amp;D side panel's "purchase all parts" (the researched-node
    /// state of <c>RDController.actionButton</c>): stock loops over the node's parts and
    /// buys each through <c>RDTech.PurchasePart</c>. The blocked ones are skipped there
    /// and named in one dialog; the rest are bought (see
    /// <see cref="StockUiPartPurchase.BeginPurchaseAll"/>).
    /// </summary>
    [HarmonyPatch]
    internal static class RnDPurchaseAllPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "RDController.ActionButtonClick(string) not found - R&D purchase-all will skip blocked parts " +
                    "one dialog per part (RDTech.PurchasePart still refuses each)");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(RDController), "ActionButtonClick", new[] { typeof(string) });
        }

        static void Prefix(RDController __instance, string state, out bool __state)
        {
            __state = state == "purchase";
            if (!__state) return;
            try
            {
                StockUiPartPurchase.BeginPurchaseAll(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-purchase-all-failed",
                    "R&D purchase-all pre-check failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) StockUiPartPurchase.ExitPurchaseAll();
            return __exception;
        }
    }

    /// <summary>
    /// P1 backstop on the R&amp;D purchase primitive <c>RDTech.PurchasePart</c> (R&amp;D
    /// tooltip purchase and purchase-all both end here, as would a mod calling it): a
    /// blocked part is not bought. Inside purchase-all the one dialog was already shown.
    /// </summary>
    [HarmonyPatch]
    internal static class RDTechPurchasePartPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "RDTech.PurchasePart(AvailablePart) not found - R&D purchase-all may buy a part the committed " +
                    "timeline buys later");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(RDTech), nameof(RDTech.PurchasePart), new[] { typeof(AvailablePart) });
        }

        static bool Prefix(AvailablePart ap)
        {
            bool inBatch = StockUiPartPurchase.InPurchaseAll;
            return !StockUiPartPurchase.TryBlockPurchase(ap,
                inBatch ? "R&D purchase all" : "RDTech.PurchasePart", showDialog: !inBatch);
        }
    }
}
