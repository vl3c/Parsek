using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared static helpers for seeding part-event tracking sets with current
    /// part state. Eliminates duplication between FlightRecorder.SeedExistingPartStates
    /// and BackgroundRecorder.SeedBackgroundPartStates.
    /// </summary>
    internal static class PartStateSeeder
    {
        /// <summary>
        /// Pre-populates all part-event tracking sets with the current state of
        /// every part on the vessel. Prevents spurious events on the first
        /// physics-frame poll when recording starts mid-flight.
        /// </summary>
        /// <param name="v">The vessel to seed from.</param>
        /// <param name="sets">The tracking set collections to populate.</param>
        /// <param name="cachedEngines">Pre-cached engine module list (may be null).</param>
        /// <param name="cachedRcsModules">Pre-cached RCS module list (may be null).</param>
        /// <param name="seedColorChangerLights">If true, also seeds ColorChanger-based cabin lights
        /// (FlightRecorder polls these; BackgroundRecorder does not).</param>
        /// <param name="logTag">Log subsystem tag ("Recorder" or "BgRecorder").</param>
        internal static void SeedPartStates(
            Vessel v,
            PartTrackingSets sets,
            List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines,
            List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules,
            bool seedColorChangerLights,
            string logTag)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                SeedFairings(p, sets, logTag);
                SeedJettisons(p, sets, logTag);
                SeedParachutes(p, sets, logTag);
                SeedDeployables(p, sets, logTag);
                SeedLights(p, sets, seedColorChangerLights, logTag);
                SeedGear(p, sets, logTag);

                var cargo = p.FindModuleImplementing<ModuleCargoBay>();
                SeedCargoBays(p, cargo, sets, logTag);
                SeedLadders(p, sets, logTag);
                SeedAnimationGroupsAndSurfaces(p, sets, logTag);
                SeedAnimateGeneric(p, cargo, sets, logTag);
            }

            SeedEngines(cachedEngines, sets, logTag);
            SeedRcs(cachedRcsModules, sets, logTag);
            SeedAnimateHeat(v, sets, logTag);

            ParsekLog.Verbose(logTag,
                $"Initial state seeding complete: fairings={sets.deployedFairings.Count} shrouds={sets.jettisonedShrouds.Count} " +
                $"parachutes={sets.parachuteStates.Count} deployables={sets.extendedDeployables.Count} lights={sets.lightsOn.Count} " +
                $"blinks={sets.blinkingLights.Count} gear={sets.deployedGear.Count} cargo={sets.openCargoBays.Count} " +
                $"ladders={sets.deployedLadders.Count} animGroups={sets.deployedAnimationGroups.Count} " +
                $"animGeneric={sets.deployedAnimateGenericModules.Count} heat={sets.animateHeatLevels.Count} " +
                $"engines={sets.activeEngineKeys.Count} rcs={sets.activeRcsKeys.Count}");
        }

        private static void SeedFairings(Part p, PartTrackingSets sets, string logTag)
        {
            var fairing = p.FindModuleImplementing<ModuleProceduralFairing>();
            if (fairing == null) return;

            try
            {
                if (fairing.GetScalar >= 0.5f)
                {
                    sets.deployedFairings.Add(p.persistentId);
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-deployed fairing: '{p.partInfo?.name}' pid={p.persistentId}");
                }
            }
            catch { }
        }

        private static void SeedJettisons(Part p, PartTrackingSets sets, string logTag)
        {
            for (int m = 0; m < p.Modules.Count; m++)
            {
                var jettison = p.Modules[m] as ModuleJettison;
                if (jettison == null) continue;

                if (jettison.isJettisoned)
                {
                    sets.jettisonedShrouds.Add(p.persistentId);
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-jettisoned shroud: '{p.partInfo?.name}' pid={p.persistentId}");
                    break; // One jettison per part is enough for the set
                }
            }
        }

        private static void SeedParachutes(Part p, PartTrackingSets sets, string logTag)
        {
            var chute = p.FindModuleImplementing<ModuleParachute>();
            if (chute == null) return;

            int chuteState = chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED ? 2
                           : chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED ? 1
                           : 0;
            if (chuteState > 0)
            {
                sets.parachuteStates[p.persistentId] = chuteState;
                ParsekLog.Verbose(logTag,
                    $"Seeded already-deployed parachute: '{p.partInfo?.name}' pid={p.persistentId} state={chuteState}");
            }
        }

        private static void SeedDeployables(Part p, PartTrackingSets sets, string logTag)
        {
            var deployable = p.FindModuleImplementing<ModuleDeployablePart>();
            if (deployable == null) return;

            if (deployable.deployState == ModuleDeployablePart.DeployState.EXTENDED)
            {
                sets.extendedDeployables.Add(p.persistentId);
                ParsekLog.Verbose(logTag,
                    $"Seeded already-extended deployable: '{p.partInfo?.name}' pid={p.persistentId}");
            }
        }

        private static void SeedLights(Part p, PartTrackingSets sets,
            bool seedColorChangerLights, string logTag)
        {
            bool hasModuleLight = false;
            var light = p.FindModuleImplementing<ModuleLight>();
            if (light != null)
            {
                hasModuleLight = true;
                if (light.isOn)
                {
                    sets.lightsOn.Add(p.persistentId);
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-on light: '{p.partInfo?.name}' pid={p.persistentId}");
                }
                if (light.isBlinking)
                {
                    sets.blinkingLights.Add(p.persistentId);
                    float safeBlinkRate = light.blinkRate > 0f ? light.blinkRate : 1f;
                    sets.lightBlinkRates[p.persistentId] = safeBlinkRate;
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-blinking light: '{p.partInfo?.name}' pid={p.persistentId} rate={safeBlinkRate:F2}");
                }
            }

            // ColorChanger-based cabin lights (only if no ModuleLight to avoid double-counting)
            if (seedColorChangerLights && !hasModuleLight)
            {
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var cc = p.Modules[m] as ModuleColorChanger;
                    if (cc == null || !cc.toggleInFlight) continue;
                    if (cc.animState)
                    {
                        sets.lightsOn.Add(p.persistentId);
                        ParsekLog.Verbose(logTag,
                            $"Seeded already-on ColorChanger light: '{p.partInfo?.name}' pid={p.persistentId}");
                    }
                    break;
                }
            }
        }

        private static void SeedGear(Part p, PartTrackingSets sets, string logTag)
        {
            for (int m = 0; m < p.Modules.Count; m++)
            {
                var wheel = p.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                if (wheel == null) continue;
                FlightRecorder.ClassifyGearState(wheel.stateString, out bool isDeployed, out bool isRetracted);
                if (isDeployed)
                {
                    sets.deployedGear.Add(p.persistentId);
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-deployed gear: '{p.partInfo?.name}' pid={p.persistentId}");
                }
                break; // one deployment module per part
            }
        }

        private static void SeedCargoBays(Part p, ModuleCargoBay cargo,
            PartTrackingSets sets, string logTag)
        {
            if (cargo == null) return;

            int deployIdx = cargo.DeployModuleIndex;
            if (deployIdx >= 0 && deployIdx < p.Modules.Count)
            {
                var anim = p.Modules[deployIdx] as ModuleAnimateGeneric;
                if (anim != null)
                {
                    FlightRecorder.ClassifyCargoBayState(anim.animTime, cargo.closedPosition, out bool isOpen, out bool isClosed);
                    if (isOpen)
                    {
                        sets.openCargoBays.Add(p.persistentId);
                        ParsekLog.Verbose(logTag,
                            $"Seeded already-open cargo bay: '{p.partInfo?.name}' pid={p.persistentId}");
                    }
                }
            }
        }

        private static void SeedLadders(Part p, PartTrackingSets sets, string logTag)
        {
            for (int m = 0; m < p.Modules.Count; m++)
            {
                PartModule module = p.Modules[m];
                if (module == null) continue;
                if (!string.Equals(module.moduleName, "RetractableLadder", System.StringComparison.Ordinal))
                    continue;

                bool ladderDeployed, ladderRetracted;
                if (FlightRecorder.TryClassifyRetractableLadderState(module, out ladderDeployed, out ladderRetracted) && ladderDeployed)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    sets.deployedLadders.Add(key);
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-deployed ladder: '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                }
                break;
            }
        }

        private static void SeedAnimationGroupsAndSurfaces(Part p, PartTrackingSets sets, string logTag)
        {
            for (int m = 0; m < p.Modules.Count; m++)
            {
                PartModule module = p.Modules[m];
                if (module == null) continue;

                if (string.Equals(module.moduleName, "ModuleAnimationGroup", System.StringComparison.Ordinal))
                {
                    bool agDeployed, agRetracted;
                    if (FlightRecorder.TryClassifyAnimationGroupState(module, out agDeployed, out agRetracted) && agDeployed)
                    {
                        ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        sets.deployedAnimationGroups.Add(key);
                        ParsekLog.Verbose(logTag,
                            $"Seeded already-deployed animation group: '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                    }
                }
                else if (string.Equals(module.moduleName, "ModuleAeroSurface", System.StringComparison.Ordinal))
                {
                    bool asDeployed, asRetracted;
                    if (FlightRecorder.TryClassifyAeroSurfaceState(module, out asDeployed, out asRetracted) && asDeployed)
                    {
                        ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        sets.deployedAeroSurfaceModules.Add(key);
                        ParsekLog.Verbose(logTag,
                            $"Seeded already-deployed aero surface: '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                    }
                }
                else if (string.Equals(module.moduleName, "ModuleControlSurface", System.StringComparison.Ordinal))
                {
                    bool csDeployed, csRetracted;
                    if (FlightRecorder.TryClassifyControlSurfaceState(module, out csDeployed, out csRetracted) && csDeployed)
                    {
                        ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        sets.deployedControlSurfaceModules.Add(key);
                        ParsekLog.Verbose(logTag,
                            $"Seeded already-deployed control surface: '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                    }
                }
                else if (string.Equals(module.moduleName, "ModuleRobotArmScanner", System.StringComparison.Ordinal))
                {
                    bool rasDeployed, rasRetracted;
                    if (FlightRecorder.TryClassifyRobotArmScannerState(module, out rasDeployed, out rasRetracted) && rasDeployed)
                    {
                        ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        sets.deployedRobotArmScannerModules.Add(key);
                        ParsekLog.Verbose(logTag,
                            $"Seeded already-deployed robot arm scanner: '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                    }
                }
            }
        }

        /// <summary>
        /// Seeds AnimateGeneric modules, using the same exclusion logic as
        /// FlightRecorder.CheckAnimateGenericState / HasDedicatedAnimateHandler.
        /// Parts with dedicated handlers (deployables, gear, cargo bays, ladders,
        /// animation groups, aero/control surfaces, robot arm scanners, animate heat)
        /// are skipped to avoid duplicate state tracking.
        /// </summary>
        private static void SeedAnimateGeneric(Part p, ModuleCargoBay cargo,
            PartTrackingSets sets, string logTag)
        {
            // Delegate to FlightRecorder's canonical exclusion check (avoids duplication)
            if (FlightRecorder.HasDedicatedAnimateHandler(p)) return;

            for (int m = 0; m < p.Modules.Count; m++)
            {
                var animGeneric = p.Modules[m] as ModuleAnimateGeneric;
                if (animGeneric == null) continue;
                if (string.IsNullOrEmpty(animGeneric.animationName)) continue;

                bool agDeployed, agRetracted;
                if (FlightRecorder.TryClassifyAnimateGenericState(animGeneric, out agDeployed, out agRetracted) && agDeployed)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    sets.deployedAnimateGenericModules.Add(key);
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-deployed AnimateGeneric: '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                }
            }
        }

        private static void SeedEngines(
            List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines,
            PartTrackingSets sets, string logTag)
        {
            if (cachedEngines == null) return;

            for (int i = 0; i < cachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = cachedEngines[i];
                if (part == null || engine == null) continue;
                if (engine.EngineIgnited && engine.isOperational)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                    sets.activeEngineKeys.Add(key);
                    sets.lastThrottle[key] = engine.currentThrottle;
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-active engine: '{part.partInfo?.name}' pid={part.persistentId} midx={moduleIndex} throttle={engine.currentThrottle:F2}");
                }
            }
        }

        private static void SeedRcs(
            List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules,
            PartTrackingSets sets, string logTag)
        {
            if (cachedRcsModules == null) return;

            for (int i = 0; i < cachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = cachedRcsModules[i];
                if (part == null || rcs == null) continue;
                if (rcs.rcs_active && rcs.rcsEnabled)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                    sets.activeRcsKeys.Add(key);
                    float power = 0f;
                    if (rcs.thrusterPower > 0f && rcs.thrustForces != null && rcs.thrustForces.Length > 0)
                    {
                        float sum = 0f;
                        for (int f = 0; f < rcs.thrustForces.Length; f++)
                            sum += rcs.thrustForces[f];
                        power = Mathf.Clamp01(sum / (rcs.thrusterPower * rcs.thrustForces.Length));
                    }
                    sets.lastRcsThrottle[key] = power;
                    ParsekLog.Verbose(logTag,
                        $"Seeded already-active RCS: '{part.partInfo?.name}' pid={part.persistentId} midx={moduleIndex} power={power:F2}");
                }
            }
        }

        private static void SeedAnimateHeat(Vessel v, PartTrackingSets sets, string logTag)
        {
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAnimateHeat", System.StringComparison.Ordinal))
                        continue;
                    if (!FlightRecorder.TryClassifyAnimateHeatState(module, out float normalizedHeat, out _))
                        continue;
                    HeatLevel level;
                    if (normalizedHeat >= FlightRecorder.AnimateHeatHotThreshold)
                        level = HeatLevel.Hot;
                    else if (normalizedHeat >= FlightRecorder.AnimateHeatMediumThreshold)
                        level = HeatLevel.Medium;
                    else
                        level = HeatLevel.Cold;
                    if (level != HeatLevel.Cold)
                    {
                        ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        sets.animateHeatLevels[key] = level;
                        ParsekLog.Verbose(logTag,
                            $"Seeded AnimateHeat: '{p.partInfo?.name}' pid={p.persistentId} midx={m} level={level} heat={normalizedHeat:F3}");
                    }
                }
            }
        }

        /// <summary>
        /// Emits seed PartEvents for all non-default initial part states found in
        /// the tracking sets. These events are inserted at recording start so that
        /// ghost playback can reconstruct the correct visual state even when playback
        /// begins at the start of the recording (before any transition events).
        ///
        /// Pure static method — no Unity dependency beyond the data already captured
        /// in the tracking sets. Directly testable.
        /// </summary>
        /// <param name="sets">The populated tracking sets (after SeedPartStates).</param>
        /// <param name="partNamesByPid">Map from part persistentId to part name for event logging.</param>
        /// <param name="startUT">The UT to stamp on all seed events.</param>
        /// <param name="logTag">Log subsystem tag ("Recorder" or "BgRecorder").</param>
        /// <returns>List of seed PartEvents (may be empty, never null).</returns>
        internal static List<PartEvent> EmitSeedEvents(
            PartTrackingSets sets,
            Dictionary<uint, string> partNamesByPid,
            double startUT,
            string logTag)
        {
            var events = new List<PartEvent>();
            if (sets == null) return events;

            // Helper to look up part name by pid
            string NameFor(uint pid)
            {
                string n;
                return partNamesByPid != null && partNamesByPid.TryGetValue(pid, out n) ? n : "unknown";
            }

            // --- uint-keyed sets (pid only, moduleIndex = 0) ---

            // extendedDeployables → DeployableExtended
            if (sets.extendedDeployables != null)
            {
                foreach (uint pid in sets.extendedDeployables)
                {
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.DeployableExtended,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: DeployableExtended pid={pid} part='{NameFor(pid)}'");
                }
            }

            // jettisonedShrouds → ShroudJettisoned
            if (sets.jettisonedShrouds != null)
            {
                foreach (uint pid in sets.jettisonedShrouds)
                {
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.ShroudJettisoned,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: ShroudJettisoned pid={pid} part='{NameFor(pid)}'");
                }
            }

            // deployedFairings → FairingJettisoned
            if (sets.deployedFairings != null)
            {
                foreach (uint pid in sets.deployedFairings)
                {
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.FairingJettisoned,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: FairingJettisoned pid={pid} part='{NameFor(pid)}'");
                }
            }

            // lightsOn → LightOn
            if (sets.lightsOn != null)
            {
                foreach (uint pid in sets.lightsOn)
                {
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.LightOn,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: LightOn pid={pid} part='{NameFor(pid)}'");
                }
            }

            // blinkingLights → LightBlinkEnabled (value = blink rate from lightBlinkRates)
            if (sets.blinkingLights != null)
            {
                foreach (uint pid in sets.blinkingLights)
                {
                    float blinkRate = 1f;
                    if (sets.lightBlinkRates != null)
                        sets.lightBlinkRates.TryGetValue(pid, out blinkRate);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.LightBlinkEnabled,
                        partName = NameFor(pid),
                        value = blinkRate,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: LightBlinkEnabled pid={pid} part='{NameFor(pid)}' rate={blinkRate.ToString("F2", CultureInfo.InvariantCulture)}");
                }
            }

            // deployedGear → GearDeployed
            if (sets.deployedGear != null)
            {
                foreach (uint pid in sets.deployedGear)
                {
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.GearDeployed,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: GearDeployed pid={pid} part='{NameFor(pid)}'");
                }
            }

            // openCargoBays → CargoBayOpened
            if (sets.openCargoBays != null)
            {
                foreach (uint pid in sets.openCargoBays)
                {
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.CargoBayOpened,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: CargoBayOpened pid={pid} part='{NameFor(pid)}'");
                }
            }

            // parachuteStates → ParachuteSemiDeployed (value=1) or ParachuteDeployed (value=2)
            if (sets.parachuteStates != null)
            {
                foreach (var kvp in sets.parachuteStates)
                {
                    uint pid = kvp.Key;
                    int chuteState = kvp.Value;
                    PartEventType eventType;
                    if (chuteState == 2)
                        eventType = PartEventType.ParachuteDeployed;
                    else if (chuteState == 1)
                        eventType = PartEventType.ParachuteSemiDeployed;
                    else
                        continue; // state 0 = stowed, no seed event needed
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = eventType,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = 0
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: {eventType} pid={pid} part='{NameFor(pid)}'");
                }
            }

            // --- ulong-keyed sets (encoded pid+moduleIndex) ---

            // deployedLadders → DeployableExtended with decoded moduleIndex
            if (sets.deployedLadders != null)
            {
                foreach (ulong key in sets.deployedLadders)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.DeployableExtended,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: DeployableExtended (ladder) pid={pid} midx={midx} part='{NameFor(pid)}'");
                }
            }

            // deployedAnimationGroups → DeployableExtended with decoded moduleIndex
            if (sets.deployedAnimationGroups != null)
            {
                foreach (ulong key in sets.deployedAnimationGroups)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.DeployableExtended,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: DeployableExtended (animGroup) pid={pid} midx={midx} part='{NameFor(pid)}'");
                }
            }

            // deployedAnimateGenericModules → DeployableExtended with decoded moduleIndex
            if (sets.deployedAnimateGenericModules != null)
            {
                foreach (ulong key in sets.deployedAnimateGenericModules)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.DeployableExtended,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: DeployableExtended (animGeneric) pid={pid} midx={midx} part='{NameFor(pid)}'");
                }
            }

            // deployedAeroSurfaceModules → DeployableExtended with decoded moduleIndex
            if (sets.deployedAeroSurfaceModules != null)
            {
                foreach (ulong key in sets.deployedAeroSurfaceModules)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.DeployableExtended,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: DeployableExtended (aeroSurface) pid={pid} midx={midx} part='{NameFor(pid)}'");
                }
            }

            // deployedControlSurfaceModules → DeployableExtended with decoded moduleIndex
            if (sets.deployedControlSurfaceModules != null)
            {
                foreach (ulong key in sets.deployedControlSurfaceModules)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.DeployableExtended,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: DeployableExtended (controlSurface) pid={pid} midx={midx} part='{NameFor(pid)}'");
                }
            }

            // deployedRobotArmScannerModules → DeployableExtended with decoded moduleIndex
            if (sets.deployedRobotArmScannerModules != null)
            {
                foreach (ulong key in sets.deployedRobotArmScannerModules)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.DeployableExtended,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: DeployableExtended (robotArmScanner) pid={pid} midx={midx} part='{NameFor(pid)}'");
                }
            }

            // animateHeatLevels → ThermalAnimationHot or ThermalAnimationMedium
            if (sets.animateHeatLevels != null)
            {
                foreach (var kvp in sets.animateHeatLevels)
                {
                    ulong key = kvp.Key;
                    HeatLevel level = kvp.Value;
                    if (level == HeatLevel.Cold) continue; // no seed event for cold
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    PartEventType eventType = level == HeatLevel.Hot
                        ? PartEventType.ThermalAnimationHot
                        : PartEventType.ThermalAnimationMedium;
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = eventType,
                        partName = NameFor(pid),
                        value = 0f,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: {eventType} pid={pid} midx={midx} part='{NameFor(pid)}'");
                }
            }

            // activeEngineKeys → EngineIgnited (value = throttle from lastThrottle)
            if (sets.activeEngineKeys != null)
            {
                foreach (ulong key in sets.activeEngineKeys)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    float throttle = 0f;
                    if (sets.lastThrottle != null)
                        sets.lastThrottle.TryGetValue(key, out throttle);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.EngineIgnited,
                        partName = NameFor(pid),
                        value = throttle,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: EngineIgnited pid={pid} midx={midx} throttle={throttle.ToString("F2", CultureInfo.InvariantCulture)} part='{NameFor(pid)}'");
                }
            }

            // activeRcsKeys → RCSActivated (value = power from lastRcsThrottle)
            if (sets.activeRcsKeys != null)
            {
                foreach (ulong key in sets.activeRcsKeys)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(key, out pid, out midx);
                    float power = 0f;
                    if (sets.lastRcsThrottle != null)
                        sets.lastRcsThrottle.TryGetValue(key, out power);
                    events.Add(new PartEvent
                    {
                        ut = startUT,
                        partPersistentId = pid,
                        eventType = PartEventType.RCSActivated,
                        partName = NameFor(pid),
                        value = power,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag, $"Seed event: RCSActivated pid={pid} midx={midx} power={power.ToString("F2", CultureInfo.InvariantCulture)} part='{NameFor(pid)}'");
                }
            }

            ParsekLog.Info(logTag, $"Seed events emitted: {events.Count} events at UT={startUT.ToString("F2", CultureInfo.InvariantCulture)}");
            return events;
        }
    }

    /// <summary>
    /// Parameter object bundling all part-event tracking collections needed by
    /// PartStateSeeder.SeedPartStates. Both FlightRecorder (instance fields) and
    /// BackgroundRecorder (BackgroundVesselState fields) create one of these,
    /// pointing at their respective collections.
    /// </summary>
    internal class PartTrackingSets
    {
        public HashSet<uint> deployedFairings;
        public HashSet<uint> jettisonedShrouds;
        public Dictionary<uint, int> parachuteStates;
        public HashSet<uint> extendedDeployables;
        public HashSet<uint> lightsOn;
        public HashSet<uint> blinkingLights;
        public Dictionary<uint, float> lightBlinkRates;
        public HashSet<uint> deployedGear;
        public HashSet<uint> openCargoBays;
        public HashSet<ulong> deployedLadders;
        public HashSet<ulong> deployedAnimationGroups;
        public HashSet<ulong> deployedAnimateGenericModules;
        public HashSet<ulong> deployedAeroSurfaceModules;
        public HashSet<ulong> deployedControlSurfaceModules;
        public HashSet<ulong> deployedRobotArmScannerModules;
        public Dictionary<ulong, HeatLevel> animateHeatLevels;
        public HashSet<ulong> activeEngineKeys;
        public Dictionary<ulong, float> lastThrottle;
        public HashSet<ulong> activeRcsKeys;
        public Dictionary<ulong, float> lastRcsThrottle;
    }
}
