using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static partial class RecordingOptimizer
    {
        /// <summary>
        /// Checks if a part event represents a permanent one-way visual state change
        /// that must be seeded in subsequent segments after a split.
        ///
        /// ParachuteRepacked is deliberately NOT here, and neither is ParachuteCut — both belong to
        /// the reversible family. The reasoning, since a repack undoing a cut is exactly the case
        /// that makes "is a cut permanent?" a live question:
        ///
        /// A cut was never in this family to begin with. ParachuteDestroyed is (the part is gone for
        /// good and the tail must keep it hidden), but a cut leaves the part present, and a stock EVA
        /// Repack turns it back into a stowed chute — so cut is not one-way and a repack has no
        /// permanence to revoke. Both stay in the reversible family, where the parachute state
        /// machine collapses a per-part run of events to its last state and
        /// <see cref="AppendReversibleStateSeeds"/> emits that last state VERBATIM, in either
        /// direction: a head ending Cut seeds ParachuteCut, a head ending Repacked seeds
        /// ParachuteRepacked, and the two render differently (cut = canopy gone AND cap hidden;
        /// repacked = canopy gone WITH the cap back on). Only ParachuteDestroyed is skipped by the
        /// inactive emitter, because ForwardPermanentStateEvents copies it instead.
        ///
        /// Before the bidirectional emitter, both inactive states fell through to "no seed at all",
        /// which happened to render correctly ONLY because the tail's ghost spawned at the stowed
        /// prefab pose. That is no longer true once a TIP spawns from a snapshot baseline, so the
        /// terminal state now has to be stated rather than implied.
        /// </summary>
        internal static bool IsPermanentVisualStateEvent(PartEventType type)
        {
            switch (type)
            {
                case PartEventType.ShroudJettisoned:
                case PartEventType.FairingJettisoned:
                case PartEventType.Decoupled:
                case PartEventType.Destroyed:
                case PartEventType.ParachuteDestroyed:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Copies permanent visual state events from the first half to the start of
        /// the second half as seed events at splitUT. This ensures the ghost for the
        /// second segment reflects the vessel's visual state at the split point
        /// (e.g., shroud already jettisoned, parts already decoupled).
        /// </summary>
        internal static void ForwardPermanentStateEvents(
            List<PartEvent> firstHalf, List<PartEvent> secondHalf, double splitUT)
        {
            if (firstHalf == null || firstHalf.Count == 0) return;

            int forwarded = 0;
            for (int i = 0; i < firstHalf.Count; i++)
            {
                if (!IsPermanentVisualStateEvent(firstHalf[i].eventType)) continue;

                var seed = firstHalf[i];
                seed.ut = splitUT;
                secondHalf.Insert(forwarded, seed);
                forwarded++;
            }

            if (forwarded > 0)
                ParsekLog.Info("Optimizer",
                    $"Forwarded {forwarded} permanent state event(s) as seeds at UT={splitUT:F1}");
        }

        internal static List<PartEvent> BuildTransientStateSeeds(List<PartEvent> partEvents, double splitUT)
        {
            var seeds = new List<PartEvent>();
            if (partEvents == null || partEvents.Count == 0) return seeds;

            var indexedEvents = new List<IndexedPartEvent>();
            for (int i = 0; i < partEvents.Count; i++)
            {
                var evt = partEvents[i];
                if (evt.ut - splitUT > SplitSeedTimeEpsilon) continue;
                if (!IsTransientVisualStateEvent(evt.eventType)) continue;

                indexedEvents.Add(new IndexedPartEvent
                {
                    Event = evt,
                    OriginalIndex = i
                });
            }

            if (indexedEvents.Count == 0) return seeds;

            indexedEvents.Sort((a, b) =>
            {
                int byTime = a.Event.ut.CompareTo(b.Event.ut);
                return byTime != 0 ? byTime : a.OriginalIndex.CompareTo(b.OriginalIndex);
            });

            var engineStates = new Dictionary<ulong, TransientPartState>();
            var rcsStates = new Dictionary<ulong, TransientPartState>();
            var deployableStates = new Dictionary<ulong, TransientPartState>();
            var gearStates = new Dictionary<ulong, TransientPartState>();
            var cargoBayStates = new Dictionary<ulong, TransientPartState>();
            var lightPowerStates = new Dictionary<ulong, TransientPartState>();
            var lightBlinkStates = new Dictionary<ulong, TransientPartState>();
            var heatStates = new Dictionary<ulong, TransientPartState>();
            var parachuteStates = new Dictionary<ulong, TransientPartState>();
            var roboticStates = new Dictionary<ulong, TransientPartState>();
            var converterStates = new Dictionary<ulong, TransientPartState>();
            var evaJetpackStates = new Dictionary<ulong, TransientPartState>();
            var evaRagdollStates = new Dictionary<ulong, TransientPartState>();

            for (int i = 0; i < indexedEvents.Count; i++)
            {
                var evt = indexedEvents[i].Event;
                ulong key = TransientVisualStateKey(evt);
                switch (evt.eventType)
                {
                    case PartEventType.EngineIgnited:
                        engineStates[key] = BuildTransientState(evt, active: true, value: evt.value);
                        break;
                    case PartEventType.EngineThrottle:
                        UpdateThrottleState(engineStates, key, evt, activeWhenUnseen: evt.value > 0f);
                        break;
                    case PartEventType.EngineShutdown:
                        engineStates[key] = BuildTransientState(evt, active: false, value: 0f);
                        break;
                    case PartEventType.RCSActivated:
                        rcsStates[key] = BuildTransientState(evt, active: true, value: evt.value);
                        break;
                    case PartEventType.RCSThrottle:
                        UpdateThrottleState(rcsStates, key, evt, activeWhenUnseen: evt.value > 0f);
                        break;
                    case PartEventType.RCSStopped:
                        rcsStates[key] = BuildTransientState(evt, active: false, value: 0f);
                        break;
                    case PartEventType.DeployableExtended:
                        deployableStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.DeployableExtended);
                        break;
                    case PartEventType.DeployableRetracted:
                        deployableStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.DeployableRetracted);
                        break;
                    // S6: the PARACHUTE-TRIO pattern. Retracted and Broken are both "panel not
                    // extended", but they are NOT interchangeable poses — retracted renders a
                    // folded panel, broken renders NO panel — so the seed carries evt.eventType
                    // VERBATIM rather than one shared inactive type. `active: false` only routes
                    // it past the active-direction emitter; AppendReversibleStateSeeds still
                    // emits it, because DeployableBroken is not an IsPermanentVisualStateEvent.
                    case PartEventType.DeployableBroken:
                        deployableStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: evt.eventType);
                        break;
                    case PartEventType.GearDeployed:
                        gearStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.GearDeployed);
                        break;
                    case PartEventType.GearRetracted:
                        gearStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.GearRetracted);
                        break;
                    case PartEventType.CargoBayOpened:
                        cargoBayStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.CargoBayOpened);
                        break;
                    case PartEventType.CargoBayClosed:
                        cargoBayStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.CargoBayClosed);
                        break;
                    case PartEventType.LightOn:
                        lightPowerStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.LightOn);
                        break;
                    case PartEventType.LightOff:
                        lightPowerStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.LightOff);
                        break;
                    case PartEventType.LightBlinkEnabled:
                        lightBlinkStates[key] = BuildTransientState(
                            evt, active: true, value: evt.value, seedEventType: PartEventType.LightBlinkEnabled);
                        break;
                    case PartEventType.LightBlinkDisabled:
                        lightBlinkStates[key] = BuildTransientState(
                            evt, active: false, value: evt.value, seedEventType: PartEventType.LightBlinkDisabled);
                        break;
                    case PartEventType.LightBlinkRate:
                        UpdateLightBlinkRateState(lightBlinkStates, key, evt);
                        break;
                    case PartEventType.ThermalAnimationHot:
                        heatStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.ThermalAnimationHot);
                        break;
                    case PartEventType.ThermalAnimationMedium:
                        heatStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.ThermalAnimationMedium);
                        break;
                    case PartEventType.ThermalAnimationCold:
                        heatStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.ThermalAnimationCold);
                        break;
                    case PartEventType.ParachuteSemiDeployed:
                        parachuteStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.ParachuteSemiDeployed);
                        break;
                    case PartEventType.ParachuteDeployed:
                        parachuteStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.ParachuteDeployed);
                        break;
                    case PartEventType.ParachuteCut:
                    case PartEventType.ParachuteDestroyed:
                    // All three are "canopy not flying", but they are NOT interchangeable poses, so
                    // the seed carries `evt.eventType` verbatim rather than one shared inactive
                    // type: Cut renders canopy-gone-and-cap-hidden, Repacked renders
                    // canopy-gone-with-cap-on (stock Repack() restores the cap), Destroyed hides the
                    // whole part. `Active=false` only routes them past the active-direction emitter;
                    // AppendReversibleStateSeeds still emits Cut and Repacked, and skips only
                    // Destroyed (ForwardPermanentStateEvents owns that one).
                    case PartEventType.ParachuteRepacked:
                        parachuteStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: evt.eventType);
                        break;
                    case PartEventType.EvaJetpackDeployed:
                        evaJetpackStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.EvaJetpackDeployed);
                        break;
                    case PartEventType.EvaJetpackStowed:
                        evaJetpackStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.EvaJetpackStowed);
                        break;
                    case PartEventType.EvaRagdollStarted:
                        evaRagdollStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.EvaRagdollStarted);
                        break;
                    case PartEventType.EvaRagdollEnded:
                        evaRagdollStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.EvaRagdollEnded);
                        break;
                    // The THRUST pair follows the RCS RULE and is absent from this reducer on
                    // purpose: "not thrusting" is the prefab default, so only the ACTIVE direction
                    // could ever need a seed - and a thrust burst that was still running at a split
                    // is a momentary input the tail's own next frame re-establishes. See
                    // AppendActiveStateSeeds' call-site comment for the same argument on heat.
                    case PartEventType.ConverterActivated:
                        converterStates[key] = BuildTransientState(
                            evt, active: true, value: 0f, seedEventType: PartEventType.ConverterActivated);
                        break;
                    case PartEventType.ConverterDeactivated:
                        converterStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: PartEventType.ConverterDeactivated);
                        break;
                    case PartEventType.RoboticMotionStarted:
                    case PartEventType.RoboticPositionSample:
                    case PartEventType.RoboticMotionStopped:
                        // Robotic state is "last sampled value", not on/off: the pose at the
                        // cut is whatever the newest event carried. `active` records whether
                        // the servo was still travelling, kept for the log only — the seed is
                        // emitted unconditionally (see AppendRoboticStateSeeds).
                        roboticStates[key] = BuildTransientState(
                            evt,
                            active: evt.eventType != PartEventType.RoboticMotionStopped,
                            value: evt.value,
                            seedEventType: PartEventType.RoboticMotionStopped);
                        break;
                }
            }

            int engineIgnitedSeeds = 0;
            int engineIdleSeeds = 0;
            int engineShutdownSeeds = 0;
            int rcsSeeds = 0;
            var engineKeys = new List<ulong>(engineStates.Keys);
            engineKeys.Sort();
            for (int i = 0; i < engineKeys.Count; i++)
            {
                var state = engineStates[engineKeys[i]];
                if (state.Active && state.Value > 0f)
                {
                    seeds.Add(new PartEvent
                    {
                        ut = splitUT,
                        partPersistentId = state.PartPersistentId,
                        eventType = PartEventType.EngineIgnited,
                        partName = state.PartName,
                        value = state.Value,
                        moduleIndex = state.ModuleIndex
                    });
                    engineIgnitedSeeds++;
                }
                else if (state.Active)
                {
                    seeds.Add(new PartEvent
                    {
                        ut = splitUT,
                        partPersistentId = state.PartPersistentId,
                        eventType = PartEventType.EngineThrottle,
                        partName = state.PartName,
                        value = 0f,
                        moduleIndex = state.ModuleIndex
                    });
                    engineIdleSeeds++;
                }
                else
                {
                    seeds.Add(new PartEvent
                    {
                        ut = splitUT,
                        partPersistentId = state.PartPersistentId,
                        eventType = PartEventType.EngineShutdown,
                        partName = state.PartName,
                        value = 0f,
                        moduleIndex = state.ModuleIndex
                    });
                    engineShutdownSeeds++;
                }
            }

            var rcsKeys = new List<ulong>(rcsStates.Keys);
            rcsKeys.Sort();
            for (int i = 0; i < rcsKeys.Count; i++)
            {
                var state = rcsStates[rcsKeys[i]];
                if (!state.Active) continue;

                seeds.Add(new PartEvent
                {
                    ut = splitUT,
                    partPersistentId = state.PartPersistentId,
                    eventType = PartEventType.RCSActivated,
                    partName = state.PartName,
                    value = state.Value,
                    moduleIndex = state.ModuleIndex
                });
                rcsSeeds++;
            }

            int visualStateSeeds = 0;
            int visualStateOffSeeds = 0;
            visualStateSeeds += AppendReversibleStateSeeds(
                deployableStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                lightPowerStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                lightBlinkStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                gearStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                cargoBayStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                parachuteStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                converterStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                evaJetpackStates, seeds, splitUT, ref visualStateOffSeeds);
            visualStateSeeds += AppendReversibleStateSeeds(
                evaRagdollStates, seeds, splitUT, ref visualStateOffSeeds);
            // Heat stays ACTIVE-ONLY on purpose. The inactive direction here is
            // ThermalAnimationCold, and cold is what the spawn pass already lays down
            // unconditionally (PopulateHeatInfos / step 3a of the loop-cycle re-apply) —
            // AND the M1 snapshot parser reads no heat key at all, so there is no stale
            // baseline for a cold seed to correct. Emitting one would be pure storage cost
            // per part per split for a state nothing can have got wrong.
            visualStateSeeds += AppendActiveStateSeeds(heatStates, seeds, splitUT);

            int roboticSeeds = AppendRoboticStateSeeds(roboticStates, seeds, splitUT);

            if (seeds.Count > 0)
                ParsekLog.Info("Optimizer",
                    $"Built {seeds.Count} transient state seed event(s) at UT={splitUT:F1} " +
                    $"(enginesOn={engineIgnitedSeeds} engineIdle={engineIdleSeeds} " +
                    $"engineSentinels={engineShutdownSeeds} rcsOn={rcsSeeds} " +
                    $"visualStates={visualStateSeeds} visualStatesOff={visualStateOffSeeds} " +
                    $"robotics={roboticSeeds})");

            return seeds;
        }

        private static void InsertTransientStateSeeds(
            List<PartEvent> seeds, List<PartEvent> secondHalf, double splitUT)
        {
            if (seeds == null || seeds.Count == 0) return;
            if (secondHalf == null) return;

            int inserted = 0;
            int skipped = 0;
            for (int i = 0; i < seeds.Count; i++)
            {
                var seed = seeds[i];
                if (HasBoundaryTransientEvent(secondHalf, seed, splitUT))
                {
                    skipped++;
                    continue;
                }

                secondHalf.Insert(inserted, seed);
                inserted++;
            }

            if (inserted > 0 || skipped > 0)
                ParsekLog.Info("Optimizer",
                    $"Inserted {inserted} transient state seed event(s) at split UT={splitUT:F1} " +
                    $"(skippedExistingBoundary={skipped})");
        }

        private static bool HasBoundaryTransientEvent(
            List<PartEvent> events, PartEvent seed, double splitUT)
        {
            if (events == null) return false;

            ulong seedKey = TransientVisualStateKey(seed);
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (Math.Abs(evt.ut - splitUT) > SplitSeedTimeEpsilon) continue;
                if (!IsTransientVisualStateEvent(evt.eventType)) continue;
                if (!IsSameTransientVisualStateFamily(seed.eventType, evt.eventType))
                    continue;

                ulong eventKey = TransientVisualStateKey(evt);
                if (eventKey == seedKey && BoundaryTransientEventCoversSeed(seed, evt))
                    return true;
            }

            return false;
        }

        private static bool BoundaryTransientEventCoversSeed(PartEvent seed, PartEvent boundaryEvent)
        {
            switch (seed.eventType)
            {
                case PartEventType.EngineIgnited:
                    return boundaryEvent.eventType == PartEventType.EngineIgnited
                        || (boundaryEvent.eventType == PartEventType.EngineThrottle && boundaryEvent.value > 0f);
                case PartEventType.EngineShutdown:
                    return boundaryEvent.eventType == PartEventType.EngineShutdown
                        // A zero-throttle boundary event is enough to suppress an
                        // inactive-engine sentinel: both keep playback power at zero
                        // and both arm the orphan auto-start guard for this engine key.
                        || (boundaryEvent.eventType == PartEventType.EngineThrottle && boundaryEvent.value <= 0f);
                case PartEventType.RCSActivated:
                    return boundaryEvent.eventType == PartEventType.RCSActivated
                        || (boundaryEvent.eventType == PartEventType.RCSThrottle && boundaryEvent.value > 0f);
                case PartEventType.RCSStopped:
                    return boundaryEvent.eventType == PartEventType.RCSStopped
                        || (boundaryEvent.eventType == PartEventType.RCSThrottle && boundaryEvent.value <= 0f);
                case PartEventType.LightBlinkEnabled:
                    return boundaryEvent.eventType == PartEventType.LightBlinkEnabled;
                case PartEventType.RoboticMotionStopped:
                    // Any robotic event at the same key on the boundary already carries a
                    // pose for this servo, so the seed would only re-apply the same value
                    // (RoboticMotionStarted / PositionSample also re-arm the motion the
                    // seed deliberately parks). All three cover the seed.
                    return IsRoboticVisualStateEvent(boundaryEvent.eventType);
                default:
                    return boundaryEvent.eventType == seed.eventType;
            }
        }

        private static bool IsTransientVisualStateEvent(PartEventType type)
        {
            return IsEngineVisualStateEvent(type)
                || IsRcsVisualStateEvent(type)
                || IsReversibleVisualStateEvent(type);
        }

        private static bool IsEngineVisualStateEvent(PartEventType type)
        {
            return type == PartEventType.EngineIgnited
                || type == PartEventType.EngineShutdown
                || type == PartEventType.EngineThrottle;
        }

        private static bool IsRcsVisualStateEvent(PartEventType type)
        {
            return type == PartEventType.RCSActivated
                || type == PartEventType.RCSStopped
                || type == PartEventType.RCSThrottle;
        }

        private static bool IsReversibleVisualStateEvent(PartEventType type)
        {
            switch (type)
            {
                case PartEventType.DeployableExtended:
                case PartEventType.DeployableRetracted:
                // S6: reversible because stock repair (eventRepairExternal -> DoRepair) takes a
                // BROKEN panel back to RETRACTED. NOT a permanent state event.
                case PartEventType.DeployableBroken:
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                case PartEventType.LightOn:
                case PartEventType.LightOff:
                case PartEventType.LightBlinkEnabled:
                case PartEventType.LightBlinkDisabled:
                case PartEventType.LightBlinkRate:
                case PartEventType.ThermalAnimationHot:
                case PartEventType.ThermalAnimationMedium:
                case PartEventType.ThermalAnimationCold:
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteDestroyed:
                case PartEventType.ParachuteRepacked:
                // Robotic servo poses are many-valued AND reversible, so they belong to the
                // latest-state-wins reducer, never to ForwardPermanentStateEvents (which
                // copies every matching event verbatim and would dump every position sample
                // of the head span at the cut).
                case PartEventType.RoboticMotionStarted:
                case PartEventType.RoboticPositionSample:
                case PartEventType.RoboticMotionStopped:
                // S7: the converter running loop is a plain reversible on/off pair - a player
                // toggle. The INACTIVE direction is not redundant: nothing else stops the loop, so
                // a tail whose head span ended with the drill switched off needs to be told, or the
                // tail's ghost would spin a drill the recording says is idle.
                case PartEventType.ConverterActivated:
                case PartEventType.ConverterDeactivated:
                // S4: the jetpack POSE and the ragdoll FLAG are both reversible two-state signals,
                // so both directions seed. The THRUST pair is deliberately absent - it follows the
                // RCS rule (active-direction only, and even that is momentary), so it is not a
                // transient VISUAL STATE a tail has to be told about.
                case PartEventType.EvaJetpackDeployed:
                case PartEventType.EvaJetpackStowed:
                case PartEventType.EvaRagdollStarted:
                case PartEventType.EvaRagdollEnded:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsRoboticVisualStateEvent(PartEventType type)
        {
            return type == PartEventType.RoboticMotionStarted
                || type == PartEventType.RoboticPositionSample
                || type == PartEventType.RoboticMotionStopped;
        }

        private static bool IsSameTransientVisualStateFamily(PartEventType a, PartEventType b)
        {
            return TransientVisualStateFamily(a) == TransientVisualStateFamily(b);
        }

        private static int TransientVisualStateFamily(PartEventType type)
        {
            if (IsEngineVisualStateEvent(type)) return 1;
            if (IsRcsVisualStateEvent(type)) return 2;

            switch (type)
            {
                case PartEventType.DeployableExtended:
                case PartEventType.DeployableRetracted:
                // S6: BROKEN joins the deployable family rather than getting its own, so the
                // reducer collapses a per-part run of extend/retract/break to its LAST state
                // and the boundary dedupe treats all three as one opinion about one pivot.
                case PartEventType.DeployableBroken:
                    return 3;
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                    return 4;
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                    return 5;
                case PartEventType.LightOn:
                case PartEventType.LightOff:
                    return 6;
                case PartEventType.LightBlinkEnabled:
                case PartEventType.LightBlinkDisabled:
                case PartEventType.LightBlinkRate:
                    return 7;
                case PartEventType.ThermalAnimationHot:
                case PartEventType.ThermalAnimationMedium:
                case PartEventType.ThermalAnimationCold:
                    return 8;
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteDestroyed:
                case PartEventType.ParachuteRepacked:
                    return 9;
                case PartEventType.RoboticMotionStarted:
                case PartEventType.RoboticPositionSample:
                case PartEventType.RoboticMotionStopped:
                    return 10;
                case PartEventType.ConverterActivated:
                case PartEventType.ConverterDeactivated:
                    return 11;
                case PartEventType.EvaJetpackDeployed:
                case PartEventType.EvaJetpackStowed:
                    return 12;
                case PartEventType.EvaRagdollStarted:
                case PartEventType.EvaRagdollEnded:
                    return 13;
                default:
                    return 0;
            }
        }

        private static ulong TransientVisualStateKey(PartEvent evt)
        {
            // Robotics are module-scoped like engines/RCS (one part can carry several
            // servos, each with its own robotic ordinal), not pid-collapsed like the
            // deployable / light / parachute families.
            if (IsEngineVisualStateEvent(evt.eventType)
                || IsRcsVisualStateEvent(evt.eventType)
                || IsRoboticVisualStateEvent(evt.eventType))
                return FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);

            return evt.partPersistentId;
        }

        private static void UpdateThrottleState(
            Dictionary<ulong, TransientPartState> states,
            ulong key,
            PartEvent evt,
            bool activeWhenUnseen)
        {
            TransientPartState state;
            if (!states.TryGetValue(key, out state))
                state = BuildTransientState(
                    evt, activeWhenUnseen, evt.value, stateKnown: false);
            else
            {
                state.PartPersistentId = evt.partPersistentId;
                state.ModuleIndex = evt.moduleIndex;
                state.PartName = evt.partName;
                state.Value = evt.value;
                if (!state.Active && evt.value > 0f)
                    state.Active = true;
            }

            states[key] = state;
        }

        private static void UpdateLightBlinkRateState(
            Dictionary<ulong, TransientPartState> states,
            ulong key,
            PartEvent evt)
        {
            TransientPartState state;
            if (!states.TryGetValue(key, out state))
                state = BuildTransientState(
                    evt, active: false, value: evt.value,
                    seedEventType: PartEventType.LightBlinkDisabled,
                    // A bare rate change says nothing about whether the lamp is blinking:
                    // the inactive emitter must not read this as "blink was off".
                    stateKnown: false);
            else
            {
                state.PartPersistentId = evt.partPersistentId;
                state.ModuleIndex = evt.moduleIndex;
                state.PartName = evt.partName;
                state.Value = evt.value;
                if (state.Active)
                    state.SeedEventType = PartEventType.LightBlinkEnabled;
            }

            states[key] = state;
        }

        /// <summary>
        /// ACTIVE-DIRECTION-ONLY emitter, kept for the ONE family whose inactive direction
        /// is genuinely redundant (heat — see the call site's comment). Every other
        /// reversible family goes through <see cref="AppendReversibleStateSeeds"/>.
        /// </summary>
        private static int AppendActiveStateSeeds(
            Dictionary<ulong, TransientPartState> states,
            List<PartEvent> seeds,
            double splitUT)
        {
            if (states == null || states.Count == 0) return 0;

            int added = 0;
            var keys = new List<ulong>(states.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                var state = states[keys[i]];
                if (!state.Active) continue;

                seeds.Add(new PartEvent
                {
                    ut = splitUT,
                    partPersistentId = state.PartPersistentId,
                    eventType = state.SeedEventType,
                    partName = state.PartName,
                    value = state.Value,
                    moduleIndex = state.ModuleIndex
                });
                added++;
            }

            return added;
        }

        /// <summary>
        /// Emits ONE seed per key in BOTH directions — the extension of the robotics rule
        /// (<see cref="AppendRoboticStateSeeds"/>) to the reversible on/off families.
        ///
        /// WHY THE INACTIVE DIRECTION IS NOT REDUNDANT. Pre-M1 it was: "inactive" meant the
        /// all-stowed prefab pose, which is exactly what the tail's ghost already spawned
        /// at, so a GearRetracted seed said nothing. Post-M1 the tail's ghost spawns at the
        /// pose its SNAPSHOT describes, and a split TIP's snapshot is a COPY of the parent's
        /// launch-time snapshot (<c>SplitAtSection</c> step 8). Gear down on the pad +
        /// GearRetracted during ascent + any routine env-boundary split then left the TIP
        /// rendering gear DOWN for its whole span, with no seed able to say otherwise — a
        /// regression against pre-M1 rather than a leftover of it.
        ///
        /// Two states deliberately get no inactive seed:
        /// <list type="bullet">
        /// <item><description><see cref="IsPermanentVisualStateEvent"/> types (only
        /// ParachuteDestroyed overlaps): <see cref="ForwardPermanentStateEvents"/> copies
        /// those verbatim, and it runs AFTER <c>InsertTransientStateSeeds</c>, so the
        /// boundary-dedupe cannot see the forwarded copy yet and we would emit a
        /// duplicate.</description></item>
        /// <item><description>States whose activeness was never OBSERVED
        /// (<c>StateKnown == false</c>): the light-blink reducer synthesises an inactive
        /// state from a bare LightBlinkRate event, and turning blink OFF off the back of a
        /// rate change would assert knowledge the head span never carried.</description></item>
        /// </list>
        ///
        /// Storage: one extra PartEvent per inactive family per part per split.
        /// </summary>
        private static int AppendReversibleStateSeeds(
            Dictionary<ulong, TransientPartState> states,
            List<PartEvent> seeds,
            double splitUT,
            ref int inactiveAdded)
        {
            if (states == null || states.Count == 0) return 0;

            int added = 0;
            var keys = new List<ulong>(states.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                var state = states[keys[i]];
                if (!state.Active)
                {
                    if (!state.StateKnown) continue;
                    if (IsPermanentVisualStateEvent(state.SeedEventType)) continue;
                }

                seeds.Add(new PartEvent
                {
                    ut = splitUT,
                    partPersistentId = state.PartPersistentId,
                    eventType = state.SeedEventType,
                    partName = state.PartName,
                    value = state.Value,
                    moduleIndex = state.ModuleIndex
                });
                added++;
                if (!state.Active)
                    inactiveAdded++;
            }

            return added;
        }

        /// <summary>
        /// Emits one <c>RoboticMotionStopped</c> seed per servo key, UNCONDITIONALLY —
        /// unlike <see cref="AppendActiveStateSeeds"/>, which only emits for `Active`
        /// states. A servo parked mid-stroke is not the default pose: without a seed the
        /// tail's ghost would render it at the prefab pose (gear up / arm folded) until the
        /// tail's own first robotic event fires, which on a rewind fork may be never.
        /// <c>RoboticMotionStopped</c> is the right carrier because
        /// <c>GhostPlaybackLogic.ApplyRoboticEvent</c> applies the pose and parks the
        /// motion; a still-travelling servo is re-armed by the tail's own next sample.
        /// </summary>
        private static int AppendRoboticStateSeeds(
            Dictionary<ulong, TransientPartState> states,
            List<PartEvent> seeds,
            double splitUT)
        {
            if (states == null || states.Count == 0) return 0;

            int added = 0;
            var keys = new List<ulong>(states.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                var state = states[keys[i]];
                seeds.Add(new PartEvent
                {
                    ut = splitUT,
                    partPersistentId = state.PartPersistentId,
                    eventType = PartEventType.RoboticMotionStopped,
                    partName = state.PartName,
                    value = state.Value,
                    moduleIndex = state.ModuleIndex
                });
                added++;
            }

            return added;
        }

        private static TransientPartState BuildTransientState(
            PartEvent evt,
            bool active,
            float value,
            PartEventType? seedEventType = null,
            bool stateKnown = true)
        {
            return new TransientPartState
            {
                PartPersistentId = evt.partPersistentId,
                ModuleIndex = evt.moduleIndex,
                PartName = evt.partName,
                Value = value,
                Active = active,
                StateKnown = stateKnown,
                SeedEventType = seedEventType.HasValue ? seedEventType.Value : evt.eventType
            };
        }

        private struct IndexedPartEvent
        {
            public PartEvent Event;
            public int OriginalIndex;
        }

        private struct TransientPartState
        {
            public uint PartPersistentId;
            public int ModuleIndex;
            public string PartName;
            public float Value;
            public bool Active;
            /// <summary>
            /// True when <see cref="Active"/> came from an event that actually STATES the
            /// on/off direction, false when it was synthesised by a value-only reducer
            /// (throttle / blink-rate) that saw no explicit state event for this key.
            /// <see cref="AppendReversibleStateSeeds"/> refuses to emit an inactive seed
            /// off an unobserved state; the engine / RCS loops ignore the flag entirely
            /// because they emit in both directions from their own explicit rules.
            /// </summary>
            public bool StateKnown;
            public PartEventType SeedEventType;
        }
    }
}
