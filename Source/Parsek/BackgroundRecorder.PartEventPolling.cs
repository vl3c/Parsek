using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    internal partial class BackgroundRecorder
    {
        #region Part Event Polling

        /// <summary>
        /// The background sink for <see cref="PartStatePoller"/>: events land in the polled
        /// vessel's tree recording, lines on the <c>BgRecorder</c> tag naming the bg vessel.
        /// One instance is re-bound to the vessel at each Check*State call.
        /// </summary>
        private sealed class BackgroundPartPollSink : PartPollSink
        {
            internal BackgroundVesselState State;
            internal Recording TreeRec;

            internal override string Tag => "BgRecorder";
            internal override List<PartEvent> Events => TreeRec.PartEvents;
            internal override string EventSuffix() => $" (bg vessel {State.vesselPid})";
            internal override string DeployableBrokenSuffix(ModuleDeployablePart.DeployState ds) =>
                $" (bg vessel {State.vesselPid}, deployable-broken edge)";
            internal override string DeployableNoPoseRateKey() =>
                $"deployable-no-pose-animation-{State.vesselPid}";
            internal override string DeployableNoPoseNote() =>
                $"(deployable-no-pose-animation gate, bg vessel {State.vesselPid})";
            internal override string LabelSuffix(string label) => $" ({label}, bg vessel {State.vesselPid})";
            internal override string VesselFirstLabelSuffix(string label) => $" (bg vessel {State.vesselPid}, {label})";
            internal override string ConverterDepartureContext(Vessel v) =>
                $"bg vessel pid={State.vesselPid} rec={State.recordingId}";
        }

        private readonly BackgroundPartPollSink pollSink = new BackgroundPartPollSink();

        private PartPollSink BindPollSink(BackgroundVesselState state, Recording treeRec)
        {
            pollSink.State = state;
            pollSink.TreeRec = treeRec;
            return pollSink;
        }

        /// <summary>
        /// Background mirror of <c>FlightRecorder.ShouldPollCachedModule</c> (M5). Returns true when
        /// a cached engine / RCS / robotic entry is still on the BG vessel being polled; logs once
        /// per key when it is not. The identity comparison is the same live-object one the
        /// foreground uses — see <see cref="FlightRecorder.DecideCachedModulePoll(bool,bool,bool,bool)"/>.
        /// </summary>
        private static bool ShouldPollCachedBackgroundModule(
            Part part, PartModule module, Vessel v, BackgroundVesselState state,
            ulong key, string family)
        {
            var decision = FlightRecorder.DecideCachedModulePoll(part, module, v);
            if (decision == FlightRecorder.CachedModulePollDecision.Poll)
                return true;

            if (decision == FlightRecorder.CachedModulePollDecision.SkipForeignVessel
                && state != null && state.loggedForeignCachedModuleKeys.Add(key))
            {
                uint pid; int midx;
                FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                ParsekLog.Info("BgRecorder",
                    $"Cached {family} module dropped from poll: pid={pid} midx={midx} " +
                    $"'{part?.partInfo?.name ?? "unknown"}' is no longer on bg vessel {state.vesselPid} " +
                    $"— events for it would have landed in rec={state.recordingId}");
            }

            return false;
        }

        /// <summary>
        /// Polls all part event types for a background vessel in loaded/physics mode.
        /// Duplicates the Layer 2 wrapper methods from FlightRecorder, calling the
        /// same Layer 1 static transition methods.
        ///
        /// Part Event Coverage Audit (BackgroundRecorder vs FlightRecorder):
        ///
        ///   POLLED (per-physics-frame, in PollPartEvents):
        ///     [x] CheckParachuteState        - ParachuteDeployed / ParachuteCut / ParachuteSemiDeployed / ParachuteRepacked
        ///     [x] CheckJettisonState          - ShroudJettisoned
        ///     [x] CheckEngineState            - EngineIgnited / EngineShutdown / EngineThrottle
        ///     [x] CheckRcsState               - RCSActivated / RCSStopped / RCSThrottle
        ///     [x] CheckDeployableState        - DeployableExtended / DeployableRetracted / DeployableBroken
        ///     [x] CheckLadderState            - DeployableExtended / DeployableRetracted (ladders)
        ///     [x] CheckAnimationGroupState    - DeployableExtended / DeployableRetracted (anim groups)
        ///     [x] CheckAeroSurfaceState       - DeployableExtended / DeployableRetracted (aero surfaces)
        ///     [x] CheckControlSurfaceState    - DeployableExtended / DeployableRetracted (control surfaces)
        ///     [x] CheckRobotArmScannerState   - DeployableExtended / DeployableRetracted (robot arms)
        ///     [x] CheckAnimateHeatState       - ThermalAnimationHot / ThermalAnimationMedium / ThermalAnimationCold
        ///     [x] CheckAnimateGenericState    - DeployableExtended / DeployableRetracted (generic animations)
        ///     [x] CheckLightState             - LightOn / LightOff / LightBlinkEnabled / LightBlinkDisabled / LightBlinkRate
        ///     [x] CheckGearState              - GearDeployed / GearRetracted
        ///     [x] CheckCargoBayState          - CargoBayOpened / CargoBayClosed
        ///     [x] CheckFairingState           - FairingJettisoned
        ///     [x] CheckRoboticState           - RoboticMotionStarted / RoboticPositionSample / RoboticMotionStopped
        ///                                       (wheel MOTOR spin excluded — derived from ground speed at playback)
        ///     [x] CheckConverterState         - ConverterActivated / ConverterDeactivated
        ///     [x] CheckWheelDamageState       - WheelBroken / WheelRepaired
        ///     [x] CheckEvaState               - EvaJetpackDeployed / EvaJetpackStowed / EvaRagdollStarted / EvaRagdollEnded
        ///                                       (jetpack THRUST is flight-only by nature: a background
        ///                                        kerbal receives no input, so the edge never fires. That
        ///                                        is parity with reality, not a coverage gap.)
        ///
        ///   GAME-EVENT DRIVEN (via SubscribePartEvents):
        ///     [x] onPartDie                   - Destroyed / ParachuteDestroyed (OnBackgroundPartDie)
        ///     [x] onPartJointBreak            - Decoupled (OnBackgroundPartJointBreak)
        ///
        ///   NOT APPLICABLE to background vessels (handled elsewhere):
        ///     [-] Docked / Undocked           - branch management in ParsekFlight
        ///     [-] InventoryPartPlaced/Removed  - EVA-only, active vessel only
        /// </summary>
        private void PollPartEvents(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            // Count delta before/after pattern: all CheckXState methods only
            // mutate treeRec.PartEvents (no Points/OrbitSegments/etc changes),
            // so a single post-polling dirty mark guarded on count-delta covers
            // all 20 child checks without 20 inline MarkFilesDirty calls.
            // If no events were emitted this poll, the mark is skipped — keeps
            // the frequent per-physics-frame poll cheap.
            int prePartEventCount = treeRec.PartEvents.Count;

            CheckParachuteState(v, state, treeRec, ut);
            CheckJettisonState(v, state, treeRec, ut);
            CheckEngineState(v, state, treeRec, ut);
            CheckRcsState(v, state, treeRec, ut);
            CheckDeployableState(v, state, treeRec, ut);
            CheckLadderState(v, state, treeRec, ut);
            CheckAnimationGroupState(v, state, treeRec, ut);
            CheckAeroSurfaceState(v, state, treeRec, ut);
            CheckControlSurfaceState(v, state, treeRec, ut);
            CheckRobotArmScannerState(v, state, treeRec, ut);
            CheckAnimateHeatState(v, state, treeRec, ut);
            CheckAnimateGenericState(v, state, treeRec, ut);
            CheckLightState(v, state, treeRec, ut);
            CheckGearState(v, state, treeRec, ut);
            CheckCargoBayState(v, state, treeRec, ut);
            CheckFairingState(v, state, treeRec, ut);
            CheckRoboticState(v, state, treeRec, ut);
            CheckConverterState(v, state, treeRec, ut);
            CheckWheelDamageState(v, state, treeRec, ut);
            CheckEvaState(v, state, treeRec, ut);

            if (treeRec.PartEvents.Count > prePartEventCount)
                treeRec.MarkFilesDirty();
        }

        private void CheckParachuteState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollParachutes(v, state.parachuteStates, ut, BindPollSink(state, treeRec));
        }

        private void CheckJettisonState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollJettisons(v, state.jettisonedShrouds, state.jettisonNameRawCache,
                state.parsedJettisonNamesCache, ut, BindPollSink(state, treeRec));
        }

        private void CheckDeployableState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollDeployables(v, state.brokenDeployables, state.extendedDeployables,
                ut, BindPollSink(state, treeRec));
        }

        /// <summary>
        /// S7 BG parity - and this is the case that MATTERS most for converters: a mining base you
        /// left behind keeps producing while you are elsewhere, and that is exactly when the base is
        /// a background vessel. Same Layer-1 transition, no window machinery (the harvest window is
        /// a flight-side resource-attribution device, unrelated to this visual).
        /// </summary>
        private void CheckConverterState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollConverters(v, state.activeConverterParts, ut, BindPollSink(state, treeRec));
        }

        /// <summary>
        /// Wheel damage BG parity. This is the path an EVA REPAIR takes: the engineer on EVA is
        /// the active vessel, so the rover whose wheel he fixes is a loaded background vessel and
        /// only this poll can see its <c>isDamaged</c> flag clear. Same Layer-1 transition as the
        /// flight poll.
        /// </summary>
        private void CheckWheelDamageState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollWheelDamage(v, state.damagedWheelParts, ut, BindPollSink(state, treeRec));
        }

        /// <summary>
        /// S4 BG parity for the two NON-momentary EVA signals. A background kerbal being knocked
        /// ragdoll is a real, visible case (a rover runs him over while you fly something else), and
        /// his jetpack pose persists across the span.
        ///
        /// THRUST is absent, and that is parity rather than a gap: JetpackIsThrusting is recomputed
        /// from fuel flow driven by player INPUT, and a background kerbal receives none - so the
        /// edge can never fire and a wrapper for it would be dead code.
        /// </summary>
        private void CheckEvaState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || !v.isEVA || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var eva = p.FindModuleImplementing<KerbalEVA>();
                if (eva == null) continue;

                string partName = p.partInfo?.name ?? "unknown";

                var jetpackEvt = FlightRecorder.CheckEvaJetpackTransition(
                    p.persistentId, partName, eva.JetpackDeployed, state.jetpackDeployedParts, ut);
                if (jetpackEvt.HasValue)
                {
                    treeRec.PartEvents.Add(jetpackEvt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {jetpackEvt.Value.eventType} '{partName}' " +
                        $"pid={p.persistentId} (bg vessel {state.vesselPid}, eva)");
                }

                var ragdollEvt = FlightRecorder.CheckEvaRagdollTransition(
                    p.persistentId, partName, eva.isRagdoll, state.ragdollParts, ut);
                if (ragdollEvt.HasValue)
                {
                    treeRec.PartEvents.Add(ragdollEvt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {ragdollEvt.Value.eventType} '{partName}' " +
                        $"pid={p.persistentId} (bg vessel {state.vesselPid}, eva)");
                }
            }
        }

        private void CheckLightState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var light = p.FindModuleImplementing<ModuleLight>();
                if (light == null) continue;

                var evt = FlightRecorder.CheckLightTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", light.isOn, state.lightsOn, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }

                reusableEventBuffer.Clear();
                FlightRecorder.CheckLightBlinkTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    light.isBlinking, light.blinkRate,
                    state.blinkingLights, state.lightBlinkRates, ut, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckGearState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollGear(v, state.deployedGear, ut, BindPollSink(state, treeRec));
        }

        private void CheckCargoBayState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollCargoBays(v, state.openCargoBays, state.loggedCargoBayDeployIndexIssues,
                state.loggedCargoBayAnimationIssues, state.loggedCargoBayClosedPositionIssues,
                ut, BindPollSink(state, treeRec));
        }

        private void CheckFairingState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            PartStatePoller.PollFairings(v, state.deployedFairings, state.loggedFairingReadFailures,
                ut, BindPollSink(state, treeRec));
        }

        private void CheckEngineState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (state.cachedEngines == null) return;

            for (int i = 0; i < state.cachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = state.cachedEngines[i];
                if (part == null || engine == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                // M5 ownership guard, background mirror: a booster staged off a BG-loaded vessel
                // keeps a live ModuleEngines in this cache and would otherwise write its burn into
                // the parent's recording. Same pure core as the foreground poll.
                if (!ShouldPollCachedBackgroundModule(part, engine, v, state, key, "engine")) continue;
                bool ignited = FlightRecorder.ShouldRecordEngineAsIgnited(
                    engine.EngineIgnited, engine.isOperational, engine.finalThrust);
                float recordedPower = FlightRecorder.ComputeRecordedEnginePower(
                    engine.currentThrottle, engine.finalThrust, engine.maxThrust);

                reusableEventBuffer.Clear();
                FlightRecorder.CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, recordedPower,
                    state.activeEngineKeys, state.lastThrottle, ut, reusableEventBuffer);

                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} " +
                        $"val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");

                    // Engine-ignite jettison fallback (same as FlightRecorder)
                    if (reusableEventBuffer[e].eventType == PartEventType.EngineIgnited &&
                        part.FindModuleImplementing<ModuleJettison>() != null)
                    {
                        var shroudEvt = FlightRecorder.CheckJettisonTransition(
                            part.persistentId,
                            part.partInfo?.name ?? "unknown",
                            isJettisoned: true,
                            jettisonedSet: state.jettisonedShrouds,
                            ut: ut);
                        if (shroudEvt.HasValue)
                        {
                            treeRec.PartEvents.Add(shroudEvt.Value);
                            ParsekLog.Verbose("BgRecorder", $"Part event: {shroudEvt.Value.eventType} '{shroudEvt.Value.partName}' " +
                                $"pid={shroudEvt.Value.partPersistentId} (engine-ignite fallback, bg vessel {state.vesselPid})");
                        }
                    }
                }
            }
        }

        private void CheckRcsState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (state.cachedRcsModules == null) return;

            for (int i = 0; i < state.cachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = state.cachedRcsModules[i];
                if (part == null || rcs == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                if (!ShouldPollCachedBackgroundModule(part, rcs, v, state, key, "rcs")) continue;
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                uint pid = part.persistentId;
                string partName = part.partInfo?.name ?? "unknown";

                reusableEventBuffer.Clear();
                FlightRecorder.ProcessRcsDebounce(
                    key, pid, moduleIndex, partName,
                    active, rcs.thrustForces, rcs.thrusterPower, ut,
                    state.rcsActiveFrameCount, state.activeRcsKeys, state.lastRcsThrottle, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} " +
                        $"val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckLadderState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyRetractableLadderState(module, out isDeployed, out isRetracted))
                    {
                        if (state.loggedLadderClassificationMisses.Add(key))
                        {
                            ParsekLog.Verbose("BgRecorder", $"Ladder: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }

                    var evt = FlightRecorder.CheckLadderTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedLadders, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} (ladder, bg vessel {state.vesselPid})");
                    }

                    break; // one RetractableLadder module per ladder part
                }
            }
        }

        private void CheckAnimationGroupState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAnimationGroupState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAnimationGroupClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimationGroup: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAnimationGroups, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animation-group, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAeroSurfaceState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAeroSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAeroSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AeroSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAeroSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (aero-surface, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckControlSurfaceState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyControlSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedControlSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"ControlSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedControlSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (control-surface, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckRobotArmScannerState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyRobotArmScannerState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedRobotArmScannerClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"RobotArmScanner: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedRobotArmScannerModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (robot-arm-scanner, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAnimateHeatState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                        continue;

                    if (!FlightRecorder.TryClassifyAnimateHeatState(
                        module, out float normalizedHeat, out string sourceField))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAnimateHeatClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimateHeat: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimateHeatTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        normalizedHeat, state.animateHeatLevels, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animate-heat, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAnimateGenericState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                // Avoid duplicate events for modules already tracked via dedicated handlers
                bool hasDedicatedHandler =
                    p.FindModuleImplementing<ModuleDeployablePart>() != null ||
                    p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                    p.FindModuleImplementing<ModuleCargoBay>() != null;

                bool hasRetractableLadder = false;
                bool hasAnimationGroup = false;
                bool hasAeroSurface = false;
                bool hasControlSurface = false;
                bool hasRobotArmScanner = false;
                bool hasAnimateHeat = false;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                        hasRetractableLadder = true;
                    if (string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                        hasAnimationGroup = true;
                    if (string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                        hasAeroSurface = true;
                    if (string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                        hasControlSurface = true;
                    if (string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                        hasRobotArmScanner = true;
                    if (string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                        hasAnimateHeat = true;
                    if (hasRetractableLadder || hasAnimationGroup ||
                        hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat)
                        break;
                }
                if (hasDedicatedHandler || hasRetractableLadder || hasAnimationGroup ||
                    hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    ModuleAnimateGeneric animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAnimateGenericState(animateModule, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAnimateGenericClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimateGeneric: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAnimateGenericModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (anim-generic, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckRoboticState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            if (state.cachedRoboticModules == null) return;

            for (int i = 0; i < state.cachedRoboticModules.Count; i++)
            {
                var (part, module, moduleIndex, moduleName) = state.cachedRoboticModules[i];
                if (part == null || module == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                if (!ShouldPollCachedBackgroundModule(part, module, v, state, key, "robotic")) continue;

                // Mirrors the foreground gate: wheel motor spin and wheel steering deflection are
                // both derived from the ghost's own ground motion at playback, not recorded. See
                // FlightRecorder.IsDerivedWheelVisualModuleName.
                // Logged once per key, exactly as FlightRecorder.CheckRoboticState does — a silent
                // skip here would leave the BG half of the gate invisible in KSP.log, and the BG
                // half is the one a rover-on-a-background-vessel report lands on.
                if (FlightRecorder.IsDerivedWheelVisualModuleName(moduleName))
                {
                    if (state.loggedRoboticModuleKeys.Add(key))
                    {
                        ParsekLog.Verbose("BgRecorder", $"Robotics: derived wheel visual not recorded for " +
                            $"'{part.partInfo?.name}' pid={part.persistentId} midx={moduleIndex} " +
                            $"module={moduleName} (bg vessel {state.vesselPid}); " +
                            $"derived from ghost ground motion at playback");
                    }
                    continue;
                }

                bool hasPosition = FlightRecorder.TryGetRoboticPositionValue(
                    module, moduleName, out float positionValue, out float deadband, out string sourceField);

                if (!hasPosition) continue;

                bool hasMovingSignal = FlightRecorder.TryGetRoboticMovingState(module, out bool movingSignal);

                var events = FlightRecorder.CheckRoboticTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    movingSignal, positionValue, deadband, ut,
                    state.activeRoboticKeys, state.lastRoboticPosition,
                    state.lastRoboticSampleUT, roboticSampleIntervalSeconds);

                for (int e = 0; e < events.Count; e++)
                {
                    treeRec.PartEvents.Add(events[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F3} (bg vessel {state.vesselPid})");
                }
            }
        }

        internal int AppendStructuralEventSnapshot(
            double eventUT,
            IEnumerable<Vessel> involved,
            string eventType,
            bool preferRootPartSurfacePose = false)
        {
            if (tree == null || involved == null) return 0;

            int considered = 0;
            int backgroundMatches = 0;
            int loadedMatches = 0;
            int recordingMatches = 0;
            int rejected = 0;
            int deduped = 0;
            int appended = 0;

            foreach (Vessel v in involved)
            {
                considered++;
                if (v == null) continue;

                uint pid = v.persistentId;
                string recordingId;
                if (!tree.BackgroundMap.TryGetValue(pid, out recordingId))
                    continue;
                backgroundMatches++;

                BackgroundVesselState state;
                if (!loadedStates.TryGetValue(pid, out state))
                    continue;
                loadedMatches++;

                Recording treeRec;
                if (!tree.Recordings.TryGetValue(recordingId, out treeRec))
                    continue;
                recordingMatches++;

                // Mirrors the foreground recorder's same-UT structural dedup:
                // multiple structural events inside one Part.Undock() call fire
                // at the same physics-clock UT and would each append an
                // identical flagged point. Equal-UT appends pass the #419
                // tolerance but break strictly-increasing consumers
                // (Catmull-Rom section fit).
                if (FlightRecorder.HasStructuralEventSnapshotAtTail(treeRec.Points, eventUT))
                {
                    ParsekLog.Verbose("Pipeline-Smoothing",
                        string.Format(CultureInfo.InvariantCulture,
                            "BG structural event snapshot deduped: same-UT structural point already " +
                            "committed (event={0} ut={1:R} vesselId={2} recId={3})",
                            eventType ?? "unknown",
                            eventUT,
                            pid,
                            recordingId ?? "<null>"));
                    deduped++;
                    continue;
                }

                Vector3 velocity = v.packed
                    ? (Vector3)v.obt_velocity
                    : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());
                TrajectoryPoint point = CreateAbsoluteTrajectoryPointFromVessel(
                    v,
                    eventUT,
                    velocity,
                    preferRootPartSurfacePose);
                point = FlightRecorder.ApplyStructuralEventFlag(point);
                TrajectoryPoint absolutePoint = point;
                bool relativeApplied = ApplyBackgroundRelativeOffset(state, ref point, v, eventUT);
                ApplySurfaceClearanceToPoint(state, v, ref point);
                // Body-fixed shadow gets clearance too when
                // the active section is RELATIVE (see helper docstring).
                if (relativeApplied)
                {
                    ApplySurfaceClearanceToBodyFixedShadow(state, v, ref absolutePoint);
                }

                if (!ApplyTrajectoryPointToRecording(treeRec, point))
                {
                    rejected++;
                    continue;
                }

                AppendFrameToCurrentTrackSection(
                    state,
                    point,
                    relativeApplied ? (TrajectoryPoint?)absolutePoint : null);
                state.lastRecordedUT = point.ut;
                state.lastRecordedVelocity = point.velocity;
                if (v.transform != null)
                {
                    state.lastWorldRotation = TrajectoryMath.SanitizeQuaternion(v.transform.rotation);
                    state.hasLastWorldRotation = true;
                }
                ActivateBackgroundHighFidelitySampling(
                    state,
                    eventUT,
                    $"structural-event-{eventType ?? "unknown"}");
                appended++;

                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "BG structural event snapshot appended: event={0} ut={1:R} vesselId={2} recId={3} " +
                        "flags={4} lat={5:R} lon={6:R} alt={7:R} relativeApplied={8}",
                        eventType ?? "unknown",
                        point.ut,
                        pid,
                        recordingId ?? "<null>",
                        point.flags,
                        point.latitude,
                        point.longitude,
                        point.altitude,
                        relativeApplied ? "true" : "false"));
            }

            if (considered > appended + deduped)
            {
                ParsekLog.Verbose("Pipeline-Smoothing",
                    string.Format(CultureInfo.InvariantCulture,
                        "BG structural event snapshot skipped: event={0} ut={1:R} considered={2} " +
                        "appended={3} backgroundMatches={4} loadedMatches={5} recordingMatches={6} " +
                        "rejected={7} deduped={8}",
                        eventType ?? "unknown",
                        eventUT,
                        considered,
                        appended,
                        backgroundMatches,
                        loadedMatches,
                        recordingMatches,
                        rejected,
                        deduped));
            }

            return appended;
        }

        #endregion
    }
}
