using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    internal static partial class CrewReservationManager
    {
        #region Testing & Serialization

        /// <summary>
        /// Clears replacement dictionary without roster access. For unit tests only.
        /// Also clears the #615 rescue-placed marker set so test fixtures see
        /// a clean rescue signal between cases.
        /// </summary>
        internal static void ResetReplacementsForTesting()
        {
            crewReplacements.Clear();
            rescuePlacedKerbals.Clear();
            SaveVesselPidExistsOverrideForTesting = null;
        }

        /// <summary>
        /// Seeds the crew replacement dictionary directly so a test can
        /// simulate the post-reserve state without driving a real
        /// <see cref="HighLogic.CurrentGame"/>. Used by the P1-review-second-pass
        /// regression that exercises the
        /// Rescue → Unreserve → ApplyToRoster sequence end-to-end.
        /// </summary>
        internal static void SeedReplacementForTesting(string originalName, string replacementName)
        {
            if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(replacementName))
                return;
            crewReplacements[originalName] = replacementName;
        }

        /// <summary>
        /// Test seam mirroring the dictionary-management half of
        /// <see cref="CleanUpReplacement"/> (the only half that touches the
        /// rescue-placed marker contract). The full
        /// <see cref="UnreserveCrewInSnapshot"/> path requires a live KSP
        /// <see cref="HighLogic.CurrentGame"/> + <see cref="KerbalRoster"/>,
        /// which xUnit cannot stand up. This seam asserts the production
        /// invariant (P1 review third pass) that the per-name unreserve does
        /// NOT clear the rescue-placed marker, and the marker stays set so
        /// every subsequent <see cref="KerbalsModule.ApplyToRoster"/> walk
        /// observes it — exercised by
        /// <see cref="RescueCompletionGuardTests"/>.
        /// </summary>
        internal static void CleanUpReplacementForTesting(string originalName)
        {
            if (string.IsNullOrEmpty(originalName)) return;
            // Runs the PRODUCTION dictionary path of CleanUpReplacement, not a copy
            // of it: remove the entry, do NOT touch the rescue-placed marker. The
            // roster-touching half is intentionally omitted because it needs a live
            // KerbalRoster and has no effect on the marker contract.
            TryRemoveReplacementMappingPreservingRescueMarker(originalName, out _);
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// returns a shallow copy of the replacement dictionary so the bundle
        /// can preserve it across a quicksave load. Keys are the reserved
        /// kerbal names; values are their stand-in replacements.
        /// </summary>
        internal static Dictionary<string, string> SnapshotReplacements()
        {
            return new Dictionary<string, string>(crewReplacements);
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// re-applies a previously captured replacement dictionary after the
        /// quicksave load has replaced the live in-memory state. The method
        /// replaces — not merges — the current map so restoring after an
        /// in-memory swap does not duplicate entries.
        /// </summary>
        internal static void RestoreReplacements(IReadOnlyDictionary<string, string> replacements)
        {
            crewReplacements.Clear();
            // #615 P1 review: rewind-quickload reconciliation rewinds time —
            // the in-memory rescue-placed markers from after the quicksave's
            // captured UT no longer apply. The replacement dict is being
            // re-seeded from the bundle; rescue markers should restart empty
            // and re-populate as the post-load spawn pipeline runs.
            rescuePlacedKerbals.Clear();
            if (replacements == null) return;
            foreach (var kv in replacements)
            {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    crewReplacements[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Load crew replacement mappings from a ConfigNode.
        /// </summary>
        internal static void LoadCrewReplacements(ConfigNode node)
        {
            crewReplacements.Clear();
            // #615: the rescue-placed markers are the save's own (RESCUE_PLACED_KERBALS);
            // entries left in memory from before the load are stale and dropped first.
            // The save already left out markers whose vessel was gone when it was written.
            rescuePlacedKerbals.Clear();
            LoadRescuePlacedMarkers(node);

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

        internal const string RescuePlacedNodeName = "RESCUE_PLACED_KERBALS";

        /// <summary>
        /// Reads the #615 rescue-placed markers a save carries. Without them the first
        /// ApplyToRoster walk after a load would take the recreate path and regenerate the
        /// stand-in of a kerbal the rescue already placed (the bug the marker exists for).
        /// </summary>
        private static void LoadRescuePlacedMarkers(ConfigNode node)
        {
            ConfigNode markersNode = node?.GetNode(RescuePlacedNodeName);
            if (markersNode == null)
                return;
            int loaded = 0, skipped = 0;
            foreach (ConfigNode entry in markersNode.GetNodes("ENTRY"))
            {
                string name = entry.GetValue("name");
                if (!string.IsNullOrEmpty(name)
                    && ulong.TryParse(entry.GetValue("vesselPid"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out ulong pid))
                {
                    // markUT is optional: absent (or unparsable) reads NaN, a marker the
                    // new-reservation retirement never touches.
                    double markUT = double.NaN;
                    string utText = entry.GetValue(RescueMarkUTKey);
                    if (!string.IsNullOrEmpty(utText)
                        && !double.TryParse(utText, NumberStyles.Float, CultureInfo.InvariantCulture, out markUT))
                        markUT = double.NaN;
                    rescuePlacedKerbals[name] = new RescuePlacedMarker(pid, markUT);
                    loaded++;
                }
                else
                {
                    skipped++;
                }
            }
            CrewLog($"Loaded {loaded} rescue-placed marker(s), skipped {skipped} malformed");
        }

        internal const string RescueMarkUTKey = "markUT";

        /// <summary>
        /// Test seam for the save-time "does this vessel pid exist" answer. Null uses
        /// the live vessel lists (<see cref="ResolveSaveVesselPidExists"/>).
        /// </summary>
        internal static Func<ulong, bool> SaveVesselPidExistsOverrideForTesting;

        /// <summary>
        /// Picks the rescue-placed markers worth writing to a save
        /// (RESCUE-PLACED-MARKER-NEVER-RETIRES): a marker whose vessel pid matches no
        /// vessel can never fire the guard again, so it is left out. A null
        /// <paramref name="vesselPidExists"/> means no reliable vessel source exists
        /// right now, and every marker is kept. Pure.
        /// </summary>
        internal static List<KeyValuePair<string, RescuePlacedMarker>> CollectSavableRescueMarkers(
            IEnumerable<KeyValuePair<string, RescuePlacedMarker>> markers,
            Func<ulong, bool> vesselPidExists,
            out int pruned)
        {
            pruned = 0;
            var kept = new List<KeyValuePair<string, RescuePlacedMarker>>();
            if (markers == null) return kept;
            foreach (var kvp in markers)
            {
                if (vesselPidExists != null && !vesselPidExists(kvp.Value.VesselPid))
                {
                    pruned++;
                    continue;
                }
                kept.Add(kvp);
            }
            return kept;
        }

        /// <summary>
        /// True for the scenes whose vessel lists describe every vessel of the game:
        /// FLIGHT, SPACECENTER and TRACKSTATION hold the vessels in
        /// <c>FlightGlobals.Vessels</c>, and every one of them plus EDITOR carries the
        /// game's <c>flightState.protoVessels</c>. Other scenes (main menu, loading)
        /// have no game universe to ask. Pure.
        /// </summary>
        internal static bool SceneHasReliableVesselLists(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT
                || scene == GameScenes.SPACECENTER
                || scene == GameScenes.TRACKSTATION
                || scene == GameScenes.EDITOR;
        }

        /// <summary>
        /// The save-time vessel-exists predicate: the union of
        /// <c>FlightGlobals.Vessels</c> and <c>HighLogic.CurrentGame.flightState.protoVessels</c>
        /// pids, so a vessel that only one list knows (unloaded, packed, a fresh split
        /// not yet in the flight state) still counts. Returns null with a reason when no
        /// reliable source exists: the scene has no game universe, neither list could be
        /// read, or both lists are empty (an empty universe and unpopulated lists look
        /// the same, so nothing is pruned).
        /// </summary>
        private static Func<ulong, bool> ResolveSaveVesselPidExists(out string skipReason)
        {
            skipReason = null;
            if (SaveVesselPidExistsOverrideForTesting != null)
                return SaveVesselPidExistsOverrideForTesting;

            GameScenes scene;
            try { scene = ReadLoadedSceneCore(); }
            catch (Exception ex)
            {
                skipReason = $"scene unreadable ({ex.GetType().Name})";
                return null;
            }
            if (!SceneHasReliableVesselLists(scene))
            {
                skipReason = $"scene {scene} has no reliable vessel list";
                return null;
            }

            var pids = new HashSet<ulong>();
            bool anySource = false;
            try
            {
                if (AddFlightGlobalsVesselPidsCore(pids)) anySource = true;
            }
            catch (Exception) { /* FlightGlobals not up in this scene; the flight state still answers */ }
            try
            {
                if (AddProtoVesselPidsCore(pids)) anySource = true;
            }
            catch (Exception) { /* no game flight state; FlightGlobals may still answer */ }

            if (!anySource)
            {
                skipReason = $"scene {scene}: neither vessel list readable";
                return null;
            }
            if (pids.Count == 0)
            {
                skipReason = $"scene {scene}: both vessel lists empty";
                return null;
            }
            return pid => pids.Contains(pid);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GameScenes ReadLoadedSceneCore()
        {
            return HighLogic.LoadedScene;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AddFlightGlobalsVesselPidsCore(HashSet<ulong> pids)
        {
            var vessels = FlightGlobals.Vessels;
            if (vessels == null) return false;
            for (int i = 0; i < vessels.Count; i++)
            {
                var v = vessels[i];
                if (v != null && v.persistentId != 0u) pids.Add(v.persistentId);
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AddProtoVesselPidsCore(HashSet<ulong> pids)
        {
            var protoVessels = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protoVessels == null) return false;
            for (int i = 0; i < protoVessels.Count; i++)
            {
                var pv = protoVessels[i];
                if (pv != null && pv.persistentId != 0u) pids.Add(pv.persistentId);
            }
            return true;
        }

        internal static void SaveCrewReplacements(ConfigNode node)
        {
            if (rescuePlacedKerbals.Count > 0)
            {
                string skipReason;
                Func<ulong, bool> pidExists = ResolveSaveVesselPidExists(out skipReason);
                if (pidExists == null)
                    ParsekLog.Verbose("CrewReservation",
                        $"Rescue-placed marker prune skipped: {skipReason}; " +
                        $"writing all {rescuePlacedKerbals.Count} marker(s)");
                int pruned;
                var kept = CollectSavableRescueMarkers(rescuePlacedKerbals, pidExists, out pruned);
                if (kept.Count > 0)
                {
                    ConfigNode markersNode = node.AddNode(RescuePlacedNodeName);
                    for (int i = 0; i < kept.Count; i++)
                    {
                        ConfigNode entry = markersNode.AddNode("ENTRY");
                        entry.AddValue("name", kept[i].Key);
                        entry.AddValue("vesselPid", kept[i].Value.VesselPid.ToString(CultureInfo.InvariantCulture));
                        if (!double.IsNaN(kept[i].Value.MarkUT))
                            entry.AddValue(RescueMarkUTKey, kept[i].Value.MarkUT.ToString("R", CultureInfo.InvariantCulture));
                    }
                }
                CrewLog($"Saved {kept.Count} rescue-placed marker(s), left out {pruned} whose vessel no longer exists");
            }

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
