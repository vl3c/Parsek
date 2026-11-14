using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Where a shared part-state poll writes: the event list, the log tag, and the wording of
    /// each log line that differs between the active-vessel recorder and the background
    /// recorder. <see cref="FlightRecorder"/> and <see cref="BackgroundRecorder"/> each own one;
    /// the loops in <see cref="PartStatePoller"/> never name either recorder's state type.
    /// </summary>
    internal abstract class PartPollSink
    {
        /// <summary>The <c>ParsekLog</c> subsystem tag for every line the poll writes.</summary>
        internal abstract string Tag { get; }

        /// <summary>The list a new part event is appended to (read at each append).</summary>
        internal abstract List<PartEvent> Events { get; }

        /// <summary>Text after <c>pid=N</c> on a plain part-event line.</summary>
        internal abstract string EventSuffix();

        /// <summary>Text after <c>pid=N</c> on a deployable-broken edge line.</summary>
        internal abstract string DeployableBrokenSuffix(ModuleDeployablePart.DeployState ds);

        /// <summary>Rate-limit key of the pose-less deployable summary line.</summary>
        internal abstract string DeployableNoPoseRateKey();

        /// <summary>Closing note of the pose-less deployable summary line.</summary>
        internal abstract string DeployableNoPoseNote();

        /// <summary>Text after <c>pid=N</c> when the family label leads the vessel note.</summary>
        internal abstract string LabelSuffix(string label);

        /// <summary>Text after <c>pid=N</c> when the vessel note leads the family label.</summary>
        internal abstract string VesselFirstLabelSuffix(string label);

        /// <summary>The vessel context of the departed-converter summary line.</summary>
        internal abstract string ConverterDepartureContext(Vessel v);

        /// <summary>
        /// Text after <c>midx=N</c> on a once-per-module "unable to classify" line. Evaluated only
        /// when the line is written.
        /// </summary>
        internal abstract string ClassificationMissDetail(PartModule module);

        /// <summary>The same for a <c>ModuleAnimateGeneric</c> miss.</summary>
        internal abstract string AnimateGenericMissDetail(ModuleAnimateGeneric module);

        /// <summary>Text after <c>midx=N</c> on an animate-heat event line.</summary>
        internal abstract string AnimateHeatEventSuffix(float normalizedHeat, string sourceField);
    }

    /// <summary>
    /// The per-part polling loops both recorders share. Each method walks a vessel's parts,
    /// classifies one module family, runs the family's transition helper against the caller's
    /// per-vessel tracking containers, and appends any event to the sink. Sampling policy
    /// (when and whether a vessel is polled) stays with the recorders.
    /// </summary>
    internal static class PartStatePoller
    {
        internal static void PollParachutes(Vessel v, Dictionary<uint, int> parachuteStates,
            double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var chute = p.FindModuleImplementing<ModuleParachute>();
                if (chute == null) continue;

                // Four-state classifier, so a repack emits ParachuteRepacked rather than
                // ParachuteCut. Stock ModuleParachute.Repack is externalToEVAOnly, so a repack is
                // performed BY an EVA kerbal ON another craft: on the background side that craft
                // is a loaded background vessel, which is why both recorders poll this.
                int state = FlightRecorder.ClassifyParachuteState(chute.deploymentState);

                var evt = FlightRecorder.CheckParachuteTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", state, parachuteStates, ut);
                if (evt.HasValue)
                {
                    sink.Events.Add(evt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId}{sink.EventSuffix()}");
                }
            }
        }

        /// <summary>
        /// Parsed <c>jettisonName</c> list for one jettison module, re-parsed only when the raw
        /// string changes. The two caches are the caller's per-vessel ones.
        /// </summary>
        // Component.gameObject and GameObject.activeInHierarchy are Unity ECalls: a method
        // that calls one directly cannot be JIT-compiled under the Windows .NET test host
        // (SecurityException on entry), so the read lives here, never inlined into the loop.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsTransformActiveInHierarchy(Transform t)
        {
            return t.gameObject.activeInHierarchy;
        }

        internal static string[] GetCachedJettisonNames(Dictionary<ulong, string> rawCache,
            Dictionary<ulong, string[]> parsedCache, ulong moduleKey, string rawNames)
        {
            rawNames = rawNames ?? string.Empty;
            string cachedRaw;
            if (rawCache.TryGetValue(moduleKey, out cachedRaw) &&
                string.Equals(cachedRaw, rawNames, StringComparison.Ordinal) &&
                parsedCache.TryGetValue(moduleKey, out string[] cachedNames))
            {
                return cachedNames;
            }

            string[] parsedNames = FlightRecorder.ParseJettisonNames(rawNames);
            rawCache[moduleKey] = rawNames;
            parsedCache[moduleKey] = parsedNames;
            return parsedNames;
        }

        internal static void PollJettisons(Vessel v, HashSet<uint> jettisonedShrouds,
            Dictionary<ulong, string> jettisonNameRawCache,
            Dictionary<ulong, string[]> parsedJettisonNamesCache, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                bool hasJettisonModule = false;
                bool isJettisoned = false;
                int jettisonModuleIndex = 0;
                // Parts with ModulePartVariants legitimately hide non-selected transforms
                // (e.g. Poodle DoubleBell hides Shroud2). The transform-visibility fallback
                // would misinterpret those as jettisoned, so skip it for variant parts.
                bool skipTransformFallback = p.FindModuleImplementing<ModulePartVariants>() != null;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var jettison = p.Modules[m] as ModuleJettison;
                    if (jettison == null) continue;
                    hasJettisonModule = true;
                    ulong moduleKey = FlightRecorder.EncodeEngineKey(p.persistentId, jettisonModuleIndex);
                    jettisonModuleIndex++;

                    // Primary state flag.
                    if (jettison.isJettisoned)
                    {
                        isJettisoned = true;
                        break;
                    }

                    // Fallback: if any configured jettison object is already hidden,
                    // treat as jettisoned. Skip for variant parts (see above).
                    if (skipTransformFallback)
                        continue;

                    string jettisonNames = jettison.jettisonName;
                    if (string.IsNullOrWhiteSpace(jettisonNames))
                        continue;

                    string[] names = GetCachedJettisonNames(
                        jettisonNameRawCache, parsedJettisonNamesCache, moduleKey, jettisonNames);
                    for (int n = 0; n < names.Length; n++)
                    {
                        Transform jt = p.FindModelTransform(names[n]);
                        if (jt != null && !IsTransformActiveInHierarchy(jt))
                        {
                            isJettisoned = true;
                            break;
                        }
                    }
                    if (isJettisoned)
                        break;
                }

                if (!hasJettisonModule) continue;

                var evt = FlightRecorder.CheckJettisonTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isJettisoned, jettisonedShrouds, ut);
                if (evt.HasValue)
                {
                    sink.Events.Add(evt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId}{sink.EventSuffix()}");
                }
            }
        }

        /// <summary>
        /// Converter on / off per part, then a stop for every running converter part that left
        /// the vessel (decouple, undock, death), or the recording reads it as running to its end.
        /// </summary>
        internal static void PollConverters(Vessel v, HashSet<uint> activeConverterParts,
            double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            int presentActive = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var evt = FlightRecorder.CheckConverterTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    FlightRecorder.IsAnyConverterActiveOnPart(p), activeConverterParts, ut);
                if (evt.HasValue)
                {
                    sink.Events.Add(evt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId}{sink.VesselFirstLabelSuffix("converter")}");
                }
                if (activeConverterParts.Contains(p.persistentId))
                    presentActive++;
            }

            FlightRecorder.AppendDepartedConverterStops(v, activeConverterParts, presentActive,
                sink.Events, ut, sink.Tag, sink.ConverterDepartureContext(v));
        }

        /// <summary>
        /// Wheel break / repair per part. On the background side this is the path an EVA repair
        /// takes: the engineer is the active vessel, so the rover he fixes is a loaded background
        /// vessel and only its poll can see <c>isDamaged</c> clear.
        /// </summary>
        internal static void PollWheelDamage(Vessel v, HashSet<uint> damagedWheelParts,
            double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var evt = FlightRecorder.CheckWheelDamageTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    FlightRecorder.IsAnyWheelDamagedOnPart(p), damagedWheelParts, ut);
                if (evt.HasValue)
                {
                    sink.Events.Add(evt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId}{sink.VesselFirstLabelSuffix("wheel damage")}");
                }
            }
        }

        internal static void PollCargoBays(Vessel v, HashSet<uint> openCargoBays,
            HashSet<uint> loggedDeployIndexIssues, HashSet<uint> loggedAnimationIssues,
            HashSet<uint> loggedClosedPositionIssues, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var cargo = p.FindModuleImplementing<ModuleCargoBay>();
                if (cargo == null) continue;

                // Resolve the linked ModuleAnimateGeneric via DeployModuleIndex
                int deployIdx = cargo.DeployModuleIndex;
                if (deployIdx < 0 || deployIdx >= p.Modules.Count)
                {
                    if (loggedDeployIndexIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose(sink.Tag, $"CargoBay: invalid DeployModuleIndex for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} deployIdx={deployIdx} modules={p.Modules.Count}");
                    }
                    continue;
                }
                var animModule = p.Modules[deployIdx] as ModuleAnimateGeneric;
                if (animModule == null)
                {
                    if (loggedAnimationIssues.Add(p.persistentId))
                    {
                        PartModule moduleAtIndex = p.Modules[deployIdx];
                        string moduleName = moduleAtIndex?.moduleName ?? "<null>";
                        ParsekLog.Verbose(sink.Tag, $"CargoBay: DeployModuleIndex did not resolve to ModuleAnimateGeneric for " +
                            $"'{p.partInfo?.name}' pid={p.persistentId} deployIdx={deployIdx} module='{moduleName}'");
                    }
                    continue;
                }

                FlightRecorder.ClassifyCargoBayState(animModule.animTime, cargo.closedPosition,
                    FlightRecorder.ResolveCargoBayDeployLimitStop(animModule, cargo.closedPosition),
                    out bool isOpen, out bool isClosed);

                if (!isOpen && !isClosed)
                {
                    if (cargo.closedPosition >= 0.1f && cargo.closedPosition <= 0.9f &&
                        loggedClosedPositionIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose(sink.Tag, $"CargoBay: unsupported closedPosition for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} closedPosition={cargo.closedPosition.ToString("F3", CultureInfo.InvariantCulture)} " +
                            $"animTime={animModule.animTime.ToString("F3", CultureInfo.InvariantCulture)}");
                    }
                    continue; // mid-transition or non-standard closedPosition
                }

                var evt = FlightRecorder.CheckCargoBayTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isOpen, openCargoBays, ut);
                if (evt.HasValue)
                {
                    sink.Events.Add(evt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId}{sink.EventSuffix()}");
                }
            }
        }

        internal static void PollFairings(Vessel v, HashSet<uint> deployedFairings,
            HashSet<uint> loggedReadFailures, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var fairing = p.FindModuleImplementing<ModuleProceduralFairing>();
                if (fairing == null) continue;

                bool isDeployed;
                try
                {
                    float scalar = fairing.GetScalar;
                    if (float.IsNaN(scalar) || float.IsInfinity(scalar)) continue;
                    isDeployed = scalar >= 0.5f;
                }
                catch (Exception ex)
                {
                    if (loggedReadFailures.Add(p.persistentId))
                    {
                        ParsekLog.Verbose(sink.Tag, $"Fairing: unable to read GetScalar for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} ({ex.GetType().Name}: {ex.Message})");
                    }
                    continue;
                }

                var evt = FlightRecorder.CheckFairingTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedFairings, ut);
                if (evt.HasValue)
                {
                    sink.Events.Add(evt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId}{sink.EventSuffix()}");
                }
            }
        }

        internal static void PollLadders(Vessel v, HashSet<ulong> deployedLadders,
            HashSet<ulong> loggedClassificationMisses, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyRetractableLadderState(module, out isDeployed, out isRetracted))
                    {
                        if (loggedClassificationMisses.Add(key))
                        {
                            ParsekLog.Verbose(sink.Tag, $"Ladder: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}{sink.ClassificationMissDetail(module)}");
                        }
                        continue;
                    }

                    var evt = FlightRecorder.CheckLadderTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedLadders, ut, m);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId}{sink.LabelSuffix("ladder")}");
                    }

                    break; // one RetractableLadder module per ladder part
                }
            }
        }

        internal static void PollAnimationGroups(Vessel v, HashSet<ulong> deployedAnimationGroups,
            HashSet<ulong> loggedClassificationMisses, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAnimationGroupState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (loggedClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose(sink.Tag, $"AnimationGroup: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}{sink.ClassificationMissDetail(module)}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAnimationGroups, ut, m);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex}{sink.LabelSuffix("animation-group")}");
                    }
                }
            }
        }

        internal static void PollAeroSurfaces(Vessel v, HashSet<ulong> deployedModules,
            HashSet<ulong> loggedClassificationMisses, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAeroSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (loggedClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose(sink.Tag, $"AeroSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}{sink.ClassificationMissDetail(module)}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedModules, ut, m);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex}{sink.LabelSuffix("aero-surface")}");
                    }
                }
            }
        }

        internal static void PollControlSurfaces(Vessel v, HashSet<ulong> deployedModules,
            HashSet<ulong> loggedClassificationMisses, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyControlSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (loggedClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose(sink.Tag, $"ControlSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}{sink.ClassificationMissDetail(module)}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedModules, ut, m);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex}{sink.LabelSuffix("control-surface")}");
                    }
                }
            }
        }

        internal static void PollRobotArmScanners(Vessel v, HashSet<ulong> deployedModules,
            HashSet<ulong> loggedClassificationMisses, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                        continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyRobotArmScannerState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (loggedClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose(sink.Tag, $"RobotArmScanner: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}{sink.ClassificationMissDetail(module)}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedModules, ut, m);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex}{sink.LabelSuffix("robot-arm-scanner")}");
                    }
                }
            }
        }

        internal static void PollAnimateHeat(Vessel v, Dictionary<ulong, HeatLevel> animateHeatLevels,
            HashSet<ulong> loggedClassificationMisses, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (!string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                        continue;

                    if (!FlightRecorder.TryClassifyAnimateHeatState(
                        module, out float normalizedHeat, out string sourceField))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (loggedClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose(sink.Tag, $"AnimateHeat: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}{sink.ClassificationMissDetail(module)}");
                        }
                        continue;
                    }

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimateHeatTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        normalizedHeat, animateHeatLevels, ut, m);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex}" +
                            sink.AnimateHeatEventSuffix(normalizedHeat, sourceField));
                    }
                }
            }
        }

        internal static void PollAnimateGeneric(Vessel v, HashSet<ulong> deployedAnimateGenericModules,
            HashSet<ulong> loggedClassificationMisses, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                // Avoid duplicate events for modules already tracked via dedicated handlers.
                if (FlightRecorder.HasDedicatedAnimateHandler(p)) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    ModuleAnimateGeneric animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!FlightRecorder.TryClassifyAnimateGenericState(animateModule, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (loggedClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose(sink.Tag, $"AnimateGeneric: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}{sink.AnimateGenericMissDetail(animateModule)}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAnimateGenericModules, ut, m);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex}{sink.LabelSuffix("anim-generic")}");
                    }
                }
            }
        }

        internal static void PollDeployables(Vessel v, HashSet<uint> brokenDeployables,
            HashSet<uint> extendedDeployables, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            int poselessDeployables = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable == null) continue;

                var ds = deployable.deployState;
                string partName = p.partInfo?.name ?? "unknown";
                bool isBroken = ds == ModuleDeployablePart.DeployState.BROKEN;

                // S6: BROKEN is a RECORDED state rather than a skipped one. This runs first
                // and owns both edges of it: entering (emit DeployableBroken, silently clear the
                // extended flag) and leaving via repair (emit DeployableRetracted). A background
                // station's panel snapping off is one of the visible cases, so both recorders
                // poll this edge.
                var brokenEvt = FlightRecorder.CheckDeployableBrokenTransition(
                    p.persistentId, partName, isBroken, brokenDeployables, extendedDeployables, ut);
                if (brokenEvt.HasValue)
                {
                    sink.Events.Add(brokenEvt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {brokenEvt.Value.eventType} '{brokenEvt.Value.partName}' " +
                        $"pid={brokenEvt.Value.partPersistentId}{sink.DeployableBrokenSuffix(ds)}");
                }

                // While BROKEN there is no extended/retracted opinion to form: the subtree is
                // gone, and the ordinary check would read "not extended" and emit a retract.
                if (isBroken) continue;

                // MIRROR of PartStateSeeder.SeedDeployables' pose-animation gate, and load-bearing
                // rather than cosmetic: with the seed removed, a pose-less module's first poll
                // would read EXTENDED against an empty set and emit the very DeployableExtended
                // the seed no longer writes, one frame later. Both ends read the same predicate.
                if (PartStateSeeder.DeployableHasNoPoseAnimation(deployable.animationName))
                {
                    poselessDeployables++;
                    continue;
                }

                // Transitional states: only fire on completed transitions.
                if (ds == ModuleDeployablePart.DeployState.EXTENDING) continue;
                if (ds == ModuleDeployablePart.DeployState.RETRACTING) continue;

                bool isExtended = ds == ModuleDeployablePart.DeployState.EXTENDED;

                var evt = FlightRecorder.CheckDeployableTransition(
                    p.persistentId, partName, isExtended, extendedDeployables, ut);
                if (evt.HasValue)
                {
                    sink.Events.Add(evt.Value);
                    ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId}{sink.EventSuffix()}");
                }
            }

            // Per-frame poll, so rate-limited and aggregated: one line, never one per panel.
            if (poselessDeployables > 0)
                ParsekLog.VerboseRateLimited(sink.Tag, sink.DeployableNoPoseRateKey(),
                    $"Deployable poll skipped {poselessDeployables} pose-animation-less module(s) " +
                    sink.DeployableNoPoseNote(), 60.0);
        }

        internal static void PollGear(Vessel v, HashSet<uint> deployedGear, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var wheel = p.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                    if (wheel == null) continue;

                    FlightRecorder.ClassifyGearState(wheel.stateString, out bool isDeployed, out bool isRetracted);

                    // Skip transitional states ("Deploying"/"Retracting"); only fire on endpoints
                    if (!isDeployed && !isRetracted) continue;

                    var evt = FlightRecorder.CheckGearTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedGear, ut);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId}{sink.EventSuffix()}");
                    }
                    break; // one deployment module per part
                }
            }
        }
    }
}
