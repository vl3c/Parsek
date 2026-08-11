using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// One part's build-time module baseline, read out of its snapshot PART node.
    /// Every field is nullable / empty by default: "no opinion" means the existing
    /// spawn baseline (all-stowed prefab pose) stands, so a snapshot that carries none
    /// of these keys reproduces today's behaviour byte for byte.
    ///
    /// Precedence contract: this is a BUILD-TIME baseline only. It is applied at the
    /// tail of <c>GhostPlaybackLogic.PopulateGhostInfoDictionaries</c>, i.e. AFTER the
    /// existing stow/cold baselines and BEFORE the prefix replay in
    /// <c>ApplyPartEvents</c> — so any seed or recorded event at or before the playback
    /// cursor overrides it. A split TIP whose snapshot is launch-time-stale is therefore
    /// always corrected by the forwarded seeds, never fought by them.
    /// </summary>
    internal sealed class SnapshotPartBaseline
    {
        /// <summary>ModuleDeployablePart subclass — EXTENDED / RETRACTED; null for mid-travel or BROKEN.</summary>
        public bool? deployableExtended;
        /// <summary>ModuleWheelDeployment.stateString — Deployed / Retracted; null otherwise.</summary>
        public bool? gearDeployed;
        /// <summary>Cargo/service bay open state, resolved from the paired ModuleAnimateGeneric animTime against the prefab's closedPosition.</summary>
        public bool? cargoBayOpen;
        /// <summary>Standalone ModuleAnimateGeneric animTime endpoint (mid-travel → null). Only read when the part has no dedicated animate handler.</summary>
        public bool? animateGenericDeployed;
        /// <summary>ModuleAnimationGroup.isDeployed.</summary>
        public bool? animationGroupDeployed;
        /// <summary>ModuleParachute.persistentState, verbatim and upper-cased (STOWED / ACTIVE / SEMIDEPLOYED / DEPLOYED / CUT).</summary>
        public string parachutePersistentState;
        /// <summary>ModuleLight.isOn.</summary>
        public bool? lightOn;
        /// <summary>ModuleLight.isBlinking.</summary>
        public bool? lightBlinking;
        /// <summary>ModuleLight.blinkRate (only when strictly positive).</summary>
        public float? lightBlinkRate;
        /// <summary>ModuleColorChanger.animState — only read when the part carries no ModuleLight, mirroring PartStateSeeder.SeedLights.</summary>
        public bool? colorChangerOn;
        /// <summary>Per-servo poses in robotic-ordinal order. Null when the part carries no readable robotic pose.</summary>
        public List<SnapshotRoboticPose> roboticPoses;

        internal bool HasAnyBaseline =>
            deployableExtended.HasValue
            || gearDeployed.HasValue
            || cargoBayOpen.HasValue
            || animateGenericDeployed.HasValue
            || animationGroupDeployed.HasValue
            || !string.IsNullOrEmpty(parachutePersistentState)
            || lightOn.HasValue
            || lightBlinking.HasValue
            || lightBlinkRate.HasValue
            || colorChangerOn.HasValue
            || (roboticPoses != null && roboticPoses.Count > 0);
    }

    /// <summary>
    /// One servo's snapshot pose. <paramref name="ordinal"/> is the per-part ROBOTIC
    /// ordinal (only robotic-classified modules counted), which is the same key the
    /// recorder stamps on robotic PartEvents (<c>FlightRecorder.CacheRoboticModules</c>)
    /// and the builder assigns to <c>RoboticGhostInfo.moduleIndex</c>
    /// (<c>TryBuildRoboticInfos</c>). <paramref name="moduleName"/> is carried so the
    /// application site can verify both sides agree at that ordinal and degrade to
    /// no-baseline on a mod-set drift instead of posing the wrong servo.
    /// </summary>
    internal struct SnapshotRoboticPose
    {
        public int ordinal;
        public string moduleName;
        public float value;
    }

    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Parses the persisted module state of one snapshot PART node into a
        /// <see cref="SnapshotPartBaseline"/>. Pure method — ConfigNode in, POCO out, no
        /// Unity access, directly testable (mirrors the
        /// <see cref="TryParseCompoundPartData"/> pattern).
        ///
        /// Returns null when the node carries nothing readable, so the caller's
        /// dictionary only holds informative entries.
        ///
        /// <paramref name="cargoBayClosedPosition"/> and
        /// <paramref name="cargoDeployModuleIndex"/> come from the part PREFAB
        /// (<c>ModuleCargoBay.closedPosition</c> / <c>DeployModuleIndex</c>) because the
        /// animTime→open/closed mapping is per-part config, not snapshot data. Passing no
        /// closedPosition means "this part has no cargo bay", which is also what routes
        /// its ModuleAnimateGeneric to the standalone family.
        /// </summary>
        internal static SnapshotPartBaseline TryParseSnapshotPartBaseline(
            ConfigNode partNode,
            float? cargoBayClosedPosition = null,
            int cargoDeployModuleIndex = -1)
        {
            if (partNode == null)
                return null;

            ConfigNode[] modules = partNode.GetNodes("MODULE");
            if (modules == null || modules.Length == 0)
                return null;

            var baseline = new SnapshotPartBaseline();
            bool hasModuleLight = false;
            bool hasDedicatedAnimateHandler = cargoBayClosedPosition.HasValue;
            ConfigNode firstAnimateGeneric = null;
            ConfigNode cargoPairedAnimateGeneric = null;
            int roboticOrdinal = 0;

            for (int i = 0; i < modules.Length; i++)
            {
                ConfigNode module = modules[i];
                if (module == null) continue;
                string moduleName = module.GetValue("name");
                if (string.IsNullOrEmpty(moduleName)) continue;

                // --- Robotics: the ordinal advances for EVERY robotic-classified module,
                //     including the wheel families whose poses we deliberately do not read
                //     (continuous-motion, no meaningful persisted pose scalar). Skipping
                //     them silently would shift every later servo's ordinal off the
                //     recorder's numbering. ---
                if (FlightRecorder.IsRoboticModuleName(moduleName))
                {
                    int ordinal = roboticOrdinal;
                    roboticOrdinal++;
                    if (TryParseSnapshotRoboticPose(module, moduleName, out float pose))
                    {
                        if (baseline.roboticPoses == null)
                            baseline.roboticPoses = new List<SnapshotRoboticPose>();
                        baseline.roboticPoses.Add(new SnapshotRoboticPose
                        {
                            ordinal = ordinal,
                            moduleName = moduleName,
                            value = pose,
                        });
                    }
                    continue;
                }

                switch (moduleName)
                {
                    case "ModuleWheelDeployment":
                        hasDedicatedAnimateHandler = true;
                        if (!baseline.gearDeployed.HasValue)
                        {
                            FlightRecorder.ClassifyGearState(
                                module.GetValue("stateString"),
                                out bool gearDeployed, out bool gearRetracted);
                            if (gearDeployed || gearRetracted)
                                baseline.gearDeployed = gearDeployed;
                        }
                        continue;
                    case "ModuleParachute":
                        if (string.IsNullOrEmpty(baseline.parachutePersistentState))
                        {
                            string chuteState = module.GetValue("persistentState");
                            if (!string.IsNullOrEmpty(chuteState))
                                baseline.parachutePersistentState =
                                    chuteState.Trim().ToUpperInvariant();
                        }
                        continue;
                    case "ModuleLight":
                        hasModuleLight = true;
                        if (!baseline.lightOn.HasValue
                            && TryParseSnapshotBool(module.GetValue("isOn"), out bool isOn))
                            baseline.lightOn = isOn;
                        if (!baseline.lightBlinking.HasValue
                            && TryParseSnapshotBool(module.GetValue("isBlinking"), out bool isBlinking))
                            baseline.lightBlinking = isBlinking;
                        if (!baseline.lightBlinkRate.HasValue
                            && TryParseSnapshotFloat(module.GetValue("blinkRate"), out float blinkRate)
                            && blinkRate > 0f)
                            baseline.lightBlinkRate = blinkRate;
                        continue;
                    case "ModuleColorChanger":
                        if (!baseline.colorChangerOn.HasValue
                            && TryParseSnapshotBool(module.GetValue("animState"), out bool ccOn))
                            baseline.colorChangerOn = ccOn;
                        continue;
                    case "ModuleAnimationGroup":
                        hasDedicatedAnimateHandler = true;
                        if (!baseline.animationGroupDeployed.HasValue
                            && TryParseSnapshotBool(module.GetValue("isDeployed"), out bool agDeployed))
                            baseline.animationGroupDeployed = agDeployed;
                        continue;
                    case "ModuleAnimateGeneric":
                        if (firstAnimateGeneric == null)
                            firstAnimateGeneric = module;
                        if (i == cargoDeployModuleIndex)
                            cargoPairedAnimateGeneric = module;
                        continue;
                    // The dedicated-handler families whose own baselines are deferred
                    // (see the audit's dead-probe / unverified-key lists) still have to
                    // suppress the standalone ModuleAnimateGeneric read, exactly as
                    // FlightRecorder.HasDedicatedAnimateHandler does on the live side.
                    case "RetractableLadder":
                    case "ModuleAeroSurface":
                    case "ModuleControlSurface":
                    case "ModuleRobotArmScanner":
                    case "ModuleAnimateHeat":
                        hasDedicatedAnimateHandler = true;
                        continue;
                }

                // ModuleDeployablePart is abstract: the persisted node names are the
                // concrete subclasses (ModuleDeployableSolarPanel / Antenna / Radiator,
                // plus any mod subclass). Match on the KEY rather than the class name so
                // a differently-named subclass still resolves.
                if (!baseline.deployableExtended.HasValue)
                {
                    string deployState = module.GetValue("deployState");
                    if (!string.IsNullOrEmpty(deployState))
                    {
                        hasDedicatedAnimateHandler = true;
                        string trimmed = deployState.Trim();
                        if (string.Equals(trimmed, "EXTENDED", System.StringComparison.OrdinalIgnoreCase))
                            baseline.deployableExtended = true;
                        else if (string.Equals(trimmed, "RETRACTED", System.StringComparison.OrdinalIgnoreCase))
                            baseline.deployableExtended = false;
                        // EXTENDING / RETRACTING are mid-travel and BROKEN is its own
                        // (deferred) visual: no opinion, prefab stow stands.
                    }
                }
                else if (module.GetValue("deployState") != null)
                {
                    hasDedicatedAnimateHandler = true;
                }
            }

            // Cargo/service bay: the paired animation's animTime resolves open/closed
            // against the prefab's closedPosition (which end of the animation is "shut"
            // is per-part config).
            if (cargoBayClosedPosition.HasValue)
            {
                ConfigNode cargoAnim = cargoPairedAnimateGeneric ?? firstAnimateGeneric;
                if (cargoAnim != null
                    && TryParseSnapshotFloat(cargoAnim.GetValue("animTime"), out float cargoAnimTime))
                {
                    FlightRecorder.ClassifyCargoBayState(
                        cargoAnimTime, cargoBayClosedPosition.Value,
                        out bool isOpen, out bool isClosed);
                    if (isOpen || isClosed)
                        baseline.cargoBayOpen = isOpen;
                }
            }
            else if (!hasDedicatedAnimateHandler && firstAnimateGeneric != null
                && TryParseSnapshotFloat(firstAnimateGeneric.GetValue("animTime"), out float animTime))
            {
                FlightRecorder.ClassifyLadderState(animTime, out bool agExtended, out bool agRetracted);
                if (agExtended || agRetracted)
                    baseline.animateGenericDeployed = agExtended;
            }

            // Mirror PartStateSeeder.SeedLights: a ColorChanger only stands in for a
            // cabin light on parts that carry no ModuleLight, so the two never
            // double-count the same lamp.
            if (hasModuleLight)
                baseline.colorChangerOn = null;

            return baseline.HasAnyBaseline ? baseline : null;
        }

        /// <summary>
        /// Reads one servo's persisted pose scalar. The candidate field list comes from
        /// <see cref="FlightRecorder.ResolveRoboticFieldPlan"/> — the SAME plan the
        /// recorder reads live — because the value has to mean the same thing on both
        /// sides: <c>GhostPlaybackLogic.ApplyRoboticPose</c> applies it as an absolute
        /// offset from the build-time stowed pose, exactly as a recorded robotic event
        /// does. First present, finite key wins (mirroring the recorder's
        /// first-match-wins probe). A servo whose snapshot node carries none of them
        /// (e.g. only the vector/quaternion servoTransform fallbacks) degrades to
        /// no-baseline — today's behaviour — rather than guessing at units.
        /// </summary>
        private static bool TryParseSnapshotRoboticPose(
            ConfigNode moduleNode, string moduleName, out float pose)
        {
            pose = 0f;
            if (moduleNode == null)
                return false;

            // Wheel robotics (suspension / steering / motor) are continuous-motion
            // families with no meaningful persisted pose: deliberately no baseline.
            if (!FlightRecorder.IsRoboticModuleName(moduleName)
                || FlightRecorder.IsWheelRoboticModuleName(moduleName))
                return false;

            FlightRecorder.ResolveRoboticFieldPlan(moduleName, out _, out string[] fieldNames);
            if (fieldNames == null)
                return false;

            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (TryParseSnapshotFloat(moduleNode.GetValue(fieldNames[i]), out float value))
                {
                    pose = value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseSnapshotBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(raw))
                return false;
            return bool.TryParse(raw.Trim(), out value);
        }

        private static bool TryParseSnapshotFloat(string raw, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(raw))
                return false;
            if (!float.TryParse(
                    raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0f;
                return false;
            }
            return true;
        }
    }
}
