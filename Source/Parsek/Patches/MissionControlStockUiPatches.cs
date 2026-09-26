using System;
using System.Reflection;
using Contracts;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    // The Mission Control stock-control annotations (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md
    // section 5, stock hooks verified in docs/dev/research/stock-ui-hooks-decompile-2026-09-25.md section 5).
    // Every body is wrapped so a Parsek failure never breaks the stock screen: the
    // stock method always runs and a failure logs one rate-limited Warn.

    /// <summary>
    /// Brackets stock <c>MissionControl.RebuildContractList()</c> (every tab switch,
    /// filter change, accept / decline / cancel and list change) so the rows AddItem
    /// builds are decided over one index and logged as one decoration pass.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionControlRebuildPassPatch
    {
        private const string Tag = "StockUiOverlay";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "MissionControl.RebuildContractList() not found - Mission Control decoration passes will not be logged. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(MissionControl), "RebuildContractList", Type.EmptyTypes);
        }

        static void Prefix(MissionControl __instance)
        {
            if (ParsekGameModeGate.CheckInert("MissionControlRebuildPassPatch.Prefix")) return; // S9 game-mode gate
            try
            {
                if (__instance != null)
                    MissionControlStockUi.BeginRebuildPass(__instance.displayMode);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "mc-rebuild-prefix",
                    "MissionControl.RebuildContractList prefix threw - pass not opened (" + ex.Message + ")");
            }
        }

        static void Postfix()
        {
            if (ParsekGameModeGate.CheckInert("MissionControlRebuildPassPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                MissionControlStockUi.EndRebuildPass();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "mc-rebuild-postfix",
                    "MissionControl.RebuildContractList postfix threw - pass not logged (" + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Prefix on stock <c>MissionControl.AddItem(Contract, bool, string label)</c>: for a
    /// contract the committed future accepts, supplies the row label (drawn by
    /// <c>MCListItem.Setup</c>) as the full stock title plus a short Parsek status.
    /// Every rebuild re-runs it, so the label survives tab switches.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionControlAddItemLabelPatch
    {
        private const string Tag = "StockUiOverlay";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "MissionControl.AddItem(Contract, bool, string) not found - Mission Control rows will carry no committed-accept label. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(MissionControl), "AddItem",
                new[] { typeof(Contract), typeof(bool), typeof(string) });
        }

        static void Prefix(Contract contract, ref string label)
        {
            if (ParsekGameModeGate.CheckInert("MissionControlAddItemLabelPatch.Prefix")) return; // S9 game-mode gate
            try
            {
                label = MissionControlStockUi.LabelForAddItem(contract, label);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "mc-additem-prefix",
                    "MissionControl.AddItem prefix threw - row keeps the stock label (" + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Postfix on stock <c>MissionControl.UpdateInfoPanelContract(Contract)</c>, the only
    /// place stock sets <c>btnDecline.interactable</c> (many stock contract types override
    /// <c>CanBeDeclined</c> without calling base, so a base-method patch misses them) and
    /// the call Contract Configurator's select handler makes too: appends the
    /// explanation to <c>contractText</c> and greys out Accept and Decline.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionControlInfoPanelPatch
    {
        private const string Tag = "StockUiOverlay";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "MissionControl.UpdateInfoPanelContract(Contract) not found - the contract detail panel will not explain or disable committed accepts " +
                    "(Contract.Accept / Contract.Decline backstops remain). Harmony will skip this patch.");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(MissionControl), "UpdateInfoPanelContract", new[] { typeof(Contract) });
        }

        static void Postfix(MissionControl __instance, Contract contract)
        {
            if (ParsekGameModeGate.CheckInert("MissionControlInfoPanelPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                MissionControlStockUi.ApplyDetailPanel(__instance, contract, "UpdateInfoPanelContract");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "mc-infopanel-postfix",
                    "MissionControl.UpdateInfoPanelContract postfix threw - panel left as stock drew it (" + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Postfix on stock private <c>MissionControl.RefreshUIControls()</c>: it sets
    /// <c>btnAccept.interactable</c> from the slot count for every contract after each
    /// list change and click, so the per-contract block is re-asserted for the selection.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionControlRefreshUIControlsPatch
    {
        private const string Tag = "StockUiOverlay";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "MissionControl.RefreshUIControls() not found - a list change may re-enable Accept on a committed-accept contract " +
                    "until it is re-selected (Contract.Accept backstop remains). Harmony will skip this patch.");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(MissionControl), "RefreshUIControls", Type.EmptyTypes);
        }

        static void Postfix(MissionControl __instance)
        {
            if (ParsekGameModeGate.CheckInert("MissionControlRefreshUIControlsPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                MissionControlStockUi.ReapplyButtonsForSelection(__instance, "RefreshUIControls");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "mc-refreshui-postfix",
                    "MissionControl.RefreshUIControls postfix threw - button state left as stock set it (" + ex.Message + ")");
            }
        }
    }
}
