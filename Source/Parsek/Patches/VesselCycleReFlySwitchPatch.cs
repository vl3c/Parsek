using System.Collections.Generic;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Design section 2.12 Rule 3: the stock <c>[</c> / <c>]</c> vessel cycle during a Re-Fly
    /// session reaches only the re-flown vessel and its own EVA crew.
    ///
    /// <para>
    /// Decompiled <c>VesselSwitching.Update</c> (KSP 1.12.5 Assembly-CSharp.dll) is the only
    /// stock reader of <c>GameSettings.FOCUS_PREV_VESSEL</c> / <c>FOCUS_NEXT_VESSEL</c>. It
    /// returns early in map view, under a <c>ControlTypes.VESSEL_SWITCHING</c> lock, while
    /// paused, outside FLIGHT and (with a screen message) when <c>CanSwitchVesselsNear</c> is
    /// off. Otherwise it walks <c>FlightGlobals.Vessels</c> from the active vessel's index in
    /// the key's direction and collects every LOADED vessel that is not the active one, not a
    /// <c>DeployedSciencePart</c>, and (unless the modifier key is held) not a
    /// <c>vesselType &lt;= SpaceObject</c> (debris, space object, unknown). It then switches
    /// with <c>ForceSetActiveVessel</c> + <c>FlightInputHandler.ResumeVesselCtrlState</c> to the
    /// first candidate that is unpacked or for which <c>ClearToSave</c> is clear, else calls
    /// <c>SetActiveVessel(candidates[0])</c> (which posts stock's own refusal).
    /// </para>
    ///
    /// <para>
    /// With no live session this Prefix returns true at once (one marker read per frame).
    /// With a session and a cycle key down it runs the same walk, drops every candidate the
    /// gate refuses, and performs stock's final step over the remaining ones itself; when
    /// none remain it logs the refusal for the candidate stock would have picked and does
    /// nothing. An empty stock candidate list is left to stock (its own message).
    /// <c>FlightGlobals.SetActiveVessel</c> is not patched: EVA, board, dock and undock keep
    /// working.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(VesselSwitching), "Update")]
    internal static class VesselCycleReFlySwitchPatch
    {
        static bool Prefix()
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario) || scenario.ActiveReFlySessionMarker == null)
                return true;
            if (ParsekGameModeGate.CheckInert("VesselCycleReFlySwitchPatch.Prefix")) return true; // S9 game-mode gate

            bool prev = GameSettings.FOCUS_PREV_VESSEL.GetKeyDown();
            if (!prev && !GameSettings.FOCUS_NEXT_VESSEL.GetKeyDown())
                return true;

            // Stock's own early returns: leave them to stock.
            if (MapView.MapIsEnabled
                || InputLockManager.IsLocked(ControlTypes.VESSEL_SWITCHING)
                || FlightDriver.Pause
                || HighLogic.CurrentGame == null
                || !HighLogic.LoadedSceneIsFlight
                || !HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsNear)
                return true;

            Vessel active = FlightGlobals.ActiveVessel;
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (active == null || vessels == null || vessels.Count == 0)
                return true;

            var candidates = CollectStockCandidates(vessels, active, prev ? -1 : 1);
            if (candidates.Count == 0)
                return true;

            ReFlySwitchGate.LiveSession session = ReFlySwitchGate.ResolveLiveSession();
            if (session.Marker == null)
                return true;
            HashSet<uint> allowed = ReFlySwitchGate.AllowedPidsFor(session);

            var candidatePids = new List<uint>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
                candidatePids.Add(candidates[i].persistentId);

            var allowedCandidates = new List<Vessel>();
            int refused = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                ReFlySwitchDecision d = ReFlySwitchGate.Decide(true, candidatePids[i], allowed,
                    ReFlySwitchGate.PathVesselCycle);
                if (d.Allowed) allowedCandidates.Add(candidates[i]);
                else refused++;
            }

            if (allowedCandidates.Count == 0)
            {
                ReFlySwitchDecision first = ReFlySwitchGate.Decide(true, candidatePids[0], allowed,
                    ReFlySwitchGate.PathVesselCycle);
                ParsekLog.Info(ReFlySwitchGate.LogTag, ReFlySwitchGate.FormatRefusal(
                    session.Marker.SessionId, ReFlySwitchGate.PathVesselCycle, candidatePids[0], first.Reason));
                return false;
            }

            if (refused > 0)
                ParsekLog.Verbose(ReFlySwitchGate.LogTag,
                    $"Vessel cycle skipped {refused} refused candidate(s) sess={session.Marker.SessionId ?? "<no-id>"} " +
                    $"next={allowedCandidates[0].persistentId} candidates={candidates.Count}");

            if (refused == 0)
                return true; // stock picks from the same list

            CameraManager.Instance.SetCameraFlight();
            ClearToSaveStatus clear = FlightGlobals.ClearToSave();
            for (int i = 0; i < allowedCandidates.Count; i++)
            {
                Vessel v = allowedCandidates[i];
                if (v.packed && clear != ClearToSaveStatus.CLEAR)
                    continue;
                FlightGlobals.ForceSetActiveVessel(v);
                FlightInputHandler.ResumeVesselCtrlState(v);
                return false;
            }
            FlightGlobals.SetActiveVessel(allowedCandidates[0]);
            return false;
        }

        /// <summary>Stock's candidate walk, in stock's order (see the class summary).</summary>
        private static List<Vessel> CollectStockCandidates(List<Vessel> vessels, Vessel active, int step)
        {
            var result = new List<Vessel>();
            int count = vessels.Count;
            int index = vessels.IndexOf(active);
            if (index < 0) return result;
            bool modifier = GameSettings.MODIFIER_KEY.GetKey();
            for (int n = 0; n < count; n++)
            {
                index += step;
                if (index < 0) index += count;
                else if (index >= count) index -= count;
                Vessel v = vessels[index];
                if (v == null) continue;
                if (v == active) break;
                if (!v.loaded) continue;
                if (v.vesselType == VesselType.DeployedSciencePart) continue;
                if (v.vesselType <= VesselType.SpaceObject && !modifier) continue;
                result.Add(v);
            }
            return result;
        }
    }
}
