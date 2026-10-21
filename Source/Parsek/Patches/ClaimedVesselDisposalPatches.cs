using System;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine.UI;

namespace Parsek.Patches
{
    /// <summary>
    /// GC-2 hooks of <see cref="ClaimedVesselDisposalGate"/> outside flight.
    ///
    /// <para>Tracking Station: decompiled <c>SpaceTracking.SetVessel(Vessel, bool)</c> and
    /// <c>unlockUI()</c> (KSP 1.12.5) are where stock unlocks the Recover / Delete buttons, so
    /// both postfixes re-grey them for a held vessel; the click-blocks are the
    /// <c>OnRecoverConfirm</c> / <c>OnVesselDeleteConfirm</c> prefixes in
    /// <see cref="GhostTrackingRecoverPatch"/> / <see cref="GhostTrackingDeletePatch"/>, which call
    /// <see cref="RefuseTrackingStationDisposal"/>.</para>
    ///
    /// <para>Space Center: decompiled <c>KSCVesselMarkers.SpawnVesselMarkers</c> builds one
    /// <c>KSCVesselMarker.Create(Vessel, Callback)</c> for every landed or splashed vessel on the
    /// home body (not only those at the KSC), whose Recover button ends in the private
    /// <c>KSCVesselMarkers.RecoverVessel(Vessel)</c>
    /// (<c>ShipConstruction.RecoverVesselFromFlight</c>). The marker greys its own Recover button
    /// with a tooltip reason in a mission that forbids recovery; the postfix does the same for a
    /// held vessel, and the prefix refuses the recovery.</para>
    /// </summary>
    internal static class ClaimedVesselDisposalPatches
    {
        private const string Surface = "Claimed vessel";

        /// <summary>
        /// Greys the Tracking Station's Recover / Delete for the selected vessel when a
        /// committed future flight holds it, with the reason on their tooltips; takes the reason
        /// off again otherwise (stock's own enable logic is left alone).
        /// </summary>
        internal static void ApplyTrackingStationButtons(SpaceTracking tracking, string site)
        {
            if (tracking == null) return;
            if (ParsekGameModeGate.CheckInert("ClaimedVesselDisposalPatches." + site)) return;
            Vessel selected = null;
            try
            {
                selected = Traverse.Create(tracking).Field("selectedVessel").GetValue<Vessel>();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(ClaimedVesselDisposalGate.Tag, "ts-selected-read-failed",
                    "Tracking Station selectedVessel read failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            GhostChain claiming = null;
            bool held = selected != null
                && ClaimedVesselDisposalGate.IsHeldLive(selected, site, out claiming);
            string why = held ? ClaimedVesselDisposalGate.HoldSentence(claiming) : null;
            if (held)
            {
                bool changed = Grey(tracking.RecoverButton) | Grey(tracking.DeleteButton);
                if (changed)
                    ClaimedVesselDisposalGate.LogBlocked("Tracking Station Recover/Delete (greyed)",
                        selected.persistentId, selected.vesselName, claiming);
            }
            StockUiReasonTooltip.Sync(tracking.RecoverButton, held, why, tracking, Surface + " Recover");
            StockUiReasonTooltip.Sync(tracking.DeleteButton, held, why, tracking, Surface + " Delete");
        }

        /// <summary>
        /// The Tracking Station click-block: true (and the confirm dialog dismissed by the caller)
        /// when the vessel the confirm acts on is held.
        /// </summary>
        internal static bool RefuseTrackingStationDisposal(Vessel selected, string action)
        {
            if (selected == null) return false;
            if (ParsekGameModeGate.CheckInert("ClaimedVesselDisposalPatches." + action)) return false;
            if (!ClaimedVesselDisposalGate.IsHeldLive(selected, action, out GhostChain claiming))
                return false;
            ClaimedVesselDisposalGate.LogBlocked("Tracking Station " + action,
                selected.persistentId, selected.vesselName, claiming);
            return true;
        }

        private static bool Grey(Button button)
        {
            if (button == null || !button.interactable) return false;
            button.interactable = false;
            return true;
        }

        [HarmonyPatch(typeof(SpaceTracking), "SetVessel")]
        internal static class TrackingStationSetVesselPostfix
        {
            static void Postfix(SpaceTracking __instance)
            {
                ApplyTrackingStationButtons(__instance, "SetVessel");
            }
        }

        [HarmonyPatch(typeof(SpaceTracking), "unlockUI")]
        internal static class TrackingStationUnlockUiPostfix
        {
            static void Postfix(SpaceTracking __instance)
            {
                ApplyTrackingStationButtons(__instance, "unlockUI");
            }
        }

        [HarmonyPatch(typeof(KSCVesselMarker), nameof(KSCVesselMarker.Create))]
        internal static class KscMarkerCreatePostfix
        {
            static void Postfix(KSCVesselMarker __result, Vessel v)
            {
                if (__result == null || v == null) return;
                if (ParsekGameModeGate.CheckInert("KscMarkerCreatePostfix")) return;
                if (!ClaimedVesselDisposalGate.IsHeldLive(v, "KSC marker", out GhostChain claiming))
                    return;
                Button recover = null;
                try
                {
                    recover = Traverse.Create(__result).Field("RecoverButton").GetValue<Button>();
                }
                catch (Exception ex)
                {
                    ParsekLog.WarnRateLimited(ClaimedVesselDisposalGate.Tag, "ksc-recover-read-failed",
                        "KSC marker RecoverButton read failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                if (recover == null) return;
                recover.interactable = false;
                StockUiReasonTooltip.Sync(recover, true, ClaimedVesselDisposalGate.HoldSentence(claiming),
                    __result, Surface + " KSC Recover");
                ClaimedVesselDisposalGate.LogBlocked("KSC marker Recover (greyed)",
                    v.persistentId, v.vesselName, claiming);
            }
        }

        [HarmonyPatch(typeof(KSCVesselMarkers), "RecoverVessel", new Type[] { typeof(Vessel) })]
        internal static class KscMarkersRecoverVesselPrefix
        {
            static bool Prefix(Vessel v)
            {
                if (v == null) return true;
                if (ParsekGameModeGate.CheckInert("KscMarkersRecoverVesselPrefix")) return true;
                if (!ClaimedVesselDisposalGate.IsHeldLive(v, "KSC Recover", out GhostChain claiming))
                    return true;
                ClaimedVesselDisposalGate.LogBlocked("KSC marker Recover", v.persistentId, v.vesselName, claiming);
                return false;
            }
        }
    }
}
