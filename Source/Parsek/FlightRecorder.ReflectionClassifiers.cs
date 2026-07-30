using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Which deploy/retract keyword vocabulary a module's UI events are matched against.
    /// Each value maps to one pure <c>Classify*EventName</c> method.
    /// </summary>
    internal enum DeployEventKeywordSet
    {
        RetractableLadder,
        AnimationGroup,
        AnimateGeneric,
        AeroSurface,
        RobotArmScanner
    }

    /// <summary>
    /// Read-only, name-keyed view over one PartModule's observable state.
    /// <para>
    /// This is the seam between the two halves of the reflection-classifier family: the thin
    /// reflection probes (<see cref="FlightRecorder.PartModuleFieldValues"/> and
    /// <see cref="FlightRecorder.LegacyPartModuleFieldValues"/>) only answer "what does the field
    /// called X hold?" and "which deploy/retract events are available?", while every field NAME,
    /// every fallback ORDER and every interpretation rule lives in the pure
    /// <c>...FromFieldValues</c> cores.
    /// </para>
    /// <para>
    /// The cores are generic over the reader so a struct reader stays unboxed on the
    /// per-physics-frame poll path, and so the reads stay lazy: a field is only read (and the
    /// event list only walked) once a classifier actually reaches that fallback stage.
    /// </para>
    /// <para>
    /// A missing or unreadable field is reported by returning false, leaving the out value at its
    /// default. Headless tests implement this interface over a dictionary.
    /// </para>
    /// </summary>
    internal interface IModuleFieldValues
    {
        bool TryGetBool(string fieldName, out bool value);
        bool TryGetFloat(string fieldName, out float value);
        bool TryGetString(string fieldName, out string value);
        bool TryGetVectorMagnitude(string fieldName, out float magnitude);
        bool TryGetRotationAngleDegrees(string fieldName, out float angleDegrees);

        /// <summary>
        /// Aggregated deploy/retract UI-event availability under the given keyword vocabulary:
        /// whether any such event exists at all, and whether any matching one is currently active.
        /// </summary>
        void ReadDeployRetractEventActivity(
            DeployEventKeywordSet keywordSet,
            out bool sawDeployEvent, out bool sawRetractEvent,
            out bool canDeploy, out bool canRetract);
    }

    public partial class FlightRecorder
    {
        #region Reflection probes (thin halves)

        /// <summary>
        /// Reflection half used by the aero-surface / robot-arm / animate-heat / robotics
        /// classifiers: reads through <c>FindModuleField</c> + the TryParse* value coercions.
        /// </summary>
        internal struct PartModuleFieldValues : IModuleFieldValues
        {
            private readonly PartModule module;

            internal PartModuleFieldValues(PartModule module)
            {
                this.module = module;
            }

            public bool TryGetBool(string fieldName, out bool value)
            {
                return TryReadModuleBoolField(module, fieldName, out value);
            }

            public bool TryGetFloat(string fieldName, out float value)
            {
                return TryReadModuleFloatField(module, fieldName, out value);
            }

            public bool TryGetString(string fieldName, out string value)
            {
                return TryGetModuleStringField(module, fieldName, out value);
            }

            public bool TryGetVectorMagnitude(string fieldName, out float magnitude)
            {
                magnitude = 0f;
                if (!TryReadModuleVector3Field(module, fieldName, out Vector3 vec))
                    return false;
                magnitude = vec.magnitude;
                return true;
            }

            public bool TryGetRotationAngleDegrees(string fieldName, out float angleDegrees)
            {
                angleDegrees = 0f;
                if (!TryReadModuleQuaternionField(module, fieldName, out Quaternion rot))
                    return false;
                angleDegrees = Quaternion.Angle(Quaternion.identity, rot);
                return true;
            }

            public void ReadDeployRetractEventActivity(
                DeployEventKeywordSet keywordSet,
                out bool sawDeployEvent, out bool sawRetractEvent,
                out bool canDeploy, out bool canRetract)
            {
                ReadModuleDeployRetractEventActivity(
                    module, keywordSet,
                    out sawDeployEvent, out sawRetractEvent, out canDeploy, out canRetract);
            }
        }

        /// <summary>
        /// Reflection half used by the ladder / animation-group / animate-generic classifiers,
        /// which read bools through the <c>module.Fields[name]</c> indexer helper rather than
        /// <c>FindModuleField</c>. Kept as a separate reader so those classifiers keep their exact
        /// original read semantics.
        /// </summary>
        internal struct LegacyPartModuleFieldValues : IModuleFieldValues
        {
            private readonly PartModule module;

            internal LegacyPartModuleFieldValues(PartModule module)
            {
                this.module = module;
            }

            public bool TryGetBool(string fieldName, out bool value)
            {
                return TryGetModuleBoolField(module, fieldName, out value);
            }

            public bool TryGetFloat(string fieldName, out float value)
            {
                return TryReadModuleFloatField(module, fieldName, out value);
            }

            public bool TryGetString(string fieldName, out string value)
            {
                return TryGetModuleStringField(module, fieldName, out value);
            }

            public bool TryGetVectorMagnitude(string fieldName, out float magnitude)
            {
                magnitude = 0f;
                if (!TryReadModuleVector3Field(module, fieldName, out Vector3 vec))
                    return false;
                magnitude = vec.magnitude;
                return true;
            }

            public bool TryGetRotationAngleDegrees(string fieldName, out float angleDegrees)
            {
                angleDegrees = 0f;
                if (!TryReadModuleQuaternionField(module, fieldName, out Quaternion rot))
                    return false;
                angleDegrees = Quaternion.Angle(Quaternion.identity, rot);
                return true;
            }

            public void ReadDeployRetractEventActivity(
                DeployEventKeywordSet keywordSet,
                out bool sawDeployEvent, out bool sawRetractEvent,
                out bool canDeploy, out bool canRetract)
            {
                ReadModuleDeployRetractEventActivity(
                    module, keywordSet,
                    out sawDeployEvent, out sawRetractEvent, out canDeploy, out canRetract);
            }
        }

        /// <summary>
        /// Walks a module's UI events once and aggregates deploy/retract availability under the
        /// requested keyword vocabulary. The only reflection/KSP-typed half of the event stage.
        /// </summary>
        private static void ReadModuleDeployRetractEventActivity(
            PartModule module, DeployEventKeywordSet keywordSet,
            out bool sawDeployEvent, out bool sawRetractEvent,
            out bool canDeploy, out bool canRetract)
        {
            sawDeployEvent = false;
            sawRetractEvent = false;
            canDeploy = false;
            canRetract = false;

            if (module == null || module.Events == null) return;

            for (int i = 0; i < module.Events.Count; i++)
            {
                BaseEvent evt = module.Events[i];
                if (evt == null) continue;

                string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                ClassifyDeployRetractEventName(
                    keywordSet, evtName, guiName, out bool isDeployEvent, out bool isRetractEvent);

                if (isDeployEvent)
                {
                    sawDeployEvent = true;
                    canDeploy = canDeploy || evt.active;
                }

                if (isRetractEvent)
                {
                    sawRetractEvent = true;
                    canRetract = canRetract || evt.active;
                }
            }
        }

        #endregion

        #region Probed field-name tables

        internal const string RetractableLadderStateFieldName = "StateName";

        internal static readonly string[] AnimationDeployedFieldNames =
        {
            "isDeployed",
            "deployed",
            "isExtended",
            "extended"
        };

        internal static readonly string[] AnimationRetractedFieldNames =
        {
            "isRetracted",
            "retracted"
        };

        internal static readonly string[] AeroSurfaceDeployedFieldNames =
        {
            "isDeployed",
            "deployed",
            "isExtended",
            "extended",
            "isBraking",
            "brakesOn",
            "isActivated",
            "active"
        };

        internal static readonly string[] AeroSurfaceRetractedFieldNames =
        {
            "isRetracted",
            "retracted",
            "isStowed",
            "stowed",
            "isPacked",
            "packed"
        };

        internal static readonly string[] AeroSurfaceDeflectionFieldNames =
        {
            "currentDeflection",
            "deflection",
            "deployPercent",
            "position"
        };

        internal static readonly string[] RobotArmScannerDeployedFieldNames =
        {
            "isUnpacked",
            "unpacked",
            "isDeployed",
            "deployed",
            "isExtended",
            "extended",
            "isScanning",
            "scanning",
            "isWorking",
            "working"
        };

        internal static readonly string[] RobotArmScannerRetractedFieldNames =
        {
            "isRetracted",
            "retracted",
            "isPacked",
            "packed",
            "isStowed",
            "stowed"
        };

        internal static readonly string[] AnimateHeatCandidateFieldNames =
        {
            "animTime",
            "heatAnimTime",
            "thermalAnimState",
            "normalizedHeat",
            "heat",
            "heatValue",
            "temperatureRatio",
            "tempRatio"
        };

        internal static readonly string[] RoboticMovingFieldNames =
        {
            "servoIsMoving",
            "isMoving",
            "moving",
            "isTraversing",
            "isRotating"
        };

        #endregion

        #region Pure event-name classifiers

        /// <summary>
        /// Dispatches an event name/gui-name pair to the keyword vocabulary of one module family.
        /// Inputs are expected lowercased.
        /// </summary>
        internal static void ClassifyDeployRetractEventName(
            DeployEventKeywordSet keywordSet, string evtName, string guiName,
            out bool isDeploy, out bool isRetract)
        {
            switch (keywordSet)
            {
                case DeployEventKeywordSet.RetractableLadder:
                    ClassifyRetractableLadderEventName(evtName, guiName, out isDeploy, out isRetract);
                    return;
                case DeployEventKeywordSet.AnimationGroup:
                    ClassifyAnimationGroupEventName(evtName, guiName, out isDeploy, out isRetract);
                    return;
                case DeployEventKeywordSet.AnimateGeneric:
                    ClassifyAnimateGenericEventName(evtName, guiName, out isDeploy, out isRetract);
                    return;
                case DeployEventKeywordSet.RobotArmScanner:
                    ClassifyRobotArmScannerEventName(evtName, guiName, out isDeploy, out isRetract);
                    return;
                default:
                    ClassifyAeroEventName(
                        evtName ?? string.Empty, guiName ?? string.Empty, out isDeploy, out isRetract);
                    return;
            }
        }

        /// <summary>
        /// Retractable-ladder event keywords: extend vs retract. Inputs are expected lowercased.
        /// </summary>
        internal static void ClassifyRetractableLadderEventName(
            string evtName, string guiName, out bool isExtend, out bool isRetract)
        {
            evtName = evtName ?? string.Empty;
            guiName = guiName ?? string.Empty;

            isExtend = evtName.Contains("extend") || guiName.Contains("extend");
            isRetract = evtName.Contains("retract") || guiName.Contains("retract");
        }

        /// <summary>
        /// ModuleAnimationGroup event keywords: deploy/extend vs retract. Inputs expected lowercased.
        /// </summary>
        internal static void ClassifyAnimationGroupEventName(
            string evtName, string guiName, out bool isDeploy, out bool isRetract)
        {
            evtName = evtName ?? string.Empty;
            guiName = guiName ?? string.Empty;

            isDeploy =
                evtName.Contains("deploy") || guiName.Contains("deploy") ||
                evtName.Contains("extend") || guiName.Contains("extend");
            isRetract =
                evtName.Contains("retract") || guiName.Contains("retract");
        }

        /// <summary>
        /// ModuleAnimateGeneric event keywords: deploy/extend/open/inflate vs
        /// retract/close/deflate. Inputs expected lowercased.
        /// </summary>
        internal static void ClassifyAnimateGenericEventName(
            string evtName, string guiName, out bool isDeploy, out bool isRetract)
        {
            evtName = evtName ?? string.Empty;
            guiName = guiName ?? string.Empty;

            isDeploy =
                evtName.Contains("deploy") || guiName.Contains("deploy") ||
                evtName.Contains("extend") || guiName.Contains("extend") ||
                evtName.Contains("open") || guiName.Contains("open") ||
                evtName.Contains("inflate") || guiName.Contains("inflate");
            isRetract =
                evtName.Contains("retract") || guiName.Contains("retract") ||
                evtName.Contains("close") || guiName.Contains("close") ||
                evtName.Contains("deflate") || guiName.Contains("deflate");
        }

        /// <summary>
        /// Robot-arm / scanner event keywords: deploy/unpack/extend/scan/start vs
        /// retract/pack/stow/stop/cancel. "unpack" deliberately does NOT count as a pack (retract)
        /// match even though it contains the substring. Inputs expected lowercased.
        /// </summary>
        internal static void ClassifyRobotArmScannerEventName(
            string evtName, string guiName, out bool isDeploy, out bool isRetract)
        {
            evtName = evtName ?? string.Empty;
            guiName = guiName ?? string.Empty;

            isDeploy =
                evtName.Contains("deploy") || guiName.Contains("deploy") ||
                evtName.Contains("unpack") || guiName.Contains("unpack") ||
                evtName.Contains("extend") || guiName.Contains("extend") ||
                evtName.Contains("scan") || guiName.Contains("scan") ||
                evtName.Contains("start") || guiName.Contains("start");
            isRetract =
                evtName.Contains("retract") || guiName.Contains("retract") ||
                ((evtName.Contains("pack") || guiName.Contains("pack")) &&
                    !evtName.Contains("unpack") && !guiName.Contains("unpack")) ||
                evtName.Contains("stow") || guiName.Contains("stow") ||
                evtName.Contains("stop") || guiName.Contains("stop") ||
                evtName.Contains("cancel") || guiName.Contains("cancel");
        }

        #endregion

        #region Pure interpretation cores

        /// <summary>
        /// Reads the first field in <paramref name="fieldNames"/> that resolves to a bool.
        /// Returns false (leaving <paramref name="value"/> false) when none of them resolve.
        /// </summary>
        private static bool TryReadFirstBoolField<TFields>(
            TFields fields, string[] fieldNames, out bool value)
            where TFields : IModuleFieldValues
        {
            value = false;
            if (fieldNames == null) return false;

            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (fields.TryGetBool(fieldNames[i], out value))
                    return true;
            }

            value = false;
            return false;
        }

        /// <summary>
        /// Reads the first field in <paramref name="fieldNames"/> that resolves to a float and
        /// reports which name supplied it.
        /// </summary>
        private static bool TryReadFirstFloatField<TFields>(
            TFields fields, string[] fieldNames, out float value, out string sourceField)
            where TFields : IModuleFieldValues
        {
            value = 0f;
            sourceField = null;
            if (fieldNames == null) return false;

            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (fields.TryGetFloat(fieldNames[i], out value))
                {
                    sourceField = fieldNames[i];
                    return true;
                }
            }

            value = 0f;
            return false;
        }

        /// <summary>
        /// Shared event-activity stage: mutually-exclusive availability of the deploy and retract
        /// actions tells us the current state. Skipped entirely when the module exposes no
        /// matching event at all.
        /// </summary>
        private static bool TryClassifyFromEventActivity<TFields>(
            TFields fields, DeployEventKeywordSet keywordSet,
            out bool isDeployed, out bool isRetracted)
            where TFields : IModuleFieldValues
        {
            isDeployed = false;
            isRetracted = false;

            fields.ReadDeployRetractEventActivity(
                keywordSet,
                out bool sawDeployEvent, out bool sawRetractEvent,
                out bool canDeploy, out bool canRetract);

            if (!sawDeployEvent && !sawRetractEvent)
                return false;

            return TryClassifyLadderStateFromEventActivity(
                canExtend: canDeploy, canRetract: canRetract,
                out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Shared deployed/retracted bool-field fallback: a resolved "deployed-family" field wins
        /// first, then a "retracted-family" field (inverted). Returns false when neither resolves.
        /// </summary>
        private static bool TryClassifyFromDeployedRetractedFields<TFields>(
            TFields fields, string[] deployedFieldNames, string[] retractedFieldNames,
            out bool isDeployed, out bool isRetracted)
            where TFields : IModuleFieldValues
        {
            isDeployed = false;
            isRetracted = false;

            if (TryReadFirstBoolField(fields, deployedFieldNames, out bool deployedValue))
            {
                isDeployed = deployedValue;
                isRetracted = !deployedValue;
                return true;
            }

            if (TryReadFirstBoolField(fields, retractedFieldNames, out bool retractedValue))
            {
                isRetracted = retractedValue;
                isDeployed = !retractedValue;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyRetractableLadderState"/>.
        /// Order: StateName (a transient Extending/Retracting name aborts classification without
        /// consulting anything else), then event activity, then the bool-field fallbacks.
        /// </summary>
        internal static bool TryClassifyRetractableLadderStateFromFieldValues<TFields>(
            TFields fields, out bool isDeployed, out bool isRetracted)
            where TFields : IModuleFieldValues
        {
            isDeployed = false;
            isRetracted = false;

            if (fields.TryGetString(RetractableLadderStateFieldName, out string stateName))
            {
                if (TryClassifyRetractableLadderStateName(stateName, out isDeployed, out isRetracted))
                    return true;

                // Mid-animation: report "unknown" rather than guessing from stale fallbacks.
                if (IsRetractableLadderTransientStateName(stateName))
                {
                    isDeployed = false;
                    isRetracted = false;
                    return false;
                }
            }

            if (TryClassifyFromEventActivity(
                fields, DeployEventKeywordSet.RetractableLadder, out isDeployed, out isRetracted))
                return true;

            return TryClassifyFromDeployedRetractedFields(
                fields, AnimationDeployedFieldNames, AnimationRetractedFieldNames,
                out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyAnimationGroupState"/>.
        /// Order: event activity, then the deployed/retracted bool-field fallbacks.
        /// </summary>
        internal static bool TryClassifyAnimationGroupStateFromFieldValues<TFields>(
            TFields fields, out bool isDeployed, out bool isRetracted)
            where TFields : IModuleFieldValues
        {
            isDeployed = false;
            isRetracted = false;

            if (TryClassifyFromEventActivity(
                fields, DeployEventKeywordSet.AnimationGroup, out isDeployed, out isRetracted))
                return true;

            return TryClassifyFromDeployedRetractedFields(
                fields, AnimationDeployedFieldNames, AnimationRetractedFieldNames,
                out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyAnimateGenericState"/>.
        /// Order: animTime endpoints (mid-travel falls through), then event activity, then the
        /// deployed/retracted bool-field fallbacks.
        /// </summary>
        internal static bool TryClassifyAnimateGenericStateFromFieldValues<TFields>(
            TFields fields, float animTime, out bool isDeployed, out bool isRetracted)
            where TFields : IModuleFieldValues
        {
            isDeployed = false;
            isRetracted = false;

            if (!float.IsNaN(animTime) && !float.IsInfinity(animTime))
            {
                ClassifyLadderState(animTime, out isDeployed, out isRetracted);
                if (isDeployed || isRetracted)
                    return true;
            }

            if (TryClassifyFromEventActivity(
                fields, DeployEventKeywordSet.AnimateGeneric, out isDeployed, out isRetracted))
                return true;

            return TryClassifyFromDeployedRetractedFields(
                fields, AnimationDeployedFieldNames, AnimationRetractedFieldNames,
                out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyAeroSurfaceState"/> (and of
        /// <see cref="TryClassifyControlSurfaceState"/>, which shares the same contract).
        /// Order: event activity, deployed-family bools, retracted-family bools, then the first
        /// finite deflection scalar (a non-finite candidate is skipped, not read as zero).
        /// </summary>
        internal static bool TryClassifyAeroSurfaceStateFromFieldValues<TFields>(
            TFields fields, out bool isDeployed, out bool isRetracted)
            where TFields : IModuleFieldValues
        {
            isDeployed = false;
            isRetracted = false;

            if (TryClassifyFromEventActivity(
                fields, DeployEventKeywordSet.AeroSurface, out isDeployed, out isRetracted))
                return true;

            if (TryClassifyFromDeployedRetractedFields(
                fields, AeroSurfaceDeployedFieldNames, AeroSurfaceRetractedFieldNames,
                out isDeployed, out isRetracted))
                return true;

            for (int i = 0; i < AeroSurfaceDeflectionFieldNames.Length; i++)
            {
                if (!fields.TryGetFloat(AeroSurfaceDeflectionFieldNames[i], out float deflection))
                    continue;

                if (float.IsNaN(deflection) || float.IsInfinity(deflection))
                    continue;

                isDeployed = Math.Abs(deflection) > 0.01f;
                isRetracted = !isDeployed;
                return true;
            }

            isDeployed = false;
            isRetracted = false;
            return false;
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyRobotArmScannerState"/>.
        /// Order: event activity, deployed-family bools, retracted-family bools, then animTime
        /// endpoints (mid-travel and non-finite animTime both leave the state unclassified).
        /// </summary>
        internal static bool TryClassifyRobotArmScannerStateFromFieldValues<TFields>(
            TFields fields, out bool isDeployed, out bool isRetracted)
            where TFields : IModuleFieldValues
        {
            isDeployed = false;
            isRetracted = false;

            if (TryClassifyFromEventActivity(
                fields, DeployEventKeywordSet.RobotArmScanner, out isDeployed, out isRetracted))
                return true;

            if (TryClassifyFromDeployedRetractedFields(
                fields, RobotArmScannerDeployedFieldNames, RobotArmScannerRetractedFieldNames,
                out isDeployed, out isRetracted))
                return true;

            if (fields.TryGetFloat("animTime", out float animTime) &&
                !float.IsNaN(animTime) && !float.IsInfinity(animTime))
            {
                ClassifyLadderState(animTime, out isDeployed, out isRetracted);
                if (isDeployed || isRetracted)
                    return true;
            }

            isDeployed = false;
            isRetracted = false;
            return false;
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyAnimateHeatState"/>: takes the first
        /// candidate field that resolves to a finite scalar and normalizes it to 0..1.
        /// </summary>
        internal static bool TryClassifyAnimateHeatFromFieldValues<TFields>(
            TFields fields, out float normalizedHeat, out string sourceField)
            where TFields : IModuleFieldValues
        {
            normalizedHeat = 0f;
            sourceField = null;

            for (int i = 0; i < AnimateHeatCandidateFieldNames.Length; i++)
            {
                if (!fields.TryGetFloat(AnimateHeatCandidateFieldNames[i], out float raw))
                    continue;
                if (float.IsNaN(raw) || float.IsInfinity(raw))
                    continue;

                normalizedHeat = NormalizeAnimateHeatScalar(raw);
                sourceField = AnimateHeatCandidateFieldNames[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryGetRoboticMovingState"/>: the first
        /// moving-flag field that resolves wins, including when it resolves to false.
        /// </summary>
        internal static bool TryClassifyRoboticMovingFromFieldValues<TFields>(
            TFields fields, out bool moving)
            where TFields : IModuleFieldValues
        {
            return TryReadFirstBoolField(fields, RoboticMovingFieldNames, out moving);
        }

        /// <summary>
        /// Per-module-name scalar-field plan for wheel robotics: which fields to probe, in order,
        /// and the position deadband that suits that quantity (suspension travel is finer-grained
        /// than steering degrees, which is finer-grained than motor RPM).
        /// </summary>
        private static readonly string[] WheelSuspensionScalarFieldNames =
        {
            "currentSuspensionOffset",
            "suspensionOffset",
            "compression",
            "suspensionCompression",
            "suspensionTravel"
        };

        private static readonly string[] WheelSteeringScalarFieldNames =
        {
            "steeringAngle",
            "currentSteering",
            "steerAngle",
            "steeringInput"
        };

        private static readonly string[] WheelMotorScalarFieldNames =
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

        internal static void ResolveWheelRoboticFieldPlan(
            string moduleName, out float deadband, out string[] scalarFieldNames)
        {
            if (string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal))
            {
                deadband = 0.0025f;
                scalarFieldNames = WheelSuspensionScalarFieldNames;
                return;
            }

            if (string.Equals(moduleName, "ModuleWheelSteering", StringComparison.Ordinal))
            {
                deadband = 0.25f;
                scalarFieldNames = WheelSteeringScalarFieldNames;
                return;
            }

            deadband = 1f;
            scalarFieldNames = WheelMotorScalarFieldNames;
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryGetWheelRoboticPositionValue"/>.
        /// Order: the module's preferred scalar plan, then the suspension position vector
        /// (suspension modules only), then the steering angle (motor-steering modules only, which
        /// also tightens the deadband to the steering one).
        /// </summary>
        internal static bool TryClassifyWheelRoboticPositionFromFieldValues<TFields>(
            TFields fields, string moduleName,
            out float positionValue, out float deadband, out string sourceField)
            where TFields : IModuleFieldValues
        {
            ResolveWheelRoboticFieldPlan(moduleName, out deadband, out string[] scalarFieldNames);

            if (TryReadFirstFloatField(fields, scalarFieldNames, out positionValue, out sourceField))
                return true;

            if (string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal) &&
                fields.TryGetVectorMagnitude("suspensionPos", out float suspensionMagnitude))
            {
                positionValue = suspensionMagnitude;
                sourceField = "suspensionPos";
                return true;
            }

            if (string.Equals(moduleName, "ModuleWheelMotorSteering", StringComparison.Ordinal) &&
                fields.TryGetFloat("steeringAngle", out float steeringAngle))
            {
                positionValue = steeringAngle;
                deadband = 0.25f;
                sourceField = "steeringAngle";
                return true;
            }

            positionValue = 0f;
            sourceField = null;
            return false;
        }

        /// <summary>
        /// Per-module-name scalar-field plan for the non-wheel robotic servos. Pistons travel in
        /// metres (linear deadband); hinges, rotation servos and rotors are angular.
        /// Wheel module names are dispatched away before this is reached.
        /// </summary>
        private static readonly string[] RoboticPistonScalarFieldNames =
        {
            "currentPosition",
            "position",
            "targetPosition",
            "traverseVelocity"
        };

        private static readonly string[] RoboticRotorScalarFieldNames =
        {
            "currentRPM",
            "rpm",
            "rpmLimit"
        };

        private static readonly string[] RoboticAngularScalarFieldNames =
        {
            "currentAngle",
            "angle",
            "targetAngle"
        };

        internal static void ResolveRoboticFieldPlan(
            string moduleName, out float deadband, out string[] scalarFieldNames)
        {
            if (string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal))
            {
                deadband = roboticLinearDeadbandMeters;
                scalarFieldNames = RoboticPistonScalarFieldNames;
                return;
            }

            deadband = roboticAngularDeadbandDegrees;

            if (string.Equals(moduleName, "ModuleRoboticServoRotor", StringComparison.Ordinal))
            {
                scalarFieldNames = RoboticRotorScalarFieldNames;
                return;
            }

            scalarFieldNames = RoboticAngularScalarFieldNames;
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryGetRoboticPositionValue"/>.
        /// Wheel module names route to the wheel core; everything else probes the module's scalar
        /// plan, then the servo transform position magnitude, then the servo transform rotation
        /// expressed as an angle from identity.
        /// </summary>
        internal static bool TryClassifyRoboticPositionFromFieldValues<TFields>(
            TFields fields, string moduleName,
            out float positionValue, out float deadband, out string sourceField)
            where TFields : IModuleFieldValues
        {
            if (IsWheelRoboticModuleName(moduleName))
                return TryClassifyWheelRoboticPositionFromFieldValues(
                    fields, moduleName, out positionValue, out deadband, out sourceField);

            ResolveRoboticFieldPlan(moduleName, out deadband, out string[] scalarFieldNames);

            if (TryReadFirstFloatField(fields, scalarFieldNames, out positionValue, out sourceField))
                return true;

            if (fields.TryGetVectorMagnitude("servoTransformPosition", out float servoMagnitude))
            {
                positionValue = servoMagnitude;
                sourceField = "servoTransformPosition";
                return true;
            }

            if (fields.TryGetRotationAngleDegrees("servoTransformRotation", out float servoAngle))
            {
                positionValue = servoAngle;
                sourceField = "servoTransformRotation";
                return true;
            }

            positionValue = 0f;
            sourceField = null;
            return false;
        }

        #endregion
    }
}
