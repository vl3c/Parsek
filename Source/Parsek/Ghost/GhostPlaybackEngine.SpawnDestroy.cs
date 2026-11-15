using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Ghost spawn and destroy lifecycle: cycle reuse, pending-spawn state, lifecycle event queueing, spawn slots, destroy / explosion, audio pause and index shifts.
    internal partial class GhostPlaybackEngine
    {
        /// <summary>
        /// #406 follow-up: reuse the existing ghost GameObject across a
        /// loop-cycle boundary instead of destroy+spawn. The ghost hierarchy
        /// (parts, particle systems, audio sources, cloned materials) and
        /// reentry FX resources (mesh, particle system, glow materials) survive;
        /// only playback iterators, per-cycle flags, and transient per-cycle
        /// state are reset. Visibility of parts hidden by prior-cycle events
        /// (decouple/destroy/inventory placed) is restored via
        /// <see cref="GhostPlaybackLogic.ReactivateGhostPartHierarchyForLoopRewind"/>
        /// and the initial-spawn visibility baseline re-applied. Does NOT
        /// consume a #414 spawn slot, does NOT increment
        /// <c>frameSpawnCount</c>, does NOT fire <c>OnGhostCreated</c> (the
        /// ghost was never destroyed). Fires
        /// <c>OnLoopCameraAction(RetargetToNewGhost)</c> at the end so the
        /// host (WatchModeController / ParsekFlight) can re-snap the camera
        /// to the ghost's post-wrap position — the <c>cameraPivot</c>
        /// Transform identity is preserved across the reuse so any hard
        /// references held by the camera code remain valid.
        ///
        /// Set <paramref name="emitRetargetEvent"/> to <c>false</c> when the
        /// caller has already emitted a primary <c>OnLoopCameraAction</c> for
        /// this cycle boundary (e.g. <c>ExplosionHoldStart</c>) and a follow-up
        /// <c>RetargetToNewGhost</c> would either be ignored by the watch
        /// handler (already in explosion hold) or override an explicit
        /// camera-state decision. The destroyed-loop-cycle path in
        /// <c>UpdateLoopingPlayback</c> uses this to keep the camera held at
        /// the explosion site without firing a redundant retarget event
        /// immediately after.
        /// </summary>
        internal void ReusePrimaryGhostAcrossCycle(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            GhostPlaybackState state, double playbackUT, long newCycleIndex,
            bool emitRetargetEvent = true)
        {
            if (state == null || state.ghost == null)
            {
                // Advance loopCycleIndex so HasLoopCycleChanged returns false next
                // frame — otherwise a state with a null/Unity-destroyed ghost
                // re-enters this path every frame and spams the log. The ghost
                // either never built (no snapshot) or was externally destroyed;
                // the spawn path below is responsible for re-creating it if and
                // when a snapshot becomes available. Rate-limited so a genuine
                // regression still leaves a breadcrumb without 85/sec spam.
                if (state != null)
                    state.loopCycleIndex = newCycleIndex;
                ParsekLog.VerboseRateLimited("Engine", $"reuse-skip-null-{index}",
                    $"ReusePrimaryGhostAcrossCycle: #{index} skipped — state.ghost is null (advanced cycle={newCycleIndex})", 1.0);
                return;
            }

            long previousCycle = state.loopCycleIndex;
            EmitLoopCycleTrace(index, traj, state, previousCycle, newCycleIndex, "reuse");

            // Step 0: parity with SpawnGhost + DestroyGhost dedupe hygiene —
            // the prior-cycle end may have fired a trajectory completion event
            // (added to completedEventFired) and emitted a missing-audio-clip
            // warning (tracked in GhostVisualBuilder.ClearMissingAudioClipWarnings).
            // Today's destroy+spawn path clears both. Mirroring here so reuse
            // does not suppress the new cycle's legitimate completion event
            // nor over-dedupe the audio warning across cycles.
            completedEventFired.Remove(index);
            GhostVisualBuilder.ClearMissingAudioClipWarnings(state.ghost.name);

            // Step 1: pure-static per-cycle state reset. See
            // GhostPlaybackLogic.ResetForLoopCycle for the field-by-field
            // preservation table in
            // docs/dev/plan-406-ghost-reuse-loop-cycles.md.
            GhostPlaybackLogic.ResetForLoopCycle(state, newCycleIndex);

            // Step 1b: Unity-touching cleanup that cannot live inside the
            // pure-logic ResetForLoopCycle (xUnit JIT-verify would trip
            // SecurityException on Unity type references). These were
            // originally called from inside ResetForLoopCycle and moved out
            // after a review finding — ordering is preserved: restore RCS
            // emitters before downstream code re-inspects `rcsSuppressed`,
            // destroy fake canopy GameObjects so stale decouple stubs from
            // the prior cycle do not linger in the scene.
            GhostPlaybackLogic.RestoreAllRcsEmissions(state);
            GhostPlaybackLogic.DestroyAllFakeCanopies(state);

            // Step 2: re-activate any part GameObjects hidden by prior-cycle
            // events (decouple, destroy, inventory placed, pause-window
            // HideAllGhostParts). Without this, a Learstar-class ghost that
            // jettisoned stages on the previous cycle would start the new
            // cycle with those stages invisible.
            int reactivated = GhostPlaybackLogic.ReactivateGhostPartHierarchyForLoopRewind(state);

            // Step 3: restore the logical-part-id set to the full snapshot
            // baseline — prior-cycle decouple/destroy events pruned pids out
            // of this set. The same helper is used by the spawn path, so
            // semantics match identically.
            ConfigNode snapshot = GhostVisualBuilder.GetGhostSnapshot(traj);
            if (snapshot != null)
                state.logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot);

            // Step 4: re-apply the spawn-time visibility baseline for
            // inventory placements and compound parts. Inventory parts
            // whose first event is InventoryPartPlaced start hidden;
            // compound parts whose target is missing start hidden. Both
            // helpers are idempotent and safe to re-run.
            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(traj, state);
            GhostPlaybackLogic.RefreshCompoundPartVisibility(state);

            // Step 5: rebuild the reentry emission mesh to include the
            // now-active parts. RebuildReentryMeshes uses GetComponentsInChildren
            // (active only), so without this rebuild the mesh would stay
            // stale (only the post-decouple subset from the prior cycle)
            // until the new cycle fires its own decouple event. For the
            // Learstar A1 class (decouples precede reentry), the
            // user-visible impact is nil — but on recordings where the
            // new cycle re-enters atmosphere BEFORE the first decouple,
            // the stale mesh would make reentry FX emit from too few
            // verts. Skipped when reentryFxInfo is null (showcase/EVA or
            // #450 B3 still pending build) — RebuildReentryMeshes itself
            // early-returns on null anyway, the null check here just
            // avoids the MeshFilter scan.
            if (state.reentryFxInfo != null)
                GhostVisualBuilder.RebuildReentryMeshes(state.ghost, state.reentryFxInfo);

            // Step 5b: re-apply spawn-time module baselines that ResetForLoopCycle
            // cannot touch because they are Unity-touching: heat parts back to
            // cold, deployables re-stowed, jettison panels re-attached, and —
            // for zero-engine-event recordings (pure debris boosters) — the
            // orphan engine/audio auto-start. Fresh-spawn behaviour in
            // TryPopulateGhostVisuals does each of these; without this step the
            // second cycle onward would inherit the prior cycle's end-state
            // (hot + deployed + jettisoned + silent) instead of replaying from
            // the same baseline the first cycle did. See the helper's XML doc
            // for the per-branch rationale.
            GhostPlaybackLogic.ReapplySpawnTimeModuleBaselinesForLoopCycle(state, traj);

            // Step 6: re-prime the ghost at the new cycle's playbackUT.
            // PrimeLoadedGhostForPlaybackUT resets playbackIndex+partEventIndex
            // (already 0 post-reset, but it's cheap and idempotent),
            // positions the ghost, applies ApplyFrameVisuals with
            // allowTransientEffects: false so decouple puffs do not re-fire,
            // hides the ghost GO, and resets appearance tracking. Same call
            // the spawn path uses, so reuse and spawn converge on identical
            // post-condition state.
            bool primeRetired = PrimeLoadedGhostForPlaybackUT(index, traj, state, playbackUT);

            DiagnosticsState.health.ghostReusedAcrossCycleThisSession++;

            // Distinctive UTF-16 string for the DLL verification recipe.
            ParsekLog.VerboseRateLimited("Engine", $"reuse-{index}",
                $"Ghost #{index} \"{state.vesselName}\" ghost reused across loop cycle: " +
                $"from cycle={previousCycle} to cycle={newCycleIndex} " +
                $"(reactivated={reactivated} parts, " +
                $"reentryFxPendingBuild={state.reentryFxPendingBuild}, " +
                $"reentryFxInfo={(state.reentryFxInfo != null ? "present" : "null")})", 1.0);

            // Camera handshake: same event the spawn path fires so the host
            // can re-snap to the ghost's post-wrap pivot position. The pivot
            // Transform identity is preserved, but the underlying GameObject
            // has moved (PrimeLoadedGhostForPlaybackUT -> positioner.PositionLoop
            // placed it at loopUT on the new cycle), so the retarget is
            // essential for watch-mode follow.
            //
            // Suppressed when the caller already emitted a primary camera event
            // for this cycle boundary (destroyed-loop ExplosionHoldStart). The
            // watch handler ignores RetargetToNewGhost while in explosion hold
            // (watchedOverlapCycleIndex == -2), so firing it adds API noise
            // without changing camera behaviour and breaks the
            // "exactly one OnLoopCameraAction per cycle boundary" contract that
            // RuntimeTests.ExplosionAnchorPosition_BelowTerrain_ClampsBeforeWatchHold
            // exercises. The non-destroyed branch (ExplosionHoldEnd) DOES need
            // the retarget event so the watch handler swaps the bridge anchor
            // for the new cycle's pivot, so the suppression only applies when
            // the boundary fired a real explosion.
            if (emitRetargetEvent && !primeRetired)
            {
                OnLoopCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = index,
                    Action = CameraActionType.RetargetToNewGhost,
                    NewCycleIndex = newCycleIndex,
                    GhostPivot = state.cameraPivot,
                    Trajectory = traj,
                    Flags = flags
                });
            }
            else
            {
                ParsekLog.VerboseRateLimited("Engine", $"reuse-suppress-retarget-{index}",
                    $"Ghost #{index} \"{state.vesselName}\" loop-cycle reuse: " +
                    (primeRetired
                        ? $"RetargetToNewGhost suppressed (anchor-retired prime; cycle={newCycleIndex})"
                        : $"RetargetToNewGhost suppressed (caller emitted primary boundary event; cycle={newCycleIndex})"),
                    1.0);
            }
        }

        /// <summary>
        /// Spawns a timeline ghost for the given trajectory at the specified index.
        /// Builds the ghost mesh from the snapshot, or falls back to a sphere.
        /// Populates all ghost info dictionaries and reentry FX. Called for the
        /// first appearance of a ghost in a session; subsequent loop cycles go
        /// through <see cref="ReusePrimaryGhostAcrossCycle"/> instead.
        /// </summary>
        internal void SpawnGhost(int index, IPlaybackTrajectory traj, double playbackUT)
        {
            // Debris with no snapshot would produce a distracting green sphere — skip entirely (#232)
            if (traj.IsDebris && GhostVisualBuilder.GetGhostSnapshot(traj) == null)
            {
                ParsekLog.Verbose("Engine",
                    $"Ghost #{index} \"{traj.VesselName}\": debris with no snapshot, skipping");
                return;
            }

            var state = CreatePendingSpawnState(
                traj, playbackUT, PendingSpawnLifecycle.StandardEnter, default(TrajectoryPlaybackFlags));
            ghostStates[index] = state;

            GhostVisualLoadStatus status = EnsureGhostVisualsLoaded(
                index, traj, state, playbackUT, "direct spawn",
                forceImmediateBuild: true, resetCompletedEventDedup: true);
            if (status == GhostVisualLoadStatus.Failed)
                ghostStates.Remove(index);
            else
                QueueOrEmitGhostSpawnPending(index, traj, state, default(TrajectoryPlaybackFlags));
        }

        /// <summary>
        /// Pure-static gate for <c>GhostPlaybackState.spawnedAtChainSeam</c>: the chain-seam
        /// carve-out applies ONLY to <see cref="PendingSpawnLifecycle.StandardEnter"/> spawns.
        /// Loop reentries and overlap-primary spawns originate from the same recording as the
        /// just-completed cycle, not from a chain predecessor, so they must not skip the
        /// activation-settle hold — their fresh first-appearance race is real.
        /// </summary>
        internal static bool ShouldMarkSpawnedAtChainSeam(
            bool isChainSeamSuccessor, PendingSpawnLifecycle lifecycle)
        {
            return isChainSeamSuccessor && lifecycle == PendingSpawnLifecycle.StandardEnter;
        }

        private GhostPlaybackState CreatePendingSpawnState(
            IPlaybackTrajectory traj, double playbackUT,
            PendingSpawnLifecycle lifecycle, TrajectoryPlaybackFlags flags)
        {
            var state = new GhostPlaybackState
            {
                vesselName = traj?.VesselName ?? "Unknown",
                recordingId = traj?.RecordingId ?? string.Empty,
                playbackIndex = 0,
                partEventIndex = 0,
                flagEventIndex = 0,
                pendingSpawnLifecycle = lifecycle,
                pendingSpawnFlags = flags,
                spawnedAtChainSeam = ShouldMarkSpawnedAtChainSeam(flags.isChainSeamSuccessor, lifecycle),
                audioPaused = ghostAudioPaused
            };

            if (state.spawnedAtChainSeam)
            {
                string shortRecId = string.IsNullOrEmpty(state.recordingId)
                    ? "(no-id)"
                    : (state.recordingId.Length > 8 ? state.recordingId.Substring(0, 8) : state.recordingId);
                ParsekLog.Verbose("Engine", FormattableString.Invariant(
                    $"Chain-seam spawn: vessel='{state.vesselName}' rec={shortRecId} UT={playbackUT:F2}: force-immediate-build + skip activation-settle (predecessor pose is continuous)"));
            }

            string pendingSpawnRecId = state.recordingId ?? string.Empty;
            if (TryResolvePendingPlaybackInterpolation(traj, playbackUT, out InterpolationResult initialPlayback))
            {
                state.SetInterpolated(initialPlayback);
                string seededBodyName = initialPlayback.bodyName ?? "(null)";
                ParsekLog.VerboseRateLimited("Engine", "pending-spawn-seed-" + pendingSpawnRecId,
                    FormattableString.Invariant(
                        $"Pending spawn interpolation seed: vessel='{state.vesselName}' lifecycle={lifecycle} UT={playbackUT:F1} body='{seededBodyName}' altitude={initialPlayback.altitude:F1}"),
                    2.0);
            }
            else
            {
                ParsekLog.VerboseRateLimited("Engine", "pending-spawn-seed-unavail-" + pendingSpawnRecId,
                    FormattableString.Invariant(
                        $"Pending spawn interpolation seed unavailable: vessel='{state.vesselName}' lifecycle={lifecycle} UT={playbackUT:F1}"),
                    2.0);
            }

            return state;
        }

        /// <summary>
        /// Early spawn signal: queue/emit <see cref="OnGhostSpawnPending"/> the moment a PRIMARY
        /// pending-spawn state registers (call sites sit AFTER the Failed-status check so a
        /// failed-and-removed registration never notifies, matching the old no-GhostCreated
        /// outcome). Same mid-update deferral as <see cref="QueueOrEmitGhostCreated"/>; no mesh
        /// trace here (the mesh does not exist yet - MeshSpawned stays with the created funnel).
        /// </summary>
        private void QueueOrEmitGhostSpawnPending(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPlaybackFlags flags)
        {
            if (OnGhostSpawnPending == null)
                return;

            ParsekLog.VerboseRateLimited("Engine", $"spawn-pending-{index}",
                $"Ghost #{index} \"{state?.vesselName}\" spawn-pending notify " +
                "(state registered; mesh build may still be time-sliced)", 2.0);

            var evt = new GhostLifecycleEvent
            {
                Index = index,
                Trajectory = traj,
                State = state,
                Flags = flags
            };

            if (updateStopwatch.IsRunning)
                deferredSpawnPendingEvents.Add(evt);
            else
                OnGhostSpawnPending(evt);
        }

        private void QueueOrEmitGhostCreated(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPlaybackFlags flags)
        {
            // Mesh APPEAR EVENT - emitted at the create funnel BEFORE the no-subscriber early-return so it
            // fires regardless of whether a policy is attached (deferred vs immediate Invoke is only about
            // WHEN the policy is notified, not when the ghost entered the scene).
            if (EmitMeshLifecycleTrace("MeshSpawned", index, traj, state, "ghost-created") && state != null)
                state.meshSpawnTraced = true;

            if (OnGhostCreated == null)
                return;

            var evt = new GhostLifecycleEvent
            {
                Index = index,
                Trajectory = traj,
                State = state,
                Flags = flags
            };

            if (updateStopwatch.IsRunning)
                deferredCreatedEvents.Add(evt);
            else
                OnGhostCreated(evt);
        }

        private void FinalizePendingSpawnLifecycle(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, HeaviestSpawnBuildType buildType)
        {
            if (state == null)
                return;

            PendingSpawnLifecycle lifecycle = state.pendingSpawnLifecycle;
            if (lifecycle == PendingSpawnLifecycle.None)
                return;

            TrajectoryPlaybackFlags flags = state.pendingSpawnFlags;
            state.pendingSpawnLifecycle = PendingSpawnLifecycle.None;
            state.pendingSpawnFlags = default(TrajectoryPlaybackFlags);

            GhostPlaybackLogic.InitializeFlagVisibility(traj, state);
            loggedGhostEnter.Add(index);

            ParsekLog.VerboseRateLimited("Engine", $"spawn-{index}",
                $"Ghost #{index} \"{traj?.VesselName}\" spawned ({buildType.ToLogToken()}, " +
                $"parts={state.partTree?.Count ?? 0} engines={state.engineInfos?.Count ?? 0} " +
                $"rcs={state.rcsInfos?.Count ?? 0})", 1.0);

            QueueOrEmitGhostCreated(index, traj, state, flags);

            bool retired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame);
            string vesselName = traj?.VesselName ?? state.vesselName ?? "(unknown)";

            switch (lifecycle)
            {
                case PendingSpawnLifecycle.LoopEnter:
                    ParsekLog.VerboseRateLimited("Engine", $"enter-{index}",
                        $"Ghost ENTERED range: #{index} \"{vesselName}\" at UT {playbackUT:F1} " +
                        $"(loop cycle={state.loopCycleIndex})");
                    if (retired)
                    {
                        ParsekLog.VerboseRateLimited("Engine", $"finalize-retire-loop-{index}",
                            $"finalize-spawn retire: suppressing RetargetToNewGhost (anchor retired on first spawn) ghost #{index} \"{vesselName}\" lifecycle=LoopEnter cycle={state.loopCycleIndex}",
                            3.0);
                        break;
                    }
                    OnLoopCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index,
                        Action = CameraActionType.RetargetToNewGhost,
                        NewCycleIndex = state.loopCycleIndex,
                        GhostPivot = state.cameraPivot,
                        Trajectory = traj,
                        Flags = flags
                    });
                    break;

                case PendingSpawnLifecycle.OverlapPrimaryEnter:
                    ParsekLog.VerboseRateLimited("Engine", $"enter-{index}",
                        $"Ghost ENTERED range: #{index} \"{vesselName}\" cycle={state.loopCycleIndex} " +
                        $"at UT {playbackUT:F1} (overlap)");
                    if (retired)
                    {
                        ParsekLog.VerboseRateLimited("Engine", $"finalize-retire-overlap-{index}",
                            $"finalize-spawn retire: suppressing RetargetToNewGhost (anchor retired on first spawn) ghost #{index} \"{vesselName}\" lifecycle=OverlapPrimaryEnter cycle={state.loopCycleIndex}",
                            3.0);
                        break;
                    }
                    OnOverlapCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index,
                        Action = CameraActionType.RetargetToNewGhost,
                        NewCycleIndex = state.loopCycleIndex,
                        GhostPivot = state.cameraPivot,
                        Trajectory = traj,
                        Flags = flags
                    });
                    break;
            }
        }

        /// <summary>
        /// Bug #414: per-frame ghost spawn throttle. Returns true if the caller is allowed
        /// to spawn this frame; false if the cap is exhausted and the spawn must be deferred
        /// to a later frame. Increments the deferred counter and emits a rate-limited Verbose
        /// log on the deferred path. Watch-mode and loop-cycle-rebuild spawns do NOT call this
        /// — see plan-414-spawn-throttle.md for the call-site taxonomy.
        /// </summary>
        private bool TryReserveSpawnSlot(int index, string site)
        {
            spawnReserveAttemptCountForTesting++;
            if (GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame))
            {
                frameSpawnDeferred++;
                ParsekLog.VerboseRateLimited("Engine", "spawn-throttle",
                    $"Spawn throttled ({site}): #{index} deferred to next frame " +
                    $"(used {frameSpawnCount}/{GhostPlayback.MaxSpawnsPerFrame})", 1.0);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Destroys materials, engine/RCS particle systems, reentry FX, ghost GameObject,
        /// and fake canopies for a single ghost playback state.
        /// Does NOT remove from any dictionary — caller handles collection bookkeeping.
        /// </summary>
        internal void DestroyGhostResources(GhostPlaybackState state, bool lingerParticleSystems = true)
        {
            if (state.pendingVisualBuild != null)
            {
                GhostVisualBuilder.DestroyPendingTimelineGhostBuild(state.pendingVisualBuild);
                state.pendingVisualBuild = null;
            }

            if (state.materials != null)
            {
                for (int i = 0; i < state.materials.Count; i++)
                {
                    if (state.materials[i] != null)
                        UnityEngine.Object.Destroy(state.materials[i]);
                }
            }
            GhostVisualBuilder.DestroyFairingShellMeshes(state.fairingInfos);

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
            if (audioStopped > 0)
                ParsekLog.Verbose("GhostAudio", $"Cleanup: stopped {audioStopped} audio source(s) for '{state.vesselName}'");

            DestroyReentryFxResources(state.reentryFxInfo);
            // S3: the dust system's GameObject is a child of the ghost and goes with it, but the
            // material and the generated texture are Parsek-created assets that Unity does NOT
            // collect with the hierarchy — the same reason DestroyReentryFxResources exists.
            GhostVisualBuilder.DestroyLaunchDust(state.launchDustInfo);
            state.launchDustInfo = null;
            // S4: the EVA jetpack puff owns a Material and a generated Texture2D on exactly the
            // same terms, so it needs the same explicit release.
            GhostVisualBuilder.DestroyEvaJetpackPlume(state.evaJetpackPlumeInfo);
            state.evaJetpackPlumeInfo = null;

            if (state.ghost != null)
                UnityEngine.Object.Destroy(state.ghost);

            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
        }

        private void DestroyGhostResourcesWithMetrics(GhostPlaybackState state, bool lingerParticleSystems = true)
        {
            if (state == null)
                return;
            if (DestroyGhostResourcesOverrideForTesting != null)
            {
                DestroyGhostResourcesOverrideForTesting(state);
                return;
            }

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
        /// The name DestroyGhost logs: the state's stamped vessel name wins, then the
        /// trajectory's, then "Unknown".
        /// </summary>
        internal static string ResolveDestroyedGhostName(GhostPlaybackState state, IPlaybackTrajectory traj)
        {
            return state?.vesselName ?? traj?.VesselName ?? "Unknown";
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

            string name = ResolveDestroyedGhostName(state, traj);
            ParsekLog.VerboseRateLimited("Engine", $"destroy-{index}",
                $"Ghost #{index} \"{name}\" destroyed ({reason ?? "unknown"})", 1.0);

            // Mesh DISAPPEAR EVENT (map-render-event-logging): mirror the map's GhostDestroyed.
            EmitMeshLifecycleTrace("MeshDestroyed", index, traj, state, reason);

            // Capture ghost root name before the GO is destroyed so we can clear the
            // per-ghost "AudioClip not found" dedupe set (#421). A fresh spawn of this
            // index can then warn once more if the clip is still missing.
            string ghostRootName = state?.ghost != null ? state.ghost.name : null;

            // Fire before destroy so subscribers can read state
            OnGhostDestroyed?.Invoke(new GhostLifecycleEvent
            {
                Index = index, Trajectory = traj, State = state, Flags = flags
            });

            DestroyGhostResourcesWithMetrics(state);

            ghostStates.Remove(index);
            loopPhaseOffsets.Remove(index);
            // Clearing the completion mark makes a slot that is still past end complete once
            // more on the next frame, so the policy makes one last spawn attempt after a held
            // ghost times out. That retry is intended (design 13.5, operator ruling 2026-09-23).
            completedEventFired.Remove(index);
            // Chain-bridge bookkeeping is tied to the slot's ghost-state
            // lifetime; once the ghost is destroyed (for any reason — bridge
            // expiry, in-range failure, anchor unloaded, parent-loop sync
            // failure, etc.) the bridge entry must clear so a fresh spawn at
            // the same index reopens its own bridge.
            chainBridgeOpenedUT.Remove(index);
            GhostVisualBuilder.ClearMissingAudioClipWarnings(ghostRootName);
            DiagnosticsState.health.ghostDestroysThisSession++;
        }

        /// <summary>
        /// Destroys a single overlap ghost's resources. Does NOT remove from any collection.
        /// </summary>
        internal void DestroyOverlapGhostState(GhostPlaybackState state,
            int index, IPlaybackTrajectory traj, string reason)
        {
            if (state == null) return;
            ParsekLog.VerboseRateLimited("Engine", "destroy-overlap",
                $"Destroying overlap ghost cycle={state.loopCycleIndex}", 2.0);
            // Mesh DISAPPEAR EVENT for an overlap copy, paired only with a MeshSpawned this same
            // state wrote (see GhostPlaybackState.meshSpawnTraced). Tracing-gated like every emit.
            if (state.meshSpawnTraced
                && EmitMeshLifecycleTrace("MeshDestroyed", index, traj, state, reason))
                state.meshSpawnTraced = false;
            DestroyGhostResourcesWithMetrics(state);
        }

        internal void SetOverlapGhostsActive(int recIdx, bool active)
        {
            List<GhostPlaybackState> list;
            if (!overlapGhosts.TryGetValue(recIdx, out list)) return;

            for (int i = 0; i < list.Count; i++)
            {
                GhostPlaybackState state = list[i];
                if (state == null || state.ghost == null) continue;
                if (state.ghost.activeSelf == active) continue;

                state.ghost.SetActive(active);
                if (!active)
                    ResetGhostAppearanceTracking(state);
            }
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
                DestroyOverlapGhostState(list[i], recIdx, null, "overlap cleared");
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
            ghostAudioPaused = true;
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
            int pausedOneShots = GhostPlaybackLogic.PauseExplosionOneShotAudio();
            ParsekLog.Verbose("GhostAudio",
                $"PauseAllGhostAudio: paused {pausedPrimary} primary + {pausedOverlap} overlap ghost(s), " +
                $"{pausedOneShots} independent explosion one-shot source(s)");
        }

        /// <summary>
        /// Resumes audio on all active ghost states. Called when the KSP pause menu closes.
        /// </summary>
        internal void UnpauseAllGhostAudio()
        {
            ghostAudioPaused = false;
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
            int resumedOneShots = GhostPlaybackLogic.UnpauseExplosionOneShotAudio();
            ParsekLog.Verbose("GhostAudio",
                $"UnpauseAllGhostAudio: resumed {resumedPrimary} primary + {resumedOverlap} overlap ghost(s), " +
                $"{resumedOneShots} independent explosion one-shot source(s)");
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
                    $"{WarpThresholds.FxSuppress}x");
                return;
            }

            state.explosionFired = true;

            Vector3 worldPos = state.ghost.transform.position;
            if (positioner != null
                && !positioner.TryResolveExplosionAnchorPosition(recIdx, traj, state, out worldPos))
            {
                worldPos = state.ghost.transform.position;
            }
            // Write the shared explosion anchor back onto the ghost root before the
            // loop/overlap boundary event emitters run; the subsequent camera-hold
            // and restart payloads both read this transform as their explosion site.
            state.ghost.transform.position = worldPos;
            float vesselLength = state.reentryFxInfo != null
                ? state.reentryFxInfo.vesselLength
                : GhostVisualBuilder.ComputeGhostLength(state.ghost);
            double power = Mathf.Clamp01(vesselLength / GhostAudioPresets.VesselLengthPowerDivisor);

            if (state.audioPaused)
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"stock-explode-paused-{recIdx}",
                    $"Stock explosion audio suppressed for ghost #{recIdx} \"{traj.VesselName}\" " +
                    "because the pause menu is open; spawning custom visual FX only",
                    10.0);
                GhostVisualBuilder.SpawnExplosionFx(worldPos, vesselLength);
            }
            else
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"stock-explode-{recIdx}",
                    $"Stock FXMonger.Explode for ghost #{recIdx} \"{traj.VesselName}\" " +
                    $"at ({worldPos.x:F1},{worldPos.y:F1},{worldPos.z:F1}) " +
                    $"vesselLength={vesselLength:F1}m power={power.ToString("F2", CultureInfo.InvariantCulture)}",
                    10.0);
                GhostPlaybackLogic.TryTriggerStockExplosionFxOrCustom(
                    worldPos,
                    power,
                    vesselLength,
                    $"ghost #{recIdx} \"{traj.VesselName}\"");
            }

            GhostPlaybackLogic.HideAllGhostParts(state);
            ParsekLog.VerboseRateLimited("Engine", "parts-hidden-explosion",
                $"Ghost #{recIdx} parts hidden after explosion");
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

            // Destroy all primary ghost GOs. Mesh DISAPPEAR EVENT per ghost, the same
            // MeshDestroyed emit DestroyGhost makes: this bulk path tears the meshes down
            // without going through DestroyGhost, so without it a clean teardown (scene
            // exit, quit, rewind) left every ghost's MeshSpawned unpaired and read as a
            // mesh leak to the ghostlife verifier's requireBalanced pairing. No trajectory
            // list here (DestroyAllGhosts takes none) - the state carries its own
            // recordingId + vesselName, which is what the tracer falls back to.
            var keys = new List<int>(ghostStates.Keys);
            foreach (int key in keys)
            {
                if (ghostStates.TryGetValue(key, out var state))
                {
                    EmitMeshLifecycleTrace("MeshDestroyed", key, null, state, "engine teardown");
                    DestroyGhostResources(state);
                }
            }

            // Destroy all overlap ghost GOs. An overlap copy that was once a primary wrote its
            // own MeshSpawned, so it closes that pair here; one that never wrote it (a
            // boundary-overlap secondary, a primary demoted mid-build) stays silent.
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                    DestroyOverlapGhostState(kvp.Value[i], kvp.Key, null, "engine teardown");
            }

            // Clear all engine state
            ghostStates.Clear();
            overlapGhosts.Clear();
            autoLoopLaunchSchedules.Clear();
            autoLoopQueueScratch.Clear();
            loopPhaseOffsets.Clear();
            loadedAnchorVessels.Clear();
            loggedGhostEnter.Clear();
            loggedReshow.Clear();
            completedEventFired.Clear();
            loopFirstRunSpawnFired.Clear();
            earlyDestroyedDebrisCompleted.Clear();
            seamMarkerRuntime.Reset();
            chainBridgeOpenedUT.Clear();
            // Chain-loop unit state is per-frame (rebuilt from host detection on the next
            // SetLoopUnits), but drop the cached transition log state so a re-spawned unit logs
            // its first handoff fresh rather than against a stale prior-session selection.
            currentLoopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;
            lastUnitSelection.Clear();

            // Drop cached structural-event UT lists and per-ghost trace
            // cursors so a re-spawned ghost computes its first delta
            // against its current frame, not against a stale pose from
            // the prior session.
            PlaybackTrace.Reset();
            GhostRenderTrace.Reset();
        }

        /// <summary>
        /// Reindex all engine dictionaries after a recording is deleted.
        /// Keys above the removed index shift down by 1; the removed key is dropped.
        /// </summary>
        internal void ReindexAfterDelete(int removedIndex)
        {
            ShiftIndexKeyedState(removedIndex, insert: false);
        }

        /// <summary>
        /// Mirror of <see cref="ReindexAfterDelete"/> for a mid-list insert into the committed
        /// list (the optimizer's split second half, the Re-Fly origin splitter's TIP): keys at
        /// or above the inserted index shift up by 1, nothing is dropped. The slot at
        /// <paramref name="insertedIndex"/> is left empty for the new recording to spawn into
        /// through the normal path.
        /// </summary>
        internal void ReindexAfterInsert(int insertedIndex)
        {
            ShiftIndexKeyedState(insertedIndex, insert: true);
        }

        // Single list of every index-keyed engine collection, so delete and insert cannot
        // drift apart when the next one is added.
        private void ShiftIndexKeyedState(int pivot, bool insert)
        {
            autoLoopLaunchSchedules.Clear();
            autoLoopQueueScratch.Clear();
            // Owner indices shift; the unit set + transition log are rebuilt next frame
            // from host detection, so clear the stale transition-log keys here.
            lastUnitSelection.Clear();
            if (insert)
            {
                IndexShift.DictAfterInsert(ghostStates, pivot);
                IndexShift.DictAfterInsert(overlapGhosts, pivot);
                IndexShift.DictAfterInsert(loopPhaseOffsets, pivot);
                IndexShift.DictAfterInsert(chainBridgeOpenedUT, pivot);
                IndexShift.SetAfterInsert(loggedGhostEnter, pivot);
                IndexShift.SetAfterInsert(loggedReshow, pivot);
                IndexShift.SetAfterInsert(completedEventFired, pivot);
                IndexShift.SetAfterInsert(loopFirstRunSpawnFired, pivot);
                IndexShift.SetAfterInsert(earlyDestroyedDebrisCompleted, pivot);
            }
            else
            {
                IndexShift.DictAfterDelete(ghostStates, pivot);
                IndexShift.DictAfterDelete(overlapGhosts, pivot);
                IndexShift.DictAfterDelete(loopPhaseOffsets, pivot);
                IndexShift.DictAfterDelete(chainBridgeOpenedUT, pivot);
                IndexShift.SetAfterDelete(loggedGhostEnter, pivot);
                IndexShift.SetAfterDelete(loggedReshow, pivot);
                IndexShift.SetAfterDelete(completedEventFired, pivot);
                IndexShift.SetAfterDelete(loopFirstRunSpawnFired, pivot);
                IndexShift.SetAfterDelete(earlyDestroyedDebrisCompleted, pivot);
            }
        }
    }
}
