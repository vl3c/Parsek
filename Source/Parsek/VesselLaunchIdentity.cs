using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Launch-unique vessel identity helpers.
    ///
    /// KSP bakes <c>persistentId</c> into the <c>.craft</c> file and reuses it verbatim on every
    /// launch of that craft (vessel AND part pids), regenerating only against a currently-live
    /// vessel. Parsek stores many historical recordings of the same craft (each carrying the baked
    /// pid), invisible to KSP's live-dedup, so a fresh launch's pid collides with prior recordings
    /// of the same craft. A bare <c>persistentId</c> match therefore cannot tell two launches apart.
    ///
    /// KSP's <c>Vessel.id</c> (a Guid) IS launch-unique: assigned fresh per launch, never stored in
    /// the <c>.craft</c>. Parsek captures it as <see cref="Recording.RecordedVesselGuid"/> (and
    /// backfills it from the snapshot's <c>pid</c> value for older recordings).
    ///
    /// These predicates treat the Guid as a POSITIVE disambiguator: a <c>persistentId</c> match is
    /// an identity match UNLESS a known (non-empty) Guid on both sides conclusively disagrees. When
    /// either Guid is empty (legacy / un-backfillable recording) the predicate falls back to
    /// pid-only behavior, so nothing that worked before a recording carried a Guid regresses.
    /// Chain-continuation segments of one launch share a Guid; distinct launches of one craft differ.
    /// </summary>
    internal static class VesselLaunchIdentity
    {
        /// <summary>
        /// Normalizes a vessel Guid string to canonical "N" form (32 lowercase hex, no dashes) for
        /// comparison. Returns null for null/empty; returns the trimmed input unchanged when it is
        /// not a parseable Guid (so a malformed value still compares equal to itself).
        /// </summary>
        internal static string NormalizeGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string trimmed = guid.Trim();
            if (trimmed.Length == 0) return null;
            return Guid.TryParse(trimmed, out Guid parsed)
                ? parsed.ToString("N", CultureInfo.InvariantCulture)
                : trimmed;
        }

        /// <summary>
        /// True only when two launch Guids conclusively identify DIFFERENT launches: both are known
        /// (non-empty) and they differ after normalization. An empty/unknown Guid on either side is
        /// never conclusive (returns false), preserving pid-only fallback behavior.
        /// </summary>
        internal static bool GuidsConclusivelyDiffer(string a, string b)
        {
            string na = NormalizeGuid(a);
            string nb = NormalizeGuid(b);
            if (na == null || nb == null) return false;
            return !string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when a live vessel (its <c>persistentId</c> and <c>Vessel.id</c> Guid) is the same
        /// launch the recording captured: the persistentId must match AND the launch Guids must not
        /// conclusively differ. Used at the live-vessel-vs-recording sites (spawn adoption, crew-swap
        /// skip, committed-tree restore, tracking-station dedup) to reject a relaunch of the same craft.
        /// </summary>
        internal static bool LiveVesselIsRecordedLaunch(Recording rec, uint livePid, string liveGuid)
        {
            if (rec == null || livePid == 0) return false;
            if (rec.VesselPersistentId == 0 || rec.VesselPersistentId != livePid) return false;
            return !GuidsConclusivelyDiffer(rec.RecordedVesselGuid, liveGuid);
        }

        /// <summary>
        /// True when two recordings belong to the same physical launch: same <c>persistentId</c> AND
        /// launch Guids that do not conclusively differ. Used at the recording-vs-recording sites
        /// (rewind spawn-suppression, supersede terminal-spawn marking, chain-walker claim pooling)
        /// to keep one launch's continuation segments together while separating distinct launches.
        /// </summary>
        internal static bool RecordingsShareLaunch(Recording a, Recording b)
        {
            if (a == null || b == null) return false;
            if (a.VesselPersistentId == 0 || a.VesselPersistentId != b.VesselPersistentId) return false;
            return !GuidsConclusivelyDiffer(a.RecordedVesselGuid, b.RecordedVesselGuid);
        }

        /// <summary>
        /// True when two recordings record the same physical vessel by launch identity: when
        /// both launch Guids are known they decide alone (equal = same vessel, whatever each
        /// recording is named; different = never, even under the same name). Only when a Guid
        /// is unknown on either side (guid-less legacy data) does the name decide, which is
        /// what every such site did before launch identity existed. Unlike
        /// <see cref="RecordingsShareLaunch"/> the pid is not consulted: a resumed spawned
        /// vessel's recording carries the spawn pid with its original guid, and the guid is
        /// launch-unique on its own.
        /// </summary>
        internal static bool RecordingsShareVesselIdentity(Recording a, Recording b)
        {
            if (a == null || b == null) return false;
            string ga = NormalizeGuid(a.RecordedVesselGuid);
            string gb = NormalizeGuid(b.RecordedVesselGuid);
            if (ga != null && gb != null)
                return string.Equals(ga, gb, StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrEmpty(a.VesselName)
                && string.Equals(a.VesselName, b.VesselName, StringComparison.Ordinal);
        }

        /// <summary>
        /// The GENUINE-spawn branch of <see cref="LiveVesselIsRecordedSpawn"/> alone: the pid is
        /// the recording's spawn pid and that spawn pid is NOT an adoption stamp (it differs from
        /// the recorded pid), so it was minted KSP-unique by a Parsek spawn and the match is
        /// conclusive. False for an adoption stamp, whatever the guids say.
        /// </summary>
        internal static bool IsGenuineSpawnPidMatch(Recording rec, uint candidatePid)
        {
            if (rec == null || candidatePid == 0) return false;
            uint spawnedPid = rec.SpawnedVesselPersistentId;
            if (spawnedPid == 0 || spawnedPid != candidatePid) return false;
            return !(rec.VesselPersistentId != 0 && spawnedPid == rec.VesselPersistentId);
        }

        /// <summary>
        /// True when a candidate vessel (its <c>persistentId</c> and <c>Vessel.id</c> Guid) is the
        /// SAME launch as the recording's SPAWN / adoption endpoint, so a revert/rewind strip may
        /// remove it. Genuine Parsek spawns use a KSP-unique spawn pid (so
        /// <c>SpawnedVesselPersistentId != VesselPersistentId</c>) and a bare spawn-pid match is
        /// conclusive. Adoption stamps (<c>SpawnedVesselPersistentId == VesselPersistentId</c>) reuse
        /// the craft-baked source pid on every launch of the craft, so they additionally require the
        /// launch Guid to NOT conclusively differ: a known Guid mismatch means a DIFFERENT real launch
        /// that merely reuses the baked pid (BUG-H), which must never be deleted in place of the
        /// recording's spawned vessel.
        /// </summary>
        internal static bool LiveVesselIsRecordedSpawn(Recording rec, uint candidatePid, string candidateGuid)
        {
            if (rec == null || candidatePid == 0) return false;
            uint spawnedPid = rec.SpawnedVesselPersistentId;
            if (spawnedPid == 0 || spawnedPid != candidatePid) return false;

            bool adoptionStamp = rec.VesselPersistentId != 0 && spawnedPid == rec.VesselPersistentId;
            if (adoptionStamp)
                return !GuidsConclusivelyDiffer(rec.RecordedVesselGuid, candidateGuid);

            // Genuine Parsek spawn: the spawn pid is KSP-unique, so a pid match is conclusive.
            return true;
        }

        /// <summary>
        /// Pure core of the spawn-stamp readers' launch test: the live vessel's guid is the
        /// recording's launch when the guids do not conclusively differ, OR when the live guid
        /// is linked to the recording's guid (<paramref name="liveGuidLinkedToRecording"/>, the
        /// link rows a Parsek spawn or a resume writes). The link matters for a RESUMED spawned
        /// vessel: the resume re-pids the recording to the spawn pid and a later commit
        /// re-installs the spawn claim, so the recording reads as an adoption stamp (spawned pid
        /// == recorded pid) while the vessel carries the spawn's fresh guid.
        /// </summary>
        internal static bool LiveGuidIsRecordedLaunch(
            string recordedGuid, string liveGuid, bool liveGuidLinkedToRecording)
        {
            return !GuidsConclusivelyDiffer(recordedGuid, liveGuid) || liveGuidLinkedToRecording;
        }

        /// <summary>
        /// Pure core of <see cref="SpawnStampHeldBy"/>: <see cref="LiveVesselIsRecordedSpawn"/>
        /// with the adoption-stamp branch's guid test widened by
        /// <paramref name="liveGuidLinkedToRecording"/> (see <see cref="LiveGuidIsRecordedLaunch"/>).
        /// </summary>
        internal static bool SpawnStampHeldByCore(
            Recording rec, uint candidatePid, string candidateGuid, bool liveGuidLinkedToRecording)
        {
            if (rec == null || candidatePid == 0) return false;
            uint spawnedPid = rec.SpawnedVesselPersistentId;
            if (spawnedPid == 0 || spawnedPid != candidatePid) return false;
            bool adoptionStamp = rec.VesselPersistentId != 0 && spawnedPid == rec.VesselPersistentId;
            if (!adoptionStamp) return true;
            return LiveGuidIsRecordedLaunch(rec.RecordedVesselGuid, candidateGuid, liveGuidLinkedToRecording);
        }

        /// <summary>
        /// The ONE spawn-stamp liveness / identity test the committed-tree matchers, the
        /// spawn-death check and the replace gate share: does the live vessel (pid, launch guid)
        /// hold the recording's spawn stamp? Reads the vessel-name link rows only when the guids
        /// conclusively differ on an adoption-shaped stamp, which is the one case they can
        /// change the answer.
        /// </summary>
        internal static bool SpawnStampHeldBy(Recording rec, uint candidatePid, string candidateGuid)
        {
            if (SpawnStampHeldByCore(rec, candidatePid, candidateGuid, false)) return true;
            if (rec == null || candidatePid == 0 || rec.SpawnedVesselPersistentId != candidatePid) return false;
            return SpawnStampHeldByCore(rec, candidatePid, candidateGuid,
                VesselNameStore.GuidContinuesIdentity(candidateGuid, rec.RecordedVesselGuid));
        }

        /// <summary>
        /// Pure core of <see cref="SpawnClaimStillHeld"/>, the destructive readers' variant of
        /// <see cref="SpawnStampHeldByCore"/>. It answers the same, except for an adoption-shaped
        /// stamp whose guid conclusively differs from the live vessel's with no link: there the
        /// claim is dropped only on POSITIVE evidence that the recording's vessel is another
        /// launch, namely a known snapshot guid (<paramref name="snapshotGuid"/>, the VESSEL
        /// <c>pid</c> of the recording's end snapshot) that is not the live guid. An unknown
        /// snapshot guid keeps the claim: declaring a live vessel gone on a guess would respawn
        /// a duplicate of a resumed spawned vessel from a save that predates the link rows.
        /// </summary>
        internal static bool SpawnClaimStillHeldCore(
            Recording rec, uint candidatePid, string candidateGuid,
            bool liveGuidLinkedToRecording, string snapshotGuid)
        {
            if (SpawnStampHeldByCore(rec, candidatePid, candidateGuid, liveGuidLinkedToRecording))
                return true;
            if (rec == null || candidatePid == 0 || rec.SpawnedVesselPersistentId != candidatePid)
                return false;
            if (NormalizeGuid(snapshotGuid) == null) return true;
            return !GuidsConclusivelyDiffer(snapshotGuid, candidateGuid);
        }

        /// <summary>
        /// The spawn-death check's and the replace gate's liveness test: <see cref="SpawnStampHeldBy"/>
        /// (guid, then the link rows), and for the one case those refuse, the recording's end
        /// snapshot guid read through <paramref name="snapshotGuidReader"/> (default
        /// <see cref="ReadRecordingSnapshotGuid"/>, which may hydrate the sidecar). The
        /// committed-tree matchers keep the stricter <see cref="SpawnStampHeldBy"/>: refusing a
        /// match only declines a restore, while these readers would declare a live vessel gone.
        /// </summary>
        internal static bool SpawnClaimStillHeld(
            Recording rec, uint candidatePid, string candidateGuid,
            Func<Recording, string> snapshotGuidReader = null)
        {
            if (SpawnStampHeldBy(rec, candidatePid, candidateGuid)) return true;
            if (rec == null || candidatePid == 0 || rec.SpawnedVesselPersistentId != candidatePid)
                return false;
            string snapshotGuid = (snapshotGuidReader ?? ReadRecordingSnapshotGuid)(rec);
            return SpawnClaimStillHeldCore(rec, candidatePid, candidateGuid, false, snapshotGuid);
        }

        /// <summary>
        /// The launch guid of a recording's end snapshot (<see cref="Recording.VesselSnapshot"/>),
        /// hydrating it from the <c>_vessel.craft</c> sidecar when the in-memory copy was dropped.
        /// Null when neither yields a guid. Never throws.
        /// </summary>
        internal static string ReadRecordingSnapshotGuid(Recording rec)
        {
            if (rec == null) return null;
            try
            {
                if (rec.VesselSnapshot == null)
                    RecordingStore.TryHydrateVesselSnapshotFromSidecar(rec);
                return TryReadVesselGuid(rec.VesselSnapshot);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("LaunchIdentity", "ReadRecordingSnapshotGuid: rec=" +
                    (rec.RecordingId ?? "(none)") + " threw " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The live-guid launch test with the link rows read: the guids do not conclusively
        /// differ, or the live guid continues the recording's guid through a link.
        /// </summary>
        internal static bool LiveGuidIsRecordedLaunchOrLinked(string recordedGuid, string liveGuid)
        {
            if (!GuidsConclusivelyDiffer(recordedGuid, liveGuid)) return true;
            return VesselNameStore.GuidContinuesIdentity(liveGuid, recordedGuid);
        }

        /// <summary>
        /// True only when a live vessel is POSITIVELY the recording's launch: pid equal AND both
        /// launch Guids known AND equal. This is the NON-degrading sibling of
        /// <see cref="LiveVesselIsRecordedLaunch"/>: it does not fall back to pid-only on an
        /// unknown Guid.
        ///
        /// <para>
        /// Which one a site wants is decided by WHICH DIRECTION the unknown-Guid fallback moves
        /// behavior. Where pid-only is what the code did before Guids existed, the degrading
        /// predicate preserves it. Where the pre-Guid behavior was to leave the vessel/action
        /// ALONE, degrading to pid-only is a destructive change of direction and this predicate
        /// is the correct gate — the #15 resurrection-retirement classifier
        /// (<see cref="ResurrectionRetirementEligibility.IsPositivelySameLaunch"/>, which
        /// delegates here) and the Re-Fly disabled-slot strip's source arm are both that shape.
        /// </para>
        /// </summary>
        internal static bool LiveVesselIsPositivelyRecordedLaunch(
            Recording rec, uint livePid, string liveGuid)
        {
            if (rec == null || livePid == 0) return false;
            if (rec.VesselPersistentId == 0 || rec.VesselPersistentId != livePid) return false;

            string recGuid = NormalizeGuid(rec.RecordedVesselGuid);
            string live = NormalizeGuid(liveGuid);
            if (recGuid == null || live == null) return false;
            return string.Equals(recGuid, live, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when a candidate vessel is the SAME launch as a recording — its recorded SOURCE
        /// vessel (when <paramref name="matchSource"/>, via <see cref="LiveVesselIsRecordedLaunch"/>)
        /// and/or its spawn/adoption endpoint (when <paramref name="matchSpawn"/>, via
        /// <see cref="LiveVesselIsRecordedSpawn"/>). This is the single launch-identity gate every
        /// vessel-deletion path routes through: a conclusive launch-Guid mismatch is NEVER a match,
        /// so a relaunch of the same craft (shared craft-baked name/pid, fresh Guid) can never be
        /// deleted in place of the recording's vessel.
        /// </summary>
        internal static bool CandidateMatchesRecording(
            Recording rec, uint candidatePid, string candidateGuid, bool matchSource, bool matchSpawn)
        {
            if (rec == null) return false;
            if (matchSpawn && LiveVesselIsRecordedSpawn(rec, candidatePid, candidateGuid)) return true;
            if (matchSource && LiveVesselIsRecordedLaunch(rec, candidatePid, candidateGuid)) return true;
            return false;
        }

        /// <summary>
        /// Returns the first recording in <paramref name="recordings"/> the candidate vessel matches
        /// as the same launch (per <paramref name="matchSource"/> / <paramref name="matchSpawn"/>),
        /// or <c>null</c> when none do. Used by the centralized strip / cleanup decision so a vessel
        /// is only deleted when it is genuinely a recording's recorded-or-spawned vessel of THIS
        /// launch, never a different real launch that reuses the craft-baked name/pid.
        /// </summary>
        internal static Recording FindMatchingRecording(
            IReadOnlyList<Recording> recordings, uint candidatePid, string candidateGuid,
            bool matchSource, bool matchSpawn)
        {
            if (recordings == null) return null;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (CandidateMatchesRecording(recordings[i], candidatePid, candidateGuid, matchSource, matchSpawn))
                    return recordings[i];
            }
            return null;
        }

        /// <summary>
        /// The launch Guid a live <see cref="ProtoVessel"/> carries (<c>vesselID</c>, KSP's
        /// per-launch <c>Vessel.id</c>), normalized to "N" form, or null when it is absent /
        /// <see cref="Guid.Empty"/>.
        ///
        /// <para>
        /// Named rather than inlined because the three recovery seams
        /// (KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 1) each need the same read, and
        /// the null result is LOAD-BEARING: an unknown live guid is what makes the recovery
        /// candidate filter degrade to the historical name+UT behavior rather than drop
        /// candidates.
        /// </para>
        /// </summary>
        internal static string ReadLaunchGuid(ProtoVessel pv)
        {
            if (pv == null) return null;
            return pv.vesselID != Guid.Empty
                ? pv.vesselID.ToString("N", CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// Reads the launch Guid (<c>Vessel.id</c>) from a saved ProtoVessel snapshot ConfigNode.
        /// <c>ProtoVessel.Save</c> writes the vessel Guid as the top-level <c>pid</c> value (and the
        /// uint as <c>persistentId</c>), so both <c>_vessel.craft</c> and <c>_ghost.craft</c> snapshots
        /// carry it. Returns the normalized "N" Guid, or null when absent/malformed. Used to backfill
        /// <see cref="Recording.RecordedVesselGuid"/> on load for recordings captured before the field existed.
        /// </summary>
        internal static string TryReadVesselGuid(ConfigNode snapshot)
        {
            if (snapshot == null) return null;
            string raw = snapshot.GetValue("pid");
            if (string.IsNullOrEmpty(raw)) return null;
            return Guid.TryParse(raw.Trim(), out Guid parsed)
                ? parsed.ToString("N", CultureInfo.InvariantCulture)
                : null;
        }
    }
}
