using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The candidate half of the recovery-owner pick
    /// (<see cref="LedgerOrchestrator.PickRecoveryRecording"/>): the identity predicates, the
    /// stage-1 launch-guid filter, the one-walk candidate scan, the identity-first path choice
    /// and the UT tier walk. Every input arrives as a parameter - the committed list, the
    /// session provisional id and the admissible id set are read by the caller - so this type
    /// holds no state and reads no store, scenario or ledger. Its log lines keep the
    /// LedgerOrchestrator subsystem tag.
    /// </summary>
    internal static class RecoveryRecordingPicker
    {
        private const string Tag = "LedgerOrchestrator";

        /// <summary>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 1: does a name-matching
        /// candidate recording belong to a launch the recovering vessel CONCLUSIVELY is not?
        ///
        /// <para>
        /// The predicate is <see cref="VesselLaunchIdentity.GuidsConclusivelyDiffer"/> and
        /// nothing stricter, on purpose. <c>persistentId</c> is craft-baked and reused on
        /// every launch of a craft, so it cannot separate two launches of one craft name; the
        /// launch <c>Guid</c> can. But the recovery seam cannot PROMISE a guid on either side
        /// (a pre-guid recording carries none; a recovery reached through a path with no
        /// <c>ProtoVessel</c> supplies none), so
        /// <c>ResurrectionRetirementEligibility.IsPositivelySameLaunch</c> - which requires a
        /// known guid on BOTH sides - is the wrong shape here: it would retire the
        /// correlation for every legacy recording, trading a rare wrong pick for a common
        /// missing one. An unknown guid on either side is NOT conclusive, so the candidate
        /// survives and the picker behaves exactly as it did before this filter existed.
        /// </para>
        ///
        /// <para>
        /// The Re-Fly provisional is safe by construction, not by exemption:
        /// <c>RewindInvoker.CopyInheritedIdentityForFork</c> inherits the origin's
        /// <c>RecordedVesselGuid</c> (the fork restores from a quicksave that preserves the
        /// origin's <c>Vessel.id</c>), and a provisional built before that copy carries a
        /// null guid - both non-conclusive, so the session-aware NotCommitted rule above is
        /// untouched.
        /// </para>
        /// </summary>
        internal static bool IsConclusiveLaunchGuidMismatch(Recording rec, string liveLaunchGuid)
        {
            if (rec == null) return false;
            return VesselLaunchIdentity.GuidsConclusivelyDiffer(
                rec.RecordedVesselGuid, liveLaunchGuid);
        }

        /// <summary>
        /// Applies <see cref="IsConclusiveLaunchGuidMismatch"/> to an ordered candidate set,
        /// returning the survivors IN INPUT ORDER and reporting how many were dropped.
        ///
        /// <para>
        /// <b>MONOTONE by construction.</b> The result is a subsequence of the input: the
        /// filter can only REMOVE candidates, never add or reorder one. That is what makes it
        /// the safe half of the fix - a recovery whose pick was already correct cannot be
        /// turned into a wrong pick by it, only into the same pick or (when every candidate
        /// is a conclusively different launch) into the existing
        /// <c>reason=no-recovery-recording</c> refusal. It runs BEFORE tier selection, so the
        /// bracketing / most-recent-ended / global-latest walk is unchanged and simply sees a
        /// smaller set.
        /// </para>
        ///
        /// <para>
        /// A null/empty <paramref name="liveLaunchGuid"/> is the documented no-op: every
        /// candidate survives and <paramref name="dropped"/> is 0.
        /// </para>
        /// </summary>
        internal static List<Recording> FilterRecoveryCandidatesByLaunchGuid(
            IReadOnlyList<Recording> candidates, string liveLaunchGuid, out int dropped)
        {
            dropped = 0;
            var survivors = new List<Recording>(candidates?.Count ?? 0);
            if (candidates == null) return survivors;

            for (int i = 0; i < candidates.Count; i++)
            {
                var rec = candidates[i];
                if (IsConclusiveLaunchGuidMismatch(rec, liveLaunchGuid))
                {
                    dropped++;
                    continue;
                }
                survivors.Add(rec);
            }
            return survivors;
        }

        /// <summary>
        /// The active Re-Fly session's provisional recording, IF it is present in
        /// <paramref name="candidates"/>, else null.
        ///
        /// <para>
        /// <b>Callers must pass the POST-FILTER candidate set.</b> This exists so that
        /// "the session provisional is admitted" means admitted to the set the tier walk
        /// actually walks, not merely to the name-match set. A tier tie-break that preferred
        /// a provisional resolved from the pre-filter set could reinstate a candidate
        /// <see cref="FilterRecoveryCandidatesByLaunchGuid"/> removed, which would break the
        /// filter's monotone property - the property the whole stage-1 safety argument rests
        /// on (KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY).
        /// </para>
        /// </summary>
        internal static Recording FindSessionProvisionalAmong(
            IReadOnlyList<Recording> candidates, string sessionProvisionalId)
        {
            if (candidates == null || string.IsNullOrEmpty(sessionProvisionalId))
                return null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var rec = candidates[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, sessionProvisionalId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 3: does <paramref name="rec"/>
        /// POSITIVELY carry the recovering vessel's launch guid? Both sides must be known and
        /// equal (format-insensitive). <c>persistentId</c> is deliberately not consulted: the
        /// launch guid is launch-unique on its own, and the pid is craft-baked.
        /// </summary>
        internal static bool IsPositiveLaunchGuidMatch(Recording rec, string liveLaunchGuid)
        {
            if (rec == null) return false;
            string live = VesselLaunchIdentity.NormalizeGuid(liveLaunchGuid);
            if (live == null) return false;
            string recorded = VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid);
            if (recorded == null) return false;
            return string.Equals(recorded, live, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 3: is the recovering vessel
        /// (<paramref name="livePid"/>) a GENUINE Parsek spawn of <paramref name="rec"/>? A
        /// genuine spawn's pid is KSP-unique, so the match is conclusive on its own; an
        /// adoption stamp (<c>SpawnedVesselPersistentId == VesselPersistentId</c>) is the
        /// craft-baked pid again and is refused here - a same-launch adoption is reached by
        /// the launch-guid path instead. Mirrors the spawn arm of
        /// <c>CrewRecoveryReservationClose.IsRecoveredVesselRecording</c>.
        /// </summary>
        internal static bool IsGenuineSpawnPidMatch(Recording rec, uint livePid)
        {
            if (rec == null || livePid == 0) return false;
            uint spawnedPid = rec.SpawnedVesselPersistentId;
            return spawnedPid != 0
                && spawnedPid == livePid
                && spawnedPid != rec.VesselPersistentId;
        }

        /// <summary>
        /// Pass 1 of <see cref="LedgerOrchestrator.PickRecoveryRecording"/>: one walk over the store sorts every
        /// eligible recording into the name, launch-guid and spawn-pid match lists (store order),
        /// counts the zombie provisionals per key, and logs how many matches the effective-timeline
        /// filter skipped. The active session's provisional is admitted by id.
        /// </summary>
        internal static void ScanRecoveryCandidates(
            IReadOnlyList<Recording> recordings,
            RecoveredVesselIdentity identity,
            double ut,
            string sessionProvisionalId,
            HashSet<string> admissible,
            out List<Recording> nameMatches,
            out List<Recording> guidMatches,
            out List<Recording> spawnMatches,
            out Recording unknownGuidSessionProvisional,
            out int zombieByName,
            out int zombieByGuid,
            out int zombieBySpawn)
        {
            // Pass 1: ONE walk over the store classifies every eligible recording against
            // the three identity keys, in store order. KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-
            // UT-ONLY stage 3 (operator principle 2026-10-01, "we should have unique
            // identities"): the launch guid decides first, the genuine spawn pid second, and
            // the vessel NAME only when neither identity reaches any recording.
            string liveGuid = VesselLaunchIdentity.NormalizeGuid(identity.LaunchGuid);
            uint livePid = identity.PersistentId;
            nameMatches = new List<Recording>();
            guidMatches = new List<Recording>();
            spawnMatches = new List<Recording>();
            unknownGuidSessionProvisional = null;
            zombieByName = 0; zombieByGuid = 0; zombieBySpawn = 0;
            int skippedNotEffective = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null) continue;
                bool nameMatch = identity.MatchesName(rec.VesselName);
                bool guidMatch = IsPositiveLaunchGuidMatch(rec, liveGuid);
                bool spawnMatch = IsGenuineSpawnPidMatch(rec, livePid);
                bool isSessionProvisional =
                    !string.IsNullOrEmpty(sessionProvisionalId)
                    && string.Equals(rec.RecordingId, sessionProvisionalId, StringComparison.Ordinal);
                if (rec.MergeState == MergeState.NotCommitted && !isSessionProvisional)
                {
                    // Zombie awaiting LoadTimeSweep. Counted per key, not logged per-item.
                    if (nameMatch) zombieByName++;
                    if (guidMatch) zombieByGuid++;
                    if (spawnMatch) zombieBySpawn++;
                    continue;
                }
                // Superseded and rewind-retired recordings are off the effective timeline.
                // After a Re-Fly merge the superseded origin and its TIP keep the fork's
                // launch guid, so without this gate a later recovery could credit them.
                if (!isSessionProvisional
                    && (string.IsNullOrEmpty(rec.RecordingId) || !admissible.Contains(rec.RecordingId)))
                {
                    if (nameMatch || guidMatch || spawnMatch) skippedNotEffective++;
                    continue;
                }
                if (nameMatch) nameMatches.Add(rec);
                if (guidMatch) guidMatches.Add(rec);
                if (spawnMatch) spawnMatches.Add(rec);
                if (isSessionProvisional && nameMatch
                    && VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid) == null)
                {
                    unknownGuidSessionProvisional = rec;
                }
            }

            if (skippedNotEffective > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PickRecoveryRecordingId effective filter: {identity.FormatForLog()} " +
                    $"ut={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"skippedNotEffective={skippedNotEffective.ToString(CultureInfo.InvariantCulture)} " +
                    "reason=superseded-or-rewind-retired");
            }
        }

        /// <summary>
        /// Chooses the candidate set the UT tiers of <see cref="LedgerOrchestrator.PickRecoveryRecording"/> walk:
        /// the launch-guid matches first, else the spawn-pid matches, else the name matches after
        /// the stage-1 guid filter. Logs the path line and returns the path.
        /// </summary>
        internal static RecoveryPickPath SelectRecoveryCandidatePath(
            RecoveredVesselIdentity identity,
            string utLog,
            List<Recording> nameMatches,
            List<Recording> guidMatches,
            List<Recording> spawnMatches,
            Recording unknownGuidSessionProvisional,
            int zombieByName,
            int zombieByGuid,
            int zombieBySpawn,
            out List<Recording> candidates,
            out int guidDropped,
            out int skippedZombieNotCommitted)
        {
            uint livePid = identity.PersistentId;
            RecoveryPickPath path;
            guidDropped = 0;
            if (guidMatches.Count > 0)
            {
                // THE LAUNCH-GUID PATH. Names are ignored: a vessel renamed after its
                // recording captured the name still reaches its own recordings, and a
                // same-name recording of a DIFFERENT launch never enters the set. The one
                // exception is the active Re-Fly session's provisional while its own guid is
                // still unknown (RewindInvoker's placeholder, or a fork whose origin predates
                // the guid): it is admitted on name, the legacy fallback for an id-less
                // member, so the TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT tie-break below can
                // still see it. A provisional with a KNOWN guid is already in guidMatches or
                // belongs to a different launch.
                path = RecoveryPickPath.LaunchGuid;
                candidates = guidMatches;
                if (unknownGuidSessionProvisional != null)
                    candidates.Add(unknownGuidSessionProvisional);
                skippedZombieNotCommitted = zombieByGuid;
                int nameMismatch = 0;
                for (int i = 0; i < guidMatches.Count; i++)
                    if (!identity.MatchesName(guidMatches[i].VesselName)) nameMismatch++;
                int nameOnly = 0;
                for (int i = 0; i < nameMatches.Count; i++)
                    if (!candidates.Contains(nameMatches[i])) nameOnly++;
                ParsekLog.Info(Tag,
                    $"PickRecoveryRecordingId path: {identity.FormatForLog()} ut={utLog} " +
                    $"path={RecoveryPickAmbiguity.PathToken(path)} " +
                    $"identityMatches={guidMatches.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"identityNameMismatch={nameMismatch.ToString(CultureInfo.InvariantCulture)} " +
                    $"nameOnlyIgnored={nameOnly.ToString(CultureInfo.InvariantCulture)} " +
                    $"unknownGuidSessionProvisionalAdmitted={unknownGuidSessionProvisional != null}");
            }
            else if (spawnMatches.Count > 0)
            {
                // THE SPAWN-PID PATH. A genuine Parsek spawn regenerates the vessel guid, so
                // the guid reaches nothing, but the KSP-unique spawn pid names the recording
                // that spawned it - the same arm CrewRecoveryReservationClose uses.
                path = RecoveryPickPath.SpawnPid;
                candidates = spawnMatches;
                skippedZombieNotCommitted = zombieBySpawn;
                ParsekLog.Info(Tag,
                    $"PickRecoveryRecordingId path: {identity.FormatForLog()} ut={utLog} " +
                    $"path={RecoveryPickAmbiguity.PathToken(path)} " +
                    $"identityMatches={spawnMatches.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"spawnPid={livePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"nameMatches={nameMatches.Count.ToString(CultureInfo.InvariantCulture)}");
            }
            else
            {
                // THE NAME FALLBACK - legacy id-less data, or a vessel no recording knows by
                // identity. Exactly the stage-1 walk: name, then the guid filter (which can
                // still drop a same-name recording of a conclusively different launch), then
                // the tiers; the XP leg keeps the stage-2 ambiguity refusal.
                path = RecoveryPickPath.NameFallback;
                skippedZombieNotCommitted = zombieByName;
                if (nameMatches.Count > 0 || identity.HasLaunchGuid)
                {
                    ParsekLog.Info(Tag,
                        $"PickRecoveryRecordingId path: {identity.FormatForLog()} ut={utLog} " +
                        $"path={RecoveryPickAmbiguity.PathToken(path)} " +
                        $"reason={(identity.HasLaunchGuid ? "no-recording-carries-launch-guid" : "live-launch-guid-unknown")} " +
                        $"nameMatches={nameMatches.Count.ToString(CultureInfo.InvariantCulture)}");
                }

                // Stage 1. Drop candidates whose recorded launch guid conclusively differs
                // from the recovering vessel's. Monotone and BEFORE the tier walk.
                candidates = FilterRecoveryCandidatesByLaunchGuid(
                    nameMatches, identity.LaunchGuid, out guidDropped);
            }
            return path;
        }

        /// <summary>
        /// The stage-1 guid filter's outcome line of <see cref="LedgerOrchestrator.PickRecoveryRecording"/>. Only
        /// the name fallback runs the filter, so the identity paths write nothing here.
        /// </summary>
        internal static void LogRecoveryGuidFilterOutcome(
            RecoveredVesselIdentity identity,
            string utLog,
            RecoveryPickPath path,
            int guidDropped,
            int candidateCount,
            int nameMatchCount)
        {
            // ONE LINE PER PICK, NOT PER RECOVERY. The funds and XP legs pick once, but the
            // science leg picks once per science subject, so a science-heavy recovery emits
            // one of these per subject. That is bounded by the subject count and each line is
            // independently true, so it is left un-rate-limited rather than collapsed - a
            // dropped candidate is an attribution-changing decision on every leg that makes it.
            // Only the name fallback runs the stage-1 filter, so only it logs it.
            if (path == RecoveryPickPath.NameFallback && guidDropped > 0)
            {
                ParsekLog.Info(Tag,
                    $"PickRecoveryRecordingId guid filter: {identity.FormatForLog()} " +
                    $"ut={utLog} " +
                    $"dropped={guidDropped.ToString(CultureInfo.InvariantCulture)} " +
                    $"remaining={candidateCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"reason=guid-conclusive-mismatch");
            }
            else if (path == RecoveryPickPath.NameFallback && nameMatchCount > 0)
            {
                // Nothing dropped - but WHY is the load-bearing half, and a silent no-drop
                // cannot be told apart from a filter that never ran.
                ParsekLog.Verbose(Tag,
                    $"PickRecoveryRecordingId guid filter: {identity.FormatForLog()} " +
                    $"ut={utLog} " +
                    $"dropped=0 remaining={candidateCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"reason={(identity.HasLaunchGuid ? "no-conclusive-mismatch" : "live-launch-guid-unknown")}");
            }
        }

        /// <summary>
        /// The UT tier walk of <see cref="LedgerOrchestrator.PickRecoveryRecording"/>: tier 1 the bracketing
        /// candidate with the largest EndUT (the admitted session provisional, by reference,
        /// as the tie-break), tier 2 the one that ended most recently at or before ut, tier 3
        /// the global latest. Ties keep the first in candidate order. Pure.
        /// </summary>
        internal static void WalkRecoveryPickTiers(
            List<Recording> candidates,
            double ut,
            Recording admittedSessionProvisional,
            out Recording bracketing,
            out Recording bracketingSessionProvisional,
            out Recording mostRecentEnded,
            out Recording globalLatest)
        {
            bracketing = null;                   // tier 1 (largest EndUT)
            bracketingSessionProvisional = null; // tier 1 tie-break override
            mostRecentEnded = null;              // tier 2
            globalLatest = null;                 // tier 3

            for (int i = 0; i < candidates.Count; i++)
            {
                var rec = candidates[i];
                double startUT = rec.StartUT;
                double endUT = rec.EndUT;

                // Tier 1: brackets ut
                if (startUT <= ut && ut <= endUT)
                {
                    if (bracketing == null || endUT > bracketing.EndUT)
                        bracketing = rec;
                    // TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT: the live session's
                    // provisional outranks largest-EndUT inside tier 1. It is the fork
                    // that SURVIVES the merge, so a mid-session payout tagged to it is
                    // out of the supersede subtree instead of being tombstoned away.
                    // Identity comes from `admittedSessionProvisional`, which is a member
                    // of `candidates` BY CONSTRUCTION - see the post-filter note above.
                    // ReferenceEquals, deliberately: an id comparison here would re-derive
                    // the provisional inside the tier walk and could match a recording the
                    // guid filter dropped, which is exactly what that note forbids.
                    if (ReferenceEquals(rec, admittedSessionProvisional))
                        bracketingSessionProvisional = rec;
                }

                // Tier 2: ended at or before ut
                if (endUT <= ut)
                {
                    if (mostRecentEnded == null || endUT > mostRecentEnded.EndUT)
                        mostRecentEnded = rec;
                }

                // Tier 3: global latest
                if (globalLatest == null || endUT > globalLatest.EndUT)
                    globalLatest = rec;
            }
        }
    }
}
