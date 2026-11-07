using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // Existing-source adoption: whether the vessel a recording would materialize already exists (pid and launch-guid overloads together), the replay / current-flight duplicate allowances, and their test-override setters.
    public static partial class VesselSpawner
    {
        internal static void SetMaterializedSourceVesselExistsOverrideForTesting(Func<uint, bool> checker)
        {
            materializedSourceVesselExistsOverrideForTesting = checker;
        }

        internal static void SetMaterializedSourceVesselGuidOverrideForTesting(Func<uint, string> resolver)
        {
            materializedSourceVesselGuidOverrideForTesting = resolver;
        }

        internal static void ResetMaterializedSourceVesselExistsOverrideForTesting()
        {
            materializedSourceVesselExistsOverrideForTesting = null;
            materializedSourceVesselGuidOverrideForTesting = null;
        }

        internal static bool TryAdoptExistingSourceVesselForSpawn(
            Recording rec,
            string logTag,
            string logContext,
            bool allowExistingSourceDuplicate = false)
        {
            // #976-class guard: only adopt a live/proto vessel as this recording's spawn endpoint
            // when it is genuinely the SAME launch (pid + launch guid), not a relaunch of the same
            // craft that reuses the craft-baked pid. MaterializedSourceVesselExists(rec) applies the
            // guid disambiguator; it falls back to pid-only when the launch guid is unknown.
            bool sourceVesselExists = MaterializedSourceVesselExists(rec);
            return TryAdoptExistingSourceVesselForSpawn(
                rec,
                sourceVesselExists,
                logTag,
                logContext,
                allowExistingSourceDuplicate);
        }

        internal static bool TryAdoptExistingSourceVesselForSpawn(
            Recording rec,
            bool sourceVesselExists,
            string logTag,
            string logContext,
            bool allowExistingSourceDuplicate = false)
        {
            if (allowExistingSourceDuplicate)
                return false;
            if (rec == null || !sourceVesselExists)
                return false;
            if (rec.VesselPersistentId == 0)
                return false;
            if (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0)
                return false;

            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = rec.VesselPersistentId;

            string tag = string.IsNullOrEmpty(logTag) ? "Spawner" : logTag;
            string context = string.IsNullOrEmpty(logContext)
                ? rec.VesselName ?? rec.RecordingId ?? "(unknown)"
                : logContext;
            ParsekLog.Info(tag,
                $"{context}: source vessel pid={rec.VesselPersistentId} already exists - " +
                "adopting instead of spawning duplicate");
            return true;
        }

        internal static bool ShouldAllowExistingSourceDuplicateForReplay(
            uint sourcePid,
            uint sceneEntryActiveVesselPid,
            uint activeVesselPid)
        {
            return ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid,
                sceneEntryActiveVesselPid,
                activeVesselPid,
                replayTargetSourcePid: 0);
        }

        internal static bool ShouldAllowExistingSourceDuplicateForReplay(
            uint sourcePid,
            uint sceneEntryActiveVesselPid,
            uint activeVesselPid,
            uint replayTargetSourcePid)
        {
            if (sourcePid == 0)
                return false;

            // The bypass checks run on every spawn attempt, including the 1 s retry of a
            // spawn-blocked chain tip, so each verdict is rate-limited per source pid with the
            // deciding pids in the key: a first or CHANGED verdict prints at once.
            if (replayTargetSourcePid != 0 && sourcePid != replayTargetSourcePid)
            {
                ParsekLog.VerboseRateLimited("Spawner",
                    BuildExistingSourceDuplicateLogKey("outside-target", sourcePid, replayTargetSourcePid, activeVesselPid),
                    $"ShouldAllowExistingSourceDuplicate=false sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"outside rewind replay target sourcePid={replayTargetSourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"(sceneEntryActiveVesselPid={sceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)}, " +
                    $"activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)})");
                return false;
            }

            // #226 replay/revert duplicate-spawn exception: when the recorded
            // source vessel matches the scene-entry active vessel or the
            // current active vessel, the source is the vehicle the player is
            // actively flying and the spawn is the replay ghost. Log which
            // PID match triggered the bypass so #226 replay diagnostics are
            // legible in KSP.log (follow-up to PR #505 review).
            if (sourcePid == sceneEntryActiveVesselPid)
            {
                ParsekLog.VerboseRateLimited("Spawner",
                    BuildExistingSourceDuplicateLogKey("scene-entry", sourcePid, sceneEntryActiveVesselPid, activeVesselPid),
                    $"ShouldAllowExistingSourceDuplicate=true sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"matched sceneEntryActiveVesselPid={sceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                    $"(activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)}) " +
                    "- #226 replay/revert bypass");
                return true;
            }
            if (sourcePid == activeVesselPid)
            {
                ParsekLog.VerboseRateLimited("Spawner",
                    BuildExistingSourceDuplicateLogKey("active", sourcePid, sceneEntryActiveVesselPid, activeVesselPid),
                    $"ShouldAllowExistingSourceDuplicate=true sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"matched activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                    $"(sceneEntryActiveVesselPid={sceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)}) " +
                    "- #226 replay/revert bypass");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Rate-limit key for the #226 bypass verdict lines: the verdict branch and every pid
        /// that decides it, so a repeated identical verdict is throttled while a changed one
        /// (another active vessel, another replay target) prints at once.
        /// </summary>
        internal static string BuildExistingSourceDuplicateLogKey(
            string branch, uint sourcePid, uint otherPid, uint activeVesselPid)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "existing-source-dup|{0}|{1}|{2}|{3}",
                branch, sourcePid, otherPid, activeVesselPid);
        }

        /// <summary>
        /// Pid-only form of the current-flight #226 bypass, for callers with no recording in
        /// hand. Spawn sites pass the recording instead
        /// (<see cref="ShouldAllowExistingSourceDuplicateForCurrentFlight(Recording)"/>), which
        /// also applies the launch-identity narrowing.
        /// </summary>
        internal static bool ShouldAllowExistingSourceDuplicateForCurrentFlight(uint sourcePid)
        {
            return ShouldAllowExistingSourceDuplicateForCurrentFlightCore(null, sourcePid);
        }

        /// <summary>
        /// The #226 replay/revert bypass for the recording about to spawn at its end: the pid
        /// verdict of <see cref="ShouldAllowExistingSourceDuplicateForReplay(uint,uint,uint,uint)"/>,
        /// then <see cref="ShouldKeepReplayBypassForLaunchIdentity"/> withholds it when no
        /// pid-matching live vessel is the recording's launch. Adoption reads the same either
        /// way (it is guid-aware and refuses a different launch). What the withhold does change:
        /// the flight leaf spawn's no-chain branch now runs
        /// <see cref="ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn"/>, which the bypass
        /// skips, so a live vessel on one of the tip's chain-claimed pids may be replaced under
        /// that path's own gates (a tip the playhead stood before, one live claimed vessel whose
        /// guid is not a different launch from the claim, not simulated since the claim, a tip
        /// that can spawn, and a vessel not in use: the active vessel is refused).
        /// </summary>
        internal static bool ShouldAllowExistingSourceDuplicateForCurrentFlight(Recording rec)
        {
            return ShouldAllowExistingSourceDuplicateForCurrentFlightCore(
                rec, rec != null ? rec.VesselPersistentId : 0u);
        }

        private static bool ShouldAllowExistingSourceDuplicateForCurrentFlightCore(
            Recording rec, uint sourcePid)
        {
            uint activeVesselPid = 0;
            string activeVesselGuid = null;
            try
            {
                ReadActiveVesselIdentity(out activeVesselPid, out activeVesselGuid);
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex))
                    throw;
                activeVesselPid = 0;
                activeVesselGuid = null;
            }

            bool allow = ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid,
                SceneEntryVesselScope.SceneEntryActiveVesselPid,
                activeVesselPid,
                SceneEntryVesselScope.RewindReplayTargetSourcePid);
            if (allow && rec != null)
            {
                string reason;
                allow = ShouldKeepReplayBypassForLaunchIdentity(
                    rec,
                    SceneEntryVesselScope.SceneEntryActiveVesselPid,
                    SceneEntryVesselScope.SceneEntryActiveVesselGuid,
                    activeVesselPid,
                    activeVesselGuid,
                    VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked,
                    out reason);
                if (!allow)
                {
                    // Re-reached by the 1 s retry of a blocked chain tip: rate-limited per
                    // recording, with the deciding reason and pids in the key so a changed
                    // verdict prints at once.
                    ParsekLog.InfoRateLimited("Spawner",
                        string.Format(CultureInfo.InvariantCulture,
                            "existing-source-dup-narrowed|{0}|{1}|{2}|{3}",
                            rec.RecordingId ?? "(none)", reason,
                            SceneEntryVesselScope.SceneEntryActiveVesselPid, activeVesselPid),
                        $"ShouldAllowExistingSourceDuplicateForCurrentFlight=false " +
                        $"rec={rec.RecordingId ?? "(none)"} sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                        $"reason={reason} recGuid={VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid) ?? "(none)"} " +
                        $"sceneEntryGuid={VesselLaunchIdentity.NormalizeGuid(SceneEntryVesselScope.SceneEntryActiveVesselGuid) ?? "(none)"} " +
                        $"activeGuid={VesselLaunchIdentity.NormalizeGuid(activeVesselGuid) ?? "(none)"} " +
                        "- #226 bypass withheld, spawn goes through guid-aware adoption");
                }
            }
            if (allow)
            {
                ParsekLog.VerboseRateLimited("Spawner",
                    BuildExistingSourceDuplicateLogKey(
                        "current-flight", sourcePid, SceneEntryVesselScope.SceneEntryActiveVesselPid, activeVesselPid),
                    $"ShouldAllowExistingSourceDuplicateForCurrentFlight=true sourcePid={sourcePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"sceneEntryActiveVesselPid={SceneEntryVesselScope.SceneEntryActiveVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                    $"activeVesselPid={activeVesselPid.ToString(CultureInfo.InvariantCulture)}");
            }
            return allow;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ReadActiveVesselIdentity(out uint pid, out string guid)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            pid = active != null ? active.persistentId : 0u;
            guid = active != null && active.id != Guid.Empty
                ? active.id.ToString("N", CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// Pure launch-identity check of the #226 bypass, applied after its pid verdict allowed
        /// it. The bypass exists for a revert or rewind re-flight: the live vessel IS the
        /// recording's launch (same pid and guid) taken back into the recording's past, so the
        /// committed recording must still spawn its end vessel beside it instead of adopting the
        /// player's re-flight. It is withheld only when no pid-matching live vessel (the
        /// scene-entry one, or the active one) is the recording's launch per
        /// <paramref name="isRecordedLaunch"/> (recorded guid, live guid; the live caller passes
        /// <see cref="VesselLaunchIdentity.LiveGuidIsRecordedLaunchOrLinked"/>, so a resumed
        /// spawned vessel linked to the recording counts as its launch). A different launch of
        /// the craft gains nothing from the bypass: guid-aware adoption refuses it anyway, so the
        /// spawn takes the same ordinary path. Unknown guids keep the bypass.
        /// <paramref name="reason"/> names the deciding check.
        /// </summary>
        internal static bool ShouldKeepReplayBypassForLaunchIdentity(
            Recording rec,
            uint sceneEntryActiveVesselPid,
            string sceneEntryActiveVesselGuid,
            uint activeVesselPid,
            string activeVesselGuid,
            Func<string, string, bool> isRecordedLaunch,
            out string reason)
        {
            if (rec == null || rec.VesselPersistentId == 0)
            {
                reason = ReplayBypassReasonPidOnly;
                return true;
            }
            if (isRecordedLaunch == null)
                isRecordedLaunch = (recGuid, liveGuid) =>
                    !VesselLaunchIdentity.GuidsConclusivelyDiffer(recGuid, liveGuid);

            uint sourcePid = rec.VesselPersistentId;
            bool sceneEntrySameLaunch = sourcePid == sceneEntryActiveVesselPid
                && isRecordedLaunch(rec.RecordedVesselGuid, sceneEntryActiveVesselGuid);
            bool activeSameLaunch = sourcePid == activeVesselPid
                && isRecordedLaunch(rec.RecordedVesselGuid, activeVesselGuid);
            if (!sceneEntrySameLaunch && !activeSameLaunch)
            {
                reason = ReplayBypassReasonDifferentLaunch;
                return false;
            }

            reason = ReplayBypassReasonSameLaunch;
            return true;
        }

        // Pid-only existence check. Retained for callers that have no recording context (e.g. the
        // post-Die() "is the vessel I tried to kill still around" check in VesselGhoster). For the
        // spawn-adoption identity decision use the Recording overload below, which is guid-aware.
        internal static bool MaterializedSourceVesselExists(uint sourcePid)
        {
            if (sourcePid == 0 || GhostMapPresence.IsGhostMapVessel(sourcePid))
                return false;

            if (materializedSourceVesselExistsOverrideForTesting != null)
                return materializedSourceVesselExistsOverrideForTesting(sourcePid);

            string ignored;
            return LoadedRealVesselExists(sourcePid, out ignored)
                || ProtoVesselExists(sourcePid, out ignored);
        }

        // Guid-aware existence check for spawn adoption (#976-class): true only when a live/proto
        // vessel with the recording's pid exists AND is the same launch (its Vessel.id matches the
        // recording's RecordedVesselGuid, or either guid is unknown -> pid-only fallback). A relaunch
        // of the same craft reuses the baked pid but carries a different launch guid, so it is rejected.
        internal static bool MaterializedSourceVesselExists(Recording rec, bool logAdoptionRejection = true)
        {
            if (rec == null)
                return false;

            uint sourcePid = rec.VesselPersistentId;
            if (sourcePid == 0 || GhostMapPresence.IsGhostMapVessel(sourcePid))
                return false;

            bool exists;
            string liveGuid;
            if (materializedSourceVesselExistsOverrideForTesting != null)
            {
                exists = materializedSourceVesselExistsOverrideForTesting(sourcePid);
                liveGuid = materializedSourceVesselGuidOverrideForTesting != null
                    ? materializedSourceVesselGuidOverrideForTesting(sourcePid)
                    : null;
            }
            else
            {
                exists = LoadedRealVesselExists(sourcePid, out liveGuid)
                    || ProtoVesselExists(sourcePid, out liveGuid);
            }

            if (!exists)
                return false;

            bool sameLaunch = VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, sourcePid, liveGuid);
            if (!sameLaunch && logAdoptionRejection)
                ParsekLog.Info("Spawner",
                    $"Adoption rejected: live vessel pid={sourcePid} is a different launch than recording " +
                    $"'{rec.RecordingId ?? rec.VesselName ?? "(unknown)"}' (recorded guid={rec.RecordedVesselGuid ?? "(none)"}, " +
                    $"live guid={liveGuid ?? "(none)"}) - not adopting (relaunch of the same craft reuses the baked pid)");
            return sameLaunch;
        }

        private static bool LoadedRealVesselExists(uint sourcePid, out string vesselGuid)
        {
            vesselGuid = null;
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels == null)
                    return false;

                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel vessel = vessels[i];
                    if (vessel != null
                        && vessel.persistentId == sourcePid
                        && !GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                    {
                        vesselGuid = vessel.id != Guid.Empty ? vessel.id.ToString("N") : null;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"LoadedRealVesselExists: FlightGlobals unavailable for pid={sourcePid}");
            }

            return false;
        }

        private static bool ProtoVesselExists(uint sourcePid, out string vesselGuid)
        {
            vesselGuid = null;
            try
            {
                var protoVessels = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protoVessels == null)
                    return false;

                for (int i = 0; i < protoVessels.Count; i++)
                {
                    ProtoVessel pv = protoVessels[i];
                    if (pv != null
                        && pv.persistentId == sourcePid
                        && !GhostMapPresence.IsGhostMapVessel(pv.persistentId))
                    {
                        vesselGuid = pv.vesselID != Guid.Empty ? pv.vesselID.ToString("N") : null;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsHeadlessKspAccessFailure(ex))
                    throw;

                ParsekLog.Verbose("Spawner",
                    $"ProtoVesselExists: flightState unavailable for pid={sourcePid}");
            }

            return false;
        }

        public static bool VesselExistsByPid(uint pid)
        {
            if (FlightGlobals.Vessels == null) return false;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                if (FlightGlobals.Vessels[i].persistentId == pid)
                {
                    if (GhostMapPresence.IsGhostMapVessel(pid)) return false;
                    return true;
                }
            }
            return false;
        }
    }
}
