using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Per-module visual state polling: parachutes, jettisons, deployables, converters, EVA,
    // lights, ladders, animations, gear, cargo bays, aero / control surfaces, robot arms,
    // animate-heat and fairings.
    public partial class FlightRecorder
    {
        /// <summary>
        /// Detect parachute state transitions over the four states
        /// <see cref="ClassifyParachuteState"/> produces: 0=stowed/active, 1=semi-deployed,
        /// 2=deployed, 3=cut. Stowed is the map's implicit default and removes the entry.
        ///
        /// The four-state split exists so CUT -> STOWED (a stock EVA <c>Repack()</c>) is
        /// distinguishable from DEPLOYED -> CUT. Under the old three-state encoding both looked
        /// like "N -> 0" and both emitted ParachuteCut, so playback hid the cap forever and a
        /// repacked chute rendered as an empty can.
        ///
        /// Transition table:
        ///   Stowed        -> Semi/Deployed : ParachuteSemiDeployed / ParachuteDeployed
        ///   Semi          -> Deployed      : ParachuteDeployed
        ///   Semi/Deployed -> Cut           : ParachuteCut
        ///   Cut           -> Stowed        : ParachuteRepacked   (the fix)
        ///   Cut           -> Semi/Deployed : ParachuteSemiDeployed / ParachuteDeployed
        ///   Semi/Deployed -> Stowed        : ParachuteCut  (defensive; not a stock transition,
        ///                                    preserves the pre-split behaviour for that shape)
        ///   Stowed        -> Cut           : NO EVENT — see below
        ///   same -> same                   : null
        ///
        /// Stowed -> Cut is not a physical transition (stock reaches CUT only from SEMIDEPLOYED or
        /// DEPLOYED). It is what the FIRST observation of an already-cut chute looks like, because
        /// an unseen pid defaults to Stowed: a recording that starts mid-descent after a cut, or a
        /// background vessel that enters loaded state carrying a cut chute. Emitting ParachuteCut
        /// there would be a spurious event at segment start, so the state is recorded silently —
        /// which still leaves the later Cut -> Stowed repack detectable. The ghost's build-time
        /// pose is stowed-with-cap either way, so no visual is lost.
        /// </summary>
        internal static PartEvent? CheckParachuteTransition(
            uint partPersistentId, string partName, int newState, Dictionary<uint, int> stateMap, double ut)
        {
            int oldState;
            if (!stateMap.TryGetValue(partPersistentId, out oldState))
                oldState = ParachuteStateStowed;

            if (newState == oldState)
                return null;

            if (newState != ParachuteStateStowed)
                stateMap[partPersistentId] = newState;
            else
                stateMap.Remove(partPersistentId);

            PartEventType? eventType = ClassifyParachuteTransitionEvent(oldState, newState);
            if (!eventType.HasValue)
                return null;

            return new PartEvent
            {
                ut = ut,
                partPersistentId = partPersistentId,
                eventType = eventType.Value,
                partName = partName
            };
        }

        /// <summary>
        /// Pure transition-to-event-type half of <see cref="CheckParachuteTransition"/>, split out
        /// so the table above is testable without a dictionary. Returns null when the transition
        /// carries no visual (same state, or first sight of an already-cut chute).
        /// </summary>
        internal static PartEventType? ClassifyParachuteTransitionEvent(int oldState, int newState)
        {
            if (newState == oldState)
                return null;

            // Any arrival at a canopy-out state is a deploy, wherever it came from (including a
            // re-deploy straight out of Cut, which stock only allows after a Repack but which
            // costs nothing to tolerate here).
            if (newState == ParachuteStateSemiDeployed)
                return PartEventType.ParachuteSemiDeployed;
            if (newState == ParachuteStateDeployed)
                return PartEventType.ParachuteDeployed;

            if (newState == ParachuteStateCut)
            {
                // First observation of an already-cut chute — not a transition. See the remarks
                // on CheckParachuteTransition.
                if (oldState == ParachuteStateStowed)
                    return null;
                return PartEventType.ParachuteCut;
            }

            // newState == Stowed.
            if (oldState == ParachuteStateCut)
                return PartEventType.ParachuteRepacked;

            // Semi/Deployed -> Stowed: not a stock path, but keep the pre-split verdict.
            return PartEventType.ParachuteCut;
        }

        private void CheckParachuteState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var chute = p.FindModuleImplementing<ModuleParachute>();
                if (chute == null) continue;

                int state = ClassifyParachuteState(chute.deploymentState);

                var evt = CheckParachuteTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", state, parachuteStates, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        internal static PartEvent? CheckJettisonTransition(
            uint partPersistentId, string partName, bool isJettisoned, HashSet<uint> jettisonedSet, double ut)
        {
            if (isJettisoned && jettisonedSet.Add(partPersistentId))
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ShroudJettisoned,
                    partName = partName
                };
            }

            return null;
        }

        private string[] GetCachedJettisonNames(ulong moduleKey, string rawNames)
        {
            rawNames = rawNames ?? string.Empty;
            string cachedRaw;
            if (jettisonNameRawCache.TryGetValue(moduleKey, out cachedRaw) &&
                string.Equals(cachedRaw, rawNames, StringComparison.Ordinal) &&
                parsedJettisonNamesCache.TryGetValue(moduleKey, out string[] cachedNames))
            {
                return cachedNames;
            }

            string[] parsedNames = ParseJettisonNames(rawNames);
            jettisonNameRawCache[moduleKey] = rawNames;
            parsedJettisonNamesCache[moduleKey] = parsedNames;
            return parsedNames;
        }

        internal static string[] ParseJettisonNames(string rawNames)
        {
            if (string.IsNullOrWhiteSpace(rawNames))
                return Array.Empty<string>();

            string[] split = rawNames.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 0)
                return Array.Empty<string>();

            var cleaned = new List<string>(split.Length);
            for (int i = 0; i < split.Length; i++)
            {
                string name = split[i].Trim();
                if (!string.IsNullOrEmpty(name))
                    cleaned.Add(name);
            }

            return cleaned.Count > 0 ? cleaned.ToArray() : Array.Empty<string>();
        }

        private void CheckJettisonState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    ulong moduleKey = EncodeEngineKey(p.persistentId, jettisonModuleIndex);
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

                    string[] names = GetCachedJettisonNames(moduleKey, jettisonNames);
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

                var evt = CheckJettisonTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isJettisoned, jettisonedShrouds, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        /// <summary>
        /// S6: the BROKEN edge of the ModuleDeployablePart state machine, which the recorder
        /// skipped outright before P8 — a snapped solar panel replayed intact for the rest of
        /// the recording, forever, on every replay.
        ///
        /// WHAT STOCK DOES, decompiled (ModuleDeployablePart, KSP 1.12.5). On the live break,
        /// <c>breakPanels()</c> detaches the <c>panelBreakTransform</c> subtree into its own
        /// physics object and sets <c>deployState = BROKEN</c>: the panel flies off. On any
        /// later load, <c>startFSM()</c> simply does
        /// <c>panelBreakTransform.gameObject.SetActive(false)</c>. Either way the net look is
        /// the same and it is what playback reproduces: the subtree is GONE.
        /// <c>breakName</c> defaults to <c>pivotName</c> when the config leaves it empty.
        ///
        /// AND IT IS REVERSIBLE. <c>eventRepairExternal</c> is active exactly while BROKEN, and
        /// <c>DoRepair()</c> returns the part to RETRACTED (it re-instantiates a fresh subtree
        /// from the prefab; our ghost only ever hid its own, so a plain un-hide is the faithful
        /// inverse). That is why this is a reversible-family member rather than a permanent one.
        ///
        /// THE TWO ORDERING RULES, which are the whole reason this is a separate pure function
        /// with its own truth table:
        /// <list type="number">
        /// <item><description>ENTERING broken must SILENTLY drop the pid from
        /// <paramref name="extendedSet"/>. A panel breaks while extended — that is the only way
        /// it usually breaks — so leaving the set alone would make the NEXT poll of a
        /// no-longer-extended part emit a spurious <c>DeployableRetracted</c>, and playback
        /// would animate the panel politely folding up before hiding it.</description></item>
        /// <item><description>LEAVING broken (a repair) must emit <c>DeployableRetracted</c>
        /// EXPLICITLY. Stock lands on RETRACTED, and playback needs a positive instruction to
        /// un-hide the subtree and snap it stowed; without one the ghost would keep rendering
        /// the panel as missing even though the recording says it was repaired.</description></item>
        /// </list>
        ///
        /// Budget: 0-2 events per flight. A panel breaks at most once per mission in practice,
        /// and repairs need an EVA kerbal carrying repair kits.
        /// </summary>
        internal static PartEvent? CheckDeployableBrokenTransition(
            uint partPersistentId, string partName, bool isBroken,
            HashSet<uint> brokenSet, HashSet<uint> extendedSet, double ut)
        {
            bool wasBroken = brokenSet.Contains(partPersistentId);

            if (isBroken && !wasBroken)
            {
                brokenSet.Add(partPersistentId);
                // Rule 1: drop the extended flag WITHOUT emitting a retract for it.
                extendedSet?.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableBroken,
                    partName = partName
                };
            }

            if (!isBroken && wasBroken)
            {
                brokenSet.Remove(partPersistentId);
                // Rule 2: a repair lands on RETRACTED, and playback needs to be told so.
                // The pid is deliberately NOT added to extendedSet: RETRACTED is the
                // not-extended state, so the ordinary extended/retracted poll picks up from
                // here correctly on the next frame.
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableRetracted,
                    partName = partName
                };
            }

            return null;
        }

        internal static PartEvent? CheckDeployableTransition(
            uint partPersistentId, string partName, bool isExtended, HashSet<uint> extendedSet, double ut)
        {
            bool wasExtended = extendedSet.Contains(partPersistentId);

            if (isExtended && !wasExtended)
            {
                extendedSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableExtended,
                    partName = partName
                };
            }

            if (!isExtended && wasExtended)
            {
                extendedSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableRetracted,
                    partName = partName
                };
            }

            return null;
        }

        private void CheckDeployableState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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

                // S6: BROKEN is now a RECORDED state rather than a skipped one. This runs first
                // and owns both edges of it — entering (emit DeployableBroken, silently clear the
                // extended flag) and leaving via repair (emit DeployableRetracted).
                var brokenEvt = CheckDeployableBrokenTransition(
                    p.persistentId, partName, isBroken, brokenDeployables, extendedDeployables, ut);
                if (brokenEvt.HasValue)
                {
                    PartEvents.Add(brokenEvt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {brokenEvt.Value.eventType} '{brokenEvt.Value.partName}' " +
                        $"pid={brokenEvt.Value.partPersistentId} (deployable-broken edge, deployState={ds})");
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

                // Transitional states — only fire on completed transitions.
                if (ds == ModuleDeployablePart.DeployState.EXTENDING) continue;
                if (ds == ModuleDeployablePart.DeployState.RETRACTING) continue;

                bool isExtended = ds == ModuleDeployablePart.DeployState.EXTENDED;

                var evt = CheckDeployableTransition(
                    p.persistentId, partName, isExtended, extendedDeployables, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }

            // Per-frame poll, so rate-limited and aggregated: one line, never one per panel.
            if (poselessDeployables > 0)
                ParsekLog.VerboseRateLimited("Recorder", "deployable-no-pose-animation",
                    $"Deployable poll skipped {poselessDeployables} pose-animation-less module(s) " +
                    "(deployable-no-pose-animation gate)", 60.0);
        }

        /// <summary>
        /// S7: whether ANY <c>BaseConverter</c> on this part is currently running.
        ///
        /// Base-class keying, the same choice <see cref="RebuildHarvestConverterCache"/> makes and
        /// for the same reason: it covers <c>ModuleResourceHarvester</c>,
        /// <c>ModuleResourceConverter</c>, the asteroid / comet drills and modded derivatives with
        /// no module-name list to maintain. <c>BaseConverter.IsActivated</c> is a
        /// <c>[KSPField(isPersistant = true)] public bool</c> (verified by decompile, KSP 1.12.5),
        /// so it is readable, persistent and rails-safe.
        ///
        /// ANY rather than ALL because the visual is per-PART: one ModuleAnimationGroup drives one
        /// running animation on the part, so a single active converter is enough to make the drill
        /// spin. A Unity-destroyed module (a staged-away drill) counts as inactive.
        /// </summary>
        internal static bool IsAnyConverterActiveOnPart(Part p)
        {
            if (p?.Modules == null) return false;
            for (int m = 0; m < p.Modules.Count; m++)
            {
                if (p.Modules[m] is BaseConverter converter
                    && converter != null
                    && converter.IsActivated)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// S7: the per-part converter on/off edge, which produced NO PartEvent at all before P8 —
        /// so a mining base replayed with a dead-still drill and an ISRU that never moved, even
        /// though the recording knew the harvest was running (the vessel-scoped
        /// <see cref="PollHarvestActivity"/> window is a resource-attribution device and emits no
        /// part event).
        ///
        /// Playback turns these into the part's ModuleAnimationGroup RUNNING animation
        /// (RadialDrill's Drill_Running, the large ISRU's ProcessorLarge_running), driven as a pure
        /// function of elapsed recorded UT.
        ///
        /// Budget: 2 events per mining session. The state is a player toggle, not a continuous
        /// signal, so there is nothing to debounce.
        /// </summary>
        internal static PartEvent? CheckConverterTransition(
            uint partPersistentId, string partName, bool isActive,
            HashSet<uint> activeConverterParts, double ut)
        {
            bool wasActive = activeConverterParts.Contains(partPersistentId);

            if (isActive && !wasActive)
            {
                activeConverterParts.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ConverterActivated,
                    partName = partName
                };
            }

            if (!isActive && wasActive)
            {
                activeConverterParts.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ConverterDeactivated,
                    partName = partName
                };
            }

            return null;
        }

        /// <summary>
        /// Pure: the ConverterDeactivated stops for tracked running converter parts that are no
        /// longer on the recorded vessel. A running converter part that leaves by decouple,
        /// undock, EVA-construction detach or death gets no off edge from the poll (the poll
        /// walks only the vessel's own parts), so without a stop the recording reads the
        /// converter as running to its end and the tail-trim converter witness
        /// (<see cref="RecordingOptimizer.CountConvertersRunningAtEnd"/>) never lets the tail
        /// trim. The part's own new recording seeds its running state from the live part.
        ///
        /// Removes every departed pid from <paramref name="activeConverterParts"/>. The part
        /// name comes from the latest event in <paramref name="recordedEvents"/> naming that
        /// pid ("unknown" when none). Stops are returned in pid order so the output is
        /// deterministic. Returns an empty list when nothing departed or when
        /// <paramref name="presentPartPids"/> is empty (a vessel read with no parts proves
        /// nothing about departure).
        /// </summary>
        internal static List<PartEvent> CollectDepartedConverterStops(
            HashSet<uint> activeConverterParts, HashSet<uint> presentPartPids,
            List<PartEvent> recordedEvents, double ut)
        {
            var stops = new List<PartEvent>();
            if (activeConverterParts == null || activeConverterParts.Count == 0
                || presentPartPids == null || presentPartPids.Count == 0)
                return stops;

            List<uint> departed = null;
            foreach (uint pid in activeConverterParts)
            {
                if (presentPartPids.Contains(pid)) continue;
                if (departed == null) departed = new List<uint>();
                departed.Add(pid);
            }
            if (departed == null)
                return stops;

            departed.Sort();
            for (int d = 0; d < departed.Count; d++)
            {
                uint pid = departed[d];
                activeConverterParts.Remove(pid);

                string name = null;
                bool destroyed = false;
                if (recordedEvents != null)
                {
                    for (int i = recordedEvents.Count - 1; i >= 0; i--)
                    {
                        if (recordedEvents[i].partPersistentId != pid) continue;
                        if (recordedEvents[i].eventType == PartEventType.Destroyed)
                            destroyed = true;
                        if (name == null && !string.IsNullOrEmpty(recordedEvents[i].partName))
                            name = recordedEvents[i].partName;
                    }
                }

                // A destroyed part already ends its run (Destroyed is final for the tail-trim
                // witness); a second edge would only read as a player toggle to the idle-on-pad
                // veto (ParsekFlight.CountPlayerConverterToggles).
                if (destroyed)
                    continue;

                stops.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.ConverterDeactivated,
                    partName = name ?? "unknown"
                });
            }

            return stops;
        }

        /// <summary>
        /// Live half shared by the flight and background converter polls: when fewer tracked
        /// running converter pids were seen on the vessel this poll than the tracking set holds,
        /// collects the vessel's part pids and appends a stop for each departed one (see
        /// <see cref="CollectDepartedConverterStops"/>). The common case (nothing departed) costs
        /// one integer compare. Logs one Info line per departure poll (bounded: departures are
        /// rare structural events).
        /// </summary>
        internal static int AppendDepartedConverterStops(
            Vessel v, HashSet<uint> activeConverterParts, int presentActiveCount,
            List<PartEvent> recordedEvents, double ut, string logTag, string context)
        {
            if (activeConverterParts == null || presentActiveCount >= activeConverterParts.Count)
                return 0;
            if (v == null || v.parts == null || v.parts.Count == 0 || recordedEvents == null)
                return 0;

            var present = new HashSet<uint>();
            for (int i = 0; i < v.parts.Count; i++)
                if (v.parts[i] != null) present.Add(v.parts[i].persistentId);

            return AppendDepartedConverterStopsCore(
                activeConverterParts, present, recordedEvents, ut, logTag, context);
        }

        /// <summary>
        /// Testable core of <see cref="AppendDepartedConverterStops"/>: appends the stops from
        /// <see cref="CollectDepartedConverterStops"/> to <paramref name="recordedEvents"/> and
        /// logs one Info summary when any were written. Returns the number written.
        /// </summary>
        internal static int AppendDepartedConverterStopsCore(
            HashSet<uint> activeConverterParts, HashSet<uint> presentPartPids,
            List<PartEvent> recordedEvents, double ut, string logTag, string context)
        {
            if (recordedEvents == null)
                return 0;
            List<PartEvent> stops = CollectDepartedConverterStops(
                activeConverterParts, presentPartPids, recordedEvents, ut);
            if (stops.Count == 0)
                return 0;

            recordedEvents.AddRange(stops);
            var pids = new System.Text.StringBuilder();
            for (int i = 0; i < stops.Count; i++)
            {
                if (i > 0) pids.Append(',');
                pids.Append(stops[i].partPersistentId.ToString(CultureInfo.InvariantCulture));
            }
            ParsekLog.Info(logTag,
                $"Converter part(s) left the vessel while running: {context} " +
                $"stopped={stops.Count.ToString(CultureInfo.InvariantCulture)} pids=[{pids}] " +
                $"ut={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"(ConverterDeactivated written so this recording no longer reads them as running)");
            return stops.Count;
        }

        /// <summary>
        /// S7 flight-side poll. DELIBERATELY SEPARATE from the harvest-window machinery
        /// (<see cref="PollHarvestActivity"/>), which is vessel-scoped, owns rails-entry /
        /// rails-exit funnels and exists to attribute harvested resources. This one is per-PART and
        /// needs none of that: the background recorder gets the identical wrapper with no window
        /// bookkeeping to replicate.
        /// </summary>
        private void CheckConverterState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            int presentActive = 0;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var evt = CheckConverterTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    IsAnyConverterActiveOnPart(p), activeConverterParts, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (converter)");
                }
                if (activeConverterParts.Contains(p.persistentId))
                    presentActive++;
            }

            AppendDepartedConverterStops(v, activeConverterParts, presentActive, PartEvents, ut,
                "Recorder", $"vessel pid={v.persistentId}");
        }

        /// <summary>
        /// Whether ANY stock <c>ModuleWheelDamage</c> on this part is broken. <c>isDamaged</c> is a
        /// <c>[KSPField(isPersistant = true)] public bool</c> (decompiled, KSP 1.12.5), so it is
        /// readable on any loaded part and rides the vessel snapshot too. Stock fires no GameEvent
        /// on the break itself (<c>SetDamaged(true)</c> fires <c>onPartRepaired</c>, oddly, and the
        /// repair fires <c>onWheelRepaired</c>), so the flag is POLLED, the same way the deployable
        /// BROKEN edge is. A Unity-destroyed module reads as intact.
        /// </summary>
        internal static bool IsAnyWheelDamagedOnPart(Part p)
        {
            if (p?.Modules == null) return false;
            for (int m = 0; m < p.Modules.Count; m++)
            {
                if (p.Modules[m] is ModuleWheels.ModuleWheelDamage damage
                    && damage != null
                    && damage.isDamaged)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The wheel break / repair edge. A plain reversible two-state signal, per PART: stock
        /// swaps two named transforms (<c>undamagedTransformName</c> off, <c>damagedTransformName</c>
        /// on) and the EVA repair swaps them back. Nothing else in the recorder reads this set, so
        /// unlike the deployable BROKEN edge there is no precedence dance with another family.
        ///
        /// Budget: 0-2 events per wheel per flight. A wheel breaks from an over-stress or a hard
        /// landing, and a repair needs an engineer on EVA carrying repair kits.
        /// </summary>
        internal static PartEvent? CheckWheelDamageTransition(
            uint partPersistentId, string partName, bool isDamaged,
            HashSet<uint> damagedSet, double ut)
        {
            if (damagedSet == null) return null;
            bool wasDamaged = damagedSet.Contains(partPersistentId);

            if (isDamaged && !wasDamaged)
            {
                damagedSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.WheelBroken,
                    partName = partName
                };
            }

            if (!isDamaged && wasDamaged)
            {
                damagedSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.WheelRepaired,
                    partName = partName
                };
            }

            return null;
        }

        /// <summary>Flight-side wheel damage poll. The background recorder has the twin wrapper.</summary>
        private void CheckWheelDamageState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var evt = CheckWheelDamageTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    IsAnyWheelDamagedOnPart(p), damagedWheelParts, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (wheel damage)");
                }
            }
        }

        /// <summary>
        /// S4: the EVA jetpack's deploy / stow edge.
        ///
        /// <c>KerbalEVA.JetpackDeployed</c> is a clean two-state player action, so this is a plain
        /// edge with no debounce. It IS a <c>[KSPField(isPersistant = true)]</c> — so it also rides
        /// the quicksave, which is why a ghost spawned from a snapshot can read the pose without an
        /// event. The event is what makes the CHANGE visible mid-recording.
        /// </summary>
        internal static PartEvent? CheckEvaJetpackTransition(
            uint partPersistentId, string partName, bool deployed,
            HashSet<uint> jetpackDeployedParts, double ut)
        {
            bool wasDeployed = jetpackDeployedParts.Contains(partPersistentId);

            if (deployed && !wasDeployed)
            {
                jetpackDeployedParts.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.EvaJetpackDeployed,
                    partName = partName
                };
            }

            if (!deployed && wasDeployed)
            {
                jetpackDeployedParts.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.EvaJetpackStowed,
                    partName = partName
                };
            }

            return null;
        }

        /// <summary>
        /// S4: the EVA ragdoll edge. <c>KerbalEVA.isRagdoll</c> is set in the FSM's ragdoll-enter
        /// state and cleared on the timed recover-complete event (decompiled, KSP 1.12.5), so it is
        /// a clean edge that needs no debounce either.
        ///
        /// EVENTS ONLY, and that asymmetry is deliberate rather than unfinished: the ragdoll POSE is
        /// a physics outcome with no animation clip to sample, so a replayed pose would be invention
        /// rather than recording. What the events buy is real: they gate the thrust plume (a
        /// tumbling kerbal is not flying) and they mark the timeline.
        /// </summary>
        internal static PartEvent? CheckEvaRagdollTransition(
            uint partPersistentId, string partName, bool ragdoll,
            HashSet<uint> ragdollParts, double ut)
        {
            bool wasRagdoll = ragdollParts.Contains(partPersistentId);

            if (ragdoll && !wasRagdoll)
            {
                ragdollParts.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.EvaRagdollStarted,
                    partName = partName
                };
            }

            if (!ragdoll && wasRagdoll)
            {
                ragdollParts.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.EvaRagdollEnded,
                    partName = partName
                };
            }

            return null;
        }

        /// <summary>
        /// S4: the EVA jetpack THRUST edge, debounced on the same frame threshold as RCS
        /// (<see cref="RcsDebounceFrameThreshold"/>).
        ///
        /// WHY THIS ONE NEEDS A DEBOUNCE WHILE THE OTHER TWO DO NOT.
        /// <c>KerbalEVA.JetpackIsThrusting</c> is not a state a player sets — it is RECOMPUTED every
        /// FixedUpdate as <c>fuelFlowRate &gt; PropellantConsumption / 2 * thrustPercentage * 0.01</c>
        /// (decompiled, KSP 1.12.5). A single tap of a translation key, or fuel flow hovering at the
        /// comparison boundary, flips it on and off across consecutive frames. Recorded raw, one
        /// kerbal nudging himself into a hatch would write dozens of start/stop pairs, which is
        /// exactly the noise the RCS debounce exists to filter — so it reuses that threshold rather
        /// than inventing a second number to keep in step.
        ///
        /// Binary rather than power-valued: a jetpack has no throttle a player can set, so
        /// <c>value = 1.0</c> on the start event says "full", and the plume needs no magnitude.
        ///
        /// Budget: 2 events per sustained thrust burst.
        /// </summary>
        internal static void ProcessEvaThrustDebounce(
            uint partPersistentId, string partName, bool thrusting, double ut,
            Dictionary<uint, int> thrustFrameCounts, HashSet<uint> thrustingParts,
            List<PartEvent> events)
        {
            if (thrusting)
            {
                int count;
                thrustFrameCounts.TryGetValue(partPersistentId, out count);
                count++;
                thrustFrameCounts[partPersistentId] = count;

                // Only the frame that EXACTLY reaches the threshold emits. Past it there is nothing
                // to say: unlike RCS there is no continuous power to re-report.
                if (ShouldStartRcsRecording(count, RcsDebounceFrameThreshold)
                    && thrustingParts.Add(partPersistentId))
                {
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = partPersistentId,
                        eventType = PartEventType.EvaJetpackThrustStarted,
                        partName = partName,
                        value = 1f
                    });
                }
                return;
            }

            int held;
            if (!thrustFrameCounts.TryGetValue(partPersistentId, out held))
                return;

            thrustFrameCounts.Remove(partPersistentId);
            if (IsRcsRecordingSustained(held, RcsDebounceFrameThreshold))
            {
                if (thrustingParts.Remove(partPersistentId))
                {
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = partPersistentId,
                        eventType = PartEventType.EvaJetpackThrustStopped,
                        partName = partName
                    });
                }
                return;
            }

            // A filtered micro-tap: nothing was ever emitted, so clear any stale membership
            // rather than emitting an unpaired stop.
            thrustingParts.Remove(partPersistentId);
        }

        /// <summary>
        /// S4 flight-side poll for the three KerbalEVA signals.
        ///
        /// TYPED CASTS, not reflection, and the reason is measurable: of the three members only
        /// <c>JetpackDeployed</c> carries <c>[KSPField]</c> — <c>JetpackIsThrusting</c> and
        /// <c>isRagdoll</c> are plain public fields (decompiled, KSP 1.12.5). A
        /// <c>module.Fields</c> walk, which is how the recorder reads modules whose types it cannot
        /// reference, sees only KSPFields and would have silently missed two of the three.
        ///
        /// The <c>v.isEVA</c> gate is what keeps this free: a rocket pays one bool read per frame
        /// and never walks its parts. EVA vessels ARE recorded — the VesselType.EVA filters
        /// elsewhere in this file are docking-ANCHOR-candidate filters, not recording gates (they
        /// also exclude LANDED / SPLASHED / PRELAUNCH, which no recording gate would).
        /// </summary>
        private void CheckEvaState(Vessel v)
        {
            if (v == null || !v.isEVA || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var eva = p.FindModuleImplementing<KerbalEVA>();
                if (eva == null) continue;

                string partName = p.partInfo?.name ?? "unknown";
                uint pid = p.persistentId;

                var jetpackEvt = CheckEvaJetpackTransition(
                    pid, partName, eva.JetpackDeployed, jetpackDeployedParts, ut);
                if (jetpackEvt.HasValue)
                {
                    PartEvents.Add(jetpackEvt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {jetpackEvt.Value.eventType} '{partName}' pid={pid} (eva)");
                }

                var ragdollEvt = CheckEvaRagdollTransition(
                    pid, partName, eva.isRagdoll, ragdollParts, ut);
                if (ragdollEvt.HasValue)
                {
                    PartEvents.Add(ragdollEvt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {ragdollEvt.Value.eventType} '{partName}' pid={pid} (eva)");
                }

                reusableEventBuffer.Clear();
                ProcessEvaThrustDebounce(
                    pid, partName, eva.JetpackIsThrusting, ut,
                    evaThrustFrameCounts, thrustingJetpackParts, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {reusableEventBuffer[e].eventType} '{partName}' " +
                        $"pid={pid} (eva thrust, debounce={RcsDebounceFrameThreshold} frames)");
                }
            }
        }

        internal static PartEvent? CheckLightTransition(
            uint partPersistentId, string partName, bool isOn, HashSet<uint> lightsOnSet, double ut)
        {
            bool wasOn = lightsOnSet.Contains(partPersistentId);

            if (isOn && !wasOn)
            {
                lightsOnSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightOn,
                    partName = partName
                };
            }

            if (!isOn && wasOn)
            {
                lightsOnSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightOff,
                    partName = partName
                };
            }

            return null;
        }

        internal static void CheckLightBlinkTransition(
            uint partPersistentId, string partName, bool isBlinking, float blinkRate,
            HashSet<uint> blinkingSet, Dictionary<uint, float> blinkRateMap, double ut,
            List<PartEvent> events)
        {
            bool wasBlinking = blinkingSet.Contains(partPersistentId);
            float safeBlinkRate = blinkRate > 0f ? blinkRate : 1f;

            if (isBlinking && !wasBlinking)
            {
                blinkingSet.Add(partPersistentId);
                blinkRateMap[partPersistentId] = safeBlinkRate;
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightBlinkEnabled,
                    partName = partName,
                    value = safeBlinkRate
                });
                return;
            }

            if (!isBlinking && wasBlinking)
            {
                blinkingSet.Remove(partPersistentId);
                blinkRateMap.Remove(partPersistentId);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.LightBlinkDisabled,
                    partName = partName
                });
                return;
            }

            if (isBlinking && wasBlinking)
            {
                float lastRate;
                if (!blinkRateMap.TryGetValue(partPersistentId, out lastRate))
                    lastRate = safeBlinkRate;

                if (Math.Abs(safeBlinkRate - lastRate) >= 0.01f)
                {
                    blinkRateMap[partPersistentId] = safeBlinkRate;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = partPersistentId,
                        eventType = PartEventType.LightBlinkRate,
                        partName = partName,
                        value = safeBlinkRate
                    });
                }
            }
        }

        internal static PartEvent? CheckLadderTransition(
            ulong key, uint partPersistentId, string partName, bool isExtended,
            HashSet<ulong> deployedSet, double ut, int moduleIndex)
        {
            bool wasExtended = deployedSet.Contains(key);

            if (isExtended && !wasExtended)
            {
                deployedSet.Add(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableExtended,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            if (!isExtended && wasExtended)
            {
                deployedSet.Remove(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableRetracted,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            return null;
        }

        internal static PartEvent? CheckAnimationGroupTransition(
            ulong key, uint partPersistentId, string partName, bool isDeployed,
            HashSet<ulong> deployedSet, double ut, int moduleIndex)
        {
            bool wasDeployed = deployedSet.Contains(key);

            if (isDeployed && !wasDeployed)
            {
                deployedSet.Add(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableExtended,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            if (!isDeployed && wasDeployed)
            {
                deployedSet.Remove(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.DeployableRetracted,
                    partName = partName,
                    moduleIndex = moduleIndex
                };
            }

            return null;
        }

        internal static PartEvent? CheckAnimateHeatTransition(
            ulong key, uint partPersistentId, string partName,
            float normalizedHeat,
            Dictionary<ulong, HeatLevel> levelMap, double ut, int moduleIndex)
        {
            HeatLevel current;
            if (!levelMap.TryGetValue(key, out current))
                current = HeatLevel.Cold;

            // Determine target level with hysteresis gaps:
            // Cold-Medium: rise at 0.40, fall at 0.35 (gap [0.35, 0.40) preserves current)
            // Medium-Hot:  rise at 0.80, fall at 0.75 (gap [0.75, 0.80) preserves current)
            HeatLevel target;
            if (normalizedHeat >= AnimateHeatHotThreshold)
                target = HeatLevel.Hot;
            else if (normalizedHeat >= AnimateHeatMediumThreshold)
            {
                // In the Medium zone — but check if falling from Hot with hysteresis
                if (current == HeatLevel.Hot && normalizedHeat >= AnimateHeatHotFallbackThreshold)
                    target = HeatLevel.Hot; // stay Hot in [0.75, 0.80) gap
                else
                    target = HeatLevel.Medium;
            }
            else if (normalizedHeat >= AnimateHeatMediumFallbackThreshold)
            {
                // In the hysteresis gap [0.35, 0.40) — keep current level
                target = current;
            }
            else
                target = HeatLevel.Cold;

            if (target == current)
                return null;

            levelMap[key] = target;

            PartEventType eventType;
            switch (target)
            {
                case HeatLevel.Hot:
                    eventType = PartEventType.ThermalAnimationHot;
                    break;
                case HeatLevel.Medium:
                    eventType = PartEventType.ThermalAnimationMedium;
                    break;
                default:
                    eventType = PartEventType.ThermalAnimationCold;
                    break;
            }

            ParsekLog.Verbose("Recorder",
                $"[Recording][AnimateHeat] Part '{partName}' pid={partPersistentId}: " +
                $"heat level {current} -> {target} (normalizedHeat={normalizedHeat:F3})");

            return new PartEvent
            {
                ut = ut,
                partPersistentId = partPersistentId,
                eventType = eventType,
                partName = partName,
                value = normalizedHeat,
                moduleIndex = moduleIndex
            };
        }

        private void CheckLightState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                bool hasModuleLight = false;
                var light = p.FindModuleImplementing<ModuleLight>();
                if (light != null)
                {
                    hasModuleLight = true;
                    var evt = CheckLightTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", light.isOn, lightsOn, ut);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                    }

                    reusableEventBuffer.Clear();
                    CheckLightBlinkTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown",
                        light.isBlinking, light.blinkRate,
                        blinkingLights, lightBlinkRates, ut, reusableEventBuffer);
                    for (int e = 0; e < reusableEventBuffer.Count; e++)
                    {
                        PartEvents.Add(reusableEventBuffer[e]);
                        ParsekLog.Verbose("Recorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                            $"pid={reusableEventBuffer[e].partPersistentId} val={reusableEventBuffer[e].value:F2}");
                    }
                }

                // Also check ModuleColorChanger with toggleInFlight=true (cabin lights).
                // Parts with both ModuleLight and ModuleColorChanger toggle together via the
                // Light action group, so the lightsOn HashSet deduplicates via partPersistentId.
                // For parts with ONLY ModuleColorChanger, this produces the LightOn/LightOff events.
                if (!hasModuleLight)
                {
                    bool foundToggleableColorChanger = false;
                    var modules = p.Modules;
                    for (int mi = 0; mi < modules.Count; mi++)
                    {
                        var cc = modules[mi] as ModuleColorChanger;
                        if (cc == null) continue;
                        if (!cc.toggleInFlight) continue;

                        foundToggleableColorChanger = true;
                        var ccEvt = CheckLightTransition(
                            p.persistentId, p.partInfo?.name ?? "unknown", cc.animState, lightsOn, ut);
                        if (ccEvt.HasValue)
                        {
                            PartEvents.Add(ccEvt.Value);
                            ParsekLog.Verbose("Recorder",
                                $"ColorChanger state change: pid={p.persistentId} animState={cc.animState} " +
                                $"event={ccEvt.Value.eventType} '{ccEvt.Value.partName}'");
                        }
                        break; // Only one toggleable ColorChanger per part matters
                    }

                    if (!foundToggleableColorChanger)
                        continue;
                }
            }
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyRetractableLadderStateFromFieldValues{TFields}"/> core, which owns
        /// the probed field names and the fallback order.
        /// </summary>
        internal static bool TryClassifyRetractableLadderState(
            PartModule ladderModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (ladderModule == null) return false;

            return TryClassifyRetractableLadderStateFromFieldValues(
                new LegacyPartModuleFieldValues(ladderModule), out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyAnimationGroupStateFromFieldValues{TFields}"/> core.
        /// </summary>
        internal static bool TryClassifyAnimationGroupState(
            PartModule animationGroupModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (animationGroupModule == null) return false;

            return TryClassifyAnimationGroupStateFromFieldValues(
                new LegacyPartModuleFieldValues(animationGroupModule),
                out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Reflection probe half: reads the typed animTime off the module and hands both it and the
        /// module to the pure <see cref="TryClassifyAnimateGenericStateFromFieldValues{TFields}"/> core.
        /// </summary>
        internal static bool TryClassifyAnimateGenericState(
            ModuleAnimateGeneric animateModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (animateModule == null) return false;

            return TryClassifyAnimateGenericStateFromFieldValues(
                new LegacyPartModuleFieldValues(animateModule), animateModule.animTime,
                out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyAeroSurfaceStateFromFieldValues{TFields}"/> core.
        /// </summary>
        internal static bool TryClassifyAeroSurfaceState(
            PartModule aeroSurfaceModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (aeroSurfaceModule == null) return false;

            return TryClassifyAeroSurfaceStateFromFieldValues(
                new PartModuleFieldValues(aeroSurfaceModule), out isDeployed, out isRetracted);
        }

        internal static bool TryClassifyControlSurfaceState(
            PartModule controlSurfaceModule, out bool isDeployed, out bool isRetracted)
        {
            // ModuleControlSurface exposes similar fields/deflection semantics to ModuleAeroSurface.
            return TryClassifyAeroSurfaceState(controlSurfaceModule, out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyRobotArmScannerStateFromFieldValues{TFields}"/> core.
        /// </summary>
        internal static bool TryClassifyRobotArmScannerState(
            PartModule robotArmScannerModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (robotArmScannerModule == null) return false;

            return TryClassifyRobotArmScannerStateFromFieldValues(
                new PartModuleFieldValues(robotArmScannerModule), out isDeployed, out isRetracted);
        }

        /// <summary>
        /// Reflection probe half: hands the module to the pure
        /// <see cref="TryClassifyAnimateHeatFromFieldValues{TFields}"/> core.
        /// </summary>
        internal static bool TryClassifyAnimateHeatState(
            PartModule animateHeatModule, out float normalizedHeat, out string sourceField)
        {
            normalizedHeat = 0f;
            sourceField = null;
            if (animateHeatModule == null) return false;

            return TryClassifyAnimateHeatFromFieldValues(
                new PartModuleFieldValues(animateHeatModule), out normalizedHeat, out sourceField);
        }

        private static bool TryGetModuleBoolField(PartModule module, string fieldName, out bool value)
        {
            value = false;
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return false;

            try
            {
                BaseField field = module.Fields[fieldName];
                if (field == null)
                    return false;

                object raw = field.GetValue(module);
                if (raw == null)
                    return false;

                if (raw is bool)
                {
                    value = (bool)raw;
                    return true;
                }

                string text = raw.ToString();
                bool parsedBool;
                if (bool.TryParse(text, out parsedBool))
                {
                    value = parsedBool;
                    return true;
                }

                int parsedInt;
                if (int.TryParse(text, out parsedInt))
                {
                    value = parsedInt != 0;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryGetModuleStringField(PartModule module, string fieldName, out string value)
        {
            value = null;
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return false;

            try
            {
                BaseField field = module.Fields[fieldName];
                if (field == null)
                    return false;

                object raw = field.GetValue(module);
                if (raw == null)
                    return false;

                value = raw.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("Recorder", $"string-field-{fieldName}",
                    $"TryGetModuleStringField exception for '{fieldName}' on {module.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static PartEvent? CheckGearTransition(
            uint partPersistentId, string partName, bool isDeployed, HashSet<uint> deployedSet, double ut)
        {
            bool wasDeployed = deployedSet.Contains(partPersistentId);

            if (isDeployed && !wasDeployed)
            {
                deployedSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.GearDeployed,
                    partName = partName
                };
            }

            if (!isDeployed && wasDeployed)
            {
                deployedSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.GearRetracted,
                    partName = partName
                };
            }

            return null;
        }

        private void CheckGearState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var wheel = p.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                    if (wheel == null) continue;

                    ClassifyGearState(wheel.stateString, out bool isDeployed, out bool isRetracted);

                    // Skip transitional states ("Deploying"/"Retracting") — only fire on endpoints
                    if (!isDeployed && !isRetracted) continue;

                    var evt = CheckGearTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedGear, ut);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                    }
                    break; // one deployment module per part
                }
            }
        }

        internal static PartEvent? CheckCargoBayTransition(
            uint partPersistentId, string partName, bool isOpen,
            HashSet<uint> openSet, double ut)
        {
            bool wasOpen = openSet.Contains(partPersistentId);

            if (isOpen && !wasOpen)
            {
                openSet.Add(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.CargoBayOpened,
                    partName = partName
                };
            }

            if (!isOpen && wasOpen)
            {
                openSet.Remove(partPersistentId);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.CargoBayClosed,
                    partName = partName
                };
            }

            return null;
        }

        private void CheckCargoBayState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (loggedCargoBayDeployIndexIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("Recorder", $"CargoBay: invalid DeployModuleIndex for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} deployIdx={deployIdx} modules={p.Modules.Count}");
                    }
                    continue;
                }
                var animModule = p.Modules[deployIdx] as ModuleAnimateGeneric;
                if (animModule == null)
                {
                    if (loggedCargoBayAnimationIssues.Add(p.persistentId))
                    {
                        PartModule moduleAtIndex = p.Modules[deployIdx];
                        string moduleName = moduleAtIndex?.moduleName ?? "<null>";
                        ParsekLog.Verbose("Recorder", $"CargoBay: DeployModuleIndex did not resolve to ModuleAnimateGeneric for " +
                            $"'{p.partInfo?.name}' pid={p.persistentId} deployIdx={deployIdx} module='{moduleName}'");
                    }
                    continue;
                }

                ClassifyCargoBayState(animModule.animTime, cargo.closedPosition,
                    ResolveCargoBayDeployLimitStop(animModule, cargo.closedPosition),
                    out bool isOpen, out bool isClosed);

                if (!isOpen && !isClosed)
                {
                    if (cargo.closedPosition >= 0.1f && cargo.closedPosition <= 0.9f &&
                        loggedCargoBayClosedPositionIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("Recorder", $"CargoBay: unsupported closedPosition for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} closedPosition={cargo.closedPosition:F3} animTime={animModule.animTime:F3}");
                    }
                    continue; // mid-transition or non-standard closedPosition
                }

                var evt = CheckCargoBayTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isOpen, openCargoBays, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }

        private void CheckLadderState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    bool isDeployed;
                    bool isRetracted;
                    if (!TryClassifyRetractableLadderState(module, out isDeployed, out isRetracted))
                    {
                        if (loggedLadderClassificationMisses.Add(key))
                        {
                            ParsekLog.Verbose("Recorder", $"Ladder: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }

                    var evt = CheckLadderTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedLadders, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} (ladder)");
                    }

                    break; // one RetractableLadder module per ladder part
                }
            }
        }

        private void CheckAnimationGroupState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyAnimationGroupState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAnimationGroupClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"AnimationGroup: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAnimationGroups, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animation-group)");
                    }
                }
            }
        }

        private void CheckAeroSurfaceState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyAeroSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAeroSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"AeroSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAeroSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (aero-surface)");
                    }
                }
            }
        }

        private void CheckControlSurfaceState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyControlSurfaceState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedControlSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"ControlSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedControlSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (control-surface)");
                    }
                }
            }
        }

        private void CheckRobotArmScannerState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (!TryClassifyRobotArmScannerState(module, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedRobotArmScannerClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"RobotArmScanner: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedRobotArmScannerModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (robot-arm-scanner)");
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the part has a dedicated animation handler module (deployable, gear,
        /// cargo bay, ladder, animation group, aero/control surface, robot arm, or animate heat).
        /// Parts with dedicated handlers are skipped by CheckAnimateGenericState to avoid duplicate events.
        /// </summary>
        internal static bool HasDedicatedAnimateHandler(Part p)
        {
            if (p.FindModuleImplementing<ModuleDeployablePart>() != null) return true;
            if (p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null) return true;
            if (p.FindModuleImplementing<ModuleCargoBay>() != null) return true;

            for (int m = 0; m < p.Modules.Count; m++)
            {
                PartModule module = p.Modules[m];
                if (module == null) continue;
                if (string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal)) return true;
                if (string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private void CheckAnimateGenericState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                // Avoid duplicate events for modules already tracked via dedicated handlers.
                if (HasDedicatedAnimateHandler(p)) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    ModuleAnimateGeneric animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    bool isDeployed;
                    bool isRetracted;
                    if (!TryClassifyAnimateGenericState(animateModule, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAnimateGenericClassificationMisses.Add(diagnosticKey))
                        {
                            string fields = DescribeModuleFields(animateModule);
                            if (fields.Length > 200) fields = fields.Substring(0, 200) + "...";
                            ParsekLog.Verbose("Recorder", $"AnimateGeneric: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m} anim='{animateModule.animationName}'; fields=[{fields}]");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted)
                        continue;

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, deployedAnimateGenericModules, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (anim-generic)");
                    }
                }
            }
        }

        private void CheckAnimateHeatState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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

                    if (!TryClassifyAnimateHeatState(
                        module, out float normalizedHeat, out string sourceField))
                    {
                        ulong diagnosticKey = EncodeEngineKey(p.persistentId, m);
                        if (loggedAnimateHeatClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("Recorder", $"AnimateHeat: unable to classify '{p.partInfo?.name}' pid={p.persistentId} " +
                                $"midx={m}; fields=[{DescribeModuleFields(module)}]");
                        }
                        continue;
                    }

                    ulong key = EncodeEngineKey(p.persistentId, m);
                    var evt = CheckAnimateHeatTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        normalizedHeat, animateHeatLevels, ut, m);
                    if (evt.HasValue)
                    {
                        PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} " +
                            $"heat={normalizedHeat:F2} src={sourceField ?? "<unknown>"}");
                    }
                }
            }
        }

        internal static PartEvent? CheckFairingTransition(
            uint partPersistentId, string partName, bool isDeployed,
            HashSet<uint> deployedSet, double ut)
        {
            if (isDeployed && deployedSet.Add(partPersistentId))
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.FairingJettisoned,
                    partName = partName
                };
            }
            return null;
        }

        private void CheckFairingState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
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
                    if (loggedFairingReadFailures.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("Recorder", $"Fairing: unable to read GetScalar for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} ({ex.GetType().Name}: {ex.Message})");
                    }
                    continue;
                }

                var evt = CheckFairingTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedFairings, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
        }
    }
}
