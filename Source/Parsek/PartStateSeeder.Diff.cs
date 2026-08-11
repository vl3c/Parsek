using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static partial class PartStateSeeder
    {
        /// <summary>
        /// Deep-copies a <see cref="PartTrackingSets"/> so it can be held across a rails span while
        /// the originals keep mutating. <see cref="BuildPartTrackingSetsFromState"/>-style bundles
        /// point AT a recorder's live collections; a shallow copy of one would track the live state
        /// and diff against itself.
        /// </summary>
        internal static PartTrackingSets ClonePartTrackingSets(PartTrackingSets source)
        {
            var clone = new PartTrackingSets();
            if (source == null) return clone;

            clone.deployedFairings = new HashSet<uint>(source.deployedFairings ?? new HashSet<uint>());
            clone.jettisonedShrouds = new HashSet<uint>(source.jettisonedShrouds ?? new HashSet<uint>());
            clone.parachuteStates = new Dictionary<uint, int>(source.parachuteStates ?? new Dictionary<uint, int>());
            clone.extendedDeployables = new HashSet<uint>(source.extendedDeployables ?? new HashSet<uint>());
            clone.brokenDeployables = new HashSet<uint>(source.brokenDeployables ?? new HashSet<uint>());
            clone.lightsOn = new HashSet<uint>(source.lightsOn ?? new HashSet<uint>());
            clone.blinkingLights = new HashSet<uint>(source.blinkingLights ?? new HashSet<uint>());
            clone.lightBlinkRates = new Dictionary<uint, float>(source.lightBlinkRates ?? new Dictionary<uint, float>());
            clone.deployedGear = new HashSet<uint>(source.deployedGear ?? new HashSet<uint>());
            clone.openCargoBays = new HashSet<uint>(source.openCargoBays ?? new HashSet<uint>());
            clone.deployedLadders = new HashSet<ulong>(source.deployedLadders ?? new HashSet<ulong>());
            clone.deployedAnimationGroups = new HashSet<ulong>(source.deployedAnimationGroups ?? new HashSet<ulong>());
            clone.deployedAnimateGenericModules = new HashSet<ulong>(source.deployedAnimateGenericModules ?? new HashSet<ulong>());
            clone.deployedAeroSurfaceModules = new HashSet<ulong>(source.deployedAeroSurfaceModules ?? new HashSet<ulong>());
            clone.deployedControlSurfaceModules = new HashSet<ulong>(source.deployedControlSurfaceModules ?? new HashSet<ulong>());
            clone.deployedRobotArmScannerModules = new HashSet<ulong>(source.deployedRobotArmScannerModules ?? new HashSet<ulong>());
            clone.animateHeatLevels = new Dictionary<ulong, HeatLevel>(source.animateHeatLevels ?? new Dictionary<ulong, HeatLevel>());
            clone.activeEngineKeys = new HashSet<ulong>(source.activeEngineKeys ?? new HashSet<ulong>());
            clone.lastThrottle = new Dictionary<ulong, float>(source.lastThrottle ?? new Dictionary<ulong, float>());
            clone.activeRcsKeys = new HashSet<ulong>(source.activeRcsKeys ?? new HashSet<ulong>());
            clone.lastRcsThrottle = new Dictionary<ulong, float>(source.lastRcsThrottle ?? new Dictionary<ulong, float>());
            clone.allEngineKeys = new HashSet<ulong>(source.allEngineKeys ?? new HashSet<ulong>());
            return clone;
        }

        /// <summary>
        /// Emits the PartEvents that reconcile <paramref name="before"/> to <paramref name="after"/>.
        ///
        /// <para>
        /// This is the M6 rails-span reconciler. A background vessel that packs onto rails has its
        /// <c>loadedStates</c> entry dropped; when it later unpacks, <c>SeedBackgroundPartStates</c>
        /// repopulates the tracking sets from live truth but <c>TrySeedLoadedPartEvents</c> declines
        /// to write anything because the recording already has events. Every state change across the
        /// span was therefore ERASED rather than deferred: the tracking sets silently agreed with
        /// reality again and the recording never learned about it. Diffing the pre-rails snapshot
        /// against the post-rails seed turns that erasure into the two or three events it should
        /// always have been.
        /// </para>
        /// <para>
        /// A family whose events are one-way (<see cref="PartEventType.ShroudJettisoned"/>,
        /// <see cref="PartEventType.FairingJettisoned"/>) only emits on ARRIVAL: a pid dropping out
        /// of those sets means the part is gone, which is a decouple/destroy the recorder covers
        /// through its own GameEvents, not an un-jettison.
        /// </para>
        /// <para>
        /// Pure: no Unity or KSP call beyond the already-captured tracking data, so the whole
        /// reconciler is directly testable.
        /// </para>
        /// </summary>
        /// <param name="before">Tracking state snapshotted at the rails transition (post terminal emit).</param>
        /// <param name="after">Tracking state freshly seeded from the live vessel on re-entry.</param>
        /// <param name="partNamesByPid">Map from part persistentId to part name for event logging.</param>
        /// <param name="ut">The UT to stamp on every emitted event.</param>
        /// <param name="logTag">Log subsystem tag.</param>
        /// <returns>The reconciling PartEvents (may be empty, never null).</returns>
        internal static List<PartEvent> EmitDiffEvents(
            PartTrackingSets before,
            PartTrackingSets after,
            Dictionary<uint, string> partNamesByPid,
            double ut,
            string logTag)
        {
            var events = new List<PartEvent>();
            if (before == null || after == null) return events;

            string NameFor(uint pid)
            {
                string n;
                return partNamesByPid != null && partNamesByPid.TryGetValue(pid, out n) ? n : "unknown";
            }

            void Emit(uint pid, int midx, PartEventType type, float value)
            {
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = type,
                    partName = NameFor(pid),
                    value = value,
                    moduleIndex = midx
                });
            }

            // --- pid-keyed boolean families ---

            void DiffUintSetWithDepartureCarveOut(
                HashSet<uint> beforeSet, HashSet<uint> afterSet,
                PartEventType onArrival, PartEventType? onDeparture,
                HashSet<uint> departureCarveOut)
            {
                if (afterSet != null)
                {
                    foreach (uint pid in afterSet)
                    {
                        if (beforeSet != null && beforeSet.Contains(pid)) continue;
                        Emit(pid, 0, onArrival, 0f);
                    }
                }

                if (onDeparture.HasValue && beforeSet != null)
                {
                    foreach (uint pid in beforeSet)
                    {
                        if (afterSet != null && afterSet.Contains(pid)) continue;
                        if (departureCarveOut != null && departureCarveOut.Contains(pid)) continue;
                        Emit(pid, 0, onDeparture.Value, 0f);
                    }
                }
            }

            void DiffUintSet(
                HashSet<uint> beforeSet, HashSet<uint> afterSet,
                PartEventType onArrival, PartEventType? onDeparture)
                => DiffUintSetWithDepartureCarveOut(
                    beforeSet, afterSet, onArrival, onDeparture, departureCarveOut: null);

            // S6: BROKEN takes PRECEDENCE over the extended/retracted diff, for exactly the
            // reason the live poll does (CheckDeployableBrokenTransition rule 1). A panel that
            // breaks across a rails span leaves extendedDeployables AND arrives in
            // brokenDeployables; running the plain extended diff would ALSO emit a
            // DeployableRetracted for it, and playback would animate the panel folding neatly
            // shut before hiding it. So the broken diff runs first and its arrivals are carved
            // out of the extended diff's DEPARTURE side.
            //
            // The ARRIVAL side deliberately has no carve-out: a pid that was broken before the
            // span and extended after it was repaired AND re-deployed, which is two real events
            // (DeployableRetracted from the broken departure here, then DeployableExtended
            // below) and both belong in the recording.
            DiffUintSet(before.brokenDeployables, after.brokenDeployables,
                PartEventType.DeployableBroken, PartEventType.DeployableRetracted);

            DiffUintSetWithDepartureCarveOut(
                before.extendedDeployables, after.extendedDeployables,
                PartEventType.DeployableExtended, PartEventType.DeployableRetracted,
                departureCarveOut: after.brokenDeployables);
            DiffUintSet(before.jettisonedShrouds, after.jettisonedShrouds,
                PartEventType.ShroudJettisoned, null);
            DiffUintSet(before.deployedFairings, after.deployedFairings,
                PartEventType.FairingJettisoned, null);
            DiffUintSet(before.lightsOn, after.lightsOn,
                PartEventType.LightOn, PartEventType.LightOff);
            DiffUintSet(before.deployedGear, after.deployedGear,
                PartEventType.GearDeployed, PartEventType.GearRetracted);
            DiffUintSet(before.openCargoBays, after.openCargoBays,
                PartEventType.CargoBayOpened, PartEventType.CargoBayClosed);

            // Blink is three-valued: enabled, disabled, and rate-changed-while-enabled.
            DiffBlinkingLights(before, after, Emit);

            // --- parachutes: routed through the same 4-state transition table the poll uses ---
            DiffParachutes(before, after, Emit);

            // --- module-keyed deployable families (all speak DeployableExtended/Retracted) ---

            void DiffUlongSet(HashSet<ulong> beforeSet, HashSet<ulong> afterSet)
            {
                if (afterSet != null)
                {
                    foreach (ulong key in afterSet)
                    {
                        if (beforeSet != null && beforeSet.Contains(key)) continue;
                        FlightRecorder.DecodeEngineKey(key, out uint pid, out int midx);
                        Emit(pid, midx, PartEventType.DeployableExtended, 0f);
                    }
                }

                if (beforeSet != null)
                {
                    foreach (ulong key in beforeSet)
                    {
                        if (afterSet != null && afterSet.Contains(key)) continue;
                        FlightRecorder.DecodeEngineKey(key, out uint pid, out int midx);
                        Emit(pid, midx, PartEventType.DeployableRetracted, 0f);
                    }
                }
            }

            DiffUlongSet(before.deployedLadders, after.deployedLadders);
            DiffUlongSet(before.deployedAnimationGroups, after.deployedAnimationGroups);
            DiffUlongSet(before.deployedAnimateGenericModules, after.deployedAnimateGenericModules);
            DiffUlongSet(before.deployedAeroSurfaceModules, after.deployedAeroSurfaceModules);
            DiffUlongSet(before.deployedControlSurfaceModules, after.deployedControlSurfaceModules);
            DiffUlongSet(before.deployedRobotArmScannerModules, after.deployedRobotArmScannerModules);

            DiffAnimateHeat(before, after, Emit);
            DiffEngines(before, after, Emit);
            DiffRcs(before, after, Emit);

            if (events.Count > 0)
            {
                ParsekLog.Info(logTag,
                    $"Rails-span reconcile: {events.Count} event(s) at " +
                    $"UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
                for (int i = 0; i < events.Count; i++)
                {
                    ParsekLog.Verbose(logTag,
                        $"Rails-span event: {events[i].eventType} pid={events[i].partPersistentId} " +
                        $"midx={events[i].moduleIndex} part='{events[i].partName}' " +
                        $"val={events[i].value.ToString("F2", CultureInfo.InvariantCulture)}");
                }
            }

            return events;
        }

        private static void DiffBlinkingLights(
            PartTrackingSets before, PartTrackingSets after,
            System.Action<uint, int, PartEventType, float> emit)
        {
            float RateFor(PartTrackingSets sets, uint pid)
            {
                float rate = 1f;
                if (sets.lightBlinkRates != null && sets.lightBlinkRates.TryGetValue(pid, out float found))
                    rate = found;
                return rate;
            }

            if (after.blinkingLights != null)
            {
                foreach (uint pid in after.blinkingLights)
                {
                    bool wasBlinking = before.blinkingLights != null && before.blinkingLights.Contains(pid);
                    if (!wasBlinking)
                    {
                        emit(pid, 0, PartEventType.LightBlinkEnabled, RateFor(after, pid));
                        continue;
                    }

                    float oldRate = RateFor(before, pid);
                    float newRate = RateFor(after, pid);
                    // Same epsilon the per-frame blink-rate check uses.
                    if (UnityEngine.Mathf.Abs(newRate - oldRate) > 0.01f)
                        emit(pid, 0, PartEventType.LightBlinkRate, newRate);
                }
            }

            if (before.blinkingLights != null)
            {
                foreach (uint pid in before.blinkingLights)
                {
                    if (after.blinkingLights != null && after.blinkingLights.Contains(pid)) continue;
                    emit(pid, 0, PartEventType.LightBlinkDisabled, 0f);
                }
            }
        }

        private static void DiffParachutes(
            PartTrackingSets before, PartTrackingSets after,
            System.Action<uint, int, PartEventType, float> emit)
        {
            // The maps only hold non-Stowed chutes: an absent pid reads as Stowed. See
            // FlightRecorder.ParachuteStateStowed.
            var seen = new HashSet<uint>();

            int StateOf(Dictionary<uint, int> map, uint pid)
            {
                if (map != null && map.TryGetValue(pid, out int state)) return state;
                return FlightRecorder.ParachuteStateStowed;
            }

            void Consider(uint pid)
            {
                if (!seen.Add(pid)) return;
                int oldState = StateOf(before.parachuteStates, pid);
                int newState = StateOf(after.parachuteStates, pid);
                PartEventType? type = FlightRecorder.ClassifyParachuteTransitionEvent(oldState, newState);
                if (type.HasValue)
                    emit(pid, 0, type.Value, 0f);
            }

            if (before.parachuteStates != null)
                foreach (var kvp in before.parachuteStates) Consider(kvp.Key);
            if (after.parachuteStates != null)
                foreach (var kvp in after.parachuteStates) Consider(kvp.Key);
        }

        private static void DiffAnimateHeat(
            PartTrackingSets before, PartTrackingSets after,
            System.Action<uint, int, PartEventType, float> emit)
        {
            // Absent from the map == Cold (EmitSeedEvents never seeds a Cold level).
            var seen = new HashSet<ulong>();

            HeatLevel LevelOf(Dictionary<ulong, HeatLevel> map, ulong key)
            {
                if (map != null && map.TryGetValue(key, out HeatLevel level)) return level;
                return HeatLevel.Cold;
            }

            void Consider(ulong key)
            {
                if (!seen.Add(key)) return;
                HeatLevel oldLevel = LevelOf(before.animateHeatLevels, key);
                HeatLevel newLevel = LevelOf(after.animateHeatLevels, key);
                if (oldLevel == newLevel) return;

                FlightRecorder.DecodeEngineKey(key, out uint pid, out int midx);
                PartEventType type;
                if (newLevel == HeatLevel.Hot) type = PartEventType.ThermalAnimationHot;
                else if (newLevel == HeatLevel.Medium) type = PartEventType.ThermalAnimationMedium;
                else type = PartEventType.ThermalAnimationCold;
                emit(pid, midx, type, 0f);
            }

            if (before.animateHeatLevels != null)
                foreach (var kvp in before.animateHeatLevels) Consider(kvp.Key);
            if (after.animateHeatLevels != null)
                foreach (var kvp in after.animateHeatLevels) Consider(kvp.Key);
        }

        private static void DiffEngines(
            PartTrackingSets before, PartTrackingSets after,
            System.Action<uint, int, PartEventType, float> emit)
        {
            float ThrottleOf(Dictionary<ulong, float> map, ulong key)
            {
                if (map != null && map.TryGetValue(key, out float t)) return t;
                return 0f;
            }

            if (after.activeEngineKeys != null)
            {
                foreach (ulong key in after.activeEngineKeys)
                {
                    FlightRecorder.DecodeEngineKey(key, out uint pid, out int midx);
                    float newThrottle = ThrottleOf(after.lastThrottle, key);
                    bool wasActive = before.activeEngineKeys != null && before.activeEngineKeys.Contains(key);

                    if (!wasActive)
                    {
                        // #165: an engine that is staged but idle gets no plume. The rails-entry
                        // terminal emit already left it shut down, so there is nothing to correct.
                        if (ShouldSkipZeroThrottleEngineSeed(newThrottle)) continue;
                        emit(pid, midx, PartEventType.EngineIgnited, newThrottle);
                        continue;
                    }

                    float oldThrottle = ThrottleOf(before.lastThrottle, key);
                    float delta = newThrottle - oldThrottle;
                    if (delta > FlightRecorder.EngineThrottleDeadband
                        || delta < -FlightRecorder.EngineThrottleDeadband)
                        emit(pid, midx, PartEventType.EngineThrottle, newThrottle);
                }
            }

            if (before.activeEngineKeys != null)
            {
                foreach (ulong key in before.activeEngineKeys)
                {
                    if (after.activeEngineKeys != null && after.activeEngineKeys.Contains(key)) continue;
                    FlightRecorder.DecodeEngineKey(key, out uint pid, out int midx);
                    emit(pid, midx, PartEventType.EngineShutdown, 0f);
                }
            }
        }

        private static void DiffRcs(
            PartTrackingSets before, PartTrackingSets after,
            System.Action<uint, int, PartEventType, float> emit)
        {
            if (after.activeRcsKeys != null)
            {
                foreach (ulong key in after.activeRcsKeys)
                {
                    if (before.activeRcsKeys != null && before.activeRcsKeys.Contains(key)) continue;
                    FlightRecorder.DecodeEngineKey(key, out uint pid, out int midx);
                    float power = 0f;
                    if (after.lastRcsThrottle != null)
                        after.lastRcsThrottle.TryGetValue(key, out power);
                    emit(pid, midx, PartEventType.RCSActivated, power);
                }
            }

            if (before.activeRcsKeys != null)
            {
                foreach (ulong key in before.activeRcsKeys)
                {
                    if (after.activeRcsKeys != null && after.activeRcsKeys.Contains(key)) continue;
                    FlightRecorder.DecodeEngineKey(key, out uint pid, out int midx);
                    emit(pid, midx, PartEventType.RCSStopped, 0f);
                }
            }
        }
    }
}
