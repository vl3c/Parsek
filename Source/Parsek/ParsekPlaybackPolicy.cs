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
            // TODO: Move spawn-death detection from ParsekFlight (deferred)
        }

        /// <summary>
        /// Flush deferred spawns that were queued during warp.
        /// Runs AFTER engine.UpdatePlayback() when warp ends.
        /// </summary>
        internal void FlushDeferredSpawns(double currentUT, float warpRate)
        {
            // TODO: Move FlushDeferredSpawns from ParsekFlight (deferred)
        }

        private void HandlePlaybackCompleted(PlaybackCompletedEvent evt)
        {
            ParsekLog.Verbose("Policy",
                $"PlaybackCompleted index={evt.Index} vessel={evt.Trajectory?.VesselName} " +
                $"ghostWasActive={evt.GhostWasActive} pastEffectiveEnd={evt.PastEffectiveEnd} " +
                $"needsSpawn={evt.Flags.needsSpawn} isMidChain={evt.Flags.isMidChain}");

            // Mid-chain segments: hold ghost at final position, don't destroy
            if (evt.Flags.isMidChain && !evt.PastEffectiveEnd)
                return;

            // Spawn vessel if needed
            if (evt.Flags.needsSpawn && evt.PastEffectiveEnd)
            {
                bool isWarp = GhostPlaybackEngine.IsAnyWarpActive();
                if (isWarp)
                {
                    // Queue for later flush
                    pendingSpawnRecordingIds.Add(evt.Flags.recordingId);
                    ParsekLog.Info("Policy",
                        $"Deferred spawn during warp: #{evt.Index} \"{evt.Trajectory?.VesselName}\"");
                }
                else
                {
                    // Spawn immediately — delegated to host which has VesselSpawner access
                    var committed = RecordingStore.CommittedRecordings;
                    if (evt.Index >= 0 && evt.Index < committed.Count)
                    {
                        host.SpawnVesselOrChainTipFromPolicy(committed[evt.Index], evt.Index);
                    }
                }
            }

            // Destroy ghost (unless watched — host manages hold timer)
            if (evt.GhostWasActive)
            {
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
            ParsekLog.Verbose("Policy",
                $"LoopRestarted index={evt.Index} cycle={evt.PreviousCycleIndex}->{evt.NewCycleIndex} " +
                $"explosion={evt.ExplosionFired}");
        }

        private void HandleOverlapExpired(OverlapExpiredEvent evt)
        {
            ParsekLog.Verbose("Policy",
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
