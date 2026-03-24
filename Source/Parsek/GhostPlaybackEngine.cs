using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Core ghost playback engine. Manages ghost GameObjects, per-frame positioning,
    /// part event application, loop/overlap playback, zone transitions, and soft caps.
    ///
    /// This class has no knowledge of Recording, RecordingTree, BranchPoint, chain IDs,
    /// resource deltas, vessel spawning, or any Parsek-specific concept. It renders
    /// trajectories as visual ghosts and nothing more.
    ///
    /// Future: this class becomes the core of the standalone ghost playback mod.
    /// </summary>
    internal class GhostPlaybackEngine
    {
        private readonly IGhostPositioner positioner;
        private readonly Action<IEnumerator> startCoroutine;

        // === Ghost state (owned by engine) ===
        // These will be moved from ParsekFlight in Phase 4.
        // For now, all state remains in ParsekFlight.

        // === Lifecycle events ===
        internal event Action<GhostLifecycleEvent> OnGhostCreated;
        internal event Action<GhostLifecycleEvent> OnGhostDestroyed;
        internal event Action<PlaybackCompletedEvent> OnPlaybackCompleted;
        internal event Action<LoopRestartedEvent> OnLoopRestarted;
        internal event Action<OverlapExpiredEvent> OnOverlapExpired;
        internal event Action OnAllGhostsDestroying;

        // === Camera events (engine detects, host handles FlightCamera) ===
        internal event Action<CameraActionEvent> OnLoopCameraAction;
        internal event Action<CameraActionEvent> OnOverlapCameraAction;

        internal GhostPlaybackEngine(IGhostPositioner positioner, Action<IEnumerator> startCoroutine)
        {
            this.positioner = positioner;
            this.startCoroutine = startCoroutine;
            ParsekLog.Info("Engine", "GhostPlaybackEngine created");
        }

        /// <summary>
        /// Main per-frame update. Iterates all active trajectories, spawns/positions/destroys
        /// ghosts, fires lifecycle events. Called from host's Update().
        /// Placeholder — will be implemented in Phase 5 when methods move from ParsekFlight.
        /// </summary>
        internal void UpdatePlayback(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            TrajectoryPlaybackFlags[] flags,
            FrameContext ctx)
        {
            // Phase 5: main loop will be moved here from ParsekFlight.UpdateTimelinePlayback
        }

        /// <summary>
        /// Clean up all engine-owned ghost state. Fires OnAllGhostsDestroying so
        /// policy and host can clear their own state.
        /// Placeholder — will be implemented in Phase 4.
        /// </summary>
        internal void DestroyAllGhosts()
        {
            ParsekLog.Info("Engine", "DestroyAllGhosts called");
            OnAllGhostsDestroying?.Invoke();
        }

        /// <summary>
        /// Release all resources. Called from host's OnDestroy().
        /// </summary>
        internal void Dispose()
        {
            ParsekLog.Info("Engine", "GhostPlaybackEngine disposed");
        }
    }
}
