using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Ghost info dictionaries, spawn-time snapshot baselines and orphan engine playback.
    internal static partial class GhostPlaybackLogic
    {
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
            // Wheel damage swaps are built intact (busted meshes hidden by the builder), so the
            // dictionary needs no spawn pass of its own; the snapshot baseline and the prefix
            // replay below set any wheel that was already broken.
            if (result.wheelDamageInfos != null)
                state.wheelDamageInfos = BuildDictByPid(result.wheelDamageInfos, w => w.partPersistentId);
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
            int wheelsBroken = 0;
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

                if (actions.wheelDamaged
                    && ApplyWheelDamageState(state, pid, damaged: true))
                {
                    wheelsBroken++;
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
                $"brokenWheels={wheelsBroken} " +
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
            /// <summary>The snapshot said this part's wheel is broken: spawn showing the busted mesh.</summary>
            public bool wheelDamaged;
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
            // Independent of every other surface: a mesh swap, not a pose.
            actions.wheelDamaged = baseline.wheelDamaged;

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
    }
}
