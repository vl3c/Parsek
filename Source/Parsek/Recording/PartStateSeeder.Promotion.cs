using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Promotion reconcile: the part states a recording taken over mid-flight does not know about.
    internal static partial class PartStateSeeder
    {
        /// <summary>
        /// Emits the part states a chain promotion finds changed with no recorder watching: each
        /// tracked state where what the recording's ghost would show and the live part differ.
        /// "Would show" is the recording's last word on that part and family, else the state its
        /// ghost spawns at (<see cref="BuildShownStates"/>), so nothing already recorded, in an
        /// event or in the start snapshot, is written again, and a state switched off, closed or
        /// repaired emits its own closing event.
        ///
        /// Engines, RCS, converters and the EVA jetpack / ragdoll are not handled here; the first
        /// and the last two have their own promotion seeds.
        ///
        /// The UT (<see cref="ResolvePromotionStateUT"/>) never moves
        /// <see cref="RecordingOptimizer.FindLastInterestingUT"/> past the activity already
        /// recorded (bug A / #263), so a do-nothing visit is still trimmed and auto-discarded.
        /// </summary>
        internal static int EmitPromotionStateEvents(
            PartTrackingSets live, List<PromotionPartFacts> facts, List<PartEvent> recordedEvents,
            List<PartEvent> events, double seedUT, double lastInterestingUT, double currentUT,
            string logTag)
        {
            if (live == null || facts == null || facts.Count == 0 || events == null)
                return 0;

            var names = new Dictionary<uint, string>();
            for (int i = 0; i < facts.Count; i++)
            {
                if (facts[i] != null && !names.ContainsKey(facts[i].pid))
                    names[facts[i].pid] = facts[i].partName ?? "unknown";
            }

            PartTrackingSets shown = BuildShownStates(live, facts, recordedEvents);
            List<PartEvent> diff = EmitDiffEvents(shown, live, names, seedUT, logTag, label: null);
            for (int i = 0; i < diff.Count; i++)
            {
                PartEvent evt = diff[i];
                evt.ut = ResolvePromotionStateUT(
                    seedUT, LastStateEventUT(recordedEvents, evt.partPersistentId),
                    lastInterestingUT, currentUT);
                events.Add(evt);
                ParsekLog.Verbose(logTag,
                    $"Seed event: {evt.eventType} pid={evt.partPersistentId} midx={evt.moduleIndex} " +
                    $"part='{evt.partName}' ut={evt.ut.ToString("F2", CultureInfo.InvariantCulture)} (promotion)");
            }
            return diff.Count;
        }

        /// <summary>
        /// The UT of one promotion state event. The recording's last interesting moment when that
        /// lies before the promotion: the latest UT that adds nothing to the tail trim, and at or
        /// after the part's own last event. Otherwise the part's last event, and with neither
        /// (a fresh recording) <paramref name="seedUT"/>, the recording's start.
        ///
        /// The latest safe UT, not the recording's start: the start would redraw the whole
        /// recorded flight with a state the part only took up afterwards.
        /// </summary>
        internal static double ResolvePromotionStateUT(
            double seedUT, double lastPartEventUT, double lastInterestingUT, double currentUT)
        {
            double ut = double.NaN;
            if (!double.IsNaN(lastInterestingUT) && lastInterestingUT < currentUT)
                ut = lastInterestingUT;
            if (!double.IsNaN(lastPartEventUT) && (double.IsNaN(ut) || lastPartEventUT > ut))
                ut = lastPartEventUT;
            return double.IsNaN(ut) ? seedUT : ut;
        }

        /// <summary>UT of the part's last state event in the recording, NaN when it has none.</summary>
        private static double LastStateEventUT(List<PartEvent> recordedEvents, uint pid)
        {
            double last = double.NaN;
            if (recordedEvents == null) return last;
            for (int i = 0; i < recordedEvents.Count; i++)
            {
                PartEvent evt = recordedEvents[i];
                if (evt.partPersistentId != pid || !IsPromotionStateEvent(evt.eventType)) continue;
                if (double.IsNaN(last) || evt.ut > last) last = evt.ut;
            }
            return last;
        }

        /// <summary>The event types <see cref="EmitPromotionStateEvents"/> reads and writes.</summary>
        internal static bool IsPromotionStateEvent(PartEventType type)
        {
            switch (type)
            {
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteRepacked:
                case PartEventType.ShroudJettisoned:
                case PartEventType.FairingJettisoned:
                case PartEventType.DeployableExtended:
                case PartEventType.DeployableRetracted:
                case PartEventType.DeployableBroken:
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                case PartEventType.WheelBroken:
                case PartEventType.WheelRepaired:
                case PartEventType.LightOn:
                case PartEventType.LightOff:
                case PartEventType.LightBlinkEnabled:
                case PartEventType.LightBlinkDisabled:
                case PartEventType.LightBlinkRate:
                case PartEventType.ThermalAnimationHot:
                case PartEventType.ThermalAnimationMedium:
                case PartEventType.ThermalAnimationCold:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The states a ghost of the recording shows at the promotion, over the parts in
        /// <paramref name="facts"/>: the spawn pose, then every recorded state event in UT order.
        /// Starts from a clone of the live sets, so a part or family with no known spawn pose and
        /// no recorded event reads as unchanged. Pure.
        /// </summary>
        internal static PartTrackingSets BuildShownStates(
            PartTrackingSets live, List<PromotionPartFacts> facts, List<PartEvent> recordedEvents)
        {
            PartTrackingSets shown = ClonePartTrackingSets(live);
            var byPid = new Dictionary<uint, PromotionPartFacts>();
            if (facts != null)
            {
                for (int i = 0; i < facts.Count; i++)
                {
                    PromotionPartFacts fact = facts[i];
                    if (fact == null || byPid.ContainsKey(fact.pid)) continue;
                    byPid[fact.pid] = fact;
                    ApplySpawnPose(shown, fact);
                }
            }

            if (recordedEvents != null && recordedEvents.Count > 0)
            {
                List<PartEvent> ordered = FlightRecorder.StableSortPartEventsByUT(recordedEvents);
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (byPid.TryGetValue(ordered[i].partPersistentId, out PromotionPartFacts fact))
                        ApplyRecordedState(shown, fact, ordered[i]);
                }
            }
            return shown;
        }

        private static void SetOn<T>(HashSet<T> set, T key, bool on)
        {
            if (on) set.Add(key);
            else set.Remove(key);
        }

        /// <summary>Puts one part's shown state at what its ghost spawns as.</summary>
        private static void ApplySpawnPose(PartTrackingSets shown, PromotionPartFacts fact)
        {
            if (fact.absentFromStartSnapshot) return;
            ApplySpawnPoseNoSnapshotKeyCarries(shown, fact);

            SnapshotPartBaseline spawn = fact.spawn;
            if (spawn == null) return;

            uint pid = fact.pid;
            if (fact.hasShroud) SetOn(shown.jettisonedShrouds, pid, fact.spawnShroudJettisoned);
            if (fact.hasFairing) SetOn(shown.deployedFairings, pid, fact.spawnFairingDeployed);
            if (fact.hasWheelDamage) SetOn(shown.damagedWheelParts, pid, spawn.wheelDamaged);
            if (fact.hasGear && spawn.gearDeployed.HasValue)
                SetOn(shown.deployedGear, pid, spawn.gearDeployed.Value);
            if (fact.hasTrackedCargoBay && spawn.cargoBayOpen.HasValue)
                SetOn(shown.openCargoBays, pid, spawn.cargoBayOpen.Value);
            ApplySpawnDeployable(shown, fact, spawn);
            ApplySpawnChute(shown, fact, spawn);
            ApplySpawnLight(shown, fact, spawn);

            SetModuleOn(shown.deployedLadders, pid, fact.ladderIndex, spawn.ladderExtended);
            SetModuleOn(shown.deployedAnimationGroups, pid, fact.animationGroupIndex, spawn.animationGroupDeployed);
            SetModuleOn(shown.deployedAnimateGenericModules, pid, fact.animateGenericIndex, spawn.animateGenericDeployed);
            if (fact.aeroSurfaceIndex >= 0)
                SetModuleOn(shown.deployedAeroSurfaceModules, pid, fact.aeroSurfaceIndex, spawn.surfaceDeployed);
            else
                SetModuleOn(shown.deployedControlSurfaceModules, pid, fact.controlSurfaceIndex, spawn.surfaceDeployed);
        }

        /// <summary>
        /// The states the snapshot reader reads nowhere (part heat, an EVA chute, a robot arm
        /// scanner): the ghost spawns them off whatever the vessel looked like.
        /// </summary>
        private static void ApplySpawnPoseNoSnapshotKeyCarries(PartTrackingSets shown, PromotionPartFacts fact)
        {
            uint pid = fact.pid;
            var heatKeys = new List<ulong>(shown.animateHeatLevels.Keys);
            for (int i = 0; i < heatKeys.Count; i++)
            {
                FlightRecorder.DecodeEngineKey(heatKeys[i], out uint heatPid, out _);
                if (heatPid == pid) shown.animateHeatLevels.Remove(heatKeys[i]);
            }
            if (fact.chuteOutsideSnapshotReader)
                shown.parachuteStates.Remove(pid);
            if (!fact.deployableOutsideSnapshotReader) return;
            shown.extendedDeployables.Remove(pid);
            shown.brokenDeployables.Remove(pid);
            foreach (KeyValuePair<int, PromotionModuleFamily> module in fact.moduleFamilies)
            {
                if (module.Value == PromotionModuleFamily.RobotArmScanner)
                    shown.deployedRobotArmScannerModules.Remove(
                        FlightRecorder.EncodeEngineKey(pid, module.Key));
            }
        }

        private static void SetModuleOn(HashSet<ulong> set, uint pid, int moduleIndex, bool? on)
        {
            if (moduleIndex < 0 || !on.HasValue) return;
            SetOn(set, FlightRecorder.EncodeEngineKey(pid, moduleIndex), on.Value);
        }

        private static void ApplySpawnDeployable(
            PartTrackingSets shown, PromotionPartFacts fact, SnapshotPartBaseline spawn)
        {
            if (!fact.hasDeployable || fact.deployableOutsideSnapshotReader) return;
            SetOn(shown.brokenDeployables, fact.pid, spawn.deployableBroken);
            if (!fact.hasPosedDeployable) return;
            if (spawn.deployableBroken)
                shown.extendedDeployables.Remove(fact.pid);
            else if (spawn.deployableExtended.HasValue)
                SetOn(shown.extendedDeployables, fact.pid, spawn.deployableExtended.Value);
        }

        private static void ApplySpawnChute(
            PartTrackingSets shown, PromotionPartFacts fact, SnapshotPartBaseline spawn)
        {
            if (!fact.hasChute || fact.chuteOutsideSnapshotReader
                || string.IsNullOrEmpty(spawn.parachutePersistentState))
                return;
            switch (spawn.parachutePersistentState)
            {
                case "SEMIDEPLOYED":
                    shown.parachuteStates[fact.pid] = FlightRecorder.ParachuteStateSemiDeployed;
                    break;
                case "DEPLOYED":
                    shown.parachuteStates[fact.pid] = FlightRecorder.ParachuteStateDeployed;
                    break;
                case "CUT":
                    shown.parachuteStates[fact.pid] = FlightRecorder.ParachuteStateCut;
                    break;
                default:
                    shown.parachuteStates.Remove(fact.pid);
                    break;
            }
        }

        private static void ApplySpawnLight(
            PartTrackingSets shown, PromotionPartFacts fact, SnapshotPartBaseline spawn)
        {
            if (!fact.hasSnapshotReadableLight) return;
            bool? on = spawn.lightOn ?? spawn.colorChangerOn;
            if (on.HasValue) SetOn(shown.lightsOn, fact.pid, on.Value);
            if (!spawn.lightBlinking.HasValue) return;
            SetOn(shown.blinkingLights, fact.pid, spawn.lightBlinking.Value);
            if (spawn.lightBlinking.Value && spawn.lightBlinkRate.HasValue)
                shown.lightBlinkRates[fact.pid] = spawn.lightBlinkRate.Value;
        }

        /// <summary>Applies one recorded state event to the shown states. Event types of other
        /// families are ignored.</summary>
        private static void ApplyRecordedState(
            PartTrackingSets shown, PromotionPartFacts fact, PartEvent evt)
        {
            uint pid = fact.pid;
            switch (evt.eventType)
            {
                case PartEventType.ParachuteSemiDeployed:
                    shown.parachuteStates[pid] = FlightRecorder.ParachuteStateSemiDeployed;
                    break;
                case PartEventType.ParachuteDeployed:
                    shown.parachuteStates[pid] = FlightRecorder.ParachuteStateDeployed;
                    break;
                case PartEventType.ParachuteCut:
                    shown.parachuteStates[pid] = FlightRecorder.ParachuteStateCut;
                    break;
                case PartEventType.ParachuteRepacked:
                    shown.parachuteStates.Remove(pid);
                    break;
                case PartEventType.ShroudJettisoned:
                    shown.jettisonedShrouds.Add(pid);
                    break;
                case PartEventType.FairingJettisoned:
                    shown.deployedFairings.Add(pid);
                    break;
                case PartEventType.DeployableExtended:
                    ApplyRecordedDeploy(shown, fact, evt.moduleIndex, true);
                    break;
                case PartEventType.DeployableRetracted:
                    ApplyRecordedDeploy(shown, fact, evt.moduleIndex, false);
                    break;
                case PartEventType.DeployableBroken:
                    shown.brokenDeployables.Add(pid);
                    shown.extendedDeployables.Remove(pid);
                    break;
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                    SetOn(shown.deployedGear, pid, evt.eventType == PartEventType.GearDeployed);
                    break;
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                    SetOn(shown.openCargoBays, pid, evt.eventType == PartEventType.CargoBayOpened);
                    break;
                case PartEventType.WheelBroken:
                case PartEventType.WheelRepaired:
                    SetOn(shown.damagedWheelParts, pid, evt.eventType == PartEventType.WheelBroken);
                    break;
                case PartEventType.LightOn:
                case PartEventType.LightOff:
                    SetOn(shown.lightsOn, pid, evt.eventType == PartEventType.LightOn);
                    break;
                case PartEventType.LightBlinkEnabled:
                    shown.blinkingLights.Add(pid);
                    shown.lightBlinkRates[pid] = evt.value;
                    break;
                case PartEventType.LightBlinkRate:
                    shown.lightBlinkRates[pid] = evt.value;
                    break;
                case PartEventType.LightBlinkDisabled:
                    shown.blinkingLights.Remove(pid);
                    break;
                case PartEventType.ThermalAnimationHot:
                case PartEventType.ThermalAnimationMedium:
                case PartEventType.ThermalAnimationCold:
                    shown.animateHeatLevels[FlightRecorder.EncodeEngineKey(pid, evt.moduleIndex)] =
                        RecordedHeatLevel(evt.eventType);
                    break;
            }
        }

        private static HeatLevel RecordedHeatLevel(PartEventType type)
        {
            if (type == PartEventType.ThermalAnimationHot) return HeatLevel.Hot;
            return type == PartEventType.ThermalAnimationMedium ? HeatLevel.Medium : HeatLevel.Cold;
        }

        /// <summary>
        /// DeployableExtended / Retracted is shared: a ModuleDeployablePart writes it at module
        /// index 0, the per-module families at their own index. The panel wins index 0 on a part
        /// that has both.
        /// </summary>
        private static void ApplyRecordedDeploy(
            PartTrackingSets shown, PromotionPartFacts fact, int moduleIndex, bool extended)
        {
            if (moduleIndex == 0 && fact.hasDeployable)
            {
                shown.brokenDeployables.Remove(fact.pid);
                SetOn(shown.extendedDeployables, fact.pid, extended);
                return;
            }
            if (!fact.moduleFamilies.TryGetValue(moduleIndex, out PromotionModuleFamily family))
                return;
            SetOn(ModuleSet(shown, family), FlightRecorder.EncodeEngineKey(fact.pid, moduleIndex), extended);
        }

        private static HashSet<ulong> ModuleSet(PartTrackingSets sets, PromotionModuleFamily family)
        {
            switch (family)
            {
                case PromotionModuleFamily.Ladder: return sets.deployedLadders;
                case PromotionModuleFamily.AnimationGroup: return sets.deployedAnimationGroups;
                case PromotionModuleFamily.AnimateGeneric: return sets.deployedAnimateGenericModules;
                case PromotionModuleFamily.AeroSurface: return sets.deployedAeroSurfaceModules;
                case PromotionModuleFamily.ControlSurface: return sets.deployedControlSurfaceModules;
                default: return sets.deployedRobotArmScannerModules;
            }
        }

        // ---- reading the live vessel and the recording's start snapshot ----

        /// <summary>
        /// Reads every part of the vessel being promoted, paired with its PART node in the
        /// recording's start ghost snapshot (null = none captured yet).
        /// </summary>
        internal static List<PromotionPartFacts> CollectPromotionPartFacts(
            Vessel v, ConfigNode startGhostSnapshot)
        {
            var facts = new List<PromotionPartFacts>();
            if (v == null || v.parts == null) return facts;

            Dictionary<uint, ConfigNode> nodes = IndexSnapshotParts(startGhostSnapshot);
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                nodes.TryGetValue(p.persistentId, out ConfigNode node);
                PromotionPartFacts fact = BuildPromotionPartFacts(p, node);
                fact.absentFromStartSnapshot = startGhostSnapshot != null && node == null;
                facts.Add(fact);
            }
            return facts;
        }

        /// <summary>The snapshot's PART nodes by persistentId (pid 0 and duplicates skipped). Pure.</summary>
        internal static Dictionary<uint, ConfigNode> IndexSnapshotParts(ConfigNode snapshot)
        {
            var nodes = new Dictionary<uint, ConfigNode>();
            ConfigNode[] parts = snapshot?.GetNodes("PART");
            if (parts == null) return nodes;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != null
                    && uint.TryParse(parts[i].GetValue("persistentId"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out uint pid)
                    && pid != 0 && !nodes.ContainsKey(pid))
                    nodes[pid] = parts[i];
            }
            return nodes;
        }

        /// <summary>
        /// Pure: the shroud and fairing state a snapshot PART node persists, read the way the
        /// ghost builder reads them: any ModuleJettison with isJettisoned or stock's
        /// shroudHideOverride (which OnStart folds into it), and a ModuleProceduralFairing whose
        /// fsm is st_flight_deployed.
        /// </summary>
        internal static void ReadSnapshotShroudAndFairing(
            ConfigNode partNode, out bool shroudJettisoned, out bool fairingDeployed)
        {
            shroudJettisoned = false;
            fairingDeployed = false;
            ConfigNode[] modules = partNode?.GetNodes("MODULE");
            if (modules == null) return;
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i] == null) continue;
                string name = modules[i].GetValue("name");
                if (name == "ModuleJettison")
                    shroudJettisoned |= IsTrue(modules[i].GetValue("isJettisoned"))
                        || IsTrue(modules[i].GetValue("shroudHideOverride"));
                else if (name == "ModuleProceduralFairing")
                    fairingDeployed |= modules[i].GetValue("fsm") == "st_flight_deployed";
            }

            bool IsTrue(string raw) => raw != null && bool.TryParse(raw.Trim(), out bool value) && value;
        }

        private static PromotionPartFacts BuildPromotionPartFacts(Part p, ConfigNode node)
        {
            var chute = p.FindModuleImplementing<ModuleParachute>();
            var deployable = p.FindModuleImplementing<ModuleDeployablePart>();
            var fact = new PromotionPartFacts
            {
                pid = p.persistentId,
                partName = p.partInfo?.name ?? "unknown",
                hasChute = chute != null,
                chuteOutsideSnapshotReader = chute != null && chute.moduleName != "ModuleParachute",
                hasDeployable = deployable != null,
                hasPosedDeployable = deployable != null
                    && !DeployableHasNoPoseAnimation(deployable.animationName),
                deployableOutsideSnapshotReader =
                    deployable != null && deployable.moduleName == "ModuleRobotArmScanner",
                hasGear = p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null,
                hasWheelDamage = p.FindModuleImplementing<ModuleWheels.ModuleWheelDamage>() != null,
                hasShroud = p.FindModuleImplementing<ModuleJettison>() != null,
                hasFairing = p.FindModuleImplementing<ModuleProceduralFairing>() != null,
                hasSnapshotReadableLight = HasSnapshotReadableLight(p),
            };
            CollectModuleFamilies(p, fact);
            if (node != null) ReadSpawnPose(p, node, fact);
            return fact;
        }

        private static void ReadSpawnPose(Part p, ConfigNode node, PromotionPartFacts fact)
        {
            float? cargoClosedPosition = null;
            int cargoDeployModuleIndex = -1;
            ModuleAnimateGeneric cargoAnim = null;
            var cargo = p.FindModuleImplementing<ModuleCargoBay>();
            if (cargo != null)
            {
                cargoClosedPosition = cargo.closedPosition;
                cargoDeployModuleIndex = cargo.DeployModuleIndex;
                if (cargoDeployModuleIndex >= 0 && cargoDeployModuleIndex < p.Modules.Count)
                    cargoAnim = p.Modules[cargoDeployModuleIndex] as ModuleAnimateGeneric;
                fact.hasTrackedCargoBay = cargoAnim != null;
            }
            fact.spawn = TryParseSnapshotPartBaseline(
                node, cargoClosedPosition, cargoDeployModuleIndex,
                cargoAnim != null && cargoAnim.allowDeployLimit,
                cargoAnim != null && cargoAnim.revClampPercent) ?? new SnapshotPartBaseline();
            ReadSnapshotShroudAndFairing(
                node, out fact.spawnShroudJettisoned, out fact.spawnFairingDeployed);
        }

        /// <summary>
        /// Whether the seeder and the snapshot reader read the same lamp: a ModuleLight by its
        /// exact name, else the part's FIRST ModuleColorChanger when the seeder tracks it
        /// (toggleInFlight).
        /// </summary>
        private static bool HasSnapshotReadableLight(Part p)
        {
            var light = p.FindModuleImplementing<ModuleLight>();
            if (light != null) return light.moduleName == "ModuleLight";
            var colorChanger = p.FindModuleImplementing<ModuleColorChanger>();
            return colorChanger != null && colorChanger.moduleName == "ModuleColorChanger"
                && colorChanger.toggleInFlight;
        }

        private static void CollectModuleFamilies(Part p, PromotionPartFacts fact)
        {
            bool dedicated = FlightRecorder.HasDedicatedAnimateHandler(p);
            var seen = new HashSet<PromotionModuleFamily>();
            for (int m = 0; m < p.Modules.Count; m++)
            {
                PartModule module = p.Modules[m];
                if (module == null || !TryResolveModuleFamily(module, dedicated, out PromotionModuleFamily family))
                    continue;

                fact.moduleFamilies[m] = family;
                // The snapshot reader takes the part's first module of the family.
                if (!seen.Add(family) || !IsLiveStateDefinite(module, family)) continue;
                switch (family)
                {
                    case PromotionModuleFamily.Ladder: fact.ladderIndex = m; break;
                    case PromotionModuleFamily.AnimationGroup: fact.animationGroupIndex = m; break;
                    case PromotionModuleFamily.AnimateGeneric: fact.animateGenericIndex = m; break;
                    case PromotionModuleFamily.AeroSurface: fact.aeroSurfaceIndex = m; break;
                    case PromotionModuleFamily.ControlSurface: fact.controlSurfaceIndex = m; break;
                }
            }
            // The reader's one surface opinion is the aero surface's when the part has one.
            if (seen.Contains(PromotionModuleFamily.AeroSurface)) fact.controlSurfaceIndex = -1;
        }

        private static bool TryResolveModuleFamily(
            PartModule module, bool hasDedicatedAnimateHandler, out PromotionModuleFamily family)
        {
            family = PromotionModuleFamily.AnimateGeneric;
            switch (module.moduleName)
            {
                case "RetractableLadder": family = PromotionModuleFamily.Ladder; return true;
                case "ModuleAnimationGroup": family = PromotionModuleFamily.AnimationGroup; return true;
                case "ModuleAeroSurface": family = PromotionModuleFamily.AeroSurface; return true;
                case "ModuleControlSurface": family = PromotionModuleFamily.ControlSurface; return true;
                case "ModuleRobotArmScanner": family = PromotionModuleFamily.RobotArmScanner; return true;
            }
            var generic = hasDedicatedAnimateHandler ? null : module as ModuleAnimateGeneric;
            return generic != null && !string.IsNullOrEmpty(generic.animationName);
        }

        private static bool IsLiveStateDefinite(PartModule module, PromotionModuleFamily family)
        {
            switch (family)
            {
                case PromotionModuleFamily.Ladder:
                    return FlightRecorder.TryClassifyRetractableLadderState(module, out _, out _);
                case PromotionModuleFamily.AnimationGroup:
                    return FlightRecorder.TryClassifyAnimationGroupState(module, out _, out _);
                case PromotionModuleFamily.AeroSurface:
                    return FlightRecorder.TryClassifyAeroSurfaceState(module, out _, out _);
                case PromotionModuleFamily.ControlSurface:
                    return FlightRecorder.TryClassifyControlSurfaceState(module, out _, out _);
                case PromotionModuleFamily.AnimateGeneric:
                    return module.moduleName == "ModuleAnimateGeneric"
                        && FlightRecorder.TryClassifyAnimateGenericState(
                            module as ModuleAnimateGeneric, out _, out _);
                default:
                    return false;
            }
        }
    }
}
