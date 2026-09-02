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

            // S3: no dictionary — every family is walked whole each frame and the lists are short
            // (a craft with 40 control surfaces is an outlier). The per-frame entry short-circuits
            // on null, so a ghost with no synthesis families pays a single reference compare.
            state.synthesizedMotionInfos = result.synthesizedMotionInfos;
            state.prevSynthUT = double.NaN;
            state.smoothedAngularVelocity = Vector3.zero;
            state.prevGroundHeadingUT = double.NaN;
            state.smoothedHeadingRateDegPerSec = 0f;

            if (result.colorChangerInfos != null)
                state.colorChangerInfos = GhostVisualBuilder.GroupColorChangersByPartId(result.colorChangerInfos);

            state.compoundPartInfos = result.compoundPartInfos;

            PopulateAudioInfos(state, result);

            // M1: the snapshot-read module baselines. Kept on the state so a loop cycle
            // can restore the same baseline, and applied LAST so it layers over the
            // stow/cold baselines the Populate* helpers above just laid down — while
            // still preceding the prefix event replay, which owns the final word.
            state.snapshotBaselines = result.snapshotBaselines;
            ApplySnapshotBaselines(state);

            if (ShouldEvaluateOrphanEnginePlayback(state, traj))
                AutoStartOrphanEnginePlayback(state, traj);
        }

        /// <summary>
        /// M1: applies the per-part module state read out of the ghost snapshot, so a
        /// ghost spawns looking the way the craft actually looked instead of at the
        /// prefab / all-stowed pose (gear up, panels folded, servos at rest).
        ///
        /// Ordering is the whole contract. This runs AFTER the stow/cold spawn baselines
        /// (PopulateDeployableInfos, PopulateHeatInfos) and BEFORE the prefix replay in
        /// <see cref="ApplyPartEvents"/>, which replays every recorded event at or before
        /// the playback cursor. So: prefab pose, then snapshot baseline, then recorded
        /// truth. All appliers here are absolute-state and idempotent, which is what makes
        /// a baseline + a start-UT seed for the same state harmless.
        ///
        /// On a split TIP the snapshot is launch-time-stale by construction, and the seeds
        /// correct it for every reversible family the head span emitted an event for — in
        /// BOTH directions since <c>RecordingOptimizer.AppendReversibleStateSeeds</c>. A
        /// family the head span never emitted an event for gets no seed and keeps the
        /// snapshot's value, which is the right answer unless the recorder could not see
        /// the change at all (the dead-probe families). Full statement on
        /// <see cref="SnapshotPartBaseline"/>.
        ///
        /// Every family no-ops when the snapshot said nothing about it, so a recording
        /// whose snapshot carries none of these keys behaves exactly as before M1.
        ///
        /// <paramref name="rateLimitLog"/> must be true on the loop-cycle path: this is a
        /// one-shot operation per ghost at spawn (plain Verbose), but per-ghost-per-cycle
        /// on a loop boundary, where hundreds of looping ghosts would multiply one line
        /// each into a flood.
        /// </summary>
        internal static void ApplySnapshotBaselines(
            GhostPlaybackState state, bool rateLimitLog = false)
        {
            if (state == null) return;
            Dictionary<uint, SnapshotPartBaseline> baselines = state.snapshotBaselines;
            if (baselines == null || baselines.Count == 0) return;

            int deployableApplied = 0;
            int brokenApplied = 0;
            int parachuteApplied = 0;
            int lightApplied = 0;
            int servoApplied = 0;
            int servoSkipped = 0;

            foreach (var kvp in baselines)
            {
                SnapshotPartBaseline baseline = kvp.Value;
                if (baseline == null) continue;
                uint pid = kvp.Key;
                var evt = new PartEvent { partPersistentId = pid };
                SnapshotBaselineActions actions = ResolveSnapshotBaselineActions(baseline);

                if (actions.deployableTarget.HasValue)
                {
                    bool applied = actions.deployableThroughCargoBayCascade
                        ? ApplyCargoBayState(state, evt, actions.deployableTarget.Value)
                        : ApplyDeployableState(state, evt, actions.deployableTarget.Value);
                    if (applied)
                        deployableApplied++;
                }

                // S6, applied AFTER any pose so the hide is not undone by ApplyDeployableState's
                // own un-hide. The two are mutually exclusive on a well-formed snapshot (the
                // parser sets at most one), but ordering it this way makes a malformed node that
                // somehow carried both resolve to BROKEN, which is the safer reading: a hidden
                // panel that should have been posed is a smaller lie than a posed panel that
                // should have been gone.
                if (actions.deployableBroken
                    && ApplyDeployableBrokenState(state, pid, broken: true))
                {
                    brokenApplied++;
                }

                if (ApplySnapshotParachuteBaseline(state, pid, actions.parachuteAction))
                    parachuteApplied++;

                // Blink mode BEFORE power: ApplyLightPowerEvent consults blinkEnabled to
                // decide whether to switch the lamp on immediately or leave it to the
                // blink pass, so the reverse order would flash a blinking lamp fully on
                // for the frames before UpdateBlinkingLights next runs.
                if (actions.blinkEnabled.HasValue)
                {
                    ApplyLightBlinkModeEvent(
                        state, pid, actions.blinkEnabled.Value,
                        actions.blinkRateHz.HasValue ? actions.blinkRateHz.Value : 0f);
                }
                else if (actions.blinkRateHz.HasValue)
                {
                    ApplyLightBlinkRateEvent(state, pid, actions.blinkRateHz.Value);
                }

                if (actions.lightPower.HasValue)
                {
                    ApplyLightPowerEvent(state, pid, actions.lightPower.Value);
                    lightApplied++;
                }

                ApplySnapshotRoboticBaseline(
                    state, pid, baseline, ref servoApplied, ref servoSkipped);
            }

            string summary =
                $"Snapshot baseline applied: parts={baselines.Count} " +
                $"deployables={deployableApplied} brokenDeployables={brokenApplied} " +
                $"parachutes={parachuteApplied} " +
                $"lights={lightApplied} servos={servoApplied} servosSkipped={servoSkipped} " +
                $"(vessel='{state.vesselName ?? "unknown"}')";
            if (rateLimitLog)
                ParsekLog.VerboseRateLimited("GhostVisual", "snapshot-baseline-loop", summary, 1.0);
            else
                ParsekLog.Verbose("GhostVisual", summary);
        }

        /// <summary>
        /// The per-part actions one snapshot baseline resolves to. Separated from the
        /// application pass because the interesting decisions — which single deployable
        /// opinion wins, whether the cargo cascade is needed, which light source drives
        /// power, which parachute states are actionable — are pure and must be testable;
        /// the appliers themselves reach into Unity components and cannot run in xUnit.
        /// </summary>
        internal struct SnapshotBaselineActions
        {
            /// <summary>The single deployable-family target, or null for "no opinion".</summary>
            public bool? deployableTarget;
            /// <summary>True when the target must route through the cargo-bay cascade (deployable, else jettison panels).</summary>
            public bool deployableThroughCargoBayCascade;
            /// <summary>
            /// S6: the snapshot said this part's deployable is BROKEN, so the ghost must spawn with
            /// its break subtree hidden. Independent of <see cref="deployableTarget"/> rather than
            /// folded into it: broken is not a point on the stowed&lt;-&gt;deployed axis, and the two
            /// drive different ghost surfaces (a pose vs a SetActive).
            /// </summary>
            public bool deployableBroken;
            public bool? lightPower;
            public bool? blinkEnabled;
            public float? blinkRateHz;
            public SnapshotParachuteAction parachuteAction;
        }

        internal enum SnapshotParachuteAction
        {
            None,
            SemiDeployed,
            Deployed,
            Cut,
        }

        /// <summary>
        /// Pure resolution of one part's snapshot baseline into at most one action per
        /// ghost surface.
        ///
        /// The deployable families all collapse onto the SINGLE
        /// <c>DeployableGhostInfo</c> a part owns (the singular out-param of
        /// <c>AddPartVisuals</c>), so exactly one opinion may be applied or they would
        /// silently overwrite each other in dictionary order. The order is: explicit deploy
        /// state, then gear, then cargo bay, then animation group, then the generic
        /// animation (which the parser already suppresses whenever a dedicated handler
        /// exists).
        ///
        /// That order is THIS RESOLVER'S OWN, not a recorder-derived precedence — the
        /// recorder has no precedence to mirror. Its per-family pollers
        /// (<c>CheckDeployableState</c> / <c>CheckGearState</c> / <c>CheckCargoBayState</c> /
        /// <c>CheckAnimationGroupState</c>) run independently and each emits its own event
        /// type for the same part; <c>FlightRecorder.HasDedicatedAnimateHandler</c>, whose
        /// listing order this echoes, is an OR that only suppresses the standalone
        /// <c>ModuleAnimateGeneric</c> read. The mismatch is visible at the cargo-bay arm:
        /// a part carrying BOTH a <c>ModuleDeployablePart</c> and a <c>ModuleCargoBay</c>
        /// takes the deployState branch and therefore <c>ApplyDeployableState</c>, skipping
        /// the cargo cascade (<c>ApplyCargoBayState</c>'s deployable-else-jettison-panels
        /// fallback) that the same part's live cargo events would route through. Rare enough
        /// to leave alone; do not "fix" the ordering by consulting the recorder, which does
        /// not answer this question.
        /// </summary>
        internal static SnapshotBaselineActions ResolveSnapshotBaselineActions(
            SnapshotPartBaseline baseline)
        {
            var actions = new SnapshotBaselineActions();
            if (baseline == null) return actions;

            // S6 first, and NOT in the else-if chain below: a broken panel has no pose opinion
            // to compete with (the parser leaves deployableExtended unset for BROKEN), so this is
            // an independent flag rather than another arm of the single-opinion cascade.
            actions.deployableBroken = baseline.deployableBroken;

            if (baseline.deployableExtended.HasValue)
                actions.deployableTarget = baseline.deployableExtended;
            else if (baseline.gearDeployed.HasValue)
                actions.deployableTarget = baseline.gearDeployed;
            else if (baseline.cargoBayOpen.HasValue)
            {
                actions.deployableTarget = baseline.cargoBayOpen;
                actions.deployableThroughCargoBayCascade = true;
            }
            else if (baseline.animationGroupDeployed.HasValue)
                actions.deployableTarget = baseline.animationGroupDeployed;
            else if (baseline.animateGenericDeployed.HasValue)
                actions.deployableTarget = baseline.animateGenericDeployed;

            // A ColorChanger cabin light only stands in for a ModuleLight the part does
            // not have (the parser enforces that), so at most one of these is ever set and
            // they share one power action.
            //
            // An ALL-FALSE light opinion (lamp off, blink off, no rate) is dropped rather
            // than applied: it is byte-identical to a fresh LightPlaybackState, and the
            // appliers would materialise a dict entry for it via
            // GetOrCreateLightPlaybackState. UpdateBlinkingLights then walks that entry on
            // EVERY frame of EVERY such ghost to set a light state nothing ever changed.
            // Nothing is lost by skipping it: parts with a LightGhostInfo already get an
            // entry pre-populated at spawn (PopulateGhostInfoDictionaries), and ColorChanger
            // materials are initialised to their off colour at build time.
            if (LightBaselineSaysSomething(baseline))
            {
                actions.lightPower = baseline.lightOn ?? baseline.colorChangerOn;
                actions.blinkEnabled = baseline.lightBlinking;
                actions.blinkRateHz = baseline.lightBlinkRate;
            }

            actions.parachuteAction = ClassifySnapshotParachuteAction(
                baseline.parachutePersistentState);

            return actions;
        }

        /// <summary>
        /// True when a part's light baseline differs from a default
        /// <see cref="LightPlaybackState"/> (off, not blinking, no explicit rate) in a way
        /// worth materialising an entry for. See the call site for why an all-false opinion
        /// is dropped instead of applied.
        /// </summary>
        internal static bool LightBaselineSaysSomething(SnapshotPartBaseline baseline)
        {
            if (baseline == null) return false;
            bool? power = baseline.lightOn ?? baseline.colorChangerOn;
            return (power.HasValue && power.Value)
                || (baseline.lightBlinking.HasValue && baseline.lightBlinking.Value)
                || baseline.lightBlinkRate.HasValue;
        }

        /// <summary>
        /// Maps a persisted <c>ModuleParachute.persistentState</c> onto the canopy
        /// appliers. STOWED / ACTIVE resolve to None: the build-time canopy pose IS stowed
        /// (localScale zero) and ACTIVE means armed-but-not-open. An unrecognised state is
        /// also None — no opinion beats a guess.
        ///
        /// THERE IS DELIBERATELY NO "REPACKED" CASE, and it is not an omission. The
        /// four-state parachute machine (Deployed / Semi / Cut / Repacked) is a PartEvent
        /// distinction, not a snapshot one: stock <c>ModuleParachute.Repack()</c> writes
        /// STOWED back to <c>persistentState</c>, so a repacked chute persists as STOWED and
        /// arrives here as None — which lands on the build-time stowed-with-cap pose, i.e.
        /// exactly the repacked look. The event side needs its own type because it must
        /// UNDO a prior cut on a ghost already posed cut; the snapshot side never has that
        /// problem, because it runs at build time before any cut was applied.
        /// </summary>
        internal static SnapshotParachuteAction ClassifySnapshotParachuteAction(
            string persistentState)
        {
            if (string.IsNullOrEmpty(persistentState)) return SnapshotParachuteAction.None;

            switch (persistentState)
            {
                case "SEMIDEPLOYED": return SnapshotParachuteAction.SemiDeployed;
                case "DEPLOYED": return SnapshotParachuteAction.Deployed;
                case "CUT": return SnapshotParachuteAction.Cut;
                default: return SnapshotParachuteAction.None;
            }
        }

        private static bool ApplySnapshotParachuteBaseline(
            GhostPlaybackState state, uint pid, SnapshotParachuteAction action)
        {
            switch (action)
            {
                case SnapshotParachuteAction.SemiDeployed:
                    ApplyParachuteSemiDeployedEvent(state, pid);
                    return true;
                case SnapshotParachuteAction.Deployed:
                    // Re-invocation-safe: the real-canopy branch is an absolute pose
                    // assignment, and the fake-canopy fallback routes through
                    // TrackFakeCanopy, which destroys any existing canopy for this pid
                    // before storing the new one. So a DEPLOYED baseline followed by a
                    // ParachuteDeployed seed at startUT leaves exactly one canopy.
                    ApplyParachuteDeployedEvent(state, state.ghost, pid);
                    return true;
                case SnapshotParachuteAction.Cut:
                    ApplyParachuteCutEvent(state, pid);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Stamps each servo's snapshot pose onto its <see cref="RoboticGhostInfo"/> as
        /// the spawn baseline and applies it. The module name is verified against the
        /// ordinal on the ghost side: if a different mod set shifted the robotic ordinals
        /// between record and replay, the mismatch degrades to no-baseline (today's
        /// behaviour) rather than posing the wrong servo.
        /// </summary>
        private static void ApplySnapshotRoboticBaseline(
            GhostPlaybackState state, uint pid, SnapshotPartBaseline baseline,
            ref int applied, ref int skipped)
        {
            if (baseline.roboticPoses == null || baseline.roboticPoses.Count == 0) return;
            if (state.roboticInfos == null || state.roboticInfos.Count == 0)
            {
                skipped += baseline.roboticPoses.Count;
                return;
            }

            for (int i = 0; i < baseline.roboticPoses.Count; i++)
            {
                SnapshotRoboticPose pose = baseline.roboticPoses[i];
                ulong key = FlightRecorder.EncodeEngineKey(pid, pose.ordinal);
                RoboticGhostInfo info;
                if (!state.roboticInfos.TryGetValue(key, out info) || info == null)
                {
                    skipped++;
                    continue;
                }

                if (!string.Equals(info.moduleName, pose.moduleName, StringComparison.Ordinal))
                {
                    skipped++;
                    ParsekLog.VerboseRateLimited("GhostVisual", $"servo-ordinal-drift-{pid}",
                        $"Snapshot baseline: servo ordinal {pose.ordinal} on pid={pid} is " +
                        $"'{pose.moduleName}' in the snapshot but '{info.moduleName ?? "(null)"}' " +
                        $"on the ghost — baseline skipped for this servo (mod-set drift)");
                    continue;
                }

                info.spawnValue = pose.value;
                info.hasSnapshotBaseline = true;
                ApplyRoboticSpawnBaseline(info);
                applied++;
            }
        }

        /// <summary>
        /// Puts one servo at its spawn baseline (<see cref="RoboticGhostInfo.spawnValue"/>
        /// — the snapshot pose when M1 read one, else 0f = the prefab pose).
        ///
        /// ROTORS ALWAYS PARK. A rotor is RPM-driven rather than posed, and a ProtoVessel
        /// snapshot carries no persisted key that reflects actual spin:
        /// <c>ModuleRoboticServoRotor.currentRPM</c> is not persistent, and the persisted
        /// <c>rpmLimit</c> is a SETTING (230 by default, 460 on every stock Breaking Ground
        /// helicopter). <see cref="GhostVisualBuilder.TryResolveSnapshotRoboticPoseKey"/>
        /// therefore refuses to give a rotor a baseline at all, so
        /// <c>hasSnapshotBaseline</c> is false for every rotor and this parks
        /// unconditionally — the pre-M1 behaviour. If a persisted spin-state key ever
        /// appears, arm from THAT plus <c>servoMotorIsEngaged</c>; do not re-derive spin
        /// from a limit.
        /// </summary>
        internal static void ApplyRoboticSpawnBaseline(RoboticGhostInfo info)
        {
            if (info == null) return;

            info.currentValue = info.spawnValue;
            if (info.visualMode == RoboticVisualMode.RotorRpm)
            {
                info.active = false;
                info.lastUpdateUT = double.NaN;
                return;
            }

            // S3 WHEEL STEERING PARKS STRAIGHT. ApplyRoboticPose only writes a transform for the
            // Linear and Rotational modes, so without this branch a caliper left turned 30 degrees
            // at the end of a cycle would still be turned at the start of the next one while
            // steeringAngleDegrees claimed zero — the M4 carry-over class this whole reset exists
            // for. WheelGroundSpeed deliberately keeps falling through: a wheel's SPIN PHASE is not
            // observable, so there is nothing to park.
            if (info.visualMode == RoboticVisualMode.WheelSteeringHeading)
            {
                info.steeringAngleDegrees = 0f;
                if (info.servoTransform != null)
                    info.servoTransform.localRotation = info.stowedRot;
                info.active = false;
                info.lastUpdateUT = double.NaN;
                return;
            }

            ApplyRoboticPose(info, info.spawnValue);
            info.active = false;
            info.lastUpdateUT = double.NaN;
        }

        /// <summary>
        /// Loop-cycle counterpart of the spawn robotic baseline: puts every servo back to
        /// its spawn pose so cycle N+1 does not inherit cycle N's end pose.
        /// <see cref="ResetForLoopCycle"/> zeroes the bookkeeping fields but cannot touch
        /// <c>servoTransform</c> (it must stay Unity-free), which is exactly the
        /// carry-over: the numbers said "at rest" while the mesh stayed where the last
        /// cycle left it. Split out of
        /// <see cref="ReapplySpawnTimeModuleBaselinesForLoopCycle"/> so it is testable
        /// without a live ghost GameObject.
        /// </summary>
        internal static int RestoreRoboticSpawnBaselines(GhostPlaybackState state)
        {
            if (state?.roboticInfos == null || state.roboticInfos.Count == 0) return 0;

            int restored = 0;
            foreach (var kvp in state.roboticInfos)
            {
                RoboticGhostInfo info = kvp.Value;
                if (info == null) continue;
                ApplyRoboticSpawnBaseline(info);
                restored++;
            }

            return restored;
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
                    // S3 wheel steering: the eased caliper angle is per-cycle state like
                    // currentValue. RestoreRoboticSpawnBaselines puts the TRANSFORM back.
                    info.steeringAngleDegrees = 0f;
                }
            }

            // S2: drop every in-flight deployable transition. A panel caught mid-clip at the loop
            // boundary must RE-STOW (Reapply... step 2) and then replay the new cycle's own events
            // from their own UTs — resuming against a rewound clock would run the clip backwards.
            ClearActiveDeployableTransitions(state);

            // S3: the attitude / heading derivatives and every synthesized deflection are per-cycle
            // state. Clearing prevSynthUT / prevGroundHeadingUT (rather than leaving a stale
            // sample) is what stops the loop boundary's backwards UT jump from being read as an
            // enormous angular velocity on the first frame of the new cycle.
            state.prevSynthRotation = Quaternion.identity;
            state.prevSynthUT = double.NaN;
            state.smoothedAngularVelocity = Vector3.zero;
            state.prevGroundHeading = Vector3.zero;
            state.prevGroundHeadingUT = double.NaN;
            state.smoothedHeadingRateDegPerSec = 0f;
            if (state.synthesizedMotionInfos != null)
            {
                var synth = state.synthesizedMotionInfos;
                if (synth.gimbals != null)
                    for (int i = 0; i < synth.gimbals.Count; i++)
                        if (synth.gimbals[i] != null) synth.gimbals[i].currentDeflection = Vector2.zero;
                if (synth.controlSurfaces != null)
                    for (int i = 0; i < synth.controlSurfaces.Count; i++)
                        if (synth.controlSurfaces[i] != null) synth.controlSurfaces[i].currentDeflection = 0f;
                if (synth.sunTrackers != null)
                {
                    for (int i = 0; i < synth.sunTrackers.Count; i++)
                    {
                        if (synth.sunTrackers[i] == null) continue;
                        synth.sunTrackers[i].currentAngleDegrees = 0f;
                        synth.sunTrackers[i].hasAimed = false;
                    }
                }
            }

            // S3 launch dust: the intensity bookkeeping resets here; the particle system itself is
            // stopped by ReapplySpawnTimeModuleBaselinesForLoopCycle (a Unity call). The latched
            // ground reference is NOT cleared — it is a property of the recording's launch site,
            // not of the cycle, and re-latching would cost a section scan for no behaviour change.
            if (state.launchDustInfo != null)
                state.launchDustInfo.lastIntensity = 0f;

            // S4: the three EVA flags go back to "pack stowed, not thrusting, on his feet", which is
            // how a kerbal leaves the hatch. The particle system itself is stopped in
            // ReapplySpawnTimeModuleBaselinesForLoopCycle (a Unity call this method cannot make),
            // exactly like launch dust. Without the reset a kerbal who was thrusting at the end of
            // one cycle would have the plume lit at the start of the next, before the recording's
            // own thrust event replayed.
            state.evaJetpackDeployed = false;
            state.evaJetpackThrusting = false;
            state.evaRagdoll = false;

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
        ///  3e. Robotic servos: put every servo transform back to its spawn
        ///     pose. <see cref="ResetForLoopCycle"/> zeroes the robotic
        ///     bookkeeping but cannot touch <c>servoTransform</c>, so the mesh
        ///     used to stay wherever the previous cycle left it while the
        ///     numbers claimed "at rest".
        ///  3f. Snapshot baselines (M1): re-apply the recorded per-part module
        ///     state that steps 1-3e just reverted, so the new cycle restarts
        ///     from the same look the first cycle spawned with instead of the
        ///     all-stowed prefab look.
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
            //
            //    S6: the break subtree is RE-SHOWN here too, and it has to be explicit. A loop
            //    cycle restarts from the craft's pre-launch look, and a panel that broke during
            //    the prior cycle must be whole again at the top of the next one — otherwise the
            //    first cycle of a looping replay shows the break and every cycle after it starts
            //    with the panel already missing. The un-hide runs BEFORE the re-stow so the
            //    re-stow's own broken-check (in ApplyDeployableState) is a no-op rather than a
            //    second write.
            if (state.deployableInfos != null)
            {
                foreach (var kvp in state.deployableInfos)
                {
                    ApplyDeployableBrokenState(state, kvp.Key, broken: false);
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

            // 3b. Parachutes: re-stow canopies to the spawn-time pose captured by
            //     TryBuildParachuteInfo and re-activate caps, so packs that cut /
            //     repacked / destroyed / deployed in the prior cycle are back to
            //     their pre-launch pose. Destroy any fake canopy left over from a
            //     prior ParachuteDeployed event so the new cycle's event can
            //     re-create it fresh.
            //
            //     This is already the full inverse of both ParachuteCut and
            //     ParachuteRepacked, so adding the repack event needed no change
            //     here: the baseline a cycle restarts from IS the repacked pose
            //     (canopy hidden, cap on), and the new cycle replays whatever
            //     cut / repack events it holds at their own UTs. It reads the
            //     stored stowed pose rather than hardcoding Vector3.zero so the
            //     builder stays the single source of that pose.
            if (state.parachuteInfos != null)
            {
                foreach (var kvp in state.parachuteInfos)
                {
                    ParachuteGhostInfo info = kvp.Value;
                    if (info == null) continue;
                    if (info.canopyTransform != null)
                    {
                        info.canopyTransform.localScale = info.stowedCanopyScale;
                        info.canopyTransform.localPosition = info.stowedCanopyPos;
                        info.canopyTransform.localRotation = info.stowedCanopyRot;
                    }
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

            // 3e. Robotics: put every servo back to its spawn pose. ResetForLoopCycle
            //     already zeroes the robotic bookkeeping (currentValue / active /
            //     lastUpdateUT) but cannot touch servoTransform — it has to stay
            //     Unity-free — so before this step the numbers said "at rest" while the
            //     mesh stayed wherever the previous cycle left it. A rover replay's second
            //     cycle began with its arm already unfolded.
            int roboticsRestored = RestoreRoboticSpawnBaselines(state);

            // 3g. S3 synthesis: put every gimbal ring, control surface and sun-tracking pivot back
            //     to the NEUTRAL pose captured at build time. Same failure mode step 3e exists for:
            //     ResetForLoopCycle zeroes the deflection numbers but cannot touch a Transform, so
            //     without this the mesh would keep the previous cycle's last deflection while the
            //     numbers claimed neutral — and for the sun pivot that is a panel frozen aimed at
            //     where the Sun was an orbit ago. Launch dust stops here for the same reason.
            RestoreSynthesizedMotionNeutralPoses(state);
            if (state.launchDustInfo?.particles != null && state.launchDustInfo.particles.isPlaying)
                state.launchDustInfo.particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            // S4: the EVA plume's Unity half of the same reset. Clear as well as stop, so no
            // in-flight particles from the previous cycle survive into the new one.
            if (state.evaJetpackPlumeInfo?.particles != null
                && state.evaJetpackPlumeInfo.particles.isPlaying)
            {
                state.evaJetpackPlumeInfo.particles.Stop(
                    true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            // 3f. Re-apply the M1 snapshot baselines the stow/cold/off steps above just
            //     reverted, so cycle N+1 restarts from the RECORDED look rather than the
            //     all-stowed prefab look — the same layering a fresh spawn gets
            //     (PopulateGhostInfoDictionaries applies the stow baselines, then this).
            //     The prefix replay in the following PrimeLoadedGhostForPlaybackUT then
            //     layers the cycle's own events on top, exactly as at spawn.
            //     rateLimitLog: this fires per ghost per cycle here, not once per spawn.
            ApplySnapshotBaselines(state, rateLimitLog: true);

            if (roboticsRestored > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", "loop-robotic-restore",
                    $"Loop cycle: restored {roboticsRestored} servo(s) to their spawn pose " +
                    $"(vessel='{state.vesselName ?? "unknown"}')", 1.0);

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
            // P8 step 1: allocated lazily on the first CONSUMED event, so the common
            // frame (cursor already caught up, loop body never entered) pays nothing.
            GhostPartEventApplyTally tally = null;

            while (evtIdx < rec.PartEvents.Count && rec.PartEvents[evtIdx].ut <= currentUT)
            {
                var evt = rec.PartEvents[evtIdx];
                if (tally == null) tally = new GhostPartEventApplyTally();
                switch (evt.eventType)
                {
                    case PartEventType.Decoupled:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Visibility,
                            evt.partPersistentId,
                            ApplyDecoupledPartEvent(
                                state,
                                ghost,
                                logicalPartIds,
                                tree,
                                evt,
                                allowTransientEffects,
                                ref visibilityChanged,
                                ref needsReentryMeshRebuild));
                        break;
                    case PartEventType.Destroyed:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Visibility,
                            evt.partPersistentId,
                            ApplyDestroyedPartEvent(
                                state,
                                ghost,
                                logicalPartIds,
                                evt,
                                allowTransientEffects,
                                ref visibilityChanged,
                                ref needsReentryMeshRebuild));
                        break;
                    case PartEventType.ParachuteCut:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Parachute,
                            evt.partPersistentId,
                            ApplyParachuteCutEvent(state, evt.partPersistentId));
                        break;
                    case PartEventType.ParachuteRepacked:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Parachute,
                            evt.partPersistentId,
                            ApplyParachuteRepackedEvent(state, evt.partPersistentId));
                        break;
                    case PartEventType.ShroudJettisoned:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.JettisonPanel,
                            evt.partPersistentId,
                            ApplyJettisonPanelStateWithOutcome(state, evt, jettisoned: true));
                        break;
                    case PartEventType.ParachuteDestroyed:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Visibility,
                            evt.partPersistentId,
                            ApplyParachuteDestroyedEvent(
                                state,
                                ghost,
                                logicalPartIds,
                                evt.partPersistentId,
                                ref visibilityChanged));
                        break;
                    case PartEventType.ParachuteSemiDeployed:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Parachute,
                            evt.partPersistentId,
                            ApplyParachuteSemiDeployedEvent(state, evt.partPersistentId));
                        break;
                    case PartEventType.ParachuteDeployed:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Parachute,
                            evt.partPersistentId,
                            ApplyParachuteDeployedEvent(state, ghost, evt.partPersistentId));
                        break;
                    case PartEventType.EngineIgnited:
                        // Use at least a minimum emission on ignition (#165) — older
                        // recordings may contain seed events with throttle=0 from before
                        // the recording-side fix. The 0.01 floor ensures plume visibility
                        // for backward compatibility. New recordings skip zero-throttle
                        // engine seeds entirely (PartStateSeeder.EmitEngineSeedEvents).
                        RecordEngineEvent(
                            tally, state, evt, System.Math.Max(evt.value, 0.01f),
                            ref audioPowerTouched);
                        break;
                    case PartEventType.EngineShutdown:
                        RecordEngineEvent(tally, state, evt, 0f, ref audioPowerTouched);
                        break;
                    case PartEventType.EngineThrottle:
                        RecordEngineEvent(tally, state, evt, evt.value, ref audioPowerTouched);
                        break;
                    // S2: the four deployable-family event pairs take the ANIMATED path
                    // (immediate: false). Every BASELINE caller keeps the snap overload; the
                    // distinction is "the recording says this moved now" vs "put the ghost into
                    // the state it spawned in".
                    case PartEventType.DeployableExtended:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Deployable,
                            evt.partPersistentId,
                            ApplyDeployableStateWithOutcome(
                                state, evt, deployed: true, immediate: false));
                        break;
                    case PartEventType.DeployableRetracted:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Deployable,
                            evt.partPersistentId,
                            ApplyDeployableStateWithOutcome(
                                state, evt, deployed: false, immediate: false));
                        break;
                    // S6: a break is a SNAP, never an animation. The panel is gone in the frame it
                    // broke (stock flings the subtree off as debris), so there is no pose to
                    // interpolate toward and no `immediate` distinction to make.
                    case PartEventType.DeployableBroken:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Deployable,
                            evt.partPersistentId,
                            ApplyDeployableBrokenStateWithOutcome(
                                state, evt.partPersistentId, broken: true));
                        break;
                    // S7: evt.ut is the loop's phase origin, which is what makes a scrubbed or
                    // looping replay land on the SAME pose for the same recorded moment.
                    case PartEventType.ConverterActivated:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.ConverterLoop,
                            evt.partPersistentId,
                            ApplyConverterLoopStateWithOutcome(
                                state, evt.partPersistentId, active: true, activeSinceUT: evt.ut));
                        break;
                    case PartEventType.ConverterDeactivated:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.ConverterLoop,
                            evt.partPersistentId,
                            ApplyConverterLoopStateWithOutcome(
                                state, evt.partPersistentId, active: false, activeSinceUT: evt.ut));
                        break;
                    // S4: all six EVA members share one applier so the plume gate lives in one
                    // place. The RAGDOLL pair is recorded and applied but renders no POSE - it
                    // gates the plume and marks the timeline (see PartEventType.EvaRagdollStarted).
                    case PartEventType.EvaJetpackDeployed:
                    case PartEventType.EvaJetpackStowed:
                    case PartEventType.EvaJetpackThrustStarted:
                    case PartEventType.EvaJetpackThrustStopped:
                    case PartEventType.EvaRagdollStarted:
                    case PartEventType.EvaRagdollEnded:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Eva,
                            evt.partPersistentId,
                            ApplyEvaStateWithOutcome(state, evt.eventType));
                        break;
                    case PartEventType.ThermalAnimationHot:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Heat,
                            evt.partPersistentId,
                            ApplyHeatStateWithOutcome(state, evt, HeatLevel.Hot));
                        break;
                    case PartEventType.ThermalAnimationMedium:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Heat,
                            evt.partPersistentId,
                            ApplyHeatStateWithOutcome(state, evt, HeatLevel.Medium));
                        break;
                    case PartEventType.ThermalAnimationCold:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Heat,
                            evt.partPersistentId,
                            ApplyHeatStateWithOutcome(state, evt, HeatLevel.Cold));
                        break;
                    case PartEventType.LightOn:
                        RecordLightPowerEvent(tally, state, evt, on: true);
                        break;
                    case PartEventType.LightOff:
                        RecordLightPowerEvent(tally, state, evt, on: false);
                        break;
                    case PartEventType.LightBlinkEnabled:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.BlinkState,
                            evt.partPersistentId,
                            ApplyLightBlinkModeEventWithOutcome(
                                state, evt.partPersistentId, enabled: true, evt.value));
                        break;
                    case PartEventType.LightBlinkDisabled:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.BlinkState,
                            evt.partPersistentId,
                            ApplyLightBlinkModeEventWithOutcome(
                                state, evt.partPersistentId, enabled: false, evt.value));
                        break;
                    case PartEventType.LightBlinkRate:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.BlinkState,
                            evt.partPersistentId,
                            ApplyLightBlinkRateEventWithOutcome(
                                state, evt.partPersistentId, evt.value));
                        break;
                    case PartEventType.GearDeployed:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Deployable,
                            evt.partPersistentId,
                            ApplyDeployableStateWithOutcome(
                                state, evt, deployed: true, immediate: false));
                        break;
                    case PartEventType.GearRetracted:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Deployable,
                            evt.partPersistentId,
                            ApplyDeployableStateWithOutcome(
                                state, evt, deployed: false, immediate: false));
                        break;
                    case PartEventType.CargoBayOpened:
                        RecordCargoBayEvent(tally, state, evt, open: true);
                        break;
                    case PartEventType.CargoBayClosed:
                        RecordCargoBayEvent(tally, state, evt, open: false);
                        break;
                    case PartEventType.FairingJettisoned:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Fairing,
                            evt.partPersistentId,
                            ApplyFairingJettisonedState(state, evt.partPersistentId));
                        break;
                    case PartEventType.RCSActivated:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.RcsFx,
                            evt.partPersistentId,
                            SetRcsEmissionWithOutcome(state, evt, evt.value));
                        break;
                    case PartEventType.RCSStopped:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.RcsFx,
                            evt.partPersistentId,
                            SetRcsEmissionWithOutcome(state, evt, 0f));
                        break;
                    case PartEventType.RCSThrottle:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.RcsFx,
                            evt.partPersistentId,
                            SetRcsEmissionWithOutcome(state, evt, evt.value));
                        break;
                    case PartEventType.RoboticMotionStarted:
                    case PartEventType.RoboticPositionSample:
                    case PartEventType.RoboticMotionStopped:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Robotic,
                            evt.partPersistentId,
                            ApplyRoboticEvent(state, evt, currentUT));
                        break;
                    case PartEventType.InventoryPartPlaced:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Inventory,
                            evt.partPersistentId,
                            ApplyInventoryPartPlacedEvent(
                                state,
                                logicalPartIds,
                                evt.partPersistentId,
                                ref placedTargetPartIds,
                                ref visibilityChanged));
                        break;
                    case PartEventType.InventoryPartRemoved:
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Inventory,
                            evt.partPersistentId,
                            ApplyInventoryPartRemovedEvent(
                                state,
                                logicalPartIds,
                                evt.partPersistentId,
                                ref visibilityChanged));
                        break;
                    default:
                        // Docked / Undocked are chain-segment boundary markers with no ghost
                        // pose, and an older build reading a NEWER sidecar can materialise an
                        // undefined member here (the binary reader raw-casts). Both land in
                        // this arm; naming them in the log is the whole point of the
                        // unhandled-event-type reason class.
                        tally.Record(
                            evt.eventType,
                            GhostPartEventSurface.Visibility,
                            evt.partPersistentId,
                            GhostPartEventOutcome.UnhandledEventType);
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
            // P8 step 1: the per-family breakdown the aggregate line above cannot carry.
            // One rate-limited line per (recording, family, surface) actually touched.
            if (tally != null) tally.Flush(recIdx);
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
            // Retained so every caller (flight preview, the KSC-scene ghost paths) keeps driving
            // continuous robotic motion exactly as before. The flight engine ALSO calls this from
            // ApplyFrameVisuals, because this method early-returns when a recording has no
            // PartEvents at all and wheel spin must not depend on that. Calling it twice in one
            // frame is a no-op: each pass stamps info.lastUpdateUT = currentUT, so the second sees
            // deltaSeconds == 0 and skips.
            //
            // DELIBERATE ASYMMETRY, do not "fix" by adding a second call to the other scenes: only
            // the flight engine drives wheel spin unconditionally. A KSC-scene ghost never spins its
            // wheels at all (ParsekKSC never calls SetInterpolated, so lastInterpolatedVelocity
            // stays zero and TryResolveWheelGroundSpeedInputs declines), and the flight PREVIEW
            // ghost spins only for a recording that carries at least one part event, because a
            // zero-event recording early-returns above before reaching here. Both are acceptable:
            // KSC is a static parked-craft display, and preview is a scrubbing aid, not the replay.
            UpdateActiveRobotics(state, currentUT, rec.TrackSections);
            // S2, and retained here for exactly the reason UpdateActiveRobotics is: the flight
            // engine ALSO calls this from ApplyFrameVisuals, but the KSC-scene and flight-PREVIEW
            // ghost paths reach ApplyPartEvents and nothing else. Without this call a panel whose
            // deploy event just fired on one of those paths would arm a transition and then sit at
            // its stowed pose forever, which is worse than the snap S2 replaces. Calling it twice
            // in one frame is a no-op: the progress is a pure function of the event UT, so the
            // second pass recomputes the same fraction and writes the same pose.
            UpdateActiveDeployables(state, currentUT);
        }

        /// <summary>
        /// P8 step 1: the engine arms drive TWO surfaces (particle FX and the ghost's
        /// looped audio) and the audio half already had its own boolean, which the
        /// switch consumed for the playback-cap enforcement and then threw away. Both
        /// are tallied so a silent-but-visible plume (or the reverse) is readable.
        /// </summary>
        private static void RecordEngineEvent(
            GhostPartEventApplyTally tally,
            GhostPlaybackState state,
            PartEvent evt,
            float power,
            ref bool audioPowerTouched)
        {
            tally.Record(
                evt.eventType,
                GhostPartEventSurface.EngineFx,
                evt.partPersistentId,
                SetEngineEmissionWithOutcome(state, evt, power));

            // The classifier mirrors SetEngineAudio's only two declining paths, so the
            // recorded outcome and the boolean below cannot disagree - it is there to
            // name WHICH of the two, which the boolean cannot.
            GhostPartEventOutcome audioOutcome = ClassifyEngineAudioApply(state, evt);
            if (SetEngineAudio(state, evt, power, enforcePlaybackCap: false))
                audioPowerTouched = true;
            tally.Record(
                evt.eventType,
                GhostPartEventSurface.EngineAudio,
                evt.partPersistentId,
                audioOutcome);
        }

        /// <summary>
        /// P8 step 1: a light event's two independent surfaces, tallied separately.
        /// See <see cref="ApplyLightPowerEventWithOutcomes"/> for why one boolean for
        /// the pair was the thing that made the colour-changer half unmeasurable.
        /// </summary>
        private static void RecordLightPowerEvent(
            GhostPartEventApplyTally tally,
            GhostPlaybackState state,
            PartEvent evt,
            bool on)
        {
            ApplyLightPowerEventWithOutcomes(
                state,
                evt.partPersistentId,
                on,
                out GhostPartEventOutcome lightOutcome,
                out GhostPartEventOutcome colorChangerOutcome);
            tally.Record(
                evt.eventType, GhostPartEventSurface.Light, evt.partPersistentId, lightOutcome);
            tally.Record(
                evt.eventType, GhostPartEventSurface.ColorChanger, evt.partPersistentId,
                colorChangerOutcome);
        }

        /// <summary>
        /// P8 step 1: the cargo-bay CASCADE. The jettison arm is tallied only when the
        /// cascade actually reached it, so a bay whose doors animated does not also
        /// report a phantom jettison skip.
        /// </summary>
        private static void RecordCargoBayEvent(
            GhostPartEventApplyTally tally,
            GhostPlaybackState state,
            PartEvent evt,
            bool open)
        {
            ApplyCargoBayStateWithOutcomes(
                state,
                evt,
                open,
                immediate: false,
                out GhostPartEventOutcome deployableOutcome,
                out GhostPartEventOutcome jettisonOutcome,
                out bool jettisonArmReached);
            tally.Record(
                evt.eventType, GhostPartEventSurface.Deployable, evt.partPersistentId,
                deployableOutcome);
            if (jettisonArmReached)
                tally.Record(
                    evt.eventType, GhostPartEventSurface.JettisonPanel, evt.partPersistentId,
                    jettisonOutcome);
        }

        /// <summary>
        /// P8 step 1: the FairingJettisoned arm, lifted out of the switch so its three
        /// distinct early-outs become named reason classes instead of one silent
        /// nested-if fall-through.
        /// </summary>
        internal static GhostPartEventOutcome ApplyFairingJettisonedState(
            GhostPlaybackState state, uint partPersistentId)
        {
            GhostPartEventOutcome precondition = ClassifyFairingJettisonApply(state, partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            state.fairingInfos[partPersistentId].fairingMeshObject.SetActive(false);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>P8 step 1 precondition classifier (pure).</summary>
        internal static GhostPartEventOutcome ClassifyFairingJettisonApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.fairingInfos == null) return GhostPartEventOutcome.NoFamilyState;
            FairingGhostInfo fInfo;
            if (!state.fairingInfos.TryGetValue(partPersistentId, out fInfo) || fInfo == null)
                return GhostPartEventOutcome.NoInfoForPart;
            if (fInfo.fairingMeshObject == null)
                return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// P8 step 1: unconditional - the subtree is hidden and dropped from logical
        /// presence on every path - so Applied on the VISIBILITY surface is always the
        /// honest report. There is no ghost lookup here that can miss.
        /// </summary>
        private static GhostPartEventOutcome ApplyDecoupledPartEvent(
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
            return GhostPartEventOutcome.Applied;
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

        /// <summary>
        /// P8 step 1: unconditional, like the decouple sibling - Applied on the
        /// VISIBILITY surface.
        /// </summary>
        private static GhostPartEventOutcome ApplyDestroyedPartEvent(
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
            return GhostPartEventOutcome.Applied;
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

        /// <summary>
        /// Applies the semi-deployed (drogue-stage) canopy pose. Extracted from the
        /// ApplyPartEvents switch so the M1 snapshot baseline can reach the same code —
        /// a pack whose snapshot says SEMIDEPLOYED must render half-open at spawn, not
        /// stowed. No-ops unless the build sampled a semi-deployed pose for this pack.
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteSemiDeployedEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            if (state?.parachuteInfos == null) return GhostPartEventOutcome.NoFamilyState;

            ParachuteGhostInfo semiInfo;
            if (!state.parachuteInfos.TryGetValue(partPersistentId, out semiInfo)
                || semiInfo == null)
                return GhostPartEventOutcome.NoInfoForPart;
            if (semiInfo.canopyTransform == null)
                return GhostPartEventOutcome.NoResolvedVisual;
            // The semi-deployed (drogue) pose is optional at build time; a pack whose
            // prefab exposed no streamer stage has nothing to interpolate toward, which
            // is a BUILD fact and not an apply failure.
            if (!semiInfo.semiDeployedSampled)
                return GhostPartEventOutcome.PoseNotSampled;

            semiInfo.canopyTransform.localScale = semiInfo.semiDeployedCanopyScale;
            semiInfo.canopyTransform.localPosition = semiInfo.semiDeployedCanopyPos;
            semiInfo.canopyTransform.localRotation = semiInfo.semiDeployedCanopyRot;
            if (semiInfo.capTransform != null)
                semiInfo.capTransform.gameObject.SetActive(false);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// The cap-visibility half of every parachute event's ghost pose, pulled out of the
        /// Transform-touching handlers so it is assertable without a Unity scene. Returns false for
        /// event types that say nothing about a cap.
        ///
        /// The cap is the whole bug this table exists to pin down. A stock chute's cap is the nose
        /// cone that covers the packed canopy: DEPLOY blows it off, and stock <c>Repack()</c> puts it
        /// back (<c>cap.gameObject.SetActive(true)</c>). Playback had a cut hide the cap with nothing
        /// that ever re-showed it, so once the recorder mistook a repack for a cut the chute rendered
        /// as an empty can for the rest of the recording — visually permanent, and faithfully
        /// reproduced on every replay.
        /// </summary>
        internal static bool TryResolveParachuteCapActive(PartEventType type, out bool capActive)
        {
            switch (type)
            {
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteDestroyed:
                    capActive = false;
                    return true;
                case PartEventType.ParachuteRepacked:
                    // The only parachute event that puts the cap BACK.
                    capActive = true;
                    return true;
                default:
                    capActive = false;
                    return false;
            }
        }

        private static GhostPartEventOutcome ApplyParachuteCutEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            GhostPartEventOutcome outcome = GhostPartEventOutcome.NoFamilyState;
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo cutInfo;
                if (!state.parachuteInfos.TryGetValue(partPersistentId, out cutInfo))
                {
                    outcome = GhostPartEventOutcome.NoInfoForPart;
                }
                else
                {
                    bool touched = false;
                    if (cutInfo.canopyTransform != null)
                    {
                        cutInfo.canopyTransform.localScale = Vector3.zero;
                        touched = true;
                    }
                    if (cutInfo.capTransform != null &&
                        TryResolveParachuteCapActive(PartEventType.ParachuteCut, out bool cutCapActive))
                    {
                        cutInfo.capTransform.gameObject.SetActive(cutCapActive);
                        touched = true;
                    }
                    outcome = touched
                        ? GhostPartEventOutcome.Applied
                        : GhostPartEventOutcome.NoResolvedVisual;
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
            return outcome;
        }

        /// <summary>
        /// Applies a ParachuteRepacked event: the exact inverse of
        /// <see cref="ApplyParachuteCutEvent"/>, and a faithful mirror of what stock
        /// <c>ModuleParachute.Repack()</c> does to the real part
        /// (<c>cap.gameObject.SetActive(true)</c> + <c>canopy.gameObject.SetActive(false)</c>).
        ///
        /// Canopy back to its captured stowed pose (hidden), and — the part that matters — the CAP
        /// RESTORED. The cut hid the cap permanently, which is why a repack replayed as a cut left
        /// the chute rendering as an empty can for the rest of the recording.
        ///
        /// The fake-canopy destroy is kept: a stowed chute has no canopy, so if the deploy fell back
        /// to a fake canopy sphere it must be gone here too. DestroyFakeCanopy is idempotent, so
        /// this is safe whether the preceding cut already removed it or the repack arrives without
        /// one (the ordinary real-canopy path).
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteRepackedEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            GhostPartEventOutcome outcome = GhostPartEventOutcome.NoFamilyState;
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo repackInfo;
                if (!state.parachuteInfos.TryGetValue(partPersistentId, out repackInfo))
                {
                    outcome = GhostPartEventOutcome.NoInfoForPart;
                }
                else
                {
                    bool touched = false;
                    if (repackInfo.canopyTransform != null)
                    {
                        repackInfo.canopyTransform.localScale = repackInfo.stowedCanopyScale;
                        repackInfo.canopyTransform.localPosition = repackInfo.stowedCanopyPos;
                        repackInfo.canopyTransform.localRotation = repackInfo.stowedCanopyRot;
                        touched = true;
                    }
                    if (repackInfo.capTransform != null &&
                        TryResolveParachuteCapActive(PartEventType.ParachuteRepacked, out bool repackCapActive))
                    {
                        repackInfo.capTransform.gameObject.SetActive(repackCapActive);
                        touched = true;
                    }
                    outcome = touched
                        ? GhostPartEventOutcome.Applied
                        : GhostPartEventOutcome.NoResolvedVisual;
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
            return outcome;
        }

        /// <summary>
        /// P8 step 1: unconditional, so it reports Applied on the VISIBILITY surface -
        /// the part is hidden and dropped from logical presence whatever the ghost
        /// carried for it. The canopy cleanup above is best-effort tidying of a visual
        /// that is about to be hidden anyway, not the outcome.
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteDestroyedEvent(
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
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// P8 step 1: unconditional (the ghost part is activated and joins logical
        /// presence whatever else the ghost carries), so Applied on the INVENTORY
        /// surface is always the honest report.
        /// </summary>
        private static GhostPartEventOutcome ApplyInventoryPartPlacedEvent(
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
            return GhostPartEventOutcome.Applied;
        }

        private static GhostPartEventOutcome ApplyInventoryPartRemovedEvent(
            GhostPlaybackState state,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref bool visibilityChanged)
        {
            SetGhostPartActive(state, partPersistentId, false);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, partPersistentId, null);
            visibilityChanged = true;
            return GhostPartEventOutcome.Applied;
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
        /// <summary>
        /// P8 step 1: this family has a FALLBACK rather than a skip - a pack whose ghost
        /// carries no canopy transform gets a fabricated sphere canopy - so the only
        /// non-applied outcome is "neither the real canopy nor the fake one resolved".
        /// </summary>
        private static GhostPartEventOutcome ApplyParachuteDeployedEvent(
            GhostPlaybackState state, GameObject ghost, uint partPersistentId)
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
                    return GhostPartEventOutcome.Applied;
                }
                return GhostPartEventOutcome.NoResolvedVisual;
            }

            return GhostPartEventOutcome.Applied;
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
            SetEngineEmissionWithOutcome(state, evt, power);
        }

        /// <summary>
        /// P8 step 1: the real body, reporting WHICH early-return it took so
        /// <see cref="ApplyPartEvents"/> can name the skip class. The historical void
        /// signature above is a thin wrapper, so no existing caller changed and there
        /// is no second copy of these guards to drift out of step.
        ///
        /// "Applied" here means the ghost's engine info for this (pid, moduleIndex)
        /// took the new power. Whether any PARTICLE moved is a separate, already-logged
        /// fact (`FX magnitude (engine) pid=...`) and is deliberately not folded in:
        /// that line is suppressed when nothing scaled, so a reader who wants emitter
        /// proof reads it, and a reader who wants "the event reached a live engine
        /// info" reads this one.
        /// </summary>
        /// <summary>
        /// P8 step 1: the applier's guards, as a PURE function. Two reasons the split is
        /// this shape rather than a bool inside the handler:
        ///
        /// 1. It is the SAME code path - the handler's early return IS this call, so
        ///    there is no second copy of a predicate to drift (the standing house rule
        ///    about re-deriving scope rather than duplicating it).
        /// 2. It is the only shape that is testable at all. A method whose BODY names a
        ///    Unity ECall (a Transform / GameObject / Light write) cannot even be JIT'd
        ///    under xUnit - it throws `SecurityException: ECall methods must be packaged
        ///    into a system module` before the first guard runs - so a guard living
        ///    inside such a body is unreachable from a headless test. A classifier that
        ///    only reads managed dictionaries JITs and runs.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyEngineEmissionApply(
            GhostPlaybackState state, PartEvent evt)
        {
            if (state.engineInfos == null) return GhostPartEventOutcome.NoFamilyState;
            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            return state.engineInfos.ContainsKey(key)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoInfoForPart;
        }

        internal static GhostPartEventOutcome SetEngineEmissionWithOutcome(
            GhostPlaybackState state, PartEvent evt, float power)
        {
            GhostPartEventOutcome precondition = ClassifyEngineEmissionApply(state, evt);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            EngineGhostInfo info = state.engineInfos[key];

            info.currentPower = power;

            // S1: MAGNITUDE. Scale the plume to the throttle before flipping the boolean gate.
            // Only on the way UP (power > 0): the fields are persistent, and the one caller that
            // does NOT route through here — RestoreAllRcsEmissions' sibling for RCS, and
            // RestoreActiveEngineFx here — relies on the last active scale still being written
            // when it re-enables emitters directly. Writing a zero-scale at power 0 would restore
            // an invisible plume.
            if (power > 0f)
            {
                ApplyFxMagnitudeScale(
                    info.kspEmitters, info.particleSystems, info.particleBaselines,
                    ComputeFxMagnitudeRatio(
                        ComputeEngineEmissionRate(info.emissionCurve, power),
                        ComputeEngineEmissionRate(info.emissionCurve, 1f), power),
                    ComputeFxMagnitudeRatio(
                        ComputeEngineSpeed(info.speedCurve, power),
                        ComputeEngineSpeed(info.speedCurve, 1f), power),
                    "engine", info.partPersistentId, info.moduleIndex, power);
            }

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

            return GhostPartEventOutcome.Applied;
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
        /// <summary>
        /// P8 step 1 precondition classifier (pure). Mirrors this method's only two
        /// `return false` paths exactly - a missing audio dictionary and a missing entry
        /// for the engine key - so the family line can name which, rather than folding
        /// both into a bare boolean. Everything past those two guards returns true.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyEngineAudioApply(
            GhostPlaybackState state, PartEvent evt)
        {
            if (state.audioInfos == null) return GhostPartEventOutcome.NoFamilyState;
            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            return state.audioInfos.ContainsKey(key)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoInfoForPart;
        }

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
            SetRcsEmissionWithOutcome(state, evt, power);
        }

        /// <summary>
        /// P8 step 1: the RCS mirror of <see cref="SetEngineEmissionWithOutcome"/>.
        /// Same wrapper shape, same meaning of "applied" (the ghost's RCS info for this
        /// engine key took the new power; emitter movement is the separate
        /// `FX magnitude (rcs) pid=...` line).
        /// </summary>
        /// <summary>P8 step 1: the RCS sibling of <see cref="ClassifyEngineEmissionApply"/>.</summary>
        internal static GhostPartEventOutcome ClassifyRcsEmissionApply(
            GhostPlaybackState state, PartEvent evt)
        {
            if (state.rcsInfos == null) return GhostPartEventOutcome.NoFamilyState;
            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            return state.rcsInfos.ContainsKey(key)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoInfoForPart;
        }

        internal static GhostPartEventOutcome SetRcsEmissionWithOutcome(
            GhostPlaybackState state, PartEvent evt, float power)
        {
            GhostPartEventOutcome precondition = ClassifyRcsEmissionApply(state, evt);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            RcsGhostInfo info = state.rcsInfos[key];

            info.currentPower = power;

            // S1: MAGNITUDE. Same choke point as the engine side, but the ratio runs through
            // ComputeScaledRcsEmissionRate / ComputeScaledRcsSpeed so the showcase visibility
            // FLOORS survive: at low power the floor lifts the numerator, which lifts the ratio,
            // which is exactly what "stays visible on a showcase rig" means once the write is
            // relative rather than absolute.
            if (power > 0f)
            {
                ApplyFxMagnitudeScale(
                    info.kspEmitters, info.particleSystems, info.particleBaselines,
                    ComputeFxMagnitudeRatio(
                        ComputeScaledRcsEmissionRate(info.emissionCurve, power, info.emissionScale),
                        ComputeScaledRcsEmissionRate(info.emissionCurve, 1f, info.emissionScale),
                        power),
                    ComputeFxMagnitudeRatio(
                        ComputeScaledRcsSpeed(info.speedCurve, power, info.speedScale),
                        ComputeScaledRcsSpeed(info.speedCurve, 1f, info.speedScale),
                        power),
                    "rcs", info.partPersistentId, info.moduleIndex, power);
            }

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

            return GhostPartEventOutcome.Applied;
        }

        #region S1 — plume magnitude (ratio of captured baseline)

        /// <summary>
        /// The lowest fraction of the full-power baseline a LIT plume is allowed to shrink to.
        /// A plume at 5% throttle must still read as "this engine is running" at playback speed —
        /// the binary on/off it replaces was at least legible. Also the floor that keeps a curve
        /// with a near-zero low end from producing a plume made of nothing.
        /// </summary>
        internal const float FxMagnitudeMinVisibleRatio = 0.2f;

        /// <summary>
        /// The engine siblings of <see cref="ComputeScaledRcsEmissionRate"/> /
        /// <see cref="ComputeScaledRcsSpeed"/>: same curve-else-linear fallback, no showcase scale
        /// and therefore no floor (engine FX carry no per-asset scale field). Kept as named
        /// functions rather than inlined curve reads so the RATIO's numerator and denominator are
        /// provably the same expression evaluated at two powers.
        /// </summary>
        internal static float ComputeEngineEmissionRate(FloatCurve emissionCurve, float power)
        {
            if (power <= 0f) return 0f;
            return emissionCurve != null ? emissionCurve.Evaluate(power) : power * 100f;
        }

        internal static float ComputeEngineSpeed(FloatCurve speedCurve, float power)
        {
            if (power <= 0f) return 0f;
            return speedCurve != null ? speedCurve.Evaluate(power) : power * 10f;
        }

        /// <summary>
        /// Turns a magnitude at the current power and the same magnitude at FULL power into the
        /// fraction of the captured build-time baseline to write. Pure floats in, pure float out —
        /// no Unity call, no FloatCurve — so the whole decision is headless-testable and the
        /// callers own the (KSP-side) curve evaluation.
        ///
        /// Degradation is deliberate and one-directional: anything that makes the ratio
        /// unknowable (no usable full-power reference, a non-finite reading, a negative magnitude)
        /// answers 1.0, i.e. "write the baseline back unchanged", which is byte-for-byte the
        /// pre-S1 boolean behaviour. It never answers 0 for a lit engine.
        /// </summary>
        internal static float ComputeFxMagnitudeRatio(
            float magnitudeAtPower, float magnitudeAtFullPower, float power)
        {
            if (power <= 0f) return 0f;
            if (power >= 1f) return 1f;

            if (float.IsNaN(magnitudeAtPower) || float.IsInfinity(magnitudeAtPower)
                || float.IsNaN(magnitudeAtFullPower) || float.IsInfinity(magnitudeAtFullPower)
                || magnitudeAtFullPower <= 0f || magnitudeAtPower < 0f)
            {
                return 1f;
            }

            float ratio = magnitudeAtPower / magnitudeAtFullPower;
            if (ratio >= 1f) return 1f;
            return Math.Max(ratio, FxMagnitudeMinVisibleRatio);
        }

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

        internal static void ResetFxMagnitudeWriteFailureCountForTesting()
            => FxMagnitudeWriteFailureCount = 0;

        /// <summary>
        /// Boxes a scaled magnitude as the EXACT type of the field it is about to be written to.
        ///
        /// THE 2026-08-11 DEFECT. <c>KSPParticleEmitter.minEmission</c> and <c>maxEmission</c> are
        /// declared <c>int</c> (verified by decompiling Assembly-CSharp: <c>minSize</c>/<c>maxSize</c>
        /// are float, <c>localVelocity</c> is Vector3, but the two EMISSION-RATE fields are int).
        /// <c>FieldInfo.SetValue</c> does no numeric conversion — it demands an instance of the
        /// field's own type — so handing it a boxed <c>float</c> threw
        /// "Object of type 'System.Single' cannot be converted to type 'System.Int32'" on every
        /// emitter of every engine and thruster, making the whole of S1 a silent no-op in game.
        ///
        /// Rounding is nearest-with-a-NONZERO-FLOOR rather than a truncation, and that floor is the
        /// contract, not a nicety: <see cref="ComputeFxMagnitudeRatio"/> guarantees it never answers
        /// zero for a lit engine, and truncating 0.4 particles/s to 0 would break that guarantee at
        /// the quantisation boundary for exactly the low-rate dense assets (ReStock SRB smoke) the
        /// ratio was tuned around. The floor keeps the SIGN, because stock reads a negative
        /// <c>maxEmission</c> as "this emitter does not emit" (<c>KSPParticleEmitter.Update</c>
        /// early-returns on it) and flipping that sentinel positive would light a dead emitter.
        ///
        /// Returns false for a type this cannot express (and for a non-finite value), which the
        /// callers degrade into "leave the field at its baseline" — the pre-S1 boolean plume.
        /// </summary>
        internal static bool TryConvertMagnitudeForField(
            Type fieldType, float value, out object converted)
        {
            converted = null;
            if (fieldType == null || float.IsNaN(value) || float.IsInfinity(value))
                return false;

            if (fieldType == typeof(float)) { converted = value; return true; }
            if (fieldType == typeof(double)) { converted = (double)value; return true; }

            if (fieldType == typeof(int) || fieldType == typeof(uint)
                || fieldType == typeof(long) || fieldType == typeof(ulong)
                || fieldType == typeof(short) || fieldType == typeof(ushort)
                || fieldType == typeof(byte) || fieldType == typeof(sbyte))
            {
                double rounded = Math.Round((double)value, MidpointRounding.AwayFromZero);
                if (rounded == 0.0 && value > 0f) rounded = 1.0;
                else if (rounded == 0.0 && value < 0f) rounded = -1.0;

                try
                {
                    converted = Convert.ChangeType(rounded, fieldType, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    // Out of the target type's range, or a negative into an unsigned field. Both
                    // degrade to "no scaling" rather than to a wrapped-around magnitude.
                    converted = null;
                    return false;
                }
                return true;
            }

            return false;
        }

        /// <summary>True when <see cref="TryConvertMagnitudeForField"/> can express a scaled scalar
        /// magnitude as <paramref name="fieldType"/>. Used at CAPTURE time so an unwritable field is
        /// dropped before it ever reaches the applier.</summary>
        internal static bool IsSupportedMagnitudeScalarFieldType(Type fieldType)
            => TryConvertMagnitudeForField(fieldType, 1f, out _);

        /// <summary>The vector magnitude surface is exactly one type; anything else is a KSP version
        /// having changed the field out from under us and degrades to "no scaling".</summary>
        internal static bool IsSupportedMagnitudeVectorFieldType(Type fieldType)
            => fieldType == typeof(Vector3);

        private static bool TryWriteMagnitudeScalarField(
            System.Reflection.FieldInfo field, object target, float value, ref string failure)
        {
            if (field == null) return false;
            if (!TryConvertMagnitudeForField(field.FieldType, value, out object boxed))
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) cannot hold " +
                    value.ToString("R", CultureInfo.InvariantCulture));
                return false;
            }
            try
            {
                field.SetValue(target, boxed);
                return true;
            }
            catch (Exception ex)
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) write threw: {ex.Message}");
                return false;
            }
        }

        private static bool TryWriteMagnitudeVectorField(
            System.Reflection.FieldInfo field, object target, Vector3 value, ref string failure)
        {
            if (field == null) return false;
            if (!IsSupportedMagnitudeVectorFieldType(field.FieldType))
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) is not a Vector3");
                return false;
            }
            try
            {
                field.SetValue(target, value);
                return true;
            }
            catch (Exception ex)
            {
                failure = AppendFailure(failure,
                    $"{field.Name} ({field.FieldType.Name}) write threw: {ex.Message}");
                return false;
            }
        }

        private static string AppendFailure(string existing, string addition)
            => string.IsNullOrEmpty(existing) ? addition : existing + "; " + addition;

        /// <summary>
        /// The emitter <c>localVelocity</c> to write for one throttle ratio: <c>baseline * ratio</c>,
        /// except that a WORLD-SPACE emitter never drops below the minimum-flow floor its baseline
        /// was given at build time.
        ///
        /// Why the special case. <c>GhostVisualBuilder.ApplyWorldSpaceEmitterVelocityFloor</c> lifts a
        /// near-static world-space emitter to <c>WorldSpaceEmitterFloorSpeed</c> (6 m/s) precisely
        /// because such an asset paints its trail by MOTION, and a ghost that is standing still would
        /// otherwise pool every particle at the nozzle. <c>CaptureFxMagnitudeBaselines</c> captures
        /// AFTER that lift, so the floored 6 m/s IS the baseline — and a plain ratio write at, say,
        /// 0.2 throttle would put 1.2 m/s back on the emitter, under the 4 m/s threshold the floor
        /// exists to clear, re-creating the pooling at partial throttle. Narrow but real: ReStock's
        /// world-space SRB smoke rigs at genuine partial throttle.
        ///
        /// The clamp is a floor, never a boost: the result is capped at the baseline's own magnitude,
        /// so an emitter whose real velocity was already above the floor keeps its exact ratio down to
        /// the floor and no further. Local-space emitters (every stock FX asset) are untouched, and a
        /// zero / negative ratio is left alone — the callers only scale a LIT engine, and the one
        /// place a zero would arrive is a shutdown, where the emitters are being gated off anyway.
        /// </summary>
        internal static Vector3 ScaleEmitterLocalVelocity(
            Vector3 baselineLocalVelocity, float speedRatio, bool useWorldSpace)
        {
            Vector3 scaled = baselineLocalVelocity * speedRatio;
            if (!useWorldSpace) return scaled;
            if (float.IsNaN(speedRatio) || float.IsInfinity(speedRatio) || speedRatio <= 0f)
                return scaled;

            float baselineMagnitude = baselineLocalVelocity.magnitude;
            if (baselineMagnitude <= 0.001f) return scaled;

            float floor = Math.Min(
                baselineMagnitude, GhostVisualBuilder.WorldSpaceEmitterFloorSpeed);
            if (scaled.magnitude >= floor) return scaled;

            return baselineLocalVelocity * (floor / baselineMagnitude);
        }

        /// <summary>
        /// Writes <c>baseline * ratio</c> into the two magnitude surfaces a ghost plume actually
        /// has: the <c>KSPParticleEmitter</c> clone's own rate / velocity fields (the ONLY particle
        /// creation source on a ghost — Unity's emission module is permanently off, bug #105), and
        /// the <c>ParticleSystem</c>'s speed / size multipliers.
        ///
        /// Ratio-of-baseline rather than absolute writes is what makes this composable: the #383
        /// size boost, the per-part FX tunings and the world-space velocity floor are all already
        /// baked into the captured baseline, so scaling multiplies them instead of overwriting
        /// them, and repeated events at the same power are idempotent instead of compounding.
        /// An uncaptured baseline (build failed, or a KSP version renamed a field) writes nothing.
        ///
        /// The one composition the ratio cannot express is the world-space minimum-flow floor, which
        /// is a THRESHOLD rather than a magnitude; <see cref="ScaleEmitterLocalVelocity"/> owns that
        /// exception and every other write here stays a plain ratio.
        ///
        /// EVERY write goes through <see cref="TryConvertMagnitudeForField"/>, because the emitter's
        /// fields are NOT all floats — see that method's contract.
        /// </summary>
        internal static void ApplyFxMagnitudeScale(
            List<KspEmitterRef> emitters, List<ParticleSystem> systems,
            List<GhostFxMagnitudeBaseline> baselines,
            float emissionRatio, float speedRatio,
            string kind, uint partPersistentId, int moduleIndex, float power)
        {
            int scaledEmitters = 0;
            int scaledSystems = 0;

            if (emitters != null)
            {
                for (int i = 0; i < emitters.Count; i++)
                {
                    KspEmitterRef r = emitters[i];
                    if (!r.magnitudeBaselineCaptured || r.emitter == null) continue;

                    // Each field is written INDEPENDENTLY. The pre-fix code wrote all three inside
                    // one try, so the first failure (minEmission, always) skipped the other two as
                    // collateral — the localVelocity write was never even attempted.
                    string failure = null;
                    bool wrote = TryWriteMagnitudeScalarField(
                        r.minEmissionField, r.emitter,
                        r.baselineMinEmission * emissionRatio, ref failure);
                    wrote |= TryWriteMagnitudeScalarField(
                        r.maxEmissionField, r.emitter,
                        r.baselineMaxEmission * emissionRatio, ref failure);
                    wrote |= TryWriteMagnitudeVectorField(
                        r.localVelocityField, r.emitter,
                        ScaleEmitterLocalVelocity(
                            r.baselineLocalVelocity, speedRatio, r.baselineUseWorldSpace),
                        ref failure);

                    if (wrote) scaledEmitters++;

                    if (failure != null)
                    {
                        // COUNTED as well as logged. The log line is rate-limited (a per-frame,
                        // per-emitter failure would otherwise drown the log), so the count is the
                        // only faithful measure of how wide the breakage is — and it is what the
                        // in-game plume cell quotes in its own failure message, so a red there is
                        // one read rather than a log hunt.
                        FxMagnitudeWriteFailureCount++;
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"fx-magnitude-write-fail-{kind}-{partPersistentId}-{moduleIndex}",
                            $"FX magnitude write failed ({kind} pid={partPersistentId} " +
                            $"midx={moduleIndex}): {failure}; plume stays at its baseline", 60.0);
                    }
                }
            }

            if (systems != null && baselines != null)
            {
                int count = Math.Min(systems.Count, baselines.Count);
                for (int i = 0; i < count; i++)
                {
                    ParticleSystem ps = systems[i];
                    GhostFxMagnitudeBaseline b = baselines[i];
                    if (ps == null || !b.captured) continue;
                    // Size rides the SPEED ratio on purpose - neither the engine nor the RCS path
                    // carries a separate size curve, and a throttled-down plume is shorter and
                    // thinner in the same proportion its exhaust slows. Do NOT "fix" this to the
                    // emission ratio: that one is a particle COUNT, and scaling size by it shrinks
                    // a deliberately low-rate dense asset (ReStock SRB smoke) to nothing.
                    var main = ps.main;
                    main.startSpeedMultiplier = b.startSpeedMultiplier * speedRatio;
                    main.startSizeMultiplier = b.startSizeMultiplier * speedRatio;
                    scaledSystems++;
                }
            }

            if (scaledEmitters == 0 && scaledSystems == 0)
                return;

            ParsekLog.VerboseRateLimited("GhostVisual",
                $"fx-magnitude-{kind}-{partPersistentId}-{moduleIndex}",
                $"FX magnitude ({kind}) pid={partPersistentId} midx={moduleIndex} " +
                $"power={power.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"emissionRatio={emissionRatio.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"speedRatio={speedRatio.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"emitters={scaledEmitters} systems={scaledSystems}", 10.0);
        }

        #endregion

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

        #region Wheel spin derived from ground speed

        /// <summary>
        /// Fallback rolling radius used when <c>ModuleWheelBase.radius</c> cannot be read off the
        /// prefab. 0.35 m sits in the middle of the stock rover-wheel range (RoveMax S2 is smaller,
        /// XL3 larger) so a wheel that misses its radius spins at a plausible rate rather than a
        /// wild one. Only the visual rate depends on it — nothing physical.
        /// </summary>
        internal const float DefaultWheelRadiusMeters = 0.35f;

        /// <summary>Radii at or below this are treated as unusable (divide-by-zero guard).</summary>
        internal const float MinWheelRadiusMeters = 0.01f;

        /// <summary>
        /// Ground speeds below this (m/s) count as stopped, so a parked ghost's wheels hold still
        /// instead of creeping on interpolation noise.
        /// </summary>
        internal const float WheelStationarySpeedMetersPerSecond = 0.05f;

        /// <summary>
        /// Strips the body's rotation and the radial (climb/sink) component out of a ghost's
        /// recorded world velocity, leaving velocity along the local surface — i.e. ground speed as
        /// a vector.
        ///
        /// Both subtractions are load-bearing. <c>TrajectoryPoint.velocity</c> is recorded as
        /// <c>rb_velocityD + Krakensbane.GetFrameVelocity()</c>, which is an ORBITAL/world velocity:
        /// on Kerbin's equator a vessel parked on the pad carries ~175 m/s of the planet's own
        /// rotation. Feeding that straight to a wheel would spin a stationary rover's wheels at
        /// highway speed. <paramref name="bodyFrameVelocity"/> is the rotating-frame velocity at the
        /// ghost's position (stock <c>CelestialBody.getRFrmVel</c>), which is exactly that term.
        /// Removing the component along <paramref name="up"/> then drops vertical motion, so a
        /// falling or hovering craft does not spin its wheels.
        /// </summary>
        internal static Vector3 ComputeSurfaceHorizontalVelocity(
            Vector3 worldVelocity, Vector3 bodyFrameVelocity, Vector3 up)
        {
            Vector3 surfaceVelocity = worldVelocity - bodyFrameVelocity;
            if (up.sqrMagnitude <= 1e-8f)
                return surfaceVelocity;

            Vector3 upUnit = up.normalized;
            return surfaceVelocity - upUnit * Vector3.Dot(surfaceVelocity, upUnit);
        }

        /// <summary>
        /// The world direction a wheel rolls toward when it spins POSITIVELY about
        /// <paramref name="wheelSpinAxisWorld"/>, given the local surface normal
        /// <paramref name="up"/>. Returns <see cref="Vector3.zero"/> when the axis is degenerate or
        /// parallel to up (a wheel lying flat has no rolling direction).
        ///
        /// Derivation, in Unity's left-handed convention where
        /// <c>Quaternion.AngleAxis(+t, a)</c> moves a point at offset <c>v</c> toward
        /// <c>Cross(a, v)</c> (check: <c>AngleAxis(90, up) * forward == right</c>):
        /// rolling without slip means the contact point, at offset <c>-up * R</c> from the hub, is
        /// stationary against the ground, so the hub velocity is
        /// <c>V = t' * Cross(a, up * R) = t' * R * Cross(axis, up)</c>. Hence the positive-spin
        /// travel direction is <c>Cross(axis, up)</c>.
        ///
        /// Taking the sign from the wheel's OWN axis rather than a guessed vessel forward is what
        /// makes reverse read as reverse, and it stays correct for wheels mirrored onto opposite
        /// sides of a rover: their local axes point opposite ways, and both must counter-rotate to
        /// travel the same way, which is exactly what the cross product yields.
        ///
        /// UNVERIFIED STEP — the one thing here that unit tests cannot settle. Everything else about
        /// this path is proven headless (rate = speed/radius, reverse is the negative of forward,
        /// sub-threshold speed holds still, sideways slide does not roll), but the ABSOLUTE visual
        /// direction depends on the Unity handedness identity quoted above, and
        /// <c>Quaternion.AngleAxis</c> is a native call that throws outside a Unity runtime, so the
        /// identity cannot be pinned by a test. If wheels visibly spin BACKWARDS while a rover drives
        /// forwards in game, the fix is to negate this one cross product —
        /// <c>Cross(up, axis)</c> instead of <c>Cross(axis, up)</c> — and nothing else.
        /// </summary>
        internal static Vector3 ComputeWheelRollForward(Vector3 wheelSpinAxisWorld, Vector3 up)
        {
            if (wheelSpinAxisWorld.sqrMagnitude <= 1e-8f || up.sqrMagnitude <= 1e-8f)
                return Vector3.zero;

            Vector3 rollForward = Vector3.Cross(wheelSpinAxisWorld.normalized, up.normalized);
            if (rollForward.sqrMagnitude <= 1e-8f)
                return Vector3.zero;

            return rollForward.normalized;
        }

        /// <summary>
        /// Signed wheel rotation for this frame, in degrees, from horizontal ground velocity.
        /// Magnitude is <c>speed / radius</c> (rad/s) converted to degrees; sign is the projection
        /// of the velocity onto <paramref name="rollForward"/>, so driving backwards rotates
        /// backwards. Returns 0 for a zero/invalid dt, an unusable radius, a degenerate roll
        /// direction, or a speed under <see cref="WheelStationarySpeedMetersPerSecond"/> — the
        /// stationary case, where a coasting-or-parked wheel must simply hold still.
        ///
        /// Replaces the recorded-<c>driveOutput</c> path, which produced the exact opposite
        /// behaviour: zero (stationary wheels) whenever the rover was coasting with no motor input,
        /// and an unsigned torque percent when it was not.
        /// </summary>
        internal static float ComputeWheelSpinDeltaDegrees(
            Vector3 horizontalSurfaceVelocity, Vector3 rollForward, float wheelRadius, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                return 0f;
            if (float.IsNaN(wheelRadius) || float.IsInfinity(wheelRadius) ||
                wheelRadius <= MinWheelRadiusMeters)
                return 0f;
            if (rollForward.sqrMagnitude <= 1e-8f)
                return 0f;

            float signedSpeed = Vector3.Dot(horizontalSurfaceVelocity, rollForward);
            if (float.IsNaN(signedSpeed) || float.IsInfinity(signedSpeed))
                return 0f;
            if (Mathf.Abs(signedSpeed) < WheelStationarySpeedMetersPerSecond)
                return 0f;

            // rad/s = v / r  ->  deg for this frame.
            return signedSpeed / wheelRadius * Mathf.Rad2Deg * (float)deltaSeconds;
        }

        /// <summary>
        /// Ground-contact test for a single recorded environment classification: only the two
        /// SURFACE classes mean the wheels are touching something they can roll on.
        ///
        /// Why this is the whole test, and why the altitude classes are not on the list: the
        /// recorder's own classifier (<see cref="EnvironmentDetector.Classify"/>) resolves
        /// LANDED / SPLASHED / PRELAUNCH straight to
        /// <see cref="SegmentEnvironment.SurfaceMobile"/> / <see cref="SegmentEnvironment.SurfaceStationary"/>
        /// before it ever looks at altitude, and it debounces the jitter (#246). So a rover driving,
        /// a plane on its takeoff roll, and a plane that has just touched down are all Surface; the
        /// instant the wheels leave the ground KSP reports FLYING and the section becomes
        /// Atmospheric. That boundary IS the wheels-on-ground boundary, already measured by the
        /// recorder, at no per-frame cost here.
        ///
        /// <see cref="SegmentEnvironment.Atmospheric"/> is deliberately NOT ground contact even
        /// though a wheel would keep freewheeling for a second after liftoff: holding still is the
        /// honest answer when nothing in the recording says the wheel is loaded, and it is the answer
        /// that keeps a rover riding a launch vehicle from spinning its wheels up the whole ascent.
        /// <see cref="SegmentEnvironment.Approach"/> is likewise excluded — on an airless body the
        /// classifier already promotes anything under 100 m AGL to Surface, so Approach means "not
        /// near the ground yet".
        /// </summary>
        internal static bool IsWheelSpinGroundContactEnvironment(SegmentEnvironment env)
        {
            return env == SegmentEnvironment.SurfaceMobile
                || env == SegmentEnvironment.SurfaceStationary;
        }

        /// <summary>
        /// Resolves whether a ghost's wheels are on the ground at <paramref name="playbackUT"/>, via
        /// the recorded <see cref="TrackSection"/> environment covering that UT, memoised in
        /// <paramref name="memo"/> for the resolved section's own span.
        ///
        /// FAILS CLOSED (returns false, wheels hold still) when no section covers the UT. That
        /// covers every "we do not actually know" shape and each of them is a case where spinning
        /// would be wrong, not right: a BG on-rails recording emits no env-classified per-frame
        /// sections at all (it is in orbit); a re-aimed trajectory presents an empty section list by
        /// contract (it is on a heliocentric transfer); an endpoint / loop-pause hold sits outside
        /// the recorded span (and zeroes the interpolated velocity anyway).
        ///
        /// The memo window is the resolved section's [startUT, endUT). That is only a correct cache
        /// because sections partition the recorded timeline without overlapping — each section's
        /// endUT is the next one's startUT — so no other section can be the answer inside the window.
        /// A malformed overlapping list could return the wrong cached class for the overlap; nothing
        /// produces that shape, and <see cref="TrajectoryMath.FindTrackSectionForUT"/> would already
        /// resolve it first-match-wins.
        /// </summary>
        internal static bool ResolveWheelGroundContact(
            List<TrackSection> sections, double playbackUT, ref WheelGroundContactMemo memo)
        {
            if (memo.hasValue && playbackUT >= memo.startUT && playbackUT < memo.endUT)
                return memo.onGround;

            int idx = TrajectoryMath.FindTrackSectionForUT(sections, playbackUT);
            if (idx < 0)
            {
                memo.hasValue = false;
                return false;
            }

            TrackSection section = sections[idx];
            bool onGround = IsWheelSpinGroundContactEnvironment(section.environment);
            memo.hasValue = true;
            memo.startUT = section.startUT;
            memo.endUT = section.endUT;
            memo.onGround = onGround;
            return onGround;
        }

        #endregion

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

        private static GhostPartEventOutcome ApplyRoboticEvent(
            GhostPlaybackState state, PartEvent evt, double currentUT)
        {
            if (state == null || state.roboticInfos == null)
                return GhostPartEventOutcome.NoFamilyState;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            if (!state.roboticInfos.TryGetValue(key, out RoboticGhostInfo info) || info == null)
                return GhostPartEventOutcome.NoInfoForPart;

            // Old recordings still carry RoboticMotion* events for wheel MOTOR modules, whose value
            // was an unsigned percent-of-max-torque. Ignore them entirely rather than using them as
            // a fallback: the ground-speed derivation works for every recording, old ones included,
            // because it reads the trajectory (which every recording has) instead of the event
            // stream. Consuming the stale value would resurrect the exact bug this replaced — zero
            // while coasting, unsigned when driving. The events stay harmless on disk; the schema
            // generation already gates real incompatibilities, so there is nothing to migrate.
            //
            // The event family itself is NOT retired: RoboticMotionStarted / RoboticPositionSample /
            // RoboticMotionStopped are still the live signal for hinges, pistons, rotation servos,
            // rotors and wheel SUSPENSION.
            //
            // S3 adds the same contract for ModuleWheelSteering: LEGACY recordings carry
            // RoboticMotion* events whose value came from a steering INPUT field
            // (steeringInput / currentSteering, unsigned on several stock rovers), not from a
            // caliper ANGLE. Replaying it as an angle was the same class of mistake the wheel-motor
            // scalar was, so the derived heading rate replaces it outright rather than falling back
            // to it — the derivation reads the trajectory, which every recording has. The producer
            // is gone as well (FlightRecorder.IsDerivedWheelVisualModuleName gates the emission for
            // both wheel families), so only pre-existing recordings can reach this branch: it is
            // the tolerance for them, not a live path.
            if (info.visualMode == RoboticVisualMode.WheelGroundSpeed
                || info.visualMode == RoboticVisualMode.WheelSteeringHeading)
            {
                ParsekLog.VerboseRateLimited("Flight",
                    $"wheel-motor-event-ignored-{evt.partPersistentId}-{evt.moduleIndex}",
                    $"Ignoring legacy wheel robotic event {evt.eventType} pid={evt.partPersistentId} " +
                    $"midx={evt.moduleIndex} mode={info.visualMode} " +
                    $"value={evt.value.ToString("F3", CultureInfo.InvariantCulture)}; " +
                    $"the visual is derived from ghost ground motion",
                    60.0);
                return GhostPartEventOutcome.LegacyEventIgnored;
            }

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

            return GhostPartEventOutcome.Applied;
        }

        /// <param name="trackSections">
        /// The trajectory's recorded track sections, used ONLY by the wheel-spin ground-contact gate
        /// (<see cref="ResolveWheelGroundContact"/>). Null / empty means "no proof of ground contact",
        /// which holds the wheels still; every other robotic visual mode ignores it.
        /// </param>
        internal static void UpdateActiveRobotics(
            GhostPlaybackState state, double currentUT, List<TrackSection> trackSections)
        {
            if (state == null || state.roboticInfos == null || state.roboticInfos.Count == 0)
                return;

            // Wheel-spin inputs are resolved ONCE per ghost per frame, not once per wheel: the
            // ground-contact gate, the body lookup, the surface normal and the ground-speed vector
            // are identical for every wheel on the craft. Only the per-wheel roll direction and
            // radius vary below. Resolution is also lazy — a ghost with no WheelGroundSpeed wheels
            // pays nothing, and a ghost whose wheels are off the ground pays only the memoised
            // section lookup (two double compares on a steady-state frame).
            bool wheelInputsResolved = false;
            bool wheelInputsUsable = false;
            Vector3 wheelUp = Vector3.zero;
            Vector3 wheelHorizontalVelocity = Vector3.zero;
            // S3 wheel steering shares those same once-per-ghost inputs; the heading rate on top of
            // them is also once-per-ghost (every steered wheel on a craft turns off one heading).
            bool steeringRateResolved = false;
            float steeringHeadingRate = 0f;

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
                else if (info.visualMode == RoboticVisualMode.WheelGroundSpeed)
                {
                    if (!wheelInputsResolved)
                    {
                        wheelInputsResolved = true;
                        // GROUND-CONTACT GATE first, before the body lookup / getRFrmVel work. A
                        // rover carried to orbit by a launch vehicle keeps its recorded orbital
                        // velocity (~2100 m/s on a Kerbin parking orbit); ungated, speed/radius
                        // turns that into thousands of degrees per frame and the wheels strobe for
                        // the whole ascent and coast. The old recorded signal never showed this
                        // because driveOutput was 0 with no motor input — it was right here by
                        // accident, which makes an ungated derivation a NEW artifact, on old
                        // recordings too. Nothing below the gate is reached off the ground.
                        bool onGround = ResolveWheelGroundContact(
                            trackSections, currentUT, ref state.wheelGroundContact);
                        wheelInputsUsable = onGround
                            && TryResolveWheelGroundSpeedInputs(
                                state, out wheelUp, out wheelHorizontalVelocity);
                        LogWheelGroundContactDecision(state, currentUT, onGround);
                    }

                    if (wheelInputsUsable)
                    {
                        Vector3 spinAxisWorld = info.servoTransform.TransformDirection(
                            info.axisLocal.sqrMagnitude > 0.0001f ? info.axisLocal.normalized : Vector3.right);
                        Vector3 rollForward = ComputeWheelRollForward(spinAxisWorld, wheelUp);
                        float deltaDegrees = ComputeWheelSpinDeltaDegrees(
                            wheelHorizontalVelocity, rollForward, info.wheelRadius, deltaSeconds);
                        if (Mathf.Abs(deltaDegrees) > 0.0001f)
                        {
                            Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                                ? info.axisLocal.normalized
                                : Vector3.right;
                            info.servoTransform.localRotation =
                                info.servoTransform.localRotation * Quaternion.AngleAxis(deltaDegrees, axis);
                        }
                    }
                }
                else if (info.visualMode == RoboticVisualMode.WheelSteeringHeading)
                {
                    if (!wheelInputsResolved)
                    {
                        wheelInputsResolved = true;
                        bool onGroundForSteering = ResolveWheelGroundContact(
                            trackSections, currentUT, ref state.wheelGroundContact);
                        wheelInputsUsable = onGroundForSteering
                            && TryResolveWheelGroundSpeedInputs(
                                state, out wheelUp, out wheelHorizontalVelocity);
                        LogWheelGroundContactDecision(state, currentUT, onGroundForSteering);
                    }

                    if (!steeringRateResolved)
                    {
                        steeringRateResolved = true;
                        steeringHeadingRate = ResolveGhostHeadingRateDegPerSec(
                            state, currentUT, wheelInputsUsable, wheelUp, wheelHorizontalVelocity);
                    }

                    // Off the ground, parked, or below the crawl threshold: heading rate is 0 and
                    // this eases the calipers back to straight rather than freezing them mid-turn.
                    //
                    // The rate is negated on the way IN because ComputeSynthDeflectionDegrees
                    // inverts (a control deflection OPPOSES the body rate it produced). Steering is
                    // the one family where the deflection and the rate share a sign — the wheels
                    // point INTO the turn — so the two negations cancel. Shared clamp/deadband/NaN
                    // handling is worth the one confusing sign.
                    float targetSteering = ComputeSynthDeflectionDegrees(
                        -steeringHeadingRate, WheelSteeringGainDegPerDegPerSec, MaxWheelSteeringDegrees);
                    info.steeringAngleDegrees = SlewTowardDegrees(
                        info.steeringAngleDegrees, targetSteering,
                        WheelSteeringSlewDegPerSec, deltaSeconds);

                    Vector3 steerAxis = info.axisLocal.sqrMagnitude > 0.0001f
                        ? info.axisLocal.normalized
                        : Vector3.up;
                    info.servoTransform.localRotation =
                        info.stowedRot * Quaternion.AngleAxis(info.steeringAngleDegrees, steerAxis);
                }

                info.lastUpdateUT = currentUT;
            }
        }

        /// <summary>Degrees of caliper angle per deg/s of ground-track heading change.</summary>
        internal const float WheelSteeringGainDegPerDegPerSec = 1.5f;
        /// <summary>Stock rover calipers top out near 30 degrees; clamp there.</summary>
        internal const float MaxWheelSteeringDegrees = 30f;
        internal const float WheelSteeringSlewDegPerSec = 60f;
        /// <summary>Below this ground speed the heading of a velocity vector is noise, not a direction.</summary>
        internal const float MinWheelSteeringGroundSpeed = 0.2f;
        internal const float WheelSteeringHeadingEmaAlpha = 0.3f;

        /// <summary>
        /// Once-per-ghost-per-frame heading rate for wheel steering, EMA-smoothed. Returns 0 (wheels
        /// straight) whenever the wheel inputs are unusable or the ghost is below a crawl, and
        /// re-seeds the heading memo so the next moving frame differences against a fresh sample
        /// rather than one from before a stop.
        /// </summary>
        private static float ResolveGhostHeadingRateDegPerSec(
            GhostPlaybackState state, double currentUT,
            bool inputsUsable, Vector3 up, Vector3 horizontalVelocity)
        {
            if (!inputsUsable || horizontalVelocity.magnitude < MinWheelSteeringGroundSpeed)
            {
                state.prevGroundHeadingUT = double.NaN;
                state.prevGroundHeading = Vector3.zero;
                state.smoothedHeadingRateDegPerSec = 0f;
                return 0f;
            }

            Vector3 heading = horizontalVelocity.normalized;
            if (double.IsNaN(state.prevGroundHeadingUT) || double.IsInfinity(state.prevGroundHeadingUT))
            {
                state.prevGroundHeading = heading;
                state.prevGroundHeadingUT = currentUT;
                return 0f;
            }

            double dt = currentUT - state.prevGroundHeadingUT;
            if (dt <= 0.0 || dt > MaxSynthSampleSeconds)
            {
                // Same reasoning as UpdateSynthesizedMotion: re-seed rather than cap the
                // denominator, so a warp gap cannot manufacture a huge heading rate and lock the
                // calipers at full lock on a rover that is barely turning.
                //
                // But DECAY the smoothed rate instead of returning it verbatim. Under sustained warp
                // every frame lands in this branch, and a held rate would hold the calipers at
                // whatever angle the last pre-warp turn commanded — a rover frozen mid-turn for as
                // long as the warp lasts, which is the one state a player is most likely to see.
                // Decaying by the same EMA weight a real zero sample would carry lets the existing
                // slew ease them straight, and the first sub-second frame re-acquires the true rate.
                state.prevGroundHeading = heading;
                state.prevGroundHeadingUT = currentUT;
                state.smoothedHeadingRateDegPerSec = DecayRateTowardZero(
                    state.smoothedHeadingRateDegPerSec, WheelSteeringHeadingEmaAlpha);
                return state.smoothedHeadingRateDegPerSec;
            }

            float sample = ComputeHeadingRateDegPerSec(state.prevGroundHeading, heading, up, dt);
            state.smoothedHeadingRateDegPerSec =
                state.smoothedHeadingRateDegPerSec * (1f - WheelSteeringHeadingEmaAlpha)
                + sample * WheelSteeringHeadingEmaAlpha;
            state.prevGroundHeading = heading;
            state.prevGroundHeadingUT = currentUT;
            return state.smoothedHeadingRateDegPerSec;
        }

        /// <summary>
        /// Resolves the two per-ghost inputs a wheel spin needs: the local surface normal and the
        /// ghost's horizontal ground velocity. Returns false (wheels hold still) when the ghost has
        /// no position, no resolved body, or no interpolated velocity yet.
        ///
        /// The body comes from <c>state.lastInterpolatedBodyName</c> through the same
        /// name-keyed cache the audio and watch-mode camera paths already use, so an SOI change
        /// re-resolves and steady flight costs a string compare rather than a scan of
        /// <c>FlightGlobals.Bodies</c>. The steady-state path allocates nothing; only a cache MISS
        /// pays for the <c>Find</c> predicate closure, and a miss happens once per body per ghost
        /// (first frame and SOI changes), not per frame. This is the same trade
        /// <c>ComputeAtmosphereFactor</c> already makes against the same cache fields.
        ///
        /// A zero <c>lastInterpolatedVelocity</c> is a legitimate state on several playback paths
        /// (it is left at default rather than always written), and it lands on the honest answer
        /// here: no known ground speed means stationary wheels — quiet, and still an improvement on
        /// the recorded signal, which showed stationary wheels for every coasting rover.
        /// </summary>
        private static bool TryResolveWheelGroundSpeedInputs(
            GhostPlaybackState state, out Vector3 up, out Vector3 horizontalVelocity)
        {
            up = Vector3.zero;
            horizontalVelocity = Vector3.zero;

            if (state.ghost == null)
                return false;

            Vector3 worldVelocity = state.lastInterpolatedVelocity;
            if (worldVelocity.sqrMagnitude <= 1e-8f)
                return false;

            string bodyName = state.lastInterpolatedBodyName;
            if (string.IsNullOrEmpty(bodyName))
                return false;

            CelestialBody body = state.cachedAudioBody;
            if (body == null || state.cachedAudioBodyName != bodyName)
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                state.cachedAudioBody = body;
                state.cachedAudioBodyName = bodyName;
            }
            if (body == null)
                return false;

            Vector3d ghostWorldPos = state.ghost.transform.position;
            up = (Vector3)(ghostWorldPos - body.position);
            if (up.sqrMagnitude <= 1e-8f)
                return false;

            Vector3 bodyFrameVelocity = (Vector3)body.getRFrmVel(ghostWorldPos);
            horizontalVelocity = ComputeSurfaceHorizontalVelocity(worldVelocity, bodyFrameVelocity, up);
            return true;
        }

        /// <summary>
        /// One Verbose line per ghost per ground-contact ANSWER, so a "wheels spun in orbit" or
        /// "wheels never turned" report is decidable from KSP.log. The rate-limit key carries the
        /// decision, so a flip emits immediately (new key, no prior timestamp) and a steady answer
        /// re-affirms at most once a minute rather than every frame.
        /// </summary>
        private static void LogWheelGroundContactDecision(
            GhostPlaybackState state, double currentUT, bool onGround)
        {
            string id = !string.IsNullOrEmpty(state.recordingId)
                ? state.recordingId
                : state.vesselName ?? "?";
            ParsekLog.VerboseRateLimited("Flight",
                $"wheel-ground-contact-{id}-{(onGround ? 1 : 0)}",
                $"Wheel spin ground-contact gate {(onGround ? "OPEN" : "CLOSED")} for \"{state.vesselName}\" " +
                $"rec={id} ut={currentUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"section={(state.wheelGroundContact.hasValue ? "resolved" : "none")}" +
                (onGround ? "" : "; wheels hold still"),
                60.0);
        }

        #endregion

        #region S3 — synthesized motion (gimbal, control surfaces, sun tracking)

        /// <summary>
        /// The longest UT gap a synthesis derivative will differentiate across. A longer gap is
        /// not clamped - it is DISCARDED and the sample re-seeded. Clamping a derivative's
        /// denominator inflates the answer instead of bounding it, which under time warp reads as
        /// a violent manoeuvre and pins every synthesized surface at its clamp.
        /// </summary>
        internal const double MaxSynthSampleSeconds = 1.0;

        /// <summary>EMA weight on the newest angular-velocity sample. Low = smooth, laggy.</summary>
        internal const float SynthAngularEmaAlpha = 0.25f;
        /// <summary>Angular rates below this are noise, not manoeuvring; everything decays to neutral.</summary>
        internal const float SynthAngularDeadbandDegPerSec = 0.5f;
        /// <summary>Degrees of gimbal per deg/s of body rate. A 5 deg/s pitch buys a 5 deg gimbal.</summary>
        internal const float GimbalGainDegPerDegPerSec = 1.0f;
        /// <summary>Degrees of control-surface deflection per deg/s of body rate.</summary>
        internal const float ControlSurfaceGainDegPerDegPerSec = 2.0f;
        /// <summary>Fallback surface range when the module carries no readable authority.</summary>
        internal const float DefaultControlSurfaceRangeDegrees = 15f;
        internal const float DefaultGimbalRangeDegrees = 4f;
        /// <summary>How fast a synthesized deflection is allowed to move toward its target.</summary>
        internal const float SynthDeflectionSlewDegPerSec = 90f;
        /// <summary>Stock tracking panels slew slowly; this is the visual cap, deg/s.</summary>
        internal const float SunTrackingSlewDegPerSec = 20f;

        /// <summary>
        /// The ghost's body-frame angular velocity in deg/s, from two APPLIED world rotations.
        ///
        /// Every quaternion operation here is written out by hand. <c>Quaternion.Inverse</c>,
        /// <c>operator*</c> and <c>ToAngleAxis</c> are all Unity native calls that throw outside a
        /// Unity runtime, and this decision has to be provable in a headless xUnit process. The
        /// struct itself is safe — only its static math is native.
        ///
        /// The inputs are APPLIED WORLD ROTATIONS, read off the ghost transform after positioning.
        /// They are never <c>TrajectoryPoint.rotation</c> values pulled from a flat Points list: in
        /// a RELATIVE track section that field holds an ANCHOR-LOCAL rotation rather than
        /// srfRelRotation, so differencing two of them across a section boundary mixes frames and
        /// invents a rotation that never happened.
        /// </summary>
        internal static Vector3 ComputeLocalAngularVelocityDegPerSec(
            Quaternion previous, Quaternion current, double deltaSeconds)
        {
            if (deltaSeconds <= 0.0 || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds))
                return Vector3.zero;

            // delta = conjugate(previous) * current, i.e. the rotation applied in previous's frame.
            float px = -previous.x, py = -previous.y, pz = -previous.z, pw = previous.w;
            float dx = pw * current.x + px * current.w + py * current.z - pz * current.y;
            float dy = pw * current.y - px * current.z + py * current.w + pz * current.x;
            float dz = pw * current.z + px * current.y - py * current.x + pz * current.w;
            float dw = pw * current.w - px * current.x - py * current.y - pz * current.z;

            // Shortest arc: q and -q are the same rotation, and only one of them has a small angle.
            if (dw < 0f) { dx = -dx; dy = -dy; dz = -dz; dw = -dw; }

            double sinHalf = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
            if (sinHalf <= 1e-7)
                return Vector3.zero;
            if (dw > 1f) dw = 1f;

            double angleDeg = 2.0 * Math.Atan2(sinHalf, dw) * (180.0 / Math.PI);
            double scale = angleDeg / deltaSeconds / sinHalf;
            if (double.IsNaN(scale) || double.IsInfinity(scale))
                return Vector3.zero;

            return new Vector3((float)(dx * scale), (float)(dy * scale), (float)(dz * scale));
        }

        /// <summary>
        /// One gap-frame decay of a smoothed scalar rate toward zero, weighted exactly as an EMA
        /// against a zero sample would be. Used where a frame carries no differentiable sample (a
        /// warp gap) but holding the last rate would freeze a visual: decaying answers "we no longer
        /// know that this is still happening" instead of "it is still happening". Snaps to exactly 0
        /// once the residue is below a ten-thousandth of a degree per second so the value settles
        /// rather than asymptoting forever.
        /// </summary>
        internal static float DecayRateTowardZero(float rate, float alpha)
        {
            if (float.IsNaN(rate) || float.IsInfinity(rate)) return 0f;
            if (float.IsNaN(alpha) || alpha <= 0f) return rate;
            if (alpha >= 1f) return 0f;

            float decayed = rate * (1f - alpha);
            return Math.Abs(decayed) < 1e-4f ? 0f : decayed;
        }

        /// <summary>Exponential moving average of a vector. Pure; <c>Vector3.Lerp</c> is managed but this is explicit.</summary>
        internal static Vector3 ComputeEmaVector(Vector3 previous, Vector3 sample, float alpha)
        {
            if (float.IsNaN(alpha) || alpha <= 0f) return previous;
            if (alpha >= 1f) return sample;
            float keep = 1f - alpha;
            return new Vector3(
                previous.x * keep + sample.x * alpha,
                previous.y * keep + sample.y * alpha,
                previous.z * keep + sample.z * alpha);
        }

        /// <summary>
        /// Maps a body rate (deg/s) to a control deflection (deg): opposite sign (a control surface
        /// or gimbal that produces a nose-up rate is itself deflected the other way), gain-scaled,
        /// deadbanded, and hard-clamped to the module's own authority. Rates below the deadband
        /// answer exactly 0 so the surface settles instead of shimmering on sampling noise.
        /// </summary>
        internal static float ComputeSynthDeflectionDegrees(
            float bodyRateDegPerSec, float gain, float rangeDegrees)
        {
            if (float.IsNaN(bodyRateDegPerSec) || float.IsInfinity(bodyRateDegPerSec))
                return 0f;
            if (Math.Abs(bodyRateDegPerSec) < SynthAngularDeadbandDegPerSec)
                return 0f;

            float range = float.IsNaN(rangeDegrees) || rangeDegrees <= 0f
                ? DefaultControlSurfaceRangeDegrees
                : rangeDegrees;
            float raw = -bodyRateDegPerSec * gain;
            return Math.Min(Math.Max(raw, -range), range);
        }

        /// <summary>
        /// Rate-limited move of a synthesized angle toward its target. Pure. Without this a
        /// deflection would jump the full clamp width on the first frame a manoeuvre starts, and a
        /// derivative spike at a trajectory sample boundary would show up as a visible twitch.
        /// </summary>
        internal static float SlewTowardDegrees(
            float currentDegrees, float targetDegrees, float maxRateDegPerSec, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0.0)
                return currentDegrees;
            if (float.IsNaN(targetDegrees) || float.IsInfinity(targetDegrees))
                return currentDegrees;

            float maxStep = (float)(maxRateDegPerSec * Math.Min(deltaSeconds, 1.0));
            float delta = targetDegrees - currentDegrees;
            if (Math.Abs(delta) <= maxStep)
                return targetDegrees;
            return currentDegrees + (delta > 0f ? maxStep : -maxStep);
        }

        /// <summary>
        /// Signed rate of change of a ground-track heading, degrees per second, measured about the
        /// local up. Pure: <c>Vector3.Dot</c> / <c>Vector3.Cross</c> and the struct's own magnitude
        /// are managed C#, not native calls.
        /// </summary>
        internal static float ComputeHeadingRateDegPerSec(
            Vector3 previousHeading, Vector3 currentHeading, Vector3 up, double deltaSeconds)
        {
            if (deltaSeconds <= 0.0 || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds))
                return 0f;
            if (previousHeading.sqrMagnitude <= 1e-8f || currentHeading.sqrMagnitude <= 1e-8f
                || up.sqrMagnitude <= 1e-8f)
            {
                return 0f;
            }

            Vector3 axis = up.normalized;
            Vector3 a = (previousHeading - axis * Vector3.Dot(previousHeading, axis)).normalized;
            Vector3 b = (currentHeading - axis * Vector3.Dot(currentHeading, axis)).normalized;
            if (a.sqrMagnitude <= 1e-8f || b.sqrMagnitude <= 1e-8f)
                return 0f;

            double cos = Math.Min(Math.Max(Vector3.Dot(a, b), -1f), 1f);
            double sin = Vector3.Dot(Vector3.Cross(a, b), axis);
            double angleDeg = Math.Atan2(sin, cos) * (180.0 / Math.PI);
            return (float)(angleDeg / deltaSeconds);
        }

        /// <summary>
        /// The angle (degrees, about <paramref name="axis"/>) that would point
        /// <paramref name="referenceForward"/> at <paramref name="towardTarget"/>, both projected
        /// onto the plane perpendicular to the axis. Returns false when either projection collapses
        /// (target along the axis, or an unusable axis) — the caller HOLDS its current angle rather
        /// than snapping to an arbitrary one. Pure.
        /// </summary>
        internal static bool TryComputeAimAngleDegrees(
            Vector3 towardTarget, Vector3 axis, Vector3 referenceForward, out float angleDegrees)
            => TryComputeAimAngleDegrees(
                towardTarget, axis, referenceForward, out angleDegrees, out _, out _);

        /// <summary>
        /// The diagnosing overload. <paramref name="targetPerpendicular"/> and
        /// <paramref name="referencePerpendicular"/> are the two projections into the aim plane, and
        /// they are what tells a "the Sun is along the axis" hold (target ~ 0, legitimate) apart
        /// from a frame bug (reference ~ 0). The H36 flight had no way to distinguish them and the
        /// cell's failure message could only list both possibilities.
        /// </summary>
        internal static bool TryComputeAimAngleDegrees(
            Vector3 towardTarget, Vector3 axis, Vector3 referenceForward,
            out float angleDegrees, out float targetPerpendicular, out float referencePerpendicular)
        {
            angleDegrees = 0f;
            targetPerpendicular = 0f;
            referencePerpendicular = 0f;
            if (towardTarget.sqrMagnitude <= 1e-8f || axis.sqrMagnitude <= 1e-8f
                || referenceForward.sqrMagnitude <= 1e-8f)
            {
                return false;
            }

            Vector3 n = axis.normalized;
            Vector3 target = towardTarget - n * Vector3.Dot(towardTarget, n);
            Vector3 reference = referenceForward - n * Vector3.Dot(referenceForward, n);
            targetPerpendicular = target.magnitude;
            referencePerpendicular = reference.magnitude;
            if (target.sqrMagnitude <= 1e-8f || reference.sqrMagnitude <= 1e-8f)
                return false;

            target = target.normalized;
            reference = reference.normalized;
            double cos = Math.Min(Math.Max(Vector3.Dot(reference, target), -1f), 1f);
            double sin = Vector3.Dot(Vector3.Cross(reference, target), n);
            angleDegrees = (float)(Math.Atan2(sin, cos) * (180.0 / Math.PI));
            return true;
        }

        /// <summary>
        /// Restores every synthesized transform to the neutral pose captured at build time. Called
        /// on a loop cycle (step 3g) — <see cref="ResetForLoopCycle"/> can zero the numbers but not
        /// touch a Transform, and a pivot left aimed at last orbit's Sun is the M4 bug class.
        /// </summary>
        internal static int RestoreSynthesizedMotionNeutralPoses(GhostPlaybackState state)
        {
            SynthesizedMotionGhostInfos synth = state?.synthesizedMotionInfos;
            if (synth == null) return 0;

            int restored = 0;

            if (synth.gimbals != null)
            {
                for (int i = 0; i < synth.gimbals.Count; i++)
                {
                    GimbalGhostInfo g = synth.gimbals[i];
                    if (g?.gimbalTransforms == null) continue;
                    g.currentDeflection = Vector2.zero;
                    for (int t = 0; t < g.gimbalTransforms.Count && t < g.neutralRotations.Count; t++)
                    {
                        if (g.gimbalTransforms[t] == null) continue;
                        g.gimbalTransforms[t].localRotation = g.neutralRotations[t];
                        restored++;
                    }
                }
            }

            if (synth.controlSurfaces != null)
            {
                for (int i = 0; i < synth.controlSurfaces.Count; i++)
                {
                    ControlSurfaceGhostInfo c = synth.controlSurfaces[i];
                    if (c?.surfaceTransforms == null) continue;
                    c.currentDeflection = 0f;
                    for (int t = 0; t < c.surfaceTransforms.Count && t < c.neutralRotations.Count; t++)
                    {
                        if (c.surfaceTransforms[t] == null) continue;
                        c.surfaceTransforms[t].localRotation = c.neutralRotations[t];
                        restored++;
                    }
                }
            }

            if (synth.sunTrackers != null)
            {
                for (int i = 0; i < synth.sunTrackers.Count; i++)
                {
                    SunTrackingGhostInfo s = synth.sunTrackers[i];
                    if (s?.pivotTransform == null) continue;
                    s.currentAngleDegrees = 0f;
                    s.hasAimed = false;
                    s.pivotTransform.localRotation = s.neutralRotation;
                    restored++;
                }
            }

            // S7: STOP every running loop and put its transforms back to phase 0. Without the stop,
            // a drill switched on during the prior cycle would keep turning through the start of
            // the next one - before the recording's own ConverterActivated has replayed - so the
            // second cycle of a looping mining replay would show the drill already running.
            if (synth.converterLoops != null)
            {
                for (int i = 0; i < synth.converterLoops.Count; i++)
                {
                    ConverterLoopGhostInfo loop = synth.converterLoops[i];
                    if (loop == null) continue;
                    loop.active = false;
                    loop.activeSinceUT = 0.0;
                    if (loop.transforms == null) continue;
                    for (int t = 0; t < loop.transforms.Count; t++)
                    {
                        ConverterLoopTransformState ts = loop.transforms[t];
                        if (ts?.t == null || ts.phases == null || ts.phases.Length == 0) continue;
                        ts.t.localPosition = ts.phases[0].pos;
                        ts.t.localRotation = ts.phases[0].rot;
                        ts.t.localScale = ts.phases[0].scale;
                        restored++;
                    }
                }
            }

            if (restored > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", "loop-synth-restore",
                    $"Loop cycle: restored {restored} synthesized transform(s) to neutral " +
                    $"(vessel='{state.vesselName ?? "unknown"}')", 1.0);
            return restored;
        }

        /// <summary>
        /// The single S3 per-frame entry, called from <c>ApplyFrameVisuals</c> beside
        /// <c>UpdateActiveRobotics</c>. Everything below the first two guards is gated: a ghost with
        /// no gimbal, no control surface and no tracking pivot pays one null check and one
        /// <c>IsEmpty</c> check per frame.
        /// </summary>
        internal static void UpdateSynthesizedMotion(GhostPlaybackState state, double currentUT)
        {
            if (state?.ghost == null) return;
            SynthesizedMotionGhostInfos synth = state.synthesizedMotionInfos;
            if (synth == null || synth.IsEmpty) return;

            // S7 runs FIRST and OUTSIDE the delta-time machinery below, deliberately. The other
            // three families are attitude-DERIVATIVE driven, so they need a usable dt and re-seed
            // (skipping the frame) when they cannot get one. A running loop needs no derivative at
            // all: its phase is a pure function of (currentUT - activeSinceUT). Putting it after
            // the re-seed guard would freeze every drill in the scene for the whole of a sustained
            // time warp — precisely the situation a mining base is usually watched in.
            DriveConverterLoops(state, synth, currentUT);

            // The APPLIED world rotation — post-positioning, post-frame-resolution, correct on
            // Absolute and RELATIVE sections alike because it is the transform, not stored data.
            Quaternion currentRotation = state.ghost.transform.rotation;

            if (double.IsNaN(state.prevSynthUT) || double.IsInfinity(state.prevSynthUT))
            {
                state.prevSynthRotation = currentRotation;
                state.prevSynthUT = currentUT;
                return;
            }

            double deltaSeconds = currentUT - state.prevSynthUT;
            if (deltaSeconds <= 0.0 || deltaSeconds > MaxSynthSampleSeconds)
            {
                // Paused, same-frame double call, a backwards scrub, or a gap too long to
                // differentiate across (high warp, a hitch, a stall). RE-SEED AND SKIP THE SAMPLE
                // rather than clamping the denominator: dividing a full-interval angle by a capped
                // dt does not GUARD the rate, it INFLATES it - a 10 s gap over-reports by 10x, so
                // a slowly-coasting ghost under warp would sit pinned at full deflection while its
                // true rate is under the deadband. A negative dt would invert the sign outright.
                //
                // Accepted: under SUSTAINED warp every frame re-seeds here, so a gimbal or surface
                // holds its last deflection instead of easing to neutral. Left as-is deliberately —
                // the hold is bounded by the clamp, invisible at warp's own visual scale, and
                // self-correcting on the first sub-second frame. Wheel steering does NOT get the same
                // pass (see ResolveGhostHeadingRateDegPerSec): its held state is a caliper angle a
                // player reads directly off a parked rover, so that one decays.
                state.prevSynthRotation = currentRotation;
                state.prevSynthUT = currentUT;
                return;
            }

            Vector3 sample = ComputeLocalAngularVelocityDegPerSec(
                state.prevSynthRotation, currentRotation, deltaSeconds);
            state.smoothedAngularVelocity =
                ComputeEmaVector(state.smoothedAngularVelocity, sample, SynthAngularEmaAlpha);
            state.prevSynthRotation = currentRotation;
            state.prevSynthUT = currentUT;

            Vector3 rate = state.smoothedAngularVelocity;

            DriveSynthesizedGimbals(state, synth, rate, deltaSeconds);
            DriveSynthesizedControlSurfaces(state, synth, rate, deltaSeconds);
            DriveSunTracking(state, synth, deltaSeconds);
        }

        /// <summary>
        /// S7: the running-loop phase for one loop at one recorded UT, wrapped into [0,1).
        ///
        /// Pure, so the cyclic arithmetic — the part most likely to be wrong and least likely to be
        /// noticed — is directly testable. A non-finite or non-positive clip length parks the loop
        /// at phase 0 rather than dividing by it.
        /// </summary>
        internal static float ComputeConverterLoopPhase(
            double currentUT, double activeSinceUT, float clipLengthSeconds)
        {
            if (clipLengthSeconds <= 0f || float.IsNaN(clipLengthSeconds) || float.IsInfinity(clipLengthSeconds))
                return 0f;
            double elapsed = currentUT - activeSinceUT;
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed <= 0.0)
                return 0f;

            double cycles = elapsed / clipLengthSeconds;
            double phase = cycles - Math.Floor(cycles);
            if (phase < 0.0) phase += 1.0;
            // Guard the boundary: floating point can land phase at exactly 1.0 for a large
            // `cycles`, and a caller indexing phases[(int)(phase * N)] would then run off the end.
            if (phase >= 1.0) phase = 0.0;
            return (float)phase;
        }

        /// <summary>
        /// S7: resolves a wrapped phase onto a pair of adjacent sampled poses and the blend between
        /// them. The pair WRAPS — phase 11 of 12 blends toward phase 0, not toward a 13th slot —
        /// because the clip is cyclic and the sampler deliberately did not store a duplicate
        /// endpoint.
        /// </summary>
        internal static void ResolveConverterLoopBlend(
            float phase, int phaseCount, out int fromIndex, out int toIndex, out float blend)
        {
            fromIndex = 0;
            toIndex = 0;
            blend = 0f;
            if (phaseCount <= 0) return;
            if (phaseCount == 1) return;

            float scaled = Mathf.Clamp01(phase) * phaseCount;
            fromIndex = (int)scaled;
            if (fromIndex >= phaseCount) fromIndex = phaseCount - 1;
            toIndex = (fromIndex + 1) % phaseCount;
            blend = Mathf.Clamp01(scaled - fromIndex);
        }

        /// <summary>
        /// S7: advances every ACTIVE running loop on this ghost to the pose its recorded UT implies.
        /// Inactive loops are left exactly where they stopped, which is what a switched-off drill
        /// looks like — parked mid-stroke, not snapped to a home pose.
        /// </summary>
        private static void DriveConverterLoops(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth, double currentUT)
        {
            if (synth.converterLoops == null || synth.converterLoops.Count == 0) return;

            int driven = 0;
            for (int i = 0; i < synth.converterLoops.Count; i++)
            {
                ConverterLoopGhostInfo loop = synth.converterLoops[i];
                if (loop == null || !loop.active) continue;
                if (loop.transforms == null || loop.transforms.Count == 0) continue;

                float phase = ComputeConverterLoopPhase(
                    currentUT, loop.activeSinceUT, loop.clipLengthSeconds);

                for (int t = 0; t < loop.transforms.Count; t++)
                {
                    ConverterLoopTransformState ts = loop.transforms[t];
                    if (ts?.t == null || ts.phases == null || ts.phases.Length == 0) continue;

                    ResolveConverterLoopBlend(
                        phase, ts.phases.Length, out int from, out int to, out float blend);

                    ConverterLoopPose a = ts.phases[from];
                    ConverterLoopPose b = ts.phases[to];
                    ts.t.localPosition = Vector3.Lerp(a.pos, b.pos, blend);
                    ts.t.localRotation = Quaternion.Slerp(a.rot, b.rot, blend);
                    ts.t.localScale = Vector3.Lerp(a.scale, b.scale, blend);
                }
                driven++;
            }

            if (driven > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", "converter-loop-drive",
                    $"Converter loops driven: {driven} (vessel='{state.vesselName ?? "unknown"}')", 5.0);
        }

        /// <summary>
        /// S7: starts or stops one part's running loop. Called by the ConverterActivated /
        /// ConverterDeactivated events, by the snapshot-start seed and by the loop-cycle reset.
        ///
        /// <paramref name="activeSinceUT"/> is the RECORDED event UT, which is what makes the whole
        /// thing scrub-safe: replaying the same recorded moment always lands on the same phase, so
        /// a rewound or looping replay is not merely close but identical.
        /// </summary>
        internal static bool ApplyConverterLoopState(
            GhostPlaybackState state, uint partPersistentId, bool active, double activeSinceUT)
        {
            GhostPartEventOutcome outcome = ApplyConverterLoopStateWithOutcome(
                state, partPersistentId, active, activeSinceUT);
            // Historical contract preserved exactly: the bool was true whenever a loop
            // for this pid was REACHED, including the deliberate ignore of a duplicate
            // activation. The outcome enum is what splits those two apart.
            return outcome == GhostPartEventOutcome.Applied
                || outcome == GhostPartEventOutcome.AlreadyInState;
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. Reports AlreadyInState when every loop
        /// matched for this pid was already active and the duplicate-activation ignore
        /// fired - the one case where "the handler ran and changed nothing" is
        /// deliberate rather than a resolution failure.
        /// </summary>
        internal static GhostPartEventOutcome ApplyConverterLoopStateWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool active, double activeSinceUT)
        {
            var synth = state?.synthesizedMotionInfos;
            if (synth?.converterLoops == null) return GhostPartEventOutcome.NoFamilyState;

            bool matched = false;
            bool changed = false;
            for (int i = 0; i < synth.converterLoops.Count; i++)
            {
                ConverterLoopGhostInfo loop = synth.converterLoops[i];
                if (loop == null || loop.partPersistentId != partPersistentId) continue;

                // Re-arming an ALREADY-active loop would restart it from phase 0 and produce a
                // visible hitch. A duplicate ConverterActivated (a snapshot seed followed by a
                // start-UT seed for the same part) is exactly that case, so it is ignored.
                if (active && loop.active) { matched = true; continue; }

                loop.active = active;
                if (active) loop.activeSinceUT = activeSinceUT;
                matched = true;
                changed = true;
            }

            if (!matched) return GhostPartEventOutcome.NoInfoForPart;
            return changed
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.AlreadyInState;
        }

        /// <summary>
        /// S4: the jetpack plume gate. Pure, because it is the one place the three recorded EVA
        /// flags combine and the combination is the whole honesty of the feature.
        ///
        /// All three conditions are load-bearing:
        /// <list type="bullet">
        /// <item><description>THRUSTING is the signal itself, already debounced by the recorder so
        /// a single tap never reaches here.</description></item>
        /// <item><description>JETPACK DEPLOYED, because a stowed pack cannot thrust. The recorder
        /// reads the two flags independently and KSP recomputes JetpackIsThrusting from fuel flow,
        /// so the pair CAN disagree for a frame around a stow - and a plume from a stowed pack is
        /// the more visible of the two possible errors.</description></item>
        /// <item><description>NOT RAGDOLL. A tumbling kerbal is not flying, and stock cuts thrust
        /// when the FSM enters ragdoll. This is also where the ragdoll events earn their keep on the
        /// VISUAL side, given that the ragdoll POSE is deliberately not replayed.</description></item>
        /// </list>
        /// </summary>
        internal static bool ShouldEmitEvaJetpackPlume(bool deployed, bool thrusting, bool ragdoll)
            => thrusting && deployed && !ragdoll;

        /// <summary>
        /// S4: records one EVA flag change and reconciles the plume to the resulting gate.
        ///
        /// All six EVA event types route through here rather than each toggling the particle system
        /// itself, so the gate is evaluated in exactly one place and cannot drift between call sites.
        /// </summary>
        internal static void ApplyEvaState(GhostPlaybackState state, PartEventType type)
        {
            ApplyEvaStateWithOutcome(state, type);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. The only non-apply here is an event type
        /// the flag reducer does not model (a null state, or a member routed to this
        /// arm by mistake), which is exactly UnhandledEventType.
        /// </summary>
        internal static GhostPartEventOutcome ApplyEvaStateWithOutcome(
            GhostPlaybackState state, PartEventType type)
        {
            if (!TryUpdateEvaFlags(state, type)) return GhostPartEventOutcome.UnhandledEventType;
            ReconcileEvaJetpackPlume(state);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// S4: folds one EVA event into the three playback flags. Returns false for an event type
        /// that is not an EVA one, so the caller skips the reconcile.
        ///
        /// Split out from <see cref="ApplyEvaState"/> so the BOOKKEEPING is headless-testable: the
        /// reconcile below has to compare a GameObject against null, which routes through a
        /// UnityEngine.Object ECall xUnit cannot host. Same pure-decision / Unity-applier division
        /// the rest of this file uses.
        /// </summary>
        internal static bool TryUpdateEvaFlags(GhostPlaybackState state, PartEventType type)
        {
            if (state == null) return false;

            switch (type)
            {
                case PartEventType.EvaJetpackDeployed: state.evaJetpackDeployed = true; return true;
                case PartEventType.EvaJetpackStowed: state.evaJetpackDeployed = false; return true;
                case PartEventType.EvaJetpackThrustStarted: state.evaJetpackThrusting = true; return true;
                case PartEventType.EvaJetpackThrustStopped: state.evaJetpackThrusting = false; return true;
                case PartEventType.EvaRagdollStarted: state.evaRagdoll = true; return true;
                case PartEventType.EvaRagdollEnded: state.evaRagdoll = false; return true;
                default: return false;
            }
        }

        /// <summary>
        /// S4: what one plume reconcile should DO, as a pure function of the gate plus the facts
        /// only Unity can answer. Split out from <see cref="ReconcileEvaJetpackPlume"/> so the
        /// interesting decision - in particular the inactive-hierarchy case, which is the one that
        /// shipped broken - is headless-testable; the Unity reads and the build / Play / Stop calls
        /// stay in the impure wrapper.
        /// </summary>
        internal enum EvaPlumeReconcileAction
        {
            /// <summary>Gate shut and nothing playing: no work.</summary>
            None,
            /// <summary>Gate shut but the system is still emitting: stop it.</summary>
            Stop,
            /// <summary>Gate open, hierarchy active, no system yet: build one, then play it.</summary>
            Build,
            /// <summary>Gate open, hierarchy active, system exists and is idle: start it.</summary>
            Play,
            /// <summary>Gate open and the system is already emitting: no work.</summary>
            AlreadyPlaying,
            /// <summary>
            /// Gate open but the ghost is NOT active in the hierarchy, so neither building nor
            /// playing would take effect. Do nothing THIS call; the per-frame self-heal retries once
            /// the ghost is shown.
            /// </summary>
            DeferInactiveHierarchy,
            /// <summary>A previous build failed (no additive shader): never retry.</summary>
            Unavailable,
        }

        /// <summary>
        /// THE INACTIVE-HIERARCHY CASE IS THE WHOLE REASON THIS IS A NAMED FUNCTION. Unity's
        /// <c>ParticleSystem.Play()</c> on a system that is not <c>activeInHierarchy</c> is a SILENT
        /// no-op - it neither throws nor sets <c>isPlaying</c> - and a ghost spends a genuinely
        /// reachable part of its life inactive: <c>BuildTimelineGhostFromSnapshot</c> ends with
        /// <c>root.SetActive(false)</c>, and the spawn-time prefix replay inside
        /// <c>ApplyPartEvents</c> runs BEFORE <c>ActivateGhostVisualsIfNeeded</c>. So a ghost whose
        /// playback cursor lands inside a thrust burst consumed its <c>EvaJetpackThrustStarted</c>
        /// against an inactive hierarchy, the Play no-opped, and - because this reconcile was
        /// EVENT-driven only - nothing ever tried again. The plume stayed dark for the whole burst
        /// while the log claimed it was emitting.
        ///
        /// Deferring the BUILD as well (rather than building and failing to play) keeps the laziness
        /// claim honest: a ghost that is never shown allocates nothing at all.
        /// </summary>
        internal static EvaPlumeReconcileAction ClassifyEvaPlumeReconcile(
            bool wanted, bool hasPlume, bool unavailable, bool hierarchyActive, bool isPlaying)
        {
            if (!wanted)
                return hasPlume && isPlaying ? EvaPlumeReconcileAction.Stop : EvaPlumeReconcileAction.None;

            // Checked BEFORE `unavailable` and before the build: an inactive ghost is a
            // RETRY-LATER, not a permanent failure, and conflating the two would either mark a
            // perfectly good ghost permanently unavailable or allocate for one never shown.
            if (!hierarchyActive)
                return EvaPlumeReconcileAction.DeferInactiveHierarchy;

            if (unavailable) return EvaPlumeReconcileAction.Unavailable;
            if (!hasPlume) return EvaPlumeReconcileAction.Build;
            return isPlaying ? EvaPlumeReconcileAction.AlreadyPlaying : EvaPlumeReconcileAction.Play;
        }

        /// <summary>
        /// S4: brings the plume into line with the flags. Builds it LAZILY on the first moment it is
        /// wanted AND showable, so an EVA ghost that never fires its pack - or is never shown -
        /// allocates no particle system, and a non-EVA ghost never reaches here at all.
        ///
        /// Called BOTH from the six EVA events (immediate response) and once per rendered frame
        /// while the recording says the pack is firing
        /// (<see cref="UpdateEvaJetpackPlumeForFrame"/>). The per-frame call is what makes it
        /// SELF-HEALING, exactly as launch dust already is: whatever the hierarchy was doing when
        /// the event arrived, the first frame the ghost is actually visible starts the plume.
        ///
        /// Logging is DECISION-VS-TRUTH, per the named anomaly class the render tracers use: the
        /// success line is emitted only after reading <c>isPlaying</c> BACK and finding it true. It
        /// used to be emitted on the strength of having called Play, which is exactly how a total
        /// no-op logged as a success.
        /// </summary>
        internal static void ReconcileEvaJetpackPlume(GhostPlaybackState state)
        {
            if (state == null) return;

            bool wanted = ShouldEmitEvaJetpackPlume(
                state.evaJetpackDeployed, state.evaJetpackThrusting, state.evaRagdoll);

            // Every Unity read happens ONCE, here, and is handed to the pure classifier. A PLAIN
            // reference check guards the info object first: comparing a null ParticleSystem against
            // null would still route through UnityEngine.Object's overloaded operator.
            bool hasPlume = state.evaJetpackPlumeInfo != null;
            ParticleSystem ps = hasPlume ? state.evaJetpackPlumeInfo.particles : null;
            bool isPlaying = hasPlume && ps != null && ps.isPlaying;
            bool hierarchyActive = state.ghost != null && state.ghost.activeInHierarchy;

            EvaPlumeReconcileAction action = ClassifyEvaPlumeReconcile(
                wanted, hasPlume, state.evaJetpackPlumeUnavailable, hierarchyActive, isPlaying);

            switch (action)
            {
                case EvaPlumeReconcileAction.None:
                case EvaPlumeReconcileAction.AlreadyPlaying:
                case EvaPlumeReconcileAction.Unavailable:
                    return;

                case EvaPlumeReconcileAction.Stop:
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    return;

                case EvaPlumeReconcileAction.DeferInactiveHierarchy:
                    // Not a failure, and deliberately not silent: this is the state the spawn prefix
                    // replay is ALWAYS in, so it has to be greppable when a plume is reported
                    // missing.
                    ParsekLog.VerboseRateLimited("GhostVisual", "eva-plume-deferred",
                        "EVA jetpack plume deferred (vessel='" + (state.vesselName ?? "unknown") +
                        "'): ghost not active in hierarchy, so Play() would silently no-op; " +
                        "retrying on the first rendered frame", 5.0);
                    return;

                case EvaPlumeReconcileAction.Build:
                    state.evaJetpackPlumeInfo = GhostVisualBuilder.TryBuildEvaJetpackPlume(
                        state.ghost, state.vesselName);
                    if (state.evaJetpackPlumeInfo == null)
                    {
                        state.evaJetpackPlumeUnavailable = true;
                        return;
                    }
                    ps = state.evaJetpackPlumeInfo.particles;
                    goto case EvaPlumeReconcileAction.Play;

                case EvaPlumeReconcileAction.Play:
                    if (ps == null) return;
                    ps.Play();

                    // TRUTH, read back rather than assumed.
                    if (ps.isPlaying)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", "eva-plume",
                            "EVA jetpack plume emitting (vessel='" + (state.vesselName ?? "unknown") +
                            "' deployed=" + state.evaJetpackDeployed +
                            " thrusting=" + state.evaJetpackThrusting +
                            " ragdoll=" + state.evaRagdoll + ")", 5.0);
                    }
                    else
                    {
                        // WARN, not Verbose: the hierarchy WAS active, so the deferral branch did not
                        // apply and Play() should have taken. Anything reaching here is an unmodelled
                        // refusal, and the render tracers' convention is that a decision-vs-truth
                        // mismatch nobody predicted is loud.
                        ParsekLog.Warn("GhostVisual",
                            "EVA jetpack plume did NOT start after Play() (vessel='" +
                            (state.vesselName ?? "unknown") + "' hierarchyActive=true deployed=" +
                            state.evaJetpackDeployed + " thrusting=" + state.evaJetpackThrusting +
                            " ragdoll=" + state.evaRagdoll +
                            ") - the gate opened but the system stayed idle");
                    }
                    return;
            }
        }

        /// <summary>
        /// S4 per-frame self-heal, called from <c>ApplyFrameVisuals</c> beside the launch-dust drive.
        /// Gated on the ONE flag that can want a plume, so a non-EVA ghost - i.e. essentially every
        /// ghost - pays a single bool field read per frame and nothing more.
        ///
        /// It exists because the event-driven reconcile alone is not enough: see
        /// <see cref="ClassifyEvaPlumeReconcile"/> for the inactive-hierarchy no-op it repairs.
        /// Launch dust gets the same property for free by re-calling Play every frame; this plume is
        /// event-driven, so it needs the retry stated explicitly.
        /// </summary>
        internal static void UpdateEvaJetpackPlumeForFrame(GhostPlaybackState state)
        {
            if (state == null || !state.evaJetpackThrusting) return;
            ReconcileEvaJetpackPlume(state);
        }

        private static void DriveSynthesizedGimbals(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth,
            Vector3 rate, double deltaSeconds)
        {
            if (synth.gimbals == null || synth.gimbals.Count == 0) return;

            for (int i = 0; i < synth.gimbals.Count; i++)
            {
                GimbalGhostInfo g = synth.gimbals[i];
                if (g?.gimbalTransforms == null || g.gimbalTransforms.Count == 0) continue;

                // A gimbal only steers while its engine is LIT. A cold engine's bell hangs neutral;
                // easing there rather than snapping keeps a shutdown from popping the nozzle.
                bool lit = IsAnyEnginePowerOnPart(state, g.partPersistentId);
                float targetX = lit
                    ? ComputeSynthDeflectionDegrees(rate.x, GimbalGainDegPerDegPerSec, g.gimbalRangeDegrees)
                    : 0f;
                float targetY = lit
                    ? ComputeSynthDeflectionDegrees(rate.y, GimbalGainDegPerDegPerSec, g.gimbalRangeDegrees)
                    : 0f;

                g.currentDeflection = new Vector2(
                    SlewTowardDegrees(g.currentDeflection.x, targetX, SynthDeflectionSlewDegPerSec, deltaSeconds),
                    SlewTowardDegrees(g.currentDeflection.y, targetY, SynthDeflectionSlewDegPerSec, deltaSeconds));

                Quaternion offset =
                    Quaternion.AngleAxis(g.currentDeflection.x, Vector3.right)
                    * Quaternion.AngleAxis(g.currentDeflection.y, Vector3.up);

                for (int t = 0; t < g.gimbalTransforms.Count && t < g.neutralRotations.Count; t++)
                {
                    Transform tr = g.gimbalTransforms[t];
                    if (tr == null) continue;
                    tr.localRotation = g.neutralRotations[t] * offset;
                }
            }
        }

        private static void DriveSynthesizedControlSurfaces(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth,
            Vector3 rate, double deltaSeconds)
        {
            if (synth.controlSurfaces == null || synth.controlSurfaces.Count == 0) return;

            // Aero surfaces only work in air. Vacuum (or an unresolvable body) drives them to
            // neutral rather than leaving them cocked from the last atmospheric frame.
            bool inAtmosphere = IsGhostInAtmosphere(state);

            for (int i = 0; i < synth.controlSurfaces.Count; i++)
            {
                ControlSurfaceGhostInfo c = synth.controlSurfaces[i];
                if (c?.surfaceTransforms == null || c.surfaceTransforms.Count == 0) continue;

                // PRECEDENCE (risk 4): a DEPLOYED deployable pose on the same part owns this
                // transform. An extended airbrake is a brake pose, and S2 is mid-clip or holding
                // it; deflecting on top would fight the transition.
                if (IsDeployablePoseHeldForPart(state, c.partPersistentId))
                    continue;

                float bodyRate = 0f;
                if (!c.ignorePitch) bodyRate += rate.x;
                if (!c.ignoreYaw) bodyRate += rate.y;
                if (!c.ignoreRoll) bodyRate += rate.z;

                float target = inAtmosphere
                    ? ComputeSynthDeflectionDegrees(
                        bodyRate, ControlSurfaceGainDegPerDegPerSec, c.rangeDegrees)
                    : 0f;
                c.currentDeflection = SlewTowardDegrees(
                    c.currentDeflection, target, SynthDeflectionSlewDegPerSec, deltaSeconds);

                Quaternion offset = Quaternion.AngleAxis(c.currentDeflection, Vector3.right);
                for (int t = 0; t < c.surfaceTransforms.Count && t < c.neutralRotations.Count; t++)
                {
                    Transform tr = c.surfaceTransforms[t];
                    if (tr == null) continue;
                    tr.localRotation = c.neutralRotations[t] * offset;
                }
            }
        }

        private static void DriveSunTracking(
            GhostPlaybackState state, SynthesizedMotionGhostInfos synth, double deltaSeconds)
        {
            if (synth.sunTrackers == null || synth.sunTrackers.Count == 0) return;

            CelestialBody sun = Planetarium.fetch?.Sun;
            if (sun == null) return;
            Vector3d sunPosition = sun.position;

            for (int i = 0; i < synth.sunTrackers.Count; i++)
            {
                SunTrackingGhostInfo s = synth.sunTrackers[i];
                if (s?.pivotTransform == null) continue;

                // PRECEDENCE (risk 4): transition > tracking. A panel only tracks once it is fully
                // deployed and nothing is animating it.
                if (!IsDeployablePoseFullyDeployedForPart(state, s.partPersistentId))
                {
                    // DISCRIMINATOR 1 of 2. A tracking panel that never aims has exactly two
                    // possible reasons and they need opposite fixes; this line says which, so the
                    // next red is one read rather than a re-flight. (The other is at the aim site.)
                    // IsVerboseEnabled-guarded because the rate limiter cannot stop the CALLER from
                    // building the string: this runs every frame for every stowed panel on every
                    // ghost, and a stowed panel is the common case.
                    if (ParsekLog.IsVerboseEnabled)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"sun-track-gate-{s.partPersistentId}",
                            $"Sun tracking held (gate closed) pid={s.partPersistentId}: " +
                            DescribeDeployableGateForPart(state, s.partPersistentId), 30.0);
                    }

                    // TRANSITION > TRACKING, in its strong form: while a clip is actually RUNNING
                    // we must not write the pivot AT ALL. On many tracking panels the pivot is one
                    // of the transforms the deploy animation itself moves, so easing it toward
                    // neutral here would overwrite UpdateActiveDeployables' write from earlier in
                    // the same frame and make the retract judder. Drop the bookkeeping and let S2
                    // own the transform; the ease-back below is only for a panel that is stowed
                    // and static, where nothing else is writing it.
                    if (IsDeployableTransitionRunningForPart(state, s.partPersistentId))
                    {
                        s.currentAngleDegrees = 0f;
                        s.hasAimed = false;
                        continue;
                    }
                    if (s.hasAimed)
                    {
                        s.currentAngleDegrees = SlewTowardDegrees(
                            s.currentAngleDegrees, 0f, SunTrackingSlewDegPerSec, deltaSeconds);
                        ApplySunTrackingAngle(s);
                        if (Math.Abs(s.currentAngleDegrees) <= 0.01f) s.hasAimed = false;
                    }
                    continue;
                }

                Transform pivot = s.pivotTransform;

                // BOTH the axis and the reference are read in the PIVOT'S OWN NEUTRAL FRAME, and
                // that is the whole of the 2026-08-11 H36 fix. ApplySunTrackingAngle POST-multiplies
                // (neutralRotation * AngleAxis(angle, axisLocal)), so the world axis the applied
                // angle actually turns about is parentRotation * neutralRotation * axisLocal — not
                // the parent's own up, which is what this used to measure against. Two consequences,
                // both real:
                //   * the measured angle was expressed about a different axis than the one it was
                //     applied about whenever the pivot's neutral rotation was not identity, and
                //   * the reference direction (the pivot's neutral forward) could land PARALLEL to
                //     the parent's up, at which point its projection into the aim plane vanished,
                //     TryComputeAimAngleDegrees answered false every frame, and the panel never
                //     tracked at all. That is exactly what solarPanelOX10C did in the H36 flight.
                // In the pivot's own frame the reference is perpendicular to the axis BY
                // CONSTRUCTION (see ResolveSunTrackingReferenceLocal), so the only remaining "hold"
                // is the legitimate one: the Sun lying along the rotation axis.
                //
                // This is also what stock does. ModuleDeployablePart tracks with
                // `Atan2(pivot.InverseTransformPoint(sun).x, ....z)` applied as
                // `pivot.rotation * Euler(0, y, 0)` — the pivot's own local +Y as the axis and its
                // own local +Z as the zero-angle reference.
                Quaternion parentRotation =
                    pivot.parent != null ? pivot.parent.rotation : Quaternion.identity;
                Quaternion neutralWorld = parentRotation * s.neutralRotation;
                Vector3 axisWorld = neutralWorld * ResolveSunTrackingAxisLocal(s.axisLocal);
                Vector3 referenceForward = neutralWorld * ResolveSunTrackingReferenceLocal(s.axisLocal);
                Vector3 toSun = (Vector3)(sunPosition - (Vector3d)pivot.position);

                if (!TryComputeAimAngleDegrees(
                        toSun, axisWorld, referenceForward,
                        out float aim, out float targetPerp, out float referencePerp))
                {
                    // DISCRIMINATOR 2 of 2. The gate is OPEN here, so this is the geometric hold —
                    // and the two projection magnitudes say which of the three degeneracies it is.
                    // Same per-frame string-building guard as the gate line above: a hold persists
                    // for as long as the geometry does, so this would run every frame.
                    if (ParsekLog.IsVerboseEnabled)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual",
                            $"sun-track-aim-{s.partPersistentId}",
                            $"Sun tracking held (aim unresolved) pid={s.partPersistentId}: " +
                            $"targetPerp={targetPerp.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"referencePerp={referencePerp.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"toSun={toSun.magnitude.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"axisWorld={axisWorld} — a near-zero targetPerp is the Sun lying along " +
                            "the pivot axis (a legitimate hold); a near-zero referencePerp is a " +
                            "frame bug", 30.0);
                    }
                    continue;
                }

                s.currentAngleDegrees = SlewTowardDegrees(
                    s.currentAngleDegrees, aim, SunTrackingSlewDegPerSec, deltaSeconds);
                s.hasAimed = true;
                ApplySunTrackingAngle(s);
            }
        }

        private static void ApplySunTrackingAngle(SunTrackingGhostInfo s)
        {
            if (s?.pivotTransform == null) return;
            // Same resolver the aim used, so the angle is applied about the axis it was measured
            // about. These two used to drift apart and that was half the H36 sun-tracking defect.
            s.pivotTransform.localRotation = s.neutralRotation
                * Quaternion.AngleAxis(s.currentAngleDegrees, ResolveSunTrackingAxisLocal(s.axisLocal));
        }

        /// <summary>
        /// The pivot's rotation axis IN ITS OWN LOCAL FRAME, normalised, with the stock default
        /// (local up / +Y, what <c>ModuleDeployablePart</c> rotates about) as the fallback for an
        /// unusable stored axis. Pure Vector3 math — no native call — so it is headless-testable.
        /// </summary>
        internal static Vector3 ResolveSunTrackingAxisLocal(Vector3 axisLocal)
            => axisLocal.sqrMagnitude > 0.0001f ? axisLocal.normalized : Vector3.up;

        /// <summary>
        /// The ZERO-ANGLE REFERENCE direction in the pivot's own local frame: the direction that
        /// ends up pointing at the Sun when the tracking angle is applied.
        ///
        /// Local +Z, matching stock (<c>Atan2(sunLocal.x, sunLocal.z)</c> is measured from the
        /// pivot's own +Z), except that it is explicitly ORTHOGONALISED against the axis and
        /// swapped for +X if the axis happens to be +Z. That guarantee is the point: a reference
        /// with no component in the aim plane makes the aim unresolvable, and a panel whose
        /// reference silently degenerated is a panel that never tracks — the H36 red.
        /// </summary>
        internal static Vector3 ResolveSunTrackingReferenceLocal(Vector3 axisLocal)
        {
            Vector3 axis = ResolveSunTrackingAxisLocal(axisLocal);
            Vector3 candidate = Vector3.forward;
            if (Math.Abs(Vector3.Dot(candidate, axis)) > 0.9f) candidate = Vector3.right;

            Vector3 perpendicular = candidate - axis * Vector3.Dot(candidate, axis);
            return perpendicular.sqrMagnitude > 1e-6f ? perpendicular.normalized : Vector3.forward;
        }

        /// <summary>The deployable gate's own state for one part, as a grep-stable one-liner. Shared
        /// by the runtime hold log and the in-game cell's failure message so both read alike.</summary>
        internal static string DescribeDeployableGateForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null)
                return "deployable=no-dictionary (treated as deployed)";
            if (!state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d)
                || d == null)
            {
                return "deployable=absent (treated as deployed)";
            }
            return "deployable=present"
                + $" breakSubtreeHidden={d.breakSubtreeHidden}"
                + $" currentDeployed={d.currentDeployed}"
                + $" transitionActive={d.transitionActive}"
                + $" deployFraction={d.deployFraction.ToString("R", CultureInfo.InvariantCulture)}"
                + $" transforms={d.transforms?.Count ?? 0}";
        }

        /// <summary>
        /// The FULL sun-tracking decision for one tracker: the deployed gate AND the two aim-plane
        /// projections, in one string. The in-game cell pastes this straight into its failure
        /// message so a red names its own cause instead of listing candidates. Reads Transforms, so
        /// this is scene-side, not headless.
        /// </summary>
        internal static string DescribeSunTrackingState(
            GhostPlaybackState state, SunTrackingGhostInfo s)
        {
            if (s == null) return "tracker=null";
            string gate = "gate="
                + (IsDeployablePoseFullyDeployedForPart(state, s.partPersistentId) ? "open" : "closed")
                + " " + DescribeDeployableGateForPart(state, s.partPersistentId);

            Transform pivot = s.pivotTransform;
            if (pivot == null) return gate + " pivot=null";

            CelestialBody sun = Planetarium.fetch?.Sun;
            if (sun == null) return gate + " sun=absent";

            Quaternion parentRotation =
                pivot.parent != null ? pivot.parent.rotation : Quaternion.identity;
            Quaternion neutralWorld = parentRotation * s.neutralRotation;
            Vector3 axisWorld = neutralWorld * ResolveSunTrackingAxisLocal(s.axisLocal);
            Vector3 referenceForward = neutralWorld * ResolveSunTrackingReferenceLocal(s.axisLocal);
            Vector3 toSun = (Vector3)(sun.position - (Vector3d)pivot.position);

            bool resolved = TryComputeAimAngleDegrees(
                toSun, axisWorld, referenceForward,
                out float aim, out float targetPerp, out float referencePerp);

            return gate
                + $" aimResolved={resolved}"
                + $" aim={aim.ToString("R", CultureInfo.InvariantCulture)}"
                + $" targetPerp={targetPerp.ToString("R", CultureInfo.InvariantCulture)}"
                + $" referencePerp={referencePerp.ToString("R", CultureInfo.InvariantCulture)}"
                + $" currentAngle={s.currentAngleDegrees.ToString("R", CultureInfo.InvariantCulture)}"
                + $" hasAimed={s.hasAimed}";
        }

        /// <summary>True when any engine module on the part is currently commanded above zero.</summary>
        private static bool IsAnyEnginePowerOnPart(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.engineInfos == null) return false;
            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info != null && info.partPersistentId == partPersistentId && info.currentPower > 0f)
                    return true;
            }
            return false;
        }

        /// <summary>True when the part has a deployable clip actually RUNNING this frame.</summary>
        private static bool IsDeployableTransitionRunningForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return false;
            return state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d)
                && d != null && d.transitionActive;
        }

        /// <summary>
        /// True when the part carries a deployable family that is deployed or mid-transition, i.e.
        /// S2 owns its transforms this frame.
        /// </summary>
        private static bool IsDeployablePoseHeldForPart(GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return false;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d) || d == null)
                return false;
            return d.transitionActive || d.currentDeployed;
        }

        /// <summary>
        /// True when the part is safe for sun tracking: fully deployed with nothing animating. A
        /// part with NO deployable family at all counts as deployed — a fixed tracking panel has no
        /// stow pose to be caught in.
        /// </summary>
        private static bool IsDeployablePoseFullyDeployedForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return true;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out DeployableGhostInfo d) || d == null)
                return true;
            // S6: a BROKEN panel closes the gate. The pivot may still carry a fully-deployed pose
            // from before the break (the recorder does not retract it - that is rule 1), so without
            // this the ghost would slew the pivot of a panel it has just hidden toward the Sun for
            // the rest of the recording.
            //
            // SCOPE, stated precisely because the obvious stronger claim is false: this closes the
            // gate for any part that HAS a DeployableGhostInfo, including one whose break transform
            // did not resolve (ApplyDeployableBrokenState still sets the flag). It does NOT cover a
            // part with no info at all - the TryGetValue miss above answers "treated as deployed" -
            // but that combination needs a part with no deploy animation AND an unresolvable break
            // transform AND isTracking, which no stock part is: every tracking panel has a deploy
            // animation, so it always has an info.
            if (d.breakSubtreeHidden) return false;
            return d.currentDeployed && !d.transitionActive && d.deployFraction >= 1f - 1e-4f;
        }

        /// <summary>
        /// Resolves the ghost's current body through the same name-keyed cache the audio, wheel and
        /// watch-camera paths use (a string compare on a steady frame; a Find only on an SOI change).
        /// </summary>
        internal static CelestialBody ResolveCachedGhostBody(GhostPlaybackState state)
        {
            if (state == null) return null;
            string bodyName = state.lastInterpolatedBodyName;
            if (string.IsNullOrEmpty(bodyName)) return null;

            if (state.cachedAudioBody == null || state.cachedAudioBodyName != bodyName)
            {
                state.cachedAudioBody = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                state.cachedAudioBodyName = bodyName;
            }
            return state.cachedAudioBody;
        }

        private static bool IsGhostInAtmosphere(GhostPlaybackState state)
        {
            CelestialBody body = ResolveCachedGhostBody(state);
            if (body == null || !body.atmosphere) return false;
            double altitude = state.lastInterpolatedAltitude;
            if (double.IsNaN(altitude) || double.IsInfinity(altitude)) return false;
            return altitude < body.atmosphereDepth;
        }

        #endregion

        #region S3 — launch dust

        /// <summary>Above this height above ground level no dust is raised at all.</summary>
        internal const float LaunchDustMaxAglMeters = 40f;
        internal const float LaunchDustEmissionMax = 260f;
        internal const float LaunchDustSizeMin = 1.5f;
        internal const float LaunchDustSizeMax = 5f;
        /// <summary>Below this the plume is not moving enough air to be worth a particle system.</summary>
        internal const float LaunchDustMinIntensity = 0.02f;
        /// <summary>How far into the recording to look for a usable ground reference before giving up.</summary>
        internal const int LaunchDustGroundLatchScanCap = 2000;

        /// <summary>
        /// Sum of every engine module's commanded power on a ghost. Used as the dust driver's
        /// cheapest-first gate: no lit engine, no dust, and nothing below this is evaluated.
        /// </summary>
        internal static float SumEnginePower(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return 0f;
            float total = 0f;
            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f) continue;
                total += info.currentPower;
            }
            return total;
        }

        /// <summary>
        /// Latches the sea-level altitude of the ground under the recording's launch site, from the
        /// first trajectory point that carries a finite <c>recordedGroundClearance</c>.
        ///
        /// SCALARS ONLY, and the RECORDER GATE is what makes reading them off a flat
        /// <c>Recording.Points</c> list safe. <c>FlightRecorder.ShouldEmitSurfaceClearance</c> only
        /// populates <c>recordedGroundClearance</c> on an ABSOLUTE, surface-environment, PQS-backed
        /// sample, so the finite-clearance test below is simultaneously the "this point's
        /// <c>altitude</c> is a real altitude-above-datum" test: a RELATIVE-section point, whose
        /// altitude field holds anchor-local METRES, carries NaN clearance and is skipped. That is
        /// the invariant to preserve if the gate ever widens — it is not true a priori that these two
        /// fields mean the same thing in every frame. <c>latitude</c> / <c>longitude</c> are NEVER
        /// read here at all, for the same reason in its sharper form: treating anchor-local metres as
        /// a body-fixed position puts the answer inside the planet.
        ///
        /// NaN clearance everywhere answers false, and the caller emits no dust. That is the honest
        /// degradation: an invented ground reference would put a dust cloud around a ghost in orbit.
        /// </summary>
        internal static bool TryLatchLaunchDustGroundReference(
            List<TrajectoryPoint> points, out double groundRefAltitude)
        {
            groundRefAltitude = double.NaN;
            if (points == null || points.Count == 0) return false;

            int scanned = Math.Min(points.Count, LaunchDustGroundLatchScanCap);
            for (int i = 0; i < scanned; i++)
            {
                double clearance = points[i].recordedGroundClearance;
                double altitude = points[i].altitude;
                if (double.IsNaN(clearance) || double.IsInfinity(clearance)) continue;
                if (double.IsNaN(altitude) || double.IsInfinity(altitude)) continue;
                groundRefAltitude = altitude - clearance;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The dust intensity (0..1) for one frame. Gate order is cheapest-first by design: engine
        /// power (a dictionary walk over a handful of entries), then the latched ground reference
        /// (one NaN test), then the AGL window.
        /// </summary>
        internal static bool TryComputeLaunchDustIntensity(
            float totalEnginePower, double altitude, double groundRefAltitude, out float intensity)
        {
            intensity = 0f;
            if (float.IsNaN(totalEnginePower) || totalEnginePower <= 0f) return false;
            if (double.IsNaN(groundRefAltitude) || double.IsInfinity(groundRefAltitude)) return false;
            if (double.IsNaN(altitude) || double.IsInfinity(altitude)) return false;

            double agl = altitude - groundRefAltitude;
            // A modest negative AGL is terrain-model disagreement between record and playback, not
            // a ghost underground; a large one means the reference belongs to somewhere else.
            if (agl < -50.0 || agl >= LaunchDustMaxAglMeters) return false;
            if (agl < 0.0) agl = 0.0;

            float proximity = (float)(1.0 - agl / LaunchDustMaxAglMeters);
            intensity = Math.Min(1f, totalEnginePower * proximity);
            return intensity > LaunchDustMinIntensity;
        }

        #endregion

        #region Heat / Reentry

        internal static bool ApplyHeatState(GhostPlaybackState state, PartEvent evt, HeatLevel level)
            => ApplyHeatStateWithOutcome(state, evt, level) == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core. This family already had per-apply evidence
        /// (`Part pid=N: applied heat level ...`), which is KEPT: it names the level,
        /// which the family line does not, and existing readers pin it.
        /// </summary>
        /// <summary>P8 step 1 precondition classifier (pure).</summary>
        internal static GhostPartEventOutcome ClassifyHeatApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state == null || state.heatInfos == null) return GhostPartEventOutcome.NoFamilyState;
            if (!state.heatInfos.TryGetValue(partPersistentId, out HeatGhostInfo info) || info == null)
                return GhostPartEventOutcome.NoInfoForPart;
            if (info.transforms == null && info.materialStates == null)
                return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyHeatStateWithOutcome(
            GhostPlaybackState state, PartEvent evt, HeatLevel level)
        {
            GhostPartEventOutcome precondition = ClassifyHeatApply(state, evt.partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            HeatGhostInfo info = state.heatInfos[evt.partPersistentId];

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

            return applied
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoResolvedVisual;
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

        #region S2 — deployable / gear / bay / ladder interpolation

        /// <summary>Fallback clip length when the prefab animation's own length is unreadable.</summary>
        internal const float DefaultDeployableClipSeconds = 3f;
        internal const float MinDeployableClipSeconds = 0.25f;
        internal const float MaxDeployableClipSeconds = 30f;

        /// <summary>
        /// Clamps a prefab <c>AnimationState.length</c> into a plausible deployable-clip duration.
        /// A zero / NaN / negative length means the clip was unreadable (no Animation component on
        /// the model clone, a stripped state) and answers the 3 s default — which still ANIMATES,
        /// because the failure mode we are replacing is the snap, not a wrong duration.
        /// </summary>
        internal static float ClampDeployableClipSeconds(float rawLengthSeconds)
        {
            if (float.IsNaN(rawLengthSeconds) || float.IsInfinity(rawLengthSeconds)
                || rawLengthSeconds <= 0f)
            {
                return DefaultDeployableClipSeconds;
            }
            return Math.Min(Math.Max(rawLengthSeconds, MinDeployableClipSeconds), MaxDeployableClipSeconds);
        }

        /// <summary>
        /// Where along stowed(0) -> deployed(1) an in-flight transition is at <paramref name="currentUT"/>.
        ///
        /// PURE FUNCTION OF THE EVENT UT, which is the whole design: a prefix catch-up, a scrub, a
        /// warp change and a loop cycle all evaluate the same expression and land on the same pose,
        /// with no accumulated per-frame state to drift or to reset. Wall time would need all four
        /// to be special-cased.
        ///
        /// Duration scales with the DISTANCE travelled (<c>|target - start|</c>), so a reversal at
        /// mid-clip takes half a clip back rather than a full one — an animation played backwards
        /// from where it got to, not a fresh clip from the far end.
        /// </summary>
        internal static float ComputeDeployableTransitionFraction(
            double currentUT, double transitionStartUT,
            float startFraction, float targetFraction, float clipLengthSeconds,
            out bool complete)
        {
            complete = true;
            startFraction = Math.Min(Math.Max(startFraction, 0f), 1f);
            targetFraction = Math.Min(Math.Max(targetFraction, 0f), 1f);

            float span = Math.Abs(targetFraction - startFraction);
            if (span <= 1e-4f)
                return targetFraction;

            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT)
                || double.IsNaN(transitionStartUT) || double.IsInfinity(transitionStartUT))
            {
                return targetFraction;
            }

            double duration = ClampDeployableClipSeconds(clipLengthSeconds) * span;
            double elapsed = currentUT - transitionStartUT;
            if (elapsed <= 0.0)
            {
                // Playback UT is at or before the event that started this transition (a backwards
                // scrub, or the same frame the event fired). Hold the start pose; the next frame
                // with a positive elapsed advances it.
                complete = false;
                return startFraction;
            }
            if (elapsed >= duration)
                return targetFraction;

            complete = false;
            return startFraction + (targetFraction - startFraction) * (float)(elapsed / duration);
        }

        /// <summary>
        /// Writes one interpolated pose into every transform of a deployable family. Endpoint
        /// fractions take the exact stored endpoint pose rather than a lerp result, so the snap
        /// path and the animated path agree bit-for-bit at 0 and 1.
        /// </summary>
        private static bool ApplyDeployableFraction(DeployableGhostInfo info, float fraction)
        {
            if (info?.transforms == null) return false;

            bool applied = false;
            bool atStowed = fraction <= 1e-4f;
            bool atDeployed = fraction >= 1f - 1e-4f;

            for (int i = 0; i < info.transforms.Count; i++)
            {
                var ts = info.transforms[i];
                if (ts.t == null) continue;
                applied = true;

                if (atStowed)
                {
                    ts.t.localPosition = ts.stowedPos;
                    ts.t.localRotation = ts.stowedRot;
                    ts.t.localScale = ts.stowedScale;
                }
                else if (atDeployed)
                {
                    ts.t.localPosition = ts.deployedPos;
                    ts.t.localRotation = ts.deployedRot;
                    ts.t.localScale = ts.deployedScale;
                }
                else
                {
                    ts.t.localPosition = Vector3.Lerp(ts.stowedPos, ts.deployedPos, fraction);
                    ts.t.localRotation = Quaternion.Slerp(ts.stowedRot, ts.deployedRot, fraction);
                    ts.t.localScale = Vector3.Lerp(ts.stowedScale, ts.deployedScale, fraction);
                }
            }

            if (applied)
                info.deployFraction = fraction;
            return applied;
        }

        /// <summary>
        /// Advances every in-flight deployable transition to <paramref name="currentUT"/> and drops
        /// the ones that finished. Called from <c>ApplyFrameVisuals</c> beside
        /// <c>UpdateActiveRobotics</c>. Cost is O(active transitions x transforms) and the active
        /// list is empty on every frame where nothing is opening or closing, which is almost all
        /// of them.
        /// </summary>
        internal static void UpdateActiveDeployables(GhostPlaybackState state, double currentUT)
        {
            List<DeployableGhostInfo> active = state?.activeDeployableTransitions;
            if (active == null || active.Count == 0) return;

            for (int i = active.Count - 1; i >= 0; i--)
            {
                DeployableGhostInfo info = active[i];
                if (info == null || !info.transitionActive)
                {
                    active.RemoveAt(i);
                    continue;
                }

                float fraction = ComputeDeployableTransitionFraction(
                    currentUT, info.transitionStartUT,
                    info.transitionStartFraction, info.transitionTargetFraction,
                    info.clipLengthSeconds, out bool complete);

                if (!ApplyDeployableFraction(info, fraction))
                {
                    // Every transform went null (part decoupled / destroyed mid-clip). Retire the
                    // entry rather than re-walking a dead list every frame.
                    info.transitionActive = false;
                    active.RemoveAt(i);
                    continue;
                }

                if (complete)
                {
                    info.transitionActive = false;
                    info.deployFraction = info.transitionTargetFraction;
                    info.currentDeployed = info.transitionTargetFraction >= 0.5f;
                    active.RemoveAt(i);
                }
            }
        }

        /// <summary>Drops every in-flight transition. Loop cycles re-stow and replay from their own UTs.</summary>
        internal static void ClearActiveDeployableTransitions(GhostPlaybackState state)
        {
            if (state?.deployableInfos != null)
            {
                foreach (var kvp in state.deployableInfos)
                {
                    if (kvp.Value == null) continue;
                    kvp.Value.transitionActive = false;
                }
            }
            state?.activeDeployableTransitions?.Clear();
        }

        #endregion

        /// <summary>
        /// The IMMEDIATE (snap) path, unchanged in behaviour and still what every baseline caller
        /// takes: <c>ApplySnapshotBaselines</c>, the split-seed stow, and loop-reset step 2. A pose
        /// that was APPLIED as a baseline must never animate — the ghost is being put into the
        /// state it spawned in, not shown opening a bay.
        /// </summary>
        internal static bool ApplyDeployableState(GhostPlaybackState state, PartEvent evt, bool deployed)
            => ApplyDeployableState(state, evt, deployed, immediate: true);

        /// <summary>
        /// S6: hides or re-shows the break subtree — the ghost-side equivalent of what stock does
        /// to a ModuleDeployablePart that goes BROKEN (<c>panelBreakTransform.gameObject
        /// .SetActive(false)</c>) and of what a repair undoes.
        ///
        /// Stock's own repair path (<c>DoRepair</c>) re-instantiates a fresh subtree off the part
        /// prefab rather than re-activating the old one, because the live break DETACHED the panel
        /// into its own physics object and flung it away. Our ghost never detached anything — it
        /// only ever hid its own clone — so a plain SetActive(true) is the faithful inverse, and
        /// the simpler one.
        ///
        /// Absolute-state and idempotent, which is what lets a snapshot baseline, a start-UT seed
        /// and a recorded event all target the same pid without fighting.
        ///
        /// Returns true when an info existed for the pid, whether or not it had a resolvable
        /// transform: the STATE flag is set either way, because "this panel is broken" still has
        /// to gate sun tracking on a part whose break transform could not be resolved.
        /// </summary>
        internal static bool ApplyDeployableBrokenState(
            GhostPlaybackState state, uint partPersistentId, bool broken)
            => ApplyDeployableBrokenStateWithOutcome(state, partPersistentId, broken)
                == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core; the bool wrapper above is unchanged in
        /// behaviour (it was already "an info existed for the pid").
        /// </summary>
        /// <summary>
        /// P8 step 1 precondition classifier (pure; see <see cref="ClassifyEngineEmissionApply"/>
        /// for why every family has one). Applied whenever an info exists for the pid,
        /// with or without a resolvable break transform - the STATE flag is set either
        /// way, which is this handler's documented contract.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyDeployableBrokenApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.deployableInfos == null) return GhostPartEventOutcome.NoFamilyState;
            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out info) || info == null)
                return GhostPartEventOutcome.NoInfoForPart;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyDeployableBrokenStateWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool broken)
        {
            GhostPartEventOutcome precondition =
                ClassifyDeployableBrokenApply(state, partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            DeployableGhostInfo info = state.deployableInfos[partPersistentId];

            info.breakSubtreeHidden = broken;

            if (info.breakSubtreeRoot != null)
                info.breakSubtreeRoot.gameObject.SetActive(!broken);

            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// Applies a deployable target pose, either immediately or as an interpolated transition
        /// keyed on <c>evt.ut</c> (the RECORDED event UT, never wall time — see
        /// <see cref="ComputeDeployableTransitionFraction"/>).
        /// </summary>
        internal static bool ApplyDeployableState(
            GhostPlaybackState state, PartEvent evt, bool deployed, bool immediate)
            => ApplyDeployableStateWithOutcome(state, evt, deployed, immediate)
                == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core. Every historical `return false` keeps its
        /// exact condition and gains a named reason class; every historical `return
        /// true` maps to Applied, so the bool wrapper above is behaviourally identical.
        /// </summary>
        /// <summary>
        /// P8 step 1 precondition classifier (pure). Applied here means only "the ghost
        /// has a pose-carrying deployable info for this part"; whether the pose write
        /// itself reached a live transform is decided inside the handler.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyDeployableApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.deployableInfos == null) return GhostPartEventOutcome.NoFamilyState;
            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(partPersistentId, out info))
                return GhostPartEventOutcome.NoInfoForPart;
            if (info?.transforms == null) return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyDeployableStateWithOutcome(
            GhostPlaybackState state, PartEvent evt, bool deployed, bool immediate)
        {
            GhostPartEventOutcome precondition =
                ClassifyDeployableApply(state, evt.partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            DeployableGhostInfo info = state.deployableInfos[evt.partPersistentId];

            // S6: ANY extend/retract opinion on a panel currently rendered broken un-hides it
            // first. The recorder emits DeployableRetracted on a repair precisely so this fires,
            // and putting it here rather than only in the Retracted arm means a recording whose
            // repair was followed immediately by a re-deploy (so the tail's first deployable
            // event is Extended) still gets the panel back.
            //
            // AND THE UN-HIDE FORCES A SNAP, which is not a detail. Rule 1 deliberately leaves the
            // pivot at deployFraction = 1 when a panel breaks (the recorder does not retract it),
            // so an ANIMATED application here would re-show the panel fully EXTENDED and then fold
            // it politely shut over the clip length. Stock DoRepair lands on RETRACTED instantly,
            // with a freshly instantiated subtree - there is no fold to show. Reaching the target
            // pose in the same frame as the un-hide is the faithful rendering.
            if (info.breakSubtreeHidden)
            {
                ApplyDeployableBrokenState(state, evt.partPersistentId, broken: false);
                immediate = true;
            }

            float target = deployed ? 1f : 0f;

            if (immediate)
            {
                info.transitionActive = false;
                info.currentDeployed = deployed;
                bool snapped = ApplyDeployableFraction(info, target);
                if (snapped)
                    state.activeDeployableTransitions?.Remove(info);
                return snapped
                    ? GhostPartEventOutcome.Applied
                    : GhostPartEventOutcome.NoResolvedVisual;
            }

            // Already there and not mid-clip: nothing to animate, and re-arming would restart a
            // finished clip every time a duplicate event replays.
            if (!info.transitionActive && Math.Abs(info.deployFraction - target) <= 1e-4f)
            {
                info.currentDeployed = deployed;
                // Historically `return info.transforms.Count > 0`, preserved exactly: a
                // pose-carrying info already at the target counts as APPLIED (the ghost
                // is in the state the event asks for), and a pose-less one is a
                // resolution failure. That keeps the cargo-bay cascade below falling
                // through to jettison panels on precisely the same condition as before.
                return info.transforms.Count > 0
                    ? GhostPartEventOutcome.Applied
                    : GhostPartEventOutcome.NoResolvedVisual;
            }

            // A REVERSAL MID-CLIP must start from where the old transition had reached AT THIS
            // EVENT'S UT, not from info.deployFraction. In a one-batch prefix replay (a spawn or a
            // scrub into the window) deploy@t1 and retract@t2 are consumed back to back with no
            // UpdateActiveDeployables pass between them, so deployFraction is still the pose as of
            // t1; using it would arm a zero-span retract that completes instantly and show a
            // fully-stowed panel where a partly-retracted one belongs.
            float startFraction = info.transitionActive
                ? ComputeDeployableTransitionFraction(
                    evt.ut, info.transitionStartUT, info.transitionStartFraction,
                    info.transitionTargetFraction, info.clipLengthSeconds, out _)
                : info.deployFraction;

            info.transitionActive = true;
            info.transitionStartUT = evt.ut;
            info.transitionStartFraction = startFraction;
            info.transitionTargetFraction = target;
            info.currentDeployed = deployed;

            if (state.activeDeployableTransitions == null)
                state.activeDeployableTransitions = new List<DeployableGhostInfo>();
            if (!state.activeDeployableTransitions.Contains(info))
                state.activeDeployableTransitions.Add(info);

            // Apply the first frame straight away so a transition that starts on a frame where
            // UpdateActiveDeployables already ran is not a frame late.
            return ApplyDeployableFraction(info, startFraction)
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoResolvedVisual;
        }

        /// <summary>
        /// The cargo/service-bay cascade: most bays animate a DeployableGhostInfo, but
        /// some (e.g. bays whose doors are jettison-style panels) only have jettison
        /// transforms. Extracted so the M1 snapshot baseline and the CargoBayOpened /
        /// CargoBayClosed events take exactly the same path.
        /// </summary>
        internal static bool ApplyCargoBayState(GhostPlaybackState state, PartEvent evt, bool open)
            => ApplyCargoBayState(state, evt, open, immediate: true);

        /// <summary>
        /// S2: the jettison-panel arm of the cascade stays a SetActive snap — a panel that is a
        /// jettisoned object has no stowed/deployed pose pair to interpolate between.
        /// </summary>
        internal static bool ApplyCargoBayState(
            GhostPlaybackState state, PartEvent evt, bool open, bool immediate)
        {
            if (ApplyDeployableState(state, evt, deployed: open, immediate: immediate))
                return true;
            return ApplyJettisonPanelState(state, evt, jettisoned: open);
        }

        /// <summary>
        /// P8 step 1: the cascade reported as its TWO arms rather than one boolean.
        /// Which arm a bay took is the fact a reader cannot recover from the old
        /// aggregate line, and it is the difference between "the doors animated" and
        /// "a jettison panel was hidden".
        /// </summary>
        internal static void ApplyCargoBayStateWithOutcomes(
            GhostPlaybackState state,
            PartEvent evt,
            bool open,
            bool immediate,
            out GhostPartEventOutcome deployableOutcome,
            out GhostPartEventOutcome jettisonOutcome,
            out bool jettisonArmReached)
        {
            deployableOutcome = ApplyDeployableStateWithOutcome(
                state, evt, deployed: open, immediate: immediate);
            if (deployableOutcome == GhostPartEventOutcome.Applied)
            {
                jettisonOutcome = GhostPartEventOutcome.Applied;
                jettisonArmReached = false;
                return;
            }

            jettisonArmReached = true;
            // The classifier is consulted here as well as inside the writer - the SAME
            // function, so no predicate is duplicated - because it lets the cascade's
            // arm choice be driven headlessly up to the point where a live Transform is
            // genuinely required. Cost is one dictionary lookup on a bay event.
            jettisonOutcome = ClassifyJettisonPanelApply(state, evt.partPersistentId);
            if (jettisonOutcome == GhostPartEventOutcome.Applied)
                jettisonOutcome = ApplyJettisonPanelStateWithOutcome(state, evt, jettisoned: open);
        }

        internal static bool ApplyJettisonPanelState(GhostPlaybackState state, PartEvent evt, bool jettisoned)
            => ApplyJettisonPanelStateWithOutcome(state, evt, jettisoned)
                == GhostPartEventOutcome.Applied;

        /// <summary>
        /// P8 step 1 outcome-reporting core. The historical single `return false` guard
        /// covered three distinct facts (no jettison dictionary, no entry for the pid,
        /// an entry with an empty transform list); they are separated here because the
        /// third is a ghost-BUILD result and the first two are not.
        /// </summary>
        /// <summary>P8 step 1 precondition classifier (pure).</summary>
        internal static GhostPartEventOutcome ClassifyJettisonPanelApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.jettisonInfos == null) return GhostPartEventOutcome.NoFamilyState;
            JettisonGhostInfo jetInfo;
            if (!state.jettisonInfos.TryGetValue(partPersistentId, out jetInfo))
                return GhostPartEventOutcome.NoInfoForPart;
            if (jetInfo == null
                || jetInfo.jettisonTransforms == null
                || jetInfo.jettisonTransforms.Count == 0)
                return GhostPartEventOutcome.NoResolvedVisual;
            return GhostPartEventOutcome.Applied;
        }

        internal static GhostPartEventOutcome ApplyJettisonPanelStateWithOutcome(
            GhostPlaybackState state, PartEvent evt, bool jettisoned)
        {
            GhostPartEventOutcome precondition =
                ClassifyJettisonPanelApply(state, evt.partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            JettisonGhostInfo jetInfo = state.jettisonInfos[evt.partPersistentId];

            bool applied = false;
            for (int i = 0; i < jetInfo.jettisonTransforms.Count; i++)
            {
                Transform jettisonTransform = jetInfo.jettisonTransforms[i];
                if (jettisonTransform == null) continue;
                jettisonTransform.gameObject.SetActive(!jettisoned);
                applied = true;
            }

            return applied
                ? GhostPartEventOutcome.Applied
                : GhostPartEventOutcome.NoResolvedVisual;
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
            ApplyLightPowerEventWithOutcomes(state, partPersistentId, on, out _, out _);
        }

        /// <summary>
        /// P8 step 1: a light event drives TWO independent ghost surfaces - Unity
        /// <c>Light</c> components and Pattern-A colour-changer emissive materials - and
        /// a part can carry either, both or neither. Reporting one boolean for the pair
        /// is what made SHOWCASE-COLORCHANGER-APPLY-UNOBSERVABLE unanswerable: a row
        /// that toggled a Light but resolved no cabin-light material was
        /// indistinguishable from one that toggled both. Both outcomes come out
        /// separately here and are logged as separate `surface=` lines.
        ///
        /// The ON-while-blinking case is NOT a failure: the visual write belongs to
        /// UpdateBlinkingLights, which runs every frame off the playback flag this
        /// event just set. It reports DeferredToDriver on both surfaces.
        /// </summary>
        internal static void ApplyLightPowerEventWithOutcomes(
            GhostPlaybackState state,
            uint partPersistentId,
            bool on,
            out GhostPartEventOutcome lightOutcome,
            out GhostPartEventOutcome colorChangerOutcome)
        {
            if (state == null)
            {
                lightOutcome = GhostPartEventOutcome.NoFamilyState;
                colorChangerOutcome = GhostPartEventOutcome.NoFamilyState;
                return;
            }

            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.isOn = on;
            if (!on)
            {
                SetLightStateWithOutcomes(
                    state, partPersistentId, false, out lightOutcome, out colorChangerOutcome);
            }
            else if (!playbackState.blinkEnabled)
            {
                SetLightStateWithOutcomes(
                    state, partPersistentId, true, out lightOutcome, out colorChangerOutcome);
            }
            else
            {
                lightOutcome = GhostPartEventOutcome.DeferredToDriver;
                colorChangerOutcome = GhostPartEventOutcome.DeferredToDriver;
            }
        }

        internal static void ApplyLightBlinkModeEvent(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            ApplyLightBlinkModeEventWithOutcome(state, partPersistentId, enabled, blinkRateHz);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. A blink event writes PLAYBACK STATE only -
        /// the visual is UpdateBlinkingLights' job every frame - so a successful write
        /// reports on the `blink-state` surface, never on `light`. Reporting it as
        /// Applied on the light surface would claim a lamp changed when none did.
        /// </summary>
        internal static GhostPartEventOutcome ApplyLightBlinkModeEventWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            if (state == null) return GhostPartEventOutcome.NoFamilyState;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.blinkEnabled = enabled;
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
            return GhostPartEventOutcome.Applied;
        }

        internal static void ApplyLightBlinkRateEvent(GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            ApplyLightBlinkRateEventWithOutcome(state, partPersistentId, blinkRateHz);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting core. A non-positive rate is DISCARDED by the
        /// handler (the recorder can emit a zero on a module whose rate field was not
        /// readable), so it reports AlreadyInState rather than claiming a write.
        /// </summary>
        internal static GhostPartEventOutcome ApplyLightBlinkRateEventWithOutcome(
            GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            if (state == null) return GhostPartEventOutcome.NoFamilyState;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            if (blinkRateHz > 0f)
            {
                playbackState.blinkRateHz = blinkRateHz;
                return GhostPartEventOutcome.Applied;
            }
            return GhostPartEventOutcome.AlreadyInState;
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

        /// <summary>
        /// THE DRIVER PATH, and it stays CLASSIFY-FREE on purpose.
        /// <see cref="UpdateBlinkingLights"/> calls this once per blinking light per
        /// ghost per frame, and that cost multiplies across every ghost in the scene.
        /// Routing it through the outcome variant would run the light classifier PLUS
        /// the nested colour-changer scan (a per-part list walk with a per-material
        /// walk inside it) on every one of those frames only to discard the answer.
        /// The blink driver has no event to attribute an outcome to, so it does the
        /// writes and nothing else - exactly the work the pre-P8 body did.
        /// </summary>
        internal static void SetLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            WriteUnityLightState(state, partPersistentId, on);
            WriteColorChangerLightState(state, partPersistentId, on);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting variant for the two light surfaces, reached ONLY
        /// from a recorded light EVENT (<see cref="ApplyLightPowerEventWithOutcomes"/>),
        /// never from the per-frame blink driver above. Classifies each surface, then
        /// hands the write to the SAME single writer the driver path uses - one write
        /// loop per surface, not two.
        /// </summary>
        internal static void SetLightStateWithOutcomes(
            GhostPlaybackState state,
            uint partPersistentId,
            bool on,
            out GhostPartEventOutcome lightOutcome,
            out GhostPartEventOutcome colorChangerOutcome)
        {
            // Toggle Unity Light components (existing behavior)
            lightOutcome = ClassifyUnityLightApply(state, partPersistentId);
            if (lightOutcome == GhostPartEventOutcome.Applied)
                WriteUnityLightState(state, partPersistentId, on);

            // Toggle ColorChanger emissive materials (Pattern A: cabin lights)
            colorChangerOutcome = ApplyColorChangerLightStateWithOutcome(state, partPersistentId, on);
        }

        /// <summary>The single Unity-Light write loop, shared by both paths above.</summary>
        private static void WriteUnityLightState(
            GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.lightInfos == null) return;
            LightGhostInfo info;
            if (!state.lightInfos.TryGetValue(partPersistentId, out info)
                || info == null || info.lights == null)
                return;
            for (int i = 0; i < info.lights.Count; i++)
            {
                if (info.lights[i] != null)
                    info.lights[i].enabled = on;
            }
        }

        /// <summary>
        /// P8 step 1 precondition classifier for the Unity-Light half of a light event
        /// (pure). Applied means at least one non-null <c>Light</c> is present to write.
        /// </summary>
        internal static GhostPartEventOutcome ClassifyUnityLightApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.lightInfos == null) return GhostPartEventOutcome.NoFamilyState;
            LightGhostInfo info;
            if (!state.lightInfos.TryGetValue(partPersistentId, out info) || info == null
                || info.lights == null)
                return GhostPartEventOutcome.NoInfoForPart;
            for (int i = 0; i < info.lights.Count; i++)
            {
                if (info.lights[i] != null) return GhostPartEventOutcome.Applied;
            }
            return GhostPartEventOutcome.NoResolvedVisual;
        }

        /// <summary>
        /// The classify-free colour-changer half of the DRIVER path (see
        /// <see cref="SetLightState"/>). Same writer the event path uses.
        /// </summary>
        internal static void ApplyColorChangerLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            WriteColorChangerLightState(state, partPersistentId, on);
        }

        /// <summary>
        /// P8 step 1 outcome-reporting variant, reached only from a recorded light
        /// EVENT. Classifies (the four distinct ghost-build facts are documented on
        /// <see cref="ClassifyColorChangerLightApply"/>) and then hands the write to
        /// the single writer below.
        /// </summary>
        internal static GhostPartEventOutcome ApplyColorChangerLightStateWithOutcome(
            GhostPlaybackState state, uint partPersistentId, bool on)
        {
            GhostPartEventOutcome precondition =
                ClassifyColorChangerLightApply(state, partPersistentId);
            if (precondition != GhostPartEventOutcome.Applied) return precondition;

            WriteColorChangerLightState(state, partPersistentId, on);
            return GhostPartEventOutcome.Applied;
        }

        /// <summary>
        /// The single Pattern-A colour-changer write loop, shared by the driver path
        /// and the event path. Self-guarding, so the driver reaches it without a
        /// classifier pass.
        /// </summary>
        private static void WriteColorChangerLightState(
            GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.colorChangerInfos == null) return;

            List<ColorChangerGhostInfo> infos;
            if (!state.colorChangerInfos.TryGetValue(partPersistentId, out infos) || infos == null)
                return;

            for (int c = 0; c < infos.Count; c++)
            {
                var ccInfo = infos[c];
                if (ccInfo == null || !ccInfo.isCabinLight) continue; // Only Pattern A responds
                if (ccInfo.materials == null) continue;

                bool wrote = false;
                for (int i = 0; i < ccInfo.materials.Count; i++)
                {
                    if (ccInfo.materials[i].material != null)
                    {
                        ccInfo.materials[i].material.SetColor(
                            ccInfo.shaderProperty,
                            on ? ccInfo.materials[i].onColor : ccInfo.materials[i].offColor);
                        wrote = true;
                    }
                }

                if (wrote)
                    ParsekLog.VerboseRateLimited("Flight", $"cc-light-{partPersistentId}",
                        $"Part pid={partPersistentId}: applied color changer cabin light state={on}");
            }
        }

        /// <summary>
        /// P8 step 1 precondition classifier (pure). The three not-applied cases are
        /// distinct facts about the GHOST BUILD, and telling them apart is what closes
        /// SHOWCASE-COLORCHANGER-APPLY-UNOBSERVABLE:
        /// <list type="bullet">
        /// <item><description><c>no-family-state</c>: the ghost has no colour-changer
        /// dictionary at all (nothing on the craft uses ModuleColorChanger).</description></item>
        /// <item><description><c>no-info-for-part</c>: the dictionary exists but Pattern-A
        /// discovery resolved NOTHING for this part - hypothesis (a) in the S1.9
        /// reading, a genuine ghost-render gap.</description></item>
        /// <item><description><c>no-cabin-light-entry</c>: entries exist for the part but
        /// none is a cabin light, so a light event has nothing here to toggle -
        /// hypothesis (b), the part genuinely carries only Pattern-B (reentry char)
        /// colour changers.</description></item>
        /// <item><description><c>no-resolved-visual</c>: a cabin-light entry exists but
        /// every material in it resolved to null.</description></item>
        /// </list>
        /// </summary>
        internal static GhostPartEventOutcome ClassifyColorChangerLightApply(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.colorChangerInfos == null) return GhostPartEventOutcome.NoFamilyState;

            List<ColorChangerGhostInfo> infos;
            if (!state.colorChangerInfos.TryGetValue(partPersistentId, out infos) || infos == null)
                return GhostPartEventOutcome.NoInfoForPart;

            bool sawCabinLight = false;
            for (int c = 0; c < infos.Count; c++)
            {
                var ccInfo = infos[c];
                if (ccInfo == null || !ccInfo.isCabinLight) continue;

                sawCabinLight = true;
                if (ccInfo.materials == null) continue;
                for (int i = 0; i < ccInfo.materials.Count; i++)
                {
                    if (ccInfo.materials[i].material != null)
                        return GhostPartEventOutcome.Applied;
                }
            }

            return sawCabinLight
                ? GhostPartEventOutcome.NoResolvedVisual
                : GhostPartEventOutcome.NoCabinLightEntry;
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
        /// Maps a snapshot's <c>sit</c> value (a KSP <c>Vessel.Situations</c> NAME, which
        /// is what <c>ProtoVessel.Save</c> writes) to the <see cref="TerminalState"/> the
        /// finalize path would have stamped for that situation. Returns false when the
        /// value is missing or is not a situation this build knows.
        ///
        /// <para><b>This table is a MIRROR of
        /// <c>RecordingTree.DetermineTerminalState(int)</c> and must not drift from it.</b>
        /// That method is the one the Finalized route runs, keyed by the situation's
        /// integer value; this one is keyed by the NAME because an un-finalized recording
        /// has no live vessel left to ask — only its snapshot. The tie is enforced
        /// MECHANICALLY rather than by this comment:
        /// <c>SpawnSafetyNetTests.SituationNameMapping_MirrorsDetermineTerminalState</c>
        /// walks every <c>Vessel.Situations</c> value and asserts the two agree, so adding
        /// a situation to one and not the other reds. The int-keyed method additionally
        /// Warn-logs its unknown default; this one stays silent and returns false, because
        /// it is called from a per-frame spawn predicate.</para>
        ///
        /// <para>The caller decides what an unmappable value means.
        /// <see cref="ShouldSpawnAtRecordingEnd"/> treats it as NO EVIDENCE and leaves
        /// its answer unchanged — see the scope-boundary comment there. Note this
        /// differs from the int-keyed method, which Warn-logs and defaults unknown to
        /// <c>SubOrbital</c>; it has a live vessel and so a real reading to fall back
        /// on, where an absent snapshot field is the absence of a reading.</para>
        /// </summary>
        internal static bool TryMapSituationNameToTerminalState(
            string situationName, out TerminalState terminal)
        {
            terminal = TerminalState.SubOrbital;
            if (string.IsNullOrEmpty(situationName))
                return false;

            switch (situationName.Trim().ToUpperInvariant())
            {
                case "ORBITING":
                    terminal = TerminalState.Orbiting;
                    return true;
                case "LANDED":
                case "PRELAUNCH":
                    terminal = TerminalState.Landed;
                    return true;
                case "SPLASHED":
                    terminal = TerminalState.Splashed;
                    return true;
                case "FLYING":
                case "SUB_ORBITAL":
                case "ESCAPING":
                    terminal = TerminalState.SubOrbital;
                    return true;
                case "DOCKED":
                    terminal = TerminalState.Docked;
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

            // COMPLETING the check above for un-finalized recordings. The
            // IsSpawnableTerminal rejection ~30 lines up sits inside
            // `rec.TerminalStateValue.HasValue`, so a recording with NO terminal state
            // never reaches it — and the only other situation gate is the one above,
            // which knows FLYING/SUB_ORBITAL and nothing else. That left ESCAPING and
            // DOCKED (and an unreadable `sit`) reading as spawnable on the un-finalized
            // path while the Finalized route ghost-onlys them by design: ESCAPING maps
            // to SubOrbital, DOCKED to Docked, neither of which IsSpawnableTerminal
            // admits. Un-finalized recordings became a live population when the
            // outside-FLIGHT auto-commit started committing Limbo resume-stashes at
            // fidelity (ParsekScenario.ClassifyAutoCommitFidelity), so the gap is
            // reachable: an interplanetary probe on an escape trajectory, quickloaded
            // and then left in FLIGHT, would otherwise be spawn-eligible.
            //
            // The snapshot's own situation is the only evidence left of what the
            // finalize path would have decided, so mirror that decision exactly rather
            // than inventing a second policy. NOT REACHED for FLYING/SUB_ORBITAL (the
            // check above already returned, keeping its pinned reason string) — those
            // stay in the mapping table only so the table is complete and can be
            // drift-guarded against RecordingTree.DetermineTerminalState.
            //
            // This rejects SPAWNING, not the snapshot: the auto-commit's promise is
            // that a silent commit never DESTROYS a snapshot that exists, not that
            // every snapshot spawns. A ghost-only'd leaf keeps its GhostVisualSnapshot
            // and can still be spawned deliberately later.
            //
            // DELIBERATE SCOPE BOUNDARY: an ABSENT or unrecognised `sit` is left on
            // today's behaviour (allowed) rather than tightened to "reject on no
            // evidence". Every real snapshot carries the field — they are written by
            // VesselSpawner.TryBackupSnapshot -> ProtoVessel.Save — so the population
            // without one is synthetic test fixtures, of which 16 cells across 5 files
            // pin the current answer, `MergeDialogVesselTests.CanPersistVessel_-
            // NullTerminalState_ReturnsTrue` by name. Tightening it is a separable
            // change with its own blast radius and no demonstrated reachable case;
            // folding it in here would flip a named contract as a side effect of a
            // targeted fix. Pinned as unchanged-by-design by
            // SpawnSafetyNetTests.UnfinalizedRecording_AbsentSit_IsUnchangedByDesign.
            if (!rec.TerminalStateValue.HasValue)
            {
                TerminalState mirroredTerminal;
                if (TryMapSituationNameToTerminalState(
                        rec.VesselSnapshot.GetValue("sit"), out mirroredTerminal)
                    && !IsSpawnableTerminal(mirroredTerminal))
                {
                    return (false,
                        "unfinalized recording, snapshot situation maps to " +
                        $"terminal {mirroredTerminal}");
                }
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
        ///
        /// <para>
        /// RE-CONFIRMED as pid-only against the preserved-fleet world (#16 triage): now that
        /// Re-Fly preserves unrelated vessels, a same-craft STRANGER can hold the block closed
        /// and a standalone Rewind-to-Launch target then never materializes. That is the
        /// accepted side, not a bug to gate away, because the live re-flight this block exists
        /// for IS a same-craft stranger by construction — a fresh launch of the rewound craft
        /// carries the baked pid and a NEW Guid, so a Guid gate here would lift the block during
        /// exactly the re-fly it must hold for and resurrect the #573 duplicate. The narrower
        /// failure (one un-materialized terminal while a same-craft vessel is loaded) is
        /// recoverable through the watch-entry lift and the next rewind/revert reset; the
        /// duplicate is not. The block-kept case is logged (see
        /// <see cref="ResolveRewindSuppressionLiveLaunchPresence"/>) so it is diagnosable in
        /// KSP.log rather than silent.
        /// </para>
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
            else
            {
                // The block stays closed. Deliberately pid-only (see
                // AnyLiveRealVesselSharesRecordedCraft): the live same-craft vessel may be the
                // genuine re-flight this block exists for, OR — since Re-Fly preserves the
                // fleet — an unrelated launch of the same craft that merely reuses the baked
                // pid. Both keep the block; this line is what makes the second case
                // diagnosable when a player reports "my rewound vessel never came back".
                ParsekLog.VerboseRateLimited(
                    "Rewind",
                    "suppression-held|" + (rec.RecordingId ?? "(none)"),
                    $"same-recording spawn suppression HELD for standalone rewind target " +
                    $"rec={rec.RecordingId} vessel=\"{rec.VesselName}\" pid={rec.VesselPersistentId} — " +
                    "a live vessel shares this craft's persistentId (re-flight, or a preserved " +
                    "same-craft launch); recorded terminal stays unspawned (#573)",
                    5.0);
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
