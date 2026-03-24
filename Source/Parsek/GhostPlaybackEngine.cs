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

        /// <summary>Whether the ghost is on the specified celestial body.</summary>
        internal bool IsGhostOnBody(int index, string bodyName)
        {
            if (!ghostStates.TryGetValue(index, out var state) || state == null)
                return false;
            if (string.IsNullOrEmpty(bodyName))
                return false;
            return state.lastInterpolatedBodyName == bodyName;
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
        /// Spawns a timeline ghost for the given trajectory at the specified index.
        /// Builds the ghost mesh from the snapshot, or falls back to a sphere.
        /// Populates all ghost info dictionaries and reentry FX.
        /// </summary>
        internal void SpawnGhost(int index, IPlaybackTrajectory traj)
        {
            ParsekLog.Info("Engine", $"SpawnGhost index={index} vessel={traj?.VesselName}");

            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            GhostBuildResult buildResult = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;

            // Skip expensive snapshot build when no snapshot exists — go straight to sphere fallback.
            if (GhostVisualBuilder.GetGhostSnapshot(traj) != null)
            {
                buildResult = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    traj, $"Parsek_Timeline_{index}");
                if (buildResult != null)
                    ghost = buildResult.root;
                builtFromSnapshot = ghost != null;
            }

            if (ghost == null)
            {
                ghost = GhostVisualBuilder.CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);
                ParsekLog.Info("Engine", $"Timeline ghost #{index}: using sphere fallback");
            }
            else
            {
                bool usedStartSnapshot = traj.GhostVisualSnapshot != null;
                ParsekLog.Info("Engine", usedStartSnapshot
                    ? $"Timeline ghost #{index}: built from recording-start snapshot"
                    : $"Timeline ghost #{index}: built from vessel snapshot");
            }

            var cameraPivotObj = new GameObject("cameraPivot");
            cameraPivotObj.transform.SetParent(ghost.transform, false);

            var state = new GhostPlaybackState
            {
                ghost = ghost,
                cameraPivot = cameraPivotObj.transform,
                playbackIndex = 0,
                partEventIndex = 0,
                partTree = GhostVisualBuilder.BuildPartSubtreeMap(GhostVisualBuilder.GetGhostSnapshot(traj))
            };

            if (builtFromSnapshot)
            {
                state.materials = new List<Material>();
            }
            else
            {
                var m = ghost.GetComponent<Renderer>()?.material;
                state.materials = m != null ? new List<Material> { m } : new List<Material>();
            }

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, buildResult);
            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(traj, state);

            state.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                ghost, state.heatInfos, index, traj.VesselName);
            state.reentryMpb = new MaterialPropertyBlock();

            GhostPlaybackLogic.InitializeFlagVisibility(traj, state);

            ghostStates[index] = state;

            ParsekLog.Info("Engine",
                $"Ghost #{index} spawned: snapshot={builtFromSnapshot} parts={state.partTree?.Count ?? 0} " +
                $"engines={state.engineInfos?.Count ?? 0} rcs={state.rcsInfos?.Count ?? 0}");
        }

        /// <summary>
        /// Destroys materials, engine/RCS particle systems, reentry FX, ghost GameObject,
        /// and fake canopies for a single ghost playback state.
        /// Does NOT remove from any dictionary — caller handles collection bookkeeping.
        /// </summary>
        internal void DestroyGhostResources(GhostPlaybackState state)
        {
            if (state.materials != null)
            {
                for (int i = 0; i < state.materials.Count; i++)
                {
                    if (state.materials[i] != null)
                        UnityEngine.Object.Destroy(state.materials[i]);
                }
            }

            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            UnityEngine.Object.Destroy(info.particleSystems[i].gameObject);
            }

            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            UnityEngine.Object.Destroy(info.particleSystems[i].gameObject);
            }

            DestroyReentryFxResources(state.reentryFxInfo);

            if (state.ghost != null)
                UnityEngine.Object.Destroy(state.ghost);

            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
        }

        /// <summary>
        /// Destroys reentry FX resources (cloned materials, generated texture, emission mesh).
        /// </summary>
        internal void DestroyReentryFxResources(ReentryFxInfo info)
        {
            if (info == null) return;
            if (info.allClonedMaterials != null)
                for (int i = 0; i < info.allClonedMaterials.Count; i++)
                    if (info.allClonedMaterials[i] != null)
                        UnityEngine.Object.Destroy(info.allClonedMaterials[i]);
            if (info.generatedTexture != null)
                UnityEngine.Object.Destroy(info.generatedTexture);
            if (info.combinedEmissionMesh != null)
                UnityEngine.Object.Destroy(info.combinedEmissionMesh);
        }

        /// <summary>
        /// Despawns a single primary timeline ghost. Destroys its resources and
        /// removes it from ghostStates and loopPhaseOffsets.
        /// </summary>
        internal void DestroyGhost(int index)
        {
            ParsekLog.Info("Engine", $"DestroyGhost index={index}");

            GhostPlaybackState state;
            if (!ghostStates.TryGetValue(index, out state))
                return;

            DestroyGhostResources(state);

            ghostStates.Remove(index);
            loopPhaseOffsets.Remove(index);
        }

        /// <summary>
        /// Destroys a single overlap ghost's resources. Does NOT remove from any collection.
        /// </summary>
        internal void DestroyOverlapGhostState(GhostPlaybackState state)
        {
            if (state == null) return;
            ParsekLog.Verbose("Engine",
                $"Destroying overlap ghost cycle={state.loopCycleIndex}");
            DestroyGhostResources(state);
        }

        /// <summary>
        /// Destroys all overlap ghosts for a single recording index.
        /// Returns true if the given recIdx matched the watched recording's overlap tracking
        /// (caller should reset camera state).
        /// </summary>
        internal bool DestroyAllOverlapGhosts(int recIdx)
        {
            List<GhostPlaybackState> list;
            if (!overlapGhosts.TryGetValue(recIdx, out list)) return false;
            if (list.Count > 0)
                ParsekLog.Verbose("Engine",
                    $"Destroying all {list.Count} overlap ghost(s) for recording #{recIdx}");

            for (int i = 0; i < list.Count; i++)
                DestroyOverlapGhostState(list[i]);
            list.Clear();

            // Return true so the caller (ParsekFlight) can reset watch mode camera state
            // if this recording was being watched. Engine does not know about watch mode.
            return true;
        }

        /// <summary>
        /// Checks whether the recording ended with destruction and spawns an explosion FX if so.
        /// Takes warpRate as parameter (engine does not read KSP globals directly).
        /// </summary>
        internal void TriggerExplosionIfDestroyed(GhostPlaybackState state, IPlaybackTrajectory traj,
            int recIdx, float warpRate)
        {
            if (state == null)
            {
                ParsekLog.Verbose("Engine", $"TriggerExplosionIfDestroyed: ghost #{recIdx} — skipped (state is null)");
                return;
            }
            if (!GhostPlaybackLogic.ShouldTriggerExplosion(state.explosionFired, traj.TerminalStateValue,
                    state.ghost != null, traj.VesselName, recIdx))
                return;

            if (GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
            {
                state.explosionFired = true;
                GhostPlaybackLogic.HideAllGhostParts(state);
                ParsekLog.VerboseRateLimited("Engine", $"explosion-suppress-{recIdx}",
                    $"Explosion suppressed for ghost #{recIdx} \"{traj.VesselName}\": " +
                    $"warp rate {warpRate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}x > " +
                    $"{GhostPlaybackLogic.FxSuppressWarpThreshold}x");
                return;
            }

            state.explosionFired = true;

            Vector3 worldPos = state.ghost.transform.position;
            float vesselLength = state.reentryFxInfo != null
                ? state.reentryFxInfo.vesselLength
                : GhostVisualBuilder.ComputeGhostLength(state.ghost);

            ParsekLog.Info("Engine",
                $"Triggering explosion for ghost #{recIdx} \"{traj.VesselName}\" " +
                $"at ({worldPos.x:F1},{worldPos.y:F1},{worldPos.z:F1}) vesselLength={vesselLength:F1}m");

            var explosion = GhostVisualBuilder.SpawnExplosionFx(worldPos, vesselLength);
            if (explosion != null)
            {
                UnityEngine.Object.Destroy(explosion, 6f);
                activeExplosions.Add(explosion);

                if (activeExplosions.Count > 20)
                {
                    for (int e = activeExplosions.Count - 1; e >= 0; e--)
                    {
                        if (activeExplosions[e] == null)
                            activeExplosions.RemoveAt(e);
                    }
                }

                ParsekLog.Verbose("Engine",
                    $"Explosion GO created for ghost #{recIdx}, activeExplosions.Count={activeExplosions.Count}");
            }

            GhostPlaybackLogic.HideAllGhostParts(state);
            ParsekLog.Verbose("Engine", $"Ghost #{recIdx} parts hidden after explosion");
        }

        /// <summary>
        /// Destroys and clears all active explosion GameObjects.
        /// </summary>
        internal void CleanupActiveExplosions()
        {
            if (activeExplosions.Count == 0) return;
            int destroyed = 0;
            for (int i = activeExplosions.Count - 1; i >= 0; i--)
            {
                if (activeExplosions[i] != null)
                {
                    UnityEngine.Object.Destroy(activeExplosions[i]);
                    destroyed++;
                }
            }
            ParsekLog.Verbose("Engine", $"CleanupActiveExplosions: destroyed {destroyed}/{activeExplosions.Count} explosion GOs");
            activeExplosions.Clear();
        }

        /// <summary>
        /// Clean up all engine-owned ghost state. Destroys all ghost GOs first,
        /// then clears all collections. Fires OnAllGhostsDestroying so policy
        /// and host can clear their own state.
        /// </summary>
        internal void DestroyAllGhosts()
        {
            ParsekLog.Info("Engine", $"DestroyAllGhosts: clearing {ghostStates.Count} primary + {overlapGhosts.Count} overlap entries");

            // Fire event BEFORE clearing so subscribers can inspect ghost state if needed
            OnAllGhostsDestroying?.Invoke();

            // Destroy all primary ghost GOs
            var keys = new List<int>(ghostStates.Keys);
            foreach (int key in keys)
            {
                if (ghostStates.TryGetValue(key, out var state))
                    DestroyGhostResources(state);
            }

            // Destroy all overlap ghost GOs
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                    DestroyOverlapGhostState(kvp.Value[i]);
            }

            // Clear all engine state
            ghostStates.Clear();
            overlapGhosts.Clear();
            loopPhaseOffsets.Clear();
            loadedAnchorVessels.Clear();
            softCapSuppressed.Clear();
            loggedGhostEnter.Clear();
            loggedReshow.Clear();
            cachedZone1Ghosts.Clear();
            cachedZone2Ghosts.Clear();
            softCapTriggeredThisFrame = false;

            CleanupActiveExplosions();
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
