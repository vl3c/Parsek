using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Captures a stock player rename (design-vessel-and-mission-names.md 7.1): the stock rename
    /// dialog's accept callback. Decompiled (KSP 1.12.5 Assembly-CSharp.dll):
    /// <code>
    /// private void onVesselRenameAccept(string newVesselName, VesselType newVesselType) {
    ///   if (!IsValidVesselName(newVesselName)) return;
    ///   string from = vesselName;
    ///   vesselName = newVesselName; vesselType = newVesselType;
    ///   if (vesselNamedBy != null) { vesselNamedBy.vesselNaming.vesselName = newVesselName; ... }
    ///   GameEvents.onVesselRename.Fire(new HostedFromToAction&lt;Vessel, string&gt;(this, from, vesselName));
    ///   onVesselRenameDismiss();
    /// }
    /// </code>
    /// Reached from the PAW "Rename Vessel" (ModuleCommand, KerbalEVA) and the map / Tracking
    /// Station vessel info header. Stock fires <c>onVesselRename</c> INSIDE the body (and also
    /// from part-naming automation and as a from == to UI ping), so Parsek never listens to that
    /// event: the Prefix keeps the name the vessel had and the Postfix compares it with the name
    /// after the body ran. A type-only change records nothing.
    /// </summary>
    [HarmonyPatch]
    internal static class VesselRenameAcceptCapturePatch
    {
        private static bool missLogged;

        internal static MethodBase ResolveTargetMethodForTesting()
            => AccessTools.Method(typeof(Vessel), "onVesselRenameAccept", new[] { typeof(string), typeof(VesselType) });

        static bool Prepare()
        {
            if (ResolveTargetMethodForTesting() != null) return true;
            if (!missLogged)
            {
                missLogged = true;
                ParsekLog.Warn("VesselNames",
                    "Vessel.onVesselRenameAccept(string, VesselType) not found - stock rename dialog renames will not be captured; Parsek renames and the load re-apply still work");
            }
            return false;
        }

        static MethodBase TargetMethod() => ResolveTargetMethodForTesting();

        static void Prefix(Vessel __instance, out string __state)
        {
            __state = __instance != null ? __instance.vesselName : null;
        }

        static void Postfix(Vessel __instance, string __state)
        {
            if (ParsekGameModeGate.CheckInert("VesselRenameAcceptCapturePatch.Postfix")) return;
            VesselNameLiveSync.OnStockRenameAccepted(__instance, __state, VesselNameSource.StockDialog);
        }
    }

    /// <summary>
    /// Captures a stock part naming accept that renames the vessel (design 7.1). Decompiled
    /// (KSP 1.12.5):
    /// <code>
    /// private void onVesselNamingAccept(string newVesselName, VesselType newVesselType, int newPriority) {
    ///   if (!Vessel.IsValidVesselName(newVesselName)) return;
    ///   if (vesselNaming == null) vesselNaming = new VesselNaming();
    ///   vesselNaming.vesselName = newVesselName; ...type...; vesselNaming.namingPriority = newPriority;
    ///   RefreshVesselNamingPAWDisplay();
    ///   GameEvents.onPartVesselNamingChanged.Fire(this);   // Vessel.UpdateVesselNaming, synchronous
    ///   onVesselNamingDismiss();
    /// }
    /// </code>
    /// The vessel is renamed (synchronously, inside the body) only when this part became the
    /// priority naming part; a naming that only pre-sets a future split name leaves the vessel
    /// name alone and records nothing (the child captures it when it separates). No vessel in
    /// the editor.
    /// </summary>
    [HarmonyPatch]
    internal static class PartVesselNamingAcceptCapturePatch
    {
        private static bool missLogged;

        internal static MethodBase ResolveTargetMethodForTesting()
            => AccessTools.Method(typeof(Part), "onVesselNamingAccept",
                new[] { typeof(string), typeof(VesselType), typeof(int) });

        static bool Prepare()
        {
            if (ResolveTargetMethodForTesting() != null) return true;
            if (!missLogged)
            {
                missLogged = true;
                ParsekLog.Warn("VesselNames",
                    "Part.onVesselNamingAccept(string, VesselType, int) not found - stock part namings will not be captured; Parsek renames and the load re-apply still work");
            }
            return false;
        }

        static MethodBase TargetMethod() => ResolveTargetMethodForTesting();

        static void Prefix(Part __instance, out string __state)
        {
            __state = __instance != null && __instance.vessel != null ? __instance.vessel.vesselName : null;
        }

        static void Postfix(Part __instance, string __state)
        {
            if (ParsekGameModeGate.CheckInert("PartVesselNamingAcceptCapturePatch.Postfix")) return;
            Vessel v = __instance != null ? __instance.vessel : null;
            if (v == null) return;
            VesselNameLiveSync.OnStockRenameAccepted(v, __state, VesselNameSource.StockPartNaming);
        }
    }
}
