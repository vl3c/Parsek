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
                recNode.AddValue("vesselName", rec.VesselName);
                recNode.AddValue("pointCount", rec.Points.Count);

                for (int i = 0; i < rec.Points.Count; i++)
                {
                    var pt = rec.Points[i];
                    ConfigNode ptNode = recNode.AddNode("POINT");
                    ptNode.AddValue("ut", pt.ut.ToString("R"));
                    ptNode.AddValue("lat", pt.latitude.ToString("R"));
                    ptNode.AddValue("lon", pt.longitude.ToString("R"));
                    ptNode.AddValue("alt", pt.altitude.ToString("R"));
                    ptNode.AddValue("rotX", pt.rotation.x.ToString("R"));
                    ptNode.AddValue("rotY", pt.rotation.y.ToString("R"));
                    ptNode.AddValue("rotZ", pt.rotation.z.ToString("R"));
                    ptNode.AddValue("rotW", pt.rotation.w.ToString("R"));
                    ptNode.AddValue("body", pt.bodyName);
                    ptNode.AddValue("funds", pt.funds.ToString("R"));
                    ptNode.AddValue("science", pt.science.ToString("R"));
                    ptNode.AddValue("rep", pt.reputation.ToString("R"));
                }

                // Persist orbit segments
                var ic = CultureInfo.InvariantCulture;
                for (int s = 0; s < rec.OrbitSegments.Count; s++)
                {
                    var seg = rec.OrbitSegments[s];
                    ConfigNode segNode = recNode.AddNode("ORBIT_SEGMENT");
                    segNode.AddValue("startUT", seg.startUT.ToString("R", ic));
                    segNode.AddValue("endUT", seg.endUT.ToString("R", ic));
                    segNode.AddValue("inc", seg.inclination.ToString("R", ic));
                    segNode.AddValue("ecc", seg.eccentricity.ToString("R", ic));
                    segNode.AddValue("sma", seg.semiMajorAxis.ToString("R", ic));
                    segNode.AddValue("lan", seg.longitudeOfAscendingNode.ToString("R", ic));
                    segNode.AddValue("argPe", seg.argumentOfPeriapsis.ToString("R", ic));
                    segNode.AddValue("mna", seg.meanAnomalyAtEpoch.ToString("R", ic));
                    segNode.AddValue("epoch", seg.epoch.ToString("R", ic));
                    segNode.AddValue("body", seg.bodyName);
                }

                // Persist vessel snapshot if present
                if (rec.VesselSnapshot != null)
                {
                    recNode.AddNode("VESSEL_SNAPSHOT", rec.VesselSnapshot);
                }

                // Persist spawned vessel pid so we can detect duplicates after scene changes
                if (rec.SpawnedVesselPersistentId != 0)
                {
                    recNode.AddValue("spawnedPid", rec.SpawnedVesselPersistentId);
                }

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

                ConfigNode[] ptNodes = recNode.GetNodes("POINT");
                for (int i = 0; i < ptNodes.Length; i++)
                {
                    var ptNode = ptNodes[i];
                    var pt = new ParsekSpike.TrajectoryPoint();

                    var inv = NumberStyles.Float;
                    var ic = CultureInfo.InvariantCulture;

                    double.TryParse(ptNode.GetValue("ut"), inv, ic, out pt.ut);
                    double.TryParse(ptNode.GetValue("lat"), inv, ic, out pt.latitude);
                    double.TryParse(ptNode.GetValue("lon"), inv, ic, out pt.longitude);
                    double.TryParse(ptNode.GetValue("alt"), inv, ic, out pt.altitude);

                    float rx, ry, rz, rw;
                    float.TryParse(ptNode.GetValue("rotX"), inv, ic, out rx);
                    float.TryParse(ptNode.GetValue("rotY"), inv, ic, out ry);
                    float.TryParse(ptNode.GetValue("rotZ"), inv, ic, out rz);
                    float.TryParse(ptNode.GetValue("rotW"), inv, ic, out rw);
                    pt.rotation = new Quaternion(rx, ry, rz, rw);

                    pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

                    double funds;
                    double.TryParse(ptNode.GetValue("funds"), inv, ic, out funds);
                    pt.funds = funds;

                    float science, rep;
                    float.TryParse(ptNode.GetValue("science"), inv, ic, out science);
                    float.TryParse(ptNode.GetValue("rep"), inv, ic, out rep);
                    pt.science = science;
                    pt.reputation = rep;

                    rec.Points.Add(pt);
                }

                // Restore orbit segments
                ConfigNode[] segNodes = recNode.GetNodes("ORBIT_SEGMENT");
                for (int s = 0; s < segNodes.Length; s++)
                {
                    var segNode = segNodes[s];
                    var seg = new ParsekSpike.OrbitSegment();
                    var inv = NumberStyles.Float;
                    var ic = CultureInfo.InvariantCulture;

                    double.TryParse(segNode.GetValue("startUT"), inv, ic, out seg.startUT);
                    double.TryParse(segNode.GetValue("endUT"), inv, ic, out seg.endUT);
                    double.TryParse(segNode.GetValue("inc"), inv, ic, out seg.inclination);
                    double.TryParse(segNode.GetValue("ecc"), inv, ic, out seg.eccentricity);
                    double.TryParse(segNode.GetValue("sma"), inv, ic, out seg.semiMajorAxis);
                    double.TryParse(segNode.GetValue("lan"), inv, ic, out seg.longitudeOfAscendingNode);
                    double.TryParse(segNode.GetValue("argPe"), inv, ic, out seg.argumentOfPeriapsis);
                    double.TryParse(segNode.GetValue("mna"), inv, ic, out seg.meanAnomalyAtEpoch);
                    double.TryParse(segNode.GetValue("epoch"), inv, ic, out seg.epoch);
                    seg.bodyName = segNode.GetValue("body") ?? "Kerbin";

                    rec.OrbitSegments.Add(seg);
                }

                // Restore vessel snapshot if saved
                ConfigNode snapshotNode = recNode.GetNode("VESSEL_SNAPSHOT");
                if (snapshotNode != null)
                {
                    rec.VesselSnapshot = snapshotNode;
                }

                // Restore spawned vessel pid for duplicate spawn detection
                string pidStr = recNode.GetValue("spawnedPid");
                if (pidStr != null)
                {
                    uint pid;
                    if (uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                        rec.SpawnedVesselPersistentId = pid;
                }

                // Restore resource application index
                string resIdxStr = recNode.GetValue("lastResIdx");
                if (resIdxStr != null)
                {
                    int resIdx;
                    if (int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx))
                        rec.LastAppliedResourceIndex = resIdx;
                }

                if (rec.Points.Count > 0)
                {
                    recordings.Add(rec);
                    Debug.Log($"[Parsek Scenario] Loaded recording: {rec.VesselName}, " +
                        $"{rec.Points.Count} points, {rec.OrbitSegments.Count} orbit segments, " +
                        $"UT {rec.StartUT:F0}-{rec.EndUT:F0}" +
                        (rec.VesselSnapshot != null ? " (has vessel snapshot)" : ""));
                }
            }

            ReserveSnapshotCrew();

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
            // Merge dialog can only show in Flight (ParsekSpike is Flight-only).
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
                        if (pcm.name == name && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available)
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                            Debug.Log($"[Parsek Scenario] Reserved crew '{name}' for deferred vessel spawn");

                            // Hire a replacement kerbal so the available pool stays constant
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
                    part.RemoveCrewmember(original);
                    part.AddCrewmemberAt(replacement, seatIndex);
                    Debug.Log($"[Parsek Scenario] Swapped '{original.name}' → '{replacement.name}' in part '{part.partInfo.title}'");
                }
            }
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
