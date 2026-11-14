using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // The ledger walk: pre-pass, per-action processing, recovery and KSC retirement closures, post-walk, the next release UT and the reservation transition log.
    internal partial class KerbalsModule : IResourceModule
    {
        internal static string FormatPrePassSummary(
            int examined,
            int cached,
            int nullRecordings,
            int missingRecordingIds,
            int rawCrewRecordings,
            int rawCrewMembers,
            int loopingChains)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "PrePass summary: examined={0} cached={1} nullRecordings={2} missingIds={3} rawCrewRecordings={4} rawCrewMembers={5} loopingChains={6}",
                examined,
                cached,
                nullRecordings,
                missingRecordingIds,
                rawCrewRecordings,
                rawCrewMembers,
                loopingChains);
        }

        internal static string FormatPostWalkSummary(
            int reservationCount,
            int permanentReservations,
            int temporaryReservations,
            int slotCount,
            int retiredCount,
            int slotsCreated,
            int releasedReservations = 0,
            int respawnPendingReservations = 0)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "PostWalk summary: reservations={0} permanent={1} temporary={2} slots={3} retired={4} slotsCreated={5} released={6} respawnPending={7}",
                reservationCount,
                permanentReservations,
                temporaryReservations,
                slotCount,
                retiredCount,
                slotsCreated,
                releasedReservations,
                respawnPendingReservations);
        }

        /// <summary>
        /// Builds recording metadata cache from RecordingStore.CommittedRecordings.
        /// This is necessary because loop/disabled/chain status is mutable (users can
        /// toggle at runtime) and is not encoded in GameAction fields.
        /// </summary>
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            // walkNowUT: the walk's cutoff when it has one. The recording-metadata cache
            // below comes from RecordingStore, not from action UTs; the walk clock is
            // captured ONCE here so every "is he reserved now" answer of this walk (and
            // of every consumer until the next walk) judges against the same instant.
            walkClockUT = ResolveWalkClockUT(walkNowUT);

            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return false;

            int examined = 0;
            int nullRecordings = 0;
            int missingRecordingIds = 0;
            int rawCrewRecordings = 0;
            int rawCrewMembers = 0;
            var kscRetiredEndUTs = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                examined++;
                var rec = recordings[i];
                if (rec == null)
                {
                    nullRecordings++;
                    continue;
                }
                if (string.IsNullOrEmpty(rec.RecordingId))
                {
                    missingRecordingIds++;
                    continue;
                }

                bool isLoop = rec.LoopPlayback;
                bool isChain = rec.IsChainRecording;
                string chainId = rec.ChainId;

                recordingMeta[rec.RecordingId] = new RecordingMeta
                {
                    IsLoop = isLoop,
                    IsChainRecording = isChain,
                    ChainId = chainId,
                    TreeId = rec.TreeId,
                    StartUT = rec.StartUT,
                    EndUT = rec.EndUT,
                    ParentBranchPointId = rec.ParentBranchPointId,
                    ChildBranchPointId = rec.ChildBranchPointId,
                    VesselPid = rec.VesselPersistentId,
                    VesselGuid = rec.RecordedVesselGuid,
                    DeathRespawns = rec.CrewDeathRespawns,
                    DeathRespawnSeconds = rec.CrewDeathRespawnSeconds
                };

                var rawCrew = ExtractRawCrewFromRecording(rec);
                if (rawCrew.Count > 0)
                {
                    rawRecordingCrew[rec.RecordingId] = new HashSet<string>(rawCrew);
                    rawCrewRecordings++;
                    rawCrewMembers += rawCrew.Count;
                }

                // Count chains that contain a looping segment (diagnostic only)
                if (isLoop && isChain && !string.IsNullOrEmpty(chainId))
                    loopingChainIds.Add(chainId);

                // KSC retirement (operator ruling 2026-09-23): a final segment whose flight
                // ended parked in the KSC exclusion zone never becomes a real vessel, so its
                // aboard crew are freed at its EndUT as if recovered. A looped recording's
                // first run is the real flight (design 12.7), so it retires like any other;
                // a crewless one has nobody to free.
                if (rawCrew.Count > 0 && VesselSpawner.IsKscRetiredFinalFlight(rec))
                    kscRetiredEndUTs[rec.RecordingId] = rec.EndUT;
            }

            ParsekLog.Verbose(Tag,
                FormatPrePassSummary(
                    examined,
                    recordingMeta.Count,
                    nullRecordings,
                    missingRecordingIds,
                    rawCrewRecordings,
                    rawCrewMembers,
                    loopingChainIds.Count)
                + " walkClockUT=" + FormatClockUT(walkClockUT));

            int closureRows = CollectRecoveryClosures(actions, recoveryClosures);
            if (closureRows > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: {closureRows.ToString(CultureInfo.InvariantCulture)} KerbalRecovered " +
                    $"row(s) for {recoveryClosures.Count.ToString(CultureInfo.InvariantCulture)} kerbal(s)");
            }

            if (kscRetiredEndUTs.Count > 0)
            {
                var treeIdByRecordingId = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in recordingMeta)
                    treeIdByRecordingId[kvp.Key] = kvp.Value.TreeId;
                int recoveryRowWins;
                int retiredClosures = CollectKscRetirementClosures(
                    actions, kscRetiredEndUTs, recoveryClosures, treeIdByRecordingId,
                    out recoveryRowWins);
                ParsekLog.Verbose(Tag,
                    $"PrePass: {kscRetiredEndUTs.Count.ToString(CultureInfo.InvariantCulture)} KSC-retired " +
                    $"recording(s) free {retiredClosures.ToString(CultureInfo.InvariantCulture)} aboard crew " +
                    "hold(s) at their EndUT (no ledger row); " +
                    $"recordedRecoveryWins={recoveryRowWins.ToString(CultureInfo.InvariantCulture)}");
            }

            int claimCount = CollectDockClaims(RecordingStore.CommittedTrees, actions);
            if (claimCount > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: {claimCount.ToString(CultureInfo.InvariantCulture)} dock claim(s) on " +
                    "recorded vessels can end an open-ended hold the claimed vessel carried");
            }

            int splitParents = CollectSplitBranchPointsByParent(RecordingStore.CommittedTrees);
            if (splitParents > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: {splitParents.ToString(CultureInfo.InvariantCulture)} recording(s) are " +
                    "the parent of a split branch point (a kerbal a child carried away mid-flight " +
                    "ends that recording's hold at the child's start)");
            }

            int handoffs = CollectSplitHandoffs(actions);
            if (handoffs > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: {handoffs.ToString(CultureInfo.InvariantCulture)} split-child crew " +
                    "row(s) can end their parent's open-ended hold at the split");
            }

            // The kerbals module never mutates the action list, so no re-sort is needed.
            return false;
        }

        /// <summary>
        /// Processes kerbal ledger actions. KerbalAssignment builds crew
        /// reservations; roster-creating rows are tracked so a tombstone-triggered
        /// roster cleanup only removes kerbals whose surviving ELS no longer has a
        /// matching creation row.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action == null)
                return;

            string rosterKerbalName;
            if (TryGetRosterCreatedKerbalName(action, out rosterKerbalName))
            {
                ledgerCreatedKerbals.Add(rosterKerbalName);
                ParsekLog.Verbose(Tag,
                    $"Roster-created kerbal retained by ledger: '{rosterKerbalName}' type={action.Type}");
                return;
            }

            if (action.Type == GameActionType.KerbalExperience)
            {
                AccumulateCareerEntries(action);
                return;
            }

            if (action.Type != GameActionType.KerbalAssignment)
                return;

            // A tourist row is tourist evidence whether or not it holds him: the spawn path
            // re-creates a missing tourist as a tourist, never as hired crew.
            bool touristRow = IsTouristRole(action.KerbalRole);
            if (touristRow && !string.IsNullOrEmpty(action.KerbalName))
                touristPassengers.Add(action.KerbalName);

            if (!RoleHoldsKerbal(action.KerbalRole))
                return;

            string recordingId = action.RecordingId;
            if (string.IsNullOrEmpty(recordingId)) return;

            // Look up recording metadata (skip if not found — orphaned action)
            RecordingMeta meta;
            if (!recordingMeta.TryGetValue(recordingId, out meta))
                return;

            string name = action.KerbalName;
            if (string.IsNullOrEmpty(name)) return;

            // A looped recording holds its crew exactly like a non-looping one, for the span
            // its real first run occupied (LOOP-RECORDING-CREW-NEVER-RESERVED; design 12.7,
            // operator ruling 2026-09-27: the per-recording Loop toggle is visual only). The
            // later loop replays are ghosts and reserve nobody: the hold is built once from
            // this row and the recording's own StartUT / EndUT, never per cycle.
            if (meta.IsLoop)
            {
                ParsekLog.Verbose(Tag,
                    $"Loop recording holds crew like its real first run: '{name}' recording '{recordingId}' " +
                    $"span={FormatClockUT(meta.StartUT)}..{FormatClockUT(meta.EndUT)} " +
                    $"endState={action.KerbalEndStateField} (loop replays are ghost-only, no per-cycle hold)");
            }

            // Build the all-crew set for O(1) lookup in ComputeRetiredSet
            allRecordingCrew.Add(name);
            HashSet<string> rawCrew;
            if (rawRecordingCrew.TryGetValue(recordingId, out rawCrew))
            {
                foreach (var rawName in rawCrew)
                    allRecordingCrew.Add(rawName);
            }

            KerbalEndState endState = action.KerbalEndStateField;

            // Map end states to reservation parameters:
            //   Dead      -> stock respawn on at the death (stamped on the recording):
            //                temporary, endUT = death UT (rec.EndUT) + stamped timer;
            //                respawn off or no stamp -> permanent,
            //                endUT = infinity (an open-ended co-row makes it permanent
            //                too, decided after the merge in ResolveOpenEndedRespawnDeaths)
            //   Recovered -> temporary, endUT = rec.EndUT
            //   Aboard    -> open-ended temporary, endUT = infinity (crew still on vessel)
            //   Unknown   -> open-ended temporary (conservative); Aboard / Unknown end at
            //                the split when a child of this recording carries the kerbal on
            // A loop changes none of this, on this recording or elsewhere in its chain: the
            // loop is visual only, the first run is the real flight and its tip spawns
            // (design 12.7, operator ruling 2026-09-27).
            bool dead = endState == KerbalEndState.Dead;
            double deathRespawnUT = double.NaN;
            string deathPolicy = null;
            if (dead)
            {
                deathRespawnUT = ResolveDeathRespawnUT(
                    meta.DeathRespawns, meta.DeathRespawnSeconds, meta.EndUT, out deathPolicy);
            }
            bool permanent = dead && double.IsNaN(deathRespawnUT);
            // Use recording's double-precision EndUT (not action's float EndUT)
            double endUT = endState == KerbalEndState.Recovered
                ? meta.EndUT : double.PositiveInfinity;
            if (dead && !permanent)
                endUT = deathRespawnUT;

            if (dead)
            {
                ParsekLog.Verbose(Tag,
                    permanent
                        ? $"Death hold: '{name}' recording '{recordingId}' is permanent " +
                          $"(respawn suppressed: {deathPolicy})"
                        : $"Death hold: '{name}' recording '{recordingId}' respawn scheduled " +
                          $"deathUT={FormatClockUT(meta.EndUT)} respawnUT={FormatClockUT(deathRespawnUT)} " +
                          $"timer={meta.DeathRespawnSeconds.ToString("R", CultureInfo.InvariantCulture)} " +
                          $"({deathPolicy})");
            }

            // Design 9.3: an open-ended (Aboard / Unknown) hold ends when the kerbal is
            // recovered from a real vessel continuing this flight's tree
            // (KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL).
            // A recording that ended at a split (staging, undock, EVA, dock) handed the kerbal
            // to the child of that branch point that carries him on: its Aboard / Unknown row
            // ends at the split and the child's own row (Dead with or without respawn,
            // Recovered, still Aboard) decides the rest. Without this the parent's Unknown
            // row, which starts before any death on the child, kept the hold open-ended.
            bool handedOff = false;
            if (!permanent
                && (endState == KerbalEndState.Aboard || endState == KerbalEndState.Unknown))
            {
                string handoffChild;
                double handoffUT = ResolveSplitHandoffUT(
                    splitHandoffs, meta.TreeId, meta.ChildBranchPointId, meta.EndUT, name,
                    out handoffChild);
                if (!double.IsPositiveInfinity(handoffUT))
                {
                    endUT = handoffUT;
                    handedOff = true;
                    ParsekLog.Verbose(Tag,
                        $"Reservation bounded by split handoff: '{name}' recording '{recordingId}' " +
                        $"({endState}) endUT={handoffUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"bp='{meta.ChildBranchPointId}' child='{handoffChild}'");
                }
            }

            // The recording flew on past a split whose child carried the kerbal away (a
            // decoupled crew cabin): its Unknown row ends at that child's start.
            if (!permanent && !handedOff && endState == KerbalEndState.Unknown)
            {
                List<string> splitBps;
                if (splitBranchPointsByParent.TryGetValue(recordingId, out splitBps))
                {
                    string midChild, midBp;
                    double windowStartUT = meta.StartUT;
                    double chainStartUT;
                    if (splitChainStartByRecording.TryGetValue(recordingId, out chainStartUT)
                        && (double.IsNaN(windowStartUT) || chainStartUT < windowStartUT))
                        windowStartUT = chainStartUT;
                    double midUT = ResolveMidFlightSplitHandoffUT(
                        splitHandoffs, meta.TreeId, splitBps, windowStartUT, meta.EndUT, name,
                        out midChild, out midBp);
                    if (!double.IsPositiveInfinity(midUT))
                    {
                        endUT = midUT;
                        handedOff = true;
                        ParsekLog.Verbose(Tag,
                            $"Reservation bounded by mid-flight split handoff: '{name}' recording '{recordingId}' " +
                            $"({endState}) endUT={midUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                            $"bp='{midBp}' child='{midChild}'");
                    }
                }
            }

            // A later dock claims the vessel this recording left the kerbal on, and the
            // docked recording carries him on (a rescue that docks and takes him aboard, in
            // this tree or another): the hold ends at the dock and the docking mission's own
            // rows decide the rest (CROSS-TREE-CREW-TRANSFER-STRANDS-RESERVATION). A later
            // mission that records the vessel itself (it joined that mission's tree, e.g.
            // after a `]` switch) claims it the same way at that recording's start
            // (GC6-FOREIGN-TREE-RECORDING-KEEPS-OPEN-HOLD).
            if (!permanent && !handedOff
                && (endState == KerbalEndState.Aboard || endState == KerbalEndState.Unknown))
            {
                DockClaim claim;
                double dockUT = ResolveDockClaimHandoffUT(
                    dockClaims, rowKerbalsByRecording, recordingId, meta.TreeId,
                    meta.VesselPid, meta.VesselGuid, meta.EndUT, name, out claim);
                if (!double.IsPositiveInfinity(dockUT))
                {
                    endUT = dockUT;
                    handedOff = true;
                    ParsekLog.Verbose(Tag,
                        claim.ForeignVesselRecording
                            ? $"Reservation bounded by foreign recording of the vessel: '{name}' recording '{recordingId}' " +
                              $"({endState}) endUT={dockUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                              $"tree='{claim.TreeId}' child='{claim.MergedChildRecordingId}' " +
                              $"pid={claim.TargetPid.ToString(CultureInfo.InvariantCulture)}"
                            : $"Reservation bounded by cross-tree dock handoff: '{name}' recording '{recordingId}' " +
                              $"({endState}) endUT={dockUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                              $"bp='{claim.BranchPointId}' tree='{claim.TreeId}' child='{claim.MergedChildRecordingId}'");
                }
            }

            if (!permanent && !handedOff
                && (endState == KerbalEndState.Aboard || endState == KerbalEndState.Unknown))
            {
                string closingOwner;
                bool closedByKscRetirement;
                double closedAtUT = ResolveRecoveryClosureUT(
                    name, recordingId, meta, out closingOwner, out closedByKscRetirement);
                if (!double.IsPositiveInfinity(closedAtUT))
                {
                    endUT = closedAtUT;
                    string closedBy = closedByKscRetirement ? "KSC retirement" : "recovery";
                    ParsekLog.Verbose(Tag,
                        $"Reservation bounded by {closedBy}: '{name}' recording '{recordingId}' " +
                        $"({endState}) endUT={closedAtUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"owner='{closingOwner}'");
                }
            }

            // An open-ended non-death row: remember when it starts, so the post-merge rule
            // can tell a row that spans the dead window from a post-respawn reuse.
            double openEndedStartUT = (!dead && double.IsPositiveInfinity(endUT))
                ? meta.StartUT : double.NaN;

            KerbalReservation existing;
            if (reservations.TryGetValue(name, out existing))
            {
                // Merge: take max endUT, permanent wins, latest respawn instant kept,
                // earliest open-ended co-row start kept
                if (permanent) existing.IsPermanent = true;
                if (touristRow) existing.IsTouristPassenger = true;
                if (!double.IsNaN(openEndedStartUT)
                    && (double.IsNaN(existing.OpenEndedCoRowStartUT)
                        || openEndedStartUT < existing.OpenEndedCoRowStartUT))
                    existing.OpenEndedCoRowStartUT = openEndedStartUT;
                if (endUT > existing.ReservedUntilUT) existing.ReservedUntilUT = endUT;
                if (!double.IsNaN(deathRespawnUT)
                    && (double.IsNaN(existing.DeathRespawnUT) || deathRespawnUT > existing.DeathRespawnUT))
                    existing.DeathRespawnUT = deathRespawnUT;
                existing.LatestRowStartUT = LaterRowStartUT(existing.LatestRowStartUT, meta.StartUT);
                ParsekLog.Verbose(Tag,
                    $"Reservation extended: '{name}' endUT->{existing.ReservedUntilUT:F1} " +
                    $"(permanent={existing.IsPermanent})" +
                    (double.IsNaN(existing.DeathRespawnUT)
                        ? ""
                        : $" respawnUT={FormatClockUT(existing.DeathRespawnUT)}"));
            }
            else
            {
                reservations[name] = new KerbalReservation
                {
                    KerbalName = name,
                    ReservedUntilUT = endUT,
                    IsPermanent = permanent,
                    DeathRespawnUT = deathRespawnUT,
                    OpenEndedCoRowStartUT = openEndedStartUT,
                    IsTouristPassenger = touristRow,
                    LatestRowStartUT = LaterRowStartUT(double.NaN, meta.StartUT)
                };
                ParsekLog.Verbose(Tag,
                    $"Reservation: '{name}' endUT={( permanent ? "INDEFINITE" : endUT.ToString("F1") )} " +
                    $"({endState}" +
                    $"{(dead && !permanent ? ", respawn at " + FormatClockUT(deathRespawnUT) : "")}), " +
                    $"recording '{recordingId}'" +
                    (touristRow ? " (tourist passenger: held, no stand-in)" : ""));
            }
        }

        /// <summary>
        /// Collects the <see cref="GameActionType.KerbalRecovered"/> rows of a walk's action
        /// list into <paramref name="into"/> (kerbal -> closures). Rows without a kerbal
        /// name, owner recording or finite UT are ignored. Returns the rows collected. Pure.
        /// </summary>
        internal static int CollectRecoveryClosures(
            IReadOnlyList<GameAction> actions,
            Dictionary<string, List<RecoveryClosure>> into)
        {
            if (actions == null || into == null) return 0;
            int collected = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.KerbalRecovered) continue;
                if (string.IsNullOrEmpty(a.KerbalName) || string.IsNullOrEmpty(a.RecordingId))
                    continue;
                if (double.IsNaN(a.UT) || double.IsInfinity(a.UT)) continue;

                List<RecoveryClosure> list;
                if (!into.TryGetValue(a.KerbalName, out list))
                {
                    list = new List<RecoveryClosure>();
                    into[a.KerbalName] = list;
                }
                list.Add(new RecoveryClosure { OwnerRecordingId = a.RecordingId, RecoveryUT = a.UT });
                collected++;
            }
            return collected;
        }

        /// <summary>
        /// Crew side of the KSC retirement ruling (operator, 2026-09-23): for every
        /// <see cref="GameActionType.KerbalAssignment"/> row of a retired recording
        /// (<paramref name="retiredEndUTByRecordingId"/>: recording id -> its EndUT) whose
        /// kerbal is still aboard at the end (Aboard, or Unknown), adds an in-memory closure
        /// owned by that recording at its EndUT. <see cref="RecoveryClosesHold"/> then ends
        /// the kerbal's open-ended hold from the retired recording itself and from the
        /// earlier segments of the same flight (same tree, ended by then) at EndUT, exactly
        /// as a <see cref="GameActionType.KerbalRecovered"/> row would, while a later flight
        /// keeps its own hold. Dead / Recovered rows and rows that hold nobody
        /// (<see cref="RoleHoldsKerbal(string)"/>) are untouched, no ledger row is written,
        /// and one closure is added per (kerbal, recording).
        ///
        /// <para>A kerbal whose committed timeline already records his recovery from this
        /// flight (a <see cref="GameActionType.KerbalRecovered"/> row that
        /// <see cref="RecoveryClosesHold"/> puts in the retired recording's scope: the vessel
        /// WAS real until the player recovered it, later than the trimmed EndUT) gets no
        /// synthetic closure and is bounded by that recorded recovery; the ruling covers a
        /// parked flight that never becomes a real vessel
        /// (<paramref name="recordedRecoveryWins"/>). A tourist is treated exactly like crew:
        /// held until his recorded recovery when there is one (a held tourist stays Assigned
        /// after a rewind, owner ruling 2026-10-25), else closed at EndUT, so no tourist hold
        /// is left without an end. <paramref name="treeIdByRecordingId"/>
        /// (committed recording id -> tree id) scopes a recovery row owned by another
        /// recording of the same tree; null scopes only the retired recording's own rows.
        /// Returns the closures added. Pure.</para>
        /// </summary>
        internal static int CollectKscRetirementClosures(
            IReadOnlyList<GameAction> actions,
            IReadOnlyDictionary<string, double> retiredEndUTByRecordingId,
            Dictionary<string, List<RecoveryClosure>> into,
            IReadOnlyDictionary<string, string> treeIdByRecordingId,
            out int recordedRecoveryWins)
        {
            recordedRecoveryWins = 0;
            if (actions == null || retiredEndUTByRecordingId == null || into == null)
                return 0;
            if (retiredEndUTByRecordingId.Count == 0)
                return 0;

            // The recorded recoveries, read from the actions (not from `into`, which may
            // already hold synthetic closures).
            var recorded = new Dictionary<string, List<RecoveryClosure>>(StringComparer.Ordinal);
            CollectRecoveryClosures(actions, recorded);

            int added = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.KerbalAssignment) continue;
                if (string.IsNullOrEmpty(a.KerbalName) || string.IsNullOrEmpty(a.RecordingId)) continue;
                if (!RoleHoldsKerbal(a.KerbalRole)) continue;
                if (a.KerbalEndStateField != KerbalEndState.Aboard
                    && a.KerbalEndStateField != KerbalEndState.Unknown)
                    continue;

                double endUT;
                if (!retiredEndUTByRecordingId.TryGetValue(a.RecordingId, out endUT)) continue;
                if (double.IsNaN(endUT) || double.IsInfinity(endUT)) continue;
                if (!seen.Add(a.KerbalName + "|" + a.RecordingId)) continue;

                double recoveryUT;
                string recoveryOwner;
                if (TryFindRecordedRecoveryInScope(recorded, a.KerbalName, a.RecordingId, endUT,
                        treeIdByRecordingId, out recoveryOwner, out recoveryUT))
                {
                    recordedRecoveryWins++;
                    ParsekLog.Verbose(Tag,
                        $"KSC retirement: '{a.KerbalName}' of recording '{a.RecordingId}' keeps his " +
                        $"recorded recovery (owner='{recoveryOwner}' " +
                        $"recoveryUT={recoveryUT.ToString("F1", CultureInfo.InvariantCulture)}) instead of " +
                        $"a closure at EndUT={endUT.ToString("F1", CultureInfo.InvariantCulture)}");
                    continue;
                }

                List<RecoveryClosure> list;
                if (!into.TryGetValue(a.KerbalName, out list))
                {
                    list = new List<RecoveryClosure>();
                    into[a.KerbalName] = list;
                }
                list.Add(new RecoveryClosure
                {
                    OwnerRecordingId = a.RecordingId,
                    RecoveryUT = endUT,
                    IsKscRetirement = true,
                });
                added++;
            }
            return added;
        }

        /// <summary>
        /// True when <paramref name="recorded"/> (kerbal -> recorded
        /// <see cref="GameActionType.KerbalRecovered"/> closures) holds a recovery of
        /// <paramref name="kerbalName"/> that <see cref="RecoveryClosesHold"/> puts in the
        /// scope of the hold from <paramref name="holdRecordingId"/> ending at
        /// <paramref name="holdEndUT"/>; reports the earliest such recovery. A recovery owned
        /// by a recording <paramref name="treeIdByRecordingId"/> does not know (not
        /// committed) counts only when it is the hold's own recording. Pure.
        /// </summary>
        internal static bool TryFindRecordedRecoveryInScope(
            IReadOnlyDictionary<string, List<RecoveryClosure>> recorded,
            string kerbalName,
            string holdRecordingId,
            double holdEndUT,
            IReadOnlyDictionary<string, string> treeIdByRecordingId,
            out string ownerRecordingId,
            out double recoveryUT)
        {
            ownerRecordingId = null;
            recoveryUT = double.PositiveInfinity;
            if (recorded == null || string.IsNullOrEmpty(kerbalName)) return false;
            List<RecoveryClosure> closures;
            if (!recorded.TryGetValue(kerbalName, out closures) || closures == null) return false;

            string holdTreeId = null;
            if (treeIdByRecordingId != null)
                treeIdByRecordingId.TryGetValue(holdRecordingId ?? "", out holdTreeId);
            for (int i = 0; i < closures.Count; i++)
            {
                var c = closures[i];
                if (c.IsKscRetirement) continue;
                string ownerTreeId = null;
                bool ownerKnown = treeIdByRecordingId != null
                    && treeIdByRecordingId.TryGetValue(c.OwnerRecordingId ?? "", out ownerTreeId);
                if (!ownerKnown
                    && !string.Equals(c.OwnerRecordingId, holdRecordingId, StringComparison.Ordinal))
                    continue;
                if (!RecoveryClosesHold(holdRecordingId, holdTreeId, holdEndUT,
                        c.OwnerRecordingId, ownerTreeId, c.RecoveryUT))
                    continue;
                if (c.RecoveryUT < recoveryUT)
                {
                    recoveryUT = c.RecoveryUT;
                    ownerRecordingId = c.OwnerRecordingId;
                }
            }
            return ownerRecordingId != null;
        }

        /// <summary>
        /// Does a recovery (owner recording <paramref name="ownerRecordingId"/> in tree
        /// <paramref name="ownerTreeId"/>, at <paramref name="recoveryUT"/>) end the
        /// open-ended hold a flight (<paramref name="holdRecordingId"/>, tree
        /// <paramref name="holdTreeId"/>, ending <paramref name="holdEndUT"/>) put on the
        /// recovered kerbal?
        ///
        /// <para>Scope is the owner's own mission: the owner recording itself, or any
        /// recording of the same tree (the chain segments and optimizer splits of one
        /// launch, an EVA that re-boarded, a crew transfer inside the mission). A flight in
        /// ANOTHER tree is never closed - a kerbal the player stranded on another mission
        /// stays stranded until that mission's own vessel comes home, and a stand-in whose
        /// recovered name reverse-maps to that owner cannot free him. The held flight must
        /// also have ENDED by the recovery (within
        /// <see cref="RecoveryClosureEndToleranceSeconds"/>): a later flight keeps its own
        /// hold and the reservation keeps its max-end merge. Pure.</para>
        /// </summary>
        internal static bool RecoveryClosesHold(
            string holdRecordingId,
            string holdTreeId,
            double holdEndUT,
            string ownerRecordingId,
            string ownerTreeId,
            double recoveryUT)
        {
            if (string.IsNullOrEmpty(holdRecordingId) || string.IsNullOrEmpty(ownerRecordingId))
                return false;
            if (double.IsNaN(recoveryUT) || double.IsInfinity(recoveryUT)) return false;
            if (double.IsNaN(holdEndUT) || holdEndUT > recoveryUT + RecoveryClosureEndToleranceSeconds)
                return false;
            if (string.Equals(holdRecordingId, ownerRecordingId, StringComparison.Ordinal))
                return true;
            return !string.IsNullOrEmpty(ownerTreeId)
                && string.Equals(holdTreeId, ownerTreeId, StringComparison.Ordinal);
        }

        /// <summary>
        /// The earliest recovery (this walk's <see cref="GameActionType.KerbalRecovered"/>
        /// rows) that closes <paramref name="kerbalName"/>'s open-ended hold from
        /// <paramref name="recordingId"/>, or +inf when none does. An owner recording that
        /// is no longer committed closes nothing.
        /// </summary>
        private double ResolveRecoveryClosureUT(
            string kerbalName, string recordingId, RecordingMeta holdMeta, out string closingOwner,
            out bool closedByKscRetirement)
        {
            closingOwner = null;
            closedByKscRetirement = false;
            List<RecoveryClosure> closures;
            if (!recoveryClosures.TryGetValue(kerbalName, out closures))
                return double.PositiveInfinity;

            double best = double.PositiveInfinity;
            for (int i = 0; i < closures.Count; i++)
            {
                var c = closures[i];
                RecordingMeta ownerMeta;
                if (!recordingMeta.TryGetValue(c.OwnerRecordingId, out ownerMeta))
                    continue;
                if (!RecoveryClosesHold(recordingId, holdMeta.TreeId, holdMeta.EndUT,
                        c.OwnerRecordingId, ownerMeta.TreeId, c.RecoveryUT))
                    continue;
                if (c.RecoveryUT < best)
                {
                    best = c.RecoveryUT;
                    closingOwner = c.OwnerRecordingId;
                    closedByKscRetirement = c.IsKscRetirement;
                }
            }
            return best;
        }

        /// <summary>
        /// Post-walk: builds replacement chains and computes retired set.
        /// Must run after all ProcessAction calls (needs complete reservation dict).
        /// </summary>
        public void PostWalk()
        {
            // Permanent-loss state is derived from the current reservation walk, not
            // sticky historical state. Rebuild it each pass from the current timeline.
            foreach (var slot in slots.Values)
                slot.OwnerPermanentlyGone = false;

            // 0. A respawn-on death merged with an open-ended co-row is a permanent loss
            // (decided on the merged set, so it never depends on row order).
            ResolveOpenEndedRespawnDeaths(reservations);

            // 1. Build/update chains for temporary reservations
            int permanentReservations = 0;
            int temporaryReservations = 0;
            int releasedReservations = 0;
            int respawnPendingReservations = 0;
            int slotsCreated = 0;
            int touristHolds = 0;
            foreach (var kvp in reservations)
            {
                if (kvp.Value.IsPermanent)
                {
                    permanentReservations++;
                    // Permanent: slot exits chain system. Mark owner as gone.
                    KerbalSlot permanentSlot;
                    if (slots.TryGetValue(kvp.Key, out permanentSlot))
                        permanentSlot.OwnerPermanentlyGone = true;
                    continue;
                }
                temporaryReservations++;

                // Released (design 9.3 / 9.4): the clock has reached the Recovered
                // flight's end (or a recorded death's stock respawn instant), so the owner
                // holds his own seat again. No slot is created for him and no chain depth
                // is demanded; an EXISTING slot keeps its chain names (a rewind before the
                // end reuses them), and ApplyToRoster's displacement pass deletes the
                // unused stand-in / retires a used one.
                if (!IsHoldInForce(kvp.Key, kvp.Value, walkClockUT))
                {
                    releasedReservations++;
                    continue;
                }

                // A recorded death waiting for its stock respawn: the owner is gone until
                // then exactly like a permanent death (stock leaves a dead or missing
                // kerbal unreplaced), so no stand-in is demanded; the walk that crosses the
                // respawn instant releases him through the branch above. Decided against
                // the walk's clock, so a LATER flight merged into the same hold (G6) does
                // not turn the dead window into an ordinary hold with a stand-in: before
                // the respawn he is Lost, from the respawn on the later flight holds him
                // as an ordinary reservation.
                if (IsLossHoldAt(kvp.Value, walkClockUT))
                {
                    respawnPendingReservations++;
                    if (!IsRespawnPendingHold(kvp.Value))
                    {
                        kvp.Value.LossAtWalkClock = true;
                        ParsekLog.Verbose(Tag,
                            $"Death hold: '{kvp.Key}' lost until the respawn although a later flight " +
                            $"extends the hold (respawnUT={FormatClockUT(kvp.Value.DeathRespawnUT)} " +
                            $"holdEndUT={FormatClockUT(kvp.Value.ReservedUntilUT)} " +
                            $"walkClockUT={FormatClockUT(walkClockUT)}); no stand-in until the respawn");
                    }
                    KerbalSlot goneSlot;
                    if (slots.TryGetValue(kvp.Key, out goneSlot))
                        goneSlot.OwnerPermanentlyGone = true;
                    continue;
                }

                // A tourist is a passenger of the flight that carried him: the hold keeps
                // him off a second vessel, but nobody stands in for him (a stand-in would be
                // a free hired kerbal, GC-12).
                if (kvp.Value.IsTouristPassenger)
                {
                    touristHolds++;
                    ParsekLog.Verbose(Tag,
                        $"Tourist hold: '{kvp.Key}' held until " +
                        $"{FormatClockUT(kvp.Value.ReservedUntilUT)} - passenger, no slot or stand-in");
                    continue;
                }

                // Ensure slot exists
                KerbalSlot slot;
                if (!slots.TryGetValue(kvp.Key, out slot))
                {
                    slot = new KerbalSlot
                    {
                        OwnerName = kvp.Key,
                        OwnerTrait = FindTraitForKerbal(kvp.Key),
                    };
                    slots[kvp.Key] = slot;
                    slotsCreated++;
                    ParsekLog.Verbose(Tag,
                        $"Created slot for '{kvp.Key}' (trait={slot.OwnerTrait})");
                }

                // Walk chain: ensure each reserved level has a stand-in
                EnsureChainDepth(slot);
            }

            // 2. Identify retired stand-ins
            ComputeRetiredSet();

            // 3. Time-based release bookkeeping: the one-per-transition release /
            // re-reserve lines, and the earliest pending release the scene-level
            // crossed-an-end checks compare the live clock with.
            bool provisional = nextWalkProvisional;
            nextWalkProvisional = false;
            if (!provisional)
                RecordReservationTransitions();
            nextReservationReleaseUT = ComputeNextReleaseUT(ActiveReservations.Values, walkClockUT);
            unchecked { postWalkCount++; }

            // 4. Log summary
            ParsekLog.Info(Tag,
                FormatPostWalkSummary(
                    reservations.Count,
                    permanentReservations,
                    temporaryReservations,
                    slots.Count,
                    retiredKerbals.Count,
                    slotsCreated,
                    releasedReservations,
                    respawnPendingReservations)
                + (touristHolds > 0
                    ? " touristHolds=" + touristHolds.ToString(CultureInfo.InvariantCulture)
                    : "")
                + (provisional ? " provisional=True" : "")
                + " walkClockUT=" + FormatClockUT(walkClockUT)
                + " nextReleaseUT=" + FormatClockUT(nextReservationReleaseUT));
        }

        /// <summary>
        /// The earliest finite end among the reservations still in force at
        /// <paramref name="nowUT"/> (all of the given ones when the clock is unknown; the
        /// walk passes its in-force view), or +inf when none will lapse by time alone. A
        /// recorded death's respawn instant strictly after a known clock counts as well,
        /// also when a later flight extends the hold past it (the Lost -> held-with-stand-in
        /// flip is a transition the clock must trigger). Pure.
        /// </summary>
        internal static double ComputeNextReleaseUT(
            IEnumerable<KerbalReservation> reservationSet, double nowUT)
        {
            double next = double.PositiveInfinity;
            if (reservationSet == null) return next;
            foreach (var reservation in reservationSet)
            {
                if (reservation == null || reservation.IsPermanent) continue;
                if (!IsReservationActiveAt(reservation, nowUT)) continue;
                double end = reservation.ReservedUntilUT;
                if (!double.IsNaN(end) && !double.IsInfinity(end) && end < next) next = end;
                // A respawn inside a hold a later flight extends (G6) is a transition too:
                // the kerbal stops reading Lost and a stand-in takes the later flight's
                // seat, so a clock crossing it must recalculate like a release. Only a
                // respawn strictly after a KNOWN clock: with the clock unknown the respawn
                // may already be behind the live clock, and a next-release at or before
                // now would re-trigger the crossed-release recalculation on every check
                // while walks keep reading no clock.
                double respawn = reservation.DeathRespawnUT;
                if (!double.IsNaN(respawn) && !double.IsInfinity(respawn)
                    && !double.IsNaN(nowUT) && nowUT < respawn
                    && respawn < next)
                    next = respawn;
            }
            return next;
        }

        /// <summary>
        /// Emits the release / re-reserve line once per ACTUAL transition of a kerbal's
        /// time-based hold, comparing with the state the previous clock-readable walk
        /// left. A walk whose clock is unknown changes nothing (it holds everyone, which
        /// is not a statement about the timeline). A name absent from this walk (a
        /// cutoff walk whose filtered list has not reached his flight yet) keeps its last
        /// state.
        /// </summary>
        private void RecordReservationTransitions()
        {
            if (double.IsNaN(walkClockUT)) return;
            foreach (var kvp in reservations)
            {
                var reservation = kvp.Value;
                if (reservation == null) continue;
                bool active = IsReservationActiveAt(reservation, walkClockUT);
                KnownHold last;
                bool known = lastKnownHold.TryGetValue(kvp.Key, out last);
                string transition = DescribeReservationTransition(known, last.Active, active);
                if (transition != null)
                {
                    ParsekLog.Info(Tag,
                        $"Reservation {transition}: '{kvp.Key}' " +
                        $"endUT={FormatClockUT(reservation.ReservedUntilUT)} " +
                        $"nowUT={FormatClockUT(walkClockUT)} " +
                        (active
                            ? (IsRespawnPendingHold(reservation)
                                ? "(held again at this clock: a rewind before the recorded death's stock respawn)"
                                : "(held again at this clock: a rewind before the end, or a later row extended the hold)")
                            : (IsRespawnPendingHold(reservation)
                                ? "(respawn applied: the clock has reached the recorded death's stock respawn instant - the kerbal is available again)"
                                : "(the clock has reached the Recovered flight's end - the kerbal is free again)")));
                }
                lastKnownHold[kvp.Key] = new KnownHold
                {
                    Active = active,
                    EndUT = reservation.ReservedUntilUT
                };
            }
        }

        /// <summary>
        /// Pure transition naming for <see cref="RecordReservationTransitions"/>:
        /// <c>released</c> when a hold that was in force (or never seen) is no longer,
        /// <c>re-reserved</c> when a released hold is in force again, else null.
        /// </summary>
        internal static string DescribeReservationTransition(bool known, bool wasActive, bool active)
        {
            if (!active && (!known || wasActive)) return "released";
            if (active && known && !wasActive) return "re-reserved";
            return null;
        }

        private static string FormatClockUT(double ut)
        {
            if (double.IsNaN(ut)) return "unknown";
            if (double.IsPositiveInfinity(ut)) return "INDEFINITE";
            return ut.ToString("F1", CultureInfo.InvariantCulture);
        }
    }
}
