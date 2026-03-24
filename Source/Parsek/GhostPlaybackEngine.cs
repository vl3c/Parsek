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

        #region Ghost state

        // Primary ghost state: one GhostPlaybackState per active timeline ghost, keyed by recording index.
        internal readonly Dictionary<int, GhostPlaybackState> ghostStates = new Dictionary<int, GhostPlaybackState>();

        // Overlap ghosts: older cycle ghosts still alive due to negative loop interval.
        internal readonly Dictionary<int, List<GhostPlaybackState>> overlapGhosts = new Dictionary<int, List<GhostPlaybackState>>();

        // Loop phase offsets: shifted loop phase for Watch mode targeting.
        internal readonly Dictionary<int, double> loopPhaseOffsets = new Dictionary<int, double>();

        // Active explosion GameObjects (tracked for cleanup).
        internal readonly List<GameObject> activeExplosions = new List<GameObject>();

        // Anchor vessel tracking: which anchor vessels are loaded (for looped ghost lifecycle).
        internal readonly HashSet<uint> loadedAnchorVessels = new HashSet<uint>();

        // Soft cap evaluation — cached lists to avoid per-frame allocation.
        internal readonly List<(int recordingIndex, GhostPriority priority)> cachedZone1Ghosts =
            new List<(int, GhostPriority)>();
        internal readonly List<(int recordingIndex, GhostPriority priority)> cachedZone2Ghosts =
            new List<(int, GhostPriority)>();
        internal bool softCapTriggeredThisFrame;
        internal readonly HashSet<int> softCapSuppressed = new HashSet<int>();

        // Diagnostic logging guards (log once per state transition, not per frame).
        internal readonly HashSet<int> loggedGhostEnter = new HashSet<int>();
        internal readonly HashSet<int> loggedReshow = new HashSet<int>();

        // Constants
        internal const int MaxOverlapGhostsPerRecording = 5;
        internal const double OverlapExplosionHoldSeconds = 3.0;

        #endregion

        #region Lifecycle events

        internal event Action<GhostLifecycleEvent> OnGhostCreated;
        internal event Action<GhostLifecycleEvent> OnGhostDestroyed;
        internal event Action<PlaybackCompletedEvent> OnPlaybackCompleted;
        internal event Action<LoopRestartedEvent> OnLoopRestarted;
        internal event Action<OverlapExpiredEvent> OnOverlapExpired;
        internal event Action OnAllGhostsDestroying;

        // Camera events (engine detects cycle changes, host handles FlightCamera).
        internal event Action<CameraActionEvent> OnLoopCameraAction;
        internal event Action<CameraActionEvent> OnOverlapCameraAction;

        #endregion

        internal GhostPlaybackEngine(IGhostPositioner positioner, Action<IEnumerator> startCoroutine)
        {
            this.positioner = positioner;
            this.startCoroutine = startCoroutine;
            ParsekLog.Info("Engine", "GhostPlaybackEngine created");
        }

        #region Per-frame update

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

        #endregion

        #region Query API

        /// <summary>Number of active primary timeline ghosts.</summary>
        internal int GhostCount => ghostStates.Count;

        /// <summary>Whether a ghost exists for the given recording index.</summary>
        internal bool HasGhost(int index) => ghostStates.ContainsKey(index);

        /// <summary>Whether a ghost exists with a non-null GameObject.</summary>
        internal bool HasActiveGhost(int index)
        {
            return ghostStates.TryGetValue(index, out var state) && state?.ghost != null;
        }

        /// <summary>Get the ghost state for a recording index.</summary>
        internal bool TryGetGhostState(int index, out GhostPlaybackState state)
        {
            return ghostStates.TryGetValue(index, out state);
        }

        /// <summary>Get the camera pivot transform for a ghost (for watch mode targeting).</summary>
        internal bool TryGetGhostPivot(int index, out Transform pivot)
        {
            pivot = null;
            if (ghostStates.TryGetValue(index, out var state) && state?.cameraPivot != null)
            {
                pivot = state.cameraPivot;
                return true;
            }
            return false;
        }

        /// <summary>Whether the ghost is within visual rendering range (not in Beyond zone).</summary>
        internal bool IsGhostWithinVisualRange(int index)
        {
            return ghostStates.TryGetValue(index, out var state)
                && state != null
                && state.currentZone != RenderingZone.Beyond;
        }

        /// <summary>Whether the ghost is on the same celestial body as the active vessel.</summary>
        internal bool IsGhostOnSameBody(int index)
        {
            if (!ghostStates.TryGetValue(index, out var state) || state == null)
                return false;
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel?.mainBody == null)
                return false;
            return state.lastInterpolatedBodyName == activeVessel.mainBody.name;
        }

        /// <summary>Get the body name the ghost was last positioned on.</summary>
        internal string GetGhostBodyName(int index)
        {
            return ghostStates.TryGetValue(index, out var state) ? state?.lastInterpolatedBodyName : null;
        }

        /// <summary>Build a dictionary of recording index to ghost GameObject (for UI).</summary>
        internal Dictionary<int, GameObject> GetGhostGameObjects()
        {
            var result = new Dictionary<int, GameObject>(ghostStates.Count);
            foreach (var kv in ghostStates)
            {
                if (kv.Value?.ghost != null)
                    result[kv.Key] = kv.Value.ghost;
            }
            return result;
        }

        /// <summary>Get overlap ghosts for a recording index.</summary>
        internal bool TryGetOverlapGhosts(int index, out List<GhostPlaybackState> overlaps)
        {
            return overlapGhosts.TryGetValue(index, out overlaps);
        }

        /// <summary>Get active ghost positions for proximity checking (Real Spawn Control).</summary>
        internal IEnumerable<(int index, Vector3 position)> GetActiveGhostPositions()
        {
            foreach (var kv in ghostStates)
            {
                if (kv.Value?.ghost != null && kv.Value.ghost.activeSelf)
                    yield return (kv.Key, kv.Value.ghost.transform.position);
            }
        }

        #endregion

        #region Anchor vessel lifecycle

        /// <summary>Notify the engine that an anchor vessel was loaded.</summary>
        internal void OnAnchorVesselLoaded(uint vesselPid)
        {
            loadedAnchorVessels.Add(vesselPid);
        }

        /// <summary>Notify the engine that an anchor vessel was unloaded.</summary>
        internal void OnAnchorVesselUnloaded(uint vesselPid)
        {
            loadedAnchorVessels.Remove(vesselPid);
        }

        #endregion

        #region Ghost lifecycle

        /// <summary>
        /// Clean up all engine-owned ghost state. Fires OnAllGhostsDestroying so
        /// policy and host can clear their own state.
        /// </summary>
        internal void DestroyAllGhosts()
        {
            ParsekLog.Info("Engine", $"DestroyAllGhosts: clearing {ghostStates.Count} primary + {overlapGhosts.Count} overlap entries");

            // Phase 4 next steps: iterate and destroy ghost GOs before clearing dicts.
            // For now, just clear state (ghost GOs are still managed by ParsekFlight
            // until lifecycle methods move in the next sub-steps).

            ghostStates.Clear();
            overlapGhosts.Clear();
            loopPhaseOffsets.Clear();
            activeExplosions.Clear();
            loadedAnchorVessels.Clear();
            softCapSuppressed.Clear();
            loggedGhostEnter.Clear();
            loggedReshow.Clear();
            cachedZone1Ghosts.Clear();
            cachedZone2Ghosts.Clear();
            softCapTriggeredThisFrame = false;

            OnAllGhostsDestroying?.Invoke();
        }

        /// <summary>
        /// Reindex all engine dictionaries after a recording is deleted.
        /// Keys above the removed index shift down by 1.
        /// </summary>
        internal void ReindexAfterDelete(int removedIndex)
        {
            ReindexDict(ghostStates, removedIndex);
            ReindexDict(overlapGhosts, removedIndex);
            ReindexDict(loopPhaseOffsets, removedIndex);
            ReindexSet(loggedGhostEnter, removedIndex);
            ReindexSet(loggedReshow, removedIndex);
            ReindexSet(softCapSuppressed, removedIndex);
        }

        private static void ReindexDict<T>(Dictionary<int, T> dict, int removedIndex)
        {
            var keys = new List<int>(dict.Keys);
            keys.Sort();
            foreach (int key in keys)
            {
                if (key > removedIndex)
                {
                    var value = dict[key];
                    dict.Remove(key);
                    dict[key - 1] = value;
                }
            }
        }

        private static void ReindexSet(HashSet<int> set, int removedIndex)
        {
            var items = new List<int>(set);
            set.Clear();
            foreach (int item in items)
            {
                if (item > removedIndex)
                    set.Add(item - 1);
                else if (item < removedIndex)
                    set.Add(item);
                // item == removedIndex is dropped
            }
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Release all resources. Called from host's OnDestroy().
        /// </summary>
        internal void Dispose()
        {
            DestroyAllGhosts();
            ParsekLog.Info("Engine", "GhostPlaybackEngine disposed");
        }

        #endregion
    }
}
