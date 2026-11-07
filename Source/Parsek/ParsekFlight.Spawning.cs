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
        /// Evaluates ghost chains from committed trees and ghosts claimed vessels.
        /// Called during OnFlightReady after initialization/migration but before
        /// pending tree dialog. Ghost chain state is NOT persisted — it is re-derived
        /// from committed recordings on every scene load.
        /// </summary>
        private void EvaluateAndApplyGhostChains()
        {
            ghostChainEvaluationCount++;
            var trees = RecordingStore.CommittedTrees;
            if (trees == null || trees.Count == 0)
            {
                activeGhostChains = null;
    
                ParsekLog.Verbose("Ghoster", "EvaluateAndApplyGhostChains: no committed trees");
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            var chains = GhostChainWalker.ComputeAllGhostChains(trees, currentUT);

            if (chains == null || chains.Count == 0)
            {
                activeGhostChains = null;
    
                ParsekLog.Verbose("Ghoster", "EvaluateAndApplyGhostChains: no ghost chains found");
                return;
            }

            if (vesselGhoster == null)
                vesselGhoster = new VesselGhoster();

            // Only store and ghost chains whose spawn UT is in the future.
            // Past-UT chains are already resolved — storing them would cause
            // spurious spawn suppression and duplicate chain-tip spawn attempts.
            int ghostedCount = FilterAndGhostChains(chains, currentUT, out var activeChains);

            activeGhostChains = activeChains;

            // Wire the ghosted-vessel check for ShouldSkipExternalVesselGhost (Task 6b-3b)
            GhostPlaybackLogic.SetIsGhostedOverride(
                pid => vesselGhoster != null && vesselGhoster.IsGhosted(pid));

            ParsekLog.Info("Ghoster",
                string.Format(CultureInfo.InvariantCulture,
                    "Ghost chains evaluated: {0} chain(s), {1} vessel(s) ghosted",
                    chains.Count, ghostedCount));
        }

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

        private static string LiveVesselGuidOrNull(uint pid)
        {
            Vessel vessel = FlightRecorder.FindVesselByPid(pid);
            return vessel != null && vessel.id != Guid.Empty ? vessel.id.ToString("N") : null;
        }

        /// <summary>
        /// Filters chains to only future-UT non-terminated chains, creates ghost map vessels
        /// for orbital tips, and ghosts real vessels in the scene.
        /// Returns the number of vessels successfully ghosted.
        /// </summary>
        private int FilterAndGhostChains(Dictionary<uint, GhostChain> chains, double currentUT,
            out Dictionary<uint, GhostChain> activeChains)
        {
            activeChains = new Dictionary<uint, GhostChain>();
            int ghostedCount = 0;
            foreach (var kvp in chains)
            {
                uint pid = kvp.Key;
                GhostChain chain = kvp.Value;

                if (!GhostChainWalker.ShouldGhostChainAtUT(chain, currentUT))
                {
                    ParsekLog.Verbose("Ghoster",
                        string.Format(CultureInfo.InvariantCulture,
                            "Skipping chain for pid={0}: currentUT={1:F1} >= spawnUT={2:F1}",
                            pid, currentUT, chain.SpawnUT));
                    continue;
                }

                // #174: Skip terminated chains — vessel destroyed/recovered, can never produce ghost
                if (chain.IsTerminated)
                {
                    ParsekLog.Verbose("Ghoster",
                        string.Format(CultureInfo.InvariantCulture,
                            "Skipping terminated chain for pid={0}", pid));
                    continue;
                }

                activeChains[pid] = chain;

                // Create ghost map vessel for chains ending in stable orbit
                // (terminated chains already filtered by the continue above)
                Recording tipRecording = string.IsNullOrEmpty(chain.TipRecordingId)
                    ? null
                    : FindTipRecordingById(chain.TipRecordingId);
                if (tipRecording != null
                    && (!tipRecording.TerminalStateValue.HasValue
                        || tipRecording.TerminalStateValue == TerminalState.Orbiting
                        || tipRecording.TerminalStateValue == TerminalState.Docked))
                    GhostMapPresence.CreateGhostVessel(chain, tipRecording);

                // Every vessel the chain holds back (its key and the claims folded into it),
                // and only those that exist in the scene. Tree recordings may reference vessels
                // that were never spawned (e.g., synthetic test data or vessels in a different
                // SOI/scene).
                List<uint> claimPids = SelectChainPidsToGhost(pid, chain, LiveVesselGuidOrNull);
                for (int c = 0; c < claimPids.Count; c++)
                {
                    uint claimPid = claimPids[c];
                    if (vesselGhoster.IsGhosted(claimPid))
                    {
                        ParsekLog.Verbose("Ghoster", string.Format(CultureInfo.InvariantCulture,
                            "Chain vessel pid={0} already ghosted (chain={1})", claimPid, pid));
                        continue;
                    }
                    Vessel chainVessel = FlightRecorder.FindVesselByPid(claimPid);
                    if (chainVessel == null)
                    {
                        ParsekLog.Verbose("Ghoster", string.Format(CultureInfo.InvariantCulture,
                            "Chain vessel pid={0} not loaded in scene - nothing to ghost (chain={1})",
                            claimPid, pid));
                    }
                    else if (vesselGhoster.GhostVessel(claimPid))
                    {
                        ghostedCount++;
                        // A Re-Fly can keep the tip stamped as this very vessel; it is gone now.
                        VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(
                            tipRecording, claimPid, "chain=" + pid.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        ParsekLog.Warn("Ghoster", string.Format(CultureInfo.InvariantCulture,
                            "Failed to ghost vessel pid={0} for chain={1} - vessel may remain real",
                            claimPid, pid));
                    }
                }
            }
            return ghostedCount;
        }

        // Phase F: ComputeTreeDeltaFunds/Science/Reputation removed.
        // Tree-level snapshot reconciliation replaced by per-action ledger walk
        // (see GameActions/RecalculationEngine + LedgerOrchestrator).

        /// <summary>
        /// Spawns all leaf vessels from a committed tree (except the active vessel).
        /// Handles crew reservation (already done by caller), proximity offsets.
        /// </summary>
        void SpawnTreeLeaves(RecordingTree tree, string activeRecordingId)
        {
            var spawnableLeaves = tree.GetSpawnableLeaves();
            if (spawnableLeaves.Count == 0)
            {
                ParsekLog.Info("Flight", "SpawnTreeLeaves: no spawnable leaves");
                return;
            }

            ParsekLog.Info("Flight", $"SpawnTreeLeaves: {spawnableLeaves.Count} spawnable leaf/leaves");

            foreach (var leaf in spawnableLeaves)
            {
                // Skip the active vessel — it's already in-game
                if (leaf.RecordingId == activeRecordingId)
                    continue;

                if (leaf.VesselSnapshot == null)
                {
                    ParsekLog.Warn("Flight", $"SpawnTreeLeaves: leaf '{leaf.RecordingId}' has null snapshot — skipping");
                    continue;
                }

                if (VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                    leaf,
                    "Flight",
                    $"SpawnTreeLeaves leaf '{leaf.VesselName ?? leaf.RecordingId}'"))
                {
                    leaf.LastAppliedResourceIndex = leaf.Points.Count - 1;
                    continue;
                }

                // Operator ruling 2026-09-23: a leaf whose flight ended parked in the KSC
                // exclusion zone is retired (settled, no vessel). Checked before the
                // same-name recovery below so a retirement never recovers anything.
                if (VesselSpawner.TryRetireEndedFlightAtKsc(leaf, -1))
                {
                    ParsekLog.Info("Flight",
                        $"SpawnTreeLeaves: retired leaf '{leaf.VesselName}' (flight ended in the KSC exclusion zone)");
                    continue;
                }

                // Before spawning, recover any timeline-spawned copy of the SAME vessel
                // (by launch identity, never by name) to prevent overlap collisions (e.g. an
                // older committed recording of this vessel already spawned it on the pad).
                RecoverTimelineSpawnedVessel(leaf);

                try
                {
                    // Route scene-load leaf spawns through the shared end-of-recording helper
                    // so EVA/orbital paths rebuild endpoint-aligned ORBIT data the same way
                    // the in-flight spawn path does.
                    if (leaf.SpawnAttempts > 0)
                    {
                        ParsekLog.Verbose("Flight",
                            $"SpawnTreeLeaves: leaf '{leaf.VesselName}' entering shared spawn path " +
                            $"with prior attempts={leaf.SpawnAttempts} (shared helper caps retries at 3)");
                    }

                    VesselSpawner.SpawnOrRecoverIfTooClose(leaf, -1);
                    if (leaf.SpawnedVesselPersistentId != 0)
                    {
                        leaf.LastAppliedResourceIndex = leaf.Points.Count - 1;
                        ParsekLog.Info("Flight",
                            $"SpawnTreeLeaves: spawned leaf '{leaf.VesselName}' pid={leaf.SpawnedVesselPersistentId}");
                    }
                    else if (leaf.SpawnAbandoned)
                    {
                        ParsekLog.Warn("Flight",
                            $"SpawnTreeLeaves: abandoned leaf '{leaf.VesselName}' without a spawned vessel");
                    }
                    else
                    {
                        ParsekLog.Warn("Flight", $"SpawnTreeLeaves: failed to spawn leaf '{leaf.VesselName}'");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Flight", $"SpawnTreeLeaves: exception spawning '{leaf.VesselName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Recovers orphaned spawned vessels after a revert/rewind. Matches by
        /// persistentId (reliable in Flight) and vessel name (fallback).
        /// Skips the active vessel.
        /// </summary>
        // BUG-H: belt-and-suspenders revert cleanup of LOADED vessels that the OnLoad protoVessel
        // strip did not catch (FLIGHT->FLIGHT launch revert recovers loaded vessels). The recover
        // decision routes through the same launch-identity-aware + scoped predicate as the OnLoad
        // strip (ParsekScenario.ShouldStripVesselForRecordings): a vessel is recovered ONLY when it
        // is the same launch as a committed recording's spawn/adoption endpoint (pid + Vessel.id Guid)
        // AND it appeared during the reverted flight (not in the revert-target launch quicksave).
        // Name-only matching previously recovered real, separately-launched craft that merely reuse a
        // recording's craft-baked name/pid.
        void CleanupOrphanedSpawnedVessels(
            IReadOnlyList<Recording> recordings, HashSet<uint> preExistingWhitelist)
        {
            ParsekLog.Info("Flight",
                $"CleanupOrphanedSpawnedVessels: starting with " +
                $"{recordings?.Count ?? 0} committed recording(s), " +
                $"preExistingWhitelist={preExistingWhitelist?.Count ?? 0} pid(s), " +
                $"{FlightGlobals.Vessels.Count} loaded vessel(s)");
            var activeVessel = FlightGlobals.ActiveVessel;
            uint activePid = activeVessel != null ? activeVessel.persistentId : 0;
            int recovered = 0;
            int keptPreExisting = 0;
            int keptDifferentLaunch = 0;

            for (int i = FlightGlobals.Vessels.Count - 1; i >= 0; i--)
            {
                var vessel = FlightGlobals.Vessels[i];
                if (vessel.persistentId == activePid)
                {
                    ParsekLog.Verbose("Flight",
                        $"CleanupOrphanedSpawnedVessels: skipping active vessel '{vessel.vesselName}' pid={vessel.persistentId}");
                    continue;
                }
                if (GhostMapPresence.IsGhostMapVessel(vessel.persistentId))
                {
                    ParsekLog.Verbose("Flight",
                        $"CleanupOrphanedSpawnedVessels: skipping ghost map vessel pid={vessel.persistentId}");
                    continue;
                }

                string candidateGuid = vessel.id != Guid.Empty ? vessel.id.ToString("N") : null;
                bool shouldRecover = ParsekScenario.ShouldStripVesselForRecordings(
                    vessel.persistentId, candidateGuid, vessel.situation, recordings,
                    matchSource: false, skipPrelaunch: true,
                    requireWhitelist: true, preExistingWhitelist: preExistingWhitelist,
                    out Recording matched, out string reason);

                if (shouldRecover)
                {
                    ParsekLog.Info("Flight",
                        $"CleanupOrphanedSpawnedVessels: recovering '{vessel.vesselName}' " +
                        $"pid={vessel.persistentId} guid={candidateGuid ?? "(none)"} — matched recording " +
                        $"'{matched?.VesselName ?? "(unknown)"}' (recordedGuid={matched?.RecordedVesselGuid ?? "(none)"}); {reason}");
                    // Parsek's OWN recovery, not the player's. Stock VesselRecovery archives
                    // every crew member's flight log and fires onVesselRecoveryProcessing
                    // unconditionally, so without this guard the P9a experience handler
                    // credits career XP for a recovery the player never performed.
                    using (SuppressionGuard.Crew())
                    {
                        ShipConstruction.RecoverVesselFromFlight(
                            vessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                    }
                    recovered++;
                }
                else
                {
                    if (reason != null && reason.StartsWith("pre-existing"))
                        keptPreExisting++;
                    else if (reason != null && reason.StartsWith("no same-launch"))
                        keptDifferentLaunch++;
                    ParsekLog.Verbose("Flight",
                        $"CleanupOrphanedSpawnedVessels: keeping '{vessel.vesselName}' " +
                        $"pid={vessel.persistentId} guid={candidateGuid ?? "(none)"} — {reason}");
                }
            }

            if (recovered > 0 || keptPreExisting > 0 || keptDifferentLaunch > 0)
                ParsekLog.Info("Flight",
                    $"CleanupOrphanedSpawnedVessels: recovered {recovered} orphaned vessel(s) " +
                    $"(kept {keptPreExisting} pre-existing, {keptDifferentLaunch} different-launch)");
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
        /// Finds and recovers any in-scene vessel that was timeline-spawned from a
        /// committed recording of the same vessel as <paramref name="leaf"/>
        /// (<see cref="IsTimelineSpawnOfLeafVessel"/>). Prevents overlap collisions when
        /// a tree commit spawns a vessel on top of an already-spawned timeline vessel.
        /// </summary>
        void RecoverTimelineSpawnedVessel(Recording leaf)
        {
            string vesselName = leaf?.VesselName;
            var committed = RecordingStore.CommittedRecordings;
            int skippedSameNameOtherVessel = 0;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!IsTimelineSpawnOfLeafVessel(rec, leaf))
                {
                    if (rec != null && rec.VesselSpawned && rec.SpawnedVesselPersistentId != 0
                        && !string.IsNullOrEmpty(vesselName)
                        && string.Equals(rec.VesselName, vesselName, StringComparison.Ordinal))
                        skippedSameNameOtherVessel++;
                    continue;
                }

                // Find the loaded vessel by persistent ID
                Vessel vessel = null;
                for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
                {
                    if (FlightGlobals.Vessels[v].persistentId == rec.SpawnedVesselPersistentId)
                    {
                        vessel = FlightGlobals.Vessels[v];
                        break;
                    }
                }
                if (vessel != null && vessel.loaded)
                {
                    // BUG-H: the loaded vessel matched by craft-baked pid may be a DIFFERENT real
                    // launch that merely reuses the pid. Only recover it when it is genuinely the
                    // same launch this recording spawned/adopted (Guid gate; falls back to pid-only
                    // when a Guid is unknown). A conclusive Guid mismatch is never recovered — it is
                    // an unrelated real vessel. The recording's stale spawn tracking is still reset
                    // below so it can re-spawn its own vessel cleanly.
                    string vGuid = vessel.id != System.Guid.Empty ? vessel.id.ToString("N") : null;
                    if (VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, vessel.persistentId, vGuid))
                    {
                        ParsekLog.Info("Flight",
                            $"RecoverTimelineSpawnedVessel: recovering '{vesselName}' pid={rec.SpawnedVesselPersistentId} " +
                            $"guid={vGuid ?? "(none)"} (from recording #{i}) to make room for tree leaf spawn");
                        // Parsek's OWN recovery — suppress the crew events stock fires from
                        // VesselRecovery so the P9a experience handler does not credit career
                        // XP for a recovery the player never performed.
                        using (SuppressionGuard.Crew())
                        {
                            ShipConstruction.RecoverVesselFromFlight(vessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                        }
                    }
                    else
                    {
                        ParsekLog.Info("Flight",
                            $"RecoverTimelineSpawnedVessel: NOT recovering '{vesselName}' pid={vessel.persistentId} " +
                            $"guid={vGuid ?? "(none)"} — different launch than recording #{i} " +
                            $"(recordedGuid={rec.RecordedVesselGuid ?? "(none)"}); leaving the real vessel intact");
                    }
                }

                // Mark recording as no longer having a live vessel
                rec.VesselSpawned = false;
                rec.SpawnedVesselPersistentId = 0;
                TerminalOrbitSpawnSafety.Clear(rec);
            }

            if (skippedSameNameOtherVessel > 0)
                ParsekLog.Info("Flight",
                    $"RecoverTimelineSpawnedVessel: left {skippedSameNameOtherVessel} same-named timeline " +
                    $"spawn(s) of OTHER vessels alone for leaf '{vesselName ?? "(null)"}' " +
                    $"(launch guid {leaf?.RecordedVesselGuid ?? "(none)"} differs; a name is not an identity)");
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
            SpawnVesselOrChainTip(rec, index);
        }

        /// <summary>
        /// Remove the ghost map vessel for a chain on spawn and transfer nav target
        /// to the newly spawned real vessel if the ghost was the current target.
        /// </summary>
        private void ResolveChainGhostOnSpawn(GhostChain chain, uint spawnedPid)
        {
            bool wasTarget = GhostMapPresence.RemoveGhostVessel(
                chain.OriginalVesselPid, "chain-tip-spawn");
            if (wasTarget)
            {
                Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid);
                if (spawned != null)
                    FlightGlobals.fetch?.SetVesselTarget(spawned);
            }
        }

        /// <summary>
        /// The stale vessel a chain-tip replacement removed was the navigation target: the
        /// target moves to the tip that replaced it (same pid, new Vessel object).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RetargetReplacedChainTip(uint spawnedPid)
        {
            Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid);
            if (spawned == null || FlightGlobals.fetch == null)
                return;
            FlightGlobals.fetch.SetVesselTarget(spawned);
            ParsekLog.Info("Flight",
                $"Navigation target moved to replaced chain tip pid={spawnedPid}");
        }

        /// <summary>
        /// A chain whose tip flight ended parked in the KSC exclusion zone (operator
        /// ruling 2026-09-23): the tip is settled with no vessel, so the chain is done.
        /// Drop it from the active set and remove its ghost map vessel, the same cleanup a
        /// tip spawn does minus the target transfer (there is no vessel to target).
        /// </summary>
        private void RetireChainAtKsc(GhostChain chain, Recording rec, int index)
        {
            activeGhostChains.Remove(chain.OriginalVesselPid);
            VesselGhoster.ReleaseChainRetiredAtKsc(
                chain, $"flight #{index} \"{rec.VesselName}\"");
        }

        /// <summary>Called by policy to switch camera to a spawned vessel after watch-mode spawn.</summary>
        internal void DeferredActivateVesselFromPolicy(uint vesselPid) =>
            StartCoroutine(DeferredActivateVessel(vesselPid));

        private void SpawnVesselOrChainTip(Recording rec, int index)
        {
            // Spawner-side backstop for ComputePlaybackFlags' live-flown hold: every flight
            // end-of-recording spawn (completion, warp-deferred flush, held-ghost retry, chain
            // tip) passes here, and the recording being flown is the live vessel, so its
            // terminal spawn would be a duplicate (todo REFLY-FORK-SELF-SPAWN-AFTER-QUICKSAVE).
            // Keyed on recording id, not launch guid: a revert re-flies the same launch guid
            // while a committed recording of that launch legitimately spawns (#226).
            string liveFlownReason;
            if (GhostPlaybackLogic.ShouldRefuseLiveFlownSpawn(
                    rec?.RecordingId,
                    SessionSuppressionState.ActiveMarker?.ActiveReFlyRecordingId,
                    ResolveLiveRecorderRecordingId(),
                    out liveFlownReason))
            {
                ParsekLog.InfoRateLimited("Spawner",
                    "live-flown-spawn-refused|" + (rec?.RecordingId ?? "(none)"),
                    $"Spawn refused for #{index} ({rec?.VesselName ?? "(unknown)"}) " +
                    $"rec={rec?.RecordingId ?? "(none)"}: {liveFlownReason}");
                return;
            }

            // A Ready placement jump armed for this recording places the player's group next
            // frame; the spawn waits for it so its collision check sees the placed group (the
            // policy holds the ghost and retries).
            if (TimeJumpManager.IsSpawnDeferredForPendingPlacement(rec?.RecordingId))
            {
                ParsekLog.InfoRateLimited("Spawner",
                    "placement-pending-spawn|" + rec.RecordingId,
                    $"Spawn deferred for #{index} ({rec.VesselName}) rec={rec.RecordingId}: " +
                    "Ready placement pending (the group is placed first)");
                return;
            }

            // Defense-in-depth: the spawn gate re-hydrates the vessel snapshot
            // from the sidecar, but a deferred (warp-queued) spawn can execute
            // frames later, after an intervening OnSave nulled the in-memory copy
            // again. Re-hydrate here so the snapshot is present at the moment of
            // spawn. No-op when already loaded.
            RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar(rec);

            // Existing source-vessel adoption is enforced by VesselSpawner. The #226
            // replay/revert path remains an explicit duplicate-spawn exception for the
            // scene-entry/current active vessel only.
            bool allowExistingSourceDuplicate =
                VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(rec);

            GhostChain chain = FindChainTipForRecording(activeGhostChains, rec);
            if (chain != null && vesselGhoster != null)
            {
                if (chain.SpawnBlocked)
                {
                    // Chain was previously blocked — retry at propagated position
                    double currentUT = Planetarium.GetUniversalTime();
                    uint spawnedPid = vesselGhoster.TrySpawnBlockedChain(
                        chain,
                        currentUT,
                        allowExistingSourceDuplicate);
                    if (spawnedPid != 0)
                    {
                        rec.SpawnedVesselPersistentId = spawnedPid;
                        rec.VesselSpawned = true;
                        activeGhostChains.Remove(chain.OriginalVesselPid);
                        ResolveChainGhostOnSpawn(chain, spawnedPid);

                        ParsekLog.Info("Flight",
                            $"Blocked chain tip spawn resolved: #{index} \"{rec.VesselName}\" pid={spawnedPid}");
                    }
                    else if (VesselSpawner.IsSettledAsKscRetirement(rec))
                    {
                        RetireChainAtKsc(chain, rec, index);
                    }
                    // If still blocked: chain stays in activeGhostChains, no spawn
                }
                else
                {
                    uint spawnedPid = vesselGhoster.SpawnAtChainTip(
                        chain,
                        allowExistingSourceDuplicate);
                    if (spawnedPid != 0)
                    {
                        rec.SpawnedVesselPersistentId = spawnedPid;
                        rec.VesselSpawned = true;
                        activeGhostChains.Remove(chain.OriginalVesselPid);
                        ResolveChainGhostOnSpawn(chain, spawnedPid);

                        ParsekLog.Info("Flight",
                            $"Chain tip spawn complete: #{index} \"{rec.VesselName}\" pid={spawnedPid}");
                    }
                    else if (VesselSpawner.IsSettledAsKscRetirement(rec))
                    {
                        RetireChainAtKsc(chain, rec, index);
                    }
                    else if (chain.SpawnBlocked)
                    {
                        // SpawnAtChainTip detected overlap — chain stays active, not removed
                        ParsekLog.Info("Flight",
                            $"Chain tip spawn blocked by collision: #{index} \"{rec.VesselName}\" — chain stays active");
                    }
                    else if (TerminalOrbitSpawnSafety.HasActiveHold(rec))
                    {
                        ParsekLog.InfoRateLimited("Flight",
                            "chain-tip-orbit-safety|" + (rec.RecordingId ?? "(none)"),
                            $"Chain tip spawn held by terminal orbit safety: #{index} \"{rec.VesselName}\" " +
                            $"cannotSpawnSafely={rec.TerminalSpawnCannotSpawnSafely} " +
                            $"reason={rec.TerminalSpawnSafetyReasonCode ?? "(none)"}");
                    }
                    else
                    {
                        ParsekLog.Warn("Flight",
                            $"Chain tip spawn failed: #{index} \"{rec.VesselName}\"");
                    }
                }
            }
            else
            {
                // No active chain here (the flight loaded past the tip's spawn UT, so
                // FilterAndGhostChains never ghosted the claimed vessel): a live vessel with the
                // chain's claimed pid after a rewind to before the tip is that vessel in its
                // pre-claim form. It is replaced by the tip with its identity preserved, the
                // same end state the chain path reaches, instead of adopted or left beside it.
                StaleVesselReplacement staleReplacement = ChainTipStaleVessel.TryReplaceStaleSourceBeforeSpawn(
                    rec,
                    "FLIGHT",
                    index,
                    allowExistingSourceDuplicate);
                bool replacedStaleVessel = staleReplacement != null;
                try
                {
                    if (LeafSpawnOverrideForTesting != null)
                        LeafSpawnOverrideForTesting(rec, index, replacedStaleVessel, allowExistingSourceDuplicate);
                    else
                        VesselSpawner.SpawnOrRecoverIfTooClose(
                            rec,
                            index,
                            preserveIdentity: replacedStaleVessel,
                            allowExistingSourceDuplicate: allowExistingSourceDuplicate);
                }
                finally
                {
                    // A replacement whose tip spawned no vessel puts the removed vessel back.
                    ChainTipStaleVessel.CompleteReplacement(rec, staleReplacement);
                }
                if (replacedStaleVessel
                    && staleReplacement.Focus.WasNavigationTarget
                    && rec.SpawnedVesselPersistentId != 0)
                    RetargetReplacedChainTip(rec.SpawnedVesselPersistentId);
            }
        }


        // UpdateWatchCamera, ProcessWatchEndHoldTimer, FindWatchedGhostState,
        // TryResolveOverlapRetarget moved to WatchModeController

        bool IsAnyWarpActive() => GhostPlaybackEngine.IsAnyWarpActiveFromGlobals();

        void ExitAllWarpForPlaybackStart(string context)
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

        private static string DetermineRecordingBlockReason()
        {
            if (Time.timeScale < 0.01f)
                return "game paused";
            if (FlightGlobals.ActiveVessel == null)
                return "no active vessel";
            return "unknown guard in FlightRecorder.StartRecording";
        }

        /// <summary>
        /// Draws floating labels on all ghost GOs from active ghost chains.
        /// Called from OnGUI. If ghost GOs are null (deferred from 6b-4), this is a no-op.
        /// </summary>
        private void DrawGhostLabels()
        {
            if (activeGhostChains == null || activeGhostChains.Count == 0)
                return;

            Camera cam = FlightCamera.fetch?.mainCamera;
            if (cam == null)
                return;

            if (ghostLabelStyle == null)
            {
                ghostLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = new Color(1f, 0.9f, 0.5f, 0.9f) }
                };
            }

            foreach (var kvp in activeGhostChains)
            {
                GhostChain chain = kvp.Value;
                if (vesselGhoster == null) continue;

                GameObject ghostGO = vesselGhoster.GetGhostGO(chain.OriginalVesselPid);
                if (ghostGO == null) continue;

                // Convert ghost world position to screen position
                Vector3 screenPos = cam.WorldToScreenPoint(ghostGO.transform.position);

                // Behind camera check
                if (screenPos.z < 0) continue;

                // Off-screen check
                if (screenPos.x < 0 || screenPos.x > Screen.width ||
                    screenPos.y < 0 || screenPos.y > Screen.height)
                    continue;

                // Get vessel name from ghosted info
                var info = vesselGhoster.GetGhostedInfo(chain.OriginalVesselPid);
                string vesselName = info != null ? info.vesselName : "(unknown)";

                string labelText = SpawnWarningUI.ComputeGhostLabelText(
                    vesselName, chain.SpawnUT, chain.IsTerminated, chain.SpawnBlocked,
                    chain.WalkbackExhausted);

                // Unity screen coordinates have Y=0 at bottom; GUI has Y=0 at top
                float guiY = Screen.height - screenPos.y;
                float labelWidth = 250f;
                float labelHeight = 40f;
                Rect labelRect = new Rect(
                    screenPos.x - labelWidth / 2f,
                    guiY - labelHeight,
                    labelWidth,
                    labelHeight);

                GUI.Label(labelRect, labelText, ghostLabelStyle);
            }
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

        // ════════════════════════════════════════════════════════════════
        //  Real Spawn Control: proximity check and warp
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Periodic proximity scan: finds nearby ghost craft whose recording ends
        /// in the future and would spawn a real vessel. Runs every 1.5s from Update().
        /// Fires a one-time screen notification when a new candidate enters range.
        /// </summary>
        private void UpdateProximityCheck()
        {
            if (Time.time < nextProximityCheckTime) return;
            nextProximityCheckTime = Time.time + ProximityCheckIntervalSec;

            nearbySpawnCandidates.Clear();

            if (FlightGlobals.ActiveVessel == null) return;

            Vector3d activePos = FlightGlobals.ActiveVessel.GetWorldPos3D();
            uint activePid = FlightGlobals.ActiveVessel.persistentId;
            if (activePid != lastProximityActiveVesselPid)
            {
                if (lastProximityActiveVesselPid != 0 && proximityVelocitySamples.Count > 0)
                    ParsekLog.Verbose("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "Proximity check: active vessel changed pid {0} -> {1}, dropped {2} velocity sample(s)",
                            lastProximityActiveVesselPid, activePid, proximityVelocitySamples.Count));
                proximityVelocitySamples.Clear();
                lastProximityActiveVesselPid = activePid;
            }
            double currentUT = Planetarium.GetUniversalTime();
            var committed = RecordingStore.CommittedRecordings;

            CollectNearbySpawnCandidates(activePos, currentUT, committed);

            // Sort by distance (closest first)
            nearbySpawnCandidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            proximityCheckGeneration++;

            NotifyNewProximityCandidates(currentUT);
        }

        /// <summary>
        /// Scans active ghosts for spawn-eligible recordings within proximity range.
        /// Populates nearbySpawnCandidates with qualifying entries.
        /// </summary>
        private void CollectNearbySpawnCandidates(Vector3d activePos, double currentUT, IReadOnlyList<Recording> committed)
        {
            int admittedOverSpeed = 0;
            int skippedSeeding = 0;
            int hiddenBeyondRadius = 0;
            float now = Time.time;
            // Track which recordings still have an active ghost so we can prune stale samples.
            var seenRecordingIds = new HashSet<string>();

            foreach (var kvp in ghostStates)
            {
                int i = kvp.Key;
                GhostPlaybackState state = kvp.Value;
                if (state == null || state.ghost == null || !state.ghost.activeSelf)
                    continue;
                if (i < 0 || i >= committed.Count)
                    continue;

                Recording rec = committed[i];
                if (!GhostPlaybackLogic.IsNearbySpawnCandidateEligible(
                        rec, currentUT, activeGhostChains))
                    continue;

                Vector3d ghostPos = state.ghost.transform.position;
                double dist = Vector3d.Distance(activePos, ghostPos);
                // Speed tracking radius: ghosts beyond it are neither sampled nor listed.
                if (dist > NearbySpawnTrackRadius)
                    continue;

                // Frame-agnostic relative-speed sample. We compute d(active_pos - ghost_pos)/dt
                // across consecutive proximity scans rather than subtracting velocity vectors
                // from KSP and ghost playback (those are not in a guaranteed common frame —
                // TrajectoryPoint.velocity is "not guaranteed surface-relative" and orbit-only
                // ghosts never seed lastInterpolatedVelocity at all). Both samples are read
                // from transform.position in the same Update tick, so floating-origin and
                // krakensbane shifts cancel in the per-sample relative vector.
                //
                // Listing (SelectiveSpawnUI.IsListedCandidate): inside NearbySpawnRadius and
                // under MaxListRelativeSpeed. Warp (SpawnControlPresentation): additionally
                // under MaxRelativeSpeed; a slower-gate miss is a greyed "Too fast" row.
                seenRecordingIds.Add(rec.RecordingId);
                bool hasPrev = proximityVelocitySamples.TryGetValue(rec.RecordingId, out var prev);
                // Always overwrite the sample so the next scan can compute against this one.
                proximityVelocitySamples[rec.RecordingId] = new ProximityVelocitySample
                {
                    activePos = activePos,
                    ghostPos = ghostPos,
                    time = now
                };

                if (!hasPrev)
                {
                    // First sighting in range — seed only, defer admission until we have
                    // a second sample to derive a velocity from.
                    skippedSeeding++;
                    continue;
                }

                float dt = now - prev.time;
                double relSpeed = SelectiveSpawnUI.ComputeRelativeSpeed(
                    activePos, ghostPos, prev.activePos, prev.ghostPos, dt,
                    ProximityVelocitySampleMinDt, ProximityVelocitySampleMaxDt);
                if (!SelectiveSpawnUI.IsListedCandidate(
                        dist, relSpeed, NearbySpawnRadius, MaxListRelativeSpeed))
                {
                    if (dist > NearbySpawnRadius)
                        hiddenBeyondRadius++;
                    continue;
                }
                if (relSpeed > MaxRelativeSpeed)
                    admittedOverSpeed++;

                var depInfo = SelectiveSpawnUI.ComputeDepartureInfo(rec, currentUT);
                // A Leaves or Ready row's Warp moves an orbital player's group (design 4.3);
                // its gates are evaluated here so the button greys before the click.
                MoveGateVerdict moveGate;
                if (depInfo.willDepart)
                    moveGate = depInfo.departureUT > currentUT
                        ? EvaluateLeavesMoveGateForScan(rec, currentUT, depInfo.departureUT)
                        : MoveGateVerdict.Pass;
                else
                    moveGate = rec.EndUT > currentUT
                        ? EvaluateReadyMoveGateForScan(rec, currentUT)
                        : MoveGateVerdict.Pass;
                nearbySpawnCandidates.Add(new NearbySpawnCandidate
                {
                    moveRefusal = moveGate.Refusal,
                    moveRefusalDetail = moveGate.Detail,
                    recordingIndex = i,
                    vesselName = rec.VesselName,
                    endUT = rec.EndUT,
                    distance = dist,
                    relativeSpeed = relSpeed,
                    recordingId = rec.RecordingId,
                    willDepart = depInfo.willDepart,
                    departureUT = depInfo.departureUT,
                    departureKind = depInfo.kind,
                    destination = depInfo.destination
                });
            }

            // Drop samples for recordings that are no longer in-range / ghosted, so the cache
            // doesn't grow unboundedly across a long session.
            if (proximityVelocitySamples.Count > seenRecordingIds.Count)
            {
                var stale = new List<string>();
                foreach (var key in proximityVelocitySamples.Keys)
                    if (!seenRecordingIds.Contains(key)) stale.Add(key);
                for (int s = 0; s < stale.Count; s++)
                    proximityVelocitySamples.Remove(stale[s]);
            }

            // Printed when the counts change, not every 1.5 s scan: a ghost parked just
            // outside the spawn radius would otherwise repeat the same line all session.
            if ((admittedOverSpeed > 0 || skippedSeeding > 0 || hiddenBeyondRadius > 0)
                && ParsekLog.IsVerboseEnabled)
                ParsekLog.VerboseOnChange("Flight",
                    "proximity-scan-summary",
                    string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                        admittedOverSpeed, skippedSeeding, hiddenBeyondRadius),
                    string.Format(CultureInfo.InvariantCulture,
                        "Proximity check: admitted {0} over rel-speed > {1:F1} m/s (FF gated) + {2} seeding first sample + {3} hidden beyond spawn radius {4:F0}m",
                        admittedOverSpeed, MaxRelativeSpeed, skippedSeeding,
                        hiddenBeyondRadius, NearbySpawnRadius));
        }

        /// <summary>
        /// Fires one-time screen notifications for newly discovered proximity candidates.
        /// </summary>
        private void NotifyNewProximityCandidates(double currentUT)
        {
            for (int c = 0; c < nearbySpawnCandidates.Count; c++)
            {
                var cand = nearbySpawnCandidates[c];
                // The list keeps too-fast ghosts (greyed rows), but the screen-message alert
                // promises "fast forward and interact", so it fires only once the ghost is
                // inside both warp gates.
                if (cand.distance > NearbySpawnRadius || cand.relativeSpeed > MaxRelativeSpeed)
                    continue;
                if (notifiedSpawnRecordingIds.Add(cand.recordingId))
                {
                    // The message names the Real Spawn Control window only when that window is
                    // actually reachable - Basic UI mode hides its launcher, and telling the
                    // player to open a window they have no button for is a dead end. The
                    // reachability question is asked of ParsekUI so this path stays mode-blind:
                    // it changes the WORDING, never whether the candidate was detected or what
                    // spawning does.
                    string notifyMsg = SelectiveSpawnUI.FormatProximityNotification(
                        cand, currentUT, ParsekUI.IsSpawnControlReachable);
                    ParsekLog.ScreenMessage(notifyMsg, 10f);
                    ParsekLog.Info("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "Proximity notification: '{0}' recording #{1} distance={2:F0}m endUT={3:F1}",
                            cand.vesselName, cand.recordingIndex, cand.distance, cand.endUT));
                }
            }

            if (ParsekLog.IsVerboseEnabled && nearbySpawnCandidates.Count > 0)
                ParsekLog.Verbose("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "Proximity check: {0} candidate(s) within list bounds {1:F0}m / {2:F1} m/s (FF gated by {3:F1} m/s; speed tracked to {4:F0}m)",
                        nearbySpawnCandidates.Count,
                        NearbySpawnRadius, MaxListRelativeSpeed,
                        MaxRelativeSpeed, NearbySpawnTrackRadius));
        }

        /// <summary>
        /// Executes a time jump to a recording's EndUT so it becomes a real craft.
        /// Called from ParsekUI when the player clicks a "Warp" button. An orbital player
        /// vessel moves with its group beside the ghost's recorded end state (design
        /// docs/dev/design-realspawn-time-jump-move.md 4.1); a landed, splashed or prelaunch
        /// one keeps the epoch-shift jump, which passes null chains to ExecuteJump. Either way
        /// the engine playback loop spawns the vessel on its recorded end.
        /// </summary>
        internal void WarpToRecordingEnd(int recordingIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex < 0 || recordingIndex >= committed.Count)
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToRecordingEnd: invalid index {0} — aborted", recordingIndex));
                return;
            }

            Recording rec = committed[recordingIndex];
            double targetUT = rec.EndUT;
            double currentUT = Planetarium.GetUniversalTime();

            if (!TimeJumpManager.IsValidJump(currentUT, targetUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToRecordingEnd: invalid jump current={0:F1} target={1:F1} — aborted",
                        currentUT, targetUT));
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            bool moveGroup = active != null && TimeJumpPlacement.IsOrbitalPlayerVessel(active.situation);
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "WarpToRecordingEnd: jumping to UT={0:F1} for '{1}' (delta={2:F1}s, mode={3})",
                    targetUT, rec.VesselName, targetUT - currentUT,
                    moveGroup ? "placement" : "epoch-shift"));

            if (moveGroup)
            {
                // Orbital player vessel: the group is placed beside the ghost's recorded end
                // at the new clock, and the ghost spawns on its record (design 4.1, rule 4).
                // A refusal leaves everything as it was.
                if (!TryWarpToRecordingEndWithPlacement(rec, active))
                    return;
                RefreshBlockedChainTipGhostOrbitsAfterJump();
                ArmSpawnWarpWatch(rec, targetUT);
                return;
            }

            TimeJumpManager.NotifyRecorder(recorder, currentUT, targetUT);
            // Pass null chains — let the engine playback loop handle spawn naturally
            TimeJumpManager.ExecuteJump(targetUT, null, vesselGhoster);
            RefreshBlockedChainTipGhostOrbitsAfterJump();
            ArmSpawnWarpWatch(rec, targetUT);
        }

        /// <summary>
        /// Starts watching the spawn a Warp just jumped to, so a vessel that does not
        /// appear gets one message naming why (<see cref="SpawnWarpOutcomeWatch"/>). A newer
        /// Warp replaces an unresolved older one.
        /// </summary>
        private void ArmSpawnWarpWatch(Recording rec, double targetUT)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return;
            if (spawnWarpWatchRecordingId != null)
                ParsekLog.Info("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "Warp spawn watch replaced: rec={0} by rec={1}",
                        spawnWarpWatchRecordingId, rec.RecordingId));
            SpawnWarpOutcomeWatch.ClearAbandonNote(rec.RecordingId);
            spawnWarpWatchRecordingId = rec.RecordingId;
            spawnWarpWatchVesselName = rec.VesselName;
            spawnWarpWatchTargetUT = targetUT;
            spawnWarpWatchArmedRealtime = Time.realtimeSinceStartup;
            spawnWarpWatchWalkbackAtArm = rec.WalkbackExhausted;
            spawnWarpWatchCollisionCountAtArm = rec.CollisionBlockCount;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "Warp spawn watch armed: rec={0} vessel=\"{1}\" targetUT={2:F1}",
                    rec.RecordingId, rec.VesselName, targetUT));
        }

        /// <summary>
        /// Reads the watched recording's spawn state once per frame until it resolves, then
        /// logs the outcome and, for a refused or held spawn, posts its one message.
        /// </summary>
        private void TickSpawnWarpWatch()
        {
            if (spawnWarpWatchRecordingId == null)
                return;

            Recording rec = null;
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i] != null
                    && string.Equals(committed[i].RecordingId, spawnWarpWatchRecordingId,
                        StringComparison.Ordinal))
                {
                    rec = committed[i];
                    break;
                }
            }

            double elapsed = Time.realtimeSinceStartup - spawnWarpWatchArmedRealtime;
            GhostChain chain = rec != null ? FindChainTipForRecording(activeGhostChains, rec) : null;
            SpawnWarpOutcomeInput input = BuildSpawnWarpOutcomeInput(
                rec, spawnWarpWatchTargetUT, elapsed, chain != null && chain.SpawnBlocked,
                spawnWarpWatchWalkbackAtArm, spawnWarpWatchCollisionCountAtArm);
            SpawnWarpOutcome outcome = SpawnWarpOutcomeWatch.Evaluate(
                input, SpawnControlUI.FormatRowDate);
            if (!SpawnWarpOutcomeWatch.IsResolved(outcome.Kind))
                return;

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "Warp spawn outcome: rec={0} vessel=\"{1}\" outcome={2} cause={3} elapsed={4:F1}s",
                    spawnWarpWatchRecordingId, spawnWarpWatchVesselName, outcome.Kind,
                    outcome.Cause, elapsed));
            string message = SpawnWarpOutcomeWatch.FormatMessage(
                outcome.Kind, spawnWarpWatchVesselName, outcome.Reason);
            if (!string.IsNullOrEmpty(message))
                ParsekLog.ScreenMessage(message, SpawnWarpOutcomeWatch.MessageSeconds);
            SpawnWarpOutcomeWatch.ClearAbandonNote(spawnWarpWatchRecordingId);
            spawnWarpWatchRecordingId = null;
            spawnWarpWatchVesselName = null;
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

        /// <summary>
        /// Epoch-shifted warp to a ghost's departure UT (when it leaves its current orbit).
        /// Used by Real Spawn Control when a ghost will depart before its EndUT.
        /// Preserves rendezvous geometry — both player and ghost stay at current positions.
        /// </summary>
        internal void WarpToDeparture(int recordingIndex, double departureUT)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (recordingIndex < 0 || recordingIndex >= committed.Count)
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToDeparture: invalid index {0} — aborted", recordingIndex));
                return;
            }

            Recording rec = committed[recordingIndex];
            double currentUT = Planetarium.GetUniversalTime();
            // Land RewindToLaunchLeadTimeSeconds before departure, matching Rewind's pre-launch lead.
            double targetUT = TimeJumpManager.ApplyJumpLead(departureUT, currentUT);

            if (!TimeJumpManager.IsValidJump(currentUT, targetUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "WarpToDeparture: invalid jump current={0:F1} target={1:F1} — aborted",
                        currentUT, targetUT));
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            bool moveGroup = active != null && TimeJumpPlacement.IsOrbitalPlayerVessel(active.situation);
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "WarpToDeparture: jumping to UT={0:F1} for '{1}' (delta={2:F1}s, departUT={3:F1}, endUT={4:F1}, mode={5})",
                    targetUT, rec.VesselName, targetUT - currentUT, departureUT, rec.EndUT,
                    moveGroup ? "placement" : "epoch-shift"));

            if (moveGroup)
            {
                // Orbital player vessel: the group is placed beside the ghost at the new clock
                // (design 4.1). A refusal leaves everything as it was.
                TryWarpToDepartureWithPlacement(rec, currentUT, targetUT, departureUT, active);
                return;
            }

            TimeJumpManager.NotifyRecorder(recorder, currentUT, targetUT);
            // Epoch-shifted jump: preserves rendezvous geometry
            TimeJumpManager.ExecuteJump(targetUT, null, vesselGhoster);
            RefreshBlockedChainTipGhostOrbitsAfterJump();

            ParsekLog.ScreenMessage(
                string.Format(CultureInfo.InvariantCulture,
                    "Warped to departure of \"{0}\" ({1:F0}s)",
                    rec.VesselName, targetUT - currentUT), 5f);
        }

        /// <summary>
        /// Fast-forwards to a recording's StartUT by advancing UT without epoch-shifting.
        /// Unlike WarpToRecordingEnd (which epoch-shifts for Real Spawn Control),
        /// this lets orbits propagate naturally — a regular instant time warp.
        /// Keeps the current view (KSC stays KSC, flight stays on current vessel).
        /// </summary>
        internal void FastForwardToRecording(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn("Flight", "FastForwardToRecording: null recording — aborted");
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            // Land RewindToLaunchLeadTimeSeconds before launch, matching Rewind's pre-launch lead.
            double targetUT = TimeJumpManager.ApplyJumpLead(rec.StartUT, currentUT);

            if (!RewindLauncher.CanFastForwardAtUT(rec, currentUT, out string ffReason, IsRecording))
            {
                ParsekLog.Warn("Flight",
                    $"FastForwardToRecording: '{rec.VesselName}' id={rec.RecordingId ?? "<none>"} blocked: {ffReason}");
                return;
            }

            if (!TimeJumpManager.IsValidJump(currentUT, targetUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "FastForwardToRecording: invalid jump current={0:F1} target={1:F1} — aborted",
                        currentUT, targetUT));
                return;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "FastForwardToRecording: jumping to UT={0:F1} for '{1}' (delta={2:F1}s)",
                    targetUT, rec.VesselName, targetUT - currentUT));

            // If watching, transfer watch to the FF target recording.
            // The current watched ghost may be far away after the time jump.
            if (watchMode.IsWatchingGhost)
            {
                var committed = RecordingStore.CommittedRecordings;
                int ffTargetIdx = -1;
                for (int i = 0; i < committed.Count; i++)
                {
                    if (committed[i].RecordingId == rec.RecordingId)
                    {
                        ffTargetIdx = i;
                        break;
                    }
                }
                if (ffTargetIdx >= 0 && ffTargetIdx != watchMode.WatchedRecordingIndex)
                {
                    ParsekLog.Info("CameraFollow",
                        $"FF transfer: watch #{watchMode.WatchedRecordingIndex} \u2192 #{ffTargetIdx} \"{rec.VesselName}\"");
                    // Exit current watch, ghost will be positioned by engine after time jump
                    ExitWatchMode();
                    // Defer entering watch on the target — the ghost needs to be positioned
                    // after the time jump. Store the target for post-jump pickup.
                    pendingWatchAfterFFId = rec.RecordingId;
                }
            }

            TimeJumpManager.NotifyRecorder(recorder, currentUT, targetUT);
            TimeJumpManager.ExecuteForwardJump(targetUT);

            ParsekLog.ScreenMessage(
                string.Format(CultureInfo.InvariantCulture,
                    "Fast-forwarded to \"{0}\" ({1:F0}s)",
                    rec.VesselName, targetUT - currentUT), 3f);
        }

        /// <summary>
        /// In-place fast-forward (NO scene change) to <see cref="RecordingStore.RewindToLaunchLeadTimeSeconds"/>
        /// before an arbitrary FUTURE event UT - the same mechanism as <see cref="FastForwardToRecording"/>
        /// but for a target that is not a recording (e.g. a looped Mission's next faithful relaunch
        /// window). Applies the launch lead, notifies the recorder of the jump, then advances the clock
        /// via <see cref="TimeJumpManager.ExecuteForwardJump"/>. No-op on an invalid (past / degenerate)
        /// jump. <paramref name="label"/> is used only for the log + screen message.
        /// </summary>
        internal void FastForwardToEventUT(double eventUT, string label)
        {
            double currentUT = Planetarium.GetUniversalTime();
            // Land RewindToLaunchLeadTimeSeconds before the event, matching the Forward button's lead.
            double targetUT = TimeJumpManager.ApplyJumpLead(eventUT, currentUT);

            if (!TimeJumpManager.IsValidJump(currentUT, targetUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "FastForwardToEventUT: invalid jump current={0:F1} target={1:F1} ({2}) - aborted",
                        currentUT, targetUT, label));
                return;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "FastForwardToEventUT: jumping to UT={0:F1} for {1} (delta={2:F1}s, eventUT={3:F1})",
                    targetUT, label, targetUT - currentUT, eventUT));

            TimeJumpManager.NotifyRecorder(recorder, currentUT, targetUT);
            TimeJumpManager.ExecuteForwardJump(targetUT);

            ParsekLog.ScreenMessage(
                string.Format(CultureInfo.InvariantCulture,
                    "Fast-forwarded to {0} ({1:F0}s)", label, targetUT - currentUT), 3f);
        }

        /// <summary>
        /// Warps to the earliest nearby spawn candidate.
        /// If the candidate will depart, warps to departure UT instead of EndUT.
        /// </summary>
        internal void WarpToNextCraftSpawn()
        {
            double currentUT = Planetarium.GetUniversalTime();
            var next = SelectiveSpawnUI.FindNextSpawnCandidate(nearbySpawnCandidates, currentUT, NearbySpawnRadius, MaxRelativeSpeed);
            if (next == null)
            {
                ParsekLog.Verbose("Flight", "WarpToNextCraftSpawn: no nearby candidates");
                return;
            }
            if (next.Value.willDepart)
                WarpToDeparture(next.Value.recordingIndex, next.Value.departureUT);
            else
                WarpToRecordingEnd(next.Value.recordingIndex);
        }
    }
}
