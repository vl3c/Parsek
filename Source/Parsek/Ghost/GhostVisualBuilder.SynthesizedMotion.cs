using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // Synthesized part motion: aero and control surfaces, robot arm scanners, gimbals, sun tracking, robotics and wheels, plus the module field readers they use.
    internal static partial class GhostVisualBuilder
    {
        private static bool TryGetAeroSurfaceDeployInfo(
            Part prefab, out string transformName, out float deployAngleDegrees)
        {
            transformName = null;
            deployAngleDegrees = 0f;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode aeroModule = FindModuleNode(partConfig, "ModuleAeroSurface");
            if (aeroModule == null) return false;

            transformName = aeroModule.GetValue("transformName");
            if (string.IsNullOrEmpty(transformName)) return false;

            string angleRaw = aeroModule.GetValue("ctrlSurfaceRange");
            if (string.IsNullOrEmpty(angleRaw) ||
                !float.TryParse(angleRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out deployAngleDegrees))
            {
                deployAngleDegrees = 0f;
            }

            // Reasonable fallback for modules with missing/invalid range config.
            if (Mathf.Abs(deployAngleDegrees) < 0.01f)
                deployAngleDegrees = 35f;

            return true;
        }

        private static bool TryGetControlSurfaceDeployInfo(
            Part prefab, out string transformName, out float deployAngleDegrees)
        {
            transformName = null;
            deployAngleDegrees = 0f;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode controlModule = FindModuleNode(partConfig, "ModuleControlSurface");
            if (controlModule == null) return false;

            transformName = controlModule.GetValue("transformName");
            if (string.IsNullOrEmpty(transformName)) return false;

            string angleRaw = controlModule.GetValue("ctrlSurfaceRange");
            if (string.IsNullOrEmpty(angleRaw) ||
                !float.TryParse(angleRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out deployAngleDegrees))
            {
                deployAngleDegrees = 0f;
            }

            // Reasonable fallback for modules with missing/invalid range config.
            if (Mathf.Abs(deployAngleDegrees) < 0.01f)
                deployAngleDegrees = 20f;

            return true;
        }

        private static bool TryGetRobotArmScannerDeployAnimations(
            Part prefab, out List<string> animationNames)
        {
            animationNames = null;
            if (prefab == null) return false;

            ConfigNode partConfig = prefab.partInfo?.partConfig;
            if (partConfig == null) return false;

            ConfigNode scannerModule = FindModuleNode(partConfig, "ModuleRobotArmScanner");
            if (scannerModule == null) return false;

            var candidates = new List<string>(3);

            // Priority:
            // 1) editorReachAnimationName often contains the full visible arm articulation preview
            // 2) unpackAnimationName provides a clear stowed/deployed pair
            // 3) animationName is the scan loop (fallback if others are unavailable)
            string[] keys = { "editorReachAnimationName", "unpackAnimationName", "animationName" };
            for (int i = 0; i < keys.Length; i++)
            {
                string name = scannerModule.GetValue(keys[i]);
                if (string.IsNullOrEmpty(name)) continue;

                bool exists = false;
                for (int c = 0; c < candidates.Count; c++)
                {
                    if (string.Equals(candidates[c], name, System.StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    candidates.Add(name);
            }

            if (candidates.Count == 0)
                return false;

            animationNames = candidates;
            return true;
        }

        private static bool HasRoboticModules(Part prefab)
        {
            if (prefab == null || prefab.Modules == null)
                return false;

            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule module = prefab.Modules[i];
                if (module == null)
                    continue;

                if (FlightRecorder.IsRoboticModuleName(module.moduleName))
                    return true;
            }

            return false;
        }

        private static object TryGetModuleFieldValue(PartModule module, string fieldName)
        {
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return null;

            try
            {
                BaseField field = module.Fields[fieldName];
                return field != null ? field.GetValue(module) : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetModuleStringField(
            PartModule module, string fieldName, out string value)
        {
            value = null;
            object raw = TryGetModuleFieldValue(module, fieldName);
            if (raw == null)
                return false;

            value = raw.ToString();
            if (string.IsNullOrEmpty(value))
                return false;

            value = value.Trim();
            return value.Length > 0;
        }

        private static bool TryGetModuleFloatField(
            PartModule module, string fieldName, out float value)
        {
            value = 0f;
            object raw = TryGetModuleFieldValue(module, fieldName);
            if (raw == null)
                return false;

            if (raw is float f)
            {
                value = f;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (raw is double d)
            {
                value = (float)d;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (raw is int i)
            {
                value = i;
                return true;
            }

            string text = raw.ToString();
            if (string.IsNullOrEmpty(text))
                return false;
            text = text.Trim();

            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return !float.IsNaN(value) && !float.IsInfinity(value);

            string[] split = text.Split(',');
            if (split.Length > 0 &&
                float.TryParse(split[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            return false;
        }

        private static bool TryGetModuleIntField(
            PartModule module, string fieldName, out int value)
        {
            value = 0;
            object raw = TryGetModuleFieldValue(module, fieldName);
            if (raw == null)
                return false;

            if (raw is int i)
            {
                value = i;
                return true;
            }

            if (raw is uint ui)
            {
                value = (int)ui;
                return true;
            }

            if (raw is float f && !float.IsNaN(f) && !float.IsInfinity(f))
            {
                value = (int)f;
                return true;
            }

            if (raw is double d && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                value = (int)d;
                return true;
            }

            string text = raw.ToString();
            if (string.IsNullOrEmpty(text))
                return false;

            text = text.Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseRoboticAxis(string axisName, out Vector3 axisLocal)
        {
            axisLocal = Vector3.up;
            if (string.IsNullOrEmpty(axisName))
                return false;

            string normalized = axisName.Trim().ToUpperInvariant();
            if (normalized.Length == 0)
                return false;

            bool negative = normalized.StartsWith("-");
            if (negative)
                normalized = normalized.Substring(1);

            switch (normalized)
            {
                case "X":
                    axisLocal = negative ? -Vector3.right : Vector3.right;
                    return true;
                case "Y":
                    axisLocal = negative ? -Vector3.up : Vector3.up;
                    return true;
                case "Z":
                    axisLocal = negative ? -Vector3.forward : Vector3.forward;
                    return true;
                default:
                    return false;
            }
        }

        private static RoboticVisualMode GetRoboticVisualMode(string moduleName)
        {
            if (string.Equals(moduleName, "ModuleRoboticServoPiston", System.StringComparison.Ordinal))
                return RoboticVisualMode.Linear;

            if (string.Equals(moduleName, "ModuleWheelSuspension", System.StringComparison.Ordinal))
                return RoboticVisualMode.Linear;

            if (string.Equals(moduleName, "ModuleRoboticServoRotor", System.StringComparison.Ordinal))
                return RoboticVisualMode.RotorRpm;

            // Wheel motors no longer replay a recorded scalar (which was an unsigned
            // percent-of-max-torque masquerading as RPM). Their spin is derived from the ghost's
            // ground speed each frame — see FlightRecorder.IsWheelMotorSpinModuleName.
            if (FlightRecorder.IsWheelMotorSpinModuleName(moduleName))
                return RoboticVisualMode.WheelGroundSpeed;

            // S3: steering calipers are derived from the ghost's own ground-track heading rate for
            // the same reason wheel spin is — the recorded scalar was a steering INPUT, not an
            // angle. ModuleWheelMotorSteering is already claimed above by the motor test; its
            // caliper is not separately driven (one transform, one visual).
            if (string.Equals(moduleName, "ModuleWheelSteering", System.StringComparison.Ordinal))
                return RoboticVisualMode.WheelSteeringHeading;

            return RoboticVisualMode.Rotational;
        }

        /// <summary>
        /// Reads the wheel's rolling radius for the ghost, from <c>ModuleWheelBase.radius</c> (a
        /// <c>[KSPField]</c>, so it is reachable through <c>module.Fields</c>) scaled by the part's
        /// <c>rescaleFactor</c> — the same product stock uses when it sizes the wheel collider
        /// (<c>wheel.wheelCollider.radius = radius * part.rescaleFactor</c>).
        ///
        /// A wheel motor submodule does not carry the radius itself; it hangs off
        /// <c>ModuleWheelBase</c> on the same part, which is what
        /// <see cref="TryGetWheelBaseModule"/> resolves. Returns
        /// <see cref="GhostPlaybackLogic.DefaultWheelRadiusMeters"/> when the base module or the
        /// field cannot be reached, or when the value is not a usable positive radius.
        /// </summary>
        private static float ResolveWheelRadius(Part prefab, PartModule module, string partName)
        {
            float radius = 0f;
            bool resolved =
                (TryGetWheelBaseModule(prefab, module, out PartModule wheelBase) &&
                 TryGetModuleFloatField(wheelBase, "radius", out radius))
                || TryGetModuleFloatField(module, "radius", out radius);

            float rescale = prefab != null && prefab.rescaleFactor > 0f ? prefab.rescaleFactor : 1f;
            float scaled = radius * rescale;

            if (!resolved || scaled <= GhostPlaybackLogic.MinWheelRadiusMeters ||
                float.IsNaN(scaled) || float.IsInfinity(scaled))
            {
                ParsekLog.Verbose("Ghost",
                    $"Wheel radius unresolved for '{partName}' module={module?.moduleName}; " +
                    $"raw={radius.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"rescale={rescale.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}; " +
                    $"using default {GhostPlaybackLogic.DefaultWheelRadiusMeters.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}m");
                return GhostPlaybackLogic.DefaultWheelRadiusMeters;
            }

            return scaled;
        }

        private static bool TryGetWheelBaseModule(
            Part prefab, PartModule module, out PartModule baseModule)
        {
            baseModule = null;
            if (prefab == null || module == null || prefab.Modules == null)
                return false;

            if (TryGetModuleIntField(module, "baseModuleIndex", out int baseModuleIndex) &&
                baseModuleIndex >= 0 &&
                baseModuleIndex < prefab.Modules.Count)
            {
                baseModule = prefab.Modules[baseModuleIndex];
                if (baseModule != null)
                    return true;
            }

            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule candidate = prefab.Modules[i];
                if (candidate == null)
                    continue;
                if (string.Equals(candidate.moduleName, "ModuleWheelBase", System.StringComparison.Ordinal))
                {
                    baseModule = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveRoboticTransform(
            Part prefab,
            PartModule module,
            string moduleName,
            out string transformName,
            out Vector3 axis,
            out string transformSource)
        {
            transformName = null;
            axis = Vector3.up;
            transformSource = null;

            if (string.Equals(moduleName, "ModuleWheelSuspension", System.StringComparison.Ordinal))
            {
                if (TryGetModuleStringField(module, "suspensionTransformName", out transformName))
                {
                    axis = Vector3.up;
                    transformSource = "suspensionTransformName";
                    return true;
                }
                return false;
            }

            if (string.Equals(moduleName, "ModuleWheelSteering", System.StringComparison.Ordinal))
            {
                if (TryGetModuleStringField(module, "caliperTransformName", out transformName))
                {
                    axis = Vector3.up;
                    transformSource = "caliperTransformName";
                    return true;
                }
                return false;
            }

            if (string.Equals(moduleName, "ModuleWheelMotor", System.StringComparison.Ordinal) ||
                string.Equals(moduleName, "ModuleWheelMotorSteering", System.StringComparison.Ordinal))
            {
                if (TryGetModuleStringField(module, "wheelTransformName", out transformName))
                {
                    axis = Vector3.right;
                    transformSource = "wheelTransformName";
                    return true;
                }

                if (TryGetWheelBaseModule(prefab, module, out PartModule wheelBase) &&
                    TryGetModuleStringField(wheelBase, "wheelTransformName", out transformName))
                {
                    axis = Vector3.right;
                    transformSource = "ModuleWheelBase.wheelTransformName";
                    return true;
                }

                return false;
            }

            if (!TryGetModuleStringField(module, "servoTransformName", out transformName))
                return false;

            transformSource = "servoTransformName";
            if (TryGetModuleStringField(module, "mainAxis", out string axisName))
                TryParseRoboticAxis(axisName, out axis);
            return true;
        }

        private static bool TryGetRoboticCurrentValue(
            PartModule module, string moduleName, out float value)
        {
            value = 0f;

            string[] fieldNames;
            if (string.Equals(moduleName, "ModuleRoboticServoPiston", System.StringComparison.Ordinal))
            {
                fieldNames = new[] { "currentPosition", "position", "targetPosition" };
            }
            else if (string.Equals(moduleName, "ModuleWheelSuspension", System.StringComparison.Ordinal))
            {
                fieldNames = new[]
                {
                    "currentSuspensionOffset",
                    "suspensionOffset",
                    "compression",
                    "suspensionCompression",
                    "suspensionTravel"
                };
            }
            else if (string.Equals(moduleName, "ModuleWheelSteering", System.StringComparison.Ordinal))
            {
                fieldNames = new[] { "steeringAngle", "currentSteering", "steerAngle", "steeringInput" };
            }
            else if (string.Equals(moduleName, "ModuleRoboticServoRotor", System.StringComparison.Ordinal))
            {
                fieldNames = new[] { "currentRPM", "rpm", "targetRPM", "rpmLimit" };
            }
            else if (string.Equals(moduleName, "ModuleWheelMotor", System.StringComparison.Ordinal) ||
                string.Equals(moduleName, "ModuleWheelMotorSteering", System.StringComparison.Ordinal))
            {
                fieldNames = new[]
                {
                    "currentRPM",
                    "rpm",
                    "wheelRPM",
                    "motorRPM",
                    "targetRPM",
                    "driveOutput",
                    "motorOutput",
                    "wheelSpeed"
                };
            }
            else
            {
                fieldNames = new[] { "currentAngle", "angle", "targetAngle" };
            }

            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (TryGetModuleFloatField(module, fieldNames[i], out value))
                {
                    return true;
                }
            }

            if (string.Equals(moduleName, "ModuleWheelMotorSteering", System.StringComparison.Ordinal) &&
                TryGetModuleFloatField(module, "steeringAngle", out value))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves one prefab model transform onto the ghost, through the clone map first and the
        /// mirrored chain as a fallback — the same two-step every other family uses. Returns null
        /// when the source transform lives outside the model subtree (nothing to mirror against).
        /// </summary>
        private static Transform ResolveGhostTransformForPrefabTransform(
            Transform sourceTransform, Transform modelRoot, Transform modelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            if (sourceTransform == null) return null;
            if (cloneMap != null && cloneMap.TryGetValue(sourceTransform, out Transform ghostT) && ghostT != null)
                return ghostT;
            if (IsDescendantOf(sourceTransform, modelRoot))
                return MirrorTransformChain(sourceTransform, modelRoot, modelNode, cloneMap);
            return null;
        }

        /// <summary>
        /// S3 discovery: the gimbal rings, control surfaces and sun-tracking pivots one part
        /// contributes. All three are read from prefab PartModules exactly as the robotic family is
        /// — named transform field, resolve on the ghost, capture the NEUTRAL localRotation — and
        /// all three are inert (never added) when their transform or authority cannot be read, so a
        /// craft made entirely of unreadable parts costs one empty container.
        ///
        /// Returned as one container rather than three out-parameters because
        /// <c>AddPartVisuals</c> already carries fourteen.
        /// </summary>
        private static SynthesizedMotionGhostInfos TryBuildSynthesizedMotionInfos(
            Part prefab,
            uint persistentId,
            string partName,
            Transform modelRoot,
            Transform modelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            if (prefab?.Modules == null || modelRoot == null || modelNode == null)
                return null;

            var result = new SynthesizedMotionGhostInfos();

            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule module = prefab.Modules[i];
                if (module == null) continue;
                string moduleName = module.moduleName;

                if (string.Equals(moduleName, "ModuleGimbal", System.StringComparison.Ordinal))
                {
                    GimbalGhostInfo gimbal = TryBuildGimbalInfo(
                        prefab, module, persistentId, modelRoot, modelNode, cloneMap);
                    if (gimbal != null)
                    {
                        if (result.gimbals == null) result.gimbals = new List<GimbalGhostInfo>();
                        result.gimbals.Add(gimbal);
                    }
                    continue;
                }

                if (module is ModuleControlSurface)
                {
                    ControlSurfaceGhostInfo surface = TryBuildControlSurfaceInfo(
                        prefab, module, persistentId, modelRoot, modelNode, cloneMap);
                    if (surface != null)
                    {
                        if (result.controlSurfaces == null)
                            result.controlSurfaces = new List<ControlSurfaceGhostInfo>();
                        result.controlSurfaces.Add(surface);
                    }
                    continue;
                }

                if (module is ModuleDeployablePart)
                {
                    SunTrackingGhostInfo tracker = TryBuildSunTrackingInfo(
                        prefab, module, persistentId, modelRoot, modelNode, cloneMap);
                    if (tracker != null)
                    {
                        if (result.sunTrackers == null)
                            result.sunTrackers = new List<SunTrackingGhostInfo>();
                        result.sunTrackers.Add(tracker);
                    }
                    continue;
                }

                if (string.Equals(moduleName, "ModuleAnimationGroup", System.StringComparison.Ordinal))
                {
                    ConverterLoopGhostInfo loop = TryBuildConverterLoopInfo(
                        prefab, module, persistentId, partName, modelNode);
                    if (loop != null)
                    {
                        if (result.converterLoops == null)
                            result.converterLoops = new List<ConverterLoopGhostInfo>();
                        result.converterLoops.Add(loop);
                    }
                }
            }

            if (result.IsEmpty)
                return null;

            ParsekLog.VerboseRateLimited("GhostVisual", $"synth-motion-{partName}",
                $"Synthesized motion for '{partName}' pid={persistentId}: " +
                $"gimbals={(result.gimbals != null ? result.gimbals.Count : 0)} " +
                $"surfaces={(result.controlSurfaces != null ? result.controlSurfaces.Count : 0)} " +
                $"sunTrackers={(result.sunTrackers != null ? result.sunTrackers.Count : 0)} " +
                $"converterLoops={(result.converterLoops != null ? result.converterLoops.Count : 0)}", 30.0);
            return result;
        }

        private static GimbalGhostInfo TryBuildGimbalInfo(
            Part prefab, PartModule module, uint persistentId,
            Transform modelRoot, Transform modelNode, Dictionary<Transform, Transform> cloneMap)
        {
            if (!TryGetModuleStringField(module, "gimbalTransformName", out string transformName))
                return null;

            var info = new GimbalGhostInfo { partPersistentId = persistentId };
            info.gimbalRangeDegrees =
                TryGetModuleFloatField(module, "gimbalRange", out float range) && range > 0f
                    ? range
                    : GhostPlaybackLogic.DefaultGimbalRangeDegrees;

            // FindTransformsRecursive, not FindModelTransform: a multi-nozzle engine (Vector,
            // Mammoth) carries several transforms with the same name and all of them gimbal.
            List<Transform> sources = FindTransformsRecursive(prefab.transform, transformName);
            for (int i = 0; i < sources.Count; i++)
            {
                Transform ghostT = ResolveGhostTransformForPrefabTransform(
                    sources[i], modelRoot, modelNode, cloneMap);
                if (ghostT == null) continue;
                info.gimbalTransforms.Add(ghostT);
                // NEUTRAL is the ghost transform's own build pose, not identity: a prefab that
                // ships a pre-canted bell must deflect around ITS pose or the engine snaps
                // straight on the first synthesis frame.
                info.neutralRotations.Add(ghostT.localRotation);
            }

            return info.gimbalTransforms.Count > 0 ? info : null;
        }

        private static ControlSurfaceGhostInfo TryBuildControlSurfaceInfo(
            Part prefab, PartModule module, uint persistentId,
            Transform modelRoot, Transform modelNode, Dictionary<Transform, Transform> cloneMap)
        {
            if (!TryGetModuleStringField(module, "transformName", out string transformName))
                return null;

            var info = new ControlSurfaceGhostInfo { partPersistentId = persistentId };
            info.rangeDegrees =
                TryGetModuleFloatField(module, "ctrlSurfaceRange", out float range) && range > 0f
                    ? range
                    : GhostPlaybackLogic.DefaultControlSurfaceRangeDegrees;
            info.ignorePitch = TryGetModuleBoolFieldOrDefault(module, "ignorePitch");
            info.ignoreYaw = TryGetModuleBoolFieldOrDefault(module, "ignoreYaw");
            info.ignoreRoll = TryGetModuleBoolFieldOrDefault(module, "ignoreRoll");

            List<Transform> sources = FindTransformsRecursive(prefab.transform, transformName);
            for (int i = 0; i < sources.Count; i++)
            {
                Transform ghostT = ResolveGhostTransformForPrefabTransform(
                    sources[i], modelRoot, modelNode, cloneMap);
                if (ghostT == null) continue;
                info.surfaceTransforms.Add(ghostT);
                info.neutralRotations.Add(ghostT.localRotation);
            }

            return info.surfaceTransforms.Count > 0 ? info : null;
        }

        private static SunTrackingGhostInfo TryBuildSunTrackingInfo(
            Part prefab, PartModule module, uint persistentId,
            Transform modelRoot, Transform modelNode, Dictionary<Transform, Transform> cloneMap)
        {
            // Only TRACKING deployables get a pivot driver. A fixed panel carries a pivotName field
            // too (stock defaults it to "suntransform"), and slewing that would rotate geometry
            // which never moves in game.
            if (!TryGetModuleBoolField(module, "isTracking", out bool isTracking) || !isTracking)
                return null;
            if (!TryGetModuleStringField(module, "pivotName", out string pivotName))
                return null;

            Transform sourcePivot = prefab.FindModelTransform(pivotName);
            Transform ghostPivot = ResolveGhostTransformForPrefabTransform(
                sourcePivot, modelRoot, modelNode, cloneMap);
            if (ghostPivot == null)
                return null;

            return new SunTrackingGhostInfo
            {
                partPersistentId = persistentId,
                pivotTransform = ghostPivot,
                neutralRotation = ghostPivot.localRotation,
                // Stock ModuleDeployableSolarPanel rotates its pivot about the pivot's local up.
                axisLocal = Vector3.up
            };
        }

        private static bool TryGetModuleBoolField(PartModule module, string fieldName, out bool value)
        {
            value = false;
            object raw = TryGetModuleFieldValue(module, fieldName);
            if (raw == null) return false;
            if (raw is bool b) { value = b; return true; }
            return bool.TryParse(raw.ToString(), out value);
        }

        private static bool TryGetModuleBoolFieldOrDefault(PartModule module, string fieldName)
            => TryGetModuleBoolField(module, fieldName, out bool value) && value;

        private static List<RoboticGhostInfo> TryBuildRoboticInfos(
            Part prefab,
            uint persistentId,
            string partName,
            Transform modelRoot,
            Transform modelNode,
            Dictionary<Transform, Transform> cloneMap)
        {
            if (prefab == null || prefab.Modules == null)
                return null;

            var infos = new List<RoboticGhostInfo>();
            int roboticModuleIndex = 0;
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule module = prefab.Modules[i];
                if (module == null)
                    continue;

                string moduleName = module.moduleName;
                if (!FlightRecorder.IsRoboticModuleName(moduleName))
                    continue;

                int moduleIndex = roboticModuleIndex;
                roboticModuleIndex++;

                if (!TryResolveRoboticTransform(
                    prefab, module, moduleName, out string servoTransformName, out Vector3 axis, out string transformSource))
                {
                    continue;
                }

                Transform sourceServo = prefab.FindModelTransform(servoTransformName);
                if (sourceServo == null)
                {
                    continue;
                }

                if (!cloneMap.TryGetValue(sourceServo, out Transform ghostServo) || ghostServo == null)
                {
                    if (IsDescendantOf(sourceServo, modelRoot))
                    {
                        ghostServo = MirrorTransformChain(
                            sourceServo, modelRoot, modelNode, cloneMap);
                    }
                    else
                    {
                        continue;
                    }
                }

                float currentValue = 0f;
                TryGetRoboticCurrentValue(module, moduleName, out currentValue);

                RoboticVisualMode visualMode = GetRoboticVisualMode(moduleName);

                var info = new RoboticGhostInfo
                {
                    partPersistentId = persistentId,
                    moduleIndex = moduleIndex,
                    moduleName = moduleName,
                    servoTransform = ghostServo,
                    axisLocal = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.up,
                    stowedPos = ghostServo.localPosition,
                    stowedRot = ghostServo.localRotation,
                    visualMode = visualMode,
                    currentValue = currentValue,
                    active = false,
                    wheelRadius = visualMode == RoboticVisualMode.WheelGroundSpeed
                        ? ResolveWheelRadius(prefab, module, partName)
                        : 0f
                };

                infos.Add(info);
            }

            return infos.Count > 0 ? infos : null;
        }
    }
}
