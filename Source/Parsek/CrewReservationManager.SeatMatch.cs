using System;
using System.Collections.Generic;

namespace Parsek
{
    internal static partial class CrewReservationManager
    {
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
        /// Bug #456 — which matcher tier produced a successful orphan placement.
        /// Exposed so `PlaceOrphanedReplacements` can count pid-hits vs
        /// name-hit fallbacks and surface the split in its summary log.
        /// </summary>
        internal enum SeatMatchKind
        {
            None = 0,
            PidHit = 1,
            NameHit = 2,
        }

        internal enum SeatMatchMissReason
        {
            None = 0,
            ActiveVesselHasNoParts = 1,
            NoSnapshotLookupTier = 2,
            ActiveVesselMissingSnapshotPart = 3,
            SnapshotPartSeatsFull = 4,
            SnapshotPidPartSeatsFull = 5,
            ActiveVesselMissingSnapshotPid = 6,
        }

        /// <summary>
        /// Bug #456 — lightweight view of a live vessel part used by the pure
        /// seat-matcher helper. Avoids taking a hard dependency on the KSP
        /// <see cref="Part"/> type so `TryResolveActiveVesselPartForSeat` is
        /// fully unit-testable.
        /// </summary>
        internal struct ActivePartSeat
        {
            public uint PersistentId;
            public string PartName;
            public int FreeSeats;
        }

        internal struct SeatMatchMissDiagnostic
        {
            public SeatMatchMissReason Reason;
            public int ActivePartCount;
            public int FreeSeatPartCount;
            public int PidMatchCount;
            public int PidFreeSeatCount;
            public int NameMatchCount;
            public int NameFreeSeatCount;
        }

        /// <summary>
        /// Formats truthful per-attempt tier telemetry for the no-match WARN.
        /// Distinct from the cumulative pidHits/nameHitFallbacks counters, which
        /// track prior successful placements across the whole pass.
        /// </summary>
        internal static string FormatSeatMatchAttemptDiagnostic(
            uint snapshotPartPid, string snapshotPartName)
        {
            return $"attempted pidTier={(snapshotPartPid != 0 ? "yes" : "no")} " +
                $"nameTier={(!string.IsNullOrEmpty(snapshotPartName) ? "yes" : "no")}";
        }

        internal static string FormatSeatMatchMissDiagnostic(SeatMatchMissDiagnostic diagnostic)
        {
            return $"reason={FormatSeatMatchMissReason(diagnostic.Reason)} " +
                $"activeParts={diagnostic.ActivePartCount} " +
                $"freeSeatParts={diagnostic.FreeSeatPartCount} " +
                $"pidMatches={diagnostic.PidMatchCount} " +
                $"pidFreeSeats={diagnostic.PidFreeSeatCount} " +
                $"nameMatches={diagnostic.NameMatchCount} " +
                $"nameFreeSeats={diagnostic.NameFreeSeatCount}";
        }

        private static string FormatSeatMatchMissReason(SeatMatchMissReason reason)
        {
            switch (reason)
            {
                case SeatMatchMissReason.ActiveVesselHasNoParts:
                    return "active-vessel-has-no-parts";
                case SeatMatchMissReason.NoSnapshotLookupTier:
                    return "no-snapshot-lookup-tier";
                case SeatMatchMissReason.ActiveVesselMissingSnapshotPart:
                    return "active-vessel-missing-snapshot-part";
                case SeatMatchMissReason.SnapshotPartSeatsFull:
                    return "snapshot-part-seats-full";
                case SeatMatchMissReason.SnapshotPidPartSeatsFull:
                    return "snapshot-pid-part-seats-full";
                case SeatMatchMissReason.ActiveVesselMissingSnapshotPid:
                    return "active-vessel-missing-snapshot-pid";
                default:
                    return "none";
            }
        }

        internal static void LogOrphanPlacementDeferred(
            string originalName,
            string replacementName,
            uint snapshotPartPid,
            string snapshotPartName,
            SeatMatchMissDiagnostic missDiagnostic,
            int pidHits,
            int nameHitFallbacks)
        {
            // Preserve the distinctive "snapshot pid=0" signal (bug #413):
            // a future regression where the capture site drops the real
            // persistentId will still show up here with pid=0 and a valid part
            // name, so it's callable out by log-grep.
            string pidDiagnostic = snapshotPartPid == 0
                ? "pid=0 (suspicious: snapshot missing persistentId)"
                : $"pid={snapshotPartPid}";
            string attemptDiagnostic = FormatSeatMatchAttemptDiagnostic(
                snapshotPartPid, snapshotPartName);
            ParsekLog.Warn("CrewReservation",
                $"Orphan placement deferred: no matching part with free seat in active vessel for " +
                $"'{originalName}' → '{replacementName}' " +
                $"(snapshot {pidDiagnostic} name='{snapshotPartName}') — " +
                $"stand-in kept in roster; {FormatSeatMatchMissDiagnostic(missDiagnostic)} " +
                $"({attemptDiagnostic}; cumulative pidHits={pidHits} " +
                $"nameHitFallbacks={nameHitFallbacks})");
        }

        /// <summary>
        /// Bug #456 — pure seat-match decision.
        ///
        /// Two tiers:
        ///   1. <b>PidHit</b>: snapshot <paramref name="snapshotPartPid"/> is
        ///      non-zero and matches an active part's PersistentId that still has
        ///      a free seat. A pid hit always wins over a name hit — this keeps
        ///      the old behaviour for post-revert vessels where part PIDs survive,
        ///      and defends against brittle false-positives when an identical-name
        ///      reassembled vessel happens to share the snapshot pid.
        ///   2. <b>NameHit</b> (fallback): no pid match, or the snapshot pid is
        ///      zero or the pid-matched part is full. Find all parts whose
        ///      <c>PartName</c> equals <paramref name="snapshotPartName"/> AND
        ///      have at least one free seat, and return the one with the
        ///      <i>fewest</i> free seats (tightest fit). Ties broken by index
        ///      (first occurrence wins). This prefers cockpits (typically 1 seat)
        ///      over passenger cabins (typically 4+ seats) when both share the
        ///      part name — which doesn't happen in stock, but defends against
        ///      part mods that reuse part.cfg <c>name =</c> across variants.
        ///
        /// Returns <c>-1</c> when neither tier matches, in which case
        /// <paramref name="matchKind"/> is <see cref="SeatMatchKind.None"/>.
        /// </summary>
        internal static int TryResolveActiveVesselPartForSeat(
            uint snapshotPartPid,
            string snapshotPartName,
            IList<ActivePartSeat> activeParts,
            out SeatMatchKind matchKind)
        {
            SeatMatchMissDiagnostic ignored;
            return TryResolveActiveVesselPartForSeat(
                snapshotPartPid, snapshotPartName, activeParts, out matchKind, out ignored);
        }

        internal static int TryResolveActiveVesselPartForSeat(
            uint snapshotPartPid,
            string snapshotPartName,
            IList<ActivePartSeat> activeParts,
            out SeatMatchKind matchKind,
            out SeatMatchMissDiagnostic missDiagnostic)
        {
            matchKind = SeatMatchKind.None;
            missDiagnostic = new SeatMatchMissDiagnostic
            {
                Reason = SeatMatchMissReason.None,
                ActivePartCount = activeParts != null ? activeParts.Count : 0,
            };
            if (activeParts == null || activeParts.Count == 0)
            {
                missDiagnostic.Reason = SeatMatchMissReason.ActiveVesselHasNoParts;
                return -1;
            }

            for (int i = 0; i < activeParts.Count; i++)
            {
                var p = activeParts[i];
                if (p.FreeSeats > 0)
                    missDiagnostic.FreeSeatPartCount++;
                if (snapshotPartPid == 0 || p.PersistentId != snapshotPartPid)
                    continue;

                missDiagnostic.PidMatchCount++;
                if (p.FreeSeats > 0)
                {
                    missDiagnostic.PidFreeSeatCount++;
                    matchKind = SeatMatchKind.PidHit;
                    return i;
                }
            }

            // Tier 2: name-hit with tightest-fit (min free seats).
            if (!string.IsNullOrEmpty(snapshotPartName))
            {
                int bestIdx = -1;
                int bestFreeSeats = int.MaxValue;
                for (int i = 0; i < activeParts.Count; i++)
                {
                    var p = activeParts[i];
                    if (string.IsNullOrEmpty(p.PartName)) continue;
                    if (p.PartName != snapshotPartName) continue;
                    missDiagnostic.NameMatchCount++;
                    if (p.FreeSeats <= 0) continue;
                    missDiagnostic.NameFreeSeatCount++;
                    if (p.FreeSeats < bestFreeSeats)
                    {
                        bestFreeSeats = p.FreeSeats;
                        bestIdx = i;
                    }
                }
                if (bestIdx >= 0)
                {
                    matchKind = SeatMatchKind.NameHit;
                    return bestIdx;
                }
            }

            missDiagnostic.Reason = ClassifySeatMatchMiss(
                snapshotPartPid, snapshotPartName, missDiagnostic);
            return -1;
        }

        private static SeatMatchMissReason ClassifySeatMatchMiss(
            uint snapshotPartPid,
            string snapshotPartName,
            SeatMatchMissDiagnostic diagnostic)
        {
            if (diagnostic.ActivePartCount <= 0)
                return SeatMatchMissReason.ActiveVesselHasNoParts;

            bool attemptedPid = snapshotPartPid != 0;
            bool attemptedName = !string.IsNullOrEmpty(snapshotPartName);
            if (!attemptedPid && !attemptedName)
                return SeatMatchMissReason.NoSnapshotLookupTier;

            if (attemptedName)
            {
                if (diagnostic.NameMatchCount == 0)
                    return SeatMatchMissReason.ActiveVesselMissingSnapshotPart;
            }

            if (attemptedPid &&
                diagnostic.PidMatchCount > 0 &&
                diagnostic.PidFreeSeatCount == 0)
                return SeatMatchMissReason.SnapshotPidPartSeatsFull;

            if (attemptedName)
            {
                if (diagnostic.NameFreeSeatCount == 0)
                    return SeatMatchMissReason.SnapshotPartSeatsFull;
            }

            if (attemptedPid)
            {
                if (!attemptedName && diagnostic.PidMatchCount == 0)
                    return SeatMatchMissReason.ActiveVesselMissingSnapshotPid;
            }

            return SeatMatchMissReason.None;
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

                        // Bug #413: KSP's ProtoPartSnapshot serializes the part's
                        // 32-bit pid under the key `persistentId` (see any stock
                        // `.sfs` PART node). The earlier implementation read `pid`,
                        // which only exists on the VESSEL node (a guid-hex string),
                        // so `uint.TryParse` always failed and every orphan seat
                        // was returned with PartPid=0. Prefer `persistentId` and
                        // fall back to `pid` so test-authored snapshots that still
                        // use the legacy field name keep matching.
                        uint pid = 0;
                        string pidValue = partNode.GetValue("persistentId");
                        if (string.IsNullOrEmpty(pidValue))
                            pidValue = partNode.GetValue("pid");
                        if (!string.IsNullOrEmpty(pidValue))
                            uint.TryParse(pidValue, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out pid);

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
        internal static HashSet<uint> BuildSpawnedVesselPidSet(IReadOnlyList<Recording> recordings)
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
        /// Pure decision: is the active vessel (pid + launch guid) genuinely a Parsek-spawned
        /// vessel per the Effective Recording Set? A recording "spawned" the active vessel when its
        /// <see cref="Recording.SpawnedVesselPersistentId"/> equals the active pid. For an
        /// adoption-stamped recording (SpawnedVesselPersistentId == its craft-baked VesselPersistentId)
        /// the pid is reused by every relaunch of the same craft, so that match additionally requires
        /// the launch guids to agree (a conclusive guid mismatch means a relaunch, not the spawned
        /// vessel). Real spawns use a KSP-unique spawn pid that cannot collide with a baked pid, so
        /// they stay pid-only. Falls back to pid-only when the guid is unknown on either side.
        /// </summary>
        internal static bool ActiveVesselIsParsekSpawned(
            IReadOnlyList<Recording> recordings, uint activePid, string activeGuid)
        {
            if (recordings == null || activePid == 0) return false;
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null || r.SpawnedVesselPersistentId == 0 || r.SpawnedVesselPersistentId != activePid)
                    continue;

                bool adoptionStamp = r.SpawnedVesselPersistentId == r.VesselPersistentId;
                if (adoptionStamp
                    && VesselLaunchIdentity.GuidsConclusivelyDiffer(r.RecordedVesselGuid, activeGuid))
                {
                    // Relaunch of the same craft reused the baked pid; this recording did not
                    // spawn the active vessel. Keep looking for a genuine match.
                    continue;
                }
                return true;
            }
            return false;
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
        /// Pure decision: can a stand-in with the given roster status be placed
        /// into a seat (crew manifest, live part, or spawn snapshot)? Only
        /// Available stand-ins are placeable. An Assigned stand-in is already
        /// aboard a live vessel — a kerbal can be aboard at most one vessel, so
        /// placing them again duplicates the ProtoCrewMember across vessels
        /// (the Barton-Kerman-on-four-vessels bug). Dead and Missing stand-ins
        /// are unusable here; the orphan-placement pass has its own dedicated
        /// Missing-rescue step that runs BEFORE this predicate.
        /// </summary>
        internal static bool IsStandInPlaceable(ProtoCrewMember.RosterStatus status)
        {
            return status == ProtoCrewMember.RosterStatus.Available;
        }

        /// <summary>
        /// Per-status verdict for placing a stand-in into a live seat.
        /// See <see cref="ClassifyStandInForPlacement"/>.
        /// </summary>
        internal enum StandInPlacementCheck
        {
            /// <summary>Available — place normally.</summary>
            Place,
            /// <summary>Missing — alive but orphaned from a removed vessel; rescue to Available, then place (mirrors ReserveCrewIn / RescueReservedMissingCrewInSnapshot).</summary>
            PlaceAfterMissingRescue,
            /// <summary>Name not in the roster — cannot place.</summary>
            SkipNotInRoster,
            /// <summary>Dead is permanent — cannot place.</summary>
            SkipDead,
            /// <summary>Assigned aboard a live vessel — placing again would duplicate the kerbal across vessels.</summary>
            SkipAssigned
        }

        /// <summary>
        /// Pure decision shared by the in-flight swap (Pass 1) and the orphan
        /// placement pass: classify a stand-in's roster state into a placement
        /// verdict. Keeps the Missing-rescue convention (the three runtime
        /// paths — ReserveCrewIn, PlaceOrphanedReplacements,
        /// RescueReservedMissingCrewInSnapshot — agree that Missing is rescued
        /// to Available before placement) while blocking the duplication case:
        /// an Assigned stand-in is already aboard a live vessel and must never
        /// be seated a second time.
        /// </summary>
        internal static StandInPlacementCheck ClassifyStandInForPlacement(
            bool inRoster, ProtoCrewMember.RosterStatus status)
        {
            if (!inRoster)
                return StandInPlacementCheck.SkipNotInRoster;
            switch (status)
            {
                case ProtoCrewMember.RosterStatus.Dead:
                    return StandInPlacementCheck.SkipDead;
                case ProtoCrewMember.RosterStatus.Missing:
                    return StandInPlacementCheck.PlaceAfterMissingRescue;
                case ProtoCrewMember.RosterStatus.Assigned:
                    return StandInPlacementCheck.SkipAssigned;
                default:
                    return StandInPlacementCheck.Place;
            }
        }

        /// <summary>
        /// Pure method: swaps reserved crew names in a vessel snapshot ConfigNode,
        /// replacing each reserved original name with its replacement name.
        /// Used for KSC spawns where SwapReservedCrewInFlight cannot run
        /// (no loaded vessel / no flight scene). Bug #167.
        ///
        /// When <paramref name="standInStatusResolver"/> is provided, a stand-in
        /// who is Assigned (aboard another live vessel) or Dead is NOT written
        /// into the snapshot; the seat is left empty instead. Writing an
        /// Assigned name would seat the same kerbal on two vessels at once when
        /// the snapshot is ProtoVessel-loaded. A resolver returning null (name
        /// not in roster) is also treated as not placeable — pre-fix that name
        /// was backfilled by VesselSpawner.EnsureCrewExistInRoster, but a
        /// missing stand-in name means a failed ApplyToRoster recreate, so the
        /// conservative empty seat is preferred. A MISSING stand-in flows
        /// through unchanged: both spawn routes run
        /// RescueReservedMissingCrewInSnapshot downstream (the agreed
        /// Missing-rescue path), and this pure method cannot mutate the roster.
        /// With a null resolver the legacy blind swap is preserved (pure-test
        /// compatibility). Returns the number of crew names swapped.
        /// </summary>
        internal static int SwapReservedCrewInSnapshot(
            ConfigNode snapshot, IReadOnlyDictionary<string, string> replacements,
            Func<string, ProtoCrewMember.RosterStatus?> standInStatusResolver = null)
        {
            return SwapReservedCrewInSnapshot(
                snapshot, replacements, standInStatusResolver, out _);
        }

        /// <summary>
        /// Overload exposing how many seats were left empty because the
        /// stand-in was not placeable, so callers (KSC spawn log) can report
        /// "0 swapped" and "N seats cleared" as distinct outcomes.
        /// </summary>
        internal static int SwapReservedCrewInSnapshot(
            ConfigNode snapshot, IReadOnlyDictionary<string, string> replacements,
            Func<string, ProtoCrewMember.RosterStatus?> standInStatusResolver,
            out int seatsCleared)
        {
            seatsCleared = 0;
            if (snapshot == null || replacements == null || replacements.Count == 0)
                return 0;

            int swapCount = 0;
            int partIndex = 0;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                string[] crewNames = partNode.GetValues("crew");
                if (crewNames.Length == 0) { partIndex++; continue; }

                bool anyChanged = false;
                var updated = new List<string>(crewNames.Length);

                for (int i = 0; i < crewNames.Length; i++)
                {
                    if (replacements.TryGetValue(crewNames[i], out string replacementName))
                    {
                        ProtoCrewMember.RosterStatus? status =
                            standInStatusResolver?.Invoke(replacementName);
                        bool blocked = standInStatusResolver != null
                            && (status == null
                                || status.Value == ProtoCrewMember.RosterStatus.Assigned
                                || status.Value == ProtoCrewMember.RosterStatus.Dead);
                        if (blocked)
                        {
                            ParsekLog.Warn("CrewReservation",
                                $"Snapshot swap: stand-in '{replacementName}' for reserved " +
                                $"'{crewNames[i]}' is {(status?.ToString() ?? "not in roster")} " +
                                $"— seat left empty in PART[{partIndex}] to avoid duplicating " +
                                "the kerbal across vessels");
                            anyChanged = true;
                            seatsCleared++;
                            continue;
                        }
                        if (status == ProtoCrewMember.RosterStatus.Missing)
                        {
                            // Missing flows through: RescueReservedMissingCrewInSnapshot
                            // runs on both spawn routes after this swap and rescues
                            // the (unreserved) Missing stand-in to Available.
                            ParsekLog.Verbose("CrewReservation",
                                $"Snapshot swap: stand-in '{replacementName}' is Missing — " +
                                "written through for the downstream spawn-route Missing rescue");
                        }

                        ParsekLog.Verbose("CrewReservation",
                            $"Snapshot swap: '{crewNames[i]}' -> '{replacementName}' in PART[{partIndex}]");
                        updated.Add(replacementName);
                        anyChanged = true;
                        swapCount++;
                    }
                    else
                    {
                        updated.Add(crewNames[i]);
                    }
                }

                if (anyChanged)
                {
                    partNode.RemoveValues("crew");
                    for (int i = 0; i < updated.Count; i++)
                        partNode.AddValue("crew", updated[i]);
                }

                partIndex++;
            }

            if (swapCount > 0 || seatsCleared > 0)
                ParsekLog.Verbose("CrewReservation",
                    $"Snapshot crew swap complete: {swapCount} name(s) replaced, " +
                    $"{seatsCleared} seat(s) left empty across {partIndex} part(s)");

            return swapCount;
        }

        #endregion
    }
}
