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
    /// Ensures ghost vessels have orbit renderers before buildVesselsList iterates them,
    /// and suppresses any remaining NREs as a safety net.
    ///
    /// Root cause (#195): ghost ProtoVessels are created in the SpaceTracking.Awake prefix
    /// when MapView.fetch may not yet be initialized (Unity Awake ordering is undefined).
    /// Vessel.AddOrbitRenderer() silently bails when MapView.fetch is null, leaving
    /// orbitRenderer null. buildVesselsList (decompiled line 751) unconditionally accesses
    /// vessel.orbitRenderer.onVesselIconClicked.Add(...) with NO try/catch in the for loop,
    /// so a single NRE aborts the entire method including ConstructUIList().
    ///
    /// The Prefix calls EnsureGhostOrbitRenderers() which uses Traverse to invoke the
    /// private AddOrbitRenderer() on ghosts with null orbitRenderer. By the time
    /// buildVesselsList runs (from Start or event handlers), all Awakes have completed
    /// and MapView.fetch is guaranteed available.
    ///
    /// The Finalizer remains as a safety net for unforeseen NREs from ghost ProtoVessels.
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "buildVesselsList")]
    internal static class GhostTrackingBuildVesselsListPatch
    {
        static void Prefix()
        {
            GhostMapPresence.EnsureGhostOrbitRenderers();
        }

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
    /// Creates ghost map ProtoVessels before SpaceTracking.Start loads the game state.
    /// SpaceTracking.Awake only registers events — vessel iteration happens in
    /// buildVesselsList() called from Start(). Creating ghosts early ensures they are
    /// in FlightGlobals.Vessels before st.Load() and buildVesselsList() run.
    /// Note: orbitRenderer may be null after creation here if MapView.Awake hasn't
    /// run yet — this is fixed by the buildVesselsList Prefix calling
    /// EnsureGhostOrbitRenderers().
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
    /// Prevents SpaceTracking.SetVessel from selecting ghost ProtoVessels.
    /// SetVessel enables the Fly/Delete/Recover buttons for the selected vessel.
    /// If we only block SetVessel, the buttons stay enabled from the previous
    /// selection — clicking Fly would then fly to the wrong vessel (e.g., an
    /// asteroid). We must also lock the buttons after blocking.
    /// </summary>
    [HarmonyPatch(typeof(SpaceTracking), "SetVessel")]
    internal static class GhostTrackingSetVesselPatch
    {
        static bool Prefix(SpaceTracking __instance, Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            ScreenMessages.PostScreenMessage(
                $"<b>{v.vesselName}</b> is a ghost — it shows the predicted orbit of a recorded vessel.",
                5f, ScreenMessageStyle.UPPER_CENTER);

            // Disable Fly/Delete/Recover buttons so the user can't accidentally
            // act on whatever vessel was previously selected internally.
            __instance.FlyButton.interactable = false;
            __instance.DeleteButton.interactable = false;
            __instance.RecoverButton.interactable = false;

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
