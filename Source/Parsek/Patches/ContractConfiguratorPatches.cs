using System;
using System.Collections.Generic;
using System.Reflection;
using Contracts;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine.UI;

namespace Parsek.Patches
{
    /// <summary>
    /// Contract Configurator (CC) hooks, resolved by type name so Parsek has no compile-time
    /// reference to CC. Shapes read from CC's source (mods/ContractConfigurator,
    /// docs/dev/research/stock-ui-hooks-decompile-2026-09-25.md section 11):
    /// <list type="bullet">
    /// <item><c>public static bool ContractConfigurator.ContractConfigurator.CanAccept(Contract)</c>:
    /// CC's <c>OnSelectContract</c> overwrites <c>btnAccept.interactable</c> with it AFTER
    /// stock <c>UpdateInfoPanelContract</c>, so the Accept block needs this postfix.</item>
    /// <item><c>protected void ContractConfigurator.Util.MissionControlUI.SetContractTitle(MCListItem, ContractContainer)</c>:
    /// CC's per-row title seam (the "All" tab, a selection, an accepted or newly offered row).</item>
    /// <item><c>public void MissionControlUI.OnClickAvailable(bool)</c> / <c>OnClickAll(bool)</c>:
    /// CC's rebuilds of the Available / All lists. CC rows never go through
    /// <c>MissionControl.AddItem</c>; its Active and Archive tabs stay stock.</item>
    /// </list>
    /// When CC is not loaded every CC patch class's <c>Prepare</c> answers false and
    /// Harmony skips it without an error.
    /// </summary>
    internal static class ContractConfiguratorCompat
    {
        private const string Tag = "StockUiOverlay";

        internal const string ContractConfiguratorTypeName = "ContractConfigurator.ContractConfigurator";
        internal const string MissionControlUiTypeName = "ContractConfigurator.Util.MissionControlUI";

        private static bool resolved;
        private static MethodInfo canAccept;
        private static bool absenceLogged;

        /// <summary>The CC type named <paramref name="typeName"/>, or null when CC is not loaded.</summary>
        internal static Type ResolveType(string typeName)
        {
            return AccessTools.TypeByName(typeName);
        }

        /// <summary><c>static bool CanAccept(Contract)</c> on <paramref name="ccType"/>, or null.</summary>
        internal static MethodInfo ResolveCanAccept(Type ccType)
        {
            if (ccType == null) return null;
            MethodInfo method = AccessTools.Method(ccType, "CanAccept", new[] { typeof(Contract) });
            return method != null && method.IsStatic && method.ReturnType == typeof(bool) ? method : null;
        }

        /// <summary>
        /// <c>SetContractTitle(MCListItem, &lt;container&gt;)</c> on <paramref name="uiType"/>:
        /// an instance method with two parameters, the first an <c>MCListItem</c>. Null when absent.
        /// </summary>
        internal static MethodInfo ResolveSetContractTitle(Type uiType)
        {
            if (uiType == null) return null;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(uiType))
            {
                if (m.Name != "SetContractTitle" || m.IsStatic) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 2 && p[0].ParameterType == typeof(MCListItem) && !p[1].ParameterType.IsValueType)
                    return m;
            }
            return null;
        }

        /// <summary>An instance <c>void name(bool)</c> on <paramref name="uiType"/>, or null.</summary>
        internal static MethodInfo ResolveListRebuild(Type uiType, string name)
        {
            if (uiType == null) return null;
            MethodInfo method = AccessTools.Method(uiType, name, new[] { typeof(bool) });
            return method != null && !method.IsStatic ? method : null;
        }

        /// <summary>
        /// The Prepare answer for a CC patch class: true when the CC type is loaded. Logs
        /// CC's absence once, at Verbose, because running without CC is the normal case.
        /// </summary>
        internal static bool IsTypeLoaded(string typeName, string patchName)
        {
            if (ResolveType(typeName) != null)
                return true;
            if (!absenceLogged)
            {
                absenceLogged = true;
                ParsekLog.Verbose(Tag, "Contract Configurator not loaded (" + typeName + " absent) - " + patchName
                    + " and the other CC hooks are skipped; the stock Mission Control hooks cover every row");
            }
            return false;
        }

        /// <summary>Calls CC's <c>CanAccept</c> when CC is loaded. False when it is not.</summary>
        internal static bool TryInvokeCanAccept(Contract contract, out bool result)
        {
            result = true;
            if (!resolved)
            {
                resolved = true;
                canAccept = ResolveCanAccept(ResolveType(ContractConfiguratorTypeName));
            }
            if (canAccept == null || contract == null)
                return false;
            try
            {
                result = (bool)canAccept.Invoke(null, new object[] { contract });
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "cc-canaccept-invoke",
                    "Contract Configurator CanAccept invoke failed (" + ex.GetType().Name + ": " + ex.Message + ")");
                return false;
            }
        }

        internal static void ResetForTesting()
        {
            resolved = false;
            canAccept = null;
            absenceLogged = false;
        }

        /// <summary>
        /// The CanAccept postfix decision: a true CC answer becomes false for a contract the
        /// committed future accepts. Replay bypasses. Only the accept block counts: an Active
        /// contract's Cancel block says nothing about accepting. Pure over its inputs.
        /// </summary>
        internal static bool FilterCanAccept(bool ccResult, StockUiDecoration decision, bool replaying)
        {
            if (!ccResult || replaying) return ccResult;
            return !MissionControlStockAnnotation.BlocksAcceptAndDecline(decision);
        }
    }

    /// <summary>
    /// Postfix on CC's <c>ContractConfigurator.CanAccept(Contract)</c>: false for a
    /// committed-accept contract, so CC's select handler leaves Accept greyed out.
    /// </summary>
    [HarmonyPatch]
    internal static class ContractConfiguratorCanAcceptPatch
    {
        private const string Tag = "StockUiOverlay";

        static bool Prepare()
        {
            return ContractConfiguratorCompat.IsTypeLoaded(
                ContractConfiguratorCompat.ContractConfiguratorTypeName, nameof(ContractConfiguratorCanAcceptPatch));
        }

        static MethodBase TargetMethod()
        {
            var method = ContractConfiguratorCompat.ResolveCanAccept(
                ContractConfiguratorCompat.ResolveType(ContractConfiguratorCompat.ContractConfiguratorTypeName));
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contract Configurator is loaded but ContractConfigurator.CanAccept(Contract) was not found - under CC, " +
                    "Accept may stay enabled on a committed-accept contract (Contract.Accept backstop remains). Harmony will skip this patch.");
            return method;
        }

        static void Postfix(Contract __0, ref bool __result)
        {
            try
            {
                if (!__result || __0 == null) return;
                bool replaying = GameStateRecorder.IsReplayingActions;
                StockUiDecoration d = replaying ? default(StockUiDecoration) : MissionControlStockUi.DecideNow(__0);
                bool filtered = ContractConfiguratorCompat.FilterCanAccept(__result, d, replaying);
                if (filtered == __result) return;
                __result = filtered;
                if (MissionControlStockUi.FirstLogThisOpen("cc-canaccept", d.Id))
                    ParsekLog.Info(Tag, "Contract Configurator CanAccept -> false for guid=" + d.Id
                        + " - committed future accept; Accept stays disabled why=\"" + d.Why + "\"");
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "cc-canaccept-postfix",
                    "Contract Configurator CanAccept postfix threw - CC's answer kept (" + ex.Message + ")");
            }
        }
    }

    /// <summary>
    /// Postfix on CC's per-row title seam <c>MissionControlUI.SetContractTitle</c>: the
    /// same row status the stock <c>AddItem</c> label carries.
    /// </summary>
    [HarmonyPatch]
    internal static class ContractConfiguratorContractTitlePatch
    {
        private const string Tag = "StockUiOverlay";
        private static FieldInfo displayModeAllField;
        private static bool displayModeAllResolved;

        static bool Prepare()
        {
            return ContractConfiguratorCompat.IsTypeLoaded(
                ContractConfiguratorCompat.MissionControlUiTypeName, nameof(ContractConfiguratorContractTitlePatch));
        }

        static MethodBase TargetMethod()
        {
            var method = ContractConfiguratorCompat.ResolveSetContractTitle(
                ContractConfiguratorCompat.ResolveType(ContractConfiguratorCompat.MissionControlUiTypeName));
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contract Configurator is loaded but MissionControlUI.SetContractTitle(MCListItem, ContractContainer) was not found - " +
                    "CC rows get their label only on a list rebuild. Harmony will skip this patch.");
            return method;
        }

        static void Postfix(object __instance, MCListItem __0, object __1)
        {
            try
            {
                if (!MissionControlStockUi.RelabelRow(__0, __1, "cc-SetContractTitle"))
                    return;
                FitCcAllTabRowHeight(__instance, __0);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "cc-title-postfix",
                    "Contract Configurator SetContractTitle postfix threw - row keeps CC's title (" + ex.Message + ")");
            }
        }

        // CC's "All" tab sizes a row to one or two lines from its title BEFORE this postfix
        // appends the status; redo that fit (CC's own rule: two lines when the preferred
        // height exceeds 17, height 38 instead of 25) so the longer label is not clipped.
        private static void FitCcAllTabRowHeight(object ccUi, MCListItem row)
        {
            if (ccUi == null || row == null || row.title == null) return;
            if (!displayModeAllResolved)
            {
                displayModeAllResolved = true;
                displayModeAllField = AccessTools.Field(ccUi.GetType(), "displayModeAll");
            }
            if (displayModeAllField == null || !(displayModeAllField.GetValue(ccUi) is bool all) || !all)
                return;
            LayoutElement layout = row.GetComponent<LayoutElement>();
            if (layout == null) return;
            float width = row.title.rectTransform.rect.width;
            if (width <= 1f) return;
            float height = row.title.GetPreferredValues(row.title.text, width, float.MaxValue).y;
            if (height > 17f && layout.preferredHeight < 38f)
                layout.preferredHeight = 38f;
        }
    }

    /// <summary>
    /// Postfix on CC's list rebuilds <c>MissionControlUI.OnClickAvailable(bool)</c> and
    /// <c>OnClickAll(bool)</c>: labels every contract row CC just built and logs one
    /// decoration pass (CC's Available rows are built inline, not through a title seam).
    /// </summary>
    [HarmonyPatch]
    internal static class ContractConfiguratorListRebuildPatch
    {
        private const string Tag = "StockUiOverlay";

        static bool Prepare()
        {
            return ContractConfiguratorCompat.IsTypeLoaded(
                ContractConfiguratorCompat.MissionControlUiTypeName, nameof(ContractConfiguratorListRebuildPatch));
        }

        static IEnumerable<MethodBase> TargetMethods()
        {
            Type ui = ContractConfiguratorCompat.ResolveType(ContractConfiguratorCompat.MissionControlUiTypeName);
            foreach (string name in new[] { "OnClickAvailable", "OnClickAll" })
            {
                MethodInfo method = ContractConfiguratorCompat.ResolveListRebuild(ui, name);
                if (method == null)
                {
                    ParsekLog.Warn(Tag, "Contract Configurator is loaded but MissionControlUI." + name
                        + "(bool) was not found - CC rows built there get their label only when selected.");
                    continue;
                }
                yield return method;
            }
        }

        static void Postfix(bool __0, MethodBase __originalMethod)
        {
            try
            {
                if (!__0) return;
                MissionControl mc = MissionControl.Instance;
                if (mc == null) return;
                MissionControlStockUi.RelabelRows(mc, "cc-" + (__originalMethod != null ? __originalMethod.Name : "rebuild"));
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "cc-rebuild-postfix",
                    "Contract Configurator list rebuild postfix threw - rows keep CC's titles (" + ex.Message + ")");
            }
        }
    }
}
