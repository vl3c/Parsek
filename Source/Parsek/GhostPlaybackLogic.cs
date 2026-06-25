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
    internal static partial class GhostPlaybackLogic
    {
        // Tunable constants live in ParsekConfig.cs:
        //   WarpThresholds.FxSuppress / GhostHide — time-warp FX and ghost-mesh suppression levels
        //   LoopTiming.* — default/untouched loop periods, min cycle/loop duration, boundary epsilon
        //   WatchMode.ZoneGraceSeconds — wall-clock grace before zone-based watch exit
        //   WatchMode.PendingPostActivationGraceSeconds / MaxPendingHoldSeconds — pending-watch holds

        // Dedupe set for ResolveLoopInterval clamp warnings. Without this, a recording whose
        // LoopIntervalSeconds is below MinCycleDuration produces a log line every frame from
        // GhostPlaybackEngine + ParsekKSC (~3,600 lines/sec with ~20 offending recordings —
        // ~1.3M entries in a 6-minute session). Keyed on RecordingId (stable across loads)
        // with fallback to VesselName for transient fixtures lacking an id. Reset between
        // tests via ResetForTesting.
        private static readonly HashSet<string> loopIntervalClampWarned = new HashSet<string>(StringComparer.Ordinal);
        private static Func<FlagEvent, bool> flagExistsOverrideForTesting;
        private static Func<FlagEvent, bool> spawnFlagOverrideForTesting;
        private static readonly List<AudioSource> activeExplosionOneShotAudioSources =
            new List<AudioSource>();
        private static readonly List<AudioSource> pausedExplosionOneShotAudioSources =
            new List<AudioSource>();
        internal const string ExplosionOneShotAudioObjectName = "GhostExplosionAudio";
        internal static Func<GhostPlaybackState, bool> EnforceLoopedAudioPlaybackCapOverrideForTesting;
        internal delegate bool TryTriggerStockExplosionFxDelegate(Vector3 worldPosition, double power, out string failureReason);
        internal delegate ExplosionOneShotAudioCandidate ResolveExplosionOneShotAudioCandidateDelegate();
        internal delegate void PlayExplosionOneShotAudioDelegate(Vector3 worldPosition, ExplosionOneShotAudioCandidate candidate);

        internal static void HideGhostForRetire(GameObject ghost)
        {
            if (ReferenceEquals(ghost, null))
                return;

            try
            {
                if (ghost.activeSelf)
                    ghost.SetActive(false);
            }
            catch (System.Security.SecurityException)
            {
                // Headless xUnit can construct state that references UnityEngine
                // types without a Unity runtime. Runtime KSP hides normally.
            }
        }

        internal enum StockExplosionFxResult
        {
            StockQueued,
            StockFailedCustomVisualSpawned
        }

        internal struct ExplosionOneShotAudioCandidate
        {
            internal bool canPlay;
            internal string clipPath;
            internal AudioClip clip;
            internal float clipLengthSeconds;
            internal float volume;
            internal int priority;
            internal string failureReason;
        }

        #region Warp / Loop Policy

        /// <summary>
        /// Reset the per-recording clamp-warning dedupe set. Test-only — call from Dispose
        /// of any test touching ResolveLoopInterval so state doesn't leak across tests.
        /// </summary>
        internal static void ResetForTesting()
        {
            loopIntervalClampWarned.Clear();
            ResetFlagReplayOverridesForTesting();
            activeExplosionOneShotAudioSources.Clear();
            pausedExplosionOneShotAudioSources.Clear();
            EnforceLoopedAudioPlaybackCapOverrideForTesting = null;
        }

        /// <summary>
        /// Test-only override for flag dedup checks. Pass null to restore the live KSP path.
        /// </summary>
        internal static void SetFlagExistsOverrideForTesting(Func<FlagEvent, bool> checker)
        {
            flagExistsOverrideForTesting = checker;
        }

        /// <summary>
        /// Test-only override for flag spawns. Return true to simulate a successful spawn.
        /// Pass null to restore the live KSP path.
        /// </summary>
        internal static void SetSpawnFlagOverrideForTesting(Func<FlagEvent, bool> spawner)
        {
            spawnFlagOverrideForTesting = spawner;
        }

        /// <summary>
        /// Clears all test-only flag replay overrides.
        /// </summary>
        internal static void ResetFlagReplayOverridesForTesting()
        {
            flagExistsOverrideForTesting = null;
            spawnFlagOverrideForTesting = null;
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

        // Resolves the live vessel's launch Guid for a pid (null = unknown). Paired with
        // vesselExistsOverride so the guid-aware RealVesselExistsForRecording is unit-testable (R4):
        // tests that set only the existence override get pid-only fallback (unchanged behavior).
        private static Func<uint, string> vesselGuidResolverOverride;

        internal static void SetVesselGuidResolverOverrideForTesting(Func<uint, string> resolver)
        {
            vesselGuidResolverOverride = resolver;
        }

        internal static void ResetVesselGuidResolverOverrideForTesting()
        {
            vesselGuidResolverOverride = null;
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
        /// Guid-aware existence check for tracking-station / spawn dedup (#976-class): true only when
        /// a real vessel with the recording's pid exists AND is the SAME launch (its Vessel.id matches
        /// the recording's RecordedVesselGuid). A relaunch of the same craft reuses the craft-baked
        /// pid but carries a different launch guid, so it no longer makes a prior recording look
        /// "already materialized" (which would suppress its ghost / corrupt its spawn state). Falls
        /// back to today's pid-only behavior when the launch guid is unknown on either side.
        /// </summary>
        internal static bool RealVesselExistsForRecording(Recording rec)
        {
            if (rec == null || rec.VesselPersistentId == 0)
                return false;
            if (!RealVesselExists(rec.VesselPersistentId))
                return false;
            string liveGuid = ResolveLiveVesselGuid(rec.VesselPersistentId);
            return VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, rec.VesselPersistentId, liveGuid);
        }

        // Step-2 double-suppression (Logistics route live-anchor bind): a loop member's
        // OWN in-bubble/map ghost is hidden as a live-anchor duplicate ONLY while its
        // launch-matched live vessel is loaded AND it was the LIVE docking anchor of an
        // in-window relative member during this-or-the-previous frame (the Step-1
        // live-bind event), NOT for the whole loop. The earlier whole-loop existence
        // check over-suppressed: RealVesselExistsForRecording is true for EVERY parked
        // route craft loaded in the scene, so a fresh new-mission launch watching the
        // looped route hid ALL its delivery meshes (the inbound member is the resolver
        // FOCUS, never the resolved anchor, so it is never in the bind set and must
        // never be suppressed). The static anchorRecordingId graph cross-cuts vessel
        // role (Depot recordings can be relative members, Kerbal X recordings can be
        // pure anchors), so it does not discriminate "is the station the player is
        // docking against"; the per-frame live-bind event does, scoped to the actual
        // docking overlap. The bind set lives on RelativeAnchorResolver, captured at the
        // resolver (not re-derived via a UT mapping, the documented drift dead-end).
        internal static bool IsLiveAnchorDoubleSuppressed(Recording rec, bool loopingLike)
        {
            return loopingLike
                && rec != null
                && RealVesselExistsForRecording(rec)
                && !string.IsNullOrEmpty(rec.RecordingId)
                && RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(rec.RecordingId);
        }

        // Resolves the launch Guid of the live vessel with the given pid (null = none / unknown).
        private static string ResolveLiveVesselGuid(uint vesselPersistentId)
        {
            if (vesselGuidResolverOverride != null)
                return vesselGuidResolverOverride(vesselPersistentId);
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels == null) return null;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null
                        && v.persistentId == vesselPersistentId
                        && !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    {
                        return v.id != System.Guid.Empty ? v.id.ToString("N") : null;
                    }
                }
            }
            catch (System.Exception)
            {
                // Headless / no-FlightGlobals: treat the guid as unknown (pid-only fallback).
            }
            return null;
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
            if (IsVesselRecordedByTree(treeId, vesselPersistentId))
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
        /// Checks whether the given vessel PID appears anywhere in the same tree's recordings.
        /// This is tree-local recorded-history membership, not a global ownership claim.
        /// Uses the cached RecordedVesselPids set on RecordingTree for O(1) lookup.
        /// </summary>
        internal static bool IsVesselRecordedByTree(string treeId, uint vesselPersistentId)
        {
            if (string.IsNullOrEmpty(treeId) || vesselPersistentId == 0) return false;

            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == treeId)
                    return trees[i].RecordedVesselPids.Contains(vesselPersistentId);
            }
            return false;
        }

        /// <summary>
        /// Resets the injectable vessel-exists override. Call from test Dispose.
        /// </summary>
        internal static void ResetVesselExistsOverride()
        {
            vesselExistsOverride = null;
            vesselGuidResolverOverride = null;
        }

        /// <summary>
        /// Invalidate the vessel PID cache. Call once per frame before any
        /// RealVesselExists calls (e.g., first line of UpdateTimelinePlaybackViaEngine).
        /// </summary>
        internal static void InvalidateVesselCache()
        {
            vesselCacheValid = false;
        }

        internal static bool ShouldSkipTimelinePlaybackForPendingReFlyInvoke(bool pendingReFlyInvoke)
        {
            return pendingReFlyInvoke;
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

            PopulateEngineInfos(state, result);

            PopulateDeployableInfos(state, result);
            PopulateHeatInfos(state, result);
            PopulateLightInfos(state, result);

            if (result.fairingInfos != null)
                state.fairingInfos = BuildDictByPid(result.fairingInfos, f => f.partPersistentId);

            PopulateRcsInfos(state, result);
            PopulateRoboticInfos(state, result);

            if (result.colorChangerInfos != null)
                state.colorChangerInfos = GhostVisualBuilder.GroupColorChangersByPartId(result.colorChangerInfos);

            state.compoundPartInfos = result.compoundPartInfos;

            PopulateAudioInfos(state, result);

            if (ShouldEvaluateOrphanEnginePlayback(state, traj))
                AutoStartOrphanEnginePlayback(state, traj);
        }

        internal static bool ShouldEvaluateOrphanEnginePlayback(
            GhostPlaybackState state,
            IPlaybackTrajectory traj)
        {
            if (traj == null) return false;

            return (state.audioInfos != null && state.audioInfos.Count > 0)
                || (state.engineInfos != null && state.engineInfos.Count > 0);
        }

        private static void PopulateDeployableInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.deployableInfos == null) return;

            state.deployableInfos = BuildDictByPid(result.deployableInfos, d => d.partPersistentId);

            // Initialize every deployable to its stowed pose at spawn — without this,
            // parts whose prefab defaults to the deployed pose (e.g. stock retractable
            // ladders) render extended in the ghost even when the recorded vessel had
            // them stowed. Already-deployed parts get a DeployableExtended seed event
            // at startUT (PartStateSeeder), so ApplyFrameVisuals snaps them back to
            // deployed when the playback loop reaches the recording start. Mirrors the
            // loop-rewind baseline in ReapplySpawnTimeModuleBaselinesForLoopCycle.
            int stowedCount = 0;
            foreach (var kvp in state.deployableInfos)
            {
                var stowedEvt = new PartEvent { partPersistentId = kvp.Key };
                if (ApplyDeployableState(state, stowedEvt, deployed: false))
                    stowedCount++;
            }

            if (state.deployableInfos.Count > 0)
                ParsekLog.Verbose("GhostVisual",
                    $"Spawn baseline: stowed {stowedCount}/{state.deployableInfos.Count} deployable(s) " +
                    $"(vessel='{state.vesselName ?? "unknown"}')");
        }

        private static void PopulateEngineInfos(GhostPlaybackState state, GhostBuildResult result)
        {
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
        }

        private static void PopulateHeatInfos(GhostPlaybackState state, GhostBuildResult result)
        {
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
        }

        private static void PopulateLightInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.lightInfos != null)
            {
                state.lightInfos = BuildDictByPid(result.lightInfos, l => l.partPersistentId);
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();
                for (int i = 0; i < result.lightInfos.Count; i++)
                    state.lightPlaybackStates[result.lightInfos[i].partPersistentId] = new LightPlaybackState();
            }
        }

        private static void PopulateRcsInfos(GhostPlaybackState state, GhostBuildResult result)
        {
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
        }

        private static void PopulateRoboticInfos(GhostPlaybackState state, GhostBuildResult result)
        {
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
        }

        private static void PopulateAudioInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.audioInfos != null)
            {
                state.audioInfos = new Dictionary<ulong, AudioGhostInfo>();
                for (int i = 0; i < result.audioInfos.Count; i++)
                {
                    result.audioInfos[i].selectionOrder = i;
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.audioInfos[i].partPersistentId, result.audioInfos[i].moduleIndex);
                    state.audioInfos[key] = result.audioInfos[i];
                }
            }
        }

        private static void AutoStartOrphanEnginePlayback(
            GhostPlaybackState state,
            IPlaybackTrajectory traj)
        {
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
                        }
                        ParsekLog.Verbose("GhostAudio",
                            $"Auto-started audio for orphan engine key={kvp.Key} " +
                            $"(no engine events in recording — likely debris booster)");
                    }

                    EnforceLoopedAudioPlaybackCap(state);
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

        internal static bool TryGetEarlyDestroyedDebrisExplosionUT(
            IPlaybackTrajectory traj, out double explosionUT)
        {
            explosionUT = double.NaN;

            if (traj == null || !traj.IsDebris || traj.TerminalStateValue != TerminalState.Destroyed)
                return false;

            if (traj.PartEvents == null || traj.PartEvents.Count == 0)
                return false;

            double latestEligibleUT = traj.EndUT - LoopTiming.MinEarlyDebrisExplosionLeadSeconds;
            if (latestEligibleUT <= traj.StartUT)
                return false;

            double earliestEligibleUT = double.NaN;
            for (int i = 0; i < traj.PartEvents.Count; i++)
            {
                var evt = traj.PartEvents[i];
                if (evt.eventType != PartEventType.Destroyed)
                    continue;
                if (evt.ut < traj.StartUT)
                    continue;
                if (evt.ut > latestEligibleUT)
                    continue;

                if (double.IsNaN(earliestEligibleUT) || evt.ut < earliestEligibleUT)
                    earliestEligibleUT = evt.ut;
            }

            if (double.IsNaN(earliestEligibleUT))
                return false;

            explosionUT = earliestEligibleUT;
            return true;
        }

        internal static bool ShouldTriggerExplosionAtPlaybackUT(
            IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj == null || traj.TerminalStateValue != TerminalState.Destroyed)
                return false;

            if (double.IsNaN(playbackUT) || double.IsInfinity(playbackUT))
                return false;

            if (TryGetEarlyDestroyedDebrisExplosionUT(traj, out double earlyExplosionUT))
                return playbackUT >= earlyExplosionUT;

            return playbackUT >= traj.EndUT;
        }

        internal static void HideAllGhostParts(GhostPlaybackState state)
        {
            if (state.ghost == null) return;
            MuteAllAudio(state);
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

        /// <summary>
        /// #406 follow-up: re-activates every ghost_part_* GameObject under the
        /// ghost's visuals container so the next loop cycle plays back from the
        /// snapshot baseline. Production ghosts created by
        /// <c>BuildTimelineGhostFromSnapshot</c> parent every ghost_part_ under
        /// a dedicated `ghost_visuals` container (see
        /// <c>GhostVisualBuilder.EnsureGhostVisualsRoot</c>); part visibility is
        /// toggled via <c>SetGhostPartActive</c> which looks up parts inside
        /// that container. Walking `state.ghost.transform` directly would miss
        /// every real part (it would only see the container + cameraPivot +
        /// horizonProxy). Call site: loop-cycle-reuse path in
        /// <c>GhostPlaybackEngine</c>. Returns the number of parts re-activated
        /// — used by the Verbose log line at the reuse call site.
        /// </summary>
        internal static int ReactivateGhostPartHierarchyForLoopRewind(GhostPlaybackState state)
        {
            if (state == null || state.ghost == null) return 0;
            // Reuse the same lookup SetGhostPartActive uses so "part hierarchy"
            // here means the same thing as at the playback event sites.
            var partContainer = GhostVisualBuilder.GetGhostPartContainer(state.ghost.transform);
            if (partContainer == null) return 0;
            int reactivated = 0;
            for (int c = 0; c < partContainer.childCount; c++)
            {
                var child = partContainer.GetChild(c);
                if (!child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(true);
                    reactivated++;
                }
            }
            return reactivated;
        }

        /// <summary>
        /// #406 follow-up: per-cycle state reset for the loop-cycle ghost reuse
        /// path. Pure-logic helper (no Unity API calls) — safe to invoke from
        /// xUnit tests. Mirrors the spawn-time baseline for iterators, per-cycle
        /// flags, AND the mutable playback fields stored inside the preserved
        /// module dictionaries (EngineGhostInfo.currentPower etc.), while
        /// PRESERVING the snapshot-derived dictionary references, the ghost
        /// GameObject, the reentry FX info, and the reentry-FX pending-build
        /// flag (#450 B3 — clearing the flag would re-pay the ~7 ms build
        /// every cycle). See <c>docs/dev/plan-406-ghost-reuse-loop-cycles.md</c>
        /// for the field-by-field preservation table.
        ///
        /// Unity-touching cleanup (RCS emission restore, fake canopy GameObject
        /// destroy) is deliberately NOT invoked here — the engine's
        /// <c>ReusePrimaryGhostAcrossCycle</c> orchestrator calls those helpers
        /// separately immediately after this one. Pulling them into this method
        /// would trip <c>System.Security.SecurityException</c> in xUnit runs
        /// because the JIT loads Unity type references at method-verify time,
        /// even if the early-returns prevent their execution.
        /// </summary>
        internal static void ResetForLoopCycle(GhostPlaybackState state, long newCycleIndex)
        {
            if (state == null) return;

            // Playback iterators rewind to cycle start.
            state.playbackIndex = 0;
            state.partEventIndex = 0;
            state.flagEventIndex = 0;
            state.appearanceCount = 0;
            state.hadVisibleRenderersLastFrame = false;
            state.loopCycleIndex = newCycleIndex;

            // Per-cycle flags reset to spawn baseline — the new cycle re-decides.
            state.explosionFired = false;
            state.pauseHidden = false;
            state.rcsSuppressed = false;
            state.visualFxSuppressed = false;

            // Audio state machine: next frame's atmosphere/mute pipeline
            // re-decides. Atmosphere factor resets to 1 (matches spawn).
            state.audioMuted = false;
            state.atmosphereFactor = 1f;

            // Per-part runtime state accrued from events (light blink state,
            // logical-pid presence set). Events on the new cycle repopulate
            // these; `logicalPartIds` is restored by the reuse orchestrator
            // via BuildSnapshotPartIdSet because that helper requires the
            // snapshot ConfigNode which this pure static doesn't have access
            // to. `fakeCanopies` entries must be destroyed — the engine
            // orchestrator calls DestroyAllFakeCanopies() separately because
            // it invokes Unity's Object.Destroy.
            state.lightPlaybackStates?.Clear();

            // Mutable playback fields INSIDE the preserved module dictionaries:
            // a fresh spawn constructs new info objects with these fields at
            // their default (zero). Reuse must match that baseline or the
            // first-visible frame can reapply stale engine throttle / robotic
            // servo / color-charge / reentry-intensity state from the previous
            // cycle before the new cycle's events have fired. Nullable-safe
            // loops — any of these dictionaries can legitimately be null
            // (trajectory had no engines, no RCS, no robotic parts, etc.).
            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                    if (info != null) info.currentPower = 0f;
            }
            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                    if (info != null) info.currentPower = 0f;
            }
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                    if (info != null) info.currentPower = 0f;
            }
            if (state.roboticInfos != null)
            {
                foreach (var info in state.roboticInfos.Values)
                {
                    if (info == null) continue;
                    info.currentValue = 0f;
                    info.active = false;
                    info.lastUpdateUT = double.NaN;
                }
            }
            if (state.colorChangerInfos != null)
            {
                foreach (var list in state.colorChangerInfos.Values)
                {
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] != null)
                            list[i].peakCharIntensity = 0f;
                    }
                }
            }
            if (state.reentryFxInfo != null)
                state.reentryFxInfo.lastIntensity = 0f;

            // Reset fresh-spawn visibility deferral so the first positioned
            // frame activates the ghost the same way a fresh spawn would.
            state.deferVisibilityUntilPlaybackSync = true;

            // Deliberately NOT reset: `fidelityReduced`, `distanceLodReduced`,
            // `fidelityDisabledRenderers`, `simplified`. These track
            // distance-LOD state that is re-evaluated every frame by
            // `ApplyDistanceLodFidelity` + `ApplyZonePolicy`. If a
            // prior-cycle-decoupled part that was on the disabled-renderers
            // list is now reactivated by ReactivateGhostPartHierarchyForLoopRewind,
            // the list holds a reference to a renderer that is briefly
            // visible again — but the next ApplyDistanceLodFidelity pass
            // re-walks active renderers and re-disables anything still out
            // of range, so the list self-corrects within one frame. Pre-#406
            // behaviour discarded the list with DestroyGhost; the reuse
            // path preserves it because rebuilding would churn the LOD
            // state machine without gameplay benefit.
        }

        /// <summary>
        /// #406 follow-up: re-apply spawn-time Unity-touching module baselines
        /// after a loop-cycle reuse. Fresh spawn does three things that
        /// <see cref="ResetForLoopCycle"/> cannot (because they touch Unity):
        ///  1. Heat parts: reset every <c>HeatGhostInfo</c> to
        ///     <see cref="HeatLevel.Cold"/> via <c>ApplyHeatState</c>, so a
        ///     cycle whose prior pass went hot does not carry that emission
        ///     into the new cycle's pre-reentry frames.
        ///  2. Deployable parts: reset every transform in
        ///     <c>DeployableGhostInfo</c> to its stowed pose, so a solar
        ///     panel that deployed mid-cycle is folded again before the new
        ///     cycle's events re-deploy it on schedule.
        ///  3. Jettison panels: reactivate jettisoned panels (SetActive true)
        ///     so the new cycle's jettison events can re-fire.
        ///  4. Orphan engine/audio auto-start: for recordings with ZERO
        ///     engine events (typical of pure debris boosters that were
        ///     running at breakup), re-fire the fresh-spawn auto-start logic
        ///     so plume/audio come back on the second cycle onward. Without
        ///     this, the first cycle has orphan FX but the second cycle
        ///     loses them silently.
        /// Must be called from the engine orchestrator AFTER
        /// <see cref="ResetForLoopCycle"/> and
        /// <see cref="ReactivateGhostPartHierarchyForLoopRewind"/> and BEFORE
        /// the next <c>PrimeLoadedGhostForPlaybackUT</c> / <c>ApplyFrameVisuals</c>
        /// call. All three branches no-op on null inputs.
        /// </summary>
        internal static void ReapplySpawnTimeModuleBaselinesForLoopCycle(
            GhostPlaybackState state, IPlaybackTrajectory traj)
        {
            if (state == null || state.ghost == null) return;

            // 1. Heat: reset every part to cold.
            if (state.heatInfos != null)
            {
                foreach (var kvp in state.heatInfos)
                {
                    var coldEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyHeatState(state, coldEvt, HeatLevel.Cold);
                }
            }

            // 2. Deployables: re-stow every panel. Events during the new
            //    cycle re-deploy on their original UT.
            if (state.deployableInfos != null)
            {
                foreach (var kvp in state.deployableInfos)
                {
                    var stowedEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyDeployableState(state, stowedEvt, deployed: false);
                }
            }

            // 3. Jettison panels: re-attach every panel. Jettison events
            //    during the new cycle hide them again on their original UT.
            if (state.jettisonInfos != null)
            {
                foreach (var kvp in state.jettisonInfos)
                {
                    var attachedEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyJettisonPanelState(state, attachedEvt, jettisoned: false);
                }
            }

            // 3b. Parachutes: re-stow canopies (localScale = Vector3.zero, the
            //     spawn-time baseline from TryBuildParachuteInfo at
            //     GhostVisualBuilder.cs line ~4539) and re-activate caps so
            //     packs that cut / destroyed / deployed in the prior cycle
            //     are back to their pre-launch pose. Destroy any fake canopy
            //     left over from a prior ParachuteDeployed event so the new
            //     cycle's event can re-create it fresh.
            if (state.parachuteInfos != null)
            {
                foreach (var kvp in state.parachuteInfos)
                {
                    ParachuteGhostInfo info = kvp.Value;
                    if (info == null) continue;
                    if (info.canopyTransform != null)
                        info.canopyTransform.localScale = UnityEngine.Vector3.zero;
                    if (info.capTransform != null)
                        info.capTransform.gameObject.SetActive(true);
                }
            }
            DestroyAllFakeCanopies(state);

            // 3c. Fairings: re-activate fairing mesh so a FairingJettisoned
            //     event from the prior cycle is undone. Events during the
            //     new cycle re-hide on their original UT.
            if (state.fairingInfos != null)
            {
                foreach (var kvp in state.fairingInfos)
                {
                    FairingGhostInfo info = kvp.Value;
                    if (info == null || info.fairingMeshObject == null) continue;
                    info.fairingMeshObject.SetActive(true);
                }
            }

            // 3d. Lights: force every Light component to disabled so a lamp
            //     that was ON at cycle-end does not stay on during the new
            //     cycle's pre-event window. ResetForLoopCycle already cleared
            //     lightPlaybackStates, so UpdateBlinkingLights would not
            //     iterate until events repopulate the dict — without this
            //     explicit SetLightState(false), the Unity Light.enabled flag
            //     stays at its prior value. The fresh-spawn path converges on
            //     "all off" only after UpdateBlinkingLights runs once with
            //     lightPlaybackStates populated; this short-circuits the
            //     transient window where lamps appear stuck on.
            if (state.lightInfos != null)
            {
                foreach (var kvp in state.lightInfos)
                    SetLightState(state, kvp.Key, false);
            }

            // 4. Orphan engine/audio auto-start: duplicates the zero-engine-event
            //    branch of TryPopulateGhostVisuals so a debris-booster recording
            //    with no engine events keeps its plume + audio across loop cycles.
            bool hasEngineOrAudioInfos =
                (state.audioInfos != null && state.audioInfos.Count > 0)
                || (state.engineInfos != null && state.engineInfos.Count > 0);
            if (!hasEngineOrAudioInfos || traj == null || traj.PartEvents == null) return;
            HashSet<ulong> engineKeysWithEvents = BuildEngineEventKeySet(traj.PartEvents);
            if (engineKeysWithEvents.Count != 0) return;

            if (state.audioInfos != null)
            {
                foreach (var kvp in state.audioInfos)
                {
                    kvp.Value.currentPower = 1f;
                    if (kvp.Value.audioSource != null)
                    {
                        kvp.Value.audioSource.volume = 0f;
                        kvp.Value.audioSource.loop = true;
                    }
                }

                EnforceLoopedAudioPlaybackCap(state);
            }

            if (state.engineInfos != null)
            {
                foreach (var kvp in state.engineInfos)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(kvp.Key, out pid, out midx);
                    var syntheticEvt = new PartEvent { partPersistentId = pid, moduleIndex = midx };
                    SetEngineEmission(state, syntheticEvt, 1f);
                }
            }
        }

        internal static bool RefreshCompoundPartVisibility(GhostPlaybackState state)
        {
            if (state == null || state.ghost == null || state.compoundPartInfos == null
                || state.compoundPartInfos.Count == 0)
                return false;

            bool changed = false;
            var logicalPartIds = state.logicalPartIds;
            for (int i = 0; i < state.compoundPartInfos.Count; i++)
            {
                CompoundPartGhostInfo info = state.compoundPartInfos[i];
                if (info == null || info.partTransform == null || info.targetPersistentId == 0)
                    continue;

                GameObject partObject = info.partTransform.gameObject;
                if (partObject == null)
                    continue;

                if (logicalPartIds != null && logicalPartIds.Count > 0
                    && !logicalPartIds.Contains(info.partPersistentId))
                    continue;

                if (!partObject.activeSelf)
                    continue;

                Transform targetTransform = GhostVisualBuilder.FindGhostPartTransform(
                    state.ghost, info.targetPersistentId);
                bool hidePart = ShouldHideCompoundPart(
                    info.targetPersistentId,
                    logicalPartIds,
                    targetTransform != null,
                    targetTransform != null && targetTransform.gameObject.activeSelf);

                if (!hidePart)
                    continue;

                partObject.SetActive(false);
                changed = true;
            }

            return changed;
        }

        internal static bool ShouldHideCompoundPart(
            uint targetPersistentId,
            HashSet<uint> logicalPartIds,
            bool targetVisualExists,
            bool targetVisualActive)
        {
            if (targetPersistentId == 0)
                return false;

            if (logicalPartIds != null && logicalPartIds.Count > 0
                && !logicalPartIds.Contains(targetPersistentId))
                return true;

            return targetVisualExists && !targetVisualActive;
        }

        internal static bool ShouldRestoreCompoundPart(
            uint sourcePersistentId,
            uint targetPersistentId,
            HashSet<uint> logicalPartIds,
            bool targetVisualExists,
            bool targetVisualActive)
        {
            if (logicalPartIds != null && logicalPartIds.Count > 0
                && !logicalPartIds.Contains(sourcePersistentId))
                return false;

            return !ShouldHideCompoundPart(
                targetPersistentId,
                logicalPartIds,
                targetVisualExists,
                targetVisualActive);
        }

        internal static void RemovePartSubtreeFromLogicalPresence(
            HashSet<uint> logicalPartIds,
            uint rootPid,
            Dictionary<uint, List<uint>> tree)
        {
            if (logicalPartIds == null || logicalPartIds.Count == 0)
                return;

            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                logicalPartIds.Remove(pid);

                if (tree == null)
                    continue;

                List<uint> children;
                if (!tree.TryGetValue(pid, out children))
                    continue;

                for (int i = 0; i < children.Count; i++)
                    stack.Push(children[i]);
            }
        }

        internal static bool RestoreCompoundPartsForPlacedTargets(
            GhostPlaybackState state,
            HashSet<uint> placedTargetPartIds)
        {
            if (state == null || state.ghost == null || state.compoundPartInfos == null
                || state.compoundPartInfos.Count == 0 || placedTargetPartIds == null
                || placedTargetPartIds.Count == 0)
                return false;

            bool changed = false;
            var logicalPartIds = state.logicalPartIds;
            for (int i = 0; i < state.compoundPartInfos.Count; i++)
            {
                CompoundPartGhostInfo info = state.compoundPartInfos[i];
                if (info == null || info.partTransform == null
                    || !placedTargetPartIds.Contains(info.targetPersistentId))
                    continue;

                GameObject partObject = info.partTransform.gameObject;
                if (partObject == null || partObject.activeSelf)
                    continue;

                Transform targetTransform = GhostVisualBuilder.FindGhostPartTransform(
                    state.ghost, info.targetPersistentId);
                if (!ShouldRestoreCompoundPart(
                    info.partPersistentId,
                    info.targetPersistentId,
                    logicalPartIds,
                    targetTransform != null,
                    targetTransform != null && targetTransform.gameObject.activeSelf))
                    continue;

                partObject.SetActive(true);
                changed = true;
            }

            return changed;
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
            var logicalPartIds = state.logicalPartIds;
            bool visibilityChanged = false;
            bool needsReentryMeshRebuild = false;
            bool audioPowerTouched = false;
            HashSet<uint> placedTargetPartIds = null;

            while (evtIdx < rec.PartEvents.Count && rec.PartEvents[evtIdx].ut <= currentUT)
            {
                var evt = rec.PartEvents[evtIdx];
                switch (evt.eventType)
                {
                    case PartEventType.Decoupled:
                        ApplyDecoupledPartEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            tree,
                            evt,
                            allowTransientEffects,
                            ref visibilityChanged,
                            ref needsReentryMeshRebuild);
                        break;
                    case PartEventType.Destroyed:
                        ApplyDestroyedPartEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            evt,
                            allowTransientEffects,
                            ref visibilityChanged,
                            ref needsReentryMeshRebuild);
                        break;
                    case PartEventType.ParachuteCut:
                        ApplyParachuteCutEvent(state, evt.partPersistentId);
                        break;
                    case PartEventType.ShroudJettisoned:
                        ApplyJettisonPanelState(state, evt, jettisoned: true);
                        break;
                    case PartEventType.ParachuteDestroyed:
                        ApplyParachuteDestroyedEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref visibilityChanged);
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
                        if (SetEngineAudio(
                                state,
                                evt,
                                System.Math.Max(evt.value, 0.01f),
                                enforcePlaybackCap: false))
                            audioPowerTouched = true;
                        break;
                    case PartEventType.EngineShutdown:
                        SetEngineEmission(state, evt, 0f);
                        if (SetEngineAudio(state, evt, 0f, enforcePlaybackCap: false))
                            audioPowerTouched = true;
                        break;
                    case PartEventType.EngineThrottle:
                        SetEngineEmission(state, evt, evt.value);
                        if (SetEngineAudio(state, evt, evt.value, enforcePlaybackCap: false))
                            audioPowerTouched = true;
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
                        ApplyInventoryPartPlacedEvent(
                            state,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref placedTargetPartIds,
                            ref visibilityChanged);
                        break;
                    case PartEventType.InventoryPartRemoved:
                        ApplyInventoryPartRemovedEvent(
                            state,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref visibilityChanged);
                        break;
                }
                evtIdx++;
            }

            int appliedCount = evtIdx - state.partEventIndex;
            state.partEventIndex = evtIdx;
            if (audioPowerTouched)
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
            if (appliedCount > 0)
                ParsekLog.VerboseRateLimited("Flight", $"part-events-{recIdx}",
                    $"Applied {appliedCount} part events for ghost #{recIdx} (evtIdx now {evtIdx})");
            if (visibilityChanged)
            {
                if (RefreshCompoundPartVisibility(state))
                    needsReentryMeshRebuild = true;
                if (RestoreCompoundPartsForPlacedTargets(state, placedTargetPartIds))
                    needsReentryMeshRebuild = true;
                if (needsReentryMeshRebuild)
                    GhostVisualBuilder.RebuildReentryMeshes(ghost, state.reentryFxInfo);
                RecalculateCameraPivot(state);
            }
            UpdateBlinkingLights(state, currentUT);
            UpdateActiveRobotics(state, currentUT);
        }

        private static void ApplyDecoupledPartEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            Dictionary<uint, List<uint>> tree,
            PartEvent evt,
            bool allowTransientEffects,
            ref bool visibilityChanged,
            ref bool needsReentryMeshRebuild)
        {
            // The decoupled subtree's parts are about to become a separate debris
            // recording with its own AudioSources. The parent ghost's per-pid
            // AudioGhostInfo / EngineGhostInfo / RcsGhostInfo entries for those parts
            // would otherwise keep playing, because audio sources get reanchored to the
            // ghost's cameraPivot at spawn (AttachGhostAudioToWatchPivot) — hiding the
            // part visual no longer takes the audio with it. Walk the whole subtree.
            StopFxAndAudioForSubtree(state, evt.partPersistentId, tree);
            ApplyHeatState(state, evt, HeatLevel.Cold);
            if (allowTransientEffects)
                SpawnPartPuffAtPart(ghost, evt.partPersistentId);
            if (tree != null)
            {
                HidePartSubtree(ghost, evt.partPersistentId, tree);
                RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, tree);
            }
            else
            {
                HideGhostPart(ghost, evt.partPersistentId);
                RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, null);
            }
            visibilityChanged = true;
            needsReentryMeshRebuild = true;
        }

        /// <summary>
        /// Walks the part-tree subtree rooted at <paramref name="rootPid"/> and returns
        /// every pid reachable through it (root + descendants). Pure logic: no Unity
        /// dependencies, safe to call from xUnit. If <paramref name="tree"/> is null
        /// only the root pid is returned, matching HidePartSubtree's null-tree
        /// fallback.
        /// </summary>
        internal static List<uint> CollectSubtreePids(
            uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            var result = new List<uint>();
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                result.Add(pid);
                if (tree == null) continue;
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                {
                    for (int i = 0; i < children.Count; i++)
                        stack.Push(children[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Walks the part-tree subtree rooted at <paramref name="rootPid"/> and stops
        /// engine FX, RCS FX, and ghost audio for every pid in the subtree. Used by
        /// Decoupled events so the parent ghost's per-pid FX/audio entries for parts
        /// that are physically gone (and now belong to a separate debris recording)
        /// stop emitting on the parent. The single-pid Stop helpers were not enough:
        /// HidePartSubtree hides every descendant of the decoupler, but FX and audio
        /// dictionaries are keyed by part pid, so a child engine's plume/audio survived
        /// the decouple and kept playing under the parent's cameraPivot until the
        /// parent ghost itself was destroyed.
        ///
        /// Touches Unity APIs through the per-pid Stop helpers so it cannot be
        /// invoked from xUnit (`System.Security.SecurityException : ECall methods
        /// must be packaged into a system module.`). The pure-walk logic is exposed
        /// via <see cref="CollectSubtreePids"/> for unit testing; the integrated
        /// audio-stop behavior is covered by in-game tests.
        /// </summary>
        internal static void StopFxAndAudioForSubtree(
            GhostPlaybackState state, uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            if (state == null) return;
            var pids = CollectSubtreePids(rootPid, tree);
            for (int i = 0; i < pids.Count; i++)
            {
                StopEngineFxForPart(state, pids[i]);
                StopRcsFxForPart(state, pids[i]);
                StopAudioForPart(state, pids[i]);
            }
            if (pids.Count > 1)
                ParsekLog.VerboseRateLimited("GhostAudio", $"subtree-fx-stop-{rootPid}",
                    $"Stopped FX/audio for decoupled subtree rooted at pid={rootPid}: {pids.Count} pid(s)",
                    5.0);
        }

        private static void ApplyDestroyedPartEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            PartEvent evt,
            bool allowTransientEffects,
            ref bool visibilityChanged,
            ref bool needsReentryMeshRebuild)
        {
            StopEngineFxForPart(state, evt.partPersistentId);
            StopRcsFxForPart(state, evt.partPersistentId);
            StopAudioForPart(state, evt.partPersistentId);
            if (allowTransientEffects)
                PlayPartDestroyedFxAtPart(
                    ghost,
                    evt.partPersistentId,
                    evt.partName,
                    state.audioPaused,
                    state.atmosphereFactor,
                    ResolveAudioPriorityDistance(state));
            ApplyHeatState(state, evt, HeatLevel.Cold);
            HideGhostPart(ghost, evt.partPersistentId);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, null);
            visibilityChanged = true;
            needsReentryMeshRebuild = true;
        }

        /// <summary>
        /// Spawn a stock-style explosion (visual + audio) at an individual destroyed part's world
        /// position. The flight scene path delegates to <see cref="FXMonger.Explode(Part, Vector3d, double)"/>
        /// with the part's recorded `explosionPotential` so KSP picks the size-appropriate clip
        /// from its `explosionSounds[]` array — decompiled `Part.cs:11610` calls
        /// `FXMonger.Explode(this, partTransform.position, explosionPotential + speedOffset)` and
        /// most stock parts default to `explosionPotential = 0.5f` (decompiled `Part.cs:591`),
        /// which lands on slot 1 (`sound_explosion_debris2`) for the 3-element stock array.
        /// We omit the speedOffset (0/0.12/0.25 by surface speed) because ghost playback doesn't
        /// know the live ship speed at destruction time; that produces audio one bucket lower
        /// than stock for fast-moving destruction events but the alternative would require
        /// recording the speed in every PartEvent.
        ///
        /// Replaces the old `PlayOneShotAtGhost(Destroyed)` path which always played
        /// <c>sound_explosion_large</c> through the ghost's per-vessel cameraPivot-anchored
        /// AudioSource regardless of part size and reserved a global "audio gate" for the full
        /// clip duration that then suppressed every subsequent terminal-vessel
        /// <see cref="FXMonger.Explode"/> call inside the same window, leaving multi-debris
        /// breakups nearly silent in watch mode (only the first explosion played sound).
        ///
        /// While the stock pause menu is open (<paramref name="audioPaused"/>=true), the FXMonger
        /// path is skipped — `FXMonger.Explode` would queue a SHIP_VOLUME `PlayOneShot` on a fresh
        /// AudioSource that does NOT respect the engine-side pause, so a destroyed-part event
        /// applied in the same frame the player opens Esc would punch through the pause and
        /// regress the flight pause-audio fix. The puff fallback runs instead so the destruction
        /// still produces a visual cue without queuing audio.
        ///
        /// FXMonger isn't loaded outside the flight scene; KSC playback (and any other scene
        /// where the singleton is not live) falls back to the small Parsek particle puff PLUS
        /// an independent positional explosion AudioSource via
        /// <see cref="TryPlayIndependentExplosionOneShot"/> so destroyed-part events at KSC stay
        /// audible (pre-fix the per-vessel `oneShotAudio.audioSource.PlayOneShot` covered this;
        /// removing that plumbing without an independent-audio fallback silenced KSC per-part
        /// destruction). The independent path is also the rescue route when FXMonger is live but
        /// `Explode` returned false (empty prefab array, threw, etc.) so the user still hears
        /// destruction audio.
        /// </summary>
        internal static void PlayPartDestroyedFxAtPart(
            GameObject ghost,
            uint persistentId,
            string partName,
            bool audioPaused,
            float atmosphereFactor,
            double audioPriorityDistanceMeters)
        {
            if (ghost == null) return;
            if (ShouldSuppressVisualFx(TimeWarp.CurrentRate)) return;

            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t == null || !t.gameObject.activeSelf) return;

            double power = ResolvePartExplosionPower(partName);

            // Flight scene: FXMonger handles audio + visual + spatial coalescing in one call.
            // We only take this branch when audio isn't paused (FXMonger.Explode would queue a
            // SHIP_VOLUME PlayOneShot on a fresh AudioSource that the per-source PauseAllAudio
            // doesn't reach mid-flight; PauseFxMongerExplosionAudioSources covers the in-flight
            // case but new spawns during the pause should not happen at all).
            if (!audioPaused && GhostVisualBuilder.IsFxMongerLive())
            {
                if (GhostVisualBuilder.TryTriggerStockExplosionFx(t.position, power, out string failure))
                {
                    ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-fxmonger-{persistentId}",
                        $"FXMonger.Explode queued for destroyed part pid={persistentId} part='{partName ?? "?"}' " +
                        $"at ({t.position.x:F1},{t.position.y:F1},{t.position.z:F1}) " +
                        $"power={power.ToString("F2", CultureInfo.InvariantCulture)}",
                        5.0);
                    return;
                }

                ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-fxmonger-failed-{persistentId}",
                    $"FXMonger.Explode failed for destroyed part pid={persistentId}: {failure}; " +
                    $"falling back to puff + independent audio",
                    10.0);
            }
            else if (audioPaused)
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-paused-{persistentId}",
                    $"Pause menu open: skipping FXMonger.Explode for destroyed part pid={persistentId}, " +
                    $"falling back to particle puff (visual only)",
                    5.0);
            }

            // Fallback path — runs when (a) FXMonger isn't loaded (KSC scene and other non-flight
            // scenes), (b) FXMonger.Explode itself failed, or (c) the stock pause menu is open.
            // The puff is always spawned as the visual cue. Audio is queued via the independent
            // explosion one-shot path UNLESS audio is paused — then we deliberately keep the
            // event silent so destroyed-part events applied during pause don't punch through.
            SpawnPartPuffAtPart(ghost, persistentId);
            if (!audioPaused && atmosphereFactor > 0.001f)
            {
                TryPlayIndependentExplosionOneShot(
                    t.position,
                    atmosphereFactor,
                    audioPriorityDistanceMeters,
                    power,
                    $"destroyed part pid={persistentId} part='{partName ?? "?"}'");
            }
            else
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"part-destroyed-puff-only-{persistentId}",
                    $"Particle puff spawned for destroyed part pid={persistentId} " +
                    $"(audio suppressed: paused={audioPaused} atmosphereFactor={atmosphereFactor.ToString("F2", CultureInfo.InvariantCulture)})",
                    5.0);
            }
        }

        /// <summary>
        /// Resolves the FXMonger power bucket for a destroyed part by looking up the part prefab's
        /// `explosionPotential` (matching stock `Part.explode()` which calls
        /// <c>FXMonger.Explode(this, pos, explosionPotential + speedOffset)</c>). Returns
        /// <see cref="GhostAudioPresets.DefaultPartExplosionPotential"/> (0.5 — stock default) when
        /// the part name is missing or doesn't resolve to a loaded `AvailablePart`. Power is
        /// clamped to [0,1] so a custom-cfg part with `explosionPotential` outside that range still
        /// produces a valid index pick. Pure helper with optional <paramref name="lookup"/> seam
        /// for unit tests; production callers leave it null and use PartLoader.
        /// </summary>
        internal static double ResolvePartExplosionPower(
            string partName, ExplosionPotentialLookup lookup = null)
        {
            ExplosionPotentialLookup resolve = lookup ?? DefaultExplosionPotentialLookup;
            float? potential = resolve(partName);
            if (!potential.HasValue)
                return GhostAudioPresets.DefaultPartExplosionPotential;
            return Mathf.Clamp01(potential.Value);
        }

        internal delegate float? ExplosionPotentialLookup(string partName);

        private static float? DefaultExplosionPotentialLookup(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            try
            {
                AvailablePart info = PartLoader.getPartInfoByName(partName);
                return info?.partPrefab?.explosionPotential;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"explosion-potential-lookup-failed-{partName}",
                    $"PartLoader.getPartInfoByName('{partName}') threw {ex.GetType().Name}: {ex.Message}; " +
                    $"using default explosionPotential",
                    30.0);
                return null;
            }
        }

        private static void ApplyParachuteCutEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo cutInfo;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out cutInfo))
                {
                    if (cutInfo.canopyTransform != null)
                        cutInfo.canopyTransform.localScale = Vector3.zero;
                    if (cutInfo.capTransform != null)
                        cutInfo.capTransform.gameObject.SetActive(false);
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
        }

        private static void ApplyParachuteDestroyedEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref bool visibilityChanged)
        {
            // Clean up canopy visuals before hiding the part
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo destroyedInfo;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out destroyedInfo))
                {
                    if (destroyedInfo.canopyTransform != null)
                        destroyedInfo.canopyTransform.localScale = Vector3.zero;
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
            HideGhostPart(ghost, partPersistentId);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, partPersistentId, null);
            visibilityChanged = true;
        }

        private static void ApplyInventoryPartPlacedEvent(
            GhostPlaybackState state,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref HashSet<uint> placedTargetPartIds,
            ref bool visibilityChanged)
        {
            SetGhostPartActive(state, partPersistentId, true);
            if (logicalPartIds != null)
                logicalPartIds.Add(partPersistentId);
            if (placedTargetPartIds == null)
                placedTargetPartIds = new HashSet<uint>();
            placedTargetPartIds.Add(partPersistentId);
            visibilityChanged = true;
        }

        private static void ApplyInventoryPartRemovedEvent(
            GhostPlaybackState state,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref bool visibilityChanged)
        {
            SetGhostPartActive(state, partPersistentId, false);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, partPersistentId, null);
            visibilityChanged = true;
        }

        /// <summary>
        /// Spawns a small smoke puff + spark FX at a ghost part's world position.
        /// Called before hiding the part on Decoupled/Destroyed events.
        /// </summary>
        internal static void SpawnPartPuffAtPart(GameObject ghost, uint persistentId)
        {
            if (ghost == null) return;
            if (ShouldSuppressVisualFx(TimeWarp.CurrentRate)) return;
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
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
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t != null) t.gameObject.SetActive(false);
        }

        internal static void SetGhostPartActive(GameObject ghost, uint persistentId, bool active)
        {
            if (ghost == null) return;
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t != null) t.gameObject.SetActive(active);
        }

        internal static void SetGhostPartActive(GhostPlaybackState state, uint persistentId, bool active)
        {
            if (state == null)
                return;

            SetGhostPartActive(state.ghost, persistentId, active);

            if (state.audioInfos == null)
                return;

            var restores = active ? new List<(int moduleIndex, float power)>() : null;
            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || info.partPersistentId != persistentId || info.audioSource == null)
                    continue;

                if (!active && info.audioSource.isPlaying)
                    StopLoopedGhostAudio(info, "part-inactive");

                info.audioSource.gameObject.SetActive(active);

                if (active && info.currentPower > 0f)
                    restores.Add((info.moduleIndex, info.currentPower));
            }

            if (restores == null)
                return;

            bool audioPowerTouched = false;
            for (int i = 0; i < restores.Count; i++)
            {
                if (SetEngineAudio(state, new PartEvent
                    {
                        partPersistentId = persistentId,
                        moduleIndex = restores[i].moduleIndex
                    }, restores[i].power, enforcePlaybackCap: false))
                    audioPowerTouched = true;
            }
            if (audioPowerTouched)
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
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
                    SetGhostPartActive(state, evt.partPersistentId, false);
                    initialized.Add(evt.partPersistentId);
                    hidden++;
                }
                else if (evt.eventType == PartEventType.InventoryPartRemoved)
                {
                    SetGhostPartActive(state, evt.partPersistentId, true);
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

            if (state != null)
            {
                // Fast path: cursor-driven walk advances the per-state index monotonically.
                while (state.flagEventIndex < rec.FlagEvents.Count)
                {
                    var evt = rec.FlagEvents[state.flagEventIndex];
                    if (evt.ut > currentUT) break;

                    TrySpawnFlagVessel(evt);
                    state.flagEventIndex++;
                }
                return;
            }

            // Bug #414: state-less fallback. Reached when the caller cannot yet produce a
            // GhostPlaybackState (e.g. first-spawn visual build is throttled for a frame) but
            // we still want flag vessels — which are independent permanent world objects — to
            // be placed on schedule. `FlagExistsAtPosition` dedups, so a follow-up state-aware
            // walk starting from `flagEventIndex = 0` on the next frame is cheap and correct.
            SpawnFlagVesselsUpToUT(rec, currentUT);
        }

        /// <summary>
        /// Replays all flag events whose UT is in the past for callers that do not carry
        /// a per-recording flag cursor (for example deferred spawn flushes at warp end).
        /// Returns how many flag events were eligible at the requested UT, and how many
        /// actually spawned new flag vessels after dedup, were already present, or failed.
        /// </summary>
        internal static (int eligibleCount, int spawnedCount, int alreadyPresentCount, int failedCount)
            SpawnFlagVesselsUpToUT(
            IPlaybackTrajectory rec, double currentUT)
        {
            if (rec == null || rec.FlagEvents == null || rec.FlagEvents.Count == 0)
                return (0, 0, 0, 0);

            int eligibleCount = 0;
            int spawnedCount = 0;
            int alreadyPresentCount = 0;
            int failedCount = 0;
            for (int i = 0; i < rec.FlagEvents.Count; i++)
            {
                var evt = rec.FlagEvents[i];
                if (evt.ut > currentUT)
                    break;

                eligibleCount++;
                switch (TrySpawnFlagVessel(evt))
                {
                    case FlagReplayOutcome.Spawned:
                        spawnedCount++;
                        break;
                    case FlagReplayOutcome.AlreadyPresent:
                        alreadyPresentCount++;
                        break;
                    default:
                        failedCount++;
                        break;
                }
            }

            return (eligibleCount, spawnedCount, alreadyPresentCount, failedCount);
        }

        private enum FlagReplayOutcome
        {
            Spawned,
            AlreadyPresent,
            Failed
        }

        private static FlagReplayOutcome TrySpawnFlagVessel(FlagEvent evt)
        {
            if (FlagExistsAtPosition(evt))
                return FlagReplayOutcome.AlreadyPresent;

            if (spawnFlagOverrideForTesting != null)
                return spawnFlagOverrideForTesting(evt)
                    ? FlagReplayOutcome.Spawned
                    : FlagReplayOutcome.Failed;

            return GhostVisualBuilder.SpawnFlagVessel(evt) != null
                ? FlagReplayOutcome.Spawned
                : FlagReplayOutcome.Failed;
        }

        /// <summary>
        /// Checks if a flag vessel already exists within 1m of the event position (prevents duplicates on loop).
        /// Uses world-space 3D distance rather than lat/lon to handle high-latitude and small-body cases correctly.
        /// </summary>
        private static bool FlagExistsAtPosition(FlagEvent evt)
        {
            if (flagExistsOverrideForTesting != null)
                return flagExistsOverrideForTesting(evt);

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
                var t = GhostVisualBuilder.FindGhostPartTransform(ghost, pid);
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
            var partContainer = GhostVisualBuilder.GetGhostPartContainer(ghostTransform);
            if (partContainer == null)
            {
                state.cameraPivot.localPosition = Vector3.zero;
                return;
            }
            int count = 0;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int i = 0; i < partContainer.childCount; i++)
            {
                var child = partContainer.GetChild(i);
                if (!child.gameObject.activeSelf || !child.name.StartsWith("ghost_part_"))
                    continue;
                var pos = ghostTransform.InverseTransformPoint(child.position);
                if (count == 0) { min = max = pos; }
                else { min = Vector3.Min(min, pos); max = Vector3.Max(max, pos); }
                count++;
            }
            state.cameraPivot.localPosition = count > 0 ? (min + max) * 0.5f : Vector3.zero;
            ParsekLog.VerboseRateLimited("CameraFollow", $"pivot-{state.ghost.name}",
                $"Camera pivot recalculated: localPos=({state.cameraPivot.localPosition.x:F2},{state.cameraPivot.localPosition.y:F2},{state.cameraPivot.localPosition.z:F2})" +
                $" activeParts={count}", 1.0);
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

            info.currentPower = power;

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
            ClearTrackedEnginePowerForPart(state, partPersistentId);
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
            ClearTrackedRcsPowerForPart(state, partPersistentId);
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

        internal static double ResolveAudioPriorityDistance(GhostPlaybackState state)
        {
            if (state == null)
                return 0.0;

            double distanceMeters = state.lastRenderDistance;
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0.0)
                distanceMeters = state.lastDistance;
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0.0)
                return 0.0;

            return distanceMeters;
        }

        internal static void UpdateLoopedAudioPriority(GhostPlaybackState state, AudioGhostInfo info)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;

            int priority = GhostAudioPresets.ComputeRuntimePriority(
                info.priorityClass,
                ResolveAudioPriorityDistance(state));
            if (info.audioSource.priority != priority)
                info.audioSource.priority = priority;
        }

        internal static List<AudioGhostInfo> SelectHighestPriorityActiveLoopedGhostAudioSources(
            IList<AudioGhostInfo> audioInfos, int maxSources)
        {
            var result = new List<AudioGhostInfo>();
            if (audioInfos == null || maxSources <= 0)
                return result;

            AudioGhostInfo first = null;
            AudioGhostInfo second = null;
            AudioGhostInfo third = null;
            AudioGhostInfo fourth = null;
            int firstOrder = int.MaxValue;
            int secondOrder = int.MaxValue;
            int thirdOrder = int.MaxValue;
            int fourthOrder = int.MaxValue;
            for (int i = 0; i < audioInfos.Count; i++)
            {
                AudioGhostInfo info = audioInfos[i];
                if (info == null || info.currentPower <= 0f)
                    continue;

                InsertLoopedAudioSelectionCandidate(
                    info,
                    i,
                    maxSources,
                    ref first,
                    ref firstOrder,
                    ref second,
                    ref secondOrder,
                    ref third,
                    ref thirdOrder,
                    ref fourth,
                    ref fourthOrder);
            }

            AppendIfNotNull(result, first);
            if (maxSources > 1) AppendIfNotNull(result, second);
            if (maxSources > 2) AppendIfNotNull(result, third);
            if (maxSources > 3) AppendIfNotNull(result, fourth);

            return result;
        }

        internal static void EnforceLoopedAudioPlaybackCapWithTestingOverride(GhostPlaybackState state)
        {
            var overrideForTesting = EnforceLoopedAudioPlaybackCapOverrideForTesting;
            if (overrideForTesting != null && overrideForTesting(state))
                return;

            EnforceLoopedAudioPlaybackCap(state);
        }

        internal static void EnforceLoopedAudioPlaybackCap(GhostPlaybackState state)
        {
            if (state?.audioInfos == null)
                return;

            if (state.audioPaused)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info == null || ReferenceEquals(info.audioSource, null))
                        continue;

                    if (info.currentPower <= 0f)
                        StopLoopedGhostAudio(info, "paused-power=0", force: true);
                    else if (info.audioSource.isPlaying)
                        info.audioSource.Pause();
                }
                return;
            }

            if (state.audioMuted || state.atmosphereFactor < 0.001f)
            {
                string stopReason = state.audioMuted ? "muted" : "vacuum";
                foreach (var info in state.audioInfos.Values)
                {
                    if (info != null && !ReferenceEquals(info.audioSource, null) && info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, stopReason);
                }
                return;
            }

            AudioGhostInfo first = null;
            AudioGhostInfo second = null;
            AudioGhostInfo third = null;
            AudioGhostInfo fourth = null;
            int firstOrder = int.MaxValue;
            int secondOrder = int.MaxValue;
            int thirdOrder = int.MaxValue;
            int fourthOrder = int.MaxValue;
            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || ReferenceEquals(info.audioSource, null))
                    continue;

                UpdateLoopedAudioPriority(state, info);

                if (info.currentPower > 0f)
                {
                    InsertLoopedAudioSelectionCandidate(
                        info,
                        info.selectionOrder,
                        GhostAudioPresets.MaxAudioSourcesPerGhost,
                        ref first,
                        ref firstOrder,
                        ref second,
                        ref secondOrder,
                        ref third,
                        ref thirdOrder,
                        ref fourth,
                        ref fourthOrder);
                }
                else if (info.audioSource.isPlaying)
                {
                    StopLoopedGhostAudio(info, "power=0");
                }
            }

            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || ReferenceEquals(info.audioSource, null) || info.currentPower <= 0f)
                    continue;

                float volume = ComputeGhostAudioVolume(
                    info.volumeCurve.Evaluate(info.currentPower),
                    state.atmosphereFactor);
                if (!IsLoopedAudioSelectedForPlayback(info, first, second, third, fourth))
                {
                    if (info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "capped");
                    continue;
                }
                if (volume <= 0f)
                {
                    if (info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "volume=0");
                    continue;
                }

                if (info.audioSource.volume != volume)
                    info.audioSource.volume = volume;

                float pitch = info.pitchCurve.Evaluate(info.currentPower);
                if (info.audioSource.pitch != pitch)
                    info.audioSource.pitch = pitch;

                if (!ReferenceEquals(info.audioSource.clip, info.clip))
                    info.audioSource.clip = info.clip;

                if (!info.audioSource.isPlaying && CanStartLoopedGhostAudio(info.audioSource))
                    StartLoopedGhostAudio(info, volume, pitch);
            }
        }

        /// <summary>
        /// Set engine audio volume/pitch from recorded throttle power.
        /// Called alongside SetEngineEmission for EngineIgnited/Throttle/Shutdown events.
        /// </summary>
        internal static bool SetEngineAudio(
            GhostPlaybackState state,
            PartEvent evt,
            float power,
            bool enforcePlaybackCap = true)
        {
            if (state.audioInfos == null) return false;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            AudioGhostInfo info;
            if (!state.audioInfos.TryGetValue(key, out info)) return false;

            info.currentPower = power;
            if (state.audioPaused)
            {
                if (!ReferenceEquals(info.audioSource, null) && power <= 0f)
                    StopLoopedGhostAudio(info, "paused-power=0", force: true);
                return true;
            }
            if (state.audioMuted)
            {
                // Keep tracked power in sync during warp so unmute resumes the correct state.
                if (!ReferenceEquals(info.audioSource, null))
                    StopLoopedGhostAudio(info, "muted");
                return true;
            }
            if (ReferenceEquals(info.audioSource, null)) return true;

            if (enforcePlaybackCap)
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
            return true;
        }

        internal static bool CanStartLoopedGhostAudio(bool sourceExists, bool sourceIsActiveAndEnabled)
        {
            return sourceExists && sourceIsActiveAndEnabled;
        }

        internal static bool CanStartLoopedGhostAudio(AudioSource audioSource)
        {
            return CanStartLoopedGhostAudio(
                sourceExists: !ReferenceEquals(audioSource, null),
                sourceIsActiveAndEnabled: !ReferenceEquals(audioSource, null) && audioSource.isActiveAndEnabled);
        }

        /// <summary>
        /// Stop all audio sources for a given part (by PID).
        /// Used defensively on decouple/destroy.
        /// </summary>
        internal static void StopAudioForPart(GhostPlaybackState state, uint partPersistentId)
        {
            ClearTrackedAudioPowerForPart(state, partPersistentId);
            if (state?.audioInfos == null) return;
            foreach (var info in state.audioInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                if (info.audioSource != null && info.audioSource.isPlaying)
                    StopLoopedGhostAudio(info, "part-removed");
            }
        }

        /// <summary>
        /// Triggers KSP's stock terminal explosion FX (visual + audio) for a destroyed ghost.
        /// FXMonger handles its own audio mixing — multiple concurrent explosions get fresh
        /// AudioSources per call (decompiled FXMonger.LateUpdate ~line 553-573) and stock spatial
        /// coalescing merges blasts within 10 m. Returns true when FXMonger queued the FX,
        /// false when FXMonger was unavailable or threw and the custom particle fallback ran.
        ///
        /// No temporal gate: the previous "audio gate" implementation reserved the global gate
        /// for the full <c>sound_explosion_large</c> clip duration (~8.6 s) which then suppressed
        /// every following terminal-vessel explosion inside the window — multi-debris breakups
        /// in watch mode were nearly silent because all but the first ghost fell to a
        /// visual-only fallback. Stock KSP itself does not gate temporally; it lets the mixer
        /// handle simultaneous voices and relies on 3D rolloff to attenuate distant ones.
        /// </summary>
        internal static bool TryTriggerStockExplosionFxOrCustom(
            Vector3 worldPosition,
            double power,
            float vesselLength,
            string contextDescription,
            TryTriggerStockExplosionFxDelegate triggerStockExplosionFx = null,
            Action<Vector3, float> spawnExplosionFx = null,
            Action<StockExplosionFxResult> recordResult = null)
        {
            string context = string.IsNullOrEmpty(contextDescription)
                ? "ghost explosion"
                : contextDescription;
            TryTriggerStockExplosionFxDelegate triggerStock = triggerStockExplosionFx
                ?? ((Vector3 pos, double pwr, out string failure) =>
                    GhostVisualBuilder.TryTriggerStockExplosionFx(pos, pwr, out failure));
            Action<Vector3, float> spawnCustom =
                spawnExplosionFx ?? ((pos, len) => GhostVisualBuilder.SpawnExplosionFx(pos, len));

            if (triggerStock(worldPosition, power, out string stockFxFailure))
            {
                recordResult?.Invoke(StockExplosionFxResult.StockQueued);
                return true;
            }

            ParsekLog.Warn("ExplosionFx",
                $"FXMonger.Explode did not queue stock FX for {context}; " +
                $"falling back to custom FX: {stockFxFailure}");
            spawnCustom(worldPosition, vesselLength);
            recordResult?.Invoke(StockExplosionFxResult.StockFailedCustomVisualSpawned);
            return false;
        }

        /// <summary>
        /// Plays an independent explosion one-shot at <paramref name="worldPosition"/>. Used by the
        /// KSC playback path where FXMonger isn't loaded (it lives in the flight scene only),
        /// so Parsek owns the AudioSource. Spawns a fresh <c>AudioSource</c> per call (the source
        /// auto-destroys after the clip finishes), so concurrent voices don't stack on a single
        /// shared source — Unity's mixer plus the source's 3D rolloff handle simultaneous KSC ghost
        /// explosions the same way stock FXMonger handles concurrent flight-scene explosions.
        ///
        /// <paramref name="power"/> drives clip selection through <see cref="GhostAudioPresets.ResolveDestroyedClipByPower"/>
        /// so size-appropriate clips play here too: a small KSC ghost picks the shorter
        /// `sound_explosion_debris1`, a heavy lifter picks `sound_explosion_large`, mirroring how
        /// stock FXMonger's `explosionSounds[]` array is indexed in the flight-scene path.
        /// </summary>
        internal static bool TryPlayIndependentExplosionOneShot(
            Vector3 worldPosition,
            float atmosphereFactor,
            double distanceMeters,
            double power,
            string contextDescription,
            ResolveExplosionOneShotAudioCandidateDelegate resolveExplosionAudioCandidate = null,
            PlayExplosionOneShotAudioDelegate playExplosionAudio = null)
        {
            string context = string.IsNullOrEmpty(contextDescription)
                ? "ghost explosion"
                : contextDescription;
            ResolveExplosionOneShotAudioCandidateDelegate resolveCandidate =
                resolveExplosionAudioCandidate
                ?? (() => ResolveExplosionOneShotAudioCandidate(atmosphereFactor, distanceMeters, power));

            ExplosionOneShotAudioCandidate candidate = resolveCandidate();
            if (!candidate.canPlay)
            {
                ParsekLog.Warn("GhostAudio",
                    $"Explosion one-shot unavailable for {context}: " +
                    $"{(string.IsNullOrEmpty(candidate.failureReason) ? "unknown reason" : candidate.failureReason)}");
                return false;
            }

            try
            {
                PlayExplosionOneShotAudioDelegate playAudio = playExplosionAudio ?? QueueExplosionOneShotAudio;
                playAudio(worldPosition, candidate);
                ParsekLog.Verbose("GhostAudio",
                    $"Explosion one-shot queued for {context}: clip='{candidate.clipPath}' " +
                    $"vol={candidate.volume.ToString("F2", CultureInfo.InvariantCulture)}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GhostAudio",
                    $"Explosion one-shot queue failed for {context}: {ex.Message}");
                return false;
            }
        }

        internal static ExplosionOneShotAudioCandidate ResolveExplosionOneShotAudioCandidate(
            float atmosphereFactor,
            double distanceMeters,
            double power)
        {
            string clipPath = GhostAudioPresets.ResolveDestroyedClipByPower(power);
            if (clipPath == null)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    failureReason = "no explosion clip configured"
                };
            }

            AudioClip clip = null;
            try
            {
                clip = GameDatabase.Instance != null
                    ? GameDatabase.Instance.GetAudioClip(clipPath)
                    : null;
            }
            catch (Exception ex)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = clipPath,
                    failureReason = $"clip lookup failed ({ex.GetType().Name}: {ex.Message})"
                };
            }

            if (clip == null)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = clipPath,
                    failureReason = $"AudioClip not found: '{clipPath}'"
                };
            }

            float volume = ComputeGhostAudioVolume(GhostAudioPresets.OneShotVolumeScale, atmosphereFactor);
            if (volume <= 0f)
            {
                return new ExplosionOneShotAudioCandidate
                {
                    canPlay = false,
                    clipPath = clipPath,
                    clip = clip,
                    clipLengthSeconds = NormalizeOneShotDurationSeconds(clip.length),
                    failureReason = "computed volume is zero"
                };
            }

            return new ExplosionOneShotAudioCandidate
            {
                canPlay = true,
                clipPath = clipPath,
                clip = clip,
                clipLengthSeconds = NormalizeOneShotDurationSeconds(clip.length),
                volume = volume,
                priority = GhostAudioPresets.ComputeRuntimePriority(
                    GhostAudioPresets.ClassifyOneShotPriority(PartEventType.Destroyed),
                    distanceMeters)
            };
        }

        internal static void QueueExplosionOneShotAudio(
            Vector3 worldPosition,
            ExplosionOneShotAudioCandidate candidate)
        {
            if (candidate.clip == null)
                throw new InvalidOperationException("candidate has no AudioClip");

            GameObject sourceObject = null;
            try
            {
                sourceObject = new GameObject(ExplosionOneShotAudioObjectName);
                sourceObject.transform.position = worldPosition;
                var source = sourceObject.AddComponent<AudioSource>();
                source.clip = candidate.clip;
                source.spatialBlend = GhostVisualBuilder.GhostAudioSpatialBlend;
                source.panStereo = 0f;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = DistanceThresholds.GhostAudio.RolloffMinDistanceMeters;
                source.maxDistance = DistanceThresholds.GhostAudio.RolloffMaxDistanceMeters;
                source.priority = candidate.priority;
                source.loop = false;
                source.playOnAwake = false;
                source.volume = 1f;
                TrackExplosionOneShotAudioSource(source);
                source.PlayOneShot(candidate.clip, candidate.volume);
                UnityEngine.Object.Destroy(
                    sourceObject,
                    NormalizeOneShotDurationSeconds(candidate.clipLengthSeconds) + 0.25f);
            }
            catch
            {
                if (sourceObject != null)
                    UnityEngine.Object.Destroy(sourceObject);
                throw;
            }
        }

        internal static int PauseExplosionOneShotAudio()
        {
            PruneExplosionOneShotAudioSources();
            int paused = 0;
            for (int i = 0; i < activeExplosionOneShotAudioSources.Count; i++)
            {
                AudioSource source = activeExplosionOneShotAudioSources[i];
                if (source != null && source.isPlaying)
                {
                    source.Pause();
                    if (!ContainsAudioSourceReference(pausedExplosionOneShotAudioSources, source))
                        pausedExplosionOneShotAudioSources.Add(source);
                    paused++;
                }
            }

            // Also pause any FXMonger-spawned explosion AudioSources still playing in-flight.
            // FXMonger.LateUpdate spawns a fresh AudioSource per ProtoExplosion (decompiled
            // FXMonger.LateUpdate ~line 553-573 / 781-803 / 882-904) and PlayOneShots SHIP_VOLUME
            // — those sources are owned by FXMonger, not Parsek's per-vessel audioInfos, so
            // PauseAllAudio's per-source loop above doesn't reach them. Without this walk, opening
            // the Esc menu mid-explosion would leave the FXMonger PlayOneShot voices playing
            // through the pause (regressing the pre-fix tracked-oneShotAudio pause behavior, where
            // PauseAllAudio's `state.oneShotAudio.audioSource.Pause()` covered the per-vessel
            // PlayOneShot voice). Tracked sources pile into `pausedExplosionOneShotAudioSources`
            // so UnpauseExplosionOneShotAudio's existing iteration resumes them via UnPause().
            paused += PauseFxMongerExplosionAudioSources();

            return paused;
        }

        private static int PauseFxMongerExplosionAudioSources()
        {
            List<FXObject> objects = GhostVisualBuilder.ResolveFxMongerExplosionObjects();
            if (objects == null)
                return 0;

            int paused = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                FXObject fxObj = objects[i];
                GameObject effectObj = fxObj?.effectObj;
                if (effectObj == null) continue;

                AudioSource[] sources = effectObj.GetComponentsInChildren<AudioSource>(includeInactive: false);
                for (int s = 0; s < sources.Length; s++)
                {
                    AudioSource source = sources[s];
                    if (source == null || !source.isPlaying) continue;
                    source.Pause();
                    if (!ContainsAudioSourceReference(pausedExplosionOneShotAudioSources, source))
                        pausedExplosionOneShotAudioSources.Add(source);
                    paused++;
                }
            }
            return paused;
        }

        internal static int UnpauseExplosionOneShotAudio()
        {
            PruneExplosionOneShotAudioSources();
            int unpaused = 0;
            for (int i = 0; i < pausedExplosionOneShotAudioSources.Count; i++)
            {
                AudioSource source = pausedExplosionOneShotAudioSources[i];
                if (source != null)
                {
                    source.UnPause();
                    unpaused++;
                }
            }

            pausedExplosionOneShotAudioSources.Clear();
            return unpaused;
        }

        private static void TrackExplosionOneShotAudioSource(AudioSource source)
        {
            if (source == null)
                return;

            PruneExplosionOneShotAudioSources();
            activeExplosionOneShotAudioSources.Add(source);
        }

        private static void PruneExplosionOneShotAudioSources()
        {
            PruneMissingAudioSources(activeExplosionOneShotAudioSources);
            PruneMissingAudioSources(pausedExplosionOneShotAudioSources);
        }

        private static void PruneMissingAudioSources(List<AudioSource> sources)
        {
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                if (sources[i] == null)
                    sources.RemoveAt(i);
            }
        }

        private static bool ContainsAudioSourceReference(List<AudioSource> sources, AudioSource source)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                if (object.ReferenceEquals(sources[i], source))
                    return true;
            }

            return false;
        }

        internal static float NormalizeOneShotDurationSeconds(float clipLengthSeconds)
        {
            if (float.IsNaN(clipLengthSeconds) || float.IsInfinity(clipLengthSeconds) || clipLengthSeconds <= 0f)
                return GhostAudioPresets.ExplosionOneShotFallbackDurationSeconds;
            return clipLengthSeconds;
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
                        StopLoopedGhostAudio(info, "muted");
                }
            }
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
            state.audioPaused = true;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource == null)
                        continue;

                    if (info.currentPower <= 0f)
                        StopLoopedGhostAudio(info, "paused-power=0", force: true);
                    else if (info.audioSource.isPlaying)
                        info.audioSource.Pause();
                }
            }
        }

        /// <summary>
        /// Resume all ghost audio sources paused by PauseAllAudio.
        /// </summary>
        internal static void UnpauseAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            state.audioPaused = false;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource == null)
                        continue;

                    if (info.currentPower <= 0f)
                        StopLoopedGhostAudio(info, "power=0", force: true);
                    else
                        info.audioSource.UnPause();
                }
            }
        }

        /// <summary>
        /// Compute the ghost audio volume for a given power level and atmosphere state.
        /// Centralizes the volume formula so SetEngineAudio, the KSC independent explosion
        /// one-shot path, and UpdateAudioAtmosphere all use the same calculation.
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
            if (state == null || state.audioInfos == null || state.audioMuted || state.audioPaused) return;

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

            EnforceLoopedAudioPlaybackCap(state);
        }

        private static void AppendIfNotNull(List<AudioGhostInfo> result, AudioGhostInfo info)
        {
            if (info != null)
                result.Add(info);
        }

        private static bool ShouldLoopedAudioCandidatePrecede(
            AudioGhostInfo candidate, int candidateOrder, AudioGhostInfo current, int currentOrder)
        {
            if (candidate == null)
                return false;
            if (current == null)
                return true;

            int priorityCompare = GhostAudioPresets.GetBasePriority(candidate.priorityClass)
                .CompareTo(GhostAudioPresets.GetBasePriority(current.priorityClass));
            if (priorityCompare != 0)
                return priorityCompare < 0;

            int powerCompare = candidate.currentPower.CompareTo(current.currentPower);
            if (powerCompare != 0)
                return powerCompare > 0;

            return candidateOrder < currentOrder;
        }

        private static void InsertLoopedAudioSelectionCandidate(
            AudioGhostInfo candidate,
            int candidateOrder,
            int maxSources,
            ref AudioGhostInfo first,
            ref int firstOrder,
            ref AudioGhostInfo second,
            ref int secondOrder,
            ref AudioGhostInfo third,
            ref int thirdOrder,
            ref AudioGhostInfo fourth,
            ref int fourthOrder)
        {
            if (candidate == null || maxSources <= 0)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, first, firstOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                if (maxSources > 2)
                {
                    third = second;
                    thirdOrder = secondOrder;
                }
                if (maxSources > 1)
                {
                    second = first;
                    secondOrder = firstOrder;
                }
                first = candidate;
                firstOrder = candidateOrder;
                return;
            }

            if (maxSources <= 1)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, second, secondOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                if (maxSources > 2)
                {
                    third = second;
                    thirdOrder = secondOrder;
                }
                second = candidate;
                secondOrder = candidateOrder;
                return;
            }

            if (maxSources <= 2)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, third, thirdOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                third = candidate;
                thirdOrder = candidateOrder;
                return;
            }

            if (maxSources <= 3)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, fourth, fourthOrder))
            {
                fourth = candidate;
                fourthOrder = candidateOrder;
            }
        }

        private static bool IsLoopedAudioSelectedForPlayback(
            AudioGhostInfo info,
            AudioGhostInfo first,
            AudioGhostInfo second,
            AudioGhostInfo third,
            AudioGhostInfo fourth)
        {
            return ReferenceEquals(info, first)
                || ReferenceEquals(info, second)
                || ReferenceEquals(info, third)
                || ReferenceEquals(info, fourth);
        }

        private static void StartLoopedGhostAudio(AudioGhostInfo info, float volume, float pitch)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;

            info.audioSource.Play();
            ParsekLog.VerboseRateLimited("GhostAudio",
                $"audio-start-{info.partPersistentId}-{info.moduleIndex}",
                $"Engine audio started: pid={info.partPersistentId} midx={info.moduleIndex} " +
                $"power={info.currentPower:F2} vol={volume:F2} pitch={pitch:F2}",
                5.0);
        }

        private static void StopLoopedGhostAudio(AudioGhostInfo info, string reason)
        {
            StopLoopedGhostAudio(info, reason, force: false);
        }

        private static void StopLoopedGhostAudio(AudioGhostInfo info, string reason, bool force)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;
            bool wasPlaying = info.audioSource.isPlaying;
            if (!force && !wasPlaying)
                return;

            info.audioSource.Stop();
            if (!wasPlaying)
                return;

            ParsekLog.VerboseRateLimited("GhostAudio",
                $"audio-stop-{info.partPersistentId}-{info.moduleIndex}",
                $"Engine audio stopped: pid={info.partPersistentId} midx={info.moduleIndex} reason={reason}",
                5.0);
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
        /// Re-applies engine FX (plume / smoke emitters + particle systems) for every engine
        /// at its last recorded throttle (<see cref="EngineGhostInfo.currentPower"/>) after a
        /// distance / warp FX suppression is lifted. The symmetric partner to
        /// <see cref="StopAllEngineFx"/>.
        ///
        /// Engine FX are event-driven (recorded EngineThrottle threshold crossings), so unlike
        /// RCS and audio they have no per-frame driver that turns them back on. Without this a
        /// ghost that crosses the FX-LOD range during a steady burn (canonical case: a looping
        /// aircraft that repeatedly flies past the anchor and back) and returns keeps a dead
        /// plume until its next recorded throttle change. Only engines with
        /// <c>currentPower &gt; 0</c> are restored, so shut-down engines stay dark.
        ///
        /// Must run after <see cref="ApplyPartEvents"/> has caught the throttle cursor up to the
        /// current UT (the <c>ApplyFrameVisuals</c> call order guarantees this), so a throttle-down
        /// that occurred while FX were suppressed has already reset <c>currentPower</c> and is not
        /// re-ignited here.
        /// </summary>
        internal static void RestoreActiveEngineFx(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return;

            int restored = 0;
            foreach (var restore in CollectDeferredEnginePowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetEngineEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
                restored++;
            }

            if (restored > 0)
                ParsekLog.Verbose("Visual",
                    $"RestoreActiveEngineFx: re-applied {restored} engine FX after FX suppression lifted");
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

            info.currentPower = power;

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

        internal static List<(ulong key, float power)> CollectDeferredEnginePowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.engineInfos == null)
                return restores;

            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedEnginePowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.engineInfos == null)
                return;

            foreach (var info in state.engineInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static List<(ulong key, float power)> CollectDeferredRcsPowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.rcsInfos == null)
                return restores;

            foreach (var kvp in state.rcsInfos)
            {
                RcsGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedRcsPowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.rcsInfos == null)
                return;

            foreach (var info in state.rcsInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static List<(ulong key, float power)> CollectDeferredAudioPowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.audioInfos == null || state.audioMuted || state.audioPaused)
                return restores;

            foreach (var kvp in state.audioInfos)
            {
                AudioGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedAudioPowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.audioInfos == null)
                return;

            foreach (var info in state.audioInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static void RestoreDeferredRuntimeFxState(GhostPlaybackState state)
        {
            if (state == null)
                return;

            foreach (var restore in CollectDeferredEnginePowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetEngineEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
            }

            foreach (var restore in CollectDeferredRcsPowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetRcsEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
            }

            var audioRestores = CollectDeferredAudioPowerRestores(state);
            for (int i = 0; i < audioRestores.Count; i++)
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(audioRestores[i].key, out partPersistentId, out moduleIndex);
                SetEngineAudio(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, audioRestores[i].power, enforcePlaybackCap: false);
            }
            if (audioRestores.Count > 0)
                EnforceLoopedAudioPlaybackCapWithTestingOverride(state);
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

        /// <summary>
        /// Bug #433: decide whether a skipped-ghost trajectory should still fire
        /// PlaybackCompleted at past-end. Only the PlaybackEnabled=false cause is
        /// career-neutral (visibility toggle) and must drive the policy's spawn
        /// branch; !hasData / externalVesselSuppressed are structural and must
        /// silently skip as before.
        ///
        /// Mirrors the visible-path contract in GhostPlaybackEngine at two points:
        ///   - "has renderable data" matches HasRenderableGhostData (Points OR
        ///     OrbitSegments OR SurfacePos) so orbit-only and surface-only
        ///     recordings still complete when hidden.
        ///   - past-end comparisons are strict (`&gt;`), same as the visible path
        ///     at GhostPlaybackEngine.cs `pastEnd = ctx.currentUT &gt; traj.EndUT`
        ///     and `pastEffectiveEnd = ctx.currentUT &gt; f.chainEndUT`, so the
        ///     toggle does not shift completion timing by a frame.
        ///
        /// Pure predicate — accepts pre-collected set-membership booleans so the
        /// engine can pass `HashSet.Contains` results without exposing the set.
        /// </summary>
        internal static bool ShouldFireHiddenPastEndCompletion(
            IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags,
            double currentUT,
            bool completionAlreadyFired,
            bool earlyDebrisCompletion)
        {
            if (traj == null) return false;
            if (completionAlreadyFired || earlyDebrisCompletion) return false;
            if (traj.PlaybackEnabled) return false; // only the visibility-hidden cause
            if (!GhostPlaybackEngine.HasRenderableGhostData(traj)) return false;
            bool pastEnd = currentUT > traj.EndUT;
            bool pastEffectiveEnd = currentUT > flags.chainEndUT;
            return pastEnd || pastEffectiveEnd;
        }

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
        /// Terminal states that produce a real vessel via ShouldSpawnAtRecordingEnd
        /// when no other suppression applies. The set is intentionally narrow:
        /// every other terminal (SubOrbital, Destroyed, Recovered, Docked, Boarded,
        /// and any future addition) means the ghost playback is the final visible
        /// trajectory — nothing replaces it.
        ///
        /// Used both here (gating spawn) and in
        /// RecordingOptimizer.TailPreservesTerminalSpawnState (gating tail trim):
        /// trimming the boring tail is only safe when a spawned vessel takes over
        /// from the trim UT onward. For non-spawnable terminals the tail IS the
        /// playback the player sees, so it must be preserved.
        /// </summary>
        internal static bool IsSpawnableTerminal(TerminalState ts)
        {
            switch (ts)
            {
                case TerminalState.Orbiting:
                case TerminalState.Landed:
                case TerminalState.Splashed:
                    return true;
                default:
                    return false;
            }
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
        /// <param name="isChainLooping">True if the recording's chain has at least one branch-0 looping segment.</param>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtRecordingEnd(
            Recording rec,
            bool isActiveChainMember,
            bool isChainLooping)
        {
            return ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember,
                isChainLooping,
                treeContext: null);
        }

        /// <param name="liveSameLaunchVesselPresent">
        /// True when a live (non-ghost) vessel of the recording's craft is currently
        /// in the scene. Only the flight-scene caller can evaluate this, so it defaults
        /// to true (the conservative value that keeps the #573 same-recording block
        /// absolute) for every other caller. It gates the standalone Rewind-to-Launch
        /// target lift in <see cref="ShouldBlockSpawnForRewindSuppression"/>.
        /// </param>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtRecordingEnd(
            Recording rec,
            bool isActiveChainMember,
            bool isChainLooping,
            RecordingTree treeContext,
            bool liveSameLaunchVesselPresent = true)
        {
            if (!string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId))
            {
                return (false,
                    "terminal spawn superseded by recording " +
                    rec.TerminalSpawnSupersededByRecordingId);
            }

            // Plain Rewind-to-Launch source protection (#573). The same-recording
            // marker blocks the rewound target so its old vessel cannot respawn next
            // to a live re-flight of that launch. The block now lifts for a STANDALONE
            // target when no live same-craft vessel is present (the player rewound to
            // launch and then did NOT re-fly it) so the recorded vessel still
            // materializes at its terminal. Future same-tree recordings are never
            // marked (#589); chain targets and active re-flights stay blocked.
            if (ShouldBlockSpawnForRewindSuppression(
                    rec, liveSameLaunchVesselPresent, out string rewindSuppressionReason))
            {
                return (false, rewindSuppressionReason);
            }

            // Preserve the existing "already spawned" precedence, but make destroyed
            // recordings win over the generic missing-snapshot diagnostic.
            if (rec.VesselSpawned)
            {
                return (false, "already spawned (VesselSpawned=true)");
            }
            if (rec.VesselDestroyed)
            {
                return (false, "vessel destroyed");
            }
            // Base condition: must have a vessel snapshot to materialize a real
            // vessel. The in-memory copy is a transient cache that several sites
            // null out in-session (vessel-gone debris, the crew-unreserve pass);
            // the durable copy lives in the _vessel.craft sidecar. Re-hydrate it
            // from disk for genuinely-spawnable, non-debris recordings only. The
            // checks below reject debris / non-spawnable terminals / ghost-only /
            // non-leaf recordings regardless, so they skip the disk probe and keep
            // the cheap early-out (no per-frame I/O). Without this, a spawnable
            // leaf whose snapshot was dropped (e.g. an orbital payload re-flown
            // after a Rewind-to-Launch) would silently fail to re-materialize.
            if (rec.VesselSnapshot == null)
            {
                bool worthHydrating = !rec.IsDebris
                    && rec.TerminalStateValue.HasValue
                    && IsSpawnableTerminal(rec.TerminalStateValue.Value);
                if (!worthHydrating
                    || !RecordingStore.TryHydrateVesselSnapshotFromSidecar(rec))
                {
                    return (false, "no vessel snapshot");
                }
            }

            // Gloops Flight Recorder recordings are ghost-only — never spawn a real vessel
            if (rec.IsGhostOnly)
            {
                return (false, "ghost-only recording (Gloops)");
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

            // Suppress spawn for looping chains (ghost loops forever, never reaches a "final" state).
            // Note: fully-disabled chains used to suppress here too, but that gated career state on
            // a visual toggle (bug #433). A fully-disabled chain still spawns its vessel at tip.
            if (isChainLooping)
            {
                return (false, "chain looping");
            }

            // Breakup-continuous check: the foreground recording continued past a breakup
            // (ProcessBreakupEvent sets ChildBranchPointId without creating a same-PID
            // continuation). If no child shares this vessel's PID, the recording IS the
            // effective leaf and should be spawnable. Only applies to non-debris recordings
            // with a spawnable terminal state (Landed/Splashed/Orbiting). (#224)
            bool hasSpawnableTerminal = rec.TerminalStateValue.HasValue
                && IsSpawnableTerminal(rec.TerminalStateValue.Value);
            bool effectiveLeaf = rec.ChildBranchPointId != null
                && !rec.IsDebris
                && hasSpawnableTerminal
                && IsEffectiveLeafForVessel(rec, treeContext);

            // Non-leaf tree recordings should never spawn — they branched into a
            // same-vessel continuation that carries the correct snapshot.
            if (rec.ChildBranchPointId != null && !effectiveLeaf)
            {
                return (false, "non-leaf tree recording");
            }

            // Safety net: even if ChildBranchPointId is null, check the resolved tree
            // for recordings that are parents of a branch point. Covers edge cases where
            // ChildBranchPointId was not set (e.g., serialization gaps). (#114)
            // Skip for effective-leaf recordings — the branch point exists but the recording
            // is still the leaf for its vessel.
            if (!effectiveLeaf && IsNonLeafInTree(rec, treeContext))
            {
                return (false, "non-leaf in tree (safety net)");
            }

            // Debris recordings are visual-only (short TTL, no meaningful vessel to persist)
            if (rec.IsDebris)
            {
                return (false, "debris recording (visual-only)");
            }

            // Terminal states: destroyed/recovered/docked/boarded/suborbital should not spawn
            // SubOrbital includes FLYING and ESCAPING — vessel would materialize mid-air and crash (#45)
            if (rec.TerminalStateValue.HasValue
                && !IsSpawnableTerminal(rec.TerminalStateValue.Value))
            {
                return (false, $"terminal state {rec.TerminalStateValue.Value}");
            }

            // Snapshot situation check: if the snapshot's sit field is FLYING or SUB_ORBITAL,
            // KSP's on-rails aero check (101.3 kPa) immediately destroys spawned vessels.
            // This catches cases where TerminalState is null/Landed but the snapshot was
            // captured mid-flight. (#114)
            // Override: if the terminal is spawnable (Landed/Splashed/Orbiting), the
            // vessel DID reach a safe state — the snapshot's sit field may be stale
            // from recording start. Orbiting: vessel captured during ascent (FLYING)
            // but achieved orbit. The spawn path corrects the snapshot situation
            // before spawning. (#169, #EVA-spawn)
            bool terminalOverridesUnsafe = rec.TerminalStateValue.HasValue
                && IsSpawnableTerminal(rec.TerminalStateValue.Value);
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
        /// Pure predicate: returns true when a recording must NOT spawn at its
        /// terminal end because plain Rewind-to-Launch scoped a #573 active/source
        /// suppression marker onto it. The only marker reason produced today is
        /// <see cref="ParsekScenario.RewindSpawnSuppressionReasonSameRecording"/>
        /// (the rewind-target recording stripped during rewind).
        ///
        /// The block exists so the rewound vessel cannot respawn next to a live
        /// re-flight of the same launch (#573). It is NOT unconditional: it lifts for
        /// a STANDALONE target (<see cref="Recording.ChainId"/> empty) when
        /// <paramref name="liveSameLaunchVesselPresent"/> is false — i.e. the player
        /// rewound to launch and then flew something else, so there is no re-flight to
        /// collide with and the recorded vessel should still materialize at its
        /// terminal. Chain targets stay blocked (a continuation tip can resurrect via
        /// the chain-tip spawn path, which is the #573 phantom class) and any target
        /// stays blocked while a live same-craft vessel is present (a genuine re-fly).
        /// Other clearing paths remain: the explicit watch-entry lift
        /// (<see cref="ParsekScenario.TryClearSpawnSuppressionOnWatchEntry"/>) and the
        /// next rewind/revert reset. This is a query: it does not mutate the recording
        /// or log.
        /// </summary>
        private static bool ShouldBlockSpawnForRewindSuppression(
            Recording rec,
            bool liveSameLaunchVesselPresent,
            out string reason)
        {
            reason = "";
            if (rec == null || !rec.SpawnSuppressedByRewind)
                return false;

            if (string.Equals(rec.SpawnSuppressedByRewindReason,
                    ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                    StringComparison.Ordinal))
            {
                // Lift for a standalone rewind target the player did not re-fly: no
                // live same-craft vessel exists, so spawning the recorded terminal
                // cannot duplicate a re-flight. Fall through to the normal spawn gates.
                bool standalone = string.IsNullOrEmpty(rec.ChainId);
                if (standalone && !liveSameLaunchVesselPresent)
                    return false;

                reason = "spawn suppressed post-rewind (same-recording active/source protection, #573)";
                return true;
            }

            return false;
        }

        // Injectable override for the scene-agnostic live-same-craft scan
        // (AnyLiveRealVesselSharesRecordedCraft), so the Flight / KSC / Tracking-Station
        // rewind-suppression lift is unit-testable without FlightGlobals. Pass null to
        // restore the real scan. Deliberately separate from vesselExistsOverride /
        // RealVesselExists: that primitive is per-frame cached and the KSC path never
        // invalidates the cache, so the rewind lift uses an uncached fresh scan instead.
        private static Func<Recording, bool> liveSameCraftOverride;

        internal static void SetLiveSameCraftOverrideForTesting(Func<Recording, bool> finder)
        {
            liveSameCraftOverride = finder;
        }

        internal static void ResetLiveSameCraftOverrideForTesting()
        {
            liveSameCraftOverride = null;
        }

        /// <summary>
        /// True when a live, non-ghost vessel of the recording's craft is currently in the
        /// scene. Uses the craft-baked persistentId deliberately: this is a "would a spawn
        /// collide with a live re-flight of this craft" check, not a same-launch identity
        /// claim, so it must also catch a relaunch of the same craft (which carries the
        /// baked pid but a fresh launch Guid — do NOT route this through
        /// <see cref="VesselLaunchIdentity"/>, which is guid-gated and would wrongly lift
        /// during an active re-fly). Parsek's own map-presence ghosts are excluded via
        /// <see cref="GhostMapPresence.IsGhostMapVessel"/>. Scene-agnostic: it scans
        /// <c>FlightGlobals.Vessels</c>, which lists live vessels in Flight and the
        /// Tracking Station; at the Space Center that list may be empty or null, and the
        /// null guard then reports no live craft (the correct outcome there, since a
        /// genuine re-flight only exists in the flight scene and can never collide at KSC).
        /// Deliberately an uncached fresh scan (unlike <see cref="RealVesselExists"/>); it
        /// runs at most once per frame because
        /// <see cref="ResolveRewindSuppressionLiveLaunchPresence"/> short-circuits before
        /// calling it for every non-marked recording.
        /// </summary>
        internal static bool AnyLiveRealVesselSharesRecordedCraft(Recording rec)
        {
            if (rec == null)
                return false;
            // Consult the test seam before the pid / FlightGlobals guards so a test can
            // assert any recording's outcome without a live Unity vessel list.
            if (liveSameCraftOverride != null)
                return liveSameCraftOverride(rec);
            if (rec.VesselPersistentId == 0)
                return false;
            var vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return false;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null)
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    continue;
                if (v.persistentId == rec.VesselPersistentId)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Scene-agnostic resolver for whether the #573 same-recording spawn-suppression
        /// block should stay absolute for <paramref name="rec"/>. Returns true
        /// (conservative: keep blocking) for everything except a standalone
        /// Rewind-to-Launch target with no live same-craft vessel in the scene; for that
        /// case it returns false to authorize the lift in
        /// <see cref="ShouldSpawnAtRecordingEnd"/> so the recorded vessel materializes at
        /// its terminal. The Flight, Tracking Station and Space Center spawn-at-end paths
        /// all route through this so they behave identically (the original fix was
        /// flight-only). The marker rides at most one recording, so the FlightGlobals scan
        /// runs only for that recording. Never mutates the recording; emits one
        /// VerboseRateLimited line on a lift so it is observable in every scene's KSP.log.
        /// </summary>
        internal static bool ResolveRewindSuppressionLiveLaunchPresence(Recording rec)
        {
            if (rec == null
                || !rec.SpawnSuppressedByRewind
                || !string.Equals(
                        rec.SpawnSuppressedByRewindReason,
                        ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                        StringComparison.Ordinal)
                || !string.IsNullOrEmpty(rec.ChainId))
            {
                // Not a liftable standalone same-recording target — the predicate does
                // not consult the value (no block, or a non-liftable block), so keep it
                // conservative without paying for a vessel scan.
                return true;
            }

            bool present = AnyLiveRealVesselSharesRecordedCraft(rec);
            if (!present)
            {
                ParsekLog.VerboseRateLimited(
                    "Rewind",
                    rec.RecordingId,
                    $"same-recording spawn suppression lifted for standalone rewind target " +
                    $"rec={rec.RecordingId} vessel=\"{rec.VesselName}\" pid={rec.VesselPersistentId} — " +
                    "no live same-craft vessel present (plain Rewind-to-Launch not re-flown); " +
                    "recorded terminal will materialize (#573/#589)");
            }
            return present;
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
            bool isChainLooping = !string.IsNullOrEmpty(rec.ChainId) &&
                RecordingStore.IsChainLooping(rec.ChainId);

            // Intermediate chain segments should not spawn — only the chain tip spawns.
            // In Flight, ShouldSuppressSpawnForChain handles this via runtime GhostChain
            // state, but at KSC there are no GhostChain objects. Use the committed data.
            if (RecordingStore.IsChainMidSegment(rec))
                return (false, "intermediate chain segment (not tip)");

            // Scene-agnostic #573 rewind lift: a standalone Rewind-to-Launch target the
            // player did not re-fly (no live same-craft vessel present) still spawns its
            // recorded terminal at KSC, identical to Flight and the Tracking Station.
            return ShouldSpawnAtRecordingEnd(
                rec, false, isChainLooping,
                treeContext: null,
                ResolveRewindSuppressionLiveLaunchPresence(rec));
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
            return IsNonLeafInTree(rec, treeContext: null);
        }

        internal static bool IsNonLeafInTree(Recording rec, RecordingTree treeContext)
        {
            RecordingTree tree;
            if (!TryResolveTreeContext(rec, treeContext, out tree))
                return false;

            // Check if any branch point lists this recording as a parent.
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                var bp = tree.BranchPoints[b];
                if (bp.ParentRecordingIds != null && bp.ParentRecordingIds.Contains(rec.RecordingId))
                {
                    string treeLabel = !string.IsNullOrEmpty(tree.Id) ? tree.Id : (tree.TreeName ?? "(pending)");
                    ParsekLog.VerboseRateLimited("Spawner",
                        $"safety-net-{rec.RecordingId}",
                        string.Format(CultureInfo.InvariantCulture,
                            "IsNonLeafInTree: recording {0} is parent of branch point {1} " +
                            "in tree {2} (ChildBranchPointId was null — safety net triggered)",
                            rec.RecordingId, bp.Id, treeLabel), 30.0);
                    return true;
                }
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
            return IsEffectiveLeafForVessel(rec, treeContext: null);
        }

        internal static bool IsEffectiveLeafForVessel(Recording rec, RecordingTree treeContext)
        {
            if (string.IsNullOrEmpty(rec.ChildBranchPointId))
                return false;

            RecordingTree tree;
            if (!TryResolveTreeContext(rec, treeContext, out tree))
                return false;

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
            return false;
        }

        private static bool TryResolveTreeContext(
            Recording rec,
            RecordingTree treeContext,
            out RecordingTree tree)
        {
            tree = null;
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId) || !rec.IsTreeRecording)
                return false;

            if (treeContext != null)
            {
                bool sameTreeId = !string.IsNullOrEmpty(treeContext.Id) && treeContext.Id == rec.TreeId;
                bool containsRecording = treeContext.Recordings != null
                    && treeContext.Recordings.ContainsKey(rec.RecordingId);
                if (sameTreeId || containsRecording)
                {
                    tree = treeContext;
                    return true;
                }
            }

            var trees = RecordingStore.CommittedTrees;
            for (int t = 0; t < trees.Count; t++)
            {
                if (trees[t].Id == rec.TreeId)
                {
                    tree = trees[t];
                    return true;
                }
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
            return isWatchedGhost
                && !double.IsNaN(ghostDistanceMeters)
                && !double.IsInfinity(ghostDistanceMeters)
                && ghostDistanceMeters >= 0.0
                && !ShouldExitWatchForCutoff(ghostDistanceMeters, cutoffKm);
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
                double ghostDistanceMeters, bool forceFullFidelity,
                RenderingZone? classifiedZone = null)
        {
            if (forceFullFidelity)
                return (false, false, false, false, false);

            if (shouldHideMesh)
                return (true, true, true, true, false);

            if (ghostDistanceMeters >= DistanceThresholds.GhostFlight.LoopSimplifiedMeters)
                return (true, true, true, true, false);

            bool shouldReduceFidelity = classifiedZone.HasValue
                ? classifiedZone.Value == RenderingZone.Visual
                : ghostDistanceMeters >= DistanceThresholds.GhostFlight.FullFidelityRangeMeters;
            if (shouldReduceFidelity)
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
                $"ReduceFidelity: disabled {state.fidelityDisabledRenderers.Count}/{renderers.Length} renderers, "
                + $"anchorDist={RenderingZoneManager.FormatDistanceForLog(state.lastDistance)}");
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
