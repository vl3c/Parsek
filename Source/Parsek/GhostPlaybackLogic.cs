using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure-static helper methods for ghost visual manipulation and playback logic.
    /// Extracted from ParsekFlight to reduce file size; all methods are stateless.
    /// </summary>
    internal static class GhostPlaybackLogic
    {
        // KSP rails warp levels: 1, 5, 10, 50, 100, 1000, 10000, 100000.
        // FX threshold: 10x is the last level where explosions/puffs/reentry/RCS look reasonable.
        // Ghost threshold: 50x is the last level where ghost meshes update often enough to be useful.
        internal const float FxSuppressWarpThreshold = 10f;
        internal const float GhostHideWarpThreshold = 50f;
        internal const double DefaultLoopIntervalSeconds = 10.0;
        internal const double MinLoopDurationSeconds = 1.0;
        internal const double MinCycleDuration = 1.0;
        // Grace period before zone-based watch mode exit (wall-clock seconds).
        // Prevents immediate exit when a ghost briefly crosses a zone boundary at watch-mode start.
        internal const float WatchModeZoneGraceSeconds = 2.0f;

        #region Warp / Loop Policy

        internal static bool ShouldSuppressVisualFx(float currentWarpRate)
        {
            return currentWarpRate > FxSuppressWarpThreshold;
        }

        internal static bool ShouldSuppressGhosts(float currentWarpRate)
        {
            return currentWarpRate > GhostHideWarpThreshold;
        }

        /// <summary>
        /// Returns true if a ghost should be exempt from zone-based hiding during time warp.
        /// Orbital ghosts travel far from the player during warp; hiding them at 120km causes
        /// them to disappear and complete playback while invisible (#171).
        /// </summary>
        internal static bool ShouldExemptFromZoneHide(float currentWarpRate, bool hasOrbitalSegments)
        {
            // Threshold lowered from > 4f to > 1f: KSP ramps through intermediate rates
            // when entering/exiting warp, causing frame-by-frame oscillation around higher
            // thresholds and ghost icon flicker in map view.
            return currentWarpRate > 1f && hasOrbitalSegments;
        }

        /// <summary>
        /// Warp-zone hide exemption only applies to true Beyond-zone hiding. It must not
        /// cancel the 50-120 km hidden-mesh tier introduced by distance LOD.
        /// </summary>
        internal static bool ShouldApplyWarpZoneHideExemption(
            bool shouldHideMesh, RenderingZone zone, float currentWarpRate, bool hasOrbitalSegments)
        {
            return shouldHideMesh
                && zone == RenderingZone.Beyond
                && ShouldExemptFromZoneHide(currentWarpRate, hasOrbitalSegments);
        }

        /// <summary>
        /// Returns true if a commit approval dialog should be shown instead of auto-committing (#88).
        /// Triggers when leaving Flight to KSC or Tracking Station with a landed/splashed vessel.
        /// </summary>
        internal static bool ShouldShowCommitApproval(GameScenes destination, TerminalState? terminalState)
        {
            if (destination != GameScenes.SPACECENTER && destination != GameScenes.TRACKSTATION)
                return false;
            if (!terminalState.HasValue)
                return false;
            var ts = terminalState.Value;
            return ts == TerminalState.Landed || ts == TerminalState.Splashed;
        }

        internal static bool ShouldFlushDeferredSpawns(int pendingCount, bool isWarpActive)
        {
            return pendingCount > 0 && !isWarpActive;
        }

        internal static bool ShouldSkipDeferredSpawn(bool vesselSpawned, bool hasSnapshot)
        {
            return vesselSpawned || !hasSnapshot;
        }

        internal static bool ShouldRestoreWatchMode(string pendingWatchId, string recordingId, uint spawnedPid)
        {
            return pendingWatchId != null && pendingWatchId == recordingId && spawnedPid != 0;
        }

        /// <summary>
        /// Pure decision: should this recording be checked for spawn-death (vessel spawned
        /// then immediately destroyed)? Returns true when the recording has a live spawned
        /// vessel that hasn't already been abandoned.
        /// </summary>
        internal static bool ShouldCheckForSpawnDeath(bool vesselSpawned, uint spawnedPid, bool spawnAbandoned)
        {
            return vesselSpawned && spawnedPid != 0 && !spawnAbandoned;
        }

        internal static bool ShouldPauseTimelineResourceReplay(bool isRecording)
        {
            return isRecording;
        }

        internal static bool ShouldLoopPlayback(bool recordingLoopPlayback)
        {
            return recordingLoopPlayback;
        }

        internal static bool IsAnyWarpActive(int currentRateIndex, float currentRate)
        {
            return currentRateIndex > 0 || currentRate > 1f;
        }

        internal static int ComputeTargetResourceIndex(
            List<TrajectoryPoint> points, int lastAppliedResourceIndex, double currentUT)
        {
            int targetIndex = lastAppliedResourceIndex;
            for (int j = lastAppliedResourceIndex + 1; j < points.Count; j++)
            {
                if (points[j].ut <= currentUT)
                    targetIndex = j;
                else
                    break;
            }
            return targetIndex;
        }

        internal static bool TryComputeLoopPlaybackUT(
            double currentUT, double startUT, double endUT, double intervalSeconds,
            out double loopUT, out long cycleIndex)
        {
            loopUT = startUT;
            cycleIndex = 0;

            double duration = endUT - startUT;
            if (duration <= 0 || currentUT < startUT)
                return false;

            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration <= MinCycleDuration)
                cycleDuration = MinCycleDuration;

            double elapsed = currentUT - startUT;
            cycleIndex = (long)Math.Floor(elapsed / cycleDuration);
            double phase = elapsed - (cycleIndex * cycleDuration);

            // For positive intervals: phase > duration means we're in the pause window
            if (intervalSeconds >= 0)
            {
                const double epsilon = 1e-6;
                if (phase > duration + epsilon)
                    return false;
            }

            if (phase < 0) phase = 0;
            if (phase > duration) phase = duration;

            loopUT = startUT + phase;
            return true;
        }

        /// <summary>
        /// Determines the new watchedOverlapCycleIndex when a watched loop cycle
        /// ends and the ghost is about to be rebuilt. Returns -2 (explosion hold),
        /// -1 (ready for immediate re-target), or unchanged if not watching.
        /// </summary>
        internal static long ComputeWatchCycleOnLoopRebuild(
            long currentWatchCycle, bool isWatching, bool needsExplosion, bool inPauseWindow)
        {
            if (!isWatching) return currentWatchCycle;
            // Already in a hold — don't start another one, let the current hold
            // expire naturally. Otherwise the timer keeps resetting during time warp
            // and the camera never re-targets.
            if (currentWatchCycle == -2) return currentWatchCycle;
            if (needsExplosion && !inPauseWindow) return -2; // hold at explosion site
            return -1; // ready for immediate re-target
        }

        /// <summary>
        /// Computes the range of active loop cycles at a given time.
        /// For positive/zero intervals, firstActiveCycle == lastActiveCycle (no overlap).
        /// For negative intervals, multiple cycles may be active simultaneously.
        /// </summary>
        internal static void GetActiveCycles(
            double currentUT, double startUT, double endUT,
            double intervalSeconds, int maxCycles,
            out long firstActiveCycle, out long lastActiveCycle)
        {
            firstActiveCycle = 0;
            lastActiveCycle = 0;

            double duration = endUT - startUT;
            if (duration <= 0 || currentUT < startUT)
                return;

            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration < MinCycleDuration)
                cycleDuration = MinCycleDuration;

            double elapsed = currentUT - startUT;
            lastActiveCycle = (long)Math.Floor(elapsed / cycleDuration);
            if (lastActiveCycle < 0) lastActiveCycle = 0;

            // First cycle whose playback hasn't finished yet
            double elapsedMinusDuration = elapsed - duration;
            if (elapsedMinusDuration < 0)
            {
                firstActiveCycle = 0;
            }
            else
            {
                firstActiveCycle = (long)Math.Floor(elapsedMinusDuration / cycleDuration) + 1;
                if (firstActiveCycle < 0) firstActiveCycle = 0;
            }

            // Cap by maxCycles
            if (firstActiveCycle < lastActiveCycle - maxCycles + 1)
                firstActiveCycle = lastActiveCycle - maxCycles + 1;
            if (firstActiveCycle < 0) firstActiveCycle = 0;
        }

        internal static double ResolveLoopInterval(
            IPlaybackTrajectory rec, double globalAutoInterval,
            double defaultInterval, double minCycleDuration)
        {
            if (rec == null) return defaultInterval;

            double interval;
            if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
            {
                interval = double.IsNaN(globalAutoInterval) || double.IsInfinity(globalAutoInterval)
                    ? defaultInterval : Math.Max(0, globalAutoInterval);
            }
            else
            {
                interval = double.IsNaN(rec.LoopIntervalSeconds) || double.IsInfinity(rec.LoopIntervalSeconds)
                    ? defaultInterval : rec.LoopIntervalSeconds;
            }

            double duration = rec.EndUT - rec.StartUT;
            return Math.Max(-duration + minCycleDuration, interval);
        }

        /// <summary>
        /// Validates that a loop anchor vessel exists. Returns true if the anchor PID
        /// corresponds to a real vessel in the game world. Returns false if the anchor
        /// vessel is missing (loop should be broken / fall back to absolute positioning).
        /// Pure-static decision method using the existing vesselExistsOverride mechanism
        /// for testability.
        /// </summary>
        internal static bool ValidateLoopAnchor(uint anchorPid)
        {
            if (anchorPid == 0)
            {
                ParsekLog.Verbose("Loop", "ValidateLoopAnchor: anchorPid=0, no anchor configured");
                return false;
            }

            bool exists = RealVesselExists(anchorPid);
            if (exists)
            {
                ParsekLog.Verbose("Loop", $"ValidateLoopAnchor: anchor pid={anchorPid} found");
            }
            else
            {
                ParsekLog.Warn("Loop", $"ValidateLoopAnchor: anchor pid={anchorPid} NOT found — loop anchor broken");
            }
            return exists;
        }

        /// <summary>
        /// Determines whether a looping recording should use anchor-relative positioning.
        /// Returns true if the recording has a LoopAnchorVesselId set AND has RELATIVE
        /// TrackSections that contain the offset data needed for relative playback.
        /// Pure static for testability.
        /// </summary>
        internal static bool ShouldUseLoopAnchor(IPlaybackTrajectory rec)
        {
            if (rec == null || rec.LoopAnchorVesselId == 0)
                return false;

            // Only use anchor-relative mode if the recording has RELATIVE TrackSections.
            // Legacy recordings with absolute positions don't have offset data, so the
            // anchor has no effect on them.
            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
                return false;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                if (rec.TrackSections[i].referenceFrame == ReferenceFrame.Relative)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Computes where a looped recording should be in its cycle at a given UT.
        /// Returns the "loop UT" -- the position within the recording's timeline that
        /// the ghost should be at right now.
        ///
        /// The loop cycles continuously: recording plays from StartUT to EndUT,
        /// then pauses for intervalSeconds, then repeats. Given any currentUT,
        /// this method returns which point in the recording the ghost should show.
        ///
        /// Returns (loopUT, cycleIndex, isInPause):
        ///   loopUT: the UT within [StartUT, EndUT] to position the ghost at
        ///   cycleIndex: which cycle we're in (0-based)
        ///   isInPause: true if currentUT falls in the pause interval between cycles
        /// </summary>
        internal static (double loopUT, long cycleIndex, bool isInPause) ComputeLoopPhaseFromUT(
            double currentUT,
            double recordingStartUT,
            double recordingEndUT,
            double intervalSeconds)
        {
            // Early guard: currentUT before recording start — consistent with TryComputeLoopPlaybackUT
            if (currentUT < recordingStartUT)
            {
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: currentUT={currentUT:R} before recordingStartUT={recordingStartUT:R}, returning startUT");
                return (recordingStartUT, 0, false);
            }

            double duration = recordingEndUT - recordingStartUT;
            if (duration <= 0)
            {
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: zero/negative duration={duration:R}, returning startUT");
                return (recordingStartUT, 0, false);
            }

            double cycleDuration = duration + Math.Max(0, intervalSeconds);
            if (cycleDuration <= 0)
            {
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: zero/negative cycleDuration={cycleDuration:R}, returning startUT");
                return (recordingStartUT, 0, false);
            }

            double elapsed = currentUT - recordingStartUT;

            long cycleIndex = (long)(elapsed / cycleDuration);
            double phaseInCycle = elapsed - (cycleIndex * cycleDuration);

            if (phaseInCycle < duration)
            {
                // In the playback portion
                double loopUT = recordingStartUT + phaseInCycle;
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: cycleIndex={cycleIndex}, loopUT={loopUT:R}, isInPause=false (phase={phaseInCycle:R}/{duration:R})");
                return (loopUT, cycleIndex, false);
            }
            else
            {
                // In the pause interval between cycles
                // Ghost should be at the end position (or hidden)
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: cycleIndex={cycleIndex}, loopUT={recordingEndUT:R}, isInPause=true (phase={phaseInCycle:R}/{duration:R})");
                return (recordingEndUT, cycleIndex, true);
            }
        }

        /// <summary>
        /// Determines whether a looped ghost should be spawned for a recording
        /// whose anchor vessel just loaded. Returns false if:
        /// - Recording is not looping
        /// - No anchor vessel ID set
        /// - Anchor vessel doesn't exist
        /// - Anchor vessel body mismatch (wrong celestial body)
        /// </summary>
        internal static bool ShouldSpawnLoopedGhost(
            IPlaybackTrajectory rec,
            bool anchorVesselExists,
            string anchorBodyName,
            string recordingBodyName)
        {
            if (rec == null)
            {
                ParsekLog.Verbose("Loop", "ShouldSpawnLoopedGhost: rec is null, returning false");
                return false;
            }

            if (!rec.LoopPlayback)
            {
                ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' not looping, returning false");
                return false;
            }

            if (rec.LoopAnchorVesselId == 0)
            {
                ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' has no anchor vessel, returning false");
                return false;
            }

            if (!anchorVesselExists)
            {
                ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' anchor pid={rec.LoopAnchorVesselId} not found, returning false");
                return false;
            }

            // Body validation: anchor must be on the same body as when recorded
            if (!string.IsNullOrEmpty(recordingBodyName) &&
                !string.IsNullOrEmpty(anchorBodyName) &&
                anchorBodyName != recordingBodyName)
            {
                ParsekLog.Warn("Loop",
                    $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' anchor vessel body mismatch: " +
                    $"expected={recordingBodyName}, actual={anchorBodyName} — loop broken");
                return false;
            }

            ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' anchor pid={rec.LoopAnchorVesselId} valid, returning true");
            return true;
        }

        /// <summary>
        /// Pure-static gating check: determines whether a looped recording with an anchor
        /// should have its ghost active right now. Returns true if:
        /// - The recording has no anchor (anchorPid == 0) — unanchored loops always run
        /// - The anchor vessel PID is in the loadedAnchors set
        /// Returns false if the recording has an anchor and it is not loaded.
        /// </summary>
        internal static bool IsAnchorLoaded(uint anchorPid, HashSet<uint> loadedAnchors)
        {
            if (anchorPid == 0)
                return true; // No anchor configured — always allow

            if (loadedAnchors == null)
            {
                ParsekLog.Verbose("Loop", $"IsAnchorLoaded: loadedAnchors set is null, anchorPid={anchorPid} — returning false");
                return false;
            }

            bool loaded = loadedAnchors.Contains(anchorPid);
            ParsekLog.Verbose("Loop", $"IsAnchorLoaded: anchorPid={anchorPid}, loaded={loaded}");
            return loaded;
        }

        #endregion

        #region External Vessel Ghost Policy

        /// <summary>
        /// Injectable override for vessel existence checks (null = use FlightGlobals).
        /// Set via SetVesselExistsOverrideForTesting for unit tests.
        /// </summary>
        private static Func<uint, bool> vesselExistsOverride;

        // Frame-cached vessel PID set for O(1) lookup. Invalidated manually per frame
        // via InvalidateVesselCache(). Using manual invalidation instead of Time.frameCount
        // because Unity native properties crash in the test environment.
        private static HashSet<uint> cachedVesselPids;
        private static bool vesselCacheValid;

        /// <summary>
        /// Injectable override for chain-ghosted vessel checks (null = assume not ghosted).
        /// Set via SetIsGhostedOverride for unit tests; in production, wired to VesselGhoster.IsGhosted.
        /// </summary>
        private static Func<uint, bool> isGhostedOverride;

        /// <summary>
        /// Sets an injectable override for RealVesselExists, enabling unit testing
        /// without FlightGlobals. Pass null to restore default behavior.
        /// </summary>
        internal static void SetVesselExistsOverrideForTesting(Func<uint, bool> finder)
        {
            vesselExistsOverride = finder;
        }

        /// <summary>
        /// Sets an injectable override for IsGhostedByChain, enabling unit testing
        /// without VesselGhoster. Pass null to restore default behavior (not ghosted).
        /// </summary>
        internal static void SetIsGhostedOverride(Func<uint, bool> checker)
        {
            isGhostedOverride = checker;
        }

        /// <summary>
        /// Resets the injectable is-ghosted override. Call from test Dispose.
        /// </summary>
        internal static void ResetIsGhostedOverride()
        {
            isGhostedOverride = null;
        }

        /// <summary>
        /// Checks if a real vessel with the given persistentId currently exists in the game.
        /// If it exists, no ghost should be spawned (the real vessel serves as its own visual).
        /// If it doesn't exist, a fallback ghost should be spawned from stored background data.
        /// Uses injectable override when set (for testing).
        /// </summary>
        internal static bool RealVesselExists(uint vesselPersistentId)
        {
            if (vesselPersistentId == 0) return false;

            if (vesselExistsOverride != null)
                return vesselExistsOverride(vesselPersistentId);

            if (FlightGlobals.Vessels == null) return false;

            if (!vesselCacheValid)
            {
                if (cachedVesselPids == null)
                    cachedVesselPids = new HashSet<uint>();
                else
                    cachedVesselPids.Clear();

                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    if (FlightGlobals.Vessels[i] != null
                        && !GhostMapPresence.IsGhostMapVessel(FlightGlobals.Vessels[i].persistentId))
                        cachedVesselPids.Add(FlightGlobals.Vessels[i].persistentId);
                }
                vesselCacheValid = true;
                ParsekLog.VerboseRateLimited("Flight", "vessel-cache-rebuild",
                    $"RealVesselExists: rebuilt vessel PID cache ({cachedVesselPids.Count} vessels)");
            }

            return cachedVesselPids.Contains(vesselPersistentId);
        }

        /// <summary>
        /// Checks if a vessel is ghosted by a chain (despawned by VesselGhoster).
        /// Uses injectable override when set (for testing); defaults to false when
        /// no override is configured (no chain system active).
        /// </summary>
        private static bool IsGhostedByChain(uint vesselPersistentId)
        {
            if (isGhostedOverride != null)
                return isGhostedOverride(vesselPersistentId);
            return false;
        }

        /// <summary>
        /// Pure decision method: determines whether a ghost should be skipped for an
        /// external background vessel whose real vessel still exists in the game world.
        /// An "external vessel" is a tree recording that was tracked via BackgroundMap
        /// (not the active vessel) and whose VesselPersistentId matches a live vessel.
        /// Returns true if the ghost should be skipped.
        /// </summary>
        internal static bool ShouldSkipExternalVesselGhost(
            string treeId, uint vesselPersistentId, bool isActiveRecording)
        {
            // Only applies to tree recordings (standalone recordings don't have BackgroundMap)
            if (string.IsNullOrEmpty(treeId)) return false;

            // Active recording is the player's own vessel — always spawn its ghost
            if (isActiveRecording) return false;

            // PID 0 means we don't know the vessel — can't check existence
            if (vesselPersistentId == 0) return false;

            // Phase 6b: If vessel is ghosted by a chain, do NOT skip.
            // The real vessel has been despawned — the background recording
            // data must produce a ghost for the chain.
            if (IsGhostedByChain(vesselPersistentId))
            {
                ParsekLog.Verbose("Ghoster",
                    $"ShouldSkipExternalVesselGhost: vessel pid={vesselPersistentId} " +
                    "is ghosted by chain — NOT skipping");
                return false;
            }

            // Tree-owned vessel: if the recording's tree has recordings with this PID,
            // the vessel is part of the tree's own flight history — always show the ghost
            // so the user can see the recorded trajectory replayed. The real vessel may sit
            // at its save-time position, which is different from the ghost's interpolated path.
            if (IsVesselOwnedByTree(treeId, vesselPersistentId))
                return false;

            if (RealVesselExists(vesselPersistentId))
            {
                ParsekLog.Verbose("Flight",
                    $"Skipping external vessel ghost: real vessel {vesselPersistentId} exists");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks whether the given vessel PID belongs to any recording in the same tree.
        /// A tree "owns" a vessel PID if any of its recordings has that VesselPersistentId.
        /// Uses the cached OwnedVesselPids set on RecordingTree for O(1) lookup.
        /// </summary>
        internal static bool IsVesselOwnedByTree(string treeId, uint vesselPersistentId)
        {
            if (string.IsNullOrEmpty(treeId) || vesselPersistentId == 0) return false;

            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == treeId)
                    return trees[i].OwnedVesselPids.Contains(vesselPersistentId);
            }
            return false;
        }

        /// <summary>
        /// Resets the injectable vessel-exists override. Call from test Dispose.
        /// </summary>
        internal static void ResetVesselExistsOverride()
        {
            vesselExistsOverride = null;
        }

        /// <summary>
        /// Invalidate the vessel PID cache. Call once per frame before any
        /// RealVesselExists calls (e.g., first line of UpdateTimelinePlaybackViaEngine).
        /// </summary>
        internal static void InvalidateVesselCache()
        {
            vesselCacheValid = false;
        }

        /// <summary>
        /// Reset vessel cache state for testing. Clears cache and invalidation flag.
        /// Call alongside ResetVesselExistsOverride in test teardown.
        /// </summary>
        internal static void ResetVesselCacheForTesting()
        {
            cachedVesselPids = null;
            vesselCacheValid = false;
        }

        #endregion

        #region Ghost Info Population

        /// <summary>
        /// Builds a Dictionary keyed by partPersistentId from a list of ghost info items.
        /// Shared helper for the 6 simple PID-keyed dict constructions in PopulateGhostInfoDictionaries.
        /// </summary>
        private static Dictionary<uint, T> BuildDictByPid<T>(List<T> items, Func<T, uint> getPid)
        {
            var dict = new Dictionary<uint, T>();
            for (int i = 0; i < items.Count; i++)
                dict[getPid(items[i])] = items[i];
            return dict;
        }

        /// <summary>
        /// Converts a GhostBuildResult into the per-PID dictionaries on GhostPlaybackState.
        /// Shared between SpawnTimelineGhost and StartPlayback to eliminate code duplication.
        /// </summary>
        internal static void PopulateGhostInfoDictionaries(
            GhostPlaybackState state, GhostBuildResult result,
            IPlaybackTrajectory traj = null)
        {
            if (result == null) return;

            if (result.parachuteInfos != null)
                state.parachuteInfos = BuildDictByPid(result.parachuteInfos, p => p.partPersistentId);

            if (result.jettisonInfos != null)
                state.jettisonInfos = BuildDictByPid(result.jettisonInfos, j => j.partPersistentId);

            if (result.engineInfos != null)
            {
                state.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
                for (int i = 0; i < result.engineInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.engineInfos[i].partPersistentId, result.engineInfos[i].moduleIndex);
                    state.engineInfos[key] = result.engineInfos[i];
                }
            }

            if (result.deployableInfos != null)
                state.deployableInfos = BuildDictByPid(result.deployableInfos, d => d.partPersistentId);

            if (result.heatInfos != null)
            {
                state.heatInfos = BuildDictByPid(result.heatInfos, h => h.partPersistentId);

                // Initialize all heat parts to cold state at spawn — ensures FXModuleAnimateThrottle
                // parts don't inherit the prefab's baked emissive state.
                foreach (var kvp in state.heatInfos)
                {
                    var coldEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyHeatState(state, coldEvt, HeatLevel.Cold);
                }
            }

            if (result.lightInfos != null)
            {
                state.lightInfos = BuildDictByPid(result.lightInfos, l => l.partPersistentId);
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();
                for (int i = 0; i < result.lightInfos.Count; i++)
                    state.lightPlaybackStates[result.lightInfos[i].partPersistentId] = new LightPlaybackState();
            }

            if (result.fairingInfos != null)
                state.fairingInfos = BuildDictByPid(result.fairingInfos, f => f.partPersistentId);

            if (result.rcsInfos != null)
            {
                state.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
                for (int i = 0; i < result.rcsInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.rcsInfos[i].partPersistentId, result.rcsInfos[i].moduleIndex);
                    state.rcsInfos[key] = result.rcsInfos[i];
                }
            }

            if (result.roboticInfos != null)
            {
                state.roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
                for (int i = 0; i < result.roboticInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.roboticInfos[i].partPersistentId, result.roboticInfos[i].moduleIndex);
                    state.roboticInfos[key] = result.roboticInfos[i];
                }
            }

            if (result.colorChangerInfos != null)
                state.colorChangerInfos = GhostVisualBuilder.GroupColorChangersByPartId(result.colorChangerInfos);

            if (result.audioInfos != null)
            {
                // Cap audio sources per ghost to prevent channel exhaustion.
                // Keeps the first N entries in build order (largest parts tend to be listed first
                // in snapshot). No explicit sort — not worth the complexity for a 4-source cap.
                var audioList = result.audioInfos;
                if (audioList.Count > GhostAudioPresets.MaxAudioSourcesPerGhost)
                {
                    for (int i = GhostAudioPresets.MaxAudioSourcesPerGhost; i < audioList.Count; i++)
                    {
                        if (audioList[i].audioSource != null)
                            UnityEngine.Object.Destroy(audioList[i].audioSource);
                    }
                    audioList = audioList.GetRange(0, GhostAudioPresets.MaxAudioSourcesPerGhost);
                    ParsekLog.Verbose("GhostAudio",
                        $"Capped audio sources to {GhostAudioPresets.MaxAudioSourcesPerGhost} " +
                        $"(was {result.audioInfos.Count})");
                }

                state.audioInfos = new Dictionary<ulong, AudioGhostInfo>();
                for (int i = 0; i < audioList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        audioList[i].partPersistentId, audioList[i].moduleIndex);
                    state.audioInfos[key] = audioList[i];
                }
            }

            state.oneShotAudio = result.oneShotAudio;

            // Build engine event key set for orphan detection (scan over PartEvents).
            // Debris boosters that were running at breakup have no seed events because
            // BackgroundRecorder.InitializeLoadedState finds engine.isOperational=false
            // (fuel severed by decouple). When the key set is empty (ZERO engine events),
            // all engines on the ghost are auto-started — targeting pure debris recordings.
            // RCS is NOT auto-started (RCS is typically idle; orphan auto-start would
            // incorrectly fire on virtually every ghost).
            HashSet<ulong> engineKeysWithEvents = null;
            bool hasEngineOrAudioInfos = (state.audioInfos != null && state.audioInfos.Count > 0)
                || (state.engineInfos != null && state.engineInfos.Count > 0);
            if (hasEngineOrAudioInfos && traj != null && traj.PartEvents != null)
                engineKeysWithEvents = BuildEngineEventKeySet(traj.PartEvents);

            // Auto-start audio + visual FX for ALL engines on recordings with ZERO engine
            // events. This targets pure debris recordings: boosters that were running at
            // breakup but got no seed events. If the recording has ANY engine events,
            // engines without events were legitimately idle (e.g., Poodle during first
            // stage) or already shut down before breakup — not running orphans.
            if (engineKeysWithEvents != null && engineKeysWithEvents.Count == 0)
            {
                // Audio auto-start
                if (state.audioInfos != null && state.audioInfos.Count > 0)
                {
                    foreach (var kvp in state.audioInfos)
                    {
                        kvp.Value.currentPower = 1f;
                        if (kvp.Value.audioSource != null)
                        {
                            kvp.Value.audioSource.volume = 0f; // will be set by UpdateAudioAtmosphere
                            kvp.Value.audioSource.loop = true;
                            kvp.Value.audioSource.Play();
                        }
                        ParsekLog.Verbose("GhostAudio",
                            $"Auto-started audio for orphan engine key={kvp.Key} " +
                            $"(no engine events in recording — likely debris booster)");
                    }
                }

                // Engine FX auto-start
                if (state.engineInfos != null && state.engineInfos.Count > 0)
                {
                    foreach (var kvp in state.engineInfos)
                    {
                        uint pid; int midx;
                        FlightRecorder.DecodeEngineKey(kvp.Key, out pid, out midx);
                        // eventType unused by SetEngineEmission — only pid+midx matter
                        var syntheticEvt = new PartEvent { partPersistentId = pid, moduleIndex = midx };
                        SetEngineEmission(state, syntheticEvt, 1f);
                        ParsekLog.Verbose("GhostFx",
                            $"Auto-started engine FX for orphan engine key={kvp.Key} pid={pid} midx={midx} " +
                            $"(no engine events in recording — likely debris booster)");
                    }
                }
            }

        }

        /// <summary>
        /// Builds a set of engine event keys from a list of PartEvents.
        /// Keys represent (pid, moduleIndex) pairs that have at least one engine
        /// event (EngineIgnited, EngineThrottle, or EngineShutdown). Used by orphan
        /// auto-start: when the set is empty, ALL engines on the ghost are
        /// auto-started. EngineShutdown is included so that dead-engine sentinel
        /// seeds (#298) prevent the auto-start from firing on debris with depleted
        /// fuel. Pure static method for testability.
        /// </summary>
        internal static HashSet<ulong> BuildEngineEventKeySet(List<PartEvent> partEvents)
        {
            var keys = new HashSet<ulong>();
            if (partEvents == null) return keys;

            for (int pe = 0; pe < partEvents.Count; pe++)
            {
                var evt = partEvents[pe];
                if (evt.eventType == PartEventType.EngineIgnited
                    || evt.eventType == PartEventType.EngineThrottle
                    || evt.eventType == PartEventType.EngineShutdown)
                    keys.Add(FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex));
            }
            return keys;
        }

        #endregion

        #region Explosion / Visibility

        /// <summary>
        /// Pure decision logic: should we trigger an explosion for this ghost/recording pair?
        /// Extracted for testability and logging of guard condition skips.
        /// Parameters are primitives so this can be called from tests without GhostPlaybackState.
        /// </summary>
        internal static bool ShouldTriggerExplosion(bool explosionAlreadyFired, TerminalState? terminalState,
            bool ghostExists, string vesselName, int recIdx)
        {
            if (explosionAlreadyFired)
                return false;
            if (terminalState != TerminalState.Destroyed)
                return false;
            if (!ghostExists)
            {
                return false;
            }
            return true;
        }

        internal static void HideAllGhostParts(GhostPlaybackState state)
        {
            if (state.ghost == null) return;
            var t = state.ghost.transform;
            int hidden = 0;
            // Keep cameraPivot active — FlightCamera targets it during watch-mode hold.
            // Disabling it would make KSP snap the camera back to the active vessel.
            var pivotT = state.cameraPivot;
            for (int c = 0; c < t.childCount; c++)
            {
                var child = t.GetChild(c);
                if (pivotT != null && child == pivotT) continue;
                if (child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(false);
                    hidden++;
                }
            }
        }

        #endregion

        #region Part Events

        internal static void ApplyPartEvents(
            int recIdx, IPlaybackTrajectory rec, double currentUT, GhostPlaybackState state,
            bool allowTransientEffects = true)
        {
            if (rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state.ghost == null)
            {
                ParsekLog.VerboseRateLimited("Flight", $"apply-part-events-null-ghost-{recIdx}",
                    $"ApplyPartEvents: ghost is null for recording #{recIdx}");
                return;
            }

            int evtIdx = state.partEventIndex;
            var tree = state.partTree;
            var ghost = state.ghost;
            bool visibilityChanged = false;

            while (evtIdx < rec.PartEvents.Count && rec.PartEvents[evtIdx].ut <= currentUT)
            {
                var evt = rec.PartEvents[evtIdx];
                switch (evt.eventType)
                {
                    case PartEventType.Decoupled:
                        StopEngineFxForPart(state, evt.partPersistentId);
                        StopRcsFxForPart(state, evt.partPersistentId);
                        StopAudioForPart(state, evt.partPersistentId);
                        ApplyHeatState(state, evt, HeatLevel.Cold);
                        if (allowTransientEffects)
                            SpawnPartPuffAtPart(ghost, evt.partPersistentId);
                        if (tree != null)
                            HidePartSubtree(ghost, evt.partPersistentId, tree);
                        else
                            HideGhostPart(ghost, evt.partPersistentId);
                        GhostVisualBuilder.RebuildReentryMeshes(ghost, state.reentryFxInfo);
                        visibilityChanged = true;
                        break;
                    case PartEventType.Destroyed:
                        StopEngineFxForPart(state, evt.partPersistentId);
                        StopRcsFxForPart(state, evt.partPersistentId);
                        StopAudioForPart(state, evt.partPersistentId);
                        if (allowTransientEffects)
                            PlayOneShotAtGhost(state, evt.eventType);
                        ApplyHeatState(state, evt, HeatLevel.Cold);
                        if (allowTransientEffects)
                            SpawnPartPuffAtPart(ghost, evt.partPersistentId);
                        HideGhostPart(ghost, evt.partPersistentId);
                        GhostVisualBuilder.RebuildReentryMeshes(ghost, state.reentryFxInfo);
                        visibilityChanged = true;
                        break;
                    case PartEventType.ParachuteCut:
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo cutInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out cutInfo))
                            {
                                if (cutInfo.canopyTransform != null)
                                    cutInfo.canopyTransform.localScale = Vector3.zero;
                                if (cutInfo.capTransform != null)
                                    cutInfo.capTransform.gameObject.SetActive(false);
                            }
                        }
                        DestroyFakeCanopy(state, evt.partPersistentId);
                        break;
                    case PartEventType.ShroudJettisoned:
                        ApplyJettisonPanelState(state, evt, jettisoned: true);
                        break;
                    case PartEventType.ParachuteDestroyed:
                        // Clean up canopy visuals before hiding the part
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo destroyedInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out destroyedInfo))
                            {
                                if (destroyedInfo.canopyTransform != null)
                                    destroyedInfo.canopyTransform.localScale = Vector3.zero;
                            }
                        }
                        DestroyFakeCanopy(state, evt.partPersistentId);
                        HideGhostPart(ghost, evt.partPersistentId);
                        visibilityChanged = true;
                        break;
                    case PartEventType.ParachuteSemiDeployed:
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo semiInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out semiInfo) &&
                                semiInfo.canopyTransform != null && semiInfo.semiDeployedSampled)
                            {
                                semiInfo.canopyTransform.localScale = semiInfo.semiDeployedCanopyScale;
                                semiInfo.canopyTransform.localPosition = semiInfo.semiDeployedCanopyPos;
                                semiInfo.canopyTransform.localRotation = semiInfo.semiDeployedCanopyRot;
                                if (semiInfo.capTransform != null)
                                    semiInfo.capTransform.gameObject.SetActive(false);
                            }
                        }
                        break;
                    case PartEventType.ParachuteDeployed:
                        ApplyParachuteDeployedEvent(state, ghost, evt.partPersistentId);
                        break;
                    case PartEventType.EngineIgnited:
                        // Use at least a minimum emission on ignition (#165) — older
                        // recordings may contain seed events with throttle=0 from before
                        // the recording-side fix. The 0.01 floor ensures plume visibility
                        // for backward compatibility. New recordings skip zero-throttle
                        // engine seeds entirely (PartStateSeeder.EmitEngineSeedEvents).
                        SetEngineEmission(state, evt, System.Math.Max(evt.value, 0.01f));
                        SetEngineAudio(state, evt, System.Math.Max(evt.value, 0.01f));
                        break;
                    case PartEventType.EngineShutdown:
                        SetEngineEmission(state, evt, 0f);
                        SetEngineAudio(state, evt, 0f);
                        break;
                    case PartEventType.EngineThrottle:
                        SetEngineEmission(state, evt, evt.value);
                        SetEngineAudio(state, evt, evt.value);
                        break;
                    case PartEventType.DeployableExtended:
                        ApplyDeployableState(state, evt, deployed: true);
                        break;
                    case PartEventType.DeployableRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        break;
                    case PartEventType.ThermalAnimationHot:
                        ApplyHeatState(state, evt, HeatLevel.Hot);
                        break;
                    case PartEventType.ThermalAnimationMedium:
                        ApplyHeatState(state, evt, HeatLevel.Medium);
                        break;
                    case PartEventType.ThermalAnimationCold:
                        ApplyHeatState(state, evt, HeatLevel.Cold);
                        break;
                    case PartEventType.LightOn:
                        ApplyLightPowerEvent(state, evt.partPersistentId, true);
                        break;
                    case PartEventType.LightOff:
                        ApplyLightPowerEvent(state, evt.partPersistentId, false);
                        break;
                    case PartEventType.LightBlinkEnabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: true, evt.value);
                        break;
                    case PartEventType.LightBlinkDisabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: false, evt.value);
                        break;
                    case PartEventType.LightBlinkRate:
                        ApplyLightBlinkRateEvent(state, evt.partPersistentId, evt.value);
                        break;
                    case PartEventType.GearDeployed:
                        ApplyDeployableState(state, evt, deployed: true);
                        break;
                    case PartEventType.GearRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        break;
                    case PartEventType.CargoBayOpened:
                        if (!ApplyDeployableState(state, evt, deployed: true))
                            ApplyJettisonPanelState(state, evt, jettisoned: true);
                        break;
                    case PartEventType.CargoBayClosed:
                        if (!ApplyDeployableState(state, evt, deployed: false))
                            ApplyJettisonPanelState(state, evt, jettisoned: false);
                        break;
                    case PartEventType.FairingJettisoned:
                        if (state.fairingInfos != null)
                        {
                            FairingGhostInfo fInfo;
                            if (state.fairingInfos.TryGetValue(evt.partPersistentId, out fInfo)
                                && fInfo.fairingMeshObject != null)
                            {
                                fInfo.fairingMeshObject.SetActive(false);
                            }
                        }
                        break;
                    case PartEventType.RCSActivated:
                        SetRcsEmission(state, evt, evt.value);
                        break;
                    case PartEventType.RCSStopped:
                        SetRcsEmission(state, evt, 0f);
                        break;
                    case PartEventType.RCSThrottle:
                        SetRcsEmission(state, evt, evt.value);
                        break;
                    case PartEventType.RoboticMotionStarted:
                    case PartEventType.RoboticPositionSample:
                    case PartEventType.RoboticMotionStopped:
                        ApplyRoboticEvent(state, evt, currentUT);
                        break;
                    case PartEventType.InventoryPartPlaced:
                        SetGhostPartActive(ghost, evt.partPersistentId, true);
                        visibilityChanged = true;
                        break;
                    case PartEventType.InventoryPartRemoved:
                        SetGhostPartActive(ghost, evt.partPersistentId, false);
                        visibilityChanged = true;
                        break;
                }
                evtIdx++;
            }

            int appliedCount = evtIdx - state.partEventIndex;
            state.partEventIndex = evtIdx;
            if (appliedCount > 0)
                ParsekLog.VerboseRateLimited("Flight", $"part-events-{recIdx}",
                    $"Applied {appliedCount} part events for ghost #{recIdx} (evtIdx now {evtIdx})");
            if (visibilityChanged)
                RecalculateCameraPivot(state);
            UpdateBlinkingLights(state, currentUT);
            UpdateActiveRobotics(state, currentUT);
        }

        /// <summary>
        /// Spawns a small smoke puff + spark FX at a ghost part's world position.
        /// Called before hiding the part on Decoupled/Destroyed events.
        /// </summary>
        internal static void SpawnPartPuffAtPart(GameObject ghost, uint persistentId)
        {
            if (ghost == null) return;
            if (ShouldSuppressVisualFx(TimeWarp.CurrentRate)) return;
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t == null)
            {
                return;
            }
            if (!t.gameObject.activeSelf)
            {
                return;
            }

            // Estimate part scale from its renderer bounds
            float partScale = 1f;
            var renderer = t.GetComponentInChildren<Renderer>();
            if (renderer != null)
                partScale = renderer.bounds.size.magnitude * 0.5f;

            var pos = t.position;
            GhostVisualBuilder.SpawnPartPuffFx(pos, partScale);
        }

        internal static void HideGhostPart(GameObject ghost, uint persistentId)
        {
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(false);
        }

        internal static void SetGhostPartActive(GameObject ghost, uint persistentId, bool active)
        {
            if (ghost == null) return;
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(active);
        }

        internal static void InitializeInventoryPlacementVisibility(
            IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (rec == null || rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state == null || state.ghost == null) return;

            // If a part's first placement-related event is "placed", start hidden so it
            // visibly appears only when the event fires.
            var initialized = new HashSet<uint>();
            int hidden = 0;
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                var evt = rec.PartEvents[i];
                if (initialized.Contains(evt.partPersistentId)) continue;

                if (evt.eventType == PartEventType.InventoryPartPlaced)
                {
                    SetGhostPartActive(state.ghost, evt.partPersistentId, false);
                    initialized.Add(evt.partPersistentId);
                    hidden++;
                }
                else if (evt.eventType == PartEventType.InventoryPartRemoved)
                {
                    SetGhostPartActive(state.ghost, evt.partPersistentId, true);
                    initialized.Add(evt.partPersistentId);
                }
            }
        }

        /// <summary>
        /// Initializes flag ghost visibility — all flags start hidden and appear when their event fires.
        /// </summary>
        internal static void InitializeFlagVisibility(IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (rec == null || rec.FlagEvents == null || rec.FlagEvents.Count == 0) return;
            if (state == null) return;
            state.flagEventIndex = 0;
        }

        /// <summary>
        /// Spawns flag vessels when their UT is reached. Flags are permanent world objects —
        /// they are never destroyed by Parsek. Duplicate check prevents re-spawning on loop wrap.
        /// The FlagEvent in the recording tracks which flag was planted (name, position, texture, plaque).
        /// </summary>
        internal static void ApplyFlagEvents(GhostPlaybackState state, IPlaybackTrajectory rec, double currentUT)
        {
            if (rec == null || rec.FlagEvents == null || rec.FlagEvents.Count == 0) return;
            if (state == null) return;

            while (state.flagEventIndex < rec.FlagEvents.Count)
            {
                var evt = rec.FlagEvents[state.flagEventIndex];
                if (evt.ut > currentUT) break;

                // Spawn a real, permanent flag vessel — skip if one already exists at this position
                if (!FlagExistsAtPosition(evt))
                    GhostVisualBuilder.SpawnFlagVessel(evt);

                state.flagEventIndex++;
            }
        }

        /// <summary>
        /// Checks if a flag vessel already exists within 1m of the event position (prevents duplicates on loop).
        /// Uses world-space 3D distance rather than lat/lon to handle high-latitude and small-body cases correctly.
        /// </summary>
        private static bool FlagExistsAtPosition(FlagEvent evt)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == evt.bodyName);
            if (body == null || FlightGlobals.Vessels == null) return false;

            Vector3d eventPos = body.GetWorldSurfacePosition(evt.latitude, evt.longitude, evt.altitude);

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v == null || v.vesselType != VesselType.Flag) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                if (v.mainBody != body) continue;

                Vector3d flagPos = body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
                double dx = flagPos.x - eventPos.x;
                double dy = flagPos.y - eventPos.y;
                double dz = flagPos.z - eventPos.z;
                if (dx * dx + dy * dy + dz * dz < 1.0) // within 1m
                    return true;
            }
            return false;
        }

        internal static void HidePartSubtree(GameObject ghost, uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            int hidden = 0;
            int notFound = 0;
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                var t = ghost.transform.Find($"ghost_part_{pid}");
                if (t != null)
                {
                    t.gameObject.SetActive(false);
                    hidden++;
                }
                else
                    notFound++;
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                    for (int c = 0; c < children.Count; c++)
                        stack.Push(children[c]);
            }
        }

        /// <summary>
        /// Recalculate cameraPivot position after a visibility change (decouple/destroy).
        /// Sets localPosition to midpoint of remaining active parts' bounding extent.
        /// </summary>
        internal static void RecalculateCameraPivot(GhostPlaybackState state)
        {
            if (state.ghost == null || state.cameraPivot == null) return;
            var ghostTransform = state.ghost.transform;
            int count = 0;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int i = 0; i < ghostTransform.childCount; i++)
            {
                var child = ghostTransform.GetChild(i);
                if (!child.gameObject.activeSelf || !child.name.StartsWith("ghost_part_"))
                    continue;
                var pos = child.localPosition;
                if (count == 0) { min = max = pos; }
                else { min = Vector3.Min(min, pos); max = Vector3.Max(max, pos); }
                count++;
            }
            state.cameraPivot.localPosition = count > 0 ? (min + max) * 0.5f : Vector3.zero;
            ParsekLog.Info("CameraFollow",
                $"Camera pivot recalculated: localPos=({state.cameraPivot.localPosition.x:F2},{state.cameraPivot.localPosition.y:F2},{state.cameraPivot.localPosition.z:F2})" +
                $" activeParts={count}");
        }

        #endregion

        #region Canopy Management

        /// <summary>
        /// Applies a ParachuteDeployed event: sets the real canopy to deployed pose if available,
        /// otherwise creates a fake canopy sphere as fallback. Hides the cap in both cases.
        /// </summary>
        private static void ApplyParachuteDeployedEvent(GhostPlaybackState state, GameObject ghost, uint partPersistentId)
        {
            bool usedRealCanopy = false;

            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo info;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out info) && info.canopyTransform != null)
                {
                    info.canopyTransform.localScale = info.deployedCanopyScale;
                    info.canopyTransform.localPosition = info.deployedCanopyPos;
                    info.canopyTransform.localRotation = info.deployedCanopyRot;
                    if (info.capTransform != null)
                        info.capTransform.gameObject.SetActive(false);
                    usedRealCanopy = true;
                }
            }

            if (!usedRealCanopy)
            {
                var canopy = GhostVisualBuilder.CreateFakeCanopy(ghost, partPersistentId);
                if (canopy != null)
                {
                    TrackFakeCanopy(state, partPersistentId, canopy);
                }
            }
        }

        internal static void TrackFakeCanopy(GhostPlaybackState state, uint partPid, GameObject canopy)
        {
            if (state.fakeCanopies == null)
                state.fakeCanopies = new Dictionary<uint, GameObject>();
            // Destroy previous canopy for this part if one exists (prevents leak)
            GameObject existing;
            if (state.fakeCanopies.TryGetValue(partPid, out existing) && existing != null)
                DestroyCanopyAndMaterial(existing);
            state.fakeCanopies[partPid] = canopy;
        }

        internal static void DestroyFakeCanopy(GhostPlaybackState state, uint partPid)
        {
            if (state.fakeCanopies == null) return;
            GameObject canopy;
            if (state.fakeCanopies.TryGetValue(partPid, out canopy) && canopy != null)
                DestroyCanopyAndMaterial(canopy);
            state.fakeCanopies.Remove(partPid);
        }

        internal static void DestroyAllFakeCanopies(GhostPlaybackState state)
        {
            if (state.fakeCanopies == null) return;
            foreach (var kv in state.fakeCanopies)
                if (kv.Value != null) DestroyCanopyAndMaterial(kv.Value);
            state.fakeCanopies = null;
        }

        internal static void DestroyCanopyAndMaterial(GameObject canopy)
        {
            var renderer = canopy.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
                UnityEngine.Object.Destroy(renderer.material);
            UnityEngine.Object.Destroy(canopy);
        }

        #endregion

        #region Engine FX

        internal static string BuildEngineFxEmissionDiagnostic(
            string partName,
            uint partPersistentId,
            int moduleIndex,
            float power,
            string particleName,
            string parentName,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 worldPosition,
            Vector3 worldForward,
            Vector3 worldUp,
            float emissionRate,
            float startSpeed,
            bool isPlaying)
        {
            string safePartName = string.IsNullOrEmpty(partName) ? "<unknown>" : partName;
            string safeParticleName = string.IsNullOrEmpty(particleName) ? "<unknown>" : particleName;
            string safeParentName = string.IsNullOrEmpty(parentName) ? "<none>" : parentName;
            string localRotationRaw =
                $"({localRotation.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.z.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.w.ToString("F4", CultureInfo.InvariantCulture)})";

            return $"Engine FX emission diag: part='{safePartName}' pid={partPersistentId} midx={moduleIndex} " +
                $"power={power.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"ps='{safeParticleName}' parent='{safeParentName}' " +
                $"localPos={FormatVector3Invariant(localPosition)} localRot={localRotationRaw} " +
                $"worldPos={FormatVector3Invariant(worldPosition)} " +
                $"worldFwd={FormatVector3Invariant(worldForward)} " +
                $"worldUp={FormatVector3Invariant(worldUp)} " +
                $"rate={emissionRate.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"speed={startSpeed.ToString("F2", CultureInfo.InvariantCulture)} playing={isPlaying}";
        }

        internal static string FormatVector3Invariant(Vector3 value)
        {
            return $"({value.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.z.ToString("F4", CultureInfo.InvariantCulture)})";
        }

        internal static void SetEngineEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.engineInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            EngineGhostInfo info;
            if (!state.engineInfos.TryGetValue(key, out info)) return;

            // Control KSPParticleEmitter.emit via reflection — this is the ONLY particle
            // creation source. Unity's emission module is permanently disabled (bug #105).
            SetKspEmittersEnabled(info.kspEmitters, power > 0f);

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                if (power > 0f)
                {
                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();
                }
                else
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

            }
        }

        /// <summary>
        /// Stop all engine FX particle systems for a given part (by PID).
        /// Used defensively on decouple/destroy to ensure no orphaned engine glow.
        /// </summary>
        internal static void StopEngineFxForPart(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.engineInfos == null) return;
            foreach (var info in state.engineInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    var ps = info.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Stop all RCS FX particle systems for a given part (by PID).
        /// Used defensively on decouple/destroy to ensure no orphaned RCS glow.
        /// </summary>
        internal static void StopRcsFxForPart(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.rcsInfos == null) return;
            foreach (var info in state.rcsInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    var ps = info.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        #region Ghost Audio Control

        /// <summary>
        /// Set engine audio volume/pitch from recorded throttle power.
        /// Called alongside SetEngineEmission for EngineIgnited/Throttle/Shutdown events.
        /// </summary>
        internal static void SetEngineAudio(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.audioInfos == null) return;
            if (state.audioMuted)
            {
                // Ensure source is stopped if muted (e.g., during warp)
                ulong mutedKey = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
                AudioGhostInfo mutedInfo;
                if (state.audioInfos.TryGetValue(mutedKey, out mutedInfo) &&
                    mutedInfo.audioSource != null && mutedInfo.audioSource.isPlaying)
                    mutedInfo.audioSource.Stop();
                return;
            }

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            AudioGhostInfo info;
            if (!state.audioInfos.TryGetValue(key, out info)) return;
            if (info.audioSource == null) return;

            info.currentPower = power;

            if (power > 0f && state.atmosphereFactor > 0.001f)
            {
                float vol = ComputeGhostAudioVolume(
                    info.volumeCurve.Evaluate(power), state.atmosphereFactor);
                if (vol <= 0f) { if (info.audioSource.isPlaying) info.audioSource.Stop(); return; }
                float pitch = info.pitchCurve.Evaluate(power);
                info.audioSource.volume = vol;
                info.audioSource.pitch = pitch;
                if (!info.audioSource.isPlaying)
                {
                    info.audioSource.clip = info.clip;
                    info.audioSource.Play();
                    ParsekLog.VerboseRateLimited("GhostAudio",
                        $"audio-start-{evt.partPersistentId}-{evt.moduleIndex}",
                        $"Engine audio started: pid={evt.partPersistentId} midx={evt.moduleIndex} " +
                        $"power={power:F2} vol={vol:F2} pitch={pitch:F2}", 5.0);
                }
            }
            else
            {
                if (info.audioSource.isPlaying)
                {
                    info.audioSource.Stop();
                    ParsekLog.VerboseRateLimited("GhostAudio",
                        $"audio-stop-{evt.partPersistentId}-{evt.moduleIndex}",
                        $"Engine audio stopped: pid={evt.partPersistentId} midx={evt.moduleIndex}", 5.0);
                }
            }
        }

        /// <summary>
        /// Stop all audio sources for a given part (by PID).
        /// Used defensively on decouple/destroy.
        /// </summary>
        internal static void StopAudioForPart(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.audioInfos == null) return;
            foreach (var info in state.audioInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                if (info.audioSource != null && info.audioSource.isPlaying)
                    info.audioSource.Stop();
            }
        }

        /// <summary>
        /// Play a one-shot sound effect at the ghost root position.
        /// Used for decouple and explosion events.
        /// </summary>
        internal static void PlayOneShotAtGhost(GhostPlaybackState state, PartEventType eventType)
        {
            if (state.oneShotAudio?.audioSource == null) return;
            // One-shot events (explosions) bypass audioMuted — they're dramatic moments
            // that should always be audible, even for overlap ghosts about to expire.
            if (state.atmosphereFactor < 0.001f) return; // no sound in vacuum

            string clipPath = GhostAudioPresets.ResolveOneShotClip(eventType);
            if (clipPath == null) return;

            var clip = GameDatabase.Instance.GetAudioClip(clipPath);
            if (clip == null) return;

            float vol = ComputeGhostAudioVolume(GhostAudioPresets.OneShotVolumeScale, state.atmosphereFactor);
            if (vol <= 0f) return;

            state.oneShotAudio.audioSource.PlayOneShot(clip, vol);
            ParsekLog.Verbose("GhostAudio",
                $"One-shot played: {eventType} clip='{clipPath}' vol={vol:F2}");
        }

        /// <summary>
        /// Mute all ghost audio sources (during high warp or ghost hidden).
        /// </summary>
        internal static void MuteAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (state.audioMuted) return;
            state.audioMuted = true;

            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null && info.audioSource.isPlaying)
                        info.audioSource.Stop();
                }
            }
            if (state.oneShotAudio?.audioSource != null)
                state.oneShotAudio.audioSource.Stop();
        }

        /// <summary>
        /// Unmute ghost audio. Active engines will resume on next throttle event.
        /// </summary>
        internal static void UnmuteAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (!state.audioMuted) return;
            state.audioMuted = false;
            // Audio restores naturally via next ApplyPartEvents cycle.
        }

        /// <summary>
        /// Pause all ghost audio sources for this state, preserving playback position.
        /// Used by the game pause handler so ESC menu mutes ghost audio.
        /// Unlike MuteAllAudio which calls Stop() (resetting position), this calls
        /// Pause() so UnPauseAllAudio can resume exactly where it left off.
        /// </summary>
        internal static void PauseAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null && info.audioSource.isPlaying)
                        info.audioSource.Pause();
                }
            }
            if (state.oneShotAudio?.audioSource != null && state.oneShotAudio.audioSource.isPlaying)
                state.oneShotAudio.audioSource.Pause();
        }

        /// <summary>
        /// Resume all ghost audio sources paused by PauseAllAudio.
        /// </summary>
        internal static void UnpauseAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null)
                        info.audioSource.UnPause();
                }
            }
            if (state.oneShotAudio?.audioSource != null)
                state.oneShotAudio.audioSource.UnPause();
        }

        /// <summary>
        /// Compute the ghost audio volume for a given power level and atmosphere state.
        /// Centralizes the volume formula so SetEngineAudio, PlayOneShotAtGhost, and
        /// UpdateAudioAtmosphere all use the same calculation.
        /// </summary>
        internal static float ComputeGhostAudioVolume(float curveValue, float atmosphereFactor)
        {
            float settingsVolume = ParsekSettings.Current?.ghostAudioVolume ?? 1.0f;
            return curveValue * settingsVolume * GameSettings.SHIP_VOLUME * atmosphereFactor;
        }

        /// <summary>
        /// Compute atmosphere attenuation factor for ghost audio.
        /// Returns 0 in vacuum (no atmosphere or above atmosphere depth), 1 at sea level,
        /// with smooth quadratic falloff at high altitude.
        /// Uses cached CelestialBody on state to avoid per-frame linear search.
        /// </summary>
        internal static float ComputeAtmosphereFactor(GhostPlaybackState state)
        {
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName)) return 0f;

            // Cache the CelestialBody lookup — body only changes on SOI transitions.
            CelestialBody body = state.cachedAudioBody;
            if (body == null || state.cachedAudioBodyName != bodyName)
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                state.cachedAudioBody = body;
                state.cachedAudioBodyName = bodyName;
            }

            if (body == null || !body.atmosphere) return 0f;
            if (altitude >= body.atmosphereDepth) return 0f;
            if (altitude <= 0) return 1f;

            // Quadratic falloff: factor = (1 - alt/depth)^2
            // Sea level: 1.0. Half depth: 0.25. Edge: 0.0.
            float ratio = (float)(altitude / body.atmosphereDepth);
            float factor = (1f - ratio) * (1f - ratio);
            return factor;
        }

        /// <summary>
        /// Per-frame update of atmosphere factor and volume adjustment for all playing audio sources.
        /// Ensures smooth fade as ghost ascends/descends through atmosphere.
        /// </summary>
        internal static void UpdateAudioAtmosphere(GhostPlaybackState state)
        {
            if (state == null || state.audioInfos == null || state.audioMuted) return;

            float newFactor = ComputeAtmosphereFactor(state);

            // Log transitions (vacuum / atmosphere)
            bool wasInVacuum = state.atmosphereFactor < 0.001f;
            bool nowInVacuum = newFactor < 0.001f;
            if (wasInVacuum != nowInVacuum)
            {
                ParsekLog.VerboseRateLimited("GhostAudio", $"atm-transition-{state.vesselName}",
                    nowInVacuum
                        ? $"Ghost '{state.vesselName}' entered vacuum — audio silent"
                        : $"Ghost '{state.vesselName}' entered atmosphere — audio enabled (factor={newFactor:F3})",
                    2.0);
            }

            state.atmosphereFactor = newFactor;

            foreach (var info in state.audioInfos.Values)
            {
                if (info.audioSource == null) continue;

                if (newFactor < 0.001f)
                {
                    if (info.audioSource.isPlaying)
                        info.audioSource.Stop();
                }
                else if (info.currentPower > 0f && info.audioSource.isPlaying)
                {
                    info.audioSource.volume = ComputeGhostAudioVolume(
                        info.volumeCurve.Evaluate(info.currentPower), newFactor);
                }
            }
        }

        #endregion

        /// <summary>
        /// Stop and clear all engine FX particle systems across every engine info in the state.
        /// Used during ghost teardown to ensure no orphaned particle effects remain.
        /// </summary>
        internal static void StopAllEngineFx(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return;
            foreach (var kv in state.engineInfos)
            {
                SetKspEmittersEnabled(kv.Value.kspEmitters, false);
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Stop and clear all RCS FX particle systems across every RCS info in the state.
        /// Used during ghost teardown to ensure no orphaned particle effects remain.
        /// </summary>
        internal static void StopAllRcsFx(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            foreach (var kv in state.rcsInfos)
            {
                SetKspEmittersEnabled(kv.Value.kspEmitters, false);
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Detaches active particle systems from the ghost hierarchy so they can linger
        /// and fade out naturally after the ghost is destroyed (#107). Stops emission,
        /// unparents, and schedules delayed destruction.
        /// </summary>
        internal static void DetachAndLingerParticleSystems(
            List<ParticleSystem> particleSystems, List<KspEmitterRef> kspEmitters, float lingerSeconds = 8f)
        {
            if (kspEmitters != null)
                SetKspEmittersEnabled(kspEmitters, false);
            if (particleSystems == null) return;

            for (int i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;

                // Only detach if particles are alive (no point lingering an empty system)
                if (ps.particleCount == 0)
                {
                    UnityEngine.Object.Destroy(ps.gameObject);
                    continue;
                }

                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                ps.transform.SetParent(null, true);
                UnityEngine.Object.Destroy(ps.gameObject, lingerSeconds);
            }
            particleSystems.Clear();
        }

        internal static void StopAndClearParticleSystems(
            List<ParticleSystem> particleSystems, List<KspEmitterRef> kspEmitters)
        {
            if (kspEmitters != null)
                SetKspEmittersEnabled(kspEmitters, false);
            if (particleSystems == null) return;

            for (int i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(true);
                SetParticleRenderersEnabled(ps, false);
            }
            particleSystems.Clear();
        }

        internal static void SetParticleRenderersEnabled(ParticleSystem ps, bool enabled)
        {
            if (ps == null)
                return;

            ParticleSystemRenderer[] renderers = ps.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = enabled;
            }
        }

        /// <summary>
        /// Enable or disable KSPParticleEmitter.emit on all captured emitters via reflection.
        /// KSPParticleEmitter is the ONLY particle creation source on ghost FX objects —
        /// Unity's emission module is permanently disabled to prevent bubble artifacts (bug #105).
        /// </summary>
        private static void SetKspEmittersEnabled(List<KspEmitterRef> kspEmitters, bool enabled)
        {
            if (kspEmitters == null) return;
            for (int i = 0; i < kspEmitters.Count; i++)
            {
                var r = kspEmitters[i];
                if (r.emitter == null || r.emitField == null) continue;
                r.emitField.SetValue(r.emitter, enabled);
            }
        }

        #endregion

        #region RCS FX

        internal static void SetRcsEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.rcsInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            RcsGhostInfo info;
            if (!state.rcsInfos.TryGetValue(key, out info)) return;

            // Control KSPParticleEmitter.emit via reflection — this is the ONLY particle
            // creation source. Unity's emission module is permanently disabled (bug #105).
            SetKspEmittersEnabled(info.kspEmitters, power > 0f);

            int configuredSystems = 0;
            int enabledRenderers = 0;
            int playingSystems = 0;
            float sampleSpeed = 0f;
            float sampleSize = 0f;
            float sampleLifetime = 0f;

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                configuredSystems++;
                if (power > 0f)
                {
                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();

                    if (sampleSpeed <= 0f)
                    {
                        var main = ps.main;
                        sampleSpeed = main.startSpeedMultiplier;
                        sampleSize = main.startSizeMultiplier;
                        sampleLifetime = main.startLifetimeMultiplier;
                    }
                }
                else
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

                if (ps.isPlaying) playingSystems++;
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.enabled) enabledRenderers++;
            }

        }

        internal static float ComputeScaledRcsEmissionRate(
            FloatCurve emissionCurve, float power, float emissionScale)
        {
            if (power <= 0f) return 0f;

            float emRate = emissionCurve != null ? emissionCurve.Evaluate(power) : power * 100f;
            emRate *= emissionScale > 0f ? emissionScale : 1f;
            if (emissionScale > 1f)
                emRate = Math.Max(emRate, 60f);

            return emRate;
        }

        internal static float ComputeScaledRcsSpeed(
            FloatCurve speedCurve, float power, float speedScale)
        {
            if (power <= 0f) return 0f;

            float spd = speedCurve != null ? speedCurve.Evaluate(power) : power * 10f;
            spd *= speedScale > 0f ? speedScale : 1f;
            if (speedScale > 1f)
                spd = Math.Max(spd, 4f);

            return spd;
        }

        internal static void StopAllRcsEmissions(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            if (state.rcsSuppressed) return;
            state.rcsSuppressed = true;
            int suppressedCount = 0;
            foreach (var info in state.rcsInfos.Values)
            {
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int j = 0; j < info.particleSystems.Count; j++)
                {
                    var ps = info.particleSystems[j];
                    if (ps != null)
                    {
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(true);
                    }
                }
                suppressedCount++;
            }
        }

        internal static void RestoreAllRcsEmissions(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            if (!state.rcsSuppressed) return;
            state.rcsSuppressed = false;
            int restoredCount = 0;
            foreach (var info in state.rcsInfos.Values)
            {
                // Only restore emission for RCS that had active KSP emitters.
                // Check if any KSPParticleEmitter was playing before suppression
                // by looking at whether the particle system was playing (ps.isPlaying
                // stays true after Stop with StopEmittingAndClear until particles expire).
                // Since we call Clear(), isPlaying is false after suppress. Instead, check
                // if any renderers are enabled — SetRcsEmission enables renderers when active.
                bool wasActive = false;
                for (int j = 0; j < info.particleSystems.Count; j++)
                {
                    var ps = info.particleSystems[j];
                    if (ps == null) continue;
                    var renderer = ps.GetComponent<ParticleSystemRenderer>();
                    if (renderer != null && renderer.enabled)
                    {
                        wasActive = true;
                        break;
                    }
                }
                if (wasActive)
                {
                    SetKspEmittersEnabled(info.kspEmitters, true);
                    for (int j = 0; j < info.particleSystems.Count; j++)
                    {
                        var ps = info.particleSystems[j];
                        if (ps != null && !ps.isPlaying)
                            ps.Play();
                    }
                    restoredCount++;
                }
            }
            ParsekLog.Info("Flight", $"Restored RCS emissions for {restoredCount} modules");
        }

        #endregion

        #region Robotic

        internal static float ComputeRotorDeltaDegrees(float rpm, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                return 0f;
            if (float.IsNaN(rpm) || float.IsInfinity(rpm) || Mathf.Abs(rpm) <= 0.0001f)
                return 0f;

            // RPM * 360deg / 60s
            return rpm * 6f * (float)deltaSeconds;
        }

        private static void ApplyRoboticPose(RoboticGhostInfo info, float value)
        {
            if (info == null || info.servoTransform == null)
                return;

            Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                ? info.axisLocal.normalized
                : Vector3.up;

            if (info.visualMode == RoboticVisualMode.Linear)
            {
                info.servoTransform.localPosition = info.stowedPos + (axis * value);
            }
            else if (info.visualMode == RoboticVisualMode.Rotational)
            {
                info.servoTransform.localRotation =
                    info.stowedRot * Quaternion.AngleAxis(value, axis);
            }
        }

        private static void ApplyRoboticEvent(
            GhostPlaybackState state, PartEvent evt, double currentUT)
        {
            if (state == null || state.roboticInfos == null)
                return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            if (!state.roboticInfos.TryGetValue(key, out RoboticGhostInfo info) || info == null)
                return;

            info.currentValue = evt.value;

            if (info.visualMode == RoboticVisualMode.RotorRpm)
            {
                info.active = evt.eventType != PartEventType.RoboticMotionStopped &&
                    Mathf.Abs(evt.value) > 0.0001f;
                info.lastUpdateUT = currentUT;
            }
            else
            {
                ApplyRoboticPose(info, evt.value);
                info.active = evt.eventType != PartEventType.RoboticMotionStopped;
                info.lastUpdateUT = currentUT;
            }
        }

        internal static void UpdateActiveRobotics(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.roboticInfos == null || state.roboticInfos.Count == 0)
                return;

            foreach (var kv in state.roboticInfos)
            {
                RoboticGhostInfo info = kv.Value;
                if (info == null || info.servoTransform == null)
                    continue;

                if (double.IsNaN(info.lastUpdateUT) || double.IsInfinity(info.lastUpdateUT))
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                double deltaSeconds = currentUT - info.lastUpdateUT;
                if (deltaSeconds <= 0)
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                // Timeline jumps/loop boundaries rebuild ghosts, but guard large UT gaps anyway.
                deltaSeconds = Math.Min(deltaSeconds, 1.0);

                if (info.visualMode == RoboticVisualMode.RotorRpm && info.active)
                {
                    float deltaDegrees = ComputeRotorDeltaDegrees(info.currentValue, deltaSeconds);
                    if (Mathf.Abs(deltaDegrees) > 0.0001f)
                    {
                        Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                            ? info.axisLocal.normalized
                            : Vector3.up;
                        info.servoTransform.localRotation =
                            info.servoTransform.localRotation * Quaternion.AngleAxis(deltaDegrees, axis);
                    }
                }

                info.lastUpdateUT = currentUT;
            }
        }

        #endregion

        #region Heat / Reentry

        internal static bool ApplyHeatState(GhostPlaybackState state, PartEvent evt, HeatLevel level)
        {
            if (state == null || state.heatInfos == null) return false;

            if (!state.heatInfos.TryGetValue(evt.partPersistentId, out HeatGhostInfo info) || info == null)
                return false;

            bool applied = false;

            if (info.transforms != null)
            {
                for (int i = 0; i < info.transforms.Count; i++)
                {
                    var ts = info.transforms[i];
                    if (ts.t == null) continue;

                    switch (level)
                    {
                        case HeatLevel.Hot:
                            ts.t.localPosition = ts.hotPos;
                            ts.t.localRotation = ts.hotRot;
                            ts.t.localScale = ts.hotScale;
                            break;
                        case HeatLevel.Medium:
                            ts.t.localPosition = ts.mediumPos;
                            ts.t.localRotation = ts.mediumRot;
                            ts.t.localScale = ts.mediumScale;
                            break;
                        default:
                            ts.t.localPosition = ts.coldPos;
                            ts.t.localRotation = ts.coldRot;
                            ts.t.localScale = ts.coldScale;
                            break;
                    }
                    applied = true;
                }
            }

            if (info.materialStates != null)
            {
                for (int i = 0; i < info.materialStates.Count; i++)
                {
                    HeatMaterialState materialState = info.materialStates[i];
                    if (materialState.material == null) continue;

                    Color color, emission;
                    switch (level)
                    {
                        case HeatLevel.Hot:
                            color = materialState.hotColor;
                            emission = materialState.hotEmission;
                            break;
                        case HeatLevel.Medium:
                            color = materialState.mediumColor;
                            emission = materialState.mediumEmission;
                            break;
                        default:
                            color = materialState.coldColor;
                            emission = materialState.coldEmission;
                            break;
                    }

                    if (!string.IsNullOrEmpty(materialState.colorProperty))
                        materialState.material.SetColor(materialState.colorProperty, color);

                    if (!string.IsNullOrEmpty(materialState.emissiveProperty))
                        materialState.material.SetColor(materialState.emissiveProperty, emission);

                    applied = true;
                }
            }

            if (applied)
                ParsekLog.VerboseRateLimited("Flight", $"heat-{evt.partPersistentId}",
                    $"Part pid={evt.partPersistentId}: applied heat level {level}", 5.0);

            return applied;
        }

        internal static void ResetReentryFx(GhostPlaybackState state, int recIdx)
        {
            var info = state.reentryFxInfo;
            if (info == null) return;

            info.lastIntensity = 0f;

            if (info.fireParticles != null)
            {
                info.fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                info.fireParticles.Clear(true);
            }

            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;
                    if (!string.IsNullOrEmpty(ms.emissiveProperty))
                        ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                    if (!string.IsNullOrEmpty(ms.colorProperty))
                        ms.material.SetColor(ms.colorProperty, ms.coldColor);
                }
            }

        }

        #endregion

        #region Deployables / Jettison

        internal static bool ApplyDeployableState(GhostPlaybackState state, PartEvent evt, bool deployed)
        {
            if (state.deployableInfos == null) return false;

            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(evt.partPersistentId, out info)) return false;

            bool applied = false;

            for (int i = 0; i < info.transforms.Count; i++)
            {
                var ts = info.transforms[i];
                if (ts.t == null) continue;
                applied = true;
                if (deployed)
                {
                    ts.t.localPosition = ts.deployedPos;
                    ts.t.localRotation = ts.deployedRot;
                    ts.t.localScale = ts.deployedScale;
                }
                else
                {
                    ts.t.localPosition = ts.stowedPos;
                    ts.t.localRotation = ts.stowedRot;
                    ts.t.localScale = ts.stowedScale;
                }
            }

            return applied;
        }

        internal static bool ApplyJettisonPanelState(GhostPlaybackState state, PartEvent evt, bool jettisoned)
        {
            if (state.jettisonInfos == null) return false;

            JettisonGhostInfo jetInfo;
            if (!state.jettisonInfos.TryGetValue(evt.partPersistentId, out jetInfo) ||
                jetInfo.jettisonTransforms == null ||
                jetInfo.jettisonTransforms.Count == 0)
                return false;

            bool applied = false;
            for (int i = 0; i < jetInfo.jettisonTransforms.Count; i++)
            {
                Transform jettisonTransform = jetInfo.jettisonTransforms[i];
                if (jettisonTransform == null) continue;
                jettisonTransform.gameObject.SetActive(!jettisoned);
                applied = true;
            }

            return applied;
        }

        #endregion

        #region Lights

        internal static LightPlaybackState GetOrCreateLightPlaybackState(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.lightPlaybackStates == null)
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();

            LightPlaybackState playbackState;
            if (!state.lightPlaybackStates.TryGetValue(partPersistentId, out playbackState))
            {
                playbackState = new LightPlaybackState();
                state.lightPlaybackStates[partPersistentId] = playbackState;
            }

            return playbackState;
        }

        internal static void ApplyLightPowerEvent(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.isOn = on;
            if (!on)
                SetLightState(state, partPersistentId, false);
            else if (!playbackState.blinkEnabled)
                SetLightState(state, partPersistentId, true);
        }

        internal static void ApplyLightBlinkModeEvent(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.blinkEnabled = enabled;
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        internal static void ApplyLightBlinkRateEvent(GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        internal static void UpdateBlinkingLights(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.lightPlaybackStates == null || state.lightPlaybackStates.Count == 0)
                return;

            foreach (var kv in state.lightPlaybackStates)
            {
                uint partPersistentId = kv.Key;
                LightPlaybackState playbackState = kv.Value;
                if (playbackState == null)
                    continue;

                bool shouldEnable = playbackState.isOn;
                if (shouldEnable && playbackState.blinkEnabled)
                {
                    float rateHz = playbackState.blinkRateHz > 0f ? playbackState.blinkRateHz : 1f;
                    double cycle = currentUT * rateHz;
                    double frac = cycle - Math.Floor(cycle);
                    shouldEnable = frac < 0.5;
                }

                SetLightState(state, partPersistentId, shouldEnable);
            }
        }

        internal static void SetLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            // Toggle Unity Light components (existing behavior)
            if (state.lightInfos != null)
            {
                LightGhostInfo info;
                if (state.lightInfos.TryGetValue(partPersistentId, out info))
                {
                    for (int i = 0; i < info.lights.Count; i++)
                    {
                        if (info.lights[i] != null)
                            info.lights[i].enabled = on;
                    }
                }
            }

            // Toggle ColorChanger emissive materials (Pattern A: cabin lights)
            ApplyColorChangerLightState(state, partPersistentId, on);
        }

        internal static void ApplyColorChangerLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.colorChangerInfos == null) return;

            List<ColorChangerGhostInfo> infos;
            if (!state.colorChangerInfos.TryGetValue(partPersistentId, out infos)) return;

            for (int c = 0; c < infos.Count; c++)
            {
                var ccInfo = infos[c];
                if (!ccInfo.isCabinLight) continue; // Only Pattern A responds to light events

                for (int i = 0; i < ccInfo.materials.Count; i++)
                {
                    if (ccInfo.materials[i].material != null)
                    {
                        ccInfo.materials[i].material.SetColor(
                            ccInfo.shaderProperty,
                            on ? ccInfo.materials[i].onColor : ccInfo.materials[i].offColor);
                    }
                }

                ParsekLog.VerboseRateLimited("Flight", $"cc-light-{partPersistentId}",
                    $"Part pid={partPersistentId}: applied color changer cabin light state={on}");
            }
        }

        /// <summary>
        /// Applies ablation char color to heat shield parts (Pattern B) based on reentry intensity.
        /// Called from DriveReentryLayers when reentry glow is active.
        /// </summary>
        internal static void ApplyColorChangerCharState(GhostPlaybackState state, float intensity)
        {
            if (state == null || state.colorChangerInfos == null) return;

            foreach (var kvp in state.colorChangerInfos)
            {
                var infos = kvp.Value;
                for (int c = 0; c < infos.Count; c++)
                {
                    var ccInfo = infos[c];
                    if (ccInfo.isCabinLight) continue; // Only Pattern B responds to reentry

                    // Char is permanent — only increase, never fade back
                    float fraction = Mathf.Clamp01(intensity);
                    if (fraction <= ccInfo.peakCharIntensity) continue;
                    ccInfo.peakCharIntensity = fraction;

                    for (int i = 0; i < ccInfo.materials.Count; i++)
                    {
                        if (ccInfo.materials[i].material != null)
                        {
                            Color lerped = Color.Lerp(
                                ccInfo.materials[i].offColor,
                                ccInfo.materials[i].onColor,
                                fraction);
                            ccInfo.materials[i].material.SetColor(ccInfo.shaderProperty, lerped);
                        }
                    }
                }
            }
        }

        #endregion

        #region Spawn-at-Recording-End Decision

        /// <summary>
        /// Determines whether spawn should be suppressed because the recording
        /// is an intermediate link in a ghost chain (not the chain tip), or
        /// because the chain is terminated (vessel destroyed/recovered).
        /// Returns (suppressed, reason). Called BEFORE ShouldSpawnAtRecordingEnd
        /// by the playback controller (Phase 6b).
        /// </summary>
        internal static (bool suppressed, string reason) ShouldSuppressSpawnForChain(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0)
                return (false, "");

            if (GhostChainWalker.IsIntermediateChainLink(chains, rec))
            {
                // Per-frame per-recording — rate-limit to avoid log spam
                ParsekLog.VerboseRateLimited("ChainWalker", $"chain-suppress-{rec.RecordingId}",
                    $"Intermediate spawn suppressed: rec={rec.RecordingId} vessel={rec.VesselName}");
                return (true, "intermediate ghost chain link");
            }

            var chain = GhostChainWalker.FindChainForVessel(chains, rec.VesselPersistentId);
            if (chain != null && chain.IsTerminated && chain.TipRecordingId == rec.RecordingId)
            {
                ParsekLog.VerboseRateLimited("ChainWalker",
                    "terminated-spawn-" + rec.VesselPersistentId,
                    string.Format(CultureInfo.InvariantCulture,
                        "Terminated chain spawn suppressed: rec={0} vessel={1} vesselPid={2}",
                        rec.RecordingId, rec.VesselName, rec.VesselPersistentId));
                return (true, "terminated ghost chain");
            }

            return (false, "");
        }

        /// <summary>
        /// Pure decision logic for whether a recording's vessel should be spawned
        /// at the end of its ghost playback (the "spawn-at-recording-end" feature).
        /// Extracted from ParsekFlight.UpdateTimelinePlayback for testability.
        ///
        /// Returns (needsSpawn, reason) where reason explains why spawn was suppressed.
        /// Empty reason means spawn is allowed.
        /// </summary>
        /// <param name="rec">The recording to evaluate.</param>
        /// <param name="isActiveChainMember">True if the recording belongs to the chain currently being built.</param>
        /// <param name="isChainLoopingOrDisabled">True if the recording's chain is looping or fully disabled.</param>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtRecordingEnd(
            Recording rec,
            bool isActiveChainMember,
            bool isChainLoopingOrDisabled)
        {
            // Base condition: must have a snapshot, not already spawned, not destroyed
            if (rec.VesselSnapshot == null)
            {
                return (false, "no vessel snapshot");
            }
            if (rec.VesselSpawned)
            {
                return (false, "already spawned (VesselSpawned=true)");
            }
            if (rec.VesselDestroyed)
            {
                return (false, "vessel destroyed");
            }

            // Branch > 0 recordings are ghost-only (undock continuations) — never spawn
            if (rec.ChainBranch > 0)
            {
                return (false, "branch > 0 (ghost-only)");
            }

            // Suppress spawning for recordings belonging to a chain currently being built
            if (isActiveChainMember)
            {
                return (false, "active chain being built");
            }

            // Looping recordings: first playthrough spawns the vessel (so it exists in the world),
            // subsequent loops are visual-only. The VesselSpawned/SpawnedVesselPersistentId checks
            // above handle this — after first spawn, VesselSpawned=true prevents re-spawning.
            // No blanket LoopPlayback suppression needed here.

            // Suppress spawn for looping or fully-disabled chains
            if (isChainLoopingOrDisabled)
            {
                return (false, "chain looping or fully disabled");
            }

            // Breakup-continuous check: the foreground recording continued past a breakup
            // (ProcessBreakupEvent sets ChildBranchPointId without creating a same-PID
            // continuation). If no child shares this vessel's PID, the recording IS the
            // effective leaf and should be spawnable. Only applies to non-debris recordings
            // with a spawnable terminal state (Landed/Splashed/Orbiting). (#224)
            bool hasSpawnableTerminal = rec.TerminalStateValue.HasValue &&
                (rec.TerminalStateValue.Value == TerminalState.Landed ||
                 rec.TerminalStateValue.Value == TerminalState.Splashed ||
                 rec.TerminalStateValue.Value == TerminalState.Orbiting);
            bool effectiveLeaf = rec.ChildBranchPointId != null
                && !rec.IsDebris
                && hasSpawnableTerminal
                && IsEffectiveLeafForVessel(rec);

            // Non-leaf tree recordings should never spawn — they branched into a
            // same-vessel continuation that carries the correct snapshot.
            if (rec.ChildBranchPointId != null && !effectiveLeaf)
            {
                return (false, "non-leaf tree recording");
            }

            // Safety net: even if ChildBranchPointId is null, check committed trees
            // for recordings that are parents of a branch point. Covers edge cases where
            // ChildBranchPointId was not set (e.g., serialization gaps). (#114)
            // Skip for effective-leaf recordings — the branch point exists but the recording
            // is still the leaf for its vessel.
            if (!effectiveLeaf && IsNonLeafInCommittedTree(rec))
            {
                return (false, "non-leaf in committed tree (safety net)");
            }

            // Debris recordings are visual-only (short TTL, no meaningful vessel to persist)
            if (rec.IsDebris)
            {
                return (false, "debris recording (visual-only)");
            }

            // Terminal states: destroyed/recovered/docked/boarded/suborbital should not spawn
            // SubOrbital includes FLYING and ESCAPING — vessel would materialize mid-air and crash (#45)
            if (rec.TerminalStateValue.HasValue)
            {
                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
                    || ts == TerminalState.Docked || ts == TerminalState.Boarded
                    || ts == TerminalState.SubOrbital)
                {
                    return (false, $"terminal state {ts}");
                }
            }

            // Snapshot situation check: if the snapshot's sit field is FLYING or SUB_ORBITAL,
            // KSP's on-rails aero check (101.3 kPa) immediately destroys spawned vessels.
            // This catches cases where TerminalState is null/Landed but the snapshot was
            // captured mid-flight. (#114)
            // Override: if terminal state is Landed/Splashed/Orbiting, the vessel DID reach
            // a safe state — the snapshot's sit field may be stale from recording start.
            // Orbiting: vessel captured during ascent (FLYING) but achieved orbit. The spawn
            // path corrects the snapshot situation before spawning. (#169, #EVA-spawn)
            bool terminalOverridesUnsafe = rec.TerminalStateValue == TerminalState.Landed ||
                rec.TerminalStateValue == TerminalState.Splashed ||
                rec.TerminalStateValue == TerminalState.Orbiting;
            if (!terminalOverridesUnsafe && IsSnapshotSituationUnsafe(rec.VesselSnapshot))
            {
                return (false, "snapshot situation unsafe (FLYING/SUB_ORBITAL)");
            }

            // PID dedup: if vessel was already spawned (PID recorded), never re-spawn.
            // On revert, SpawnedVesselPersistentId resets to 0 from quicksave so reverts still work.
            if (rec.SpawnedVesselPersistentId != 0)
            {
                return (false, $"already spawned (pid={rec.SpawnedVesselPersistentId})");
            }

            return (true, "");
        }

        /// <summary>
        /// KSC-specific spawn eligibility check. Simplified version of the Flight scene's
        /// spawn decision: at KSC there is no active chain being built, so isActiveChainMember
        /// is always false. Chain looping/disabled state is derived from RecordingStore.
        /// Returns (needsSpawn, reason) — same semantics as ShouldSpawnAtRecordingEnd.
        /// </summary>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtKscEnd(Recording rec)
        {
            // During rewind, Planetarium UT is still the pre-rewind future value until
            // the deferred coroutine fires. Block all spawns to prevent future vessels
            // from being re-created before the clock is wound back.
            if (RecordingStore.RewindUTAdjustmentPending)
                return (false, "rewind UT adjustment pending — Planetarium UT not yet corrected");

            return ShouldSpawnAtKscEnd(rec, Planetarium.GetUniversalTime());
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtKscEnd(Recording rec, double currentUT)
        {
            // Don't spawn vessels whose recording hasn't finished yet at the current UT (#rewind-persistence)
            if (currentUT < rec.EndUT)
                return (false, $"current UT {currentUT:F0} before recording end {rec.EndUT:F0}");

            // Orbiting/Docked vessels cannot survive pv.Load() in the Space Center scene —
            // KSP crashes them through terrain within frames. Defer to flight scene spawn
            // where SpawnAtPosition can place them correctly. (#171)
            if (rec.TerminalStateValue == TerminalState.Orbiting
                || rec.TerminalStateValue == TerminalState.Docked)
                return (false, $"orbital vessel deferred to flight scene (terminal={rec.TerminalStateValue})");

            // At KSC, no chain is being built → isActiveChainMember = false
            bool isChainLoopingOrDisabled = !string.IsNullOrEmpty(rec.ChainId) &&
                (RecordingStore.IsChainLooping(rec.ChainId) ||
                 RecordingStore.IsChainFullyDisabled(rec.ChainId));

            // Intermediate chain segments should not spawn — only the chain tip spawns.
            // In Flight, ShouldSuppressSpawnForChain handles this via runtime GhostChain
            // state, but at KSC there are no GhostChain objects. Use the committed data.
            if (RecordingStore.IsChainMidSegment(rec))
                return (false, "intermediate chain segment (not tip)");

            return ShouldSpawnAtRecordingEnd(rec, false, isChainLoopingOrDisabled);
        }

        /// <summary>
        /// Safety-net check: determines whether a recording is a non-leaf node in a
        /// committed tree by scanning the tree's branch points for parent references.
        /// This catches cases where ChildBranchPointId was not set on the recording
        /// (e.g., serialization gaps, edge-case commit paths) but the tree structure
        /// shows the recording has children. (#114)
        /// Static method, testable via RecordingStore.CommittedTrees setup.
        /// </summary>
        internal static bool IsNonLeafInCommittedTree(Recording rec)
        {
            if (!rec.IsTreeRecording || string.IsNullOrEmpty(rec.RecordingId))
                return false;

            var trees = RecordingStore.CommittedTrees;
            for (int t = 0; t < trees.Count; t++)
            {
                var tree = trees[t];
                if (tree.Id != rec.TreeId) continue;

                // Check if any branch point lists this recording as a parent
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    var bp = tree.BranchPoints[b];
                    if (bp.ParentRecordingIds != null && bp.ParentRecordingIds.Contains(rec.RecordingId))
                    {
                        ParsekLog.VerboseRateLimited("Spawner",
                            $"safety-net-{rec.RecordingId}",
                            string.Format(CultureInfo.InvariantCulture,
                                "IsNonLeafInCommittedTree: recording {0} is parent of branch point {1} " +
                                "in tree {2} (ChildBranchPointId was null — safety net triggered)",
                                rec.RecordingId, bp.Id, tree.Id), 30.0);
                        return true;
                    }
                }
                break; // Found the tree, no need to check others
            }
            return false;
        }

        /// <summary>
        /// Returns true when a recording with ChildBranchPointId is the effective leaf
        /// for its vessel — no child recording of that branch point shares the same
        /// VesselPersistentId. This happens for breakup-continuous foreground recordings
        /// where ProcessBreakupEvent sets ChildBranchPointId without creating a same-PID
        /// continuation (debris-only breakups on splashdown/landing). (#224)
        /// </summary>
        internal static bool IsEffectiveLeafForVessel(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChildBranchPointId) || !rec.IsTreeRecording)
                return false;

            var trees = RecordingStore.CommittedTrees;
            for (int t = 0; t < trees.Count; t++)
            {
                var tree = trees[t];
                if (tree.Id != rec.TreeId) continue;

                // Find the branch point
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    var bp = tree.BranchPoints[b];
                    if (bp.Id != rec.ChildBranchPointId) continue;

                    // Check if any child recording shares the same vessel PID
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        Recording childRec;
                        if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out childRec))
                        {
                            if (childRec.VesselPersistentId == rec.VesselPersistentId)
                                return false; // Same-PID continuation exists — NOT effective leaf
                        }
                    }

                    // No child shares this vessel PID — recording IS the effective leaf
                    ParsekLog.VerboseRateLimited("Spawner",
                        rec.RecordingId,
                        string.Format(CultureInfo.InvariantCulture,
                            "IsEffectiveLeafForVessel: recording {0} vessel={1} is effective leaf " +
                            "(breakup-continuous, no same-PID continuation child)",
                            rec.RecordingId, rec.VesselPersistentId));
                    return true;
                }
                break;
            }
            return false;
        }

        /// <summary>
        /// Checks whether a vessel snapshot's situation is unsafe for spawning.
        /// FLYING and SUB_ORBITAL vessels are immediately killed by KSP's on-rails
        /// atmospheric pressure check (101.3 kPa at sea level). (#114)
        /// Pure static method for testability.
        /// </summary>
        internal static bool IsSnapshotSituationUnsafe(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return false;

            string sit = vesselSnapshot.GetValue("sit");
            if (string.IsNullOrEmpty(sit)) return false;

            // KSP situation strings: LANDED, SPLASHED, PRELAUNCH, FLYING,
            // SUB_ORBITAL, ORBITING, ESCAPING, DOCKED
            return sit.Equals("FLYING", System.StringComparison.OrdinalIgnoreCase)
                || sit.Equals("SUB_ORBITAL", System.StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Zone-Based Rendering

        /// <summary>
         /// Determines the rendering actions to take when a ghost transitions between zones.
         /// Returns (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning).
         /// </summary>
        internal static (bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning)
            GetZoneRenderingPolicy(RenderingZone zone)
        {
            switch (zone)
            {
                case RenderingZone.Beyond:
                    return (true, true, true);
                case RenderingZone.Visual:
                    return (false, false, false); // part events apply in Visual zone
                case RenderingZone.Physics:
                default:
                    return (false, false, false);
            }
        }

        /// <summary>
        /// Returns true when the watched ghost should ignore distance-based LOD suppression
        /// and stay at full fidelity for the current frame.
        /// </summary>
        internal static bool ShouldForceWatchedFullFidelity(
            bool isWatchedGhost, double ghostDistanceMeters, float cutoffKm)
        {
            return isWatchedGhost && !ShouldExitWatchForCutoff(ghostDistanceMeters, cutoffKm);
        }

        /// <summary>
        /// Applies the watched-ghost full-fidelity override to a zone policy tuple.
        /// Distance-based LOD should not suppress a watched ghost that is still within cutoff.
        /// </summary>
        internal static (bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning)
            ApplyWatchedFullFidelityOverride(
                bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning,
                bool forceFullFidelity)
        {
            if (!forceFullFidelity)
                return (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning);

            return (false, false, false);
        }

        /// <summary>
        /// Applies the distance-based LOD tiers for unwatched ghosts on top of the base zone policy.
        /// The thresholds intentionally reuse the shared distance constants rather than adding
        /// another set of rendering knobs.
        /// </summary>
        internal static (bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning,
            bool shouldSuppressVisualFx, bool shouldReduceFidelity)
            ApplyDistanceLodPolicy(
                bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning,
                double ghostDistanceMeters, bool forceFullFidelity)
        {
            if (forceFullFidelity)
                return (false, false, false, false, false);

            if (shouldHideMesh)
                return (true, true, true, true, false);

            if (ghostDistanceMeters >= DistanceThresholds.GhostFlight.LoopSimplifiedMeters)
                return (true, true, true, true, false);

            if (ghostDistanceMeters >= DistanceThresholds.PhysicsBubbleMeters)
                return (false, true, false, true, true);

            return (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning, false, false);
        }

        /// <summary>
        /// Detects a zone transition and returns whether the zone changed.
        /// Pure decision method — does not mutate state or log.
        /// </summary>
        internal static bool DetectZoneTransition(
            RenderingZone previousZone, RenderingZone newZone,
            out string transitionDescription)
        {
            if (previousZone == newZone)
            {
                transitionDescription = null;
                return false;
            }

            // Describe the transition direction
            bool movingOutward = (int)newZone > (int)previousZone;
            transitionDescription = movingOutward ? "outward" : "inward";
            return true;
        }

        /// <summary>
        /// Determines whether a looped ghost should be spawned at the given distance,
        /// and whether it should use simplified rendering (no part events).
        /// Wraps RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance for consistency.
        /// </summary>
        internal static (bool shouldSpawn, bool simplified) EvaluateLoopedGhostSpawn(
            double distanceMeters)
        {
            return RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(distanceMeters);
        }

        #endregion

        #region Soft Cap Fidelity

        /// <summary>
        /// Reduces ghost visual fidelity by disabling a fraction of renderers.
        /// Keeps approximately 1 in 4 renderers to maintain recognizable shape
        /// while significantly reducing draw calls.
        /// </summary>
        internal static void ReduceGhostFidelity(GhostPlaybackState state)
        {
            if (state.ghost == null) return;
            var renderers = state.ghost.GetComponentsInChildren<Renderer>(true);
            state.fidelityDisabledRenderers = new List<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                // Keep every 4th renderer for a coarse silhouette
                if (i % 4 == 0) continue;
                if (!renderers[i].enabled) continue;
                renderers[i].enabled = false;
                state.fidelityDisabledRenderers.Add(renderers[i]);
            }
            state.fidelityReduced = true;
            ParsekLog.Verbose("Visual",
                $"ReduceFidelity: disabled {state.fidelityDisabledRenderers.Count}/{renderers.Length} renderers");
        }

        /// <summary>
        /// Restores ghost visual fidelity by re-enabling only the renderers that
        /// ReduceGhostFidelity disabled. Preserves part-event visibility state
        /// (e.g. decoupled/destroyed parts stay hidden).
        /// </summary>
        internal static void RestoreGhostFidelity(GhostPlaybackState state)
        {
            if (state.fidelityDisabledRenderers != null)
            {
                int restored = 0;
                for (int i = 0; i < state.fidelityDisabledRenderers.Count; i++)
                {
                    if (state.fidelityDisabledRenderers[i] != null)
                    {
                        state.fidelityDisabledRenderers[i].enabled = true;
                        restored++;
                    }
                }
                ParsekLog.Verbose("Visual", $"RestoreGhostFidelity: re-enabled {restored} renderers");
                state.fidelityDisabledRenderers = null;
            }
            state.fidelityReduced = false;
        }

        /// <summary>
        /// Restores any runtime suppression state that would prevent a watched ghost from
        /// rendering at full fidelity. Used when watch mode overrides distance-based LOD.
        /// </summary>
        internal static void RestoreWatchedFullFidelityState(GhostPlaybackState state)
        {
            if (state == null) return;

            if (state.fidelityReduced)
                RestoreGhostFidelity(state);
            state.distanceLodReduced = false;

            if (state.simplified)
            {
                if (state.ghost != null && !state.ghost.activeSelf)
                    state.ghost.SetActive(true);
                state.simplified = false;
            }
        }

        /// <summary>
        /// Applies or removes the distance-based reduced-fidelity renderer mode without
        /// interfering with the soft-cap ownership of the same visual primitive.
        /// </summary>
        internal static void ApplyDistanceLodFidelity(GhostPlaybackState state, bool shouldReduceFidelity)
        {
            if (state == null || state.ghost == null) return;

            if (shouldReduceFidelity)
            {
                if (!state.distanceLodReduced && !state.fidelityReduced)
                {
                    ReduceGhostFidelity(state);
                    state.distanceLodReduced = true;
                }
                return;
            }

            if (state.distanceLodReduced)
            {
                RestoreGhostFidelity(state);
                state.distanceLodReduced = false;
            }
        }

        /// <summary>
        /// Protected ghosts (currently watched) should ignore runtime suppression that
        /// would reduce or hide them.
        /// </summary>
        internal static bool IsProtectedGhost(int protectedIndex, int currentIndex)
        {
            return protectedIndex == currentIndex;
        }

        internal static bool IsProtectedGhost(
            int protectedIndex, long protectedLoopCycleIndex,
            int currentIndex, long currentLoopCycleIndex)
        {
            return protectedIndex == currentIndex
                && protectedLoopCycleIndex == currentLoopCycleIndex;
        }

        /// <summary>
        /// Returns true when the current recording should inherit watch-mode protection.
        /// This is broader than the exact watched ghost: breakup debris linked to the
        /// watched vessel's same-tree lineage should stay visible while that vessel is watched.
        /// </summary>
        internal static bool IsWatchProtectedRecording(
            IReadOnlyList<Recording> committed, int watchedRecordingIndex, int currentIndex)
        {
            return IsWatchProtectedRecording(
                committed, RecordingStore.CommittedTrees, watchedRecordingIndex, currentIndex);
        }

        internal static bool IsWatchProtectedRecording(
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> committedTrees,
            int watchedRecordingIndex, int currentIndex)
        {
            if (committed == null
                || watchedRecordingIndex < 0
                || currentIndex < 0
                || watchedRecordingIndex >= committed.Count
                || currentIndex >= committed.Count)
                return false;

            if (watchedRecordingIndex == currentIndex)
                return true;

            Recording watched = committed[watchedRecordingIndex];
            Recording current = committed[currentIndex];
            if (watched == null || current == null || !current.IsDebris)
                return false;

            if (string.IsNullOrEmpty(watched.TreeId)
                || string.IsNullOrEmpty(current.TreeId)
                || watched.TreeId != current.TreeId)
                return false;

            if (IsLoopSyncedDebrisOfWatchedLineage(committed, watched, current))
                return true;

            RecordingTree tree = FindTreeById(committedTrees, watched.TreeId);
            return IsDebrisDescendedFromWatchedLineage(watched, current, tree);
        }

        internal static double ComputeWatchLineageProtectionUntilUT(
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> committedTrees,
            int watchedRecordingIndex,
            double currentUT)
        {
            if (committed == null
                || watchedRecordingIndex < 0
                || watchedRecordingIndex >= committed.Count)
            {
                return double.NaN;
            }

            bool hasCurrentUT = !double.IsNaN(currentUT) && !double.IsInfinity(currentUT);
            double protectionUntilUT = double.NaN;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate == null || !candidate.IsDebris)
                    continue;
                if (hasCurrentUT && candidate.EndUT < currentUT)
                    continue;
                if (!IsWatchProtectedRecording(committed, committedTrees, watchedRecordingIndex, i))
                    continue;

                if (double.IsNaN(protectionUntilUT) || candidate.EndUT > protectionUntilUT)
                    protectionUntilUT = candidate.EndUT;
            }

            return protectionUntilUT;
        }

        private static bool IsLoopSyncedDebrisOfWatchedLineage(
            IReadOnlyList<Recording> committed, Recording watched, Recording current)
        {
            int parentIdx = current.LoopSyncParentIdx;
            if (committed == null || parentIdx < 0 || parentIdx >= committed.Count)
                return false;

            Recording parent = committed[parentIdx];
            if (parent == null || parent.TreeId != watched.TreeId)
                return false;

            if (parent.RecordingId == watched.RecordingId)
                return true;

            if (watched.VesselPersistentId == 0 || parent.VesselPersistentId == 0)
                return false;

            return parent.VesselPersistentId == watched.VesselPersistentId;
        }

        private static bool IsDebrisDescendedFromWatchedLineage(
            Recording watched, Recording current, RecordingTree tree)
        {
            if (watched == null || current == null || tree == null)
                return false;

            var pendingBranchPoints = new Queue<string>();
            var visitedBranchPoints = new HashSet<string>();
            var visitedRecordings = new HashSet<string>();

            if (!string.IsNullOrEmpty(current.ParentBranchPointId))
                pendingBranchPoints.Enqueue(current.ParentBranchPointId);

            while (pendingBranchPoints.Count > 0)
            {
                string branchPointId = pendingBranchPoints.Dequeue();
                if (string.IsNullOrEmpty(branchPointId) || !visitedBranchPoints.Add(branchPointId))
                    continue;

                BranchPoint branchPoint = FindBranchPointById(tree, branchPointId);
                if (branchPoint?.ParentRecordingIds == null)
                    continue;

                for (int i = 0; i < branchPoint.ParentRecordingIds.Count; i++)
                {
                    string parentRecordingId = branchPoint.ParentRecordingIds[i];
                    if (string.IsNullOrEmpty(parentRecordingId) || !visitedRecordings.Add(parentRecordingId))
                        continue;

                    Recording parent;
                    if (!tree.Recordings.TryGetValue(parentRecordingId, out parent) || parent == null)
                        continue;

                    if (parent.RecordingId == watched.RecordingId)
                        return true;

                    if (watched.VesselPersistentId != 0
                        && parent.VesselPersistentId != 0
                        && parent.VesselPersistentId == watched.VesselPersistentId)
                    {
                        return true;
                    }

                    if (!string.IsNullOrEmpty(parent.ParentBranchPointId))
                        pendingBranchPoints.Enqueue(parent.ParentBranchPointId);
                }
            }

            return false;
        }

        private static RecordingTree FindTreeById(
            IReadOnlyList<RecordingTree> committedTrees, string treeId)
        {
            if (committedTrees == null || string.IsNullOrEmpty(treeId))
                return null;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                RecordingTree tree = committedTrees[i];
                if (tree != null && tree.Id == treeId)
                    return tree;
            }

            return null;
        }

        private static BranchPoint FindBranchPointById(RecordingTree tree, string branchPointId)
        {
            if (tree?.BranchPoints == null || string.IsNullOrEmpty(branchPointId))
                return null;

            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint branchPoint = tree.BranchPoints[i];
                if (branchPoint != null && branchPoint.Id == branchPointId)
                    return branchPoint;
            }

            return null;
        }

        #endregion

        #region Watch Mode Decisions

        /// <summary>
        /// Determines whether the watch mode should auto-exit because the ghost
        /// exceeded the camera cutoff distance.
        /// </summary>
        internal static bool ShouldExitWatchForCutoff(double ghostDistanceMeters, float cutoffKm)
        {
            return ghostDistanceMeters >= cutoffKm * 1000.0;
        }

        /// <summary>
        /// Determines whether a ghost is within the camera cutoff distance
        /// (eligible for Watch button). Only checks distance against the
        /// user-configurable cutoff — zone is irrelevant because watch mode
        /// moves the camera to the ghost (T39).
        /// </summary>
        internal static bool IsWithinWatchRange(double distanceMeters, float cutoffKm)
        {
            return distanceMeters < cutoffKm * 1000.0;
        }

        /// <summary>
        /// Searches committed recordings for the next watch target after the current
        /// recording completes. Handles chain continuation (same chainId, next index)
        /// and tree branching (childBranchPointId → child with same vessel PID).
        /// </summary>
        /// <param name="currentRec">The recording that just completed.</param>
        /// <param name="committed">All committed recordings.</param>
        /// <param name="trees">All committed trees (for branch point lookup).</param>
        /// <param name="isGhostActive">Predicate: is there an active ghost at index j?</param>
        /// <returns>Index into committed, or -1 if no target found.</returns>
        internal static int FindNextWatchTarget(
            Recording currentRec,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            int depth = 0)
        {
            const int MaxRecursionDepth = 10;
            if (currentRec == null || committed == null || depth > MaxRecursionDepth) return -1;

            // Case 1: Chain continuation (same chainId, next chainIndex, branch 0)
            if (!string.IsNullOrEmpty(currentRec.ChainId) && currentRec.ChainIndex >= 0
                && currentRec.ChainBranch == 0)
            {
                int nextChainIndex = currentRec.ChainIndex + 1;
                for (int j = 0; j < committed.Count; j++)
                {
                    var candidate = committed[j];
                    if (candidate.ChainId == currentRec.ChainId
                        && candidate.ChainBranch == 0
                        && candidate.ChainIndex == nextChainIndex
                        && isGhostActive(j))
                    {
                        return j;
                    }
                }
            }

            // Case 2: Tree branching via ChildBranchPointId
            if (!string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                && currentRec.IsTreeRecording
                && trees != null)
            {
                BranchPoint bp = null;
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree.Id != currentRec.TreeId) continue;
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        if (tree.BranchPoints[b].Id == currentRec.ChildBranchPointId)
                        {
                            bp = tree.BranchPoints[b];
                            break;
                        }
                    }
                    break;
                }

                if (bp != null)
                {
                    int fallbackIdx = -1;
                    bool pidMatchFound = false;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        string childId = bp.ChildRecordingIds[c];
                        for (int j = 0; j < committed.Count; j++)
                        {
                            if (committed[j].RecordingId != childId) continue;

                            bool isPidMatch = committed[j].VesselPersistentId == currentRec.VesselPersistentId;

                            if (isGhostActive(j))
                            {
                                // Prefer child with same vessel PID (same vessel continues)
                                if (isPidMatch)
                                    return j;

                                if (fallbackIdx < 0)
                                    fallbackIdx = j;
                            }
                            else if (isPidMatch)
                            {
                                // #158: PID-matched continuation has no ghost (boundary seed
                                // with insufficient data). Recursively descend through its
                                // children to find a deeper target with an active ghost.
                                pidMatchFound = true;
                                int deeper = FindNextWatchTarget(
                                    committed[j], committed, trees, isGhostActive, depth + 1);
                                if (deeper >= 0)
                                    return deeper;
                            }
                        }
                    }
                    // #158: If we found the PID-matched continuation but it (and its
                    // descendants) have no ghost, don't fall through to debris — there's
                    // no good target. The watch hold timer will expire naturally.
                    if (pidMatchFound)
                        return -1;
                    if (fallbackIdx >= 0)
                        return fallbackIdx;
                }
            }

            return -1;
        }

        #endregion

        #region Auto Loop Range

        /// <summary>
        /// Returns true if the given environment is visually uninteresting for looping purposes.
        /// ExoBallistic (orbital coasting) and SurfaceStationary (sitting on ground) are trimmed
        /// from the loop range because they contain no visible action.
        /// </summary>
        internal static bool IsBoringEnvironment(SegmentEnvironment env)
        {
            return env == SegmentEnvironment.ExoBallistic || env == SegmentEnvironment.SurfaceStationary;
        }

        /// <summary>
        /// Computes the automatic loop range for a recording by trimming leading and trailing
        /// "boring" TrackSections (ExoBallistic, SurfaceStationary). Returns (NaN, NaN) if no
        /// trimming is possible (recording has fewer than 2 sections, all sections are interesting,
        /// or all sections are boring).
        /// </summary>
        internal static (double startUT, double endUT) ComputeAutoLoopRange(List<TrackSection> sections)
        {
            if (sections == null || sections.Count < 2)
                return (double.NaN, double.NaN);

            // Find first non-boring section
            int first = -1;
            for (int i = 0; i < sections.Count; i++)
            {
                if (!IsBoringEnvironment(sections[i].environment))
                {
                    first = i;
                    break;
                }
            }

            if (first < 0)
                return (double.NaN, double.NaN); // all boring — loop the whole thing

            // Find last non-boring section
            int last = first;
            for (int i = sections.Count - 1; i >= first; i--)
            {
                if (!IsBoringEnvironment(sections[i].environment))
                {
                    last = i;
                    break;
                }
            }

            // If nothing was trimmed, no range narrowing needed
            if (first == 0 && last == sections.Count - 1)
                return (double.NaN, double.NaN);

            return (sections[first].startUT, sections[last].endUT);
        }

        #endregion
    }
}
