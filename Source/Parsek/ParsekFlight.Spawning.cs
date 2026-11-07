using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Vessel spawning from playback: chain-tip spawn, tree-leaf spawn, chain ghosting at flight
    // ready, orphaned and recovered timeline spawns, proximity candidates, warp-to and
    // fast-forward, and ghost labels.
    public partial class ParsekFlight
    {

        /// <summary>
        /// Pure: every vessel pid an active chain holds back on a flight load, the chain key
        /// first, then each claim folded into it (a tug that docks to a depot and carries it to
        /// a station ends both: the walker folds the station's chain into the depot's, and
        /// ghosting the key alone leaves the stale station live for the tip to adopt). A folded
        /// claim whose live vessel is conclusively another launch of the same craft (a
        /// craft-baked pid) is left out; the key keeps its pid-only ghosting.
        /// <paramref name="liveGuidOf"/> answers the live vessel's launch guid for a pid (null
        /// when unknown or not live).
        /// </summary>
        internal static List<uint> SelectChainPidsToGhost(
            uint chainKey, GhostChain chain, Func<uint, string> liveGuidOf)
        {
            var pids = new List<uint>();
            if (chainKey != 0)
                pids.Add(chainKey);
            if (chain == null || chain.ClaimedVessels == null)
                return pids;
            List<ChainClaimedVessel> held = chain.HeldBackVessels();
            for (int i = 0; i < held.Count; i++)
            {
                ChainClaimedVessel claim = held[i];
                if (claim.Pid == 0 || pids.Contains(claim.Pid))
                    continue;
                string liveGuid = liveGuidOf != null ? liveGuidOf(claim.Pid) : null;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(liveGuid, claim.LaunchGuid))
                {
                    ParsekLog.Verbose("Ghoster",
                        string.Format(CultureInfo.InvariantCulture,
                            "Folded chain claim not held back: pid={0} chain={1} liveLaunch={2} claimLaunch={3} " +
                            "(another launch of the same craft)",
                            claim.Pid, chainKey, liveGuid, claim.LaunchGuid ?? "(none)"));
                    continue;
                }
                pids.Add(claim.Pid);
            }
            return pids;
        }

        /// <summary>
        /// Pure selection half of <see cref="RecoverTimelineSpawnedVessel"/>: does committed
        /// recording <paramref name="rec"/> hold a live timeline spawn of the same vessel the
        /// tree leaf <paramref name="leaf"/> is about to spawn? By launch identity
        /// (<see cref="VesselLaunchIdentity.RecordingsShareVesselIdentity"/>): a renamed copy
        /// of the vessel still qualifies, and a different launch that merely shares the name
        /// never does (its spawned vessel is another real ship). The name decides only for
        /// guid-less legacy recordings.
        /// </summary>
        internal static bool IsTimelineSpawnOfLeafVessel(Recording rec, Recording leaf)
        {
            if (rec == null || leaf == null) return false;
            if (!rec.VesselSpawned || rec.SpawnedVesselPersistentId == 0) return false;
            return VesselLaunchIdentity.RecordingsShareVesselIdentity(rec, leaf);
        }

        /// <summary>
        /// Routes spawn through VesselGhoster for chain tips, or existing
        /// SpawnOrRecoverIfTooClose for normal recordings.
        /// Handles collision-blocked chains: if spawn is blocked, chain stays active.
        /// If chain was previously blocked, tries to spawn at propagated position.
        /// Called by ParsekPlaybackPolicy when engine fires PlaybackCompleted with needsSpawn.
        /// </summary>
        internal void SpawnVesselOrChainTipFromPolicy(Recording rec, int index)
        {
            SpawnHost.SpawnVesselOrChainTip(rec, index);
        }

        /// <summary>Called by policy to switch camera to a spawned vessel after watch-mode spawn.</summary>
        internal void DeferredActivateVesselFromPolicy(uint vesselPid) =>
            StartCoroutine(DeferredActivateVessel(vesselPid));


        // UpdateWatchCamera, ProcessWatchEndHoldTimer, FindWatchedGhostState,
        // TryResolveOverlapRetarget moved to WatchModeController

        internal bool IsAnyWarpActive() => GhostPlaybackEngine.IsAnyWarpActiveFromGlobals();

        internal void ExitAllWarpForPlaybackStart(string context)
        {
            if (!IsAnyWarpActive()) return;
            TimeWarp.SetRate(0, true);
            Log($"Warp reset for playback start ({context})");
        }

        IEnumerator DeferredActivateVessel(uint pid)
        {
            // Wait up to 5 seconds for the vessel to appear and load.
            // Distant vessels (e.g. splashdown site 37km away) take much longer
            // than 10 frames to load — KSP must range-load them first.
            float deadline = UnityEngine.Time.time + 5f;
            while (UnityEngine.Time.time < deadline)
            {
                yield return null;
                Vessel v = FlightGlobals.Vessels?.Find(vessel => vessel.persistentId == pid
                    && !GhostMapPresence.IsGhostMapVessel(vessel.persistentId));
                if (v != null)
                {
                    // Only activate if within physics range. ForceSetActiveVessel on an
                    // unloaded vessel triggers a full FLIGHT→FLIGHT scene reload (black
                    // screen flash). Better to stay on the pad and let the player switch
                    // manually via tracking station or map view.
                    if (!v.loaded)
                    {
                        Log($"Spawned vessel pid={pid} not loaded — staying on current vessel");
                        yield break;
                    }
                    v.IgnoreGForces(240);
                    FlightGlobals.ForceSetActiveVessel(v);
                    Log($"Activated spawned vessel pid={pid} ({v.vesselName})");
                    yield break;
                }
            }
            Log($"WARNING: Could not find spawned vessel pid={pid} within 5 seconds");
        }

        /// <summary>
        /// Static query: returns chain status text for a recording, or null if not in a chain.
        /// Checks whether the recording's vessel PID matches any active ghost chain,
        /// or whether the recording is the tip recording for any chain.
        /// Callable from ParsekUI without needing an instance reference.
        /// </summary>
        internal static string GetChainStatusForRecording(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0 || rec == null)
                return null;

            // Check if this recording's vessel is ghosted (vessel PID match)
            GhostChain vesselChain;
            if (rec.VesselPersistentId != 0 &&
                chains.TryGetValue(rec.VesselPersistentId, out vesselChain))
            {
                var info = vesselChain;
                return SpawnWarningUI.FormatChainStatus(info, rec.VesselName);
            }

            // Check if this recording is the tip recording for any chain
            foreach (var kvp in chains)
            {
                if (kvp.Value.TipRecordingId == rec.RecordingId)
                {
                    return string.Format(CultureInfo.InvariantCulture,
                        "Chain tip -- will spawn vessel at UT={0}",
                        kvp.Value.SpawnUT.ToString("F0", CultureInfo.InvariantCulture));
                }
            }

            return null;
        }

        internal static SpawnWarpOutcomeInput BuildSpawnWarpOutcomeInput(
            Recording rec, double targetUT, double elapsedSeconds, bool chainSpawnBlocked,
            bool walkbackExhaustedAtArm, int collisionBlockCountAtArm)
        {
            if (rec == null)
                return new SpawnWarpOutcomeInput { RecordingFound = false, ElapsedSeconds = elapsedSeconds };
            return new SpawnWarpOutcomeInput
            {
                RecordingFound = true,
                SpawnedVesselPersistentId = rec.SpawnedVesselPersistentId,
                VesselSpawned = rec.VesselSpawned,
                SpawnAbandoned = rec.SpawnAbandoned,
                KscRetired = rec.VesselSpawned && rec.SpawnedVesselPersistentId == 0
                    && VesselSpawner.IsSettledAsKscRetirement(rec),
                WalkbackExhausted = rec.WalkbackExhausted,
                CollisionBlockCount = rec.CollisionBlockCount,
                WalkbackExhaustedAtArm = walkbackExhaustedAtArm,
                CollisionBlockCountAtArm = collisionBlockCountAtArm,
                ChainSpawnBlocked = chainSpawnBlocked,
                SpawnAttempts = rec.SpawnAttempts,
                TerminalSpawnCannotSpawnSafely = rec.TerminalSpawnCannotSpawnSafely,
                TerminalSpawnSafetyDeferred = rec.TerminalSpawnSafetyDeferred,
                TerminalSpawnSafetyReasonCode = rec.TerminalSpawnSafetyReasonCode,
                TerminalSpawnNextAttemptUT = rec.TerminalSpawnNextAttemptUT,
                TerminalDecisionAfterWarp = rec.TerminalSpawnSafetyDecisionUT >= targetUT,
                AbandonNote = SpawnWarpOutcomeWatch.GetAbandonNote(rec.RecordingId),
                ElapsedSeconds = elapsedSeconds
            };
        }
    }
}
