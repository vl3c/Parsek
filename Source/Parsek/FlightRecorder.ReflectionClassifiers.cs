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
        /// The module's <c>IScalarModule.GetScalar</c>, when it implements that interface.
        /// <para>
        /// This is the one accessor in the family that is NOT name-keyed, and it exists because
        /// <c>ModuleAnimateHeat</c> cannot be reached any other way: its live scalars
        /// (<c>animState</c> / <c>inputState</c>, both declared on <c>ModuleAnimationSetter</c>) are
        /// plain public fields carrying NO <c>[KSPField]</c> attribute, so they are absent from
        /// <c>module.Fields</c> and no addition to a probe name table can find them. The interface
        /// property is the accessor KSP itself uses, and <c>ModuleAnimationSetter</c> implements it
        /// as <c>GetScalar =&gt; inputState</c> — the 0..1 heat ratio
        /// <c>ModuleAnimateHeat.UpdateHeatEffect</c> writes through <c>SetScalar</c> every frame.
        /// </para>
        /// </summary>
        bool TryGetScalarModuleScalar(out float scalar);

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

            public bool TryGetScalarModuleScalar(out float scalar)
            {
                return TryReadScalarModuleScalar(module, out scalar);
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

            public bool TryGetScalarModuleScalar(out float scalar)
            {
                return TryReadScalarModuleScalar(module, out scalar);
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
        /// The typed half of the <c>IScalarModule</c> accessor. One cast, no name table: see the
        /// contract on <see cref="IModuleFieldValues.TryGetScalarModuleScalar"/> for why
        /// <c>ModuleAnimateHeat</c> is unreachable by name.
        /// </summary>
        private static bool TryReadScalarModuleScalar(PartModule module, out float scalar)
        {
            scalar = 0f;
            if (module == null) return false;

            var scalarModule = module as IScalarModule;
            if (scalarModule == null) return false;

            try
            {
                float value = scalarModule.GetScalar;
                if (float.IsNaN(value) || float.IsInfinity(value)) return false;
                scalar = value;
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("Recorder", "scalar-module-getscalar",
                    $"IScalarModule.GetScalar threw on {module.GetType().Name}: {ex.Message}");
                return false;
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

        /// <summary>
        /// Deployed-family bools for <c>ModuleAeroSurface</c> / <c>ModuleControlSurface</c>.
        ///
        /// <para>
        /// <c>deploy</c> leads and is the ONLY entry that exists on a stock part. Decompiled
        /// (KSP 1.12.5), <c>ModuleControlSurface</c> declares
        /// <c>[KSPField(isPersistant = true, guiActive = true, ...)] public bool deploy;</c> and
        /// <c>ModuleAeroSurface : ModuleControlSurface</c> inherits it (its <c>OnAwake</c> seeds
        /// <c>deployInvert = brakeDeployInvert</c> and its action group toggles <c>deploy</c>).
        /// Neither type declares any of the other eight names, and neither declares a deploy or
        /// retract <c>[KSPEvent]</c> — they expose <c>[KSPAction]</c>s only, which
        /// <c>module.Events</c> never sees. So before <c>deploy</c> was added the event stage found
        /// nothing, both bool stages found nothing and the deflection stage found nothing: every
        /// airbrake and every deployed flap on every stock craft recorded NOTHING.
        /// </para>
        /// </summary>
        internal static readonly string[] AeroSurfaceDeployedFieldNames =
        {
            "deploy",
            "isDeployed",
            "deployed",
            "isExtended",
            "extended",
            "isBraking",
            "brakesOn",
            "isActivated",
            "active"
        };

        /// <summary>
        /// The commanded deploy ANGLE, probed only to veto a <c>deploy == true</c> that produces no
        /// visible movement.
        ///
        /// <para>
        /// Deliberately NOT folded into <see cref="AeroSurfaceDeflectionFieldNames"/>, and the
        /// distinction is load-bearing. That list means "a non-zero magnitude here proves the
        /// surface is deployed". <c>deployAngle</c> does not mean that: it is a
        /// <c>[KSPAxisField]</c> TWEAKABLE — the angle the surface will travel to WHEN deployed —
        /// and <c>ModuleControlSurface.OnStart</c> resolves its <c>float.NaN</c> default to
        /// <c>ctrlSurfaceRange</c> (15-20 deg on stock parts). Putting it in the deflection list
        /// would classify every control surface in the game as permanently deployed.
        /// </para>
        /// <para>
        /// <c>aeroDeployAngle</c> leads because <c>ModuleAeroSurface</c> declares its own axis field
        /// and switches the inherited <c>deployAngle</c> off in the UI
        /// (<c>Fields["deployAngle"].guiActive = false</c>, <c>(… as BaseAxisField).active = false</c>)
        /// while reading <c>aeroDeployAngle</c> for its actual deflection. An airbrake has BOTH
        /// fields present in <c>module.Fields</c>, so the order decides which one is believed.
        /// </para>
        /// </summary>
        internal static readonly string[] AeroSurfaceDeployAngleFieldNames =
        {
            "aeroDeployAngle",
            "deployAngle"
        };

        /// <summary>
        /// Below this many degrees a deployed control surface has no visible deflection, so the
        /// ghost renders it stowed rather than carrying a deploy the viewer cannot see.
        /// </summary>
        internal const float AeroSurfaceVisibleDeployAngleDegrees = 0.5f;

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
        /// Order: event activity, deployed-family bools (now led by the real stock field
        /// <c>deploy</c>), retracted-family bools, then the first finite deflection scalar (a
        /// non-finite candidate is skipped, not read as zero).
        ///
        /// <para>
        /// One refinement sits on the bool stage: a surface whose <c>deploy</c> is on but whose
        /// commanded deploy angle is ~0 is reported RETRACTED, because it does not move and a
        /// DeployableExtended event for it would put a visual change in the recording that the
        /// viewer never sees. See <see cref="AeroSurfaceDeployAngleFieldNames"/> for why the angle
        /// is a veto rather than a deflection signal.
        /// </para>
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
            {
                if (isDeployed && AeroSurfaceDeployAngleIsInvisible(fields))
                {
                    isDeployed = false;
                    isRetracted = true;
                }
                return true;
            }

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
        /// True when the module exposes a commanded deploy angle and it is effectively zero, i.e.
        /// deploying it moves nothing. A module with no such field (or a non-finite one — a
        /// <c>deployAngle</c> read before <c>OnStart</c> resolves it is <c>float.NaN</c>) reports
        /// false, so the veto only ever fires on a positively-measured zero.
        /// </summary>
        internal static bool AeroSurfaceDeployAngleIsInvisible<TFields>(TFields fields)
            where TFields : IModuleFieldValues
        {
            for (int i = 0; i < AeroSurfaceDeployAngleFieldNames.Length; i++)
            {
                if (!fields.TryGetFloat(AeroSurfaceDeployAngleFieldNames[i], out float angle))
                    continue;
                if (float.IsNaN(angle) || float.IsInfinity(angle))
                    continue;

                return Math.Abs(angle) < AeroSurfaceVisibleDeployAngleDegrees;
            }

            return false;
        }

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyRobotArmScannerState"/>.
        /// Order: event activity, deployed-family bools, retracted-family bools, then animTime
        /// endpoints (mid-travel and non-finite animTime both leave the state unclassified).
        ///
        /// <para>
        /// DELIBERATELY LEFT AS-IS by the S5 dead-probe pass, and the reason is not "we ran out of
        /// names" — it is that the audit's premise was wrong here. Three decompiled facts
        /// (KSP 1.12.5, <c>Expansions.Serenity.ModuleRobotArmScanner</c>):
        /// </para>
        /// <list type="number">
        /// <item><description>The probe is NOT dead. <c>ModuleRobotArmScanner : ModuleDeployablePart</c>,
        /// which declares <c>[KSPEvent] Extend()</c> and <c>[KSPEvent] Retract()</c>, and the
        /// scanner actively toggles their availability (<c>Events["Extend"].active = true/false</c>,
        /// <c>Events["Retract"].active = false</c>). <c>BaseEvent.name</c> is the method name, so
        /// the stage-1 keyword match on "extend"/"retract" resolves and the mutually-exclusive
        /// availability rule classifies the arm.</description></item>
        /// <item><description>None of the ten deployed-family names below exists on it, and no name
        /// COULD: the live <c>ArmDeployState</c> sits behind a <c>new</c> property over a private,
        /// unattributed <c>_deployState</c> field, so it is not in <c>module.Fields</c> at all.</description></item>
        /// <item><description>Adding an accessor would be REDUNDANT and actively harmful. The
        /// scanner's <c>deployState</c> setter mirrors every arm state onto the base
        /// <c>ModuleDeployablePart.deployState</c> (UNPACKING/EXTENDING → EXTENDING, SCANNING →
        /// EXTENDED, RETRACTING/PACKING → RETRACTING, BROKEN → BROKEN), and Parsek's
        /// <c>CheckDeployableState</c> / <c>PartStateSeeder.SeedDeployables</c> already poll every
        /// <c>ModuleDeployablePart</c> on the vessel. A second signal would emit a duplicate
        /// DeployableExtended for the same physical motion under a different key.</description></item>
        /// </list>
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

        /// <summary>The <see cref="AnimateHeatScalarModuleSourceField"/> marker reported when the
        /// heat level came from the typed <c>IScalarModule</c> accessor rather than a named field.</summary>
        internal const string AnimateHeatScalarModuleSourceField = "IScalarModule.GetScalar";

        /// <summary>
        /// Pure interpretation half of <see cref="TryClassifyAnimateHeatState"/>: the typed
        /// <c>IScalarModule</c> accessor first, then the first candidate field that resolves to a
        /// finite scalar, normalized to 0..1.
        ///
        /// <para>
        /// The interface accessor leads because it is the ONLY thing that works on a stock part.
        /// <c>ModuleAnimateHeat</c> extends <c>ModuleAnimationSetter</c>, whose live scalars
        /// <c>animState</c> and <c>inputState</c> are plain public fields with NO attribute, so
        /// <c>module.Fields</c> — which is <c>[KSPField]</c>-only — cannot see them, and none of
        /// the eight candidate names below exists on any stock part. The whole reentry-glow
        /// recorder and its already-built Hot / Medium / Cold playback path have therefore been
        /// inert on every stock heat shield since they shipped. <c>ModuleAnimationSetter</c>
        /// implements <c>GetScalar =&gt; inputState</c>, and <c>UpdateHeatEffect</c> writes that
        /// through <c>SetScalar</c> every frame as the already-normalized 0..1 temperature ratio —
        /// which is exactly what this classifier's contract wants.
        /// </para>
        /// <para>
        /// The name table is kept beneath it for modded heat-animation modules that expose a
        /// <c>[KSPField]</c> instead of implementing the interface.
        /// </para>
        /// </summary>
        internal static bool TryClassifyAnimateHeatFromFieldValues<TFields>(
            TFields fields, out float normalizedHeat, out string sourceField)
            where TFields : IModuleFieldValues
        {
            normalizedHeat = 0f;
            sourceField = null;

            if (fields.TryGetScalarModuleScalar(out float scalar)
                && !float.IsNaN(scalar) && !float.IsInfinity(scalar))
            {
                normalizedHeat = NormalizeAnimateHeatScalar(scalar);
                sourceField = AnimateHeatScalarModuleSourceField;
                return true;
            }

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
        /// <summary>
        /// Suspension travel candidates. <c>suspensionOffset</c> was REMOVED: decompiled,
        /// <c>ModuleWheels.ModuleWheelSuspension</c> declares it as a plain <c>[KSPField]</c> read
        /// exactly once, in <c>OnStart</c>, to configure the wheel collider
        /// (<c>wheel.wheelCollider.suspensionOffset = suspensionOffset * part.rescaleFactor</c>).
        /// It is a config CONSTANT that never moves, and because it resolved, it shadowed the
        /// working <c>suspensionPos</c> vector fallback below — the only live signal on the module,
        /// a <c>[KSPField(isPersistant = true)] Vector3</c> assigned from
        /// <c>suspensionTransform.localPosition</c> as the wheel compresses. None of the remaining
        /// names exists on a stock part, so a stock rover now falls straight through to
        /// <c>suspensionPos</c> and records real suspension travel for the first time.
        /// </summary>
        private static readonly string[] WheelSuspensionScalarFieldNames =
        {
            "currentSuspensionOffset",
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
        /// <summary>
        /// Piston stroke candidates, reordered after decompiling
        /// <c>Expansions.Serenity.ModuleRoboticServoPiston</c> (KSP 1.12.5).
        ///
        /// <para>
        /// <c>currentExtension</c> leads and is the correct live signal: it is a
        /// <c>[KSPField(guiActive = true, guiUnits = "m")] public float</c> recomputed from the
        /// actual transform geometry
        /// (<c>currentExtension = Vector3.Dot(… ) + driveTargetPosition</c>), so it sweeps
        /// continuously through a stroke. <c>targetExtension</c> follows it as the commanded
        /// setpoint — a <c>[KSPAxisField(isPersistant = true)]</c> that STEPS rather than sweeping,
        /// but is the right answer for a module that somehow lacks the live one, and is also the
        /// only one of the two that a saved craft carries (which is why the snapshot-side ghost
        /// baseline reads <c>targetExtension</c> and this live probe reads <c>currentExtension</c>:
        /// the divergence is deliberate and follows from which field is persistent).
        /// </para>
        /// <para>
        /// Two names were REMOVED. <c>targetPosition</c> is <c>private float</c> with no attribute,
        /// so <c>module.Fields</c> can never see it — it was never reachable. <c>traverseVelocity</c>
        /// IS reachable, and that was the bug: a <c>[KSPAxisField]</c> SPEED SLIDER (m/s, 0.05-5)
        /// that is constant for the whole stroke. It resolved first, so every piston recorded its
        /// speed setting as though it were a pose, and the working <c>servoTransformPosition</c>
        /// transform fallback below was never reached.
        /// </para>
        /// </summary>
        private static readonly string[] RoboticPistonScalarFieldNames =
        {
            "currentExtension",
            "targetExtension",
            "currentPosition",
            "position"
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
