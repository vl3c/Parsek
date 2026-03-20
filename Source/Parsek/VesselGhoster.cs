using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Manages the real→ghost vessel lifecycle: snapshot, despawn, create ghost GO,
    /// and spawn final-form vessels at chain tips.
    /// </summary>
    internal class VesselGhoster
    {
        private const string Tag = "Ghoster";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        private Dictionary<uint, GhostedVesselInfo> ghostedVessels =
            new Dictionary<uint, GhostedVesselInfo>();

        /// <summary>
        /// Tracks the state of a single ghosted vessel: its captured snapshot,
        /// the ghost visual GO (may be null), and identifying info.
        /// </summary>
        internal class GhostedVesselInfo
        {
            public uint vesselPid;
            public string vesselName;
            public ConfigNode snapshot;   // captured via TryBackupSnapshot before despawn
            public GameObject ghostGO;    // ghost visual (may be null if build failed/deferred)
        }

        // --- Core lifecycle ---

        /// <summary>
        /// Snapshot the real vessel, despawn it, create ghost GO.
        /// Returns true on success. On failure, vessel is left untouched.
        /// </summary>
        internal bool GhostVessel(uint vesselPid)
        {
            if (!ShouldAttemptGhosting(vesselPid, true))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic, "GhostVessel guard: pid={0} — ShouldAttemptGhosting=false", vesselPid));
                return false;
            }

            // Find vessel via PID
            Vessel vessel = FlightRecorder.FindVesselByPid(vesselPid);
            if (vessel == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic, "GhostVessel: pid={0} — vessel not found via FindVesselByPid", vesselPid));
                return false;
            }

            string vesselName = vessel.vesselName ?? "(unnamed)";

            // Capture snapshot before despawn
            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            if (snapshot == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic, "GhostVessel: pid={0} name={1} — snapshot capture failed", vesselPid, vesselName));
                return false;
            }

            ParsekLog.Info(Tag,
                string.Format(ic, "Ghosting vessel: pid={0} name={1} — snapshot captured, despawning",
                    vesselPid, vesselName));

            // Despawn the real vessel
            try
            {
                vessel.Die();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "Ghost conversion FAILED: pid={0} name={1} — despawn exception: {2}. Attempting restore from snapshot.",
                        vesselPid, vesselName, ex.Message));
                try
                {
                    VesselSpawner.RespawnVessel(snapshot, preserveIdentity: true);
                    ParsekLog.Info(Tag,
                        string.Format(ic, "Vessel restored from snapshot after despawn failure: pid={0}", vesselPid));
                }
                catch (Exception restoreEx)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "Vessel restore ALSO FAILED: pid={0} — {1}. Player should reload quicksave.",
                            vesselPid, restoreEx.Message));
                }
                return false;
            }

            // Create tracking entry
            var info = new GhostedVesselInfo
            {
                vesselPid = vesselPid,
                vesselName = vesselName,
                snapshot = snapshot,
                ghostGO = null // Ghost GO creation deferred to 6b-4
            };
            ghostedVessels[vesselPid] = info;

            // Ghost GO creation placeholder — actual visual from non-Recording snapshot
            // is complex and requires ParsekFlight integration. Deferred to Task 6b-4.
            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "Ghost GO creation deferred to 6b-4 for pid={0} name={1}", vesselPid, vesselName));

            ParsekLog.Info(Tag,
                string.Format(ic, "Vessel ghosted: pid={0} name={1}", vesselPid, vesselName));

            return true;
        }

        /// <summary>
        /// Is this vessel currently ghosted?
        /// </summary>
        internal bool IsGhosted(uint vesselPid)
        {
            return ghostedVessels.ContainsKey(vesselPid);
        }

        /// <summary>
        /// Get the info for a ghosted vessel (null if not ghosted).
        /// </summary>
        internal GhostedVesselInfo GetGhostedInfo(uint vesselPid)
        {
            GhostedVesselInfo info;
            if (ghostedVessels.TryGetValue(vesselPid, out info))
                return info;
            return null;
        }

        /// <summary>
        /// Get the ghost GO for a ghosted vessel (null if not ghosted or GO creation failed).
        /// </summary>
        internal GameObject GetGhostGO(uint vesselPid)
        {
            GhostedVesselInfo info;
            if (ghostedVessels.TryGetValue(vesselPid, out info))
                return info.ghostGO;
            return null;
        }

        /// <summary>
        /// Spawn the final-form vessel at chain tip, destroy ghost GO.
        /// Returns the spawned vessel's PID (0 on failure).
        /// </summary>
        internal uint SpawnAtChainTip(GhostChain chain)
        {
            if (!CanSpawnAtChainTip(chain))
            {
                ParsekLog.Warn(Tag, "SpawnAtChainTip guard: CanSpawnAtChainTip=false");
                return 0;
            }

            // Find the tip recording from committed recordings
            string tipId = chain.TipRecordingId;
            ConfigNode vesselSnapshot = null;
            string vesselName = null;

            var committedRecordings = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i].RecordingId == tipId)
                {
                    vesselSnapshot = committedRecordings[i].VesselSnapshot;
                    vesselName = committedRecordings[i].VesselName;
                    break;
                }
            }

            if (vesselSnapshot == null)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "SpawnAtChainTip: tip recording '{0}' has no VesselSnapshot — cannot spawn",
                        tipId));
                return 0;
            }

            // Spawn with PID preservation for chain continuity
            uint spawnedPid = VesselSpawner.RespawnVessel(vesselSnapshot, preserveIdentity: true);

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Chain tip spawn: pid={0} vessel={1} preserveIdentity=true — real vessel created",
                    spawnedPid, vesselName ?? "(unknown)"));

            if (spawnedPid == 0)
            {
                ParsekLog.Error(Tag,
                    string.Format(ic,
                        "SpawnAtChainTip: RespawnVessel returned pid=0 for tip '{0}' — spawn failed",
                        tipId));
                return 0;
            }

            // Destroy ghost GO if it exists, and remove tracking entry
            uint originalPid = chain.OriginalVesselPid;
            GhostedVesselInfo info;
            if (ghostedVessels.TryGetValue(originalPid, out info))
            {
                if (info.ghostGO != null)
                {
                    UnityEngine.Object.Destroy(info.ghostGO);
                    ParsekLog.Verbose(Tag,
                        string.Format(ic, "Destroyed ghost GO for pid={0}", originalPid));
                }
                ghostedVessels.Remove(originalPid);
            }

            return spawnedPid;
        }

        /// <summary>
        /// Destroy all ghost GOs and clear state. Called on scene exit.
        /// </summary>
        internal void CleanupAll()
        {
            int count = ghostedVessels.Count;
            foreach (var kvp in ghostedVessels)
            {
                if (kvp.Value.ghostGO != null)
                {
                    UnityEngine.Object.Destroy(kvp.Value.ghostGO);
                }
            }
            ghostedVessels.Clear();

            ParsekLog.Info(Tag,
                string.Format(ic, "CleanupAll: cleared {0} ghosted vessel(s)", count));
        }

        /// <summary>
        /// Number of currently ghosted vessels.
        /// </summary>
        internal int GhostedCount => ghostedVessels.Count;

        /// <summary>
        /// Reset for testing — clears all state without destroying GOs.
        /// </summary>
        internal void ResetForTesting()
        {
            ghostedVessels.Clear();
        }

        /// <summary>
        /// Add a ghosted vessel entry directly for test seeding.
        /// </summary>
        internal void AddGhostedForTesting(uint pid, string name, ConfigNode snapshot)
        {
            ghostedVessels[pid] = new GhostedVesselInfo
            {
                vesselPid = pid,
                vesselName = name,
                snapshot = snapshot
            };
        }

        // --- Pure decision methods (static, testable) ---

        /// <summary>
        /// Should we attempt to ghost this vessel?
        /// Guards: pid=0 → false, vessel missing → false.
        /// </summary>
        internal static bool ShouldAttemptGhosting(uint vesselPid, bool vesselExists)
        {
            return vesselPid != 0 && vesselExists;
        }

        /// <summary>
        /// Can we spawn at this chain's tip?
        /// Guards: null chain, terminated chain, null/empty tip recording ID → false.
        /// </summary>
        internal static bool CanSpawnAtChainTip(GhostChain chain)
        {
            return chain != null
                && !chain.IsTerminated
                && !string.IsNullOrEmpty(chain.TipRecordingId);
        }
    }
}
