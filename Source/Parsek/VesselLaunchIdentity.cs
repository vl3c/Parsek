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
