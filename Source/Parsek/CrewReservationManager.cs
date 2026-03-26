using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Manages crew reservation and replacement for deferred vessel spawns.
    /// Reserves crew from committed recording snapshots so they can't be placed
    /// on new craft, hires replacements to keep the available pool constant,
    /// and swaps reserved crew out of the active flight vessel.
    /// </summary>
    internal static class CrewReservationManager
    {
        #region Static State

        // Maps reserved kerbal name → replacement kerbal name
        private static Dictionary<string, string> crewReplacements = new Dictionary<string, string>();

        /// <summary>
        /// Read-only access to current replacement mappings. For testing/diagnostics.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> CrewReplacements => crewReplacements;

        #endregion

        #region Public Methods

        /// <summary>
        /// Mark crew from all unspawned vessel snapshots as Assigned so they
        /// can't be placed on new craft in the VAB/SPH.
        /// </summary>
        public static void ReserveSnapshotCrew()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Verbose("CrewReservation", "ReserveSnapshotCrew: no crew roster available — skipping");
                return;
            }

            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                foreach (var rec in RecordingStore.CommittedRecordings)
                {
                    if (rec.LoopPlayback) continue;
                    if (RecordingStore.IsChainFullyDisabled(rec.ChainId)) continue;
                    ReserveCrewIn(rec.VesselSnapshot, rec.VesselSpawned, roster);
                }

                if (RecordingStore.HasPending && RecordingStore.Pending.VesselSnapshot != null)
                    ReserveCrewIn(RecordingStore.Pending.VesselSnapshot, false, roster);
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = false;
            }
        }

        /// <summary>
        /// Set crew in a specific snapshot back to Available.
        /// Call when discarding, recovering, or wiping recordings.
        /// </summary>
        public static void UnreserveCrewInSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null)
            {
                ParsekLog.Verbose("CrewReservation", "UnreserveCrewInSnapshot: null snapshot — skipping");
                return;
            }
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Verbose("CrewReservation", "UnreserveCrewInSnapshot: no crew roster — skipping");
                return;
            }

            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    foreach (string name in partNode.GetValues("crew"))
                    {
                        foreach (ProtoCrewMember pcm in roster.Crew)
                        {
                            if (pcm.name == name && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned)
                            {
                                pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                                CrewLog($"Unreserved crew '{name}'");

                                // Clean up the replacement kerbal
                                CleanUpReplacement(name, roster);

                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = false;
            }
        }

        internal static void ReserveCrewIn(ConfigNode snapshot, bool alreadySpawned, KerbalRoster roster)
        {
            if (snapshot == null || alreadySpawned)
                return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    bool found = false;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name != name) continue;
                        found = true;

                        // Skip dead crew — they're truly gone
                        if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                            break;

                        // Rescue Missing crew — they're alive but orphaned from a
                        // removed vessel (e.g. --clean-start or manual save edits).
                        // The recording will respawn them, so restore them first.
                        if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Missing)
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                            CrewLog($"Rescued Missing crew '{name}' → Available for reservation");
                        }

                        // Hire a replacement kerbal so the available pool stays constant.
                        // This also handles crew who are already Assigned (e.g. on the pad
                        // vessel after a revert) — they still need a replacement so the
                        // swap can move them off the active vessel.
                        if (!crewReplacements.ContainsKey(name))
                        {
                            try
                            {
                                ProtoCrewMember replacement = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                                if (replacement != null)
                                {
                                    KerbalRoster.SetExperienceTrait(replacement, pcm.experienceTrait.TypeName);
                                    crewReplacements[name] = replacement.name;
                                    CrewLog($"Hired replacement '{replacement.name}' " +
                                        $"(trait: {pcm.experienceTrait.TypeName}) for reserved '{name}'");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                CrewLog($"Failed to hire replacement for '{name}': {ex.Message}");
                            }
                        }

                        break;
                    }
                    if (!found)
                        CrewWarn($" Crew '{name}' not found in roster during reservation");
                }
            }
        }

        /// <summary>
        /// Remove all Available replacement kerbals and clear the mapping.
        /// Called when wiping all recordings.
        /// </summary>
        public static void ClearReplacements()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                crewReplacements.Clear();
                return;
            }

            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                foreach (var kvp in new Dictionary<string, string>(crewReplacements))
                {
                    CleanUpReplacement(kvp.Key, roster);
                }

                crewReplacements.Clear();
                CrewLog("Cleared all crew replacements");
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = false;
            }
        }

        /// <summary>
        /// Swap reserved crew out of the active flight vessel, replacing them
        /// with their hired replacements. Prevents the player from recording
        /// with a reserved kerbal again after revert.
        /// </summary>
        public static int SwapReservedCrewInFlight()
        {
            if (FlightGlobals.ActiveVessel == null)
            {
                ParsekLog.Verbose("CrewReservation", "SwapReservedCrewInFlight: no active vessel — skipping");
                return 0;
            }
            if (crewReplacements.Count == 0)
            {
                ParsekLog.Verbose("CrewReservation", "SwapReservedCrewInFlight: no crew replacements — skipping");
                return 0;
            }

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Verbose("CrewReservation", "SwapReservedCrewInFlight: no crew roster — skipping");
                return 0;
            }

            int swapCount = 0;
            int failCount = 0;

            foreach (Part part in FlightGlobals.ActiveVessel.parts)
            {
                // Iterate a copy because RemoveCrewmember modifies the list
                var crewList = new List<ProtoCrewMember>(part.protoModuleCrew);
                for (int i = 0; i < crewList.Count; i++)
                {
                    ProtoCrewMember original = crewList[i];
                    if (!crewReplacements.TryGetValue(original.name, out string replacementName))
                        continue;

                    // Find the replacement in the roster
                    ProtoCrewMember replacement = null;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == replacementName)
                        {
                            replacement = pcm;
                            break;
                        }
                    }

                    if (replacement == null)
                    {
                        CrewLog($"Cannot swap '{original.name}': replacement '{replacementName}' not in roster");
                        failCount++;
                        continue;
                    }

                    int seatIndex = part.protoModuleCrew.IndexOf(original);
                    if (seatIndex < 0)
                    {
                        CrewLog($"Cannot swap '{original.name}': not found in part crew list");
                        failCount++;
                        continue;
                    }
                    part.RemoveCrewmember(original);
                    part.AddCrewmemberAt(replacement, seatIndex);
                    swapCount++;
                    CrewLog($"Swapped '{original.name}' → '{replacement.name}' in part '{part.partInfo.title}'");
                }
            }

            if (swapCount > 0)
            {
                FlightGlobals.ActiveVessel.SpawnCrew();
                GameEvents.onVesselCrewWasModified.Fire(FlightGlobals.ActiveVessel);
                CrewLog($"Crew swap complete: {swapCount} succeeded" +
                    (failCount > 0 ? $", {failCount} failed" : "") +
                    " — refreshed vessel crew display");
            }
            else if (failCount > 0)
            {
                CrewLog($"Crew swap: 0 succeeded, {failCount} failed");
            }

            RemoveReservedEvaVessels();

            return swapCount;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Remove a replacement kerbal from the roster if they're still Available.
        /// If the replacement is Assigned (on a mission), leave them as a "real" kerbal.
        /// </summary>
        private static void CleanUpReplacement(string originalName, KerbalRoster roster)
        {
            if (!crewReplacements.TryGetValue(originalName, out string replacementName))
                return;

            // Always remove the mapping
            crewReplacements.Remove(originalName);

            // Find the replacement in the roster
            ProtoCrewMember replacement = null;
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == replacementName)
                {
                    replacement = pcm;
                    break;
                }
            }

            if (replacement == null)
            {
                CrewLog($"Replacement '{replacementName}' not found in roster (already removed?)");
                return;
            }

            if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Available)
            {
                roster.Remove(replacement);
                CrewLog($"Removed replacement '{replacementName}' (was unused)");
            }
            else
            {
                CrewLog($"Kept replacement '{replacementName}' " +
                    $"(status: {replacement.rosterStatus} — now a real kerbal)");
            }
        }

        /// <summary>
        /// Removes EVA vessels whose crew is reserved (in the replacements dict).
        /// Reserved crew on EVA are separate vessels, not in ActiveVessel.parts.
        /// Removing them prevents duplicates at ghost EndUT spawn.
        /// </summary>
        private static void RemoveReservedEvaVessels()
        {
            int evaRemoved = 0;
            var allVessels = FlightGlobals.Vessels;
            for (int v = allVessels.Count - 1; v >= 0; v--)
            {
                Vessel vessel = allVessels[v];
                if (vessel == FlightGlobals.ActiveVessel) continue;
                if (!vessel.isEVA) continue;

                string evaCrewName = GetEvaCrewName(vessel);
                if (!ShouldRemoveEvaVessel(true, evaCrewName, crewReplacements)) continue;

                CrewLog($"Removing reserved EVA vessel '{evaCrewName}' (pid={vessel.persistentId})");

                // 1. Remove ProtoVessel to prevent re-spawn on save/load
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null && vessel.protoVessel != null)
                    flightState.protoVessels.Remove(vessel.protoVessel);

                // 2. Remove from active vessel list
                allVessels.RemoveAt(v);

                // 3. Unload parts/modules/physics, then destroy GameObject
                vessel.Unload();
                if (vessel.gameObject != null)
                {
                    vessel.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(vessel.gameObject);
                }
                evaRemoved++;
            }

            if (evaRemoved > 0)
            {
                CrewLog($"Removed {evaRemoved} reserved EVA vessel(s)");
                RescueReservedCrewAfterEvaRemoval();
            }
        }

        /// <summary>
        /// Rescues reserved crew members that were set to Missing by vessel.Unload()
        /// during EVA vessel removal. Sets them back to Assigned (not Available) because
        /// they are still reserved for a future ghost spawn.
        /// Must be called after RemoveReservedEvaVessels when EVA vessels were removed.
        /// </summary>
        private static void RescueReservedCrewAfterEvaRemoval()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                CrewLog("RescueReservedCrewAfterEvaRemoval: no crew roster — skipping");
                return;
            }

            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                int rescued = 0;
                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (!ShouldRescueFromMissing(pcm.rosterStatus, pcm.name, crewReplacements))
                        continue;

                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                    rescued++;
                    CrewLog($"Rescued Missing crew '{pcm.name}' → Assigned " +
                        $"(was orphaned by EVA vessel removal)");
                }

                if (rescued > 0)
                    CrewLog($"Rescued {rescued} reserved crew member(s) from Missing status");
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = false;
            }
        }

        /// <summary>
        /// Returns the single crew member's name from an EVA vessel, or null.
        /// Uses GetVesselCrew() for robustness with both packed and unpacked vessels.
        /// </summary>
        private static string GetEvaCrewName(Vessel evaVessel)
        {
            var crew = evaVessel.GetVesselCrew();
            return crew.Count > 0 ? crew[0].name : null;
        }

        private static void CrewLog(string message)
        {
            ParsekLog.Info("CrewReservation", message ?? "(empty)");
        }

        private static void CrewWarn(string message)
        {
            ParsekLog.Warn("CrewReservation", message ?? "(empty)");
        }

        #endregion

        #region Pure Static Methods

        /// <summary>
        /// Pure decision: should this EVA vessel be removed during crew swap?
        /// An EVA vessel is removed if its crew member is reserved (in the replacements dict).
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldRemoveEvaVessel(
            bool isEva, string crewName, IReadOnlyDictionary<string, string> replacements)
        {
            return isEva && !string.IsNullOrEmpty(crewName) && replacements.ContainsKey(crewName);
        }

        /// <summary>
        /// Returns true if a crew member with the given roster status should be
        /// processed for reservation (i.e. not dead). Missing crew are processed
        /// because they may be alive but orphaned from a removed vessel.
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldProcessCrewForReservation(ProtoCrewMember.RosterStatus status)
        {
            return status != ProtoCrewMember.RosterStatus.Dead;
        }

        /// <summary>
        /// Pure decision: should this crew member be rescued from Missing status?
        /// A Missing crew member is rescued if they are reserved (in the replacements dict),
        /// indicating they were orphaned by RemoveReservedEvaVessels calling vessel.Unload().
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldRescueFromMissing(
            ProtoCrewMember.RosterStatus status,
            string crewName,
            IReadOnlyDictionary<string, string> replacements)
        {
            return status == ProtoCrewMember.RosterStatus.Missing
                && !string.IsNullOrEmpty(crewName)
                && replacements.ContainsKey(crewName);
        }

        /// <summary>
        /// Extracts crew names from a vessel snapshot ConfigNode.
        /// </summary>
        internal static List<string> ExtractCrewFromSnapshot(ConfigNode snapshot)
        {
            var crew = new List<string>();
            if (snapshot == null) return crew;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    if (!string.IsNullOrEmpty(name))
                        crew.Add(name);
                }
            }
            return crew;
        }

        #endregion

        #region Testing & Serialization

        /// <summary>
        /// Clears replacement dictionary without roster access. For unit tests only.
        /// </summary>
        internal static void ResetReplacementsForTesting()
        {
            crewReplacements.Clear();
        }

        /// <summary>
        /// Load crew replacement mappings from a ConfigNode.
        /// </summary>
        internal static void LoadCrewReplacements(ConfigNode node)
        {
            crewReplacements.Clear();

            ConfigNode replacementsNode = node.GetNode("CREW_REPLACEMENTS");
            if (replacementsNode == null)
            {
                CrewLog("Loaded 0 crew replacements (no CREW_REPLACEMENTS node)");
                return;
            }

            ConfigNode[] entries = replacementsNode.GetNodes("ENTRY");
            for (int i = 0; i < entries.Length; i++)
            {
                string original = entries[i].GetValue("original");
                string replacement = entries[i].GetValue("replacement");
                if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(replacement))
                {
                    crewReplacements[original] = replacement;
                }
            }

            CrewLog($"Loaded {crewReplacements.Count} crew replacement(s)");
        }

        internal static void SaveCrewReplacements(ConfigNode node)
        {
            if (crewReplacements.Count > 0)
            {
                ConfigNode replacementsNode = node.AddNode("CREW_REPLACEMENTS");
                foreach (var kvp in crewReplacements)
                {
                    ConfigNode entry = replacementsNode.AddNode("ENTRY");
                    entry.AddValue("original", kvp.Key);
                    entry.AddValue("replacement", kvp.Value);
                }
                CrewLog($"Saved {crewReplacements.Count} crew replacement(s)");
            }
        }

        #endregion
    }
}
