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
    ///
    /// <para>
    /// The vessel the press lands on then takes the map Switch-To's pre-switch handling
    /// (owner ruling 2026-11-18; <see cref="MapFocusObjectOnSelectPatch.TryTakePreSwitch"/>):
    /// with an armed switch segment focused on another vessel (Case A), or a live recording
    /// and a loaded, separately committed target (Case C), the recording the player leaves is
    /// merged or discarded first and the switch is made by that handler (or the dialog's
    /// buttons) with the cycle's own call, so the target picks up its own committed mission
    /// through the committed-clone path. While stock is not clear to save such a press is refused
    /// as the map Switch-To is, with no commit and no switch. Any other press (GC-4's cycle
    /// between members of one tree, with no switch session armed) routes exactly as before and
    /// arms no intent.
    /// </para>
    ///
    /// <para>
    /// Everything after the key read and the game-mode gate is <see cref="RunCycle"/>, which
    /// the automation-only <c>CycleVessel</c> seam verb calls as a key press would (no seam
    /// can press a key); its optional report is the only difference, and the key path passes
    /// none.
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

        /// <summary>How a press reaches the vessel it lands on.</summary>
        internal enum CycleRoute
        {
            /// <summary>Every candidate was dropped (ghost markers or Re-Fly refusals).</summary>
            NothingKept,
            /// <summary>Nothing was dropped: stock's own final step switches.</summary>
            LeaveToStock,
            /// <summary>The Prefix switches with <c>ForceSetActiveVessel</c>.</summary>
            ForceSwitch,
            /// <summary>Every kept candidate is packed and the game is not clear to save:
            /// stock's <c>SetActiveVessel</c> fallback, which stock refuses with its own message.</summary>
            SetActiveFallback,
            /// <summary>The target needs the map Switch-To's pre-switch handling.</summary>
            PreSwitch,
        }

        /// <summary>The pure routing of one press: the route, the candidate index it lands on
        /// (-1 for none), and the route the press takes when the pre-switch handling does not
        /// take the switch.</summary>
        internal struct CycleRouting
        {
            public CycleRoute Route;
            public int TargetIndex;
            public CycleRoute DirectRoute;
        }

        /// <summary>
        /// Pure: where a press goes, over the candidates in stock's order. The target is the
        /// candidate stock's own final step takes (from the kept list when anything was
        /// dropped): the first one that is unpacked, or any one when the game is clear to save.
        /// <paramref name="takePreSwitch"/> (by candidate index) is called at most once, for
        /// the target, and only for a switch that will happen (a switch stock refuses decides
        /// nothing, as the map Switch-To's own <c>StockWillRefuse</c> rule); true means the
        /// shared pre-switch handling took the switch (<see cref="CycleRoute.PreSwitch"/>),
        /// false leaves the press on <see cref="CycleRouting.DirectRoute"/>. The live caller
        /// passes <see cref="MapFocusObjectOnSelectPatch.TryTakePreSwitch"/>.
        /// </summary>
        internal static CycleRouting RouteCycle(
            CycleFilter filter, IList<bool> candidatePacked, bool clearToSave, Func<int, bool> takePreSwitch)
        {
            var r = new CycleRouting { Route = CycleRoute.NothingKept, TargetIndex = -1, DirectRoute = CycleRoute.NothingKept };
            if (filter.LeaveToStock)
            {
                int pick = StockPickIndex(candidatePacked, clearToSave);
                r.Route = r.DirectRoute = CycleRoute.LeaveToStock;
                r.TargetIndex = pick;
                return AskPreSwitch(r, candidatePacked, clearToSave, takePreSwitch);
            }
            if (filter.KeptIndices == null || filter.KeptIndices.Count == 0)
                return r;
            for (int k = 0; k < filter.KeptIndices.Count; k++)
            {
                int i = filter.KeptIndices[k];
                bool packed = candidatePacked != null && i < candidatePacked.Count && candidatePacked[i];
                if (packed && !clearToSave) continue;
                r.Route = r.DirectRoute = CycleRoute.ForceSwitch;
                r.TargetIndex = i;
                return AskPreSwitch(r, candidatePacked, clearToSave, takePreSwitch);
            }
            r.Route = r.DirectRoute = CycleRoute.SetActiveFallback;
            r.TargetIndex = filter.KeptIndices[0];
            return r;
        }

        private static CycleRouting AskPreSwitch(
            CycleRouting r, IList<bool> candidatePacked, bool clearToSave, Func<int, bool> takePreSwitch)
        {
            if (takePreSwitch == null || r.TargetIndex < 0) return r;
            bool packed = candidatePacked != null && r.TargetIndex < candidatePacked.Count
                && candidatePacked[r.TargetIndex];
            // Stock's own fallback (every candidate packed, not clear to save) is a switch
            // setActiveVessel refuses: it decides nothing.
            if (packed && !clearToSave) return r;
            if (takePreSwitch(r.TargetIndex)) r.Route = CycleRoute.PreSwitch;
            return r;
        }

        /// <summary>What one cycle press did, filled only for the automation seam
        /// (<c>CycleVessel</c>); the key path passes no report.</summary>
        internal enum CycleOutcome
        {
            /// <summary>One of stock's own early returns applied (see <see cref="CycleReport.Detail"/>).</summary>
            StockEarlyReturn,
            NoActiveVessel,
            /// <summary>Stock collected no candidate; stock posts its own message.</summary>
            NoCandidates,
            /// <summary>Nothing was dropped: stock picks from the same list.</summary>
            LeftToStock,
            OnlyGhosts,
            OnlyRefused,
            /// <summary>The Prefix switched with <c>ForceSetActiveVessel</c>.</summary>
            Switched,
            /// <summary>Every kept candidate was packed and the game was not clear to save:
            /// stock's own <c>SetActiveVessel</c> fallback on the first kept candidate.</summary>
            SetActiveFallback,
            /// <summary>The press landed on a vessel that needs the map Switch-To's
            /// pre-switch handling (Case A / Case C), which took the switch: its Merge /
            /// Discard handler switched (<see cref="CycleReport.Detail"/> <c>auto-resolved</c>),
            /// its dialog is open and its buttons switch (<c>dialog-open</c>), or stock was not
            /// clear to save and the press was refused like the map Switch-To, with no commit and
            /// no switch (<c>refused-not-clear-to-save</c>).</summary>
            PreSwitchHandoff,
        }

        /// <summary>The seam's view of one press. Candidate lists are in stock's order.</summary>
        internal sealed class CycleReport
        {
            public CycleOutcome Outcome;
            public string Detail;
            public readonly List<uint> CandidatePids = new List<uint>();
            public readonly List<bool> CandidateIsGhost = new List<bool>();
            public int GhostsDropped;
            public int RefusedDropped;
            /// <summary>The vessel the Prefix handed to the switch call, or 0.</summary>
            public uint PickedPid;
            /// <summary>The vessel stock's unfiltered final step would have taken, or 0.
            /// Filled on a press the Prefix itself switched, and by the seam on a press left
            /// to stock.</summary>
            public uint StockWouldPickPid;
            public bool StockWouldPickGhost;
        }

        static bool Prefix()
        {
            bool prev = GameSettings.FOCUS_PREV_VESSEL.GetKeyDown();
            if (!prev && !GameSettings.FOCUS_NEXT_VESSEL.GetKeyDown())
                return true;
            if (ParsekGameModeGate.CheckInert("VesselCycleReFlySwitchPatch.Prefix")) return true; // S9 game-mode gate
            return RunCycle(prev, null);
        }

        /// <summary>
        /// Everything the Prefix does after a cycle key press and the game-mode gate. Returns
        /// true when stock's <c>VesselSwitching.Update</c> should run. The automation seam
        /// (<c>CycleVessel</c>) calls it with a <paramref name="report"/> exactly as a key press
        /// reaches it; with a null report (the key path) it reads nothing more than before.
        /// </summary>
        internal static bool RunCycle(bool prev, CycleReport report)
        {
            // Stock's own early returns: leave them to stock.
            string early = StockEarlyReturnReason();
            if (early != null)
            {
                if (report != null) { report.Outcome = CycleOutcome.StockEarlyReturn; report.Detail = early; }
                return true;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (active == null || vessels == null || vessels.Count == 0)
            {
                if (report != null) report.Outcome = CycleOutcome.NoActiveVessel;
                return true;
            }

            var candidates = CollectStockCandidates(vessels, active, prev ? -1 : 1);
            if (candidates.Count == 0)
            {
                if (report != null) report.Outcome = CycleOutcome.NoCandidates;
                return true;
            }

            var candidatePids = new List<uint>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
                candidatePids.Add(candidates[i].persistentId);
            if (report != null) FillCandidates(report, candidatePids);

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
            CycleFilter filter = FilterCandidates(candidatePids, GhostMapPresence.IsGhostOrDroneVessel, isAllowed);
            if (report != null) { report.GhostsDropped = filter.GhostsDropped; report.RefusedDropped = filter.RefusedDropped; }

            if (!filter.LeaveToStock && filter.KeptIndices.Count == 0)
            {
                if (filter.FirstRefusedIndex >= 0)
                {
                    uint refusedPid = candidatePids[filter.FirstRefusedIndex];
                    ReFlySwitchDecision first = ReFlySwitchGate.Decide(true, refusedPid, allowed,
                        ReFlySwitchGate.PathVesselCycle);
                    ParsekLog.Info(ReFlySwitchGate.LogTag, ReFlySwitchGate.FormatRefusal(
                        session.Marker.SessionId, ReFlySwitchGate.PathVesselCycle, refusedPid, first.Reason));
                    if (report != null) report.Outcome = CycleOutcome.OnlyRefused;
                }
                else
                {
                    ParsekLog.Info("GhostMap",
                        $"Vessel cycle found only ghost map markers ({filter.GhostsDropped}) - no switch");
                    if (report != null) report.Outcome = CycleOutcome.OnlyGhosts;
                }
                return false;
            }

            // Owner ruling 2026-11-18: a press that lands on a vessel needing the map
            // Switch-To's pre-switch handling (Case A / Case C) takes it, after the candidate
            // order, the ghost drop and the Re-Fly gate above. Stock reads ClearToSave in its
            // own final step too.
            bool clearToSave = FlightGlobals.ClearToSave() == ClearToSaveStatus.CLEAR;
            var packed = new List<bool>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
                packed.Add(candidates[i].packed);
            MapFocusObjectOnSelectPatch.PreSwitchTake take = MapFocusObjectOnSelectPatch.PreSwitchTake.NotTaken;
            CycleRouting routing = RouteCycle(filter, packed, clearToSave, i =>
            {
                take = MapFocusObjectOnSelectPatch.TryTakePreSwitch(candidates[i], cycleSwitch: true);
                return take != MapFocusObjectOnSelectPatch.PreSwitchTake.NotTaken;
            });
            if (report != null) FillStockPick(report, candidates, clearToSave);

            if (routing.Route == CycleRoute.PreSwitch)
            {
                Vessel target = candidates[routing.TargetIndex];
                string detail = take == MapFocusObjectOnSelectPatch.PreSwitchTake.DialogOpened
                    ? "dialog-open"
                    : take == MapFocusObjectOnSelectPatch.PreSwitchTake.RefusedNotClearToSave
                        ? "refused-not-clear-to-save" : "auto-resolved";
                ParsekLog.Info("SwitchIntentPatch",
                    $"Vessel cycle handed to pre-switch handling: targetPid={target.persistentId} " +
                    $"take={take} directRoute={routing.DirectRoute} ghostsDropped={filter.GhostsDropped} " +
                    $"refusedDropped={filter.RefusedDropped} candidates={candidates.Count}");
                if (report != null)
                {
                    report.Outcome = CycleOutcome.PreSwitchHandoff;
                    report.Detail = detail;
                    report.PickedPid = target.persistentId;
                }
                return false; // the pre-switch handler (or its dialog) switches
            }

            if (routing.Route == CycleRoute.LeaveToStock)
            {
                // Seam only: the vessel stock's final step will take from this list.
                if (report != null) report.Outcome = CycleOutcome.LeftToStock;
                return true; // stock picks from the same list
            }

            Vessel next = candidates[filter.KeptIndices[0]];
            ParsekLog.Verbose(filter.RefusedDropped > 0 ? ReFlySwitchGate.LogTag : "GhostMap",
                $"Vessel cycle skipped {filter.GhostsDropped} ghost marker(s) and {filter.RefusedDropped} refused candidate(s) " +
                $"sess={(session.Marker != null ? session.Marker.SessionId ?? "<no-id>" : "<none>")} " +
                $"next={next.persistentId} candidates={candidates.Count}");

            CameraManager.Instance.SetCameraFlight();
            if (routing.Route == CycleRoute.ForceSwitch)
            {
                Vessel v = candidates[routing.TargetIndex];
                if (report != null) { report.Outcome = CycleOutcome.Switched; report.PickedPid = v.persistentId; }
                FlightGlobals.ForceSetActiveVessel(v);
                FlightInputHandler.ResumeVesselCtrlState(v);
                return false;
            }
            if (report != null) { report.Outcome = CycleOutcome.SetActiveFallback; report.PickedPid = next.persistentId; }
            FlightGlobals.SetActiveVessel(next);
            return false;
        }

        /// <summary>
        /// Stock's early returns in stock's order (decompiled <c>VesselSwitching.Update</c>):
        /// the name of the first that applies, or null. Same short-circuit order as stock.
        /// </summary>
        private static string StockEarlyReturnReason()
        {
            if (MapView.MapIsEnabled) return "map-view";
            if (InputLockManager.IsLocked(ControlTypes.VESSEL_SWITCHING)) return "switch-locked";
            if (FlightDriver.Pause) return "paused";
            if (HighLogic.CurrentGame == null) return "no-game";
            if (!HighLogic.LoadedSceneIsFlight) return "not-flight";
            if (!HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsNear) return "switch-near-off";
            return null;
        }

        /// <summary>
        /// Index of the candidate stock's final step hands to a switch call: the first one
        /// that is unpacked, or any one when the game is clear to save; when none qualifies,
        /// stock's <c>SetActiveVessel(candidates[0])</c> fallback, index 0. -1 for no candidate.
        /// </summary>
        internal static int StockPickIndex(IList<bool> candidatePacked, bool clearToSave)
        {
            if (candidatePacked == null || candidatePacked.Count == 0) return -1;
            for (int i = 0; i < candidatePacked.Count; i++)
                if (!candidatePacked[i] || clearToSave)
                    return i;
            return 0;
        }

        private static void FillCandidates(CycleReport report, List<uint> candidatePids)
        {
            for (int i = 0; i < candidatePids.Count; i++)
            {
                report.CandidatePids.Add(candidatePids[i]);
                report.CandidateIsGhost.Add(GhostMapPresence.IsGhostOrDroneVessel(candidatePids[i]));
            }
        }

        /// <summary>Records which candidate stock's unfiltered final step would take.</summary>
        internal static void FillStockPick(CycleReport report, List<Vessel> candidates, bool clearToSave)
        {
            var packed = new List<bool>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
                packed.Add(candidates[i].packed);
            int pick = StockPickIndex(packed, clearToSave);
            if (pick < 0 || pick >= report.CandidatePids.Count) return;
            report.StockWouldPickPid = report.CandidatePids[pick];
            report.StockWouldPickGhost = report.CandidateIsGhost[pick];
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
