using System;
using System.Collections.Generic;
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
                        if (jt != null && !jt.gameObject.activeInHierarchy)
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
