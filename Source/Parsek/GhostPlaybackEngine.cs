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

        // Deferred event lists (reused per frame to avoid GC allocation)
        private readonly List<PlaybackCompletedEvent> deferredCompletedEvents = new List<PlaybackCompletedEvent>();
        private readonly List<GhostLifecycleEvent> deferredCreatedEvents = new List<GhostLifecycleEvent>();

        // Dedup: prevent completed events from firing every frame for past-end recordings
        private readonly HashSet<int> completedEventFired = new HashSet<int>();

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
            if (trajectories == null || trajectories.Count == 0) return;

            bool suppressGhosts = GhostPlaybackLogic.ShouldSuppressGhosts(ctx.warpRate);
            bool suppressVisualFx = GhostPlaybackLogic.ShouldSuppressVisualFx(ctx.warpRate);

            // Reset reshow dedup when entering warp suppression
            if (suppressGhosts)
                loggedReshow.Clear();

            deferredCompletedEvents.Clear();
            deferredCreatedEvents.Clear();

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
                        DestroyGhost(i);
                        ParsekLog.Info("Engine", $"Ghost #{i} destroyed — recording skipped (disabled/suppressed)");
                    }
                    continue;
                }

                bool hasPoints = traj.Points != null && traj.Points.Count >= 2;
                bool hasOrbitData = traj.OrbitSegments != null && traj.OrbitSegments.Count > 0;
                bool hasSurfaceData = traj.SurfacePos.HasValue;
                if (!hasPoints && !hasOrbitData && !hasSurfaceData) continue;

                bool inRange = ctx.currentUT >= traj.StartUT && ctx.currentUT <= traj.EndUT;
                bool pastEnd = ctx.currentUT > traj.EndUT;
                bool pastEffectiveEnd = ctx.currentUT > f.chainEndUT;

                GhostPlaybackState state;
                ghostStates.TryGetValue(i, out state);
                bool ghostActive = state != null && state.ghost != null;

                // === Loop dispatch (before main rendering) ===
                if (ShouldLoopPlayback(traj))
                {
                    // Anchor gating: if anchor configured but not loaded, skip ghost
                    if (traj.LoopAnchorVesselId != 0 && !loadedAnchorVessels.Contains(traj.LoopAnchorVesselId))
                    {
                        if (ghostActive)
                        {
                            ParsekLog.Info("Engine", $"Loop ghost #{i} \"{traj.VesselName}\" — anchor vessel {traj.LoopAnchorVesselId} unloaded, destroying ghost");
                            DestroyGhost(i);
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

                // === Warp suppression: hide ghost during high warp ===
                if (suppressGhosts && ghostActive)
                {
                    if (state.ghost.activeSelf)
                        state.ghost.SetActive(false);
                    DestroyAllOverlapGhosts(i);
                    continue;
                }

                // === In-range rendering ===
                if (inRange && !ghostActive && !softCapSuppressed.Contains(i))
                {
                    SpawnGhost(i, traj);
                    ghostStates.TryGetValue(i, out state);
                    ghostActive = state != null && state.ghost != null;
                    if (ghostActive)
                    {
                        if (loggedGhostEnter.Add(i))
                            ParsekLog.Info("Engine", $"Ghost ENTERED range: #{i} \"{traj.VesselName}\" ({f.segmentLabel})");

                        deferredCreatedEvents.Add(new GhostLifecycleEvent
                        {
                            Index = i, Trajectory = traj, State = state, Flags = f
                        });
                    }
                }

                if (inRange && ghostActive)
                {
                    // Re-show ghost after warp-down
                    if (!state.ghost.activeSelf && state.currentZone != RenderingZone.Beyond)
                    {
                        state.ghost.SetActive(true);
                        if (loggedReshow.Add(i))
                            ParsekLog.Info("Engine", $"Ghost re-shown after warp-down: #{i} \"{traj.VesselName}\"");
                    }

                    // Zone-based rendering
                    double ghostDist = Vector3d.Distance(ctx.activeVesselPos,
                        (Vector3d)state.ghost.transform.position);
                    var zoneResult = positioner.ApplyZoneRendering(i, state, traj, ghostDist, ctx.protectedIndex);
                    if (zoneResult.hiddenByZone) continue;

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
                    if (!zoneResult.skipPartEvents)
                    {
                        GhostPlaybackLogic.ApplyPartEvents(i, traj, ctx.currentUT, state);
                        GhostPlaybackLogic.ApplyFlagEvents(state, traj, ctx.currentUT);
                    }

                    // Reentry FX
                    UpdateReentryFx(i, state, traj.VesselName, ctx.warpRate);

                    // RCS emission suppression during warp
                    if (suppressVisualFx)
                        GhostPlaybackLogic.StopAllRcsEmissions(state);
                    else
                        GhostPlaybackLogic.RestoreAllRcsEmissions(state);

                    continue;
                }

                // === Past end: fire completed event, optionally destroy ===
                if ((pastEnd || pastEffectiveEnd) && ghostActive && !completedEventFired.Contains(i))
                {
                    completedEventFired.Add(i);
                    // Position ghost at final point
                    if (hasPoints)
                    {
                        var lastPoint = traj.Points[traj.Points.Count - 1];
                        positioner.PositionAtPoint(i, traj, state, lastPoint);
                    }

                    // Trigger explosion if destroyed
                    TriggerExplosionIfDestroyed(state, traj, i, ctx.warpRate);

                    // Fire completed event (policy handles spawn/resources/camera)
                    deferredCompletedEvents.Add(new PlaybackCompletedEvent
                    {
                        Index = i,
                        Trajectory = traj,
                        State = state,
                        Flags = f,
                        GhostWasActive = true,
                        PastEffectiveEnd = pastEffectiveEnd,
                        LastPoint = hasPoints ? traj.Points[traj.Points.Count - 1] : default,
                        CurrentUT = ctx.currentUT
                    });

                    // Mid-chain: hold ghost at final position; otherwise destroy
                    if (!f.isMidChain)
                    {
                        DestroyGhost(i);
                    }

                    continue;
                }

                // Past end, ghost not active: fire event for policy (deferred spawn, resources)
                if ((pastEnd || pastEffectiveEnd) && !completedEventFired.Contains(i))
                {
                    completedEventFired.Add(i);
                    deferredCompletedEvents.Add(new PlaybackCompletedEvent
                    {
                        Index = i,
                        Trajectory = traj,
                        State = state,
                        Flags = f,
                        GhostWasActive = false,
                        PastEffectiveEnd = pastEffectiveEnd,
                        LastPoint = hasPoints ? traj.Points[traj.Points.Count - 1] : default,
                        CurrentUT = ctx.currentUT
                    });
                }
            }

            // Post-loop: soft caps
            EvaluateSoftCaps(trajectories, ctx);

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
        }

        /// <summary>
        /// Placeholder for soft cap evaluation. Will be moved from ParsekFlight.
        /// </summary>
        private void EvaluateSoftCaps(IReadOnlyList<IPlaybackTrajectory> trajectories, FrameContext ctx)
        {
            // TODO Phase 5: move EvaluateGhostSoftCaps here
        }

        /// <summary>
        /// Handles looping ghost playback for a single trajectory.
        /// Manages cycle changes, ghost spawn/destroy, pause windows.
        /// Fires CameraActionEvents for watch mode interactions.
        /// Placeholder — delegates to host's existing method until fully extracted.
        /// </summary>
        private void UpdateLoopingPlayback(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            bool suppressGhosts, bool suppressVisualFx)
        {
            // TODO Phase 5.4: Move UpdateLoopingTimelinePlayback + UpdateOverlapLoopPlayback
            // rendering logic here, extract camera code to CameraActionEvents.
            // For now, the host's forwarding methods handle this via the existing code path.
        }

        #endregion

        #region Loop utilities

        /// <summary>Whether the trajectory should loop (has enough points and duration).</summary>
        internal static bool ShouldLoopPlayback(IPlaybackTrajectory traj)
        {
            if (traj == null || !traj.LoopPlayback || traj.Points == null || traj.Points.Count < 2)
                return false;
            return traj.EndUT - traj.StartUT > GhostPlaybackLogic.MinLoopDurationSeconds;
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
            out int cycleIndex,
            out bool inPauseWindow,
            int recIdx = -1)
        {
            loopUT = traj != null ? traj.StartUT : 0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (traj == null || traj.Points == null || traj.Points.Count < 2) return false;
            if (currentUT < traj.StartUT) return false;

            double duration = traj.EndUT - traj.StartUT;
            if (duration <= GhostPlaybackLogic.MinLoopDurationSeconds) return false;

            double intervalSeconds = GetLoopIntervalSeconds(traj, autoLoopIntervalSeconds);
            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration <= GhostPlaybackLogic.MinLoopDurationSeconds)
                cycleDuration = duration;

            double elapsed = currentUT - traj.StartUT;

            // Apply loop phase offset (set by Watch mode to reset ghost to recording start)
            double phaseOffset;
            if (recIdx >= 0 && loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
            {
                ParsekLog.Verbose("Engine", $"TryComputeLoopPlaybackUT: applying phase offset {phaseOffset:F2}s for recIdx={recIdx}");
                elapsed += phaseOffset;
            }

            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0) cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (intervalSeconds > 0 && cycleTime > duration)
            {
                inPauseWindow = true;
                loopUT = traj.EndUT;
                if (ParsekLog.IsVerboseEnabled)
                    ParsekLog.VerboseRateLimited("Engine", "loop_pause_" + recIdx,
                        $"TryComputeLoopPlaybackUT: in pause window for recIdx={recIdx}, cycle={cycleIndex}");
                return true;
            }

            loopUT = traj.StartUT + Math.Min(cycleTime, duration);
            return true;
        }

        /// <summary>Whether any time warp is active.</summary>
        internal static bool IsAnyWarpActive()
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
            completedEventFired.Clear();

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
