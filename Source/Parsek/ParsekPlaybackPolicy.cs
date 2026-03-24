using System;

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
        /// Placeholder — will be implemented in Phase 6.
        /// </summary>
        internal void RunSpawnDeathChecks()
        {
            // Phase 6: spawn-death detection logic moves here from ParsekFlight
        }

        /// <summary>
        /// Flush deferred spawns that were queued during warp.
        /// Runs AFTER engine.UpdatePlayback() when warp ends.
        /// Placeholder — will be implemented in Phase 6.
        /// </summary>
        internal void FlushDeferredSpawns(double currentUT, float warpRate)
        {
            // Phase 6: deferred spawn logic moves here from ParsekFlight
        }

        private void HandlePlaybackCompleted(PlaybackCompletedEvent evt)
        {
            ParsekLog.Verbose("Policy",
                $"PlaybackCompleted index={evt.Index} vessel={evt.Trajectory?.VesselName} " +
                $"ghostWasActive={evt.GhostWasActive} pastEffectiveEnd={evt.PastEffectiveEnd}");
            // Phase 6: spawn-at-end logic, resource deltas
        }

        private void HandleGhostDestroyed(GhostLifecycleEvent evt)
        {
            ParsekLog.Verbose("Policy",
                $"GhostDestroyed index={evt.Index} vessel={evt.Trajectory?.VesselName}");
            // Phase 6: cleanup notifications
        }

        private void HandleLoopRestarted(LoopRestartedEvent evt)
        {
            ParsekLog.Verbose("Policy",
                $"LoopRestarted index={evt.Index} cycle={evt.PreviousCycleIndex}->{evt.NewCycleIndex} " +
                $"explosion={evt.ExplosionFired}");
            // Phase 6: camera management
        }

        private void HandleOverlapExpired(OverlapExpiredEvent evt)
        {
            ParsekLog.Verbose("Policy",
                $"OverlapExpired index={evt.Index} cycle={evt.CycleIndex} explosion={evt.ExplosionFired}");
            // Phase 6: camera management
        }

        private void HandleAllGhostsDestroying()
        {
            ParsekLog.Info("Policy", "AllGhostsDestroying — clearing policy state");
            // Phase 6: clear pendingSpawnRecordingIds, pendingWatchRecordingId, etc.
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
            ParsekLog.Info("Policy", "ParsekPlaybackPolicy disposed");
        }
    }
}
