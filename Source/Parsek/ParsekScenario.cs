using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// ScenarioModule that persists committed recordings to save games.
    /// Handles OnSave/OnLoad to serialize trajectory data into ConfigNodes.
    /// Also manages crew reservation for deferred vessel spawns.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public class ParsekScenario : ScenarioModule
    {
        #region Crew Replacements

        // Maps reserved kerbal name → replacement kerbal name
        private static Dictionary<string, string> crewReplacements = new Dictionary<string, string>();

        /// <summary>
        /// Read-only access to current replacement mappings. For testing/diagnostics.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> CrewReplacements => crewReplacements;

        #endregion

        public override void OnSave(ConfigNode node)
        {
            // Clear any existing recording nodes
            node.RemoveNodes("RECORDING");

            var recordings = RecordingStore.CommittedRecordings;
            Debug.Log($"[Parsek Scenario] Saving {recordings.Count} committed recordings");

            for (int r = 0; r < recordings.Count; r++)
            {
                var rec = recordings[r];
                ConfigNode recNode = node.AddNode("RECORDING");

                // Write bulk data to external files
                rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
                if (!RecordingStore.SaveRecordingFiles(rec))
                    Debug.Log($"[Parsek Scenario] WARNING: File write failed for '{rec.VesselName}'");

                SaveRecordingMetadata(recNode, rec);
                recNode.AddValue("vesselName", rec.VesselName);
                recNode.AddValue("pointCount", rec.Points.Count);

                // Persist EVA child recording linkage
                if (!string.IsNullOrEmpty(rec.ParentRecordingId))
                    recNode.AddValue("parentRecordingId", rec.ParentRecordingId);
                if (!string.IsNullOrEmpty(rec.EvaCrewName))
                    recNode.AddValue("evaCrewName", rec.EvaCrewName);

                // Persist spawned vessel pid so we can detect duplicates after scene changes
                if (rec.SpawnedVesselPersistentId != 0)
                    recNode.AddValue("spawnedPid", rec.SpawnedVesselPersistentId);

                if (rec.VesselDestroyed)
                    recNode.AddValue("vesselDestroyed", rec.VesselDestroyed.ToString());

                if (rec.TakenControl)
                    recNode.AddValue("takenControl", rec.TakenControl.ToString());

                // Persist resource index so quickload doesn't re-apply deltas
                recNode.AddValue("lastResIdx", rec.LastAppliedResourceIndex);
            }

            // Persist crew replacement mappings
            if (crewReplacements.Count > 0)
            {
                ConfigNode replacementsNode = node.AddNode("CREW_REPLACEMENTS");
                foreach (var kvp in crewReplacements)
                {
                    ConfigNode entry = replacementsNode.AddNode("ENTRY");
                    entry.AddValue("original", kvp.Key);
                    entry.AddValue("replacement", kvp.Value);
                }
                Debug.Log($"[Parsek Scenario] Saved {crewReplacements.Count} crew replacement(s)");
            }
        }

        // Static flag: only load from save once per KSP session.
        // On revert, the launch quicksave has stale data — the in-memory
        // static list is the real source of truth within a session.
        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;

        public override void OnLoad(ConfigNode node)
        {
            var recordings = RecordingStore.CommittedRecordings;

            // Detect loading a different save game (not a revert)
            string currentSave = HighLogic.SaveFolder;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                lastSaveFolder = currentSave;
                Debug.Log($"[Parsek Scenario] Save folder changed to '{currentSave}' — resetting session state");
            }

            // Load crew replacement mappings from the node (both initial and revert paths need this)
            LoadCrewReplacements(node);

            if (initialLoadDone)
            {
                // Reset spawn state; restore resource index from save so quickload
                // doesn't re-apply deltas that were already applied before the save.
                // On revert, the launch quicksave has lastResIdx=-1, which is correct.
                ConfigNode[] savedRecNodes = node.GetNodes("RECORDING");
                for (int i = 0; i < recordings.Count; i++)
                {
                    recordings[i].VesselSpawned = false;
                    recordings[i].TakenControl = false;
                    recordings[i].SpawnedVesselPersistentId = 0;
                    recordings[i].SpawnAttempts = 0;
                    int resIdx = -1;
                    if (i < savedRecNodes.Length)
                    {
                        string resIdxStr = savedRecNodes[i].GetValue("lastResIdx");
                        if (resIdxStr != null)
                            int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx);
                    }
                    recordings[i].LastAppliedResourceIndex = resIdx;
                }

                ReserveSnapshotCrew();
                Debug.Log($"[Parsek Scenario] Revert detected — preserving {recordings.Count} session recordings");
                return;
            }

            initialLoadDone = true;
            recordings.Clear();

            ConfigNode[] recNodes = node.GetNodes("RECORDING");
            Debug.Log($"[Parsek Scenario] Loading {recNodes.Length} committed recordings");

            for (int r = 0; r < recNodes.Length; r++)
            {
                var recNode = recNodes[r];
                var rec = new RecordingStore.Recording
                {
                    VesselName = recNode.GetValue("vesselName") ?? "Unknown"
                };
                LoadRecordingMetadata(recNode, rec);

                // Load bulk data from external files
                RecordingStore.LoadRecordingFiles(rec);

                // Restore EVA child recording linkage
                rec.ParentRecordingId = recNode.GetValue("parentRecordingId");
                rec.EvaCrewName = recNode.GetValue("evaCrewName");

                // Restore spawned vessel pid for duplicate spawn detection
                string pidStr = recNode.GetValue("spawnedPid");
                if (pidStr != null)
                {
                    uint pid;
                    if (uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                        rec.SpawnedVesselPersistentId = pid;
                }

                // Restore vessel destroyed flag
                string destroyedStr = recNode.GetValue("vesselDestroyed");
                if (destroyedStr != null)
                {
                    bool destroyed;
                    if (bool.TryParse(destroyedStr, out destroyed))
                        rec.VesselDestroyed = destroyed;
                }

                // Restore taken control flag
                string takenStr = recNode.GetValue("takenControl");
                if (takenStr != null)
                {
                    bool taken;
                    if (bool.TryParse(takenStr, out taken))
                        rec.TakenControl = taken;
                }

                // Restore resource application index
                string resIdxStr = recNode.GetValue("lastResIdx");
                if (resIdxStr != null)
                {
                    int resIdx;
                    if (int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx))
                        rec.LastAppliedResourceIndex = resIdx;
                }

                // Always add — even degraded recordings (missing .prec → 0 points)
                // must occupy their slot to preserve index-based revert mapping.
                recordings.Add(rec);
                Debug.Log($"[Parsek Scenario] Loaded recording: {rec.VesselName}, " +
                    $"{rec.Points.Count} points, {rec.OrbitSegments.Count} orbit segments" +
                    (rec.Points.Count > 0 ? $", UT {rec.StartUT:F0}-{rec.EndUT:F0}" : ", degraded (0 points)") +
                    ((rec.GhostVisualSnapshot != null || rec.VesselSnapshot != null) ? " (has vessel snapshot)" : "") +
                    (!string.IsNullOrEmpty(rec.GhostGeometryRelativePath)
                        ? $" (ghost geometry: {(rec.GhostGeometryAvailable ? "ready" : "fallback")})"
                        : ""));
            }

            ReserveSnapshotCrew();

            // Diagnostic summary of loaded recordings with UT context
            double loadUT = Planetarium.GetUniversalTime();
            ParsekLog.Log($"Scenario load summary — UT: {loadUT:F0}, {recordings.Count} recording(s)");
            for (int i = 0; i < recordings.Count; i++)
            {
                var loadedRec = recordings[i];
                double duration = loadedRec.EndUT - loadedRec.StartUT;
                string status;
                if (loadUT < loadedRec.StartUT)
                    status = $"future (starts in {loadedRec.StartUT - loadUT:F0}s)";
                else if (loadUT <= loadedRec.EndUT && duration > 0)
                    status = $"IN PROGRESS ({(loadUT - loadedRec.StartUT) / duration * 100:F0}%)";
                else if (loadUT <= loadedRec.EndUT)
                    status = "IN PROGRESS";
                else
                    status = "past";
                ParsekLog.Log($"  #{i}: \"{loadedRec.VesselName}\" — {status}");
            }

            if (crewReplacements.Count > 0)
            {
                ParsekLog.Log($"Crew reservations active ({crewReplacements.Count}):");
                foreach (var kvp in crewReplacements)
                    ParsekLog.Log($"  {kvp.Key} -> replacement: {kvp.Value}");
            }

            // Auto-unreserve crew for recordings whose EndUT has already passed
            // but vessel was never spawned (e.g. UT advanced while in Space Center).
            double currentUT = Planetarium.GetUniversalTime();
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.VesselSnapshot != null && !rec.VesselSpawned && currentUT > rec.EndUT)
                {
                    UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    rec.VesselSnapshot = null;
                    rec.VesselSpawned = true;
                    Debug.Log($"[Parsek Scenario] Auto-unreserved crew for recording #{i} " +
                        $"({rec.VesselName}) — EndUT passed without spawn");
                }
            }

            // If pending recording exists but we're not in Flight, auto-commit it.
            // Merge dialog can only show in Flight (ParsekFlight is Flight-only).
            // This handles Esc > Abort Mission → Space Center path.
            if (RecordingStore.HasPending && HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                var pending = RecordingStore.Pending;
                if (pending.VesselSnapshot != null)
                    UnreserveCrewInSnapshot(pending.VesselSnapshot);
                pending.VesselSnapshot = null;
                RecordingStore.CommitPending();
                Debug.Log($"[Parsek Scenario] Auto-committed pending recording outside Flight " +
                    $"(scene: {HighLogic.LoadedScene})");
            }
        }

        #region Crew Reservation

        /// <summary>
        /// Saves versioned recording metadata and ghost-geometry metadata.
        /// Extracted for testability.
        /// </summary>
        internal static void SaveRecordingMetadata(ConfigNode recNode, RecordingStore.Recording rec)
        {
            recNode.AddValue("recordingId", rec.RecordingId ?? "");
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            recNode.AddValue("ghostGeometryVersion", rec.GhostGeometryVersion);
            recNode.AddValue("ghostGeometryStrategy", rec.GhostGeometryCaptureStrategy ?? "stub_v1");
            recNode.AddValue("ghostGeometryProbeStatus", rec.GhostGeometryProbeStatus ?? "unknown");
            if (!string.IsNullOrEmpty(rec.GhostGeometryRelativePath))
                recNode.AddValue("ghostGeometryPath", rec.GhostGeometryRelativePath);
            recNode.AddValue("ghostGeometryAvailable", rec.GhostGeometryAvailable);
            if (!string.IsNullOrEmpty(rec.GhostGeometryCaptureError))
                recNode.AddValue("ghostGeometryError", rec.GhostGeometryCaptureError);
        }

        /// <summary>
        /// Loads versioned recording metadata and ghost-geometry metadata.
        /// Missing fields are treated as old-format recordings.
        /// Extracted for testability.
        /// </summary>
        internal static void LoadRecordingMetadata(ConfigNode recNode, RecordingStore.Recording rec)
        {
            string id = recNode.GetValue("recordingId");
            if (!string.IsNullOrEmpty(id))
                rec.RecordingId = id;

            string formatVersionStr = recNode.GetValue("recordingFormatVersion");
            if (formatVersionStr != null)
            {
                int formatVersion;
                if (int.TryParse(formatVersionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out formatVersion))
                    rec.RecordingFormatVersion = formatVersion;
            }

            string geomVersionStr = recNode.GetValue("ghostGeometryVersion");
            if (geomVersionStr != null)
            {
                int geomVersion;
                if (int.TryParse(geomVersionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out geomVersion))
                    rec.GhostGeometryVersion = geomVersion;
            }

            rec.GhostGeometryRelativePath = recNode.GetValue("ghostGeometryPath");
            string strategy = recNode.GetValue("ghostGeometryStrategy");
            if (!string.IsNullOrEmpty(strategy))
                rec.GhostGeometryCaptureStrategy = strategy;
            string probeStatus = recNode.GetValue("ghostGeometryProbeStatus");
            if (!string.IsNullOrEmpty(probeStatus))
                rec.GhostGeometryProbeStatus = probeStatus;
            string geomAvailableStr = recNode.GetValue("ghostGeometryAvailable");
            if (geomAvailableStr != null)
            {
                bool geomAvailable;
                if (bool.TryParse(geomAvailableStr, out geomAvailable))
                    rec.GhostGeometryAvailable = geomAvailable;
            }
            rec.GhostGeometryCaptureError = recNode.GetValue("ghostGeometryError");
        }

        /// <summary>
        /// Mark crew from all unspawned vessel snapshots as Assigned so they
        /// can't be placed on new craft in the VAB/SPH.
        /// </summary>
        public static void ReserveSnapshotCrew()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            foreach (var rec in RecordingStore.CommittedRecordings)
                ReserveCrewIn(rec.VesselSnapshot, rec.VesselSpawned, roster);

            if (RecordingStore.HasPending && RecordingStore.Pending.VesselSnapshot != null)
                ReserveCrewIn(RecordingStore.Pending.VesselSnapshot, false, roster);
        }

        /// <summary>
        /// Set crew in a specific snapshot back to Available.
        /// Call when discarding, recovering, or wiping recordings.
        /// </summary>
        public static void UnreserveCrewInSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == name && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned)
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                            Debug.Log($"[Parsek Scenario] Unreserved crew '{name}'");

                            // Clean up the replacement kerbal
                            CleanUpReplacement(name, roster);

                            break;
                        }
                    }
                }
            }
        }

        private static void ReserveCrewIn(ConfigNode snapshot, bool alreadySpawned, KerbalRoster roster)
        {
            if (snapshot == null || alreadySpawned) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name != name) continue;

                        // Skip dead/missing crew — they'll be stripped at spawn time
                        if (!ShouldProcessCrewForReservation(pcm.rosterStatus))
                            break;

                        // Mark as Assigned if Available
                        if (NeedsStatusChange(pcm.rosterStatus))
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                            Debug.Log($"[Parsek Scenario] Reserved crew '{name}' for deferred vessel spawn");
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
                                    Debug.Log($"[Parsek Scenario] Hired replacement '{replacement.name}' " +
                                        $"(trait: {pcm.experienceTrait.TypeName}) for reserved '{name}'");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.Log($"[Parsek Scenario] Failed to hire replacement for '{name}': {ex.Message}");
                            }
                        }

                        break;
                    }
                }
            }
        }

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
                Debug.Log($"[Parsek Scenario] Replacement '{replacementName}' not found in roster (already removed?)");
                return;
            }

            if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Available)
            {
                roster.Remove(replacement);
                Debug.Log($"[Parsek Scenario] Removed replacement '{replacementName}' (was unused)");
            }
            else
            {
                Debug.Log($"[Parsek Scenario] Kept replacement '{replacementName}' " +
                    $"(status: {replacement.rosterStatus} — now a real kerbal)");
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

            foreach (var kvp in new Dictionary<string, string>(crewReplacements))
            {
                CleanUpReplacement(kvp.Key, roster);
            }

            crewReplacements.Clear();
            Debug.Log("[Parsek Scenario] Cleared all crew replacements");
        }

        /// <summary>
        /// Load crew replacement mappings from a ConfigNode.
        /// </summary>
        private static void LoadCrewReplacements(ConfigNode node)
        {
            crewReplacements.Clear();

            ConfigNode replacementsNode = node.GetNode("CREW_REPLACEMENTS");
            if (replacementsNode == null) return;

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

            Debug.Log($"[Parsek Scenario] Loaded {crewReplacements.Count} crew replacement(s)");
        }

        /// <summary>
        /// Swap reserved crew out of the active flight vessel, replacing them
        /// with their hired replacements. Prevents the player from recording
        /// with a reserved kerbal again after revert.
        /// </summary>
        public static void SwapReservedCrewInFlight()
        {
            if (FlightGlobals.ActiveVessel == null) return;
            if (crewReplacements.Count == 0) return;

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            bool anySwapped = false;

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
                        Debug.Log($"[Parsek Scenario] Cannot swap '{original.name}': replacement '{replacementName}' not in roster");
                        continue;
                    }

                    int seatIndex = part.protoModuleCrew.IndexOf(original);
                    if (seatIndex < 0)
                    {
                        Debug.Log($"[Parsek Scenario] Cannot swap '{original.name}': not found in part crew list");
                        continue;
                    }
                    part.RemoveCrewmember(original);
                    part.AddCrewmemberAt(replacement, seatIndex);
                    anySwapped = true;
                    Debug.Log($"[Parsek Scenario] Swapped '{original.name}' → '{replacement.name}' in part '{part.partInfo.title}'");
                }
            }

            if (anySwapped)
            {
                FlightGlobals.ActiveVessel.SpawnCrew();
                GameEvents.onVesselCrewWasModified.Fire(FlightGlobals.ActiveVessel);
                Debug.Log("[Parsek Scenario] Crew swap complete — refreshed vessel crew display");
            }
        }

        /// <summary>
        /// Returns true if a crew member with the given roster status should be
        /// processed for reservation (i.e. not dead/missing). Extracted for testability.
        /// </summary>
        internal static bool ShouldProcessCrewForReservation(ProtoCrewMember.RosterStatus status)
        {
            return status != ProtoCrewMember.RosterStatus.Dead &&
                   status != ProtoCrewMember.RosterStatus.Missing;
        }

        /// <summary>
        /// Returns true if a crew member with the given roster status needs to be
        /// marked as Assigned. Already-Assigned crew (e.g. on the pad vessel after
        /// revert) still need replacements but don't need a status change.
        /// </summary>
        internal static bool NeedsStatusChange(ProtoCrewMember.RosterStatus status)
        {
            return status == ProtoCrewMember.RosterStatus.Available;
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
        /// Clears replacement dictionary without roster access. For unit tests only.
        /// </summary>
        internal static void ResetReplacementsForTesting()
        {
            crewReplacements.Clear();
        }

        #endregion
    }
}
