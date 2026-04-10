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
        /// Clean up replacement kerbals for crew in a snapshot.
        /// Call when discarding, recovering, or wiping recordings.
        ///
        /// No rosterStatus changes — reserved kerbals stay at their natural status
        /// (typically Available). CrewDialogFilterPatch handles crew dialog filtering.
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

            using (SuppressionGuard.Crew())
            {
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    foreach (string name in partNode.GetValues("crew"))
                    {
                        if (!string.IsNullOrEmpty(name))
                            CleanUpReplacement(name, roster);
                    }
                }
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

            using (SuppressionGuard.Crew())
            {
                foreach (var kvp in new Dictionary<string, string>(crewReplacements))
                {
                    CleanUpReplacement(kvp.Key, roster);
                }

                crewReplacements.Clear();
                CrewLog("Cleared all crew replacements");
            }
        }

        /// <summary>
        /// Swap reserved crew out of the active flight vessel, replacing them
        /// with their hired replacements. Prevents the player from recording
        /// with a reserved kerbal again after revert.
        ///
        /// Two passes (bug #277):
        ///   Pass 1: walk active vessel parts and swap any reserved kerbal currently
        ///           in a seat (the legacy path — handles the common in-pod case).
        ///   Pass 2: for any reservation whose original is NOT in the active vessel
        ///           (typically because the kerbal is on a separate EVA vessel), look
        ///           up the recording snapshot that originally seated them, find the
        ///           matching part in the active vessel by persistentId/name, and
        ///           place the stand-in into a free seat there. This catches the
        ///           common scenario where the player EVA'd one of the launch crew
        ///           before merge.
        ///
        /// Seat resolution lives at swap time (not at SetReplacement time) because
        /// SetReplacement runs on every commit/recalculate cycle and would pay the
        /// snapshot-walk cost on hot paths even when no orphan exists. The orphan
        /// pass only runs when the swap actually fails to place every replacement.
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

            // Guard: skip crew swap entirely for Parsek-spawned vessels. Their crew
            // was definitively set by VesselSpawner (RemoveDeadCrewFromSnapshot,
            // RemoveSpecificCrewFromSnapshot for EVA'd crew, UnreserveCrewInSnapshot).
            // Swapping or orphan-placing into a spawned vessel is always wrong — it
            // fills empty seats that are intentionally empty (crew who EVA'd or died).
            var spawnedPids = BuildSpawnedVesselPidSet(RecordingStore.CommittedRecordings);
            uint activePid = FlightGlobals.ActiveVessel.persistentId;
            if (spawnedPids.Contains(activePid))
            {
                ParsekLog.Info("CrewReservation",
                    $"SwapReservedCrewInFlight skipped: active vessel pid={activePid} " +
                    "is a Parsek-spawned vessel (crew already set by spawn path)");
                RemoveReservedEvaVessels();
                return 0;
            }

            int swapCount = 0;
            int failCount = 0;
            var swappedOriginals = new HashSet<string>();

            // Pass 1 — legacy path: kerbals currently seated in the active vessel.
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
                    swappedOriginals.Add(original.name);
                    CrewLog($"Swapped '{original.name}' → '{replacement.name}' in part '{part.partInfo.title}'");
                }
            }

            // Pass 2 — bug #277 orphan placement: reserved kerbals NOT seated in
            // the active vessel (typically EVA'd before merge). Look up where the
            // recording snapshot originally seated them and place the stand-in there.
            int orphanPlaced = PlaceOrphanedReplacements(roster, swappedOriginals);
            swapCount += orphanPlaced;

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

        /// <summary>
        /// Bug #277 second pass: for each replacement whose original is NOT in
        /// the active vessel (e.g. because the original is on a separate EVA
        /// vessel that's about to be removed), look up the recording snapshot
        /// that originally seated them and place the stand-in into a matching
        /// part on the active vessel.
        ///
        /// Adds successfully-placed originals to <paramref name="swappedOriginals"/>
        /// so RemoveReservedEvaVessels can proceed cleanly afterwards.
        /// Returns the number of placements completed.
        ///
        /// `internal` rather than `private` so the in-game integration test
        /// (`Bug277_PlaceOrphanedReplacements_PlacesStandinFromSnapshot`) can
        /// invoke just the orphan-pass without triggering the surrounding
        /// SpawnCrew + RemoveReservedEvaVessels side effects.
        /// </summary>
        internal static int PlaceOrphanedReplacements(
            KerbalRoster roster, HashSet<string> swappedOriginals)
        {
            int placed = 0;
            int orphanCount = 0;

            // Distinct skip/fail counters (per PR #175 review): keep infrastructural
            // failures separated from placement-impossible cases for diagnostics.
            int rescuedFromMissing = 0;
            int skippedDeadOrMissingReplacement = 0;     // Warn: alarming
            int skippedReplacementNotInRoster = 0;       // Warn: alarming
            int skippedAlreadyOnActiveVessel = 0;        // Info: nothing to do, expected sometimes
            int skippedOriginalStillOnActiveVessel = 0;  // Info: defensive — Pass 1 didn't swap them but they're seated
            int skippedSnapshotMiss = 0;                 // Warn: orphan but no snapshot trail
            int skippedNoMatchingPart = 0;               // Warn: snapshot found but no live part

            // Build the snapshot list once. Use GhostVisualSnapshot (recording-start
            // state) — VesselSnapshot is end-of-recording and would not contain a
            // crew member who EVA'd mid-recording.
            var snapshots = new List<ConfigNode>();
            var committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var snap = committed[i].GhostVisualSnapshot;
                    if (snap != null) snapshots.Add(snap);
                }
            }

            // Build the active-vessel crew name set ONCE up front (PR #175 review):
            // O(parts × crew) build, then O(1) per orphan lookup, vs the previous
            // O(parts × crew) per orphan. Negligible at small N but cleaner for
            // pathological multi-pod cases.
            var activeVesselCrewNames = BuildActiveVesselCrewNameSet();

            // Snapshot crew lists may contain stand-in names from earlier
            // recordings (see KerbalsModule.ReverseMapCrewNames). Pass the
            // current crewReplacements as the reverse-map source.
            foreach (var kvp in crewReplacements)
            {
                string originalName = kvp.Key;
                string replacementName = kvp.Value;

                if (swappedOriginals.Contains(originalName))
                    continue;

                orphanCount++;

                // Defensive guard (PR #175 review): a Pass 1 failCount path (e.g.
                // replacement-not-in-roster) leaves the original seated and out of
                // swappedOriginals, so Pass 2 would re-process it and potentially
                // double-place. Check directly against the active-vessel crew set
                // to short-circuit before snapshot scan.
                if (activeVesselCrewNames.Contains(originalName))
                {
                    CrewLog($"Orphan placement: '{originalName}' is still seated on the active vessel — skipping (Pass 1 left it unprocessed)");
                    skippedOriginalStillOnActiveVessel++;
                    continue;
                }

                // Resolve replacement in the roster — must exist, not Dead, and
                // not Missing (mirrors ReserveCrewIn's Missing-rescue pattern).
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
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: cannot place '{originalName}' → '{replacementName}': replacement not in roster");
                    skippedReplacementNotInRoster++;
                    continue;
                }
                if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: skipping '{originalName}' → '{replacementName}': replacement is Dead");
                    skippedDeadOrMissingReplacement++;
                    continue;
                }
                if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Missing)
                {
                    // Mirrors ReserveCrewIn (CrewReservationManager.cs:84-88): a
                    // Missing reserved kerbal is alive but orphaned from a removed
                    // vessel. Rescue them by setting back to Available before
                    // placing them.
                    //
                    // Asymmetry note: this state mutation is NOT rolled back on a
                    // later failure (snapshot miss / no matching part / etc.).
                    // Available is a valid terminal state for an unused stand-in,
                    // and Missing was the broken state to begin with — leaving the
                    // kerbal Available regardless of placement outcome is strictly
                    // an improvement, not a regression. Other skip paths leave
                    // state untouched only because they have no state to fix.
                    replacement.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    rescuedFromMissing++;
                    CrewLog($"Orphan placement: rescued Missing replacement '{replacementName}' → Available before placement");
                }
                if (activeVesselCrewNames.Contains(replacementName))
                {
                    CrewLog($"Orphan placement: skipping '{originalName}' → '{replacementName}': replacement already on active vessel");
                    swappedOriginals.Add(originalName);
                    skippedAlreadyOnActiveVessel++;
                    continue;
                }

                // Find seat in committed snapshots.
                var seat = ResolveOrphanSeatFromSnapshots(originalName, snapshots, crewReplacements);
                if (!seat.Found)
                {
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: no snapshot contains original '{originalName}' " +
                        $"— stand-in '{replacementName}' left unplaced in roster");
                    skippedSnapshotMiss++;
                    continue;
                }

                // Find a matching part on the active vessel. Two-tier match only
                // (PR #175 review): persistentId → partInfo.name. The previous
                // tier-3 "any part with free capacity" fallback was removed because
                // a misplaced stand-in (e.g. dropped into a passenger cabin instead
                // of the command pod) is arguably worse than an unplaced one and
                // would silently mask the bug it's trying to fix.
                Part target = FindTargetPartForOrphan(seat.PartPid, seat.PartName);
                if (target == null)
                {
                    ParsekLog.Warn("CrewReservation",
                        $"Orphan placement: no matching part with free seat in active vessel for " +
                        $"'{originalName}' → '{replacementName}' " +
                        $"(snapshot pid={seat.PartPid} name='{seat.PartName}') — stand-in left in roster");
                    skippedNoMatchingPart++;
                    continue;
                }

                // Use the non-indexed AddCrewmember overload — KSP picks a free seat
                // in the part. Avoids the AddCrewmemberAt-on-empty-seat behavior
                // which is unverified for our case.
                target.AddCrewmember(replacement);
                placed++;
                swappedOriginals.Add(originalName);
                // Keep the local activeVesselCrewNames set in sync so a subsequent
                // orphan that maps to the same kerbal doesn't false-collide.
                activeVesselCrewNames.Add(replacementName);
                CrewLog($"Orphan placement: '{originalName}' → '{replacement.name}' " +
                    $"placed in part '{target.partInfo.title}' " +
                    $"(snapshot pid={seat.PartPid}, live pid={target.persistentId})");
            }

            if (orphanCount > 0)
            {
                CrewLog($"Orphan placement pass: orphans={orphanCount} placed={placed} " +
                    $"rescuedFromMissing={rescuedFromMissing} " +
                    $"skippedReplacementNotInRoster={skippedReplacementNotInRoster} " +
                    $"skippedDeadOrMissingReplacement={skippedDeadOrMissingReplacement} " +
                    $"skippedAlreadyOnActiveVessel={skippedAlreadyOnActiveVessel} " +
                    $"skippedOriginalStillOnActiveVessel={skippedOriginalStillOnActiveVessel} " +
                    $"skippedSnapshotMiss={skippedSnapshotMiss} " +
                    $"skippedNoMatchingPart={skippedNoMatchingPart}");
            }

            return placed;
        }

        /// <summary>
        /// Build a set of crew names currently seated on the active vessel.
        /// O(parts × crew) once, then O(1) lookups inside the orphan loop
        /// (PR #175 review).
        /// </summary>
        private static HashSet<string> BuildActiveVesselCrewNameSet()
        {
            var set = new HashSet<string>();
            var av = FlightGlobals.ActiveVessel;
            if (av == null) return set;
            for (int p = 0; p < av.parts.Count; p++)
            {
                var crew = av.parts[p].protoModuleCrew;
                for (int c = 0; c < crew.Count; c++)
                {
                    var pcm = crew[c];
                    if (pcm != null && !string.IsNullOrEmpty(pcm.name))
                        set.Add(pcm.name);
                }
            }
            return set;
        }

        /// <summary>
        /// Walks the active vessel looking for a part to place an orphan stand-in into.
        /// Two-tier match (PR #175 review): persistentId → partInfo.name+free seat.
        /// The previous "any free seat" tier was removed because a misplaced stand-in
        /// (e.g. into a passenger cabin instead of the command pod) is arguably worse
        /// than an unplaced one and would silently mask the bug we're fixing.
        /// Returns null if no matching part has a free seat.
        /// </summary>
        private static Part FindTargetPartForOrphan(uint snapshotPartPid, string snapshotPartName)
        {
            var av = FlightGlobals.ActiveVessel;
            if (av == null) return null;

            // 1. Match by persistentId (most reliable for post-revert vessels
            //    where part PIDs are preserved from the snapshot).
            if (snapshotPartPid != 0)
            {
                for (int p = 0; p < av.parts.Count; p++)
                {
                    var part = av.parts[p];
                    if (part.persistentId == snapshotPartPid && PartHasFreeSeat(part))
                        return part;
                }
            }

            // 2. Match by partInfo.name with free capacity. Walk in order, take first.
            if (!string.IsNullOrEmpty(snapshotPartName))
            {
                for (int p = 0; p < av.parts.Count; p++)
                {
                    var part = av.parts[p];
                    if (part.partInfo != null && part.partInfo.name == snapshotPartName && PartHasFreeSeat(part))
                        return part;
                }
            }

            return null;
        }

        private static bool PartHasFreeSeat(Part part)
        {
            return part != null && part.CrewCapacity > 0 && part.protoModuleCrew.Count < part.CrewCapacity;
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
            // Bug #233: build set of PIDs spawned by committed recordings so we
            // don't delete EVA vessels that Parsek intentionally created.
            var spawnedPids = BuildSpawnedVesselPidSet(RecordingStore.CommittedRecordings);

            int evaRemoved = 0;
            int loadedKept = 0;
            int pidKept = 0;
            var allVessels = FlightGlobals.Vessels;
            for (int v = allVessels.Count - 1; v >= 0; v--)
            {
                Vessel vessel = allVessels[v];
                if (vessel == FlightGlobals.ActiveVessel) continue;
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) continue;
                if (!vessel.isEVA) continue;

                // Bug #46: don't remove loaded EVA vessels — they're actively in the
                // physics bubble (player-created or recently spawned). Only remove
                // packed/unloaded stale EVA vessels from quicksave.
                if (vessel.loaded)
                {
                    string loadedName = GetEvaCrewName(vessel);
                    if (crewReplacements.ContainsKey(loadedName ?? ""))
                    {
                        loadedKept++;
                        CrewLog($"Kept loaded EVA vessel '{loadedName}' (pid={vessel.persistentId}) — in physics bubble");
                    }
                    continue;
                }

                string evaCrewName = GetEvaCrewName(vessel);
                if (!ShouldRemoveEvaVessel(true, evaCrewName, crewReplacements,
                    vessel.persistentId, spawnedPids))
                {
                    if (crewReplacements.ContainsKey(evaCrewName ?? ""))
                        pidKept++;
                    continue;
                }

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

            if (loadedKept > 0)
                CrewLog($"Kept {loadedKept} loaded EVA vessel(s) (in physics bubble)");
            if (pidKept > 0)
                CrewLog($"Kept {pidKept} EVA vessel(s) (matched committed recording PID)");

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

            using (SuppressionGuard.Crew())
            {
                int rescued = 0;
                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (!ShouldRescueFromMissing(pcm.rosterStatus, pcm.name, crewReplacements))
                        continue;

                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    rescued++;
                    CrewLog($"Rescued Missing crew '{pcm.name}' → Available " +
                        $"(was orphaned by EVA vessel removal)");
                }

                if (rescued > 0)
                    CrewLog($"Rescued {rescued} reserved crew member(s) from Missing status");
            }
        }

        /// <summary>
        /// Rescues crew orphaned by vessel stripping during rewind/revert (#116).
        /// Any crew member with Assigned status who is not on a surviving ProtoVessel
        /// is set to Available. Dead crew are skipped. Must be called after
        /// StripOrphanedSpawnedVessels / StripFuturePrelaunchVessels.
        /// </summary>
        internal static int RescueOrphanedCrew(List<ProtoVessel> survivingVessels)
        {
            if (survivingVessels == null) return 0;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                CrewLog("RescueOrphanedCrew: no crew roster — skipping");
                return 0;
            }

            // Build set of crew names still referenced by surviving vessels
            var survivingCrew = new HashSet<string>();
            for (int i = 0; i < survivingVessels.Count; i++)
            {
                var crew = survivingVessels[i].GetVesselCrew();
                for (int j = 0; j < crew.Count; j++)
                    if (!string.IsNullOrEmpty(crew[j].name))
                        survivingCrew.Add(crew[j].name);
            }

            using (SuppressionGuard.Crew())
            {
                int rescued = 0;
                foreach (ProtoCrewMember pcm in roster.Crew)
                {
                    if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Assigned)
                        continue;
                    if (survivingCrew.Contains(pcm.name))
                        continue;

                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                    rescued++;
                    CrewLog($"Rescued orphaned crew '{pcm.name}' → Available " +
                        $"(was Assigned but no vessel references them)");
                }

                if (rescued > 0)
                    ParsekLog.Info("Crew",
                        $"Rescued {rescued} orphaned crew member(s) from vessel strip → Available");
                return rescued;
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
        /// Bug #277 — pure helper result: where in a recording snapshot was a
        /// reserved kerbal originally seated. Used by SwapReservedCrewInFlight's
        /// orphan placement pass to find a matching part on the active vessel.
        /// </summary>
        internal struct OrphanSeatLocation
        {
            public bool Found;
            public uint PartPid;     // PART node 'pid' value (matches Part.persistentId on the live vessel)
            public string PartName;  // PART node 'name' value (matches part.partInfo.name)
        }

        /// <summary>
        /// Bug #277 — pure helper: scan the supplied recording snapshots for a
        /// PART that lists <paramref name="originalName"/> in its crew values.
        /// Returns the first match (PartPid + PartName) or Found=false.
        ///
        /// Snapshot crew lists may contain stand-in names from prior recordings
        /// (see KerbalsModule.ReverseMapCrewNames). When <paramref name="reverseStandinMap"/>
        /// is supplied, a snapshot crew name that maps back to <paramref name="originalName"/>
        /// via reverse lookup is also considered a match — this catches the case
        /// where an earlier recording committed and replaced the original's name
        /// with a stand-in in subsequent snapshots.
        ///
        /// Match key is the kerbal name itself (kerbal names are unique in a
        /// roster). VesselName comparison is intentionally NOT used because two
        /// launches can share the same vessel name and would falsely match.
        /// </summary>
        internal static OrphanSeatLocation ResolveOrphanSeatFromSnapshots(
            string originalName,
            IEnumerable<ConfigNode> ghostVisualSnapshots,
            IReadOnlyDictionary<string, string> reverseStandinMap = null)
        {
            var notFound = new OrphanSeatLocation { Found = false };
            if (string.IsNullOrEmpty(originalName) || ghostVisualSnapshots == null)
                return notFound;

            foreach (var snapshot in ghostVisualSnapshots)
            {
                if (snapshot == null) continue;
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    if (partNode == null) continue;
                    string[] crewNames = partNode.GetValues("crew");
                    for (int i = 0; i < crewNames.Length; i++)
                    {
                        string crewEntry = crewNames[i];
                        if (string.IsNullOrEmpty(crewEntry)) continue;

                        bool isMatch = (crewEntry == originalName);

                        // Reverse-map: crew entry might be a stand-in for the original.
                        if (!isMatch && reverseStandinMap != null
                            && reverseStandinMap.TryGetValue(originalName, out string knownStandin)
                            && knownStandin == crewEntry)
                        {
                            isMatch = true;
                        }

                        if (!isMatch) continue;

                        uint pid = 0;
                        string pidValue = partNode.GetValue("pid");
                        if (!string.IsNullOrEmpty(pidValue))
                            uint.TryParse(pidValue, out pid);

                        string partName = partNode.GetValue("name") ?? "";

                        return new OrphanSeatLocation
                        {
                            Found = true,
                            PartPid = pid,
                            PartName = partName
                        };
                    }
                }
            }

            return notFound;
        }

        /// <summary>
        /// Pure decision: should this EVA vessel be removed during crew swap?
        /// An EVA vessel is removed if its crew member is reserved (in the replacements dict)
        /// AND the vessel was not spawned by a committed recording (bug #233).
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldRemoveEvaVessel(
            bool isEva, string crewName, IReadOnlyDictionary<string, string> replacements,
            uint vesselPid = 0, HashSet<uint> spawnedVesselPids = null)
        {
            if (!isEva || string.IsNullOrEmpty(crewName) || !replacements.ContainsKey(crewName))
                return false;

            // Bug #233: don't remove EVA vessels that Parsek spawned at recording end
            if (vesselPid != 0 && spawnedVesselPids != null && spawnedVesselPids.Contains(vesselPid))
                return false;

            return true;
        }

        /// <summary>
        /// Builds a HashSet of SpawnedVesselPersistentId values from committed recordings.
        /// Used by RemoveReservedEvaVessels to avoid deleting Parsek-spawned EVA vessels (bug #233).
        /// Extracted for testability.
        /// </summary>
        internal static HashSet<uint> BuildSpawnedVesselPidSet(List<Recording> recordings)
        {
            var pids = new HashSet<uint>();
            if (recordings == null) return pids;
            for (int i = 0; i < recordings.Count; i++)
            {
                uint pid = recordings[i].SpawnedVesselPersistentId;
                if (pid != 0)
                    pids.Add(pid);
            }
            return pids;
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
                && replacements != null
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

        /// <summary>
        /// Pure method: swaps reserved crew names in a vessel snapshot ConfigNode,
        /// replacing each reserved original name with its replacement name.
        /// Used for KSC spawns where SwapReservedCrewInFlight cannot run
        /// (no loaded vessel / no flight scene). Bug #167.
        /// Returns the number of crew names swapped.
        /// </summary>
        internal static int SwapReservedCrewInSnapshot(
            ConfigNode snapshot, IReadOnlyDictionary<string, string> replacements)
        {
            if (snapshot == null || replacements == null || replacements.Count == 0)
                return 0;

            int swapCount = 0;
            int partIndex = 0;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                string[] crewNames = partNode.GetValues("crew");
                if (crewNames.Length == 0) { partIndex++; continue; }

                bool anySwapped = false;
                var updated = new List<string>(crewNames.Length);

                for (int i = 0; i < crewNames.Length; i++)
                {
                    if (replacements.TryGetValue(crewNames[i], out string replacementName))
                    {
                        ParsekLog.Verbose("CrewReservation",
                            $"Snapshot swap: '{crewNames[i]}' -> '{replacementName}' in PART[{partIndex}]");
                        updated.Add(replacementName);
                        anySwapped = true;
                        swapCount++;
                    }
                    else
                    {
                        updated.Add(crewNames[i]);
                    }
                }

                if (anySwapped)
                {
                    partNode.RemoveValues("crew");
                    for (int i = 0; i < updated.Count; i++)
                        partNode.AddValue("crew", updated[i]);
                }

                partIndex++;
            }

            if (swapCount > 0)
                ParsekLog.Verbose("CrewReservation",
                    $"Snapshot crew swap complete: {swapCount} name(s) replaced across {partIndex} part(s)");

            return swapCount;
        }

        #endregion

        #region Bridge Methods (KerbalsModule)

        /// <summary>
        /// Set a crew replacement mapping. Called by KerbalsModule.ApplyToRoster
        /// to bridge derived state to SwapReservedCrewInFlight.
        /// </summary>
        internal static void SetReplacement(string originalName, string replacementName)
        {
            crewReplacements[originalName] = replacementName;
        }

        /// <summary>
        /// Clear all replacements without roster access. For KerbalsModule use.
        /// </summary>
        internal static void ClearReplacementsInternal()
        {
            crewReplacements.Clear();
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
