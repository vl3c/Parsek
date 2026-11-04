using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Engine and RCS module caching, key encoding, power, debounce, state polling and terminal
    // events.
    public partial class FlightRecorder
    {
        internal static List<(Part part, ModuleEngines engine, int moduleIndex)> CacheEngineModules(Vessel v)
        {
            var result = new List<(Part, ModuleEngines, int)>();
            if (v == null || v.parts == null) return result;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                int midx = 0;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var eng = p.Modules[m] as ModuleEngines;
                    if (eng != null)
                    {
                        result.Add((p, eng, midx));
                        midx++;
                    }
                }
            }
            return result;
        }

        internal static ulong EncodeEngineKey(uint pid, int moduleIndex)
        {
            return ((ulong)pid << 8) | (uint)moduleIndex;
        }

        internal static void DecodeEngineKey(ulong key, out uint pid, out int moduleIndex)
        {
            pid = (uint)(key >> 8);
            moduleIndex = (int)(key & 0xFF);
        }

        /// <summary>
        /// Emits synthetic EngineShutdown, RCSStopped, and RoboticMotionStopped events
        /// for all engines/RCS/robotics still active when a recording ends (user stop,
        /// chain boundary, scene change). Without these, ghost plumes and FX may stay
        /// frozen at the last throttle level instead of shutting down at recording end.
        /// Bug #108.
        ///
        /// <para>
        /// S4 JOINS THE SAME CONTRACT via <paramref name="thrustingJetpackParts"/>, and it needs to
        /// for a reason the other families do not have: nothing else can close an EVA thrust pair.
        /// A kerbal switched away from mid-burst (a map click or a `[` press while a translate key
        /// is held) stops being polled by the flight recorder before the false-thrust edge lands,
        /// and the BACKGROUND recorder has no thrust wrapper at all — deliberately, since a
        /// background kerbal receives no input, so the edge can never fire there. An RCS pair left
        /// open at the same handoff is closed by the BG recorder's own CheckRcsState; an EVA thrust
        /// pair would simply stay open, leaving the ghost's plume lit for the rest of the recording
        /// and re-lit on every loop cycle. Optional so the existing call sites (and ~20 test cells)
        /// are unchanged; a null set is exactly the old behaviour.
        /// </para>
        ///
        /// <para>
        /// NONE of the four families' tracking state is mutated here — double-emit safety and
        /// post-emit lifetime are the CALLER's, uniformly across all four. See the jetpack block's
        /// own comment for why that matters more for S4 than for the other three.
        /// </para>
        /// </summary>
        internal static List<PartEvent> EmitTerminalEngineAndRcsEvents(
            HashSet<ulong> activeEngineKeys,
            HashSet<ulong> activeRcsKeys,
            HashSet<ulong> activeRoboticKeys,
            Dictionary<ulong, float> lastRoboticPosition,
            double finalUT,
            string logTag,
            HashSet<uint> thrustingJetpackParts = null)
        {
            var events = new List<PartEvent>();

            // Terminal EvaJetpackThrustStopped for a kerbal still mid-burst.
            //
            // THIS BLOCK DOES NOT MUTATE THE SET, and that is the engine/RCS/robotics convention
            // rather than an omission. The CALLERS own the lifetime: the rails site
            // (EmitTerminalEventsAndClearActiveState) clears every tracking set right after this
            // returns, so a second rails emit sees empty sets and the `Count > 0` gates above write
            // nothing; the STOP site (FinalizeRecordingState) deliberately leaves the sets intact so
            // ResumeAfterFalseAlarm can unwind an abandoned stop and keep tracking the burn that is
            // still running. Removing pids here broke exactly that unwind: after the resume the real
            // thrust-end edge hit `thrustingParts.Remove(pid) == false` in ProcessEvaThrustDebounce
            // and emitted nothing, leaving the pair open to recording end — the very failure the
            // terminal emit exists to prevent. Engines have never had a remove-on-emit and neither
            // does this now.
            if (thrustingJetpackParts != null && thrustingJetpackParts.Count > 0)
            {
                var pids = new List<uint>(thrustingJetpackParts);
                for (int i = 0; i < pids.Count; i++)
                {
                    events.Add(new PartEvent
                    {
                        ut = finalUT,
                        partPersistentId = pids[i],
                        eventType = PartEventType.EvaJetpackThrustStopped,
                        partName = "unknown"
                    });
                    ParsekLog.Verbose(logTag,
                        $"Terminal event: EvaJetpackThrustStopped pid={pids[i]} at " +
                        $"UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            // Terminal EngineShutdown for each active engine
            if (activeEngineKeys != null && activeEngineKeys.Count > 0)
            {
                // Snapshot keys before iterating (we don't modify the set here, but defensive)
                var keys = new List<ulong>(activeEngineKeys);
                for (int i = 0; i < keys.Count; i++)
                {
                    uint pid; int midx;
                    DecodeEngineKey(keys[i], out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = finalUT,
                        partPersistentId = pid,
                        eventType = PartEventType.EngineShutdown,
                        partName = "unknown",
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag,
                        $"Terminal event: EngineShutdown pid={pid} midx={midx} at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            // Terminal RCSStopped for each active RCS
            if (activeRcsKeys != null && activeRcsKeys.Count > 0)
            {
                var keys = new List<ulong>(activeRcsKeys);
                for (int i = 0; i < keys.Count; i++)
                {
                    uint pid; int midx;
                    DecodeEngineKey(keys[i], out pid, out midx);
                    events.Add(new PartEvent
                    {
                        ut = finalUT,
                        partPersistentId = pid,
                        eventType = PartEventType.RCSStopped,
                        partName = "unknown",
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag,
                        $"Terminal event: RCSStopped pid={pid} midx={midx} at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            // Terminal RoboticMotionStopped for each active robotic module
            if (activeRoboticKeys != null && activeRoboticKeys.Count > 0)
            {
                var keys = new List<ulong>(activeRoboticKeys);
                for (int i = 0; i < keys.Count; i++)
                {
                    uint pid; int midx;
                    DecodeEngineKey(keys[i], out pid, out midx);
                    float lastPos = 0f;
                    if (lastRoboticPosition != null)
                        lastRoboticPosition.TryGetValue(keys[i], out lastPos);
                    events.Add(new PartEvent
                    {
                        ut = finalUT,
                        partPersistentId = pid,
                        eventType = PartEventType.RoboticMotionStopped,
                        partName = "unknown",
                        value = lastPos,
                        moduleIndex = midx
                    });
                    ParsekLog.Verbose(logTag,
                        $"Terminal event: RoboticMotionStopped pid={pid} midx={midx} pos={lastPos.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }

            if (events.Count > 0)
            {
                ParsekLog.Info(logTag,
                    $"Emitted {events.Count} terminal events at UT={finalUT.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}" +
                    $" (engines={activeEngineKeys?.Count ?? 0}, rcs={activeRcsKeys?.Count ?? 0}, robotics={activeRoboticKeys?.Count ?? 0}" +
                    $", jetpack={thrustingJetpackParts?.Count ?? 0})");
            }

            return events;
        }

        internal static bool ShouldRecordEngineAsIgnited(
            bool engineIgnited, bool isOperational, float finalThrust)
        {
            return engineIgnited
                && isOperational
                && !float.IsNaN(finalThrust)
                && !float.IsInfinity(finalThrust)
                && finalThrust > EngineRecordedThrustEpsilon;
        }

        internal static float ComputeRecordedEnginePower(
            float currentThrottle, float finalThrust, float maxThrust)
        {
            if (!float.IsNaN(currentThrottle)
                && !float.IsInfinity(currentThrottle)
                && currentThrottle > EngineRecordedPowerEpsilon)
            {
                return Mathf.Clamp01(currentThrottle);
            }

            if (!float.IsNaN(finalThrust)
                && !float.IsInfinity(finalThrust)
                && finalThrust > EngineRecordedThrustEpsilon
                && !float.IsNaN(maxThrust)
                && !float.IsInfinity(maxThrust)
                && maxThrust > EngineRecordedThrustEpsilon)
            {
                return Mathf.Clamp01(finalThrust / maxThrust);
            }

            return 0f;
        }

        internal static void CheckEngineTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool ignited, float throttle,
            HashSet<ulong> activeSet, Dictionary<ulong, float> lastThrottleMap, double ut,
            List<PartEvent> events)
        {
            bool wasActive = activeSet.Contains(key);

            if (ignited && !wasActive)
            {
                if (PartStateSeeder.ShouldSkipZeroThrottleEngineSeed(throttle))
                {
                    lastThrottleMap.Remove(key);
                    ParsekLog.VerboseRateLimited("Recorder",
                        "engine-zero-throttle-transition-skip|" + key.ToString(CultureInfo.InvariantCulture),
                        $"Part event skipped: EngineIgnited pid={pid} midx={moduleIndex} throttle={throttle.ToString("F2", CultureInfo.InvariantCulture)} (idle engine, #165)",
                        5.0);
                    return;
                }

                activeSet.Add(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.EngineIgnited,
                    partName = partName,
                    value = throttle,
                    moduleIndex = moduleIndex
                });
                lastThrottleMap[key] = throttle;
            }
            else if (!ignited && wasActive)
            {
                activeSet.Remove(key);
                lastThrottleMap.Remove(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.EngineShutdown,
                    partName = partName,
                    moduleIndex = moduleIndex
                });
            }
            else if (ignited && wasActive)
            {
                float lastT;
                if (!lastThrottleMap.TryGetValue(key, out lastT))
                    lastT = 0f;

                float delta = throttle - lastT;
                if (delta > EngineThrottleDeadband || delta < -EngineThrottleDeadband)
                {
                    lastThrottleMap[key] = throttle;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = pid,
                        eventType = PartEventType.EngineThrottle,
                        partName = partName,
                        value = throttle,
                        moduleIndex = moduleIndex
                    });
                }
            }
        }

        /// <summary>
        /// Live half of the cached-module ownership guard: reads the four observable booleans off
        /// the KSP objects and hands them to the pure
        /// <see cref="DecideCachedModulePoll(bool, bool, bool, bool)"/> core.
        ///
        /// <para>
        /// <c>ReferenceEquals</c> is deliberate. Unity's overloaded <c>==</c> on
        /// <c>UnityEngine.Object</c> treats a destroyed-but-not-collected vessel as equal to
        /// <c>null</c>, which would make a destroyed recorded vessel compare equal to a destroyed
        /// foreign one. Identity here is instance identity, nothing more; the
        /// <paramref name="part"/>/<paramref name="module"/> null checks use Unity's operator
        /// because there "destroyed" genuinely means "nothing to read".
        /// </para>
        /// </summary>
        internal static CachedModulePollDecision DecideCachedModulePoll(
            Part part, PartModule module, Vessel recordedVessel)
        {
            bool hasPart = part != null;
            Vessel partVessel = hasPart ? part.vessel : null;
            return DecideCachedModulePoll(
                hasPart,
                module != null,
                hasPart && partVessel != null,
                hasPart && ReferenceEquals(partVessel, recordedVessel));
        }

        /// <summary>
        /// Logs (once per cached key) that a cached module entry was skipped because its part no
        /// longer belongs to the recorded vessel. Returns true when the entry should be polled.
        /// </summary>
        private bool ShouldPollCachedModule(
            Part part, PartModule module, Vessel recordedVessel, ulong key, string family)
        {
            var decision = DecideCachedModulePoll(part, module, recordedVessel);
            if (decision == CachedModulePollDecision.Poll)
                return true;

            if (decision == CachedModulePollDecision.SkipForeignVessel
                && loggedForeignCachedModuleKeys.Add(key))
            {
                uint pid; int midx;
                DecodeEngineKey(key, out pid, out midx);
                ParsekLog.Info("Recorder",
                    $"Cached {family} module dropped from poll: pid={pid} midx={midx} " +
                    $"'{part?.partInfo?.name ?? "unknown"}' is no longer on the recorded vessel " +
                    $"(recordedPid={recordedVessel?.persistentId ?? 0}) — events for it would have " +
                    $"landed in this recording");
            }

            return false;
        }

        private void CheckEngineState(Vessel v)
        {
            if (cachedEngines == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < cachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = cachedEngines[i];
                if (part == null || engine == null) continue;

                ulong key = EncodeEngineKey(part.persistentId, moduleIndex);
                if (!ShouldPollCachedModule(part, engine, v, key, "engine")) continue;
                bool ignited = ShouldRecordEngineAsIgnited(
                    engine.EngineIgnited, engine.isOperational, engine.finalThrust);
                float recordedPower = ComputeRecordedEnginePower(
                    engine.currentThrottle, engine.finalThrust, engine.maxThrust);
                if (loggedEngineModuleKeys.Add(key))
                {
                    int thrustTransformCount = engine.thrustTransforms != null
                        ? engine.thrustTransforms.Count
                        : 0;
                    string engineId = string.IsNullOrEmpty(engine.engineID) ? "<none>" : engine.engineID;
                    ParsekLog.Verbose("Recorder", $"Engine tracking: '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} id={engineId} thrustTransforms={thrustTransformCount}");
                }

                reusableEventBuffer.Clear();
                CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, recordedPower,
                    activeEngineKeys, lastThrottle, ut, reusableEventBuffer);

                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} val={reusableEventBuffer[e].value:F2}");

                    // Some engines with ModuleJettison (e.g. Mainsail/Skipper/Vector) can
                    // visually drop covers on ignition even when isJettisoned polling lags.
                    // Emit a one-shot shroud event on ignition as a reliable fallback.
                    if (reusableEventBuffer[e].eventType == PartEventType.EngineIgnited &&
                        part.FindModuleImplementing<ModuleJettison>() != null)
                    {
                        var shroudEvt = CheckJettisonTransition(
                            part.persistentId,
                            part.partInfo?.name ?? "unknown",
                            isJettisoned: true,
                            jettisonedSet: jettisonedShrouds,
                            ut: ut);
                        if (shroudEvt.HasValue)
                        {
                            PartEvents.Add(shroudEvt.Value);
                            ParsekLog.Info("Recorder", $"Part event: {shroudEvt.Value.eventType} '{shroudEvt.Value.partName}' " +
                                $"pid={shroudEvt.Value.partPersistentId} (engine-ignite fallback)");
                        }
                    }
                }
            }
        }

        internal static List<(Part part, ModuleRCS rcs, int moduleIndex)> CacheRcsModules(Vessel v)
        {
            var result = new List<(Part, ModuleRCS, int)>();
            if (v == null || v.parts == null) return result;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                int midx = 0;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var rcs = p.Modules[m] as ModuleRCS;
                    if (rcs != null)
                    {
                        result.Add((p, rcs, midx));
                        midx++;
                    }
                }
            }
            return result;
        }

        internal static float ComputeRcsPower(float[] thrustForces, float thrusterPower)
        {
            if (thrusterPower <= 0f || thrustForces == null || thrustForces.Length == 0)
                return 0f;

            float sum = 0f;
            for (int i = 0; i < thrustForces.Length; i++)
                sum += thrustForces[i];

            float power = sum / (thrusterPower * thrustForces.Length);
            if (power < 0f) power = 0f;
            if (power > 1f) power = 1f;
            return power;
        }

        /// <summary>
        /// Returns true when the RCS frame count has exactly reached the debounce threshold,
        /// meaning this is the first frame where recording should start.
        /// </summary>
        internal static bool ShouldStartRcsRecording(int frameCount, int threshold)
        {
            return frameCount == threshold;
        }

        /// <summary>
        /// Returns true when the RCS frame count is at or above the debounce threshold,
        /// meaning the activation was sustained and is being recorded.
        /// </summary>
        internal static bool IsRcsRecordingSustained(int frameCount, int threshold)
        {
            return frameCount >= threshold;
        }

        internal static void CheckRcsTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool active, float power,
            HashSet<ulong> activeSet, Dictionary<ulong, float> lastThrottleMap, double ut,
            List<PartEvent> events)
        {
            bool wasActive = activeSet.Contains(key);

            if (active && !wasActive)
            {
                activeSet.Add(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RCSActivated,
                    partName = partName,
                    value = power,
                    moduleIndex = moduleIndex
                });
                lastThrottleMap[key] = power;
            }
            else if (!active && wasActive)
            {
                activeSet.Remove(key);
                lastThrottleMap.Remove(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RCSStopped,
                    partName = partName,
                    moduleIndex = moduleIndex
                });
            }
            else if (active && wasActive)
            {
                float lastP;
                if (!lastThrottleMap.TryGetValue(key, out lastP))
                    lastP = 0f;

                float delta = power - lastP;
                if (delta > 0.05f || delta < -0.05f)
                {
                    lastThrottleMap[key] = power;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = pid,
                        eventType = PartEventType.RCSThrottle,
                        partName = partName,
                        value = power,
                        moduleIndex = moduleIndex
                    });
                }
            }
        }

        /// <summary>
        /// Shared RCS debounce state machine. Manages frame counting, debounce
        /// threshold crossing, throttle change detection, and micro-correction
        /// filtering. Returns any part events produced this frame.
        /// </summary>
        internal static void ProcessRcsDebounce(
            ulong key, uint pid, int moduleIndex, string partName,
            bool active, float[] thrustForces, float thrusterPower, double ut,
            Dictionary<ulong, int> frameCountMap,
            HashSet<ulong> activeSet, Dictionary<ulong, float> throttleMap,
            List<PartEvent> events)
        {
            if (active)
            {
                int count;
                frameCountMap.TryGetValue(key, out count);
                count++;
                frameCountMap[key] = count;

                if (ShouldStartRcsRecording(count, RcsDebounceFrameThreshold))
                {
                    // Debounce threshold crossed — emit RCSActivated
                    float power = ComputeRcsPower(thrustForces, thrusterPower);
                    CheckRcsTransition(
                        key, pid, moduleIndex, partName,
                        true, power, activeSet, throttleMap, ut, events);
                }
                else if (count > RcsDebounceFrameThreshold)
                {
                    // Already recording — check for throttle changes
                    float power = ComputeRcsPower(thrustForces, thrusterPower);
                    CheckRcsTransition(
                        key, pid, moduleIndex, partName,
                        true, power, activeSet, throttleMap, ut, events);
                }
                // else: count < threshold, still debouncing — skip
            }
            else
            {
                // RCS not active this frame
                int count;
                if (frameCountMap.TryGetValue(key, out count))
                {
                    if (IsRcsRecordingSustained(count, RcsDebounceFrameThreshold))
                    {
                        // Was a sustained activation — emit RCSStopped
                        CheckRcsTransition(
                            key, pid, moduleIndex, partName,
                            false, 0f, activeSet, throttleMap, ut, events);
                    }
                    else
                    {
                        // Filtered micro-correction — clean up any stale state
                        activeSet.Remove(key);
                        throttleMap.Remove(key);
                    }
                    frameCountMap.Remove(key);
                }
            }
        }

        private void CheckRcsState(Vessel v)
        {
            if (cachedRcsModules == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < cachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = cachedRcsModules[i];
                if (part == null || rcs == null) continue;

                ulong key = EncodeEngineKey(part.persistentId, moduleIndex);
                if (!ShouldPollCachedModule(part, rcs, v, key, "rcs")) continue;
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                if (loggedRcsModuleKeys.Add(key))
                {
                    int thrusterCount = rcs.thrusterTransforms != null ? rcs.thrusterTransforms.Count : 0;
                    int forceCount = rcs.thrustForces != null ? rcs.thrustForces.Length : 0;
                    ParsekLog.Verbose("Recorder", $"RCS tracking: '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} thrusters={thrusterCount} forces={forceCount} power={rcs.thrusterPower:F2}");
                }

                uint pid = part.persistentId;
                string partName = part.partInfo?.name ?? "unknown";

                reusableEventBuffer.Clear();
                ProcessRcsDebounce(
                    key, pid, moduleIndex, partName,
                    active, rcs.thrustForces, rcs.thrusterPower, ut,
                    rcsActiveFrameCount, activeRcsKeys, lastRcsThrottle, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} val={reusableEventBuffer[e].value:F2}");
                }
            }
        }
    }
}
