using System.Collections.Generic;
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
            // Exclusion check: same logic as HasDedicatedAnimateHandler / CheckAnimateGenericState
            bool hasDeployablePart = p.FindModuleImplementing<ModuleDeployablePart>() != null;
            bool hasWheelDeploy = p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null;
            bool hasCargoBay = cargo != null;
            bool hasLadder = false, hasAnimGroup = false, hasAeroSurf = false;
            bool hasCtrlSurf = false, hasRobotArm = false, hasAnimHeat = false;
            for (int m = 0; m < p.Modules.Count; m++)
            {
                PartModule mod = p.Modules[m];
                if (mod == null) continue;
                if (string.Equals(mod.moduleName, "RetractableLadder", System.StringComparison.Ordinal)) hasLadder = true;
                if (string.Equals(mod.moduleName, "ModuleAnimationGroup", System.StringComparison.Ordinal)) hasAnimGroup = true;
                if (string.Equals(mod.moduleName, "ModuleAeroSurface", System.StringComparison.Ordinal)) hasAeroSurf = true;
                if (string.Equals(mod.moduleName, "ModuleControlSurface", System.StringComparison.Ordinal)) hasCtrlSurf = true;
                if (string.Equals(mod.moduleName, "ModuleRobotArmScanner", System.StringComparison.Ordinal)) hasRobotArm = true;
                if (string.Equals(mod.moduleName, "ModuleAnimateHeat", System.StringComparison.Ordinal)) hasAnimHeat = true;
            }
            if (hasDeployablePart || hasWheelDeploy || hasCargoBay ||
                hasLadder || hasAnimGroup || hasAeroSurf || hasCtrlSurf || hasRobotArm || hasAnimHeat)
            {
                return;
            }

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
