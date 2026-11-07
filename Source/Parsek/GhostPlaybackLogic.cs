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
            catch (MissingMethodException)
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
            RemovedReplayFlags.ResetForTesting();
            bodyRadiusOverrideForTesting = null;
            removeStandingFlagOverrideForTesting = null;
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

        /// <summary>
        /// Ghost CommNet one-node rule (design 15.6, scenarios 13 and 15): true when a real
        /// vessel of the recording's launch is in the world and is NOT chain-ghosted, so its
        /// own stock node already relays for it. Unlike
        /// <see cref="ShouldSkipExternalVesselGhost(Recording, bool)"/> there is no tree-owned
        /// exemption: the visual ghost of a tree's own recording still draws beside the real
        /// vessel, but a second relay node would count the vessel twice (the Tracking Station
        /// case of a claimed vessel that was never despawned there).
        /// </summary>
        internal static bool RealVesselRelaysForRecording(Recording rec)
        {
            if (rec == null || rec.VesselPersistentId == 0)
                return false;
            if (IsGhostedByChain(rec.VesselPersistentId))
                return false;
            return RealVesselExistsForRecording(rec);
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
        /// Production entry point for <see cref="ShouldSkipExternalVesselGhost(string, uint, bool, Recording)"/>:
        /// passes the recording itself so the "the real vessel is already its own visual"
        /// existence test is launch-Guid gated. Without the gate a preserved DIFFERENT launch of
        /// the same craft (same craft-baked pid) hides a ghost whose recorded vessel is not in
        /// the world at all.
        /// </summary>
        internal static bool ShouldSkipExternalVesselGhost(Recording rec, bool isActiveRecording)
        {
            if (rec == null) return false;
            return ShouldSkipExternalVesselGhost(
                rec.TreeId, rec.VesselPersistentId, isActiveRecording, rec);
        }

        /// <summary>
        /// Pure decision method: determines whether a ghost should be skipped for an
        /// external background vessel whose real vessel still exists in the game world.
        /// An "external vessel" is a tree recording that was tracked via BackgroundMap
        /// (not the active vessel) and whose VesselPersistentId matches a live vessel.
        /// Returns true if the ghost should be skipped.
        ///
        /// <para>
        /// <paramref name="launchGateRecording"/> (production always supplies it, via the
        /// <see cref="ShouldSkipExternalVesselGhost(Recording, bool)"/> overload) switches the
        /// existence test to the launch-Guid-gated
        /// <see cref="RealVesselExistsForRecording"/>. Null keeps the bare pid test and is only
        /// used by pid-level unit cells that assert the surrounding gates.
        /// </para>
        /// </summary>
        internal static bool ShouldSkipExternalVesselGhost(
            string treeId, uint vesselPersistentId, bool isActiveRecording,
            Recording launchGateRecording = null)
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

            bool realVesselExists = launchGateRecording != null
                ? RealVesselExistsForRecording(launchGateRecording)
                : RealVesselExists(vesselPersistentId);
            if (realVesselExists)
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

        private static Func<string, double> bodyRadiusOverrideForTesting;
        private static Func<RemovedReplayFlags.Tombstone, int> removeStandingFlagOverrideForTesting;

        /// <summary>
        /// How many individual FX magnitude field writes have been REFUSED or have thrown since the
        /// process started (or since <see cref="ResetFxMagnitudeWriteFailureCountForTesting"/>).
        /// Zero is the only healthy value: a nonzero count means some part of the plume magnitude
        /// scaling is silently degrading to the pre-S1 boolean plume.
        ///
        /// This exists because the H36 flight of 2026-08-11 red'd with the write throwing on EVERY
        /// engine and RCS emitter while the log line that reported it was rate-limited to one line
        /// per minute per module — the log undercounted a total no-op as a curiosity.
        /// </summary>
        internal static int FxMagnitudeWriteFailureCount;

        // Injectable override for the scene-agnostic live-same-craft scan
        // (AnyLiveRealVesselSharesRecordedCraft), so the Flight / KSC / Tracking-Station
        // rewind-suppression lift is unit-testable without FlightGlobals. Pass null to
        // restore the real scan. Deliberately separate from vesselExistsOverride /
        // RealVesselExists: that primitive is per-frame cached and the KSC path never
        // invalidates the cache, so the rewind lift uses an uncached fresh scan instead.
        private static Func<Recording, bool> liveSameCraftOverride;

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
