using System;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// R&amp;D mark: after stock's <c>RDNode.UpdateGraphics</c> (which runs
    /// <c>SetButtonState</c>, resetting the icon colour except on a FADED node), tint a
    /// node a committed future researches and clear a stale tint.
    /// </summary>
    [HarmonyPatch]
    internal static class RnDNodeMarkPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "RDNode.UpdateGraphics() not found - R&D committed-future node tint will not apply");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(RDNode), nameof(RDNode.UpdateGraphics), Type.EmptyTypes);
        }

        static void Postfix(RDNode __instance)
        {
            try
            {
                StockUiRnDDecoration.ApplyNodeMark(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-mark-failed",
                    "R&D node tint failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// R&amp;D why: append the explanation to the stock node tooltip caption. The private
    /// <c>RDNode.GetTooltipCaption()</c> has one consumer, <c>UpdateGraphics</c>, which
    /// writes it into the node's stock <c>TooltipController_TitleAndText</c>.
    /// </summary>
    [HarmonyPatch]
    internal static class RnDNodeTooltipPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "RDNode.GetTooltipCaption() not found - R&D node tooltip explanation will not apply");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(RDNode), "GetTooltipCaption", Type.EmptyTypes);
        }

        static void Postfix(RDNode __instance, ref string __result)
        {
            try
            {
                __result = StockUiRnDDecoration.AppendNodeTooltip(__instance, __result);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-tooltip-failed",
                    "R&D node tooltip explanation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// R&amp;D block at the control: stock <c>RDController.UpdatePanel</c> re-enables the
    /// Research button whenever a node is shown, so the block is re-applied after it.
    /// <c>TechResearchPatch</c> / <c>TechResearchSpendPatch</c> stay the click backstops.
    /// </summary>
    [HarmonyPatch]
    internal static class RnDPanelBlockPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "RDController.UpdatePanel() not found - the R&D Research button will not be disabled " +
                    "(TechResearchSpendPatch still refuses the click)");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(RDController), nameof(RDController.UpdatePanel), Type.EmptyTypes);
        }

        static void Postfix(RDController __instance)
        {
            bool disabledByParsek = false;
            try
            {
                disabledByParsek |= StockUiRnDDecoration.ApplyPanelBlock(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-panel-block-failed",
                    "R&D Research button block failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            try
            {
                // On a researched node the same button is "purchase all parts" (P1).
                disabledByParsek |= StockUiPartPurchase.ApplyPurchaseAllBlock(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-purchase-all-block-failed",
                    "R&D purchase-all button block failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            try
            {
                // Every UpdatePanel re-derives the greyed look from this node's block, so a
                // node shown after a blocked one gets its stock look back.
                StockUiRnDDecoration.SyncActionButtonGreyed(__instance, disabledByParsek);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-greyed-sync-failed",
                    "R&D action button greyed state failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// R&amp;D why beside the disabled button: stock <c>RDController.ShowNodePanel</c>
    /// rewrites the side panel's description on every show; the explanation is appended
    /// after it.
    /// </summary>
    [HarmonyPatch]
    internal static class RnDPanelDescriptionPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "RDController.ShowNodePanel(RDNode) not found - the R&D side panel explanation will not apply");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(RDController), nameof(RDController.ShowNodePanel), new[] { typeof(RDNode) });
        }

        static void Postfix(RDController __instance, RDNode node)
        {
            try
            {
                StockUiRnDDecoration.AppendPanelDescription(__instance, node);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-panel-description-failed",
                    "R&D side panel explanation failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// The per-refresh pass log: <c>RDTechTree.RefreshUI</c> runs every node's
    /// <c>UpdateGraphics</c> and the panel, so one <c>decorate screen=RnD ...</c> line per
    /// refresh replaces a per-node line.
    /// </summary>
    [HarmonyPatch]
    internal static class RnDRefreshPassLogPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StockUiOverlay",
                    "RDTechTree.RefreshUI() not found - the per-refresh R&D decoration log will not be written");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(RDTechTree), nameof(RDTechTree.RefreshUI), Type.EmptyTypes);
        }

        static void Postfix(RDTechTree __instance)
        {
            try
            {
                StockUiRnDDecoration.LogPass(
                    __instance != null && __instance.controller != null ? __instance.controller : RDController.Instance,
                    "tree refresh");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StockUiOverlay", "rnd-pass-log-failed",
                    "R&D decoration pass log failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}
