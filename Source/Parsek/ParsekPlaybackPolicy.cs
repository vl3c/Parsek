using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Subscribes to GhostPlaybackEngine lifecycle events and implements
    /// Parsek-specific policy: vessel spawning, resource replay, camera
    /// management, deferred spawn queue, spawn-death-loop detection.
    ///
    /// This class knows about Recording, RecordingStore, VesselSpawner —
    /// it is the bridge between the engine's visual mechanics and Parsek's
    /// recording tree / timeline / spawn system.
    /// </summary>
    internal class ParsekPlaybackPolicy
    {
        private readonly GhostPlaybackEngine engine;
        private readonly ParsekFlight host;

        // Deferred spawn queue: recording IDs queued during warp, flushed when warp ends
        internal readonly HashSet<string> pendingSpawnRecordingIds = new HashSet<string>();
        internal string pendingWatchRecordingId;

        internal ParsekPlaybackPolicy(GhostPlaybackEngine engine, ParsekFlight host)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.host = host ?? throw new ArgumentNullException(nameof(host));

            // Subscribe to engine events
            engine.OnPlaybackCompleted += HandlePlaybackCompleted;
            engine.OnGhostDestroyed += HandleGhostDestroyed;
            engine.OnLoopRestarted += HandleLoopRestarted;
            engine.OnOverlapExpired += HandleOverlapExpired;
            engine.OnAllGhostsDestroying += HandleAllGhostsDestroying;

            ParsekLog.Info("Policy", "ParsekPlaybackPolicy created and subscribed to engine events");
        }

        /// <summary>
        /// Pre-check: detect spawned vessels that died since last frame.
        /// Runs BEFORE engine.UpdatePlayback() so flags reflect current state.
        /// </summary>
        internal void RunSpawnDeathChecks()
        {
            // TODO(#132): Move spawn-death detection from ParsekFlight
        }

        /// <summary>
        /// Flush deferred spawns that were queued during warp.
        /// Runs AFTER engine.UpdatePlayback() when warp ends.
        /// </summary>
        internal void FlushDeferredSpawns(double currentUT, float warpRate)
        {
            // TODO(#132): Move FlushDeferredSpawns from ParsekFlight
        }

        private void HandlePlaybackCompleted(PlaybackCompletedEvent evt)
        {
            bool isWatched = host.watchedRecordingIndex == evt.Index;

            ParsekLog.Verbose("Policy",
                $"PlaybackCompleted index={evt.Index} vessel={evt.Trajectory?.VesselName} " +
                $"ghostWasActive={evt.GhostWasActive} pastEffectiveEnd={evt.PastEffectiveEnd} " +
                $"needsSpawn={evt.Flags.needsSpawn} isMidChain={evt.Flags.isMidChain} watched={isWatched}");

            // Mid-chain segments: hold ghost at final position
            if (evt.Flags.isMidChain && !evt.PastEffectiveEnd)
            {
                // Auto-follow watch mode to next chain segment
                if (isWatched)
                {
                    var committed = RecordingStore.CommittedRecordings;
                    if (evt.Index >= 0 && evt.Index < committed.Count)
                    {
                        int nextTarget = host.FindNextWatchTargetFromPolicy(evt.Index, committed[evt.Index]);
                        if (nextTarget >= 0)
                        {
                            host.TransferWatchToNextSegmentFromPolicy(nextTarget);
                            ParsekLog.Info("Policy",
                                $"Mid-chain auto-follow: #{evt.Index} → #{nextTarget}");
                        }
                    }
                }
                return;
            }

            // Spawn vessel if needed
            bool spawned = false;
            if (evt.Flags.needsSpawn && evt.PastEffectiveEnd)
            {
                bool isWarp = GhostPlaybackEngine.IsAnyWarpActiveFromGlobals();
                if (isWarp)
                {
                    pendingSpawnRecordingIds.Add(evt.Flags.recordingId);
                    if (isWatched)
                    {
                        pendingWatchRecordingId = evt.Flags.recordingId;
                        host.ExitWatchModeFromPolicy();
                    }
                    ParsekLog.Info("Policy",
                        $"Deferred spawn during warp: #{evt.Index} \"{evt.Trajectory?.VesselName}\"");
                }
                else
                {
                    var committed = RecordingStore.CommittedRecordings;
                    if (evt.Index >= 0 && evt.Index < committed.Count)
                    {
                        // If watching, exit watch mode and switch to spawned vessel
                        if (isWatched)
                            host.ExitWatchModeFromPolicy();

                        host.SpawnVesselOrChainTipFromPolicy(committed[evt.Index], evt.Index);
                        spawned = true;

                        // Switch camera to spawned vessel if we were watching
                        if (isWatched)
                        {
                            uint spawnedPid = committed[evt.Index].SpawnedVesselPersistentId;
                            if (spawnedPid != 0)
                                host.DeferredActivateVesselFromPolicy(spawnedPid);
                        }
                    }
                }
            }

            // Destroy ghost
            if (evt.GhostWasActive)
            {
                // If watching and not spawning: hold ghost for camera (3-5 seconds)
                if (isWatched && !spawned && !evt.Flags.needsSpawn)
                {
                    // Set hold timer — the existing UpdateWatchCamera / watchEndHoldUntilUT
                    // mechanism in ParsekFlight handles the per-frame countdown
                    double holdSeconds = evt.Trajectory?.TerminalStateValue == TerminalState.Destroyed
                        ? 5.0 : 3.0;
                    host.StartWatchHoldFromPolicy(evt.CurrentUT + holdSeconds);

                    // Trigger explosion if terminal was Destroyed
                    engine.TriggerExplosionIfDestroyed(evt.State, evt.Trajectory, evt.Index,
                        TimeWarp.CurrentRate);

                    ParsekLog.Info("Policy",
                        $"Watch hold started for #{evt.Index}: {holdSeconds:F0}s " +
                        $"terminal={evt.Trajectory?.TerminalStateValue}");
                    // Ghost stays alive — ParsekFlight's watch hold timer will destroy it
                    return;
                }

                engine.DestroyGhost(evt.Index, evt.Trajectory, evt.Flags);
            }
        }

        private void HandleGhostDestroyed(GhostLifecycleEvent evt)
        {
            ParsekLog.Verbose("Policy",
                $"GhostDestroyed index={evt.Index} vessel={evt.Trajectory?.VesselName}");
        }

        private void HandleLoopRestarted(LoopRestartedEvent evt)
        {
            ParsekLog.VerboseRateLimited("Policy", "loop-restarted",
                $"LoopRestarted index={evt.Index} cycle={evt.PreviousCycleIndex}->{evt.NewCycleIndex} " +
                $"explosion={evt.ExplosionFired}");
        }

        private void HandleOverlapExpired(OverlapExpiredEvent evt)
        {
            ParsekLog.VerboseRateLimited("Policy", "overlap-expired",
                $"OverlapExpired index={evt.Index} cycle={evt.CycleIndex} explosion={evt.ExplosionFired}");
        }

        private void HandleAllGhostsDestroying()
        {
            ParsekLog.Info("Policy", "AllGhostsDestroying — clearing policy state");
            pendingSpawnRecordingIds.Clear();
            pendingWatchRecordingId = null;
        }

        /// <summary>
        /// Unsubscribe from engine events. Called from host's OnDestroy().
        /// </summary>
        internal void Dispose()
        {
            engine.OnPlaybackCompleted -= HandlePlaybackCompleted;
            engine.OnGhostDestroyed -= HandleGhostDestroyed;
            engine.OnLoopRestarted -= HandleLoopRestarted;
            engine.OnOverlapExpired -= HandleOverlapExpired;
            engine.OnAllGhostsDestroying -= HandleAllGhostsDestroying;
            ParsekLog.Info("Policy", "ParsekPlaybackPolicy disposed and unsubscribed from 5 engine events");
        }
    }
}
