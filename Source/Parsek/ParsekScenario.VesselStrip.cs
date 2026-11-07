using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Load-time vessel stripping: orphaned spawned vessels, future prelaunch vessels, and the
    // spawn state reconciled after the strip.
    public partial class ParsekScenario
    {
        /// <summary>
        /// BUG-H — the single launch-identity-aware decision every revert/rewind vessel deletion
        /// routes through. A candidate flightState/loaded vessel may be deleted ONLY when both hold:
        ///
        /// <list type="number">
        /// <item><b>Launch-identity match</b> (Correction 1): the candidate (its
        /// <c>persistentId</c> + <c>Vessel.id</c> Guid) is the SAME launch as some recording — the
        /// recording's spawn/adoption endpoint (always) and, on rewind, its recorded source vessel
        /// (<paramref name="matchSource"/>). A conclusive launch-Guid mismatch is never a match, so
        /// a relaunch of the same craft (shared craft-baked name/pid, fresh Guid) is never deleted
        /// in place of the recording's vessel.</item>
        /// <item><b>Pre-existing whitelist</b> (Correction 2): a candidate whose pid is in
        /// <paramref name="preExistingWhitelist"/> pre-existed this operation and is NEVER stripped.
        /// On revert this is the revert-target launch/prelaunch quicksave (a vessel present there
        /// belongs to an unrelated mission, not the reverted flight). On rewind this is the rewind
        /// quicksave (a vessel present there pre-existed the rewind point, so it is not future); this
        /// backstops the launch-Guid gate for the rare guidless adoption-stamp recording, mirroring
        /// the existing <see cref="StripFuturePrelaunchVessels"/> pid-whitelist contract.</item>
        /// </list>
        ///
        /// <paramref name="requireWhitelist"/> (revert only): when the whitelist is unavailable the
        /// candidate is NOT deleted (fail-closed, data-loss safe) — a missing scope signal can never
        /// delete real vessels. Rewind passes <paramref name="requireWhitelist"/>=false so that, if
        /// the rewind quicksave pids are unavailable, it falls back to launch-identity-only stripping
        /// (its prior behaviour) rather than failing to remove genuinely future-timeline vessels; the
        /// separate <see cref="StripFuturePrelaunchVessels"/> pid-whitelist pass still backstops it.
        ///
        /// Pure and testable (no ProtoVessel / FlightGlobals dependency): callers resolve the
        /// candidate's pid + Guid + situation and pass them in.
        /// </summary>
        internal static bool ShouldStripVesselForRecordings(
            uint candidatePid, string candidateGuid, Vessel.Situations situation,
            IReadOnlyList<Recording> recordings,
            bool matchSource, bool skipPrelaunch,
            bool requireWhitelist, HashSet<uint> preExistingWhitelist,
            out Recording matched, out string reason)
        {
            matched = null;

            if (candidatePid == 0)
            {
                reason = "candidate pid zero";
                return false;
            }

            // On KSP Revert, skip PRELAUNCH vessels — these are the user's launch vessel on the pad.
            // On Parsek Rewind, strip them too — a PRELAUNCH vessel from a later launch is
            // incompatible with the earlier game state being restored.
            if (skipPrelaunch && situation == Vessel.Situations.PRELAUNCH)
            {
                reason = "PRELAUNCH skipped (launch vessel protected)";
                return false;
            }

            // Correction 2: a vessel present in the pre-existing whitelist (revert target / rewind
            // quicksave) pre-existed this operation and belongs to an unrelated mission or an earlier
            // timeline point — never strip it. Applied whenever a whitelist is supplied (revert AND
            // rewind), so the rewind path is backstopped against the guidless adoption-stamp gap.
            if (preExistingWhitelist != null && preExistingWhitelist.Contains(candidatePid))
            {
                reason = "pre-existing in launch/rewind quicksave (not part of this operation)";
                return false;
            }

            // Revert requires the whitelist: with no scope signal, fail closed so a missing target
            // snapshot can never delete real vessels. (Rewind passes requireWhitelist=false.)
            if (requireWhitelist && preExistingWhitelist == null)
            {
                reason = "scope required but launch-quicksave whitelist unavailable (fail-closed)";
                return false;
            }

            // Correction 1: only delete a vessel that is genuinely the SAME launch as a recording.
            matched = VesselLaunchIdentity.FindMatchingRecording(
                recordings, candidatePid, candidateGuid, matchSource, matchSpawn: true);
            if (matched == null)
            {
                reason = "no same-launch recording match (different real launch or unrelated vessel)";
                return false;
            }

            reason = requireWhitelist
                ? "same-launch recording match + appeared during the reverted flight"
                : "same-launch recording match";
            return true;
        }

        /// <summary>
        /// Strips protoVessels from flightState that genuinely belong to a recording's launch and
        /// (on revert) appeared during the reverted flight. Called during revert/rewind to remove
        /// orphaned spawned vessels before they contaminate the next launch quicksave. The
        /// per-vessel decision is the launch-identity-aware <see cref="ShouldStripVesselForRecordings"/>
        /// (BUG-H): name-only matching previously deleted real, separately-launched craft that merely
        /// reuse the craft-baked name/pid of a recording.
        /// </summary>
        /// <param name="protoVessels">flightState's protoVessels list (modified in place).</param>
        /// <param name="recordings">All committed recordings (for the launch-identity lookup).</param>
        /// <param name="matchSource">Also match a recording's recorded SOURCE vessel (rewind=true,
        /// revert=false). Revert only undoes Parsek-spawned/adopted vessels of the reverted flight;
        /// rewind also removes the recorded vessel itself, which is future relative to the rewind point.</param>
        /// <param name="skipPrelaunch">When true (KSP Revert), PRELAUNCH vessels are kept; when false
        /// (Parsek Rewind), a PRELAUNCH vessel from a later launch is also stripped.</param>
        /// <param name="requireWhitelist">When true (revert), fail closed (strip nothing) if
        /// <paramref name="preExistingWhitelist"/> is null. Rewind passes false (identity-only fallback).</param>
        /// <param name="preExistingWhitelist">Pids that pre-existed this operation and are never
        /// stripped: the revert-target launch/prelaunch quicksave on revert, or the rewind quicksave
        /// on rewind. Applied as a protect-set whenever supplied.</param>
        internal static int StripOrphanedSpawnedVessels(
            List<ProtoVessel> protoVessels,
            IReadOnlyList<Recording> recordings,
            bool matchSource, bool skipPrelaunch,
            bool requireWhitelist, HashSet<uint> preExistingWhitelist)
        {
            if (protoVessels == null || recordings == null || recordings.Count == 0)
                return 0;

            int stripped = 0;
            int skippedPreExisting = 0;
            int skippedDifferentLaunch = 0;
            for (int i = protoVessels.Count - 1; i >= 0; i--)
            {
                var pv = protoVessels[i];
                if (GhostMapPresence.IsGhostMapVessel(pv.persistentId)) continue;

                string candidateGuid = pv.vesselID != Guid.Empty
                    ? pv.vesselID.ToString("N")
                    : null;

                bool shouldStrip = ShouldStripVesselForRecordings(
                    pv.persistentId, candidateGuid, pv.situation, recordings,
                    matchSource, skipPrelaunch, requireWhitelist, preExistingWhitelist,
                    out Recording matched, out string reason);

                if (!shouldStrip)
                {
                    if (reason != null && reason.StartsWith("pre-existing"))
                        skippedPreExisting++;
                    else if (reason != null && reason.StartsWith("no same-launch"))
                        skippedDifferentLaunch++;
                    ParsekLog.Verbose("Scenario",
                        $"Keeping vessel '{pv.vesselName}' (pid={pv.persistentId}, guid={candidateGuid ?? "(none)"}, " +
                        $"situation={pv.situation}) — {reason}");
                    continue;
                }

                ParsekLog.Info("Scenario",
                    $"Stripping orphaned spawned vessel '{pv.vesselName}' " +
                    $"(pid={pv.persistentId}, guid={candidateGuid ?? "(none)"}, situation={pv.situation}) " +
                    $"from flightState — matched recording '{matched?.VesselName ?? "(unknown)"}' " +
                    $"(recordedGuid={matched?.RecordedVesselGuid ?? "(none)"}); {reason}");
                protoVessels.RemoveAt(i);
                stripped++;
            }

            if (stripped > 0 || skippedPreExisting > 0 || skippedDifferentLaunch > 0)
                ParsekLog.Info("Scenario",
                    $"StripOrphanedSpawnedVessels: removed {stripped} vessel(s) from flightState " +
                    $"(kept {skippedPreExisting} pre-existing, {skippedDifferentLaunch} different-launch; " +
                    $"matchSource={matchSource}, requireWhitelist={requireWhitelist}, " +
                    $"whitelistPids={preExistingWhitelist?.Count ?? 0})");

            return stripped;
        }

        /// <summary>
        /// Strips vessels whose persistentId is NOT in the rewind quicksave whitelist (#129/#164).
        /// These are vessels from after the rewind point (e.g. a pad vessel from a later launch)
        /// that persisted through rewind because the launch-identity strip only removes vessels that
        /// belong to a recording's launch — an unrecorded future vessel survives that match.
        ///
        /// BUG-H audit: this path is NOT in the name/pid-without-Guid defect class. It is keyed on the
        /// rewind quicksave whitelist (keep-if-present), never on a recording-name match, so it cannot
        /// delete a vessel "in place of" a recording's relaunched craft. A vessel present in the
        /// quicksave is always kept (the data-loss-safe direction); only vessels genuinely absent from
        /// the rewind target are removed, preserving the rewind contract. Behaviour intentionally
        /// unchanged.
        /// </summary>
        /// <param name="protoVessels">The flightState's protoVessels list (modified in-place).</param>
        /// <param name="quicksavePids">PIDs of vessels that existed in the rewind quicksave.</param>
        internal static int StripFuturePrelaunchVessels(
            List<ProtoVessel> protoVessels, HashSet<uint> quicksavePids)
        {
            if (protoVessels == null || quicksavePids == null)
                return 0;

            int stripped = 0;
            for (int i = protoVessels.Count - 1; i >= 0; i--)
            {
                var pv = protoVessels[i];
                if (!ShouldStripFuturePrelaunch(pv.situation, pv.persistentId, quicksavePids))
                    continue;

                ParsekLog.Info("Scenario",
                    $"Stripping future vessel '{pv.vesselName}' " +
                    $"(pid={pv.persistentId}, sit={pv.situation}) — not in quicksave whitelist");
                protoVessels.RemoveAt(i);
                stripped++;
            }

            return stripped;
        }

        /// <summary>
        /// Pure decision: should this vessel be stripped as a future PRELAUNCH vessel?
        /// Returns true if the vessel is PRELAUNCH and its PID is not in the quicksave whitelist.
        /// Extracted for testability (ProtoVessel can't be constructed outside KSP).
        /// </summary>
        internal static bool ShouldStripFuturePrelaunch(
            Vessel.Situations situation, uint persistentId, HashSet<uint> quicksavePids)
        {
            if (quicksavePids == null)
                return false;
            // Strip ANY vessel not in the quicksave whitelist (#164).
            // Previously only stripped PRELAUNCH; now catches flags, landed capsules,
            // and other player-created vessels from the future after rewind.
            return !quicksavePids.Contains(persistentId);
        }

        // The pid-only ReconcileSpawnStateAfterStrip overloads (List<ProtoVessel> and
        // HashSet<uint>) and their ShouldResetSpawnState(uint, HashSet<uint>) predicate were
        // DELETED (#16) once their last production caller — the plain Rewind-to-Launch OnLoad
        // path — moved to (pid, launch-guid) identities. A bare craft-baked pid cannot tell a
        // preserved relaunch of the same craft from the recording's own vessel, and in the
        // post-Re-Fly preserved-fleet world that stranger keeps VesselSpawned=true forever and
        // permanently blocks the recording's terminal spawn. The guid-aware overload below is
        // now the ONLY reconcile surface, so no future caller can pick the unsafe one.

        /// <summary>
        /// BUG-H: launch-identity-aware reconcile. A recording is reset only when its spawned vessel
        /// is no longer present as the SAME launch among the survivors. A surviving vessel that shares
        /// the recording's spawned pid but is a DIFFERENT launch (craft-baked pid collision) does NOT
        /// keep the recording's spawn state alive — the recording is reset so it can re-spawn, instead
        /// of being permanently blocked by a same-pid stranger. Adoption-stamp recordings are matched
        /// by launch Guid; genuine Parsek spawns (KSP-unique spawn pid) match by pid.
        /// </summary>
        internal static int ReconcileSpawnStateAfterStrip(
            IReadOnlyList<(uint pid, string guid)> survivors, IReadOnlyList<Recording> recordings)
        {
            if (recordings == null || recordings.Count == 0)
                return 0;

            int reconciled = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (ShouldResetSpawnState(recordings[i], survivors))
                {
                    uint oldPid = recordings[i].SpawnedVesselPersistentId;
                    recordings[i].SpawnedVesselPersistentId = 0;
                    recordings[i].VesselSpawned = false;
                    recordings[i].SpawnAttempts = 0;
                    recordings[i].SpawnDeathCount = 0;
                    TerminalOrbitSpawnSafety.Clear(recordings[i]);
                    ParsekLog.Info("Scenario",
                        $"Reconciled spawn state for recording #{i} \"{recordings[i].VesselName}\": " +
                        $"pid={oldPid} not present as the same launch in flightState — reset for re-spawn");
                    reconciled++;
                }
            }

            if (reconciled > 0)
                ParsekLog.Info("Scenario",
                    $"ReconcileSpawnStateAfterStrip (guid-aware): reset {reconciled} recording(s) whose spawned vessel was stripped");

            return reconciled;
        }

        /// <summary>
        /// Guid-aware pure decision for <see cref="ReconcileSpawnStateAfterStrip(IReadOnlyList{ValueTuple{uint,string}}, IReadOnlyList{Recording})"/>:
        /// reset the recording's spawn state when its spawn endpoint is non-zero and no surviving
        /// vessel is the same launch (via <see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/>).
        /// A null survivor set means nothing survived, so any non-zero spawn pid is reset.
        /// </summary>
        internal static bool ShouldResetSpawnState(
            Recording rec, IReadOnlyList<(uint pid, string guid)> survivors)
        {
            if (rec == null || rec.SpawnedVesselPersistentId == 0)
                return false;
            if (survivors == null)
                return true;
            for (int i = 0; i < survivors.Count; i++)
            {
                if (VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, survivors[i].pid, survivors[i].guid))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Collects (persistentId, launch-Guid) identities of the surviving protoVessels for the
        /// guid-aware reconcile. Extracted so the reconcile logic stays testable without a live
        /// flightState (ProtoVessel can't be constructed outside KSP).
        /// </summary>
        internal static List<(uint pid, string guid)> CollectSurvivingVesselIdentities(
            List<ProtoVessel> remainingVessels)
        {
            var list = new List<(uint pid, string guid)>();
            if (remainingVessels == null)
                return list;
            for (int i = 0; i < remainingVessels.Count; i++)
            {
                var pv = remainingVessels[i];
                if (pv == null) continue;
                string g = pv.vesselID != Guid.Empty ? pv.vesselID.ToString("N") : null;
                list.Add((pv.persistentId, g));
            }
            return list;
        }

        /// <summary>
        /// BUG-C (2026-06-07 career playtest): re-applies the durable terminal-orbit
        /// "cannot spawn safely" abandon from a saved RECORDING node onto an in-memory
        /// committed recording. The in-session OnLoad reconcile clears all terminal
        /// spawn-safety fields (via <see cref="TerminalOrbitSpawnSafety.Clear"/>) and
        /// then restores only the saved subset; without re-restoring this flag a
        /// terminal-orbit vessel that already died on spawn is re-materialized after
        /// every scene change (it logs "will not be retried" yet is retried). The flag
        /// is absent on a revert quicksave (no tree nodes), so the abandon correctly
        /// does not carry across a revert. Mirrors
        /// <see cref="RecordingTreeRecordCodec"/>'s save/load of the same keys, which
        /// covers the cold-start rebuild path.
        /// </summary>
        internal static void RestorePersistedTerminalAbandon(Recording rec, ConfigNode savedTreeRecNode)
        {
            if (rec == null || savedTreeRecNode == null)
                return;

            bool savedCannotSpawn = false;
            bool.TryParse(savedTreeRecNode.GetValue("terminalSpawnCannotSpawnSafely"), out savedCannotSpawn);

            // Only the durable spawn-death abandon is restored; a geometry verdict is
            // re-derived from the recording's own terminal orbit on the next attempt.
            TerminalOrbitSpawnSafety.RestoreSavedRefusal(
                rec,
                savedCannotSpawn,
                savedTreeRecNode.GetValue("terminalSpawnSafetyReasonCode"));
        }
    }
}
