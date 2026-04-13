using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        #region Ghost state

        // Primary ghost state: one GhostPlaybackState per active timeline ghost, keyed by recording index.
        internal readonly Dictionary<int, GhostPlaybackState> ghostStates = new Dictionary<int, GhostPlaybackState>();

        // Overlap ghosts: older cycle ghosts still alive due to negative loop interval.
        // NOTE: Overlap playback paths use raw StartUT/EndUT, not EffectiveLoop bounds.
        // Loop range narrowing (LoopStartUT/LoopEndUT) only affects the primary loop path.
        internal readonly Dictionary<int, List<GhostPlaybackState>> overlapGhosts = new Dictionary<int, List<GhostPlaybackState>>();

        // Loop phase offsets: shifted loop phase for Watch mode targeting.
        internal readonly Dictionary<int, double> loopPhaseOffsets = new Dictionary<int, double>();

        // Active explosion GameObjects (tracked for cleanup).
        internal readonly List<GameObject> activeExplosions = new List<GameObject>();

        // Anchor vessel tracking: which anchor vessels are loaded (for looped ghost lifecycle).
        internal readonly HashSet<uint> loadedAnchorVessels = new HashSet<uint>();

        // Diagnostic logging guards (log once per state transition, not per frame).
        internal readonly HashSet<int> loggedGhostEnter = new HashSet<int>();
        internal readonly HashSet<int> loggedReshow = new HashSet<int>();

        // Constants
        internal const int MaxOverlapGhostsPerRecording = 5;
        internal const double OverlapExplosionHoldSeconds = 3.0;
        internal const int MaxActiveExplosions = 30;
        internal const double HiddenGhostVisibleTierPrewarmBufferMeters = 5000.0;
        internal const double HiddenGhostEventPrewarmLookaheadSeconds = 2.0;

        // Per-frame batch counters (avoid per-ghost log spam)
        private int frameSpawnCount;
        private int frameDestroyCount;

        // Diagnostics: reusable Stopwatches (allocated once, no per-frame GC)
        private readonly Stopwatch updateStopwatch = new Stopwatch();
        private readonly Stopwatch spawnStopwatch = new Stopwatch();
        private readonly Stopwatch destroyStopwatch = new Stopwatch();

        // Deferred event lists (reused per frame to avoid GC allocation)
        private readonly List<PlaybackCompletedEvent> deferredCompletedEvents = new List<PlaybackCompletedEvent>();
        private readonly List<GhostLifecycleEvent> deferredCreatedEvents = new List<GhostLifecycleEvent>();

        // Dedup: prevent completed events from firing every frame for past-end recordings.
        // Rewind safety: DestroyAllGhosts() clears this set, and rewind always calls
        // DestroyAllGhosts (via ParsekFlight cleanup path), so completedEventFired
        // is guaranteed to be empty when playback restarts after a rewind.
        private readonly HashSet<int> completedEventFired = new HashSet<int>();
        // Destroyed debris can complete before EndUT when the recording captured a clear
        // destructive part event. Keep those indices suppressed until a rewind/reset so
        // they do not respawn while the original recording window is still in range.
        private readonly HashSet<int> earlyDestroyedDebrisCompleted = new HashSet<int>();

        #endregion

        #region Lifecycle events

        internal event Action<GhostLifecycleEvent> OnGhostCreated;
        internal event Action<GhostLifecycleEvent> OnGhostDestroyed;
        internal event Action<PlaybackCompletedEvent> OnPlaybackCompleted;
        internal event Action<LoopRestartedEvent> OnLoopRestarted;
        internal event Action<OverlapExpiredEvent> OnOverlapExpired;
        internal event Action OnAllGhostsDestroying;

        /// <summary>
        /// Delegate set by the policy to check if a ghost index is being held
        /// (pending spawn, watched, etc.). Used by stale past-end cleanup to avoid
        /// destroying ghosts that the policy is intentionally keeping alive.
        /// </summary>
        internal System.Func<int, bool> IsGhostHeld;

        /// <summary>
        /// Optional host-supplied exact watched-state predicate. Needed so overlap cycles
        /// don't inherit watched-only exemptions from the recording index alone.
        /// </summary>
        internal System.Func<int, GhostPlaybackState, bool> IsWatchedGhostStateResolver;

        /// <summary>
        /// Optional host-supplied logical distance resolver. Lets the engine classify hidden
        /// or inactive ghosts against their current playback position rather than a stale transform.
        /// </summary>
        internal System.Func<int, IPlaybackTrajectory, GhostPlaybackState, double, double> ResolvePlaybackDistanceOverride;

        // Camera events (engine detects cycle changes, host handles FlightCamera).
        internal event Action<CameraActionEvent> OnLoopCameraAction;
        internal event Action<CameraActionEvent> OnOverlapCameraAction;

        #endregion

        internal GhostPlaybackEngine(IGhostPositioner positioner)
        {
            this.positioner = positioner;
            ParsekLog.Info("Engine", "GhostPlaybackEngine created");
        }

        internal static bool HasLoadedGhostVisuals(GhostPlaybackState state)
        {
            return state != null && state.ghost != null;
        }

        internal static bool HasLoopCycleChanged(GhostPlaybackState state, long cycleIndex)
        {
            return state == null || state.loopCycleIndex != cycleIndex;
        }

        internal static bool ShouldPrewarmHiddenGhostForPartEvent(PartEventType type)
        {
            switch (type)
            {
                case PartEventType.Decoupled:
                case PartEventType.Destroyed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteDestroyed:
                case PartEventType.ShroudJettisoned:
                case PartEventType.DeployableExtended:
                case PartEventType.DeployableRetracted:
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                case PartEventType.FairingJettisoned:
                case PartEventType.Docked:
                case PartEventType.Undocked:
                case PartEventType.InventoryPartPlaced:
                case PartEventType.InventoryPartRemoved:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool ShouldPrewarmHiddenGhost(
            IPlaybackTrajectory traj, GhostPlaybackState state, double ghostDistance, double currentUT)
        {
            if (traj == null || state == null)
                return false;

            if (ghostDistance <= DistanceThresholds.GhostFlight.LoopSimplifiedMeters
                + HiddenGhostVisibleTierPrewarmBufferMeters)
            {
                return true;
            }

            if (traj.PartEvents == null || traj.PartEvents.Count == 0)
                return false;

            double lookaheadDeadline = currentUT + HiddenGhostEventPrewarmLookaheadSeconds;
            int startIndex = Math.Max(0, state.partEventIndex);
            for (int i = startIndex; i < traj.PartEvents.Count; i++)
            {
                var evt = traj.PartEvents[i];
                if (evt.ut > lookaheadDeadline)
                    break;
                if (evt.ut < currentUT)
                    continue;
                if (ShouldPrewarmHiddenGhostForPartEvent(evt.eventType))
                    return true;
            }

            return false;
        }

        #region Per-frame update

        /// <summary>
        /// Main per-frame update. Iterates all active trajectories, spawns/positions/destroys
        /// ghosts, fires lifecycle events. Called from host's Update().
        /// </summary>
        internal void UpdatePlayback(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            TrajectoryPlaybackFlags[] flags,
            FrameContext ctx)
        {
            if (trajectories == null || trajectories.Count == 0)
            {
                DiagnosticsState.playbackBudget = default;
                return;
            }
            if (positioner == null)
            {
                DiagnosticsState.playbackBudget = default;
                return; // Not yet wired (Phase 7)
            }
            if (flags == null || flags.Length < trajectories.Count)
            {
                DiagnosticsState.playbackBudget = default;
                ParsekLog.Warn("Engine", $"UpdatePlayback: flags array mismatch (flags={flags?.Length ?? 0} trajectories={trajectories.Count})");
                return;
            }

            // In map view, keep ghosts positioned so map markers draw at the correct
            // location even during high warp (#290). The mesh is invisible at orbital
            // distances anyway — only the icon+text matters.
            bool suppressGhosts = !ctx.mapViewEnabled
                && GhostPlaybackLogic.ShouldSuppressGhosts(ctx.warpRate);
            bool suppressVisualFx = GhostPlaybackLogic.ShouldSuppressVisualFx(ctx.warpRate);

            // Reset reshow dedup when entering warp suppression
            if (suppressGhosts)
                loggedReshow.Clear();

            deferredCompletedEvents.Clear();
            deferredCreatedEvents.Clear();
            frameSpawnCount = 0;
            frameDestroyCount = 0;

            // Diagnostics: start total frame timing
            updateStopwatch.Restart();
            // Reset spawn timer to zero — Start()/Stop() pairs inside SpawnGhost
            // accumulate across multiple spawns per frame (Start resumes, not resets)
            spawnStopwatch.Reset();
            destroyStopwatch.Reset();
            long spawnMicroseconds = 0;
            int ghostsProcessed = 0;

            for (int i = 0; i < trajectories.Count; i++)
            {
                var traj = trajectories[i];
                var f = flags[i];

                // Disabled/suppressed: destroy any active ghost before skipping
                if (f.skipGhost)
                {
                    if (ghostStates.ContainsKey(i))
                    {
                        DestroyAllOverlapGhosts(i);
                        DestroyGhost(i, traj, f, reason: "disabled/suppressed");
                    }
                    continue;
                }

                bool hasPoints = traj.Points != null && traj.Points.Count >= 2;
                bool hasOrbitData = traj.HasOrbitSegments;
                bool hasSurfaceData = traj.SurfacePos.HasValue;
                if (!hasPoints && !hasOrbitData && !hasSurfaceData) continue;

                if (ctx.currentUT < traj.StartUT)
                {
                    completedEventFired.Remove(i);
                    earlyDestroyedDebrisCompleted.Remove(i);
                }

                bool inRange = ctx.currentUT >= traj.StartUT && ctx.currentUT <= traj.EndUT;
                bool pastEnd = ctx.currentUT > traj.EndUT;
                bool pastEffectiveEnd = ctx.currentUT > f.chainEndUT;

                GhostPlaybackState state;
                ghostStates.TryGetValue(i, out state);
                bool ghostActive = HasLoadedGhostVisuals(state);
                if (ghostActive) ghostsProcessed++;

                // === Loop dispatch (before main rendering) ===
                if (ShouldLoopPlayback(traj))
                {
                    // Anchor gating: if anchor configured but not loaded, skip ghost
                    if (traj.LoopAnchorVesselId != 0 && !loadedAnchorVessels.Contains(traj.LoopAnchorVesselId))
                    {
                        if (ghostActive)
                        {
                            DestroyGhost(i, traj, f, reason: $"anchor {traj.LoopAnchorVesselId} unloaded");
                            DestroyAllOverlapGhosts(i);
                        }
                        continue;
                    }

                    UpdateLoopingPlayback(i, traj, f, ctx, suppressGhosts, suppressVisualFx);

                    // Clean up leftover overlap ghosts from loop->non-loop transition
                    List<GhostPlaybackState> leftoverOverlaps;
                    if (overlapGhosts.TryGetValue(i, out leftoverOverlaps) && leftoverOverlaps.Count > 0)
                    {
                        // Overlap cleanup handled in UpdateLoopingPlayback/UpdateOverlapPlayback
                    }

                    continue;
                }

                // Clean up overlap ghosts if recording switched from looping to non-looping
                if (overlapGhosts.ContainsKey(i))
                {
                    DestroyAllOverlapGhosts(i);
                    overlapGhosts.Remove(i);
                }

                // === Loop-synced debris: use parent's loop clock ===
                if (traj.LoopSyncParentIdx >= 0 && traj.LoopSyncParentIdx < trajectories.Count)
                {
                    var parent = trajectories[traj.LoopSyncParentIdx];
                    if (ShouldLoopPlayback(parent))
                    {
                        double parentLoopUT;
                        long parentCycle;
                        bool parentPaused;
                        if (!TryComputeLoopPlaybackUT(parent, ctx.currentUT, ctx.autoLoopIntervalSeconds,
                                out parentLoopUT, out parentCycle, out parentPaused, traj.LoopSyncParentIdx))
                        {
                            if (ghostActive)
                                DestroyGhost(i, traj, f, reason: "parent loop sync failed");
                            continue;
                        }

                        if (parentPaused || suppressGhosts)
                        {
                            if (state != null)
                                DestroyGhost(i, traj, f, reason: "parent loop paused/warp");
                            continue;
                        }

                        // Cycle change: rebuild ghost for clean visual state
                        if (state != null && state.loopCycleIndex != parentCycle)
                        {
                            DestroyGhost(i, traj, f, reason: "parent loop cycle change");
                            ghostActive = false;
                            state = null;
                            // Clear completed-event flag so the debris can play again
                            completedEventFired.Remove(i);
                        }

                        bool debrisInRange = parentLoopUT >= traj.StartUT && parentLoopUT <= traj.EndUT;
                        if (debrisInRange)
                        {
                            // Override UT for positioning — use parent's loop clock
                            var syncCtx = ctx;
                            syncCtx.currentUT = parentLoopUT;
                            if (RenderInRangeGhost(i, traj, f, syncCtx, suppressVisualFx,
                                    hasPoints, hasSurfaceData, hasOrbitData,
                                    allowEarlyDestroyedDebrisCompletion: false,
                                    ref state, ref ghostActive))
                            {
                                if (state != null)
                                    state.loopCycleIndex = parentCycle;
                            }
                        }
                        else if (state != null)
                        {
                            DestroyGhost(i, traj, f, reason: "outside debris UT range in parent loop");
                        }
                        continue;
                    }
                }

                // === Warp suppression: hide ghost during high warp ===
                if (suppressGhosts && state != null)
                {
                    if (ghostActive && state.ghost.activeSelf)
                        state.ghost.SetActive(false);
                    DestroyAllOverlapGhosts(i);
                    continue;
                }

                // === In-range rendering ===
                if (inRange)
                {
                    if (RenderInRangeGhost(i, traj, f, ctx, suppressVisualFx,
                            hasPoints, hasSurfaceData, hasOrbitData,
                            allowEarlyDestroyedDebrisCompletion: true,
                            ref state, ref ghostActive))
                        continue;
                }

                // === Past end: fire completed event, optionally destroy ===
                if ((pastEnd || pastEffectiveEnd)
                    && !completedEventFired.Contains(i)
                    && !earlyDestroyedDebrisCompleted.Contains(i))
                    HandlePastEndGhost(i, traj, f, ctx, state, ghostActive, hasPoints);

                // === Stale past-end ghost cleanup ===
                // Ghost survived past-end (e.g. watch hold), completed event already fired,
                // and not being held by the policy — destroy it. Prevents debris ghosts
                // from freezing at their last trajectory point indefinitely.
                if (state != null && completedEventFired.Contains(i)
                    && (IsGhostHeld == null || !IsGhostHeld(i)))
                {
                    DestroyGhost(i, traj, f, reason: "stale past-end ghost (no longer held)");
                }
            }

            // Post-loop: batch summary
            if (frameSpawnCount > 0 || frameDestroyCount > 0)
                ParsekLog.VerboseRateLimited("Engine", "frame-summary",
                    $"Frame: spawned={frameSpawnCount} destroyed={frameDestroyCount} active={ghostStates.Count}");

            // Post-loop: cleanup explosions
            for (int i = activeExplosions.Count - 1; i >= 0; i--)
            {
                if (activeExplosions[i] == null)
                    activeExplosions.RemoveAt(i);
            }

            // Fire deferred events AFTER loop completes
            for (int i = 0; i < deferredCreatedEvents.Count; i++)
                OnGhostCreated?.Invoke(deferredCreatedEvents[i]);

            for (int i = 0; i < deferredCompletedEvents.Count; i++)
                OnPlaybackCompleted?.Invoke(deferredCompletedEvents[i]);

            // Diagnostics: stop total frame timing and write results
            updateStopwatch.Stop();
            long totalMicroseconds = updateStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            spawnMicroseconds = spawnStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            long destroyMicroseconds = destroyStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            GhostObservability ghostObservability = CaptureGhostObservability();

            DiagnosticsState.playbackBudget.totalMicroseconds = totalMicroseconds;
            DiagnosticsState.playbackBudget.spawnMicroseconds = spawnMicroseconds;
            DiagnosticsState.playbackBudget.destroyMicroseconds = destroyMicroseconds;
            DiagnosticsState.playbackBudget.ghostsProcessed = ghostsProcessed;
            DiagnosticsState.playbackBudget.warpRate = ctx.warpRate;
            DiagnosticsState.playbackBudget.ghostObservability = ghostObservability;

            DiagnosticsState.playbackFrameHistory.Append(
                Time.realtimeSinceStartup, totalMicroseconds);

            // Budget threshold warning (8ms = half of 16.6ms frame budget at 60fps)
            DiagnosticsComputation.CheckPlaybackBudgetThreshold(totalMicroseconds, ghostsProcessed, ctx.warpRate);
        }

        /// <summary>
        /// Handles in-range ghost rendering: spawn if needed, position, apply visual events.
        /// Returns true if the ghost was processed (caller should continue to next iteration).
        /// </summary>
        private bool RenderInRangeGhost(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f,
            FrameContext ctx, bool suppressVisualFx,
            bool hasPoints, bool hasSurfaceData, bool hasOrbitData,
            bool allowEarlyDestroyedDebrisCompletion,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            if (allowEarlyDestroyedDebrisCompletion && earlyDestroyedDebrisCompleted.Contains(i))
                return true;

            if (state == null)
            {
                SpawnGhost(i, traj);
                ghostStates.TryGetValue(i, out state);
                ghostActive = HasLoadedGhostVisuals(state);
                if (ghostActive)
                {
                    loggedGhostEnter.Add(i);

                    if (OnGhostCreated != null)
                    {
                        deferredCreatedEvents.Add(new GhostLifecycleEvent
                        {
                            Index = i, Trajectory = traj, State = state, Flags = f
                        });
                    }
                }
            }

            if (state == null) return false;

            // Zone-based rendering
            double ghostDist = ResolvePlaybackDistance(i, traj, state, ctx.currentUT, ctx.activeVesselPos);
            var zoneResult = positioner.ApplyZoneRendering(i, state, traj, ghostDist, ctx.protectedIndex);

            // Flag events spawn permanent world vessels — apply regardless of zone distance (#249).
            // Unlike visual part events (mesh toggles), flags are independent entities that must
            // exist whether or not the ghost is visible.
            GhostPlaybackLogic.ApplyFlagEvents(state, traj, ctx.currentUT);

            if (zoneResult.hiddenByZone)
            {
                ghostActive = HandleHiddenGhostVisualState(
                    i, traj, state, ctx.currentUT, ctx.warpRate, ghostDist, overlapGhost: false,
                    hiddenReason: $"hidden by distance LOD at {ghostDist:F0}m");
                return true;
            }

            if (!HasLoadedGhostVisuals(state)
                && !EnsureGhostVisualsLoaded(i, traj, state, ctx.currentUT, "entered visible distance tier"))
            {
                ghostActive = false;
                return false;
            }

            ghostActive = HasLoadedGhostVisuals(state);
            if (!ghostActive) return false;

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, zoneResult.reduceFidelity);

            // Position the ghost
            if (hasPoints)
            {
                // Check for relative frame positioning (Phase 3b: docking/rendezvous)
                bool usedRelative = false;
                if (traj.TrackSections != null && traj.TrackSections.Count > 0)
                {
                    int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ctx.currentUT);
                    if (sectionIdx >= 0 && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
                    {
                        positioner.InterpolateAndPositionRelative(i, traj, state,
                            ctx.currentUT, suppressVisualFx,
                            traj.TrackSections[sectionIdx].anchorVesselId);
                        usedRelative = true;
                    }
                }
                if (!usedRelative)
                {
                    positioner.InterpolateAndPosition(i, traj, state, ctx.currentUT, suppressVisualFx);
                }
            }
            else if (hasSurfaceData)
            {
                positioner.PositionAtSurface(i, traj, state);
            }
            else if (hasOrbitData)
            {
                positioner.PositionFromOrbit(i, traj, state, ctx.currentUT);
            }

            // Apply visual events
            ApplyFrameVisuals(i, traj, state, ctx.currentUT, ctx.warpRate,
                zoneResult.skipPartEvents, suppressVisualFx || zoneResult.suppressVisualFx);

            if (allowEarlyDestroyedDebrisCompletion
                && TryHandleEarlyDestroyedDebrisCompletion(
                    i, traj, f, ctx, state, ghostActive, hasPoints))
            {
                return true;
            }

            return true;
        }

        private bool TryHandleEarlyDestroyedDebrisCompletion(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            FrameContext ctx, GhostPlaybackState state, bool ghostActive, bool hasPoints)
        {
            if (completedEventFired.Contains(index))
                return true;

            if (!GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(traj, out double explosionUT))
                return false;

            if (ctx.currentUT + 1e-6 < explosionUT)
                return false;

            completedEventFired.Add(index);
            earlyDestroyedDebrisCompleted.Add(index);

            if (ghostActive)
                TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

            deferredCompletedEvents.Add(new PlaybackCompletedEvent
            {
                Index = index,
                Trajectory = traj,
                State = state,
                Flags = flags,
                GhostWasActive = ghostActive,
                PastEffectiveEnd = ctx.currentUT > flags.chainEndUT,
                LastPoint = hasPoints ? traj.Points[traj.Points.Count - 1] : default,
                CurrentUT = ctx.currentUT
            });

            ParsekLog.Info("Engine",
                $"Early debris completion: ghost #{index} \"{traj.VesselName}\" " +
                $"explosionUT={explosionUT:F2} currentUT={ctx.currentUT:F2} endUT={traj.EndUT:F2}");
            return true;
        }

        /// <summary>
        /// Handles past-end ghost: positions at final point, triggers explosion if destroyed,
        /// fires completed event. Works for both active and inactive ghost cases.
        /// </summary>
        private void HandlePastEndGhost(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f,
            FrameContext ctx, GhostPlaybackState state, bool ghostActive, bool hasPoints)
        {
            completedEventFired.Add(i);

            if (ghostActive)
            {
                // Position ghost at final point
                if (hasPoints)
                {
                    var lastPoint = traj.Points[traj.Points.Count - 1];
                    positioner.PositionAtPoint(i, traj, state, lastPoint);
                }

                // Trigger explosion if destroyed
                TriggerExplosionIfDestroyed(state, traj, i, ctx.warpRate);
            }

            // Fire completed event (policy handles spawn/resources/camera).
            // Ghost stays alive — policy decides when to destroy
            // (may hold for watch-mode camera, or destroy immediately).
            deferredCompletedEvents.Add(new PlaybackCompletedEvent
            {
                Index = i,
                Trajectory = traj,
                State = state,
                Flags = f,
                GhostWasActive = ghostActive,
                PastEffectiveEnd = ctx.currentUT > f.chainEndUT,
                LastPoint = hasPoints ? traj.Points[traj.Points.Count - 1] : default,
                CurrentUT = ctx.currentUT
            });
        }

        /// <summary>
        /// Applies per-frame visual events to a ghost: part events, flag events,
        /// reentry FX, and RCS emission state. Called after positioning.
        /// When skipPartEvents is true, only reentry FX and RCS are applied.
        /// </summary>
        private void ApplyFrameVisuals(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, float warpRate,
            bool skipPartEvents, bool suppressVisualFx, bool allowTransientEffects = true)
        {
            if (!skipPartEvents)
            {
                GhostPlaybackLogic.ApplyPartEvents(index, traj, ut, state, allowTransientEffects);
                // Flag events are applied earlier in RenderInRangeGhost, before zone check (#249).
            }

            if (suppressVisualFx)
            {
                GhostPlaybackLogic.StopAllEngineFx(state);
                GhostPlaybackLogic.StopAllRcsFx(state);
                GhostPlaybackLogic.StopAllRcsEmissions(state);
                GhostPlaybackLogic.ResetReentryFx(state, index);
                GhostPlaybackLogic.MuteAllAudio(state);
            }
            else
            {
                UpdateReentryFx(index, state, traj.VesselName, warpRate);
                GhostPlaybackLogic.RestoreAllRcsEmissions(state);
                GhostPlaybackLogic.UnmuteAllAudio(state);
            }

            // Per-frame atmosphere attenuation — smoothly fade audio as ghost ascends/descends.
            // Runs after part events (which may start/stop audio) and after mute/unmute.
            GhostPlaybackLogic.UpdateAudioAtmosphere(state);
        }

        /// <summary>
        /// Handles looping ghost playback for a single trajectory.
        /// Manages cycle changes, ghost spawn/destroy, pause windows.
        /// Fires CameraActionEvents for watch mode interactions (engine does not
        /// know about watch mode — ParsekFlight handles camera in event handlers).
        /// </summary>
        private void UpdateLoopingPlayback(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            bool suppressGhosts, bool suppressVisualFx)
        {
            GhostPlaybackState state;
            ghostStates.TryGetValue(index, out state);
            bool ghostActive = HasLoadedGhostVisuals(state);

            double intervalSeconds = GetLoopIntervalSeconds(traj, ctx.autoLoopIntervalSeconds);
            double duration = traj.EndUT - traj.StartUT;

            // High time warp: hide ghost, destroy overlaps
            if (suppressGhosts)
            {
                if (ghostActive && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.Info("Engine",
                        $"Ghost #{index} \"{traj.VesselName}\" (loop) hidden: warp > {GhostPlaybackLogic.GhostHideWarpThreshold}x");
                    // Fire camera event so host can exit watch mode
                    OnLoopCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index, Action = CameraActionType.ExitWatch,
                        Trajectory = traj, Flags = flags
                    });
                }
                DestroyAllOverlapGhosts(index);
                return;
            }

            // For negative intervals: use multi-cycle overlap path
            if (intervalSeconds < 0)
            {
                UpdateOverlapPlayback(index, traj, flags, ctx, state,
                    intervalSeconds, duration, suppressVisualFx);
                return;
            }

            // --- Positive/zero interval: single ghost path (no overlap) ---
            DestroyAllOverlapGhosts(index);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;
            if (!TryComputeLoopPlaybackUT(traj, ctx.currentUT, ctx.autoLoopIntervalSeconds,
                    out loopUT, out cycleIndex, out inPauseWindow, index))
            {
                if (ghostActive)
                    DestroyGhost(index, traj, flags, reason: "loop UT computation failed");
                return;
            }

            // Rebuild once per loop cycle to guarantee clean visual state and event indices.
            bool cycleChanged = HasLoopCycleChanged(state, cycleIndex);
            if (cycleChanged && state != null)
            {
                // Position at final point so explosion appears at crash site
                if (traj.Points.Count > 0 && state != null && state.ghost != null)
                    positioner.PositionAtPoint(index, traj, state, traj.Points[traj.Points.Count - 1]);

                bool needsExplosion = state != null
                    && traj.TerminalStateValue == TerminalState.Destroyed
                    && !state.explosionFired;

                TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

                // Fire camera event for cycle change (host handles camera anchor/hold/retarget)
                OnLoopCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = index,
                    Action = needsExplosion ? CameraActionType.ExplosionHoldStart : CameraActionType.ExplosionHoldEnd,
                    AnchorPosition = state?.ghost != null ? state.ghost.transform.position : Vector3.zero,
                    HoldUntilUT = ctx.currentUT + OverlapExplosionHoldSeconds,
                    Trajectory = traj, Flags = flags
                });

                // Fire loop restarted event
                OnLoopRestarted?.Invoke(new LoopRestartedEvent
                {
                    Index = index, Trajectory = traj, State = state, Flags = flags,
                    PreviousCycleIndex = state?.loopCycleIndex ?? 0,
                    NewCycleIndex = cycleIndex,
                    ExplosionFired = needsExplosion,
                    ExplosionPosition = state?.ghost != null ? state.ghost.transform.position : Vector3.zero
                });

                GhostPlaybackLogic.ResetReentryFx(state, index);
                DestroyGhost(index, traj, flags, reason: "loop cycle boundary");
                ghostActive = false;
                state = null;
            }

            // Looped ghost distance gating
            double loopGhostDistance = ResolvePlaybackDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);

            var (_, loopSimplified) =
                GhostPlaybackLogic.EvaluateLoopedGhostSpawn(loopGhostDistance);

            if (state == null)
            {
                SpawnGhost(index, traj);
                if (!ghostStates.TryGetValue(index, out state) || state == null)
                    return;
                state.loopCycleIndex = cycleIndex;
                ParsekLog.VerboseRateLimited("Engine", $"enter-{index}",
                    $"Ghost ENTERED range: #{index} \"{traj.VesselName}\" at UT {ctx.currentUT:F1} (loop cycle={cycleIndex})");

                // Defer OnGhostCreated for policy (ghost map ProtoVessel creation)
                if (OnGhostCreated != null)
                {
                    deferredCreatedEvents.Add(new GhostLifecycleEvent
                    {
                        Index = index, Trajectory = traj, State = state, Flags = flags
                    });
                }

                // Fire camera event for retarget to new ghost
                OnLoopCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = index, Action = CameraActionType.RetargetToNewGhost,
                    NewCycleIndex = cycleIndex,
                    GhostPivot = state.cameraPivot,
                    Trajectory = traj, Flags = flags
                });

                ghostActive = true;
            }

            if (state == null)
                return;

            // Zone-based rendering
            double loopZoneDistance = ResolvePlaybackDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);
            var zoneResult = positioner.ApplyZoneRendering(index, state, traj, loopZoneDistance, ctx.protectedIndex);
            if (zoneResult.hiddenByZone)
            {
                ghostActive = HandleHiddenGhostVisualState(
                    index, traj, state, loopUT, ctx.warpRate, loopZoneDistance, overlapGhost: false,
                    hiddenReason: $"loop hidden by distance LOD at {loopZoneDistance:F0}m");
                return;
            }

            if (!HasLoadedGhostVisuals(state)
                && !EnsureGhostVisualsLoaded(index, traj, state, loopUT, "loop re-entered visible distance tier"))
            {
                return;
            }

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, zoneResult.reduceFidelity);
            bool forceWatchedFullFidelity = IsWatchedGhostState(index, state, ctx);
            bool skipLoopPartEvents = zoneResult.skipPartEvents || (loopSimplified && !forceWatchedFullFidelity);
            bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;

            // Pause window: position at end, hide parts, zero velocity for reentry decay
            if (inPauseWindow)
            {
                HandleLoopPauseWindow(index, traj, state, ctx.warpRate);
                return;
            }

            // Position the loop ghost
            positioner.PositionLoop(index, traj, state, loopUT, effectiveSuppressVisualFx);

            // Apply visual events
            if (!skipLoopPartEvents)
                ApplyFrameVisuals(index, traj, state, loopUT, ctx.warpRate, false, effectiveSuppressVisualFx);
        }

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        private void UpdateOverlapPlayback(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            GhostPlaybackState primaryState,
            double intervalSeconds, double duration, bool suppressVisualFx)
        {
            if (ctx.currentUT < traj.StartUT)
            {
                if (primaryState != null) DestroyGhost(index, traj, flags, reason: "before start UT");
                DestroyAllOverlapGhosts(index);
                return;
            }

            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration < GhostPlaybackLogic.MinCycleDuration)
                cycleDuration = GhostPlaybackLogic.MinCycleDuration;

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(ctx.currentUT, traj.StartUT, traj.EndUT, intervalSeconds,
                MaxOverlapGhostsPerRecording, out firstCycle, out lastCycle);

            List<GhostPlaybackState> overlaps;
            if (!overlapGhosts.TryGetValue(index, out overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                overlapGhosts[index] = overlaps;
            }

            // Primary ghost represents the newest (lastCycle)
            bool primaryCycleChanged = HasLoopCycleChanged(primaryState, lastCycle);

            if (primaryCycleChanged)
            {
                // Move old primary to overlap list if still alive
                if (primaryState != null)
                {
                    ghostStates.Remove(index);
                    if (primaryState.ghost != null)
                        GhostPlaybackLogic.MuteAllAudio(primaryState); // overlap ghosts get no audio
                    overlaps.Add(primaryState);
                    ParsekLog.VerboseRateLimited("Engine", "overlap-move",
                        $"Ghost #{index} cycle={primaryState.loopCycleIndex} moved to overlap list (audio muted)");
                }

                // Spawn new primary for lastCycle
                SpawnGhost(index, traj);
                if (!ghostStates.TryGetValue(index, out primaryState) || primaryState == null)
                {
                    ParsekLog.Warn("Engine",
                        $"Overlap: SpawnGhost failed for #{index} cycle={lastCycle}");
                    return;
                }
                primaryState.loopCycleIndex = lastCycle;
                ParsekLog.VerboseRateLimited("Engine", $"enter-{index}",
                    $"Ghost ENTERED range: #{index} \"{traj.VesselName}\" cycle={lastCycle} at UT {ctx.currentUT:F1} (overlap)");

                // Defer OnGhostCreated for policy (ghost map ProtoVessel creation)
                if (OnGhostCreated != null)
                {
                    deferredCreatedEvents.Add(new GhostLifecycleEvent
                    {
                        Index = index, Trajectory = traj, State = primaryState, Flags = flags
                    });
                }

                // Fire camera event for retarget
                OnOverlapCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = index, Action = CameraActionType.RetargetToNewGhost,
                    NewCycleIndex = lastCycle,
                    GhostPivot = primaryState.cameraPivot,
                    Trajectory = traj, Flags = flags
                });
            }

            // Position primary ghost
            // Note: anchor-relative positioning is handled internally by positioner.PositionLoop,
            // which calls ShouldUseLoopAnchor itself. No need to pre-compute here.
            if (primaryState != null)
            {
                bool primaryReady = true;
                double cycleStartUT = traj.StartUT + lastCycle * cycleDuration;
                double phase = Math.Max(0, Math.Min(ctx.currentUT - cycleStartUT, duration));
                double loopUT = traj.StartUT + phase;
                double primaryDistance = ResolvePlaybackDistance(
                    index, traj, primaryState, loopUT, ctx.activeVesselPos);
                var zoneResult = positioner.ApplyZoneRendering(
                    index, primaryState, traj, primaryDistance, ctx.protectedIndex);
                if (zoneResult.hiddenByZone)
                {
                    HandleHiddenGhostVisualState(
                        index, traj, primaryState, loopUT, ctx.warpRate, primaryDistance, overlapGhost: false,
                        hiddenReason: $"overlap primary hidden by distance LOD at {primaryDistance:F0}m");
                }
                else
                {
                    if (!HasLoadedGhostVisuals(primaryState)
                        && !EnsureGhostVisualsLoaded(index, traj, primaryState, loopUT, "overlap primary re-entered visible distance tier"))
                        primaryReady = false;

                    if (primaryReady)
                    {
                        GhostPlaybackLogic.ApplyDistanceLodFidelity(primaryState, zoneResult.reduceFidelity);
                        bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
                        positioner.PositionLoop(index, traj, primaryState, loopUT, effectiveSuppressVisualFx);
                        ApplyFrameVisuals(index, traj, primaryState, loopUT, ctx.warpRate,
                            zoneResult.skipPartEvents, effectiveSuppressVisualFx);
                    }
                }
            }

            // Update overlap ghosts (older cycles)
            UpdateExpireAndPositionOverlaps(index, traj, flags, ctx, overlaps,
                duration, cycleDuration, suppressVisualFx);
        }

        /// <summary>
        /// Iterates overlap ghosts (older cycles) in reverse. Expires cycles whose phase
        /// exceeds duration (triggers explosion + camera event), removes null entries,
        /// and positions remaining overlaps at their current loop UT.
        /// </summary>
        private void UpdateExpireAndPositionOverlaps(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            List<GhostPlaybackState> overlaps,
            double duration, double cycleDuration, bool suppressVisualFx)
        {
            for (int i = overlaps.Count - 1; i >= 0; i--)
            {
                var ovState = overlaps[i];
                if (ovState == null)
                {
                    overlaps.RemoveAt(i);
                    continue;
                }

                long cycle = ovState.loopCycleIndex;
                double cycleStart = traj.StartUT + cycle * cycleDuration;
                double phase = ctx.currentUT - cycleStart;

                // Expired cycle
                if (phase > duration)
                {
                    if (traj.Points.Count > 0 && ovState.ghost != null)
                        positioner.PositionAtPoint(index, traj, ovState, traj.Points[traj.Points.Count - 1]);
                    TriggerExplosionIfDestroyed(ovState, traj, index, ctx.warpRate);

                    // Fire camera event for overlap expiry
                    OnOverlapCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index,
                        Action = CameraActionType.ExplosionHoldStart,
                        NewCycleIndex = cycle,
                        AnchorPosition = ovState.ghost != null ? ovState.ghost.transform.position : Vector3.zero,
                        HoldUntilUT = ctx.currentUT + OverlapExplosionHoldSeconds,
                        Trajectory = traj, Flags = flags
                    });

                    // Fire overlap expired event
                    OnOverlapExpired?.Invoke(new OverlapExpiredEvent
                    {
                        Index = index, Trajectory = traj, State = ovState, Flags = flags,
                        CycleIndex = cycle,
                        ExplosionFired = traj.TerminalStateValue == TerminalState.Destroyed,
                        ExplosionPosition = ovState.ghost != null ? ovState.ghost.transform.position : Vector3.zero
                    });

                    ParsekLog.VerboseRateLimited("Engine", "overlap-expired",
                        $"Ghost EXITED range: #{index} \"{traj.VesselName}\" cycle={cycle} (overlap expired)");
                    DestroyOverlapGhostState(ovState);
                    overlaps.RemoveAt(i);
                    continue;
                }

                phase = Math.Max(0, Math.Min(phase, duration));
                double loopUT = traj.StartUT + phase;
                double overlapDistance = ResolvePlaybackDistance(
                    index, traj, ovState, loopUT, ctx.activeVesselPos);
                var zoneResult = positioner.ApplyZoneRendering(
                    index, ovState, traj, overlapDistance, ctx.protectedIndex);
                if (zoneResult.hiddenByZone)
                {
                    HandleHiddenGhostVisualState(
                        index, traj, ovState, loopUT, ctx.warpRate, overlapDistance, overlapGhost: true,
                        hiddenReason: $"overlap hidden by distance LOD at {overlapDistance:F0}m");
                    continue;
                }

                if (!HasLoadedGhostVisuals(ovState)
                    && !EnsureGhostVisualsLoaded(index, traj, ovState, loopUT, "overlap re-entered visible distance tier"))
                {
                    continue;
                }

                GhostPlaybackLogic.ApplyDistanceLodFidelity(ovState, zoneResult.reduceFidelity);
                bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
                positioner.PositionLoop(index, traj, ovState, loopUT, effectiveSuppressVisualFx);
                ApplyFrameVisuals(index, traj, ovState, loopUT, ctx.warpRate,
                    zoneResult.skipPartEvents, effectiveSuppressVisualFx);
            }
        }

        /// <summary>
        /// Handles the loop pause window: positions ghost at the final trajectory point,
        /// hides all parts (crash-site hold), zeroes velocity for reentry FX decay,
        /// and triggers explosion if the recording ended in destruction.
        /// </summary>
        private void HandleLoopPauseWindow(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, float warpRate)
        {
            var lastPt = traj.Points[traj.Points.Count - 1];
            positioner.PositionAtPoint(index, traj, state, lastPt);
            // PositionAtPoint now sets state.lastInterpolatedAltitude to the
            // clamped altitude (#282). Don't overwrite it here with the raw
            // lastPt.altitude on the first pause-window frame, or the watch
            // overlay reports the buried recorded value for that one frame.
            // Still seed the body name (PositionAtPoint doesn't touch it).
            if (string.IsNullOrEmpty(state.lastInterpolatedBodyName))
            {
                state.lastInterpolatedBodyName = lastPt.bodyName;
            }
            TriggerExplosionIfDestroyed(state, traj, index, warpRate);
            if (!state.pauseHidden)
            {
                state.pauseHidden = true;
                GhostPlaybackLogic.HideAllGhostParts(state);
            }
            if (state.reentryFxInfo != null)
            {
                state.lastInterpolatedVelocity = Vector3.zero;
                UpdateReentryFx(index, state, traj.VesselName, warpRate);
            }
        }

        #endregion

        #region Loop utilities

        /// <summary>
        /// Returns the effective loop start UT, falling back to traj.StartUT when
        /// LoopStartUT is NaN or out of range.
        /// </summary>
        internal static double EffectiveLoopStartUT(IPlaybackTrajectory traj)
        {
            double loopStart = traj.LoopStartUT;
            if (!double.IsNaN(loopStart) && loopStart >= traj.StartUT && loopStart < traj.EndUT)
            {
                // Cross-validate: effective start must be less than effective end
                double loopEnd = traj.LoopEndUT;
                double effectiveEnd = (!double.IsNaN(loopEnd) && loopEnd <= traj.EndUT && loopEnd > traj.StartUT)
                    ? loopEnd : traj.EndUT;
                if (loopStart >= effectiveEnd)
                    return traj.StartUT;
                return loopStart;
            }
            return traj.StartUT;
        }

        /// <summary>
        /// Returns the effective loop end UT, falling back to traj.EndUT when
        /// LoopEndUT is NaN or out of range.
        /// </summary>
        internal static double EffectiveLoopEndUT(IPlaybackTrajectory traj)
        {
            double loopEnd = traj.LoopEndUT;
            if (!double.IsNaN(loopEnd) && loopEnd <= traj.EndUT && loopEnd > traj.StartUT)
            {
                // Cross-validate: effective end must be greater than effective start
                double loopStart = traj.LoopStartUT;
                double effectiveStart = (!double.IsNaN(loopStart) && loopStart >= traj.StartUT && loopStart < traj.EndUT)
                    ? loopStart : traj.StartUT;
                if (loopEnd <= effectiveStart)
                    return traj.EndUT;
                return loopEnd;
            }
            return traj.EndUT;
        }

        /// <summary>Whether the trajectory should loop (has enough points and duration).</summary>
        internal static bool ShouldLoopPlayback(IPlaybackTrajectory traj)
        {
            if (traj == null || !traj.LoopPlayback || traj.Points == null || traj.Points.Count < 2)
                return false;
            double start = EffectiveLoopStartUT(traj);
            double end = EffectiveLoopEndUT(traj);
            return end - start > GhostPlaybackLogic.MinLoopDurationSeconds;
        }

        /// <summary>Resolve the effective loop interval for a trajectory.</summary>
        internal double GetLoopIntervalSeconds(IPlaybackTrajectory traj, double autoLoopIntervalSeconds)
        {
            return GhostPlaybackLogic.ResolveLoopInterval(
                traj, autoLoopIntervalSeconds,
                GhostPlaybackLogic.DefaultLoopIntervalSeconds,
                GhostPlaybackLogic.MinCycleDuration);
        }

        /// <summary>
        /// Compute the effective UT within a looping trajectory, accounting for cycle index,
        /// pause windows, and loop phase offsets. Returns false if the trajectory is not loopable.
        /// </summary>
        internal bool TryComputeLoopPlaybackUT(
            IPlaybackTrajectory traj,
            double currentUT,
            double autoLoopIntervalSeconds,
            out double loopUT,
            out long cycleIndex,
            out bool inPauseWindow,
            int recIdx = -1)
        {
            cycleIndex = 0;
            inPauseWindow = false;
            if (traj == null || traj.Points == null || traj.Points.Count < 2)
            {
                loopUT = 0;
                return false;
            }

            double loopStart = EffectiveLoopStartUT(traj);
            double loopEnd = EffectiveLoopEndUT(traj);
            loopUT = loopStart;
            if (currentUT < loopStart) return false;

            double duration = loopEnd - loopStart;
            if (duration <= GhostPlaybackLogic.MinLoopDurationSeconds) return false;

            double intervalSeconds = GetLoopIntervalSeconds(traj, autoLoopIntervalSeconds);
            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration <= GhostPlaybackLogic.MinLoopDurationSeconds)
                cycleDuration = duration;

            double elapsed = currentUT - loopStart;

            // Apply loop phase offset (set by Watch mode to reset ghost to recording start)
            double phaseOffset;
            if (recIdx >= 0 && loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
            {
                ParsekLog.Verbose("Engine", $"TryComputeLoopPlaybackUT: applying phase offset {phaseOffset:F2}s for recIdx={recIdx}");
                elapsed += phaseOffset;
            }

            cycleIndex = (long)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0) cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (intervalSeconds > 0 && cycleTime > duration)
            {
                inPauseWindow = true;
                loopUT = loopEnd;
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited("Engine", "loop_pause_" + recIdx,
                        $"TryComputeLoopPlaybackUT: in pause window for recIdx={recIdx}, cycle={cycleIndex}");
                return true;
            }

            loopUT = loopStart + Math.Min(cycleTime, duration);
            return true;
        }

        /// <summary>Whether any time warp is active (using FrameContext values, no KSP globals).</summary>
        internal static bool IsAnyWarpActive(FrameContext ctx)
        {
            return GhostPlaybackLogic.IsAnyWarpActive(ctx.warpRateIndex, ctx.warpRate);
        }

        private bool IsWatchedGhostState(int recordingIndex, GhostPlaybackState state, FrameContext ctx)
        {
            if (state == null)
                return false;

            if (IsWatchedGhostStateResolver != null)
                return IsWatchedGhostStateResolver(recordingIndex, state);

            return GhostPlaybackLogic.IsProtectedGhost(
                ctx.protectedIndex, ctx.protectedLoopCycleIndex,
                recordingIndex, state.loopCycleIndex);
        }

        private double ResolvePlaybackDistance(
            int recordingIndex, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d activeVesselPos)
        {
            if (ResolvePlaybackDistanceOverride != null)
            {
                double resolved = ResolvePlaybackDistanceOverride(
                    recordingIndex, traj, state, playbackUT);
                if (!double.IsNaN(resolved) && !double.IsInfinity(resolved) && resolved >= 0)
                    return resolved;
            }

            if (state != null && state.ghost != null)
                return Vector3d.Distance((Vector3d)state.ghost.transform.position, activeVesselPos);

            return double.MaxValue;
        }

        /// <summary>Whether any time warp is active (reads KSP globals directly — host convenience wrapper).</summary>
        internal static bool IsAnyWarpActiveFromGlobals()
        {
            return GhostPlaybackLogic.IsAnyWarpActive(TimeWarp.CurrentRateIndex, TimeWarp.CurrentRate);
        }

        #endregion

        #region Reentry FX

        /// <summary>
        /// Update reentry visual effects for a ghost. Computes atmospheric density,
        /// Mach number, and intensity, then drives glow/fire/shell layers.
        /// </summary>
        internal void UpdateReentryFx(int recIdx, GhostPlaybackState state, string vesselName, float warpRate)
        {
            var info = state.reentryFxInfo;
            if (info == null || state.ghost == null) return;

            if (GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
            {
                DriveReentryToZero(info, recIdx, state.lastInterpolatedBodyName,
                    state.lastInterpolatedAltitude, vesselName);
                return;
            }

            Vector3 interpolatedVel = state.lastInterpolatedVelocity;
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName))
            {
                DriveReentryToZero(info, recIdx, bodyName, 0.0, vesselName, state);
                return;
            }

            // TODO(standalone): FlightGlobals.Bodies is a KSP global dependency.
            // For standalone extraction, inject a body-lookup delegate or interface
            // (e.g., Func<string, CelestialBody> bodyLookup) via the constructor.
            // CelestialBody is needed here for atmosphere physics (pressure, temperature,
            // density, speed of sound), so this cannot be easily abstracted away.
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Engine", $"ghost-{recIdx}-nobody",
                    $"ReentryFx: body '{bodyName}' not found — skipping");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            Vector3 surfaceVel = interpolatedVel - (Vector3)body.getRFrmVel(state.ghost.transform.position);
            float speed = surfaceVel.magnitude;

            if (!body.atmosphere)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            if (altitude >= body.atmosphereDepth)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure < 0 || double.IsNaN(temperature) || temperature < 0)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double speedOfSound = body.GetSpeedOfSound(pressure, density);
            float machNumber = (speedOfSound > 0) ? (float)(speed / speedOfSound) : 0f;
            float rawIntensity = GhostVisualBuilder.ComputeReentryIntensity(speed, (float)density, machNumber);

            float smoothedIntensity = Mathf.Lerp(info.lastIntensity, rawIntensity,
                1f - Mathf.Exp(-GhostVisualBuilder.ReentrySmoothingRate * Time.deltaTime));
            if (smoothedIntensity < 0.001f && rawIntensity == 0f)
                smoothedIntensity = 0f;

            DriveReentryLayers(info, smoothedIntensity, surfaceVel, recIdx, bodyName, altitude, machNumber, vesselName, state);
            info.lastIntensity = smoothedIntensity;
        }

        private void DriveReentryToZero(ReentryFxInfo info, int recIdx, string bodyName, double altitude,
            string vesselName, GhostPlaybackState state = null)
        {
            DriveReentryLayers(info, 0f, Vector3.zero, recIdx, bodyName, altitude, 0f, vesselName, state);
            info.lastIntensity = 0f;
        }

        private void DriveReentryLayers(ReentryFxInfo info, float intensity, Vector3 surfaceVel,
            int recIdx, string bodyName, double altitude, float machNumber, string vesselName,
            GhostPlaybackState state = null)
        {
            bool wasActive = info.lastIntensity > 0f;
            bool isActive = intensity > 0f;

            if (isActive && !wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Engine",
                    $"ReentryFx: Activated for ghost #{recIdx} \"{vesselName}\" — intensity={intensity:F2}, Mach={machNumber:F2}, speed={speed:F0} m/s, alt={altitude:F0} m, body={bodyName}");
            }
            else if (!isActive && wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Engine",
                    $"ReentryFx: Deactivated for ghost #{recIdx} — intensity dropped to 0 (speed={speed:F0} m/s, alt={altitude:F0} m)");
            }

            if (isActive)
            {
                ParsekLog.VerboseRateLimited("Engine", $"ghost-{recIdx}-intensity",
                    $"ReentryFx: ghost #{recIdx} intensity={intensity:F2} speed={surfaceVel.magnitude:F0} alt={altitude:F0}");
            }

            // Layer A: Heat glow (material emission)
            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;

                    if (intensity <= GhostVisualBuilder.ReentryLayerAThreshold)
                    {
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty, ms.coldColor);
                    }
                    else
                    {
                        float glowFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerAThreshold, 1f, intensity);
                        Color targetEmission = Color.Lerp(GhostVisualBuilder.ReentryHotEmissionLow,
                            GhostVisualBuilder.ReentryHotEmissionHigh, glowFraction);
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty,
                                Color.Lerp(ms.coldEmission, ms.coldEmission + targetEmission, glowFraction));
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty,
                                Color.Lerp(ms.coldColor, ms.hotColor, glowFraction));
                    }
                }
            }

            // Apply ablation char to heat shield parts
            if (state != null)
                GhostPlaybackLogic.ApplyColorChangerCharState(state, intensity);

            // Fire envelope particles
            if (info.fireParticles != null)
            {
                if (intensity > GhostVisualBuilder.ReentryFireThreshold)
                {
                    float fireFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryFireThreshold, 1f, intensity);

                    var emissionMod = info.fireParticles.emission;
                    emissionMod.rateOverTimeMultiplier = Mathf.Lerp(
                        GhostVisualBuilder.ReentryFireEmissionMin,
                        GhostVisualBuilder.ReentryFireEmissionMax, fireFraction);

                    var mainMod = info.fireParticles.main;
                    mainMod.startSizeMultiplier = Mathf.Lerp(0.8f, 2.0f, fireFraction);

                    if (!info.fireParticles.isPlaying)
                        info.fireParticles.Play();
                }
                else
                {
                    if (info.fireParticles.isPlaying)
                        info.fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            // Fire shell overlay
            if (info.fireShellMeshes != null && info.fireShellMaterial != null
                && intensity > GhostVisualBuilder.ReentryFireThreshold)
            {
                Vector3 velDir = surfaceVel.normalized;
                float maxOffset = info.vesselLength * GhostVisualBuilder.ReentryFireShellMaxOffset * intensity;
                float baseTint = Mathf.Lerp(0.026f, 0.156f, intensity);

                for (int pass = 0; pass < GhostVisualBuilder.ReentryFireShellPasses; pass++)
                {
                    float t = (pass + 1f) / GhostVisualBuilder.ReentryFireShellPasses;
                    Vector3 offset = -velDir * (maxOffset * t);
                    float alpha = baseTint * (1f - t * 0.7f);
                    Color tint = GhostVisualBuilder.ReentryFireShellColor * alpha;

                    var mpb = state?.reentryMpb ?? new MaterialPropertyBlock();
                    mpb.SetColor("_TintColor", tint);

                    for (int m = 0; m < info.fireShellMeshes.Count; m++)
                    {
                        FireShellMesh fsm = info.fireShellMeshes[m];
                        if (fsm.mesh == null || fsm.transform == null) continue;
                        if (!fsm.transform.gameObject.activeInHierarchy) continue;

                        Matrix4x4 matrix = Matrix4x4.Translate(offset) * fsm.transform.localToWorldMatrix;
                        Graphics.DrawMesh(fsm.mesh, matrix, info.fireShellMaterial, 0, null, 0, mpb);
                    }
                }
            }
        }

        #endregion

        #region Observability helpers

        /// <summary>
        /// Aggregates the current primary/overlap ghost counts and engine/RCS FX counts.
        /// Purely observational — callers must not use this to drive gameplay decisions.
        /// </summary>
        internal GhostObservability CaptureGhostObservability()
        {
            var result = new GhostObservability();

            foreach (var kvp in ghostStates)
            {
                CountPrimaryGhostForObservability(kvp.Value, ref result);
            }

            foreach (var kvp in overlapGhosts)
            {
                var overlaps = kvp.Value;
                if (overlaps == null) continue;

                for (int i = 0; i < overlaps.Count; i++)
                    CountOverlapGhostForObservability(overlaps[i], ref result);
            }

            return result;
        }

        private static void CountPrimaryGhostForObservability(
            GhostPlaybackState state, ref GhostObservability result)
        {
            if (!HasLoadedGhostVisuals(state)) return;

            result.activePrimaryGhostCount++;

            if (state.currentZone == RenderingZone.Physics)
                result.zone1GhostCount++;
            else if (state.currentZone == RenderingZone.Visual)
                result.zone2GhostCount++;

            if (state.fidelityReduced)
                result.softCapReducedCount++;
            if (state.simplified)
                result.softCapSimplifiedCount++;

            CountFxForObservability(state, ref result);
        }

        private static void CountOverlapGhostForObservability(
            GhostPlaybackState state, ref GhostObservability result)
        {
            if (!HasLoadedGhostVisuals(state)) return;

            result.activeOverlapGhostCount++;
            CountFxForObservability(state, ref result);
        }

        private static void CountFxForObservability(
            GhostPlaybackState state, ref GhostObservability result)
        {
            int engineModulesForGhost = CountModulesAndParticleSystems(
                state?.engineInfos, out int engineSystemsForGhost);
            if (engineModulesForGhost > 0)
            {
                result.ghostsWithEngineFx++;
                result.engineModuleCount += engineModulesForGhost;
                result.engineParticleSystemCount += engineSystemsForGhost;
            }

            int rcsModulesForGhost = CountModulesAndParticleSystems(
                state?.rcsInfos, out int rcsSystemsForGhost);
            if (rcsModulesForGhost > 0)
            {
                result.ghostsWithRcsFx++;
                result.rcsModuleCount += rcsModulesForGhost;
                result.rcsParticleSystemCount += rcsSystemsForGhost;
            }
        }

        private static int CountModulesAndParticleSystems<TGhostInfo>(
            Dictionary<ulong, TGhostInfo> infos, out int particleSystemCount)
            where TGhostInfo : class
        {
            particleSystemCount = 0;
            if (infos == null || infos.Count == 0)
                return 0;

            int moduleCount = 0;
            foreach (var info in infos.Values)
            {
                if (info == null) continue;

                moduleCount++;
                if (info is EngineGhostInfo engineInfo)
                    particleSystemCount += engineInfo.particleSystems?.Count ?? 0;
                else if (info is RcsGhostInfo rcsInfo)
                    particleSystemCount += rcsInfo.particleSystems?.Count ?? 0;
            }

            return moduleCount;
        }

        #endregion

        #region Query API

        /// <summary>Number of primary timeline ghosts with loaded visuals.</summary>
        internal int GhostCount
        {
            get
            {
                int count = 0;
                foreach (var state in ghostStates.Values)
                {
                    if (HasLoadedGhostVisuals(state))
                        count++;
                }
                return count;
            }
        }

        /// <summary>Whether a ghost exists for the given recording index.</summary>
        internal bool HasGhost(int index) => ghostStates.ContainsKey(index);

        /// <summary>Whether a ghost exists with a non-null GameObject.</summary>
        internal bool HasActiveGhost(int index)
        {
            return ghostStates.TryGetValue(index, out var state) && HasLoadedGhostVisuals(state);
        }

        /// <summary>Get the ghost state for a recording index.</summary>
        internal bool TryGetGhostState(int index, out GhostPlaybackState state)
        {
            return ghostStates.TryGetValue(index, out state);
        }

        internal bool EnsureGhostVisualsLoadedForWatch(
            int index, IPlaybackTrajectory traj, double playbackUT,
            bool forceRebuildLoadedVisuals = false)
        {
            if (!ghostStates.TryGetValue(index, out var state) || state == null)
                return false;
            if (forceRebuildLoadedVisuals && HasLoadedGhostVisuals(state))
            {
                DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
                state.ClearLoadedVisualReferences();
            }
            if (!EnsureGhostVisualsLoaded(index, traj, state, playbackUT, "watch mode requested"))
                return false;

            SynchronizeLoadedGhostForWatch(index, traj, state, playbackUT);
            return true;
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
            // Debris with no snapshot would produce a distracting green sphere — skip entirely (#232)
            if (traj.IsDebris && GhostVisualBuilder.GetGhostSnapshot(traj) == null)
            {
                ParsekLog.Verbose("Engine",
                    $"Ghost #{index} \"{traj.VesselName}\": debris with no snapshot, skipping");
                return;
            }

            var state = new GhostPlaybackState
            {
                vesselName = traj?.VesselName ?? "Unknown",
                playbackIndex = 0,
                partEventIndex = 0,
                flagEventIndex = 0
            };

            if (!BuildGhostVisualsWithMetrics(index, traj, state, resetCompletedEventDedup: true, out string buildType))
                return;

            GhostPlaybackLogic.InitializeFlagVisibility(traj, state);
            ghostStates[index] = state;

            ParsekLog.VerboseRateLimited("Engine", $"spawn-{index}",
                $"Ghost #{index} \"{traj?.VesselName}\" spawned ({buildType}, " +
                $"parts={state.partTree?.Count ?? 0} engines={state.engineInfos?.Count ?? 0} " +
                $"rcs={state.rcsInfos?.Count ?? 0})", 1.0);
        }

        private bool BuildGhostVisualsWithMetrics(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            bool resetCompletedEventDedup, out string buildType)
        {
            bool built = false;
            buildType = null;
            spawnStopwatch.Start();
            frameSpawnCount++;
            if (resetCompletedEventDedup)
                completedEventFired.Remove(index);

            try
            {
                built = TryPopulateGhostVisuals(index, traj, state, out buildType);
                return built;
            }
            finally
            {
                spawnStopwatch.Stop();
                if (built)
                    DiagnosticsState.health.ghostBuildsThisSession++;
            }
        }

        private bool TryPopulateGhostVisuals(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            out string buildType)
        {
            buildType = null;
            if (state == null)
                return false;

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
                ghost = GhostVisualBuilder.CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);
            if (ghost == null)
                return false;

            var cameraPivotObj = new GameObject("cameraPivot");
            cameraPivotObj.transform.SetParent(ghost.transform, false);

            var horizonProxyObj = new GameObject("horizonProxy");
            horizonProxyObj.transform.SetParent(cameraPivotObj.transform, false);

            state.vesselName = traj?.VesselName ?? state.vesselName ?? "Unknown";
            state.ghost = ghost;
            state.cameraPivot = cameraPivotObj.transform;
            state.horizonProxy = horizonProxyObj.transform;
            state.partTree = GhostVisualBuilder.BuildPartSubtreeMap(GhostVisualBuilder.GetGhostSnapshot(traj));

            if (builtFromSnapshot)
            {
                state.materials = new List<Material>();
            }
            else
            {
                var m = ghost.GetComponent<Renderer>()?.material;
                state.materials = m != null ? new List<Material> { m } : new List<Material>();
            }

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, buildResult, traj);
            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(traj, state);

            state.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                ghost, state.heatInfos, index, traj.VesselName);
            state.reentryMpb = new MaterialPropertyBlock();

            buildType = builtFromSnapshot
                ? (traj.GhostVisualSnapshot != null ? "recording-start snapshot" : "vessel snapshot")
                : "sphere fallback";
            return true;
        }

        private bool HandleHiddenGhostVisualState(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, float warpRate, double ghostDistance,
            bool overlapGhost, string hiddenReason)
        {
            if (state == null)
                return false;

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, shouldReduceFidelity: false);

            if (ShouldPrewarmHiddenGhost(traj, state, ghostDistance, playbackUT))
            {
                if (!HasLoadedGhostVisuals(state)
                    && !EnsureGhostVisualsLoaded(index, traj, state, playbackUT,
                        $"hidden-tier prewarm ({hiddenReason})"))
                {
                    return false;
                }

                if (HasLoadedGhostVisuals(state))
                {
                    PositionLoadedGhostAtPlaybackUT(index, traj, state, playbackUT);
                    if (state.ghost.activeSelf)
                        state.ghost.SetActive(false);
                    ApplyFrameVisuals(index, traj, state, playbackUT, warpRate,
                        skipPartEvents: false, suppressVisualFx: true, allowTransientEffects: false);
                    ParsekLog.VerboseRateLimited("Engine", $"prewarm-hidden-{index}",
                        $"Ghost #{index} \"{state.vesselName}\" hidden-tier prewarm active " +
                        $"({hiddenReason})", 1.0);
                    return true;
                }
            }

            if (HasLoadedGhostVisuals(state))
            {
                if (overlapGhost)
                    UnloadOverlapGhostVisuals(index, state, hiddenReason);
                else
                    UnloadGhostVisuals(index, state, hiddenReason);
            }

            return false;
        }

        private bool EnsureGhostVisualsLoaded(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, string reason)
        {
            if (state == null)
                return false;
            if (HasLoadedGhostVisuals(state))
                return true;
            if (traj.IsDebris && GhostVisualBuilder.GetGhostSnapshot(traj) == null)
                return false;

            if (!BuildGhostVisualsWithMetrics(index, traj, state, resetCompletedEventDedup: false, out string buildType))
                return false;

            RehydrateGhostVisualState(index, traj, state, playbackUT);
            ParsekLog.VerboseRateLimited("Engine", $"rebuild-{index}",
                $"Ghost #{index} \"{state.vesselName}\" visuals rebuilt ({buildType}, {reason})", 1.0);
            return HasLoadedGhostVisuals(state);
        }

        private static void RehydrateGhostVisualState(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (state?.ghost == null)
                return;

            state.playbackIndex = 0;
            state.partEventIndex = 0;
            GhostPlaybackLogic.ApplyPartEvents(index, traj, playbackUT, state, allowTransientEffects: false);
        }

        private void SynchronizeLoadedGhostForWatch(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            state.playbackIndex = 0;
            state.partEventIndex = 0;
            PositionLoadedGhostAtPlaybackUT(index, traj, state, playbackUT);
            ApplyFrameVisuals(index, traj, state, playbackUT, TimeWarp.CurrentRate,
                skipPartEvents: false, suppressVisualFx: false, allowTransientEffects: false);

            if (state.ghost != null && !state.ghost.activeSelf)
                state.ghost.SetActive(true);
        }

        private void PositionLoadedGhostAtPlaybackUT(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!HasLoadedGhostVisuals(state) || traj == null || positioner == null)
                return;

            bool hasPoints = traj.Points != null && traj.Points.Count >= 2;
            bool hasSurfaceData = traj.SurfacePos.HasValue;
            bool hasOrbitData = traj.HasOrbitSegments;

            if (hasPoints)
            {
                bool usedRelative = false;
                if (traj.TrackSections != null && traj.TrackSections.Count > 0)
                {
                    int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
                    if (sectionIdx >= 0 && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
                    {
                        positioner.InterpolateAndPositionRelative(index, traj, state,
                            playbackUT, suppressFx: true, traj.TrackSections[sectionIdx].anchorVesselId);
                        usedRelative = true;
                    }
                }

                if (!usedRelative)
                    positioner.InterpolateAndPosition(index, traj, state, playbackUT, suppressFx: true);
                return;
            }

            if (traj.Points != null && traj.Points.Count > 0)
            {
                positioner.PositionAtPoint(index, traj, state, traj.Points[0]);
                return;
            }

            if (hasSurfaceData)
            {
                positioner.PositionAtSurface(index, traj, state);
                return;
            }

            if (hasOrbitData)
                positioner.PositionFromOrbit(index, traj, state, playbackUT);
        }

        private void UnloadGhostVisuals(int index, GhostPlaybackState state, string reason)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            ParsekLog.VerboseRateLimited("Engine", $"unload-{index}",
                $"Ghost #{index} \"{state.vesselName}\" visuals unloaded ({reason})", 1.0);
            DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
            state.ClearLoadedVisualReferences();
        }

        private void UnloadOverlapGhostVisuals(int index, GhostPlaybackState state, string reason)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            ParsekLog.VerboseRateLimited("Engine", $"overlap-unload-{index}-{state.loopCycleIndex}",
                $"Ghost #{index} cycle={state.loopCycleIndex} visuals unloaded ({reason})", 1.0);
            DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
            state.ClearLoadedVisualReferences();
        }

        /// <summary>
        /// Destroys materials, engine/RCS particle systems, reentry FX, ghost GameObject,
        /// and fake canopies for a single ghost playback state.
        /// Does NOT remove from any dictionary — caller handles collection bookkeeping.
        /// </summary>
        internal void DestroyGhostResources(GhostPlaybackState state, bool lingerParticleSystems = true)
        {
            if (state.materials != null)
            {
                for (int i = 0; i < state.materials.Count; i++)
                {
                    if (state.materials[i] != null)
                        UnityEngine.Object.Destroy(state.materials[i]);
                }
            }

            // Detach active engine/RCS particle systems so smoke trails linger (#107).
            // Stopped systems with no live particles are destroyed immediately.
            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                {
                    if (lingerParticleSystems)
                        GhostPlaybackLogic.DetachAndLingerParticleSystems(info.particleSystems, info.kspEmitters);
                    else
                        GhostPlaybackLogic.StopAndClearParticleSystems(info.particleSystems, info.kspEmitters);
                }
            }

            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                {
                    if (lingerParticleSystems)
                        GhostPlaybackLogic.DetachAndLingerParticleSystems(info.particleSystems, info.kspEmitters);
                    else
                        GhostPlaybackLogic.StopAndClearParticleSystems(info.particleSystems, info.kspEmitters);
                }
            }

            // Stop all ghost audio sources before destroying the GO hierarchy.
            int audioStopped = 0;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null && info.audioSource.isPlaying)
                    {
                        info.audioSource.Stop();
                        audioStopped++;
                    }
                }
            }
            if (state.oneShotAudio?.audioSource != null && state.oneShotAudio.audioSource.isPlaying)
            {
                state.oneShotAudio.audioSource.Stop();
                audioStopped++;
            }
            if (audioStopped > 0)
                ParsekLog.Verbose("GhostAudio", $"Cleanup: stopped {audioStopped} audio source(s) for '{state.vesselName}'");

            DestroyReentryFxResources(state.reentryFxInfo);

            if (state.ghost != null)
                UnityEngine.Object.Destroy(state.ghost);

            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
        }

        private void DestroyGhostResourcesWithMetrics(GhostPlaybackState state, bool lingerParticleSystems = true)
        {
            if (state == null)
                return;

            if (!updateStopwatch.IsRunning)
            {
                DestroyGhostResources(state, lingerParticleSystems);
                return;
            }

            destroyStopwatch.Start();
            try
            {
                DestroyGhostResources(state, lingerParticleSystems);
            }
            finally
            {
                destroyStopwatch.Stop();
            }
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
        internal void DestroyGhost(int index, IPlaybackTrajectory traj = null,
            TrajectoryPlaybackFlags flags = default, string reason = null)
        {
            frameDestroyCount++;

            GhostPlaybackState state;
            if (!ghostStates.TryGetValue(index, out state))
                return;

            string name = state?.vesselName ?? traj?.VesselName ?? "Unknown";
            ParsekLog.VerboseRateLimited("Engine", $"destroy-{index}",
                $"Ghost #{index} \"{name}\" destroyed ({reason ?? "unknown"})", 1.0);

            // Fire before destroy so subscribers can read state
            OnGhostDestroyed?.Invoke(new GhostLifecycleEvent
            {
                Index = index, Trajectory = traj, State = state, Flags = flags
            });

            DestroyGhostResourcesWithMetrics(state);

            ghostStates.Remove(index);
            loopPhaseOffsets.Remove(index);
            completedEventFired.Remove(index);
            DiagnosticsState.health.ghostDestroysThisSession++;
        }

        /// <summary>
        /// Destroys a single overlap ghost's resources. Does NOT remove from any collection.
        /// </summary>
        internal void DestroyOverlapGhostState(GhostPlaybackState state)
        {
            if (state == null) return;
            ParsekLog.VerboseRateLimited("Engine", "destroy-overlap",
                $"Destroying overlap ghost cycle={state.loopCycleIndex}", 2.0);
            DestroyGhostResourcesWithMetrics(state);
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
        /// Pauses audio on all active ghost states (primary + overlap). Called when the KSP
        /// pause menu opens (ESC). Preserves playback position via AudioSource.Pause() so
        /// UnpauseAllGhostAudio can resume exactly where it left off.
        /// </summary>
        internal void PauseAllGhostAudio()
        {
            int pausedPrimary = 0, pausedOverlap = 0;
            foreach (var kvp in ghostStates)
            {
                GhostPlaybackLogic.PauseAllAudio(kvp.Value);
                pausedPrimary++;
            }
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    GhostPlaybackLogic.PauseAllAudio(kvp.Value[i]);
                    pausedOverlap++;
                }
            }
            ParsekLog.Verbose("GhostAudio",
                $"PauseAllGhostAudio: paused {pausedPrimary} primary + {pausedOverlap} overlap ghost(s)");
        }

        /// <summary>
        /// Resumes audio on all active ghost states. Called when the KSP pause menu closes.
        /// </summary>
        internal void UnpauseAllGhostAudio()
        {
            int resumedPrimary = 0, resumedOverlap = 0;
            foreach (var kvp in ghostStates)
            {
                GhostPlaybackLogic.UnpauseAllAudio(kvp.Value);
                resumedPrimary++;
            }
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    GhostPlaybackLogic.UnpauseAllAudio(kvp.Value[i]);
                    resumedOverlap++;
                }
            }
            ParsekLog.Verbose("GhostAudio",
                $"UnpauseAllGhostAudio: resumed {resumedPrimary} primary + {resumedOverlap} overlap ghost(s)");
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

            // Bug #131: cap active explosion count to prevent frame drops with overlapping reentry loops
            if (activeExplosions.Count >= MaxActiveExplosions)
            {
                GhostPlaybackLogic.HideAllGhostParts(state);
                ParsekLog.VerboseRateLimited("ExplosionFx", "explosion-capped",
                    $"Explosion skipped for ghost #{recIdx} \"{traj.VesselName}\": " +
                    $"activeExplosions={activeExplosions.Count} >= cap={MaxActiveExplosions}");
                return;
            }

            Vector3 worldPos = state.ghost.transform.position;
            float vesselLength = state.reentryFxInfo != null
                ? state.reentryFxInfo.vesselLength
                : GhostVisualBuilder.ComputeGhostLength(state.ghost);

            ParsekLog.VerboseRateLimited("ExplosionFx", $"trigger-{recIdx}",
                $"Triggering explosion for ghost #{recIdx} \"{traj.VesselName}\" " +
                $"at ({worldPos.x:F1},{worldPos.y:F1},{worldPos.z:F1}) vesselLength={vesselLength:F1}m", 10.0);

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

                ParsekLog.VerboseRateLimited("ExplosionFx", "explosion-created",
                    $"Explosion GO created for ghost #{recIdx}, activeExplosions.Count={activeExplosions.Count}");
            }

            GhostPlaybackLogic.HideAllGhostParts(state);
            ParsekLog.VerboseRateLimited("Engine", "parts-hidden-explosion",
                $"Ghost #{recIdx} parts hidden after explosion");
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
            loggedGhostEnter.Clear();
            loggedReshow.Clear();
            completedEventFired.Clear();
            earlyDestroyedDebrisCompleted.Clear();

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
            ReindexSet(completedEventFired, removedIndex);
            ReindexSet(earlyDestroyedDebrisCompleted, removedIndex);
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
