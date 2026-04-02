using System;
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
    /// Suppresses NullReferenceException in SpaceTracking.buildVesselsList caused by
    /// ghost ProtoVessels. Ghost ProtoVessels can trigger NREs in KSP's internal vessel
    /// list rebuilding when asteroids are spawned/destroyed, because buildVesselsList
    /// assumes certain vessel state fields are always populated. The Finalizer returns
    /// null for the exception, which tells Harmony to swallow it completely (the original
    /// method's caller never sees it). Unlike the previous approach of hiding ghosts from
    /// FlightGlobals, this allows the ghost to remain in the vessel list so its sidebar
    /// widget and map node are created. The NRE typically occurs on a single ghost vessel
    /// iteration but the method continues processing remaining vessels (KSP uses try/catch
    /// internally for individual widget creation).
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "buildVesselsList")]
    internal static class GhostTrackingBuildVesselsListPatch
    {
        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                ParsekLog.VerboseRateLimited("GhostMap", "buildVesselsListNRE",
                    $"Suppressed SpaceTracking.buildVesselsList exception: {__exception.GetType().Name}");
            }
            return null; // swallow — return null tells Harmony to suppress the exception
        }
    }

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
        static bool Prefix(SpaceTracking __instance, Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            ScreenMessages.PostScreenMessage(
                $"<b>{v.vesselName}</b> is a ghost vessel — it will materialize when its timeline reaches the spawn point.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked FlyVessel for ghost '{v.vesselName}' pid={v.persistentId}");

            // Release the input lock that selecting the vessel may have set.
            // The original FlyVessel transitions to flight scene (clearing locks implicitly).
            // Since we skip the original, we must dismiss ourselves to avoid trapping
            // the user in tracking station with a stale input lock.
            Traverse.Create(__instance).Method("OnDialogDismiss").GetValue();
            return false;
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "OnVesselDeleteConfirm")]
    internal static class GhostTrackingDeletePatch
    {
        static bool Prefix(SpaceTracking __instance)
        {
            Vessel selected = Traverse.Create(__instance).Field("selectedVessel").GetValue<Vessel>();
            if (selected == null)
            {
                // Null here means either no vessel selected (normal) or the private field was
                // renamed in a KSP update (Traverse failed silently). Log a warning so we notice.
                ParsekLog.Warn("GhostMap", "GhostTrackingDeletePatch: selectedVessel is null — Traverse may have failed");
                return true;
            }
            if (!GhostMapPresence.IsGhostMapVessel(selected.persistentId))
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

    /// <summary>
    /// Prevents SpaceTracking.SetVessel from crashing on ghost ProtoVessels.
    /// Ghost vessels lack certain state fields that SetVessel assumes exist,
    /// causing NullReferenceException when clicking a ghost in the tracking station.
    /// Shows a screen message instead.
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "SetVessel")]
    internal static class GhostTrackingSetVesselPatch
    {
        static bool Prefix(Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            ScreenMessages.PostScreenMessage(
                $"<b>{v.vesselName}</b> is a ghost — it shows the predicted orbit of a recorded vessel.",
                5f, ScreenMessageStyle.UPPER_CENTER);
            ParsekLog.Info("GhostMap",
                $"Blocked SetVessel for ghost '{v.vesselName}' pid={v.persistentId} in Tracking Station");
            return false;
        }
    }

    [HarmonyPatch(typeof(SpaceTracking), "OnRecoverConfirm")]
    internal static class GhostTrackingRecoverPatch
    {
        static bool Prefix(SpaceTracking __instance)
        {
            Vessel selected = Traverse.Create(__instance).Field("selectedVessel").GetValue<Vessel>();
            if (selected == null)
            {
                ParsekLog.Warn("GhostMap", "GhostTrackingRecoverPatch: selectedVessel is null — Traverse may have failed");
                return true;
            }
            if (!GhostMapPresence.IsGhostMapVessel(selected.persistentId))
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
