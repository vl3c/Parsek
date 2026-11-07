using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Explosion triggers, ghost part visibility, the loop-cycle reset and compound-part visibility.
    internal static partial class GhostPlaybackLogic
    {
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

            // 2b. Wheels: back to intact. A wheel that broke during the prior cycle must be whole at
            //     the top of the next one; the snapshot baseline (step 3f) re-breaks any wheel that
            //     was already broken when the recording started, and the cycle's own WheelBroken
            //     events break the rest on their original UT.
            if (state.wheelDamageInfos != null)
            {
                foreach (var kvp in state.wheelDamageInfos)
                    ApplyWheelDamageState(state, kvp.Key, damaged: false);
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

        /// <summary>
        /// Re-applies stock ModuleStructuralNode visibility (the fairing interstage truss and
        /// cap plates) against the ghost's current logical part set: a structure whose only
        /// attached stage was decoupled or destroyed hides (stock re-runs CheckDisplay from
        /// DecoupleAction; see FairingStructureNodeGhostInfo for the parent-side case where
        /// stock keeps it). Idempotent; returns true when anything changed.
        /// </summary>
        internal static bool RefreshFairingStructureVisibility(GhostPlaybackState state)
        {
            if (state == null || state.fairingInfos == null || state.fairingInfos.Count == 0)
                return false;

            int changed = 0;
            foreach (var kvp in state.fairingInfos)
            {
                FairingGhostInfo info = kvp.Value;
                if (info == null || info.structureNodes == null) continue;
                for (int i = 0; i < info.structureNodes.Count; i++)
                {
                    FairingStructureNodeGhostInfo node = info.structureNodes[i];
                    if (node == null || node.structureObject == null) continue;
                    bool visible = GhostVisualBuilder.ComputeStructureNodeVisible(node, state.logicalPartIds);
                    if (node.structureObject.activeSelf == visible) continue;
                    node.structureObject.SetActive(visible);
                    changed++;
                    ParsekLog.VerboseRateLimited("GhostVisual",
                        $"fairing-struct-{info.partPersistentId}-{node.rootObject}",
                        $"Fairing structure '{node.rootObject}' on pid={info.partPersistentId} " +
                        $"now {(visible ? "shown" : "hidden")} (attached stage " +
                        $"{(GhostVisualBuilder.StructureNodeHasAttachedPart(node, state.logicalPartIds) ? "present" : "gone")})");
                }
            }
            return changed > 0;
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
    }
}
