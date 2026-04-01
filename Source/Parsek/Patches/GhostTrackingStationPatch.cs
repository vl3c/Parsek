using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents tracking station actions (Fly, Delete, Recover) on ghost map ProtoVessels.
    /// Ghost vessels are transient map-presence objects — they cannot be flown, deleted, or recovered.
    /// Shows a screen message explaining the ghost status instead.
    ///
    /// IMPORTANT: Delete and Recover patches must call OnDialogDismiss on the SpaceTracking
    /// instance to release the input lock set by the confirmation dialog. Returning false
    /// skips the original method which contains the unlock call.
    /// </summary>

    /// <summary>
    /// Creates ghost map ProtoVessels BEFORE SpaceTracking builds its widget list.
    /// SpaceTracking.Awake iterates FlightGlobals.Vessels to create sidebar widgets
    /// AND map node click callbacks. Ghost vessels created after this point appear
    /// in the sidebar (via onVesselCreate) but their map icons are not clickable.
    /// Running in a prefix ensures ghost vessels are in FlightGlobals.Vessels when
    /// SpaceTracking processes the list, giving them full click interactivity.
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "Awake")]
    internal static class GhostTrackingStationInitPatch
    {
        static void Prefix()
        {
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            if (created > 0)
                ParsekLog.Info("GhostMap",
                    $"Pre-created {created} ghost vessel(s) before SpaceTracking.Awake — " +
                    "ensures map icon click handlers are registered");
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "FlyVessel")]
    internal static class GhostTrackingFlyPatch
    {
        static bool Prefix(Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            ScreenMessages.PostScreenMessage(
                $"<b>{v.vesselName}</b> is a ghost vessel — it will materialize when its timeline reaches the spawn point.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked FlyVessel for ghost '{v.vesselName}' pid={v.persistentId}");
            return false;
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "OnVesselDeleteConfirm")]
    internal static class GhostTrackingDeletePatch
    {
        static bool Prefix(SpaceTracking __instance)
        {
            Vessel selected = Traverse.Create(__instance).Field("selectedVessel").GetValue<Vessel>();
            if (selected == null || !GhostMapPresence.IsGhostMapVessel(selected.persistentId))
                return true;

            ScreenMessages.PostScreenMessage(
                $"<b>{selected.vesselName}</b> is a ghost vessel and cannot be deleted. " +
                "It will be removed automatically when its chain resolves.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked Delete for ghost '{selected.vesselName}' pid={selected.persistentId}");

            // Release the input lock that the confirmation dialog set.
            // The original OnVesselDeleteConfirm calls OnDialogDismiss which unlocks UI.
            // Since we skip the original, we must dismiss ourselves.
            Traverse.Create(__instance).Method("OnDialogDismiss").GetValue();
            return false;
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "OnRecoverConfirm")]
    internal static class GhostTrackingRecoverPatch
    {
        static bool Prefix(SpaceTracking __instance)
        {
            Vessel selected = Traverse.Create(__instance).Field("selectedVessel").GetValue<Vessel>();
            if (selected == null || !GhostMapPresence.IsGhostMapVessel(selected.persistentId))
                return true;

            ScreenMessages.PostScreenMessage(
                $"<b>{selected.vesselName}</b> is a ghost vessel and cannot be recovered. " +
                "It will be removed automatically when its chain resolves.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked Recover for ghost '{selected.vesselName}' pid={selected.persistentId}");

            // Release input lock (same reason as Delete patch above)
            Traverse.Create(__instance).Method("OnDialogDismiss").GetValue();
            return false;
        }
    }
}
