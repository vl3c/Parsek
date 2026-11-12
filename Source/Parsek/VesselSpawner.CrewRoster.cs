using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // Crew against the live roster: dead / reserved / duplicate crew in a spawn snapshot, the crew manifest, crew rescue and roster creation.
    public static partial class VesselSpawner
    {
        /// <summary>
        /// Walk PART nodes in a vessel snapshot ConfigNode and return
        /// a dictionary of crew traits (Pilot/Scientist/Engineer/Tourist) to counts.
        /// Uses traitResolver if provided; otherwise falls back to KerbalsModule.FindTraitForKerbal.
        /// Returns null if input is null, has no parts, or no crew found.
        /// </summary>
        internal static Dictionary<string, int> ExtractCrewManifest(
            ConfigNode vesselSnapshot, Func<string, string> traitResolver = null)
        {
            if (vesselSnapshot == null) return null;

            var parts = vesselSnapshot.GetNodes("PART");
            if (parts.Length == 0) return null;

            var manifest = new Dictionary<string, int>();
            int crewCount = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                var crewValues = parts[i].GetValues("crew");
                for (int j = 0; j < crewValues.Length; j++)
                {
                    string name = crewValues[j];
                    if (string.IsNullOrEmpty(name)) continue;

                    string trait = traitResolver != null
                        ? traitResolver(name)
                        : KerbalsModule.FindTraitForKerbal(name);

                    if (manifest.ContainsKey(trait))
                        manifest[trait] = manifest[trait] + 1;
                    else
                        manifest[trait] = 1;

                    crewCount++;
                }
            }

            if (manifest.Count == 0) return null;

            ParsekLog.Verbose("Spawner",
                $"ExtractCrewManifest: {crewCount} crew across {manifest.Count} trait(s) from {parts.Length} part(s)");

            return manifest;
        }

        /// <summary>
        /// Builds a set of crew names from the given list that are spawn-blocking.
        /// Only StrictlyDead (RosterStatus.Dead) crew block spawn — Dead is
        /// permanent. Missing crew are NOT blocking, regardless of reservation:
        /// Missing is transient (stock resolves it at the kerbal's respawn timer:
        /// back to Available when Difficulty.MissingCrewsRespawn is on, Dead when it
        /// is off - decompiled ProtoCrewMember.CheckRespawnTimer), a recorded crew
        /// death reaches Missing only with respawn on and is held by its own
        /// death-respawn reservation (owner ruling S8), and the spawn pipeline
        /// rescues every Missing snapshot crew member to Available before
        /// <c>ProtoVessel.Load</c> (see
        /// <see cref="RescueReservedMissingCrewInSnapshot"/>). The reserved-only
        /// carve-out from #608/#609 was extended in #687 to cover unreserved
        /// Missing crew too: a plain Rewind that strips a previously-spawned
        /// Parsek vessel marks its kerbals Missing without setting up
        /// reservations, but the recording's end-of-playback spawn must still
        /// re-create the vessel with those (transiently Missing) kerbals.
        /// Runtime-only: accesses HighLogic.CurrentGame.CrewRoster.
        /// Returns an empty set if the roster is unavailable.
        /// </summary>
        private static HashSet<string> BuildDeadCrewSet(List<string> crewNames)
        {
            var deadSet = new HashSet<string>();
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return deadSet;
            for (int i = 0; i < crewNames.Count; i++)
            {
                string name = crewNames[i];
                if (IsCrewStrictlyDeadInRoster(name, roster))
                    deadSet.Add(name);
            }
            return deadSet;
        }

        /// <summary>
        /// Per-crew classification used by the dead-crew abandon log so the WARN
        /// can break down the snapshot crew list by reason instead of just
        /// listing names. (#608/#609)
        /// </summary>
        internal enum SpawnableClassification
        {
            Alive = 0,                    // Available / Assigned / Crew — does not block spawn
            ReservedMissingRescuable = 1, // Missing but reserved — rescued at spawn time, does not block (#608/#609)
            MissingNotReserved = 2,       // Missing, no reservation — rescued at spawn time, does not block (#687)
            StrictlyDead = 3,             // Dead — permanent, blocks spawn, never rescuable (#170)
        }

        /// <summary>
        /// Classify each snapshot crew name into the categories the abandon log
        /// reports. Pure decision: does not mutate state. (#608/#609)
        /// </summary>
        internal static List<KeyValuePair<string, SpawnableClassification>> ClassifySnapshotCrew(
            List<string> snapshotCrew)
        {
            var result = new List<KeyValuePair<string, SpawnableClassification>>(
                snapshotCrew != null ? snapshotCrew.Count : 0);
            if (snapshotCrew == null || snapshotCrew.Count == 0) return result;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            var reserved = CrewReservationManager.CrewReplacements;
            for (int i = 0; i < snapshotCrew.Count; i++)
            {
                string name = snapshotCrew[i];
                SpawnableClassification cls;
                if (roster != null && IsCrewStrictlyDeadInRoster(name, roster))
                    cls = SpawnableClassification.StrictlyDead;
                else if (roster != null && IsCrewDeadInRoster(name, roster))
                    cls = reserved.ContainsKey(name)
                        ? SpawnableClassification.ReservedMissingRescuable
                        : SpawnableClassification.MissingNotReserved;
                else
                    cls = SpawnableClassification.Alive;
                result.Add(new KeyValuePair<string, SpawnableClassification>(name, cls));
            }
            return result;
        }

        /// <summary>
        /// Format the per-category summary used by both the abandon WARN and
        /// the carve-out Verbose log (#608/#609). Counts each category and lists the
        /// names with their classification.
        /// </summary>
        internal static string FormatSpawnableClassificationSummary(
            List<KeyValuePair<string, SpawnableClassification>> classified)
        {
            int alive = 0, reservedMissing = 0, missingNotReserved = 0, strictlyDead = 0;
            for (int i = 0; i < classified.Count; i++)
            {
                switch (classified[i].Value)
                {
                    case SpawnableClassification.Alive: alive++; break;
                    case SpawnableClassification.ReservedMissingRescuable: reservedMissing++; break;
                    case SpawnableClassification.MissingNotReserved: missingNotReserved++; break;
                    case SpawnableClassification.StrictlyDead: strictlyDead++; break;
                }
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("total=").Append(classified.Count);
            sb.Append(" strictlyDead=").Append(strictlyDead);
            sb.Append(" missingNotReserved=").Append(missingNotReserved);
            sb.Append(" reservedMissing=").Append(reservedMissing);
            sb.Append(" alive=").Append(alive);
            sb.Append(" [");
            for (int i = 0; i < classified.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(classified[i].Key).Append(": ").Append(classified[i].Value);
            }
            sb.Append("]");
            return sb.ToString();
        }

        internal static bool ShouldBlockSpawnForDeadCrewInSnapshot(
            ConfigNode snapshot,
            out List<string> snapshotCrew)
        {
            snapshotCrew = VesselSnapshotOps.ExtractCrewNamesFromSnapshot(snapshot);
            var deadSet = BuildDeadCrewSet(snapshotCrew);
            bool block = VesselSnapshotOps.ShouldBlockSpawnForDeadCrew(snapshotCrew, deadSet);

            // (#608/#609/#687) When the carve-out lets a snapshot through that
            // the pre-fix code would have abandoned, log a Verbose breakdown
            // so playtest logs make the recovery visible. Pre-fix decision
            // used IsCrewDeadInRoster (Dead OR Missing) for every name, so the
            // old would-have-blocked predicate is "every snapshot name is Dead
            // OR Missing in the roster". The carve-out now covers BOTH
            // reserved+Missing (#608/#609) and unreserved+Missing (#687) — at
            // least one ReservedMissingRescuable or MissingNotReserved entry
            // made the difference.
            if (!block && snapshotCrew.Count > 0)
            {
                var classified = ClassifySnapshotCrew(snapshotCrew);
                bool wouldHaveBlocked = true;
                bool anyReservedMissing = false;
                bool anyMissingNotReserved = false;
                for (int i = 0; i < classified.Count; i++)
                {
                    var c = classified[i].Value;
                    if (c == SpawnableClassification.Alive) { wouldHaveBlocked = false; }
                    else if (c == SpawnableClassification.ReservedMissingRescuable) anyReservedMissing = true;
                    else if (c == SpawnableClassification.MissingNotReserved) anyMissingNotReserved = true;
                }
                if (wouldHaveBlocked && (anyReservedMissing || anyMissingNotReserved))
                {
                    ParsekLog.Verbose("Spawner",
                        "Spawn-block carve-out applied (#608/#609/#687): allowing spawn that pre-fix would " +
                        "have abandoned because Missing crew is rescuable — " +
                        FormatSpawnableClassificationSummary(classified));
                }
            }

            return block;
        }

        internal static HashSet<string> BuildExcludeCrewSet(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.RecordingId)) return null;

            HashSet<string> excludeCrew = null;
            // [Phase 3] ERS-routed: chain-aware crew-exclusion walk must respect
            // effective recording visibility so that hidden / superseded chain
            // siblings do not spuriously exclude their crew.
            var committed = EffectiveState.ComputeERS();

            // Chain-aware: exclude EVA crew who are still on EVA at the end of the chain
            // (no subsequent vessel segment). Crew who boarded back are NOT excluded.
            // EVA segments themselves should never exclude their own crew.
            if (!string.IsNullOrEmpty(rec.ChainId) && string.IsNullOrEmpty(rec.EvaCrewName))
            {
                // Find the highest ChainIndex of a non-EVA (vessel) segment on branch 0 only
                int highestVesselIndex = -1;
                List<(string crew, int index)> evaSegments = null;

                for (int c = 0; c < committed.Count; c++)
                {
                    var sibling = committed[c];
                    if (sibling.ChainId != rec.ChainId) continue;
                    // Skip branch > 0 segments for crew exclusion logic
                    if (sibling.ChainBranch != 0) continue;

                    if (!string.IsNullOrEmpty(sibling.EvaCrewName))
                    {
                        if (evaSegments == null) evaSegments = new List<(string, int)>();
                        evaSegments.Add((sibling.EvaCrewName, sibling.ChainIndex));
                    }
                    else if (sibling.ChainIndex > highestVesselIndex)
                    {
                        highestVesselIndex = sibling.ChainIndex;
                    }
                }

                // Exclude EVA crew whose EVA segment comes after all vessel segments
                // (they're still on EVA — didn't board back)
                if (evaSegments != null)
                {
                    for (int e = 0; e < evaSegments.Count; e++)
                    {
                        if (evaSegments[e].index > highestVesselIndex)
                        {
                            if (excludeCrew == null) excludeCrew = new HashSet<string>();
                            excludeCrew.Add(evaSegments[e].crew);
                        }
                    }
                }

                if (excludeCrew != null)
                    ParsekLog.Info("Spawner", $"Excluding EVA'd crew from chain vessel spawn: [{string.Join(", ", excludeCrew)}]");
                return excludeCrew;
            }

            // Legacy fallback: also check single-level parent→child linkage
            // (for old saves without chain fields)
            for (int c = 0; c < committed.Count; c++)
            {
                var child = committed[c];
                if (child.ParentRecordingId == rec.RecordingId && !string.IsNullOrEmpty(child.EvaCrewName))
                {
                    if (excludeCrew == null) excludeCrew = new HashSet<string>();
                    excludeCrew.Add(child.EvaCrewName);
                }
            }

            if (excludeCrew != null)
                ParsekLog.Info("Spawner", $"Excluding EVA'd crew from parent spawn: [{string.Join(", ", excludeCrew)}]");
            return excludeCrew;
        }

        /// <summary>
        /// Removes crew members from a spawn snapshot if they already exist on a loaded
        /// vessel in the scene. Prevents kerbal duplication when a previously-spawned vessel
        /// still has the same crew member aboard.
        ///
        /// <para>
        /// DELIBERATELY NAME-GLOBAL, not scoped to the recording's own launch (#16 triage).
        /// The tempting fix - only dedup against vessels of the same launch - is wrong,
        /// because a kerbal is not a per-launch thing: KSP's <c>KerbalRoster</c> holds ONE
        /// <c>ProtoCrewMember</c> per name for the whole save, and a name is that object's
        /// identity. Loading a snapshot that seats a kerbal who is already aboard another
        /// live vessel does not create a copy of them; it points a second part at the SAME
        /// roster entry, which is a corrupt game state (KSP tracks a kerbal's seat on the
        /// crew member itself). So "this kerbal is somewhere else right now" really does mean
        /// "this kerbal cannot be here", whichever launch the somewhere-else vessel belongs
        /// to - a kerbal genuinely cannot be in two places, and the empty seat is the
        /// CORRECT outcome rather than a symptom.
        /// </para>
        /// <para>
        /// What the preserved-fleet world changed is how OFTEN that outcome happens: before
        /// Re-Fly preserved unrelated vessels, the scene the spawn landed in was nearly
        /// empty, so a materialization arriving with empty seats was rare enough to go
        /// unnoticed. It is now reachable in normal play (the crew you recorded are aboard a
        /// craft the rewind left flying), and the fix that was actually owed is the log:
        /// each removal now names WHICH live vessel holds the kerbal, and a removal that
        /// empties the snapshot's roster entirely is called out as one line instead of being
        /// inferable only by counting Warns.
        /// </para>
        /// </summary>
        public static void RemoveDuplicateCrewFromSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return;

            // Build name -> holding live vessel, so a removal can name the holder.
            var existingCrew = BuildExistingCrewHolders();
            if (existingCrew.Count == 0)
            {
                ParsekLog.Verbose("Spawner", "Crew dedup: no existing crew in scene — skipped");
                return;
            }

            RemoveDuplicateCrewFromSnapshotCore(snapshot, existingCrew);
        }

        /// <summary>
        /// Scene-free core of <see cref="RemoveDuplicateCrewFromSnapshot"/>: the holder map
        /// is the ONLY live-scene input, so with it supplied the extract / find-duplicates /
        /// PART-node removal / holder-naming Warn / summary Info sequence runs headless.
        /// Extracted so the removal loop is reachable from a unit test instead of being
        /// replayed by one. Returns the number of seats vacated.
        /// </summary>
        internal static int RemoveDuplicateCrewFromSnapshotCore(
            ConfigNode snapshot, Dictionary<string, string> existingCrew)
        {
            if (snapshot == null || existingCrew == null) return 0;

            // Extract crew from the snapshot and find duplicates
            var snapshotCrew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            var duplicates = VesselSnapshotOps.FindDuplicateCrew(
                snapshotCrew, new HashSet<string>(existingCrew.Keys));

            if (duplicates.Count == 0)
            {
                ParsekLog.Verbose("Spawner",
                    $"Crew dedup: checked {snapshotCrew.Count} crew against {existingCrew.Count} existing — no duplicates");
                return 0;
            }

            // Remove duplicates from snapshot parts (same pattern as RemoveSpecificCrewFromSnapshot)
            int removedCount = 0;
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var names = partNode.GetValues("crew");
                if (names.Length == 0) continue;

                var keep = new List<string>();
                bool removedAny = false;
                foreach (string name in names)
                {
                    if (duplicates.Contains(name))
                    {
                        removedAny = true;
                        removedCount++;
                        string holder;
                        if (!existingCrew.TryGetValue(name, out holder) || string.IsNullOrEmpty(holder))
                            holder = "(unknown vessel)";
                        ParsekLog.Warn("Spawner",
                            $"Crew dedup: '{name}' is already aboard {holder} — removed from spawn snapshot " +
                            "(a kerbal is one roster entry and cannot be in two places; the seat spawns empty)");
                    }
                    else
                    {
                        keep.Add(name);
                    }
                }

                if (removedAny)
                {
                    partNode.RemoveValues("crew");
                    foreach (string name in keep)
                        partNode.AddValue("crew", name);
                }
            }

            ParsekLog.Info("Spawner", VesselSnapshotOps.DescribeCrewDedupOutcome(snapshotCrew.Count, removedCount));
            return removedCount;
        }

        /// <summary>
        /// Walks the loaded vessels for their crew names and keeps WHICH live vessel holds
        /// each kerbal (<c>"name" (pid=N)</c>), so the crew-dedup Warn can name the holder
        /// instead of saying only that the kerbal is "on a vessel in the scene". With the
        /// fleet preserved across a rewind, that holder is the whole diagnosis: it is the
        /// craft the player still has flying with the recorded crew aboard.
        /// </summary>
        internal static Dictionary<string, string> BuildExistingCrewHolders()
        {
            var existing = new Dictionary<string, string>();
            if (FlightGlobals.Vessels == null) return existing;

            for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
            {
                Vessel vessel = FlightGlobals.Vessels[v];
                if (vessel == null) continue;
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId)) continue;
                var crew = vessel.GetVesselCrew();
                string holder = $"\"{vessel.vesselName ?? "(unnamed)"}\" (pid={vessel.persistentId})";
                for (int c = 0; c < crew.Count; c++)
                {
                    if (!string.IsNullOrEmpty(crew[c].name) && !existing.ContainsKey(crew[c].name))
                        existing[crew[c].name] = holder;
                }
            }
            return existing;
        }

        /// <summary>
        /// Pure decision: should crew be stripped from the spawn snapshot?
        /// Returns true when the recording's terminal state is Destroyed,
        /// preventing crew deaths during spawn-death cycles. (#114)
        /// </summary>
        internal static bool ShouldStripCrewForSpawn(Recording rec)
        {
            return rec.TerminalStateValue.HasValue
                && rec.TerminalStateValue.Value == TerminalState.Destroyed;
        }

        /// <summary>
        /// (#608/#609) Pre-spawn pass: for each crew name in the snapshot that is
        /// currently Missing in the roster, flip the roster status back to
        /// Available so KSP's <c>ProtoVessel.Load → Part.RegisterCrew</c> path
        /// can place them on the spawned vessel cleanly. Missing is transient
        /// (KSP's natural respawn timer flips it to Available after the
        /// respawn cooldown); rescuing here just accelerates that for the
        /// snapshot KSP is about to load.
        ///
        /// <para>Reserved+Missing is the post-Re-Fly-strip state where the
        /// original capsule was destroyed (KSP marked the crew Missing) but
        /// the recording is about to materialize and bring them back (#608/#609).
        /// Unreserved+Missing is the plain-Rewind strip case (#687) — the
        /// snapshot's originals are Missing but no reservation maps them
        /// because the rewind quicksave wiped scenario-side reservation state.
        /// Both shapes are rescued; only reserved rescues feed the pid-scoped
        /// marker pipeline (no stand-in lifecycle exists for unreserved).</para>
        ///
        /// <para>Mirrors the rescue branch in
        /// <see cref="CrewReservationManager.PlaceOrphanedReplacements"/> so
        /// the two runtime paths agree on Missing-rescue semantics.</para>
        ///
        /// <para>Guard: skip strictly Dead crew (Dead is permanent —
        /// <see cref="RemoveDeadCrewFromSnapshot"/> will strip them).</para>
        /// </summary>
        public static void RescueReservedMissingCrewInSnapshot(ConfigNode snapshot)
        {
            RescueReservedMissingCrewInSnapshot(snapshot, null);
        }

        /// <summary>
        /// Pid-scoping overload (#615 P1 review fourth pass). Collects the
        /// names of RESERVED kerbals actually flipped from Missing -> Available
        /// into <paramref name="rescuedNames"/> so the caller can call
        /// <see cref="CrewReservationManager.MarkRescuePlaced(string, ulong)"/>
        /// for each name once <c>ProtoVessel.Load</c> has assigned the new
        /// vessel's persistentId. The marker MUST be pid-scoped: a stale
        /// name-only marker from a long-past rescue would suppress a later
        /// unrelated fresh reservation for the same kerbal who happens to be
        /// on the active player vessel.
        ///
        /// <para>#687: unreserved+Missing snapshot crew are also rescued here.
        /// They have no stand-in to suppress, so the rescue-placed marker is
        /// not set (and unreserved names are NOT added to
        /// <paramref name="rescuedNames"/>). This covers the plain-Rewind
        /// strip case where the snapshot still references the originals but
        /// the live game lost reservations across the quicksave reload.</para>
        /// </summary>
        public static void RescueReservedMissingCrewInSnapshot(
            ConfigNode snapshot, List<string> rescuedNames)
        {
            if (snapshot == null) return;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn("Spawner",
                    "RescueReservedMissingCrewInSnapshot skipped: crew roster unavailable");
                return;
            }
            var reserved = CrewReservationManager.CrewReplacements;

            int rescuedReserved = 0;
            int rescuedUnreserved = 0;
            using (SuppressionGuard.Crew())
            {
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    var crewNames = partNode.GetValues("crew");
                    for (int i = 0; i < crewNames.Length; i++)
                    {
                        string name = crewNames[i];
                        if (string.IsNullOrEmpty(name)) continue;
                        ProtoCrewMember pcm = roster[name];
                        if (pcm == null) continue;
                        if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Missing) continue;
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;

                        bool isReserved = reserved != null && reserved.ContainsKey(name);
                        if (isReserved)
                        {
                            rescuedReserved++;
                            // #615 P1 review (fourth pass): the rescue-placed
                            // marker is pid-scoped — the caller marks each
                            // reserved rescued name with the spawned vessel's
                            // pid AFTER ProtoVessel.Load assigns one. Only
                            // RESERVED rescues need the marker (it suppresses
                            // ApplyToRoster stand-in re-creation); unreserved
                            // rescues have no stand-in lifecycle to suppress.
                            if (rescuedNames != null)
                                rescuedNames.Add(name);
                            ParsekLog.Info("Spawner",
                                $"Rescued reserved+Missing crew '{name}' -> Available before snapshot load (#608/#609; pending pid mark)");
                        }
                        else
                        {
                            rescuedUnreserved++;
                            ParsekLog.Info("Spawner",
                                $"Rescued unreserved+Missing crew '{name}' -> Available before snapshot load (#687)");
                        }
                    }
                }
            }

            int total = rescuedReserved + rescuedUnreserved;
            if (total > 0)
                ParsekLog.Info("Spawner",
                    $"Spawn prep: rescued {total} Missing crew member(s) -> Available " +
                    $"(reserved={rescuedReserved}; unreserved={rescuedUnreserved}; #608/#609/#687; " +
                    $"rescuedNames={(rescuedNames != null ? rescuedNames.Count.ToString() : "null")})");
        }

        public static void RemoveDeadCrewFromSnapshot(ConfigNode snapshot)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn("Spawner", "RemoveDeadCrewFromSnapshot skipped: crew roster unavailable");
                return;
            }

            var reserved = CrewReservationManager.CrewReplacements;
            int removedCount = 0;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                var crewNames = partNode.GetValues("crew");
                if (crewNames.Length == 0) continue;

                // Check if any crew are dead/missing
                var keepNames = new List<string>();
                bool removedAny = false;
                foreach (string name in crewNames)
                {
                    bool isDeadOrMissing = IsCrewDeadInRoster(name, roster);

                    // Reserved crew with Missing status are kept — Missing can be
                    // stale state from save manipulation (e.g. --clean-start).
                    // But Dead is permanent — a dead reserved crew member must be
                    // removed to avoid resurrecting them. (#170)
                    // Use Dead-only check: IsCrewDeadInRoster includes Missing,
                    // which would incorrectly remove reserved+Missing crew.
                    bool isStrictlyDead = IsCrewStrictlyDeadInRoster(name, roster);
                    if (reserved.ContainsKey(name) && !isStrictlyDead)
                    {
                        keepNames.Add(name);
                        continue;
                    }

                    if (isDeadOrMissing)
                    {
                        ParsekLog.Info("Spawner", $"Removed dead/missing crew '{name}' from vessel snapshot" +
                            (reserved.ContainsKey(name) ? " (was reserved but Dead overrides)" : ""));
                        removedCount++;
                        removedAny = true;
                    }
                    else
                    {
                        keepNames.Add(name);
                    }
                }

                if (removedAny)
                {
                    partNode.RemoveValues("crew");
                    foreach (string name in keepNames)
                        partNode.AddValue("crew", name);
                }
            }

            if (removedCount > 0)
                ParsekLog.Info("Spawner", $"Spawn prep: removed {removedCount} dead/missing crew from snapshot");
        }

        /// <summary>
        /// Checks whether a crew member is Dead or Missing in the KSP crew roster.
        /// Returns false if the crew member is not found in the roster (unknown crew
        /// are not considered dead — they may be from synthetic recordings).
        /// Runtime-only: requires HighLogic.CurrentGame.CrewRoster via the roster parameter.
        /// </summary>
        private static bool IsCrewDeadInRoster(string crewName, KerbalRoster roster)
        {
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == crewName &&
                    (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead ||
                     pcm.rosterStatus == ProtoCrewMember.RosterStatus.Missing))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks whether a crew member is strictly Dead (not Missing) in the KSP crew roster.
        /// Dead is permanent and unconditionally blocks/strips the kerbal across all spawn paths;
        /// Missing is transient (stock ends it at the respawn timer: Available with
        /// MissingCrewsRespawn on, Dead with it off; a recorded death is Missing only with
        /// respawn on and is held by its own death-respawn reservation, owner ruling S8) and
        /// a snapshot kerbal is rescued to Available before snapshot load. Called from <see cref="BuildDeadCrewSet"/>,
        /// <see cref="ClassifySnapshotCrew"/>, and <see cref="RemoveDeadCrewFromSnapshot"/> —
        /// the three sites that need a Dead-only predicate to avoid resurrecting Dead reserved
        /// crew. (#170 / #687)
        /// </summary>
        private static bool IsCrewStrictlyDeadInRoster(string crewName, KerbalRoster roster)
        {
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == crewName &&
                    pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Ensure all crew referenced in the snapshot exist in the game's CrewRoster.
        /// Synthetic recordings or cross-save imports may reference kerbals that were
        /// never added to this career's roster, causing NullRef in Part.RegisterCrew.
        /// A missing kerbal the timeline knows as a tourist (stock deleted him when his
        /// contract ended or his vessel was recovered, or a rewind went to before the offer)
        /// comes back as a tourist, never as hired crew (GC-12): created the way stock's
        /// TourismContract creates one, and marked toured so stock sends him home when the
        /// vessel is recovered (ScenarioContractEvents.TouristDepartures).
        /// </summary>
        public static void EnsureCrewExistInRoster(ConfigNode snapshot)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn("Spawner", "EnsureCrewExistInRoster skipped: crew roster unavailable");
                return;
            }

            var missing = VesselSnapshotOps.PlanMissingCrew(
                snapshot, name => roster[name] != null, KerbalsModule.HasTouristEvidence);

            int createdCount = 0;
            int touristCount = 0;
            for (int i = 0; i < missing.Count; i++)
            {
                string name = missing[i].Name;
                if (missing[i].AsTourist)
                {
                    ProtoCrewMember tourist = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Unowned);
                    if (tourist == null) continue;
                    tourist.ChangeName(name);
                    tourist.type = ProtoCrewMember.KerbalType.Tourist;
                    KerbalRoster.SetExperienceTrait(tourist, KerbalRoster.touristTrait);
                    tourist.hasToured = true;
                    tourist.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                    ParsekLog.Info("Spawner",
                        $"Created missing tourist '{name}' in roster for vessel spawn " +
                        "(type=Tourist trait=Tourist toured=True: a passenger, not hired crew)");
                    createdCount++;
                    touristCount++;
                    continue;
                }

                ProtoCrewMember newCrew = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                if (newCrew != null)
                {
                    newCrew.ChangeName(name);
                    newCrew.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                    ParsekLog.Info("Spawner", $"Created missing crew '{name}' in roster for vessel spawn");
                    createdCount++;
                }
            }

            if (createdCount > 0)
                ParsekLog.Info("Spawner",
                    $"Spawn prep: created {createdCount} missing crew in roster (tourists={touristCount})");
        }
    }
}
