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
                        parachuteStates[key] = BuildTransientState(
                            evt, active: false, value: 0f, seedEventType: evt.eventType);
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
            visualStateSeeds += AppendActiveStateSeeds(deployableStates, seeds, splitUT);
            visualStateSeeds += AppendActiveStateSeeds(lightPowerStates, seeds, splitUT);
            visualStateSeeds += AppendActiveStateSeeds(lightBlinkStates, seeds, splitUT);
            visualStateSeeds += AppendActiveStateSeeds(gearStates, seeds, splitUT);
            visualStateSeeds += AppendActiveStateSeeds(cargoBayStates, seeds, splitUT);
            visualStateSeeds += AppendActiveStateSeeds(parachuteStates, seeds, splitUT);
            visualStateSeeds += AppendActiveStateSeeds(heatStates, seeds, splitUT);

            if (seeds.Count > 0)
                ParsekLog.Info("Optimizer",
                    $"Built {seeds.Count} transient state seed event(s) at UT={splitUT:F1} " +
                    $"(enginesOn={engineIgnitedSeeds} engineIdle={engineIdleSeeds} " +
                    $"engineSentinels={engineShutdownSeeds} rcsOn={rcsSeeds} " +
                    $"visualStates={visualStateSeeds})");

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
                    return true;
                default:
                    return false;
            }
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
                    return 9;
                default:
                    return 0;
            }
        }

        private static ulong TransientVisualStateKey(PartEvent evt)
        {
            if (IsEngineVisualStateEvent(evt.eventType) || IsRcsVisualStateEvent(evt.eventType))
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
                state = BuildTransientState(evt, activeWhenUnseen, evt.value);
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
                    evt, active: false, value: evt.value, seedEventType: PartEventType.LightBlinkDisabled);
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

        private static TransientPartState BuildTransientState(
            PartEvent evt,
            bool active,
            float value,
            PartEventType? seedEventType = null)
        {
            return new TransientPartState
            {
                PartPersistentId = evt.partPersistentId,
                ModuleIndex = evt.moduleIndex,
                PartName = evt.partName,
                Value = value,
                Active = active,
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
            public PartEventType SeedEventType;
        }
    }
}
