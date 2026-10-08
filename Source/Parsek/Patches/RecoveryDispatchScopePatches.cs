using System;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// Opens a <see cref="RecoveryDispatchScope"/> around each stock method that fires
    /// <c>GameEvents.onVesselRecovered</c> for one vessel, so a progress node the recovery
    /// completes is owned by the recovered vessel's recording
    /// (QUICKLOAD-UNTAGGED-RECOVERY-MILESTONE-SURVIVES-F9). The fire sites, decompiled from
    /// KSP 1.12.5:
    /// <list type="bullet">
    /// <item><c>VesselRetrieval.recoverVessel</c>: the in-flight Recover button, run at the
    /// Space Center a few frames after the scene switch.</item>
    /// <item><c>SpaceTracking.OnRecoverConfirm</c>: the Tracking Station Recover.</item>
    /// <item><c>ShipConstruction.RecoverVesselFromFlight(ProtoVessel, FlightState, bool)</c>:
    /// the Space Center vessel marker, the editor and launch-site clears, FlightDriver (the
    /// two-argument overload calls this one).</item>
    /// <item><c>ProtoVessel.Clean</c>: stock's quick recovery of a cleaned-up vessel on the
    /// home body.</item>
    /// </list>
    /// Not covered: the per-vessel loop inside <c>ShipConstruction.CheckLaunchSiteClear</c>
    /// (a pad cleared for a new launch); a node completed there stays untagged, as before.
    /// Each prefix carries whether it opened a scope to its finalizer, which closes exactly
    /// that scope whether the original returned or threw.
    /// </summary>
    [HarmonyPatch(typeof(VesselRetrieval), "recoverVessel")]
    internal static class RecoveryScopeVesselRetrievalPatch
    {
        internal const string Route = "in-flight-recover";

        static void Prefix(Vessel v, out bool __state)
        {
            __state = RecoveryDispatchScopePatchSupport.TryOpen(v != null ? v.protoVessel : null, Route);
        }

        static void Finalizer(bool __state)
        {
            RecoveryDispatchScopePatchSupport.CloseIfOpened(__state, Route);
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "OnRecoverConfirm")]
    internal static class RecoveryScopeTrackingStationPatch
    {
        internal const string Route = "tracking-station-recover";

        static void Prefix(SpaceTracking __instance, out bool __state)
        {
            __state = false;
            Vessel selected = null;
            try
            {
                selected = Traverse.Create(__instance).Field("selectedVessel").GetValue<Vessel>();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecoveryScope",
                    "Tracking Station recovery scope: selectedVessel read failed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }

            __state = RecoveryDispatchScopePatchSupport.TryOpen(
                selected != null ? selected.protoVessel : null, Route);
        }

        static void Finalizer(bool __state)
        {
            RecoveryDispatchScopePatchSupport.CloseIfOpened(__state, Route);
        }
    }

    [HarmonyPatch(typeof(ShipConstruction), nameof(ShipConstruction.RecoverVesselFromFlight),
        new[] { typeof(ProtoVessel), typeof(FlightState), typeof(bool) })]
    internal static class RecoveryScopeRecoverVesselFromFlightPatch
    {
        internal const string Route = "recover-vessel-from-flight";

        static void Prefix(ProtoVessel vessel, out bool __state)
        {
            __state = RecoveryDispatchScopePatchSupport.TryOpen(vessel, Route);
        }

        static void Finalizer(bool __state)
        {
            RecoveryDispatchScopePatchSupport.CloseIfOpened(__state, Route);
        }
    }

    [HarmonyPatch(typeof(ProtoVessel), nameof(ProtoVessel.Clean))]
    internal static class RecoveryScopeProtoVesselCleanPatch
    {
        internal const string Route = "proto-vessel-clean";

        static void Prefix(ProtoVessel __instance, out bool __state)
        {
            __state = RecoveryDispatchScopePatchSupport.TryOpen(__instance, Route);
        }

        static void Finalizer(bool __state)
        {
            RecoveryDispatchScopePatchSupport.CloseIfOpened(__state, Route);
        }
    }

    internal static class RecoveryDispatchScopePatchSupport
    {
        /// <summary>Never blocks a recovery: any failure opens no scope (the event stays untagged).</summary>
        internal static bool TryOpen(ProtoVessel pv, string route)
        {
            try
            {
                return RecoveryDispatchScope.TryOpen(pv, route);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecoveryScope",
                    "Recovery scope open failed, the recovery proceeds unscoped: route=" + route + " " +
                    ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        internal static void CloseIfOpened(bool opened, string route)
        {
            if (opened)
                RecoveryDispatchScope.Close(route);
        }
    }
}
