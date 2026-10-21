using System;
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
    /// The Prefix reads the cycle keys first and returns true at once when neither is down.
    /// On a press it runs the same walk and drops (a) every ghost map marker, always (GC-4: a
    /// marker inside physics range is a loaded vessel of the recorded type, and stock would
    /// make it the active vessel and then copy its throttle and action groups onto the real
    /// one through <c>FlightInputHandler.ResumeVesselCtrlState</c>), and (b) during a live
    /// Re-Fly session every candidate <see cref="ReFlySwitchGate"/> refuses. When nothing was
    /// dropped stock runs unchanged; otherwise the Prefix performs stock's final step over the
    /// remaining candidates itself, and when none remain it logs why and does nothing. An
    /// empty stock candidate list is left to stock (its own message).
    /// <c>FlightGlobals.SetActiveVessel</c> is not patched here: EVA, board, dock and undock
    /// keep working.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(VesselSwitching), "Update")]
    internal static class VesselCycleReFlySwitchPatch
    {
        /// <summary>What a cycle press keeps, pure over candidate pids in stock's order.</summary>
        internal struct CycleFilter
        {
            public List<int> KeptIndices;
            public int GhostsDropped;
            public int RefusedDropped;
            /// <summary>Index of the first non-ghost candidate the session refused, or -1.</summary>
            public int FirstRefusedIndex;
            public bool LeaveToStock => GhostsDropped == 0 && RefusedDropped == 0;
        }

        /// <summary>
        /// Drops ghost map markers (<paramref name="isGhost"/>) and, when
        /// <paramref name="isAllowed"/> is non-null (a live Re-Fly session), every candidate it
        /// refuses.
        /// </summary>
        internal static CycleFilter FilterCandidates(
            IList<uint> candidatePids, Func<uint, bool> isGhost, Func<uint, bool> isAllowed)
        {
            var f = new CycleFilter { KeptIndices = new List<int>(), FirstRefusedIndex = -1 };
            if (candidatePids == null) return f;
            for (int i = 0; i < candidatePids.Count; i++)
            {
                uint pid = candidatePids[i];
                if (isGhost != null && isGhost(pid)) { f.GhostsDropped++; continue; }
                if (isAllowed != null && !isAllowed(pid))
                {
                    f.RefusedDropped++;
                    if (f.FirstRefusedIndex < 0) f.FirstRefusedIndex = i;
                    continue;
                }
                f.KeptIndices.Add(i);
            }
            return f;
        }

        static bool Prefix()
        {
            bool prev = GameSettings.FOCUS_PREV_VESSEL.GetKeyDown();
            if (!prev && !GameSettings.FOCUS_NEXT_VESSEL.GetKeyDown())
                return true;
            if (ParsekGameModeGate.CheckInert("VesselCycleReFlySwitchPatch.Prefix")) return true; // S9 game-mode gate

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

            var candidatePids = new List<uint>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
                candidatePids.Add(candidates[i].persistentId);

            ReFlySwitchGate.LiveSession session = default(ReFlySwitchGate.LiveSession);
            HashSet<uint> allowed = null;
            var scenario = ParsekScenario.Instance;
            if (!ReferenceEquals(null, scenario) && scenario.ActiveReFlySessionMarker != null)
            {
                session = ReFlySwitchGate.ResolveLiveSession();
                if (session.Marker != null)
                    allowed = ReFlySwitchGate.AllowedPidsFor(session);
            }

            Func<uint, bool> isAllowed = null;
            if (allowed != null)
                isAllowed = pid => ReFlySwitchGate.Decide(true, pid, allowed, ReFlySwitchGate.PathVesselCycle).Allowed;
            CycleFilter filter = FilterCandidates(candidatePids, GhostMapPresence.IsGhostMapVessel, isAllowed);
            if (filter.LeaveToStock)
                return true; // stock picks from the same list

            if (filter.KeptIndices.Count == 0)
            {
                if (filter.FirstRefusedIndex >= 0)
                {
                    uint refusedPid = candidatePids[filter.FirstRefusedIndex];
                    ReFlySwitchDecision first = ReFlySwitchGate.Decide(true, refusedPid, allowed,
                        ReFlySwitchGate.PathVesselCycle);
                    ParsekLog.Info(ReFlySwitchGate.LogTag, ReFlySwitchGate.FormatRefusal(
                        session.Marker.SessionId, ReFlySwitchGate.PathVesselCycle, refusedPid, first.Reason));
                }
                else
                {
                    ParsekLog.Info("GhostMap",
                        $"Vessel cycle found only ghost map markers ({filter.GhostsDropped}) - no switch");
                }
                return false;
            }

            Vessel next = candidates[filter.KeptIndices[0]];
            ParsekLog.Verbose(filter.RefusedDropped > 0 ? ReFlySwitchGate.LogTag : "GhostMap",
                $"Vessel cycle skipped {filter.GhostsDropped} ghost marker(s) and {filter.RefusedDropped} refused candidate(s) " +
                $"sess={(session.Marker != null ? session.Marker.SessionId ?? "<no-id>" : "<none>")} " +
                $"next={next.persistentId} candidates={candidates.Count}");

            CameraManager.Instance.SetCameraFlight();
            ClearToSaveStatus clear = FlightGlobals.ClearToSave();
            for (int k = 0; k < filter.KeptIndices.Count; k++)
            {
                Vessel v = candidates[filter.KeptIndices[k]];
                if (v.packed && clear != ClearToSaveStatus.CLEAR)
                    continue;
                FlightGlobals.ForceSetActiveVessel(v);
                FlightInputHandler.ResumeVesselCtrlState(v);
                return false;
            }
            FlightGlobals.SetActiveVessel(next);
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
