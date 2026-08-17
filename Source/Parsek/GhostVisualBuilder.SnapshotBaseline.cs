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
    /// cursor overrides it.
    ///
    /// HOW FAR THAT ACTUALLY GOES on a split TIP, whose snapshot is a COPY of the
    /// parent's launch-time one (<c>RecordingOptimizer.SplitAtSection</c> step 8) and so
    /// stale by construction. A seed exists only for a family the HEAD SPAN emitted at
    /// least one event for. For those, <c>BuildTransientStateSeeds</c> now seeds BOTH
    /// directions of the reversible families (<c>AppendReversibleStateSeeds</c>), so a
    /// stale baseline is corrected either way — that inactive direction was added
    /// precisely because it is not redundant post-M1. For a family the head span emitted
    /// NOTHING for there is no seed and the stale baseline stands; that is normally right,
    /// because "no event" means the state did not change during the head span and the
    /// launch-time value is still the value at the cut. It is wrong only where the
    /// recorder cannot SEE a change at all (the dead-probe families: ladders, aero /
    /// control surfaces, robot-arm scanners), which is the same blind spot pre-M1 had.
    /// </summary>
    internal sealed class SnapshotPartBaseline
    {
        /// <summary>ModuleDeployablePart subclass — EXTENDED / RETRACTED; null for mid-travel or BROKEN.</summary>
        public bool? deployableExtended;
        /// <summary>
        /// S6: the part's ModuleDeployablePart persisted as BROKEN, so the ghost must spawn with
        /// its break subtree hidden. Deliberately a plain bool rather than a nullable: there is no
        /// "snapshot says NOT broken" opinion to carry — an intact panel is the prefab default and
        /// <see cref="deployableExtended"/> already covers its pose. Mutually exclusive with a
        /// deployableExtended value: broken is not a point on the stowed&lt;-&gt;deployed axis.
        /// </summary>
        public bool deployableBroken;
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
            || deployableBroken
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
                if (!baseline.deployableExtended.HasValue && !baseline.deployableBroken)
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
                        else if (string.Equals(trimmed, "BROKEN", System.StringComparison.OrdinalIgnoreCase))
                        {
                            // S6 fills the slot this comment used to defer. A ghost spawning from
                            // a snapshot whose panel was already broken must spawn with the break
                            // subtree HIDDEN — the prefab pose is intact, and before P8 nothing
                            // corrected it, so a station recorded after losing a panel replayed
                            // with the panel back.
                            //
                            // deployableExtended is deliberately left with NO value: broken is not
                            // a point on the stowed<->deployed axis, and giving it one would make
                            // the resolver pose the panel (folded or open) as well as hiding it.
                            baseline.deployableBroken = true;
                        }
                        // EXTENDING / RETRACTING remain mid-travel: no opinion, prefab stow stands.
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
        /// The SNAPSHOT-side robotic pose key, per family. Deliberately NOT
        /// <see cref="FlightRecorder.ResolveRoboticFieldPlan"/>: that is the LIVE probe
        /// plan, which reads a running <c>PartModule</c> through <c>module.Fields</c> and
        /// so can see every <c>[KSPField]</c>. A snapshot MODULE node carries only the
        /// <c>isPersistant = true</c> subset, and for all three posed families the live
        /// plan's primary key is NOT in it — so reusing the live plan silently fell through
        /// to whatever persisted key happened to sit further down the list, which is a
        /// SETTING rather than a pose.
        ///
        /// Verified by decompiling Assembly-CSharp (KSP 1.12.5,
        /// <c>Expansions.Serenity.*</c>) and against the stock Breaking Ground craft files:
        /// <list type="bullet">
        /// <item><description>Hinge / rotation servo — <c>currentAngle</c> is a plain
        /// <c>[KSPField]</c> (NOT persisted); <c>targetAngle</c> is
        /// <c>[KSPAxisField(isPersistant = true)]</c> and <c>KSPAxisField : KSPField</c>, so
        /// it does persist. Val-Thopter.craft's hinge node carries
        /// <c>targetAngle = -45</c> and no <c>currentAngle</c>. Both are degrees from the
        /// servo's zero, which is the unit <c>ApplyRoboticPose</c> wants.</description></item>
        /// <item><description>Piston — the live plan is
        /// <c>currentPosition / position / targetPosition / traverseVelocity</c> and
        /// <c>ModuleRoboticServoPiston</c> declares NONE of the first three publicly
        /// (its fields are <c>currentExtension</c>, non-persisted, and <c>targetExtension</c>,
        /// persisted; <c>targetPosition</c> exists but is a private non-KSPField). The only
        /// persisted plan key was <c>traverseVelocity</c> — a SPEED in m/s, default 1 — so
        /// every piston posed to ~1 m. <c>targetExtension</c> is metres of extension
        /// (<c>targetPosition = targetExtension</c>, and <c>currentExtension</c> is measured
        /// along the same axis), which is what the Linear applier wants.</description></item>
        /// <item><description>Rotor — NO KEY. <c>currentRPM</c> is non-persisted;
        /// <c>rpmLimit</c> is persisted with a 230 default, and every stock Breaking Ground
        /// helicopter craft ships it at 460. Reading it made every rotor ghost spin at its
        /// LIMIT from spawn and re-arm on every loop cycle, parked or not. No persisted key
        /// reflects actual spin (<c>servoMotorIsEngaged</c> says the motor is switched on,
        /// not that the rotor is turning; brakes, resource starvation and a zero motor limit
        /// all park an engaged rotor), so rotors get no baseline at all — which is exactly
        /// the pre-M1 behaviour and far better than a confident wrong spin.</description></item>
        /// </list>
        ///
        /// NOTE for whoever reads the piston case and reaches for the live plan: the live
        /// piston probe lands on <c>traverseVelocity</c> too, for the same
        /// name-mismatch reason. That is a separate recorder-side defect, out of scope here;
        /// this method fixes only what the ghost SPAWNS at.
        /// </summary>
        internal static bool TryResolveSnapshotRoboticPoseKey(
            string moduleName, out string persistedKey)
        {
            persistedKey = null;
            if (string.IsNullOrEmpty(moduleName))
                return false;

            // Wheel robotics (suspension / steering / motor) are continuous-motion
            // families with no meaningful persisted pose: deliberately no baseline.
            if (!FlightRecorder.IsRoboticModuleName(moduleName)
                || FlightRecorder.IsWheelRoboticModuleName(moduleName))
                return false;

            if (string.Equals(moduleName, "ModuleRoboticServoRotor", System.StringComparison.Ordinal))
                return false;

            persistedKey =
                string.Equals(moduleName, "ModuleRoboticServoPiston", System.StringComparison.Ordinal)
                    ? "targetExtension"
                    : "targetAngle";
            return true;
        }

        /// <summary>
        /// Reads one servo's persisted pose scalar through
        /// <see cref="TryResolveSnapshotRoboticPoseKey"/>. A servo whose snapshot node does
        /// not carry that key — or carries an unparseable / non-finite value — degrades to
        /// no-baseline (pre-M1 behaviour) rather than guessing at units.
        ///
        /// CAVEAT, deliberately accepted for the angular families: <c>targetAngle</c> is the
        /// COMMANDED target, not the achieved angle. It is wrong for a servo captured
        /// mid-travel, and for one locked or power-starved short of its target. Neither
        /// <c>servoIsLocked</c> nor <c>servoMotorIsEngaged</c> is used to gate it, because
        /// the alternative is 0 (the prefab pose), which is not a safer guess — it is the
        /// guess M1 exists to stop making — and because locking is what a player does to
        /// HOLD a servo at the target it just reached, so gating on it would suppress the
        /// baseline exactly where it is most likely right. Any recorded robotic event
        /// overrides it anyway.
        /// </summary>
        private static bool TryParseSnapshotRoboticPose(
            ConfigNode moduleNode, string moduleName, out float pose)
        {
            pose = 0f;
            if (moduleNode == null)
                return false;

            if (!TryResolveSnapshotRoboticPoseKey(moduleName, out string persistedKey))
                return false;

            return TryParseSnapshotFloat(moduleNode.GetValue(persistedKey), out pose);
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
