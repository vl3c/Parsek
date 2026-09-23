using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure decisions behind the <see cref="GameActionType.KerbalRecovered"/> row: which
    /// committed recordings a recovered REAL vessel continues, and which of its crew have an
    /// open-ended (Aboard / Unknown) hold that the recovery ends
    /// (KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL; design 9.3, STRANDED "stays open
    /// until a rescue recording closes it").
    ///
    /// <para>The live shell is <c>LedgerOrchestrator.OnRealVesselCrewRecovered</c>; the walk
    /// side is <c>KerbalsModule.RecoveryClosesHold</c>, which this class reuses, plus the
    /// walk's two open-ended-hold exemptions it mirrors (a loop recording, and a chain with
    /// a looping segment, <see cref="IsInLoopingChain"/>), so the write decision and the
    /// reservation derivation do not disagree about which holds a row closes.</para>
    ///
    /// <para><b>Identity is POSITIVE.</b> Closing a hold RELEASES a kerbal, and before this
    /// row existed the answer was to leave the hold alone, so an unknown launch guid must
    /// not degrade to a bare pid match (the craft-baked pid is shared by every launch of
    /// the craft). The recorded-launch arm is
    /// <see cref="VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch"/>; the spawn arm
    /// accepts a GENUINE Parsek spawn only (a KSP-unique spawn pid, which is conclusive on
    /// its own). An adoption stamp (spawn pid == recorded pid) is the craft-baked pid again
    /// and so goes through the positive launch arm.</para>
    /// </summary>
    internal static class CrewRecoveryReservationClose
    {
        /// <summary>
        /// True when the live vessel (<paramref name="livePid"/>, launch guid
        /// <paramref name="liveGuid"/>) is the vessel <paramref name="rec"/> recorded
        /// (positive launch-guid match) or the vessel Parsek spawned from it (genuine spawn
        /// pid).
        /// </summary>
        internal static bool IsRecoveredVesselRecording(Recording rec, uint livePid, string liveGuid)
        {
            if (rec == null || livePid == 0) return false;
            uint spawnedPid = rec.SpawnedVesselPersistentId;
            if (spawnedPid != 0 && spawnedPid == livePid && spawnedPid != rec.VesselPersistentId)
                return true;
            return VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(rec, livePid, liveGuid);
        }

        /// <summary>
        /// The committed recordings the recovered vessel continues, one per tree: among the
        /// matches (<see cref="IsRecoveredVesselRecording"/>) that ended at or before the
        /// recovery, the one with the latest end in each tree (a recording with no tree is
        /// its own group). Ghost-only recordings have no career footprint and are skipped.
        /// Deterministic order: by end UT, then recording id.
        /// </summary>
        internal static List<Recording> SelectOwnerRecordings(
            IReadOnlyList<Recording> effectiveRecordings,
            uint livePid,
            string liveGuid,
            double recoveryUT)
        {
            var result = new List<Recording>();
            if (effectiveRecordings == null || livePid == 0) return result;
            if (double.IsNaN(recoveryUT) || double.IsInfinity(recoveryUT)) return result;

            var bestByGroup = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < effectiveRecordings.Count; i++)
            {
                var rec = effectiveRecordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (rec.IsGhostOnly) continue;
                if (!IsRecoveredVesselRecording(rec, livePid, liveGuid)) continue;
                if (rec.EndUT > recoveryUT + KerbalsModule.RecoveryClosureEndToleranceSeconds)
                    continue;

                string group = string.IsNullOrEmpty(rec.TreeId)
                    ? "rec:" + rec.RecordingId
                    : "tree:" + rec.TreeId;
                Recording current;
                if (!bestByGroup.TryGetValue(group, out current)
                    || rec.EndUT > current.EndUT
                    || (rec.EndUT == current.EndUT
                        && string.CompareOrdinal(rec.RecordingId, current.RecordingId) > 0))
                {
                    bestByGroup[group] = rec;
                }
            }

            foreach (var kvp in bestByGroup)
                result.Add(kvp.Value);
            result.Sort((a, b) =>
            {
                int c = a.EndUT.CompareTo(b.EndUT);
                return c != 0 ? c : string.CompareOrdinal(a.RecordingId, b.RecordingId);
            });
            return result;
        }

        /// <summary>One row to write, and how many open-ended holds it closes.</summary>
        internal struct ClosureRow
        {
            public GameAction Action;
            public int ClosedHolds;
        }

        /// <summary>
        /// Builds one <see cref="GameActionType.KerbalRecovered"/> row per (owner, recovered
        /// kerbal) that closes at least one open-ended hold: an effective
        /// <see cref="GameActionType.KerbalAssignment"/> row for that kerbal whose end state is
        /// <see cref="KerbalEndState.Aboard"/> or <see cref="KerbalEndState.Unknown"/>, on a
        /// non-tourist flight that is neither a loop nor a member of a chain with a looping
        /// segment (the walk keeps those holds open-ended, see
        /// <see cref="IsInLoopingChain"/>), and that
        /// <see cref="KerbalsModule.RecoveryClosesHold"/> puts in the owner's scope. A kerbal with nothing open-ended in scope (a
        /// Recovered flight, a death, a hold from another mission) gets no row.
        ///
        /// <para><paramref name="recoveredOwnerNames"/> are the recovered crew ALREADY
        /// reverse-mapped to reservation owners (a seated stand-in reads as the kerbal he
        /// stands in for, the name the assignment rows carry).</para>
        /// </summary>
        internal static List<ClosureRow> BuildClosureRows(
            IReadOnlyList<Recording> owners,
            IReadOnlyList<string> recoveredOwnerNames,
            IReadOnlyList<GameAction> effectiveActions,
            IReadOnlyList<Recording> effectiveRecordings,
            double recoveryUT)
        {
            var result = new List<ClosureRow>();
            if (owners == null || owners.Count == 0) return result;
            if (recoveredOwnerNames == null || recoveredOwnerNames.Count == 0) return result;
            if (effectiveActions == null || effectiveRecordings == null) return result;

            var recordingsById = new Dictionary<string, Recording>(StringComparer.Ordinal);
            var loopingChainIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < effectiveRecordings.Count; i++)
            {
                var rec = effectiveRecordings[i];
                if (rec != null && !string.IsNullOrEmpty(rec.RecordingId))
                    recordingsById[rec.RecordingId] = rec;
                if (rec != null && rec.LoopPlayback && rec.IsChainRecording)
                    loopingChainIds.Add(rec.ChainId);
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < recoveredOwnerNames.Count; i++)
            {
                if (!string.IsNullOrEmpty(recoveredOwnerNames[i]))
                    names.Add(recoveredOwnerNames[i]);
            }

            for (int o = 0; o < owners.Count; o++)
            {
                var owner = owners[o];
                if (owner == null || string.IsNullOrEmpty(owner.RecordingId)) continue;

                // Ordered by kerbal name so a multi-crew recovery writes a stable row order.
                var holdsByName = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var roleByName = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int a = 0; a < effectiveActions.Count; a++)
                {
                    var action = effectiveActions[a];
                    if (!IsOpenEndedHoldRow(action)) continue;
                    if (!names.Contains(action.KerbalName)) continue;

                    Recording held;
                    if (!recordingsById.TryGetValue(action.RecordingId, out held)) continue;
                    if (held.LoopPlayback) continue;
                    if (IsInLoopingChain(held, loopingChainIds)) continue;
                    if (!KerbalsModule.RecoveryClosesHold(
                            held.RecordingId, held.TreeId, held.EndUT,
                            owner.RecordingId, owner.TreeId, recoveryUT))
                        continue;

                    int count;
                    holdsByName.TryGetValue(action.KerbalName, out count);
                    holdsByName[action.KerbalName] = count + 1;
                    if (!roleByName.ContainsKey(action.KerbalName))
                        roleByName[action.KerbalName] = action.KerbalRole;
                }

                foreach (var kvp in holdsByName)
                {
                    string role;
                    roleByName.TryGetValue(kvp.Key, out role);
                    result.Add(new ClosureRow
                    {
                        Action = new GameAction
                        {
                            UT = recoveryUT,
                            Type = GameActionType.KerbalRecovered,
                            RecordingId = owner.RecordingId,
                            KerbalName = kvp.Key,
                            KerbalRole = role
                        },
                        ClosedHolds = kvp.Value
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// The walk's looping-chain override (<c>KerbalsModule.ProcessAction</c>'s
        /// <c>chainHasLoop</c>): a recording in a chain that has a looping segment keeps
        /// its hold at +inf whatever a recovery says, because the ghost replays past its
        /// end. The writer mirrors it so it never logs a closure that changes nothing.
        /// </summary>
        internal static bool IsInLoopingChain(Recording rec, HashSet<string> loopingChainIds)
        {
            return rec != null && rec.IsChainRecording
                && loopingChainIds != null && loopingChainIds.Contains(rec.ChainId);
        }

        /// <summary>
        /// The assignment rows the kerbals walk turns into an open-ended hold: a
        /// non-tourist <see cref="GameActionType.KerbalAssignment"/> with an owner
        /// recording and an Aboard / Unknown end state.
        /// </summary>
        internal static bool IsOpenEndedHoldRow(GameAction action)
        {
            if (action == null || action.Type != GameActionType.KerbalAssignment) return false;
            if (string.IsNullOrEmpty(action.RecordingId) || string.IsNullOrEmpty(action.KerbalName))
                return false;
            if (string.Equals(action.KerbalRole, "Tourist", StringComparison.OrdinalIgnoreCase))
                return false;
            return action.KerbalEndStateField == KerbalEndState.Aboard
                || action.KerbalEndStateField == KerbalEndState.Unknown;
        }
    }
}
