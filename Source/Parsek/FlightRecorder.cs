using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Owns all recording state and sampling logic.
    /// Called each physics frame by the Harmony postfix on VesselPrecalculate.
    /// </summary>
    public class FlightRecorder
    {
        internal enum VesselSwitchDecision
        {
            None,
            ContinueOnEva,
            Stop,
            ChainToVessel,  // EVA recording → boarded a vessel
            DockMerge,      // Vessel absorbed into dock target (pid changed)
            UndockSwitch,   // Player switched to undocked sibling vessel
            TransitionToBackground,   // active recording → background (orbit segment)
            PromoteFromBackground     // background recording → active (resume physics sampling)
        }

        // Recording output
        public List<TrajectoryPoint> Recording { get; } = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
        public List<PartEvent> PartEvents { get; } = new List<PartEvent>();

        // Part event tracking
        private Dictionary<uint, int> parachuteStates = new Dictionary<uint, int>(); // 0=stowed, 1=semi, 2=deployed
        private HashSet<uint> jettisonedShrouds = new HashSet<uint>();
        private Dictionary<ulong, string> jettisonNameRawCache = new Dictionary<ulong, string>();
        private Dictionary<ulong, string[]> parsedJettisonNamesCache = new Dictionary<ulong, string[]>();
        private HashSet<uint> extendedDeployables = new HashSet<uint>();
        private HashSet<uint> lightsOn = new HashSet<uint>();
        private HashSet<uint> blinkingLights = new HashSet<uint>();
        private Dictionary<uint, float> lightBlinkRates = new Dictionary<uint, float>();
        private HashSet<uint> deployedGear = new HashSet<uint>();
        private HashSet<uint> openCargoBays = new HashSet<uint>();
        private HashSet<uint> deployedFairings = new HashSet<uint>();
        private HashSet<ulong> deployedLadders = new HashSet<ulong>();
        private HashSet<ulong> deployedAnimationGroups = new HashSet<ulong>();
        private HashSet<ulong> deployedAnimateGenericModules = new HashSet<ulong>();
        private HashSet<ulong> deployedAeroSurfaceModules = new HashSet<ulong>();
        private HashSet<ulong> deployedControlSurfaceModules = new HashSet<ulong>();
        private HashSet<ulong> deployedRobotArmScannerModules = new HashSet<ulong>();
        private HashSet<ulong> hotAnimateHeatModules = new HashSet<ulong>();

        // Engine state tracking (key = EncodeEngineKey(pid, moduleIndex))
        private List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines;
        private HashSet<ulong> activeEngineKeys;
        private Dictionary<ulong, float> lastThrottle;
        private HashSet<ulong> loggedEngineModuleKeys = new HashSet<ulong>();

        // RCS state tracking (separate dicts from engines — keys can overlap for same part)
        private List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules;
        private HashSet<ulong> activeRcsKeys;
        private Dictionary<ulong, float> lastRcsThrottle;
        private HashSet<ulong> loggedRcsModuleKeys = new HashSet<ulong>();
        // Robotics state tracking (Breaking Ground; sparse sampling to limit event volume)
        private List<(Part part, PartModule module, int moduleIndex, string moduleName)> cachedRoboticModules;
        private HashSet<ulong> activeRoboticKeys;
        private Dictionary<ulong, float> lastRoboticPosition;
        private Dictionary<ulong, double> lastRoboticSampleUT;
        private HashSet<ulong> loggedRoboticModuleKeys;
        private HashSet<ulong> loggedLadderClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAnimationGroupClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAnimateGenericClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAeroSurfaceClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedControlSurfaceClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedRobotArmScannerClassificationMisses = new HashSet<ulong>();
        private HashSet<ulong> loggedAnimateHeatClassificationMisses = new HashSet<ulong>();
        private HashSet<uint> loggedCargoBayDeployIndexIssues = new HashSet<uint>();
        private HashSet<uint> loggedCargoBayAnimationIssues = new HashSet<uint>();
        private HashSet<uint> loggedCargoBayClosedPositionIssues = new HashSet<uint>();
        private HashSet<uint> loggedFairingReadFailures = new HashSet<uint>();
        internal bool partEventsSubscribed;
        public bool IsRecording { get; internal set; }
        public uint RecordingVesselId { get; private set; }
        public bool RecordingStartedAsEva { get; private set; }
        public bool VesselDestroyedDuringRecording { get; set; }
        public bool ChainToVesselPending { get; internal set; }
        public bool DockMergePending { get; set; }
        public bool UndockSwitchPending { get; set; }
        // Set by ParsekFlight to enable sibling vessel switch detection in OnPhysicsFrame
        public uint UndockSiblingPid { get; set; }

        // Tree mode: set by ParsekFlight when a recording tree is active
        public RecordingTree ActiveTree { get; set; }
        public bool TransitionToBackgroundPending { get; internal set; }

        // Joint break split detection: set by OnPartJointBreak, consumed by ParsekFlight.Update()
        public bool HasPendingJointBreakCheck { get; private set; }

        /// <summary>
        /// Consumes the pending joint break flag, returning whether a check was pending.
        /// </summary>
        public bool ConsumePendingJointBreakCheck()
        {
            bool was = HasPendingJointBreakCheck;
            HasPendingJointBreakCheck = false;
            return was;
        }

        /// <summary>
        /// True when the recorder is conceptually recording but not actively sampling
        /// physics frames (e.g., vessel switched away in tree mode).
        /// </summary>
        public bool IsBackgrounded => IsRecording && Patches.PhysicsFramePatch.ActiveRecorder != this;

        public RecordingStore.Recording CaptureAtStop { get; private set; }
        public double PreLaunchFunds { get; private set; }
        public double PreLaunchScience { get; private set; }
        public float PreLaunchReputation { get; private set; }
        public string RewindSaveFileName { get; private set; }
        public double RewindReservedFunds { get; private set; }
        public double RewindReservedScience { get; private set; }
        public float RewindReservedRep { get; private set; }
        public ConfigNode LastGoodVesselSnapshot => lastGoodVesselSnapshot;
        public ConfigNode InitialGhostVisualSnapshot => initialGhostVisualSnapshot;

        // Adaptive sampling thresholds (read from settings, fallback to defaults)
        private static float maxSampleInterval =>
            ParsekSettings.Current?.maxSampleInterval ?? 3.0f;
        private static float velocityDirThreshold =>
            ParsekSettings.Current?.velocityDirThreshold ?? 2.0f;
        private static float speedChangeThreshold =>
            (ParsekSettings.Current?.speedChangeThreshold ?? 5.0f) / 100f;
        private const double snapshotRefreshIntervalUT = 10.0;
        private const float snapshotPerfLogThresholdMs = 25.0f;
        private const double roboticSampleIntervalSeconds = 0.25; // 4 Hz
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;
        private const float animateHeatHotThreshold = 0.75f;
        private const float animateHeatColdThreshold = 0.10f;
        private double lastRecordedUT = -1;
        private Vector3 lastRecordedVelocity;
        private ConfigNode lastGoodVesselSnapshot;
        private ConfigNode initialGhostVisualSnapshot;
        private double lastSnapshotRefreshUT = double.MinValue;

        // Boundary anchor: if set, inserted as the first point when recording starts.
        // Used for chain continuation to seamlessly stitch segments.
        public TrajectoryPoint? BoundaryAnchor { get; set; }

        // On-rails state
        private bool isOnRails;
        private bool hasPersistentRotation;
        private OrbitSegment currentOrbitSegment;

        // Atmosphere boundary detection
        private bool wasInAtmosphere;
        private bool atmosphereBoundaryPending;
        private double atmosphereBoundaryPendingUT;
        public bool AtmosphereBoundaryCrossed { get; private set; }
        public bool EnteredAtmosphere { get; private set; }

        // SOI change detection
        public bool SoiChangePending { get; private set; }
        public string SoiChangeFromBody { get; private set; }

        #region Part Event Subscription

        private void SubscribePartEvents()
        {
            if (partEventsSubscribed) return;
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onPartJointBreak.Add(OnPartJointBreak);
            partEventsSubscribed = true;
            ParsekLog.Verbose("Recorder", "Subscribed part event hooks");
        }

        private void UnsubscribePartEvents()
        {
            if (!partEventsSubscribed) return;
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onPartJointBreak.Remove(OnPartJointBreak);
            partEventsSubscribed = false;
            ParsekLog.Verbose("Recorder", "Unsubscribed part event hooks");
        }

        private void OnPartDie(Part p)
        {
            if (!IsRecording) return;
            if (p?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "part-die-null",
                    "OnPartDie: part or vessel is null");
                return;
            }
            if (p.vessel.persistentId != RecordingVesselId) return;

            bool hasChute = p.FindModuleImplementing<ModuleParachute>() != null;
            var evtType = ClassifyPartDeath(p.persistentId, hasChute, parachuteStates);

            PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = evtType,
                partName = p.partInfo?.name ?? "unknown"
            });
            ParsekLog.Verbose("Recorder", $"Part event: {evtType} '{p.partInfo?.name}' pid={p.persistentId}");
        }

        internal static PartEventType ClassifyPartDeath(
            uint partPersistentId, bool hasParachuteModule, Dictionary<uint, int> parachuteStates)
        {
            int state;
            if (hasParachuteModule && parachuteStates.TryGetValue(partPersistentId, out state) && state > 0)
            {
                parachuteStates.Remove(partPersistentId);
                return PartEventType.ParachuteDestroyed;
            }
            return PartEventType.Destroyed;
        }

        private void OnPartJointBreak(PartJoint joint, float breakForce)
        {
            if (!IsRecording) return;
            if (joint?.Child?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "joint-break-null",
                    "OnPartJointBreak: joint, child, or vessel is null");
                return;
            }
            if (joint.Child.vessel.persistentId != RecordingVesselId) return;

            PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = joint.Child.persistentId,
                eventType = PartEventType.Decoupled,
                partName = joint.Child.partInfo?.name ?? "unknown"
            });
            ParsekLog.Verbose("Recorder", $"Part event: Decoupled '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId}");

            // Signal potential vessel split for deferred check by ParsekFlight
            HasPendingJointBreakCheck = true;
        }

        /// <summary>
        /// Detect parachute state transitions. States: 0=stowed/active/cut, 1=semi-deployed, 2=deployed.
        /// </summary>
        internal static PartEvent? CheckParachuteTransition(
            uint partPersistentId, string partName, int newState, Dictionary<uint, int> stateMap, double ut)
        {
            int oldState;
            if (!stateMap.TryGetValue(partPersistentId, out oldState))
                oldState = 0;

            if (newState == oldState)
                return null;

            if (newState > 0)
                stateMap[partPersistentId] = newState;
            else
                stateMap.Remove(partPersistentId);

            // 0→1: semi-deployed (streamer)
            if (newState == 1 && oldState == 0)
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ParachuteSemiDeployed,
                    partName = partName
                };
            }

            // 0→2 or 1→2: fully deployed (dome)
            if (newState == 2)
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ParachuteDeployed,
                    partName = partName
                };
            }

            // 1→0 or 2→0: cut
            if (newState == 0 && oldState > 0)
            {
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ParachuteCut,
                    partName = partName
                };
            }

            return null;
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

                int state = chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED ? 2
                          : chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED ? 1
                          : 0;

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

                    // Fallback for parts where the module flag may lag/never set:
                    // if any configured jettison object is already hidden, treat as jettisoned.
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
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable == null) continue;

                var ds = deployable.deployState;

                // Skip broken and transitional states — only fire on completed transitions
                if (ds == ModuleDeployablePart.DeployState.BROKEN) continue;
                if (ds == ModuleDeployablePart.DeployState.EXTENDING) continue;
                if (ds == ModuleDeployablePart.DeployState.RETRACTING) continue;

                bool isExtended = ds == ModuleDeployablePart.DeployState.EXTENDED;

                var evt = CheckDeployableTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isExtended, extendedDeployables, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
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

        internal static List<PartEvent> CheckLightBlinkTransition(
            uint partPersistentId, string partName, bool isBlinking, float blinkRate,
            HashSet<uint> blinkingSet, Dictionary<uint, float> blinkRateMap, double ut)
        {
            var events = new List<PartEvent>();
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
                return events;
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
                return events;
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

            return events;
        }

        internal static void ClassifyLadderState(float animTime, out bool isExtended, out bool isRetracted)
        {
            isExtended = animTime >= 0.99f;
            isRetracted = animTime <= 0.01f;
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
            bool isHot, bool isCold, float normalizedHeat,
            HashSet<ulong> hotSet, double ut, int moduleIndex)
        {
            bool wasHot = hotSet.Contains(key);

            if (isHot && !wasHot)
            {
                hotSet.Add(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ThermalAnimationHot,
                    partName = partName,
                    value = normalizedHeat,
                    moduleIndex = moduleIndex
                };
            }

            if (isCold && wasHot)
            {
                hotSet.Remove(key);
                return new PartEvent
                {
                    ut = ut,
                    partPersistentId = partPersistentId,
                    eventType = PartEventType.ThermalAnimationCold,
                    partName = partName,
                    value = normalizedHeat,
                    moduleIndex = moduleIndex
                };
            }

            return null;
        }

        private void CheckLightState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var light = p.FindModuleImplementing<ModuleLight>();
                if (light == null) continue;

                var evt = CheckLightTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", light.isOn, lightsOn, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("Recorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }

                var blinkEvents = CheckLightBlinkTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    light.isBlinking, light.blinkRate,
                    blinkingLights, lightBlinkRates, ut);
                for (int e = 0; e < blinkEvents.Count; e++)
                {
                    PartEvents.Add(blinkEvents[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {blinkEvents[e].eventType} '{blinkEvents[e].partName}' " +
                        $"pid={blinkEvents[e].partPersistentId} val={blinkEvents[e].value:F2}");
                }
            }
        }

        internal static bool TryClassifyLadderStateFromEventActivity(
            bool canExtend, bool canRetract, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;

            // For ladders, mutually-exclusive UI event activity indicates current state:
            // - can retract => currently deployed
            // - can extend  => currently retracted
            if (canExtend == canRetract)
                return false;

            isDeployed = canRetract;
            isRetracted = canExtend;
            return true;
        }

        internal static bool TryClassifyRetractableLadderState(
            PartModule ladderModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (ladderModule == null) return false;

            bool sawExtendEvent = false;
            bool sawRetractEvent = false;
            bool canExtend = false;
            bool canRetract = false;

            // Prefer event activity, which directly reflects action availability.
            if (ladderModule.Events != null)
            {
                for (int i = 0; i < ladderModule.Events.Count; i++)
                {
                    BaseEvent evt = ladderModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isExtendEvent = evtName.Contains("extend") || guiName.Contains("extend");
                    bool isRetractEvent = evtName.Contains("retract") || guiName.Contains("retract");

                    if (isExtendEvent)
                    {
                        sawExtendEvent = true;
                        canExtend = canExtend || evt.active;
                    }

                    if (isRetractEvent)
                    {
                        sawRetractEvent = true;
                        canRetract = canRetract || evt.active;
                    }
                }
            }

            if ((sawExtendEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend, canRetract, out isDeployed, out isRetracted))
                return true;

            // Fallback: some module variants expose direct bool fields.
            bool boolValue;
            if (TryGetModuleBoolField(ladderModule, "isDeployed", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "deployed", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "isExtended", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "extended", out boolValue))
            {
                isDeployed = boolValue;
                isRetracted = !boolValue;
                return true;
            }

            if (TryGetModuleBoolField(ladderModule, "isRetracted", out boolValue) ||
                TryGetModuleBoolField(ladderModule, "retracted", out boolValue))
            {
                isRetracted = boolValue;
                isDeployed = !boolValue;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyAnimationGroupState(
            PartModule animationGroupModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (animationGroupModule == null) return false;

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            // Prefer event activity: for ModuleAnimationGroup this directly tracks whether
            // the opposite action is currently available (deploy vs retract).
            if (animationGroupModule.Events != null)
            {
                for (int i = 0; i < animationGroupModule.Events.Count; i++)
                {
                    BaseEvent evt = animationGroupModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("extend") || guiName.Contains("extend");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract");

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

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            // Fallback: some variants expose direct bool fields.
            bool boolValue;
            if (TryGetModuleBoolField(animationGroupModule, "isDeployed", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "deployed", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "isExtended", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "extended", out boolValue))
            {
                isDeployed = boolValue;
                isRetracted = !boolValue;
                return true;
            }

            if (TryGetModuleBoolField(animationGroupModule, "isRetracted", out boolValue) ||
                TryGetModuleBoolField(animationGroupModule, "retracted", out boolValue))
            {
                isRetracted = boolValue;
                isDeployed = !boolValue;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyAnimateGenericState(
            ModuleAnimateGeneric animateModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (animateModule == null) return false;

            float animTime = animateModule.animTime;
            if (!float.IsNaN(animTime) && !float.IsInfinity(animTime))
            {
                ClassifyLadderState(animTime, out isDeployed, out isRetracted);
                if (isDeployed || isRetracted)
                    return true;
            }

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            if (animateModule.Events != null)
            {
                for (int i = 0; i < animateModule.Events.Count; i++)
                {
                    BaseEvent evt = animateModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("extend") || guiName.Contains("extend") ||
                        evtName.Contains("open") || guiName.Contains("open") ||
                        evtName.Contains("inflate") || guiName.Contains("inflate");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract") ||
                        evtName.Contains("close") || guiName.Contains("close") ||
                        evtName.Contains("deflate") || guiName.Contains("deflate");

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

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            bool boolValue;
            if (TryGetModuleBoolField(animateModule, "isDeployed", out boolValue) ||
                TryGetModuleBoolField(animateModule, "deployed", out boolValue) ||
                TryGetModuleBoolField(animateModule, "isExtended", out boolValue) ||
                TryGetModuleBoolField(animateModule, "extended", out boolValue))
            {
                isDeployed = boolValue;
                isRetracted = !boolValue;
                return true;
            }

            if (TryGetModuleBoolField(animateModule, "isRetracted", out boolValue) ||
                TryGetModuleBoolField(animateModule, "retracted", out boolValue))
            {
                isRetracted = boolValue;
                isDeployed = !boolValue;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyAeroSurfaceState(
            PartModule aeroSurfaceModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (aeroSurfaceModule == null) return false;

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            if (aeroSurfaceModule.Events != null)
            {
                for (int i = 0; i < aeroSurfaceModule.Events.Count; i++)
                {
                    BaseEvent evt = aeroSurfaceModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("extend") || guiName.Contains("extend") ||
                        evtName.Contains("open") || guiName.Contains("open") ||
                        evtName.Contains("brake") || guiName.Contains("brake") ||
                        evtName.Contains("enable") || guiName.Contains("enable");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract") ||
                        evtName.Contains("close") || guiName.Contains("close") ||
                        evtName.Contains("stow") || guiName.Contains("stow") ||
                        evtName.Contains("disable") || guiName.Contains("disable");

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

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            bool boolValue;
            string[] deployedFields =
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
            for (int i = 0; i < deployedFields.Length; i++)
            {
                if (TryReadModuleBoolField(aeroSurfaceModule, deployedFields[i], out boolValue))
                {
                    isDeployed = boolValue;
                    isRetracted = !boolValue;
                    return true;
                }
            }

            string[] retractedFields =
            {
                "isRetracted",
                "retracted",
                "isStowed",
                "stowed",
                "isPacked",
                "packed"
            };
            for (int i = 0; i < retractedFields.Length; i++)
            {
                if (TryReadModuleBoolField(aeroSurfaceModule, retractedFields[i], out boolValue))
                {
                    isRetracted = boolValue;
                    isDeployed = !boolValue;
                    return true;
                }
            }

            string[] deflectionFields =
            {
                "currentDeflection",
                "deflection",
                "deployPercent",
                "position"
            };
            for (int i = 0; i < deflectionFields.Length; i++)
            {
                if (!TryReadModuleFloatField(aeroSurfaceModule, deflectionFields[i], out float deflection))
                    continue;

                if (float.IsNaN(deflection) || float.IsInfinity(deflection))
                    continue;

                isDeployed = Math.Abs(deflection) > 0.01f;
                isRetracted = !isDeployed;
                return true;
            }

            return false;
        }

        internal static bool TryClassifyControlSurfaceState(
            PartModule controlSurfaceModule, out bool isDeployed, out bool isRetracted)
        {
            // ModuleControlSurface exposes similar fields/deflection semantics to ModuleAeroSurface.
            return TryClassifyAeroSurfaceState(controlSurfaceModule, out isDeployed, out isRetracted);
        }

        internal static bool TryClassifyRobotArmScannerState(
            PartModule robotArmScannerModule, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (robotArmScannerModule == null) return false;

            bool sawDeployEvent = false;
            bool sawRetractEvent = false;
            bool canDeploy = false;
            bool canRetract = false;

            if (robotArmScannerModule.Events != null)
            {
                for (int i = 0; i < robotArmScannerModule.Events.Count; i++)
                {
                    BaseEvent evt = robotArmScannerModule.Events[i];
                    if (evt == null) continue;

                    string evtName = (evt.name ?? string.Empty).ToLowerInvariant();
                    string guiName = (evt.guiName ?? string.Empty).ToLowerInvariant();

                    bool isDeployEvent =
                        evtName.Contains("deploy") || guiName.Contains("deploy") ||
                        evtName.Contains("unpack") || guiName.Contains("unpack") ||
                        evtName.Contains("extend") || guiName.Contains("extend") ||
                        evtName.Contains("scan") || guiName.Contains("scan") ||
                        evtName.Contains("start") || guiName.Contains("start");
                    bool isRetractEvent =
                        evtName.Contains("retract") || guiName.Contains("retract") ||
                        ((evtName.Contains("pack") || guiName.Contains("pack")) &&
                            !evtName.Contains("unpack") && !guiName.Contains("unpack")) ||
                        evtName.Contains("stow") || guiName.Contains("stow") ||
                        evtName.Contains("stop") || guiName.Contains("stop") ||
                        evtName.Contains("cancel") || guiName.Contains("cancel");

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

            if ((sawDeployEvent || sawRetractEvent) &&
                TryClassifyLadderStateFromEventActivity(
                    canExtend: canDeploy, canRetract: canRetract,
                    out isDeployed, out isRetracted))
                return true;

            bool boolValue;
            string[] deployedFields =
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
            for (int i = 0; i < deployedFields.Length; i++)
            {
                if (TryReadModuleBoolField(robotArmScannerModule, deployedFields[i], out boolValue))
                {
                    isDeployed = boolValue;
                    isRetracted = !boolValue;
                    return true;
                }
            }

            string[] retractedFields =
            {
                "isRetracted",
                "retracted",
                "isPacked",
                "packed",
                "isStowed",
                "stowed"
            };
            for (int i = 0; i < retractedFields.Length; i++)
            {
                if (TryReadModuleBoolField(robotArmScannerModule, retractedFields[i], out boolValue))
                {
                    isRetracted = boolValue;
                    isDeployed = !boolValue;
                    return true;
                }
            }

            if (TryReadModuleFloatField(robotArmScannerModule, "animTime", out float animTime) &&
                !float.IsNaN(animTime) && !float.IsInfinity(animTime))
            {
                ClassifyLadderState(animTime, out isDeployed, out isRetracted);
                if (isDeployed || isRetracted)
                    return true;
            }

            return false;
        }

        internal static bool TryClassifyAnimateHeatState(
            PartModule animateHeatModule, out bool isHot, out bool isCold,
            out float normalizedHeat, out string sourceField)
        {
            isHot = false;
            isCold = false;
            normalizedHeat = 0f;
            sourceField = null;
            if (animateHeatModule == null) return false;

            string[] candidateFields =
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

            for (int i = 0; i < candidateFields.Length; i++)
            {
                if (!TryReadModuleFloatField(animateHeatModule, candidateFields[i], out float raw))
                    continue;
                if (float.IsNaN(raw) || float.IsInfinity(raw))
                    continue;

                normalizedHeat = NormalizeAnimateHeatScalar(raw);
                sourceField = candidateFields[i];
                isHot = normalizedHeat >= animateHeatHotThreshold;
                isCold = normalizedHeat <= animateHeatColdThreshold;
                return true;
            }

            return false;
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

        internal static void ClassifyGearState(string stateString, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = stateString == "Deployed";
            isRetracted = stateString == "Retracted";
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

        internal static void ClassifyCargoBayState(
            float animTime, float closedPosition, out bool isOpen, out bool isClosed)
        {
            bool atStart = animTime <= 0.01f;
            bool atEnd = animTime >= 0.99f;

            if (closedPosition > 0.9f)
            {
                // closedPosition near 1 → closed at animTime≈1, open at animTime≈0
                isClosed = atEnd;
                isOpen = atStart;
            }
            else if (closedPosition < 0.1f)
            {
                // closedPosition near 0 → closed at animTime≈0, open at animTime≈1
                isClosed = atStart;
                isOpen = atEnd;
            }
            else
            {
                // Non-standard closedPosition (modded part) — skip
                isClosed = false;
                isOpen = false;
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

        private void CheckAnimateGenericState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                // Avoid duplicate events for modules already tracked via dedicated handlers.
                bool hasDedicatedHandler =
                    p.FindModuleImplementing<ModuleDeployablePart>() != null ||
                    p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                    p.FindModuleImplementing<ModuleCargoBay>() != null;

                bool hasRetractableLadder = false;
                bool hasAnimationGroup = false;
                bool hasAeroSurface = false;
                bool hasControlSurface = false;
                bool hasRobotArmScanner = false;
                bool hasAnimateHeat = false;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    if (string.Equals(module.moduleName, "RetractableLadder", StringComparison.Ordinal))
                        hasRetractableLadder = true;
                    if (string.Equals(module.moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                        hasAnimationGroup = true;
                    if (string.Equals(module.moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                        hasAeroSurface = true;
                    if (string.Equals(module.moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                        hasControlSurface = true;
                    if (string.Equals(module.moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                        hasRobotArmScanner = true;
                    if (string.Equals(module.moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                        hasAnimateHeat = true;
                    if (hasRetractableLadder || hasAnimationGroup ||
                        hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat)
                        break;
                }
                if (hasDedicatedHandler || hasRetractableLadder || hasAnimationGroup ||
                    hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat) continue;

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
                        module, out bool isHot, out bool isCold,
                        out float normalizedHeat, out string sourceField))
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
                        isHot, isCold, normalizedHeat, hotAnimateHeatModules, ut, m);
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

        private static string FormatCoverageEntries(List<string> entries, int maxEntries = 8)
        {
            if (entries == null || entries.Count == 0)
                return "<none>";

            if (entries.Count <= maxEntries)
                return string.Join(", ", entries.ToArray());

            var preview = entries.GetRange(0, maxEntries);
            return string.Join(", ", preview.ToArray()) + $", +{entries.Count - maxEntries} more";
        }

        private static void LogCoverageDetails(string label, List<string> entries)
        {
            ParsekLog.Verbose("Recorder", $"  Visual coverage [{label}] {entries.Count}: {FormatCoverageEntries(entries)}");
        }

        private void LogVisualRecordingCoverage(Vessel v)
        {
            if (v == null || v.parts == null)
                return;

            int moduleCount = 0;
            var parachuteParts = new List<string>();
            var jettisonModules = new List<string>();
            var deployableParts = new List<string>();
            var ladderModules = new List<string>();
            var animationGroupModules = new List<string>();
            var animateGenericModules = new List<string>();
            var lightParts = new List<string>();
            var gearModules = new List<string>();
            var cargoBayParts = new List<string>();
            var fairingParts = new List<string>();
            var aeroSurfaceModules = new List<string>();
            var controlSurfaceModules = new List<string>();
            var robotArmScannerModules = new List<string>();
            var animateHeatModules = new List<string>();
            var engineModules = new List<string>();
            var rcsModules = new List<string>();
            var roboticModules = new List<string>();

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                string partName = p.partInfo?.name ?? "unknown";
                string partRef = $"{partName}[pid={p.persistentId}]";

                if (p.FindModuleImplementing<ModuleParachute>() != null)
                    parachuteParts.Add(partRef);

                ModuleDeployablePart deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable != null)
                    deployableParts.Add(
                        $"{partRef}(state={deployable.deployState})");

                if (p.FindModuleImplementing<ModuleLight>() != null)
                    lightParts.Add(partRef);

                ModuleCargoBay cargoBay = p.FindModuleImplementing<ModuleCargoBay>();
                if (cargoBay != null)
                    cargoBayParts.Add(
                        $"{partRef}(deployIdx={cargoBay.DeployModuleIndex},closed={cargoBay.closedPosition:F2})");

                if (p.FindModuleImplementing<ModuleProceduralFairing>() != null)
                    fairingParts.Add(partRef);

                bool hasRetractableLadder = false;
                bool hasAnimationGroup = false;
                bool hasAeroSurface = false;
                bool hasControlSurface = false;
                bool hasRobotArmScanner = false;
                bool hasAnimateHeat = false;
                bool hasDedicatedAnimateHandler =
                    deployable != null ||
                    p.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                    cargoBay != null;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;
                    moduleCount++;

                    string moduleName = module.moduleName ?? string.Empty;

                    var jettison = module as ModuleJettison;
                    if (jettison != null && !string.IsNullOrWhiteSpace(jettison.jettisonName))
                    {
                        jettisonModules.Add(
                            $"{partRef}(midx={m},names={jettison.jettisonName})");
                    }

                    var wheel = module as ModuleWheels.ModuleWheelDeployment;
                    if (wheel != null)
                    {
                        gearModules.Add(
                            $"{partRef}(midx={m},state='{wheel.stateString}')");
                    }

                    if (string.Equals(moduleName, "RetractableLadder", StringComparison.Ordinal))
                    {
                        ladderModules.Add($"{partRef}(midx={m})");
                        hasRetractableLadder = true;
                    }

                    if (string.Equals(moduleName, "ModuleAnimationGroup", StringComparison.Ordinal))
                    {
                        animationGroupModules.Add($"{partRef}(midx={m})");
                        hasAnimationGroup = true;
                    }

                    if (string.Equals(moduleName, "ModuleAeroSurface", StringComparison.Ordinal))
                    {
                        aeroSurfaceModules.Add($"{partRef}(midx={m})");
                        hasAeroSurface = true;
                    }

                    if (string.Equals(moduleName, "ModuleControlSurface", StringComparison.Ordinal))
                    {
                        controlSurfaceModules.Add($"{partRef}(midx={m})");
                        hasControlSurface = true;
                    }

                    if (string.Equals(moduleName, "ModuleRobotArmScanner", StringComparison.Ordinal))
                    {
                        robotArmScannerModules.Add($"{partRef}(midx={m})");
                        hasRobotArmScanner = true;
                    }

                    if (string.Equals(moduleName, "ModuleAnimateHeat", StringComparison.Ordinal))
                    {
                        animateHeatModules.Add($"{partRef}(midx={m})");
                        hasAnimateHeat = true;
                    }
                }

                if (hasDedicatedAnimateHandler || hasRetractableLadder || hasAnimationGroup ||
                    hasAeroSurface || hasControlSurface || hasRobotArmScanner || hasAnimateHeat)
                    continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var animateModule = p.Modules[m] as ModuleAnimateGeneric;
                    if (animateModule == null) continue;
                    if (string.IsNullOrEmpty(animateModule.animationName)) continue;

                    animateGenericModules.Add(
                        $"{partRef}(midx={m},anim={animateModule.animationName})");
                }
            }

            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    var (part, engine, moduleIndex) = cachedEngines[i];
                    if (part == null || engine == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    string engineId = string.IsNullOrEmpty(engine.engineID) ? "<none>" : engine.engineID;
                    int thrustTransformCount = engine.thrustTransforms != null ? engine.thrustTransforms.Count : 0;
                    engineModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},id={engineId},thrust={thrustTransformCount})");
                }
            }

            if (cachedRcsModules != null)
            {
                for (int i = 0; i < cachedRcsModules.Count; i++)
                {
                    var (part, rcs, moduleIndex) = cachedRcsModules[i];
                    if (part == null || rcs == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    int thrusterCount = rcs.thrusterTransforms != null ? rcs.thrusterTransforms.Count : 0;
                    int forceCount = rcs.thrustForces != null ? rcs.thrustForces.Length : 0;
                    rcsModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},thrusters={thrusterCount},forces={forceCount},power={rcs.thrusterPower:F1})");
                }
            }

            if (cachedRoboticModules != null)
            {
                for (int i = 0; i < cachedRoboticModules.Count; i++)
                {
                    var (part, _, moduleIndex, moduleName) = cachedRoboticModules[i];
                    if (part == null) continue;
                    string partName = part.partInfo?.name ?? "unknown";
                    roboticModules.Add(
                        $"{partName}[pid={part.persistentId}](midx={moduleIndex},module={moduleName})");
                }
            }

            ParsekLog.Verbose("Recorder", 
                $"Visual recording coverage for '{v.vesselName}' pid={v.persistentId}: " +
                $"parts={v.parts.Count} modules={moduleCount} " +
                $"parachute={parachuteParts.Count} jettison={jettisonModules.Count} deployable={deployableParts.Count} " +
                $"ladder={ladderModules.Count} animationGroup={animationGroupModules.Count} animateGeneric={animateGenericModules.Count} " +
                $"lights={lightParts.Count} gear={gearModules.Count} cargoBay={cargoBayParts.Count} fairing={fairingParts.Count} " +
                $"aeroSurface={aeroSurfaceModules.Count} controlSurface={controlSurfaceModules.Count} " +
                $"robotArmScanner={robotArmScannerModules.Count} animateHeat={animateHeatModules.Count} " +
                $"engine={engineModules.Count} rcs={rcsModules.Count} robotics={roboticModules.Count}");

            LogCoverageDetails("Parachute", parachuteParts);
            LogCoverageDetails("Jettison", jettisonModules);
            LogCoverageDetails("Deployable", deployableParts);
            LogCoverageDetails("Ladder", ladderModules);
            LogCoverageDetails("AnimationGroup", animationGroupModules);
            LogCoverageDetails("AnimateGeneric", animateGenericModules);
            LogCoverageDetails("Light", lightParts);
            LogCoverageDetails("Gear", gearModules);
            LogCoverageDetails("CargoBay", cargoBayParts);
            LogCoverageDetails("Fairing", fairingParts);
            LogCoverageDetails("AeroSurface", aeroSurfaceModules);
            LogCoverageDetails("ControlSurface", controlSurfaceModules);
            LogCoverageDetails("RobotArmScanner", robotArmScannerModules);
            LogCoverageDetails("AnimateHeat", animateHeatModules);
            LogCoverageDetails("Engine", engineModules);
            LogCoverageDetails("RCS", rcsModules);
            LogCoverageDetails("Robotics", roboticModules);
        }

        internal static List<PartEvent> CheckEngineTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool ignited, float throttle,
            HashSet<ulong> activeSet, Dictionary<ulong, float> lastThrottleMap, double ut)
        {
            var events = new List<PartEvent>();
            bool wasActive = activeSet.Contains(key);

            if (ignited && !wasActive)
            {
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
                if (delta > 0.01f || delta < -0.01f)
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

            return events;
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
                bool ignited = engine.EngineIgnited && engine.isOperational;
                float throttle = engine.currentThrottle;
                if (loggedEngineModuleKeys.Add(key))
                {
                    int thrustTransformCount = engine.thrustTransforms != null
                        ? engine.thrustTransforms.Count
                        : 0;
                    string engineId = string.IsNullOrEmpty(engine.engineID) ? "<none>" : engine.engineID;
                    ParsekLog.Verbose("Recorder", $"Engine tracking: '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} id={engineId} thrustTransforms={thrustTransformCount}");
                }

                var events = CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, throttle,
                    activeEngineKeys, lastThrottle, ut);

                for (int e = 0; e < events.Count; e++)
                {
                    PartEvents.Add(events[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} val={events[e].value:F2}");

                    // Some engines with ModuleJettison (e.g. Mainsail/Skipper/Vector) can
                    // visually drop covers on ignition even when isJettisoned polling lags.
                    // Emit a one-shot shroud event on ignition as a reliable fallback.
                    if (events[e].eventType == PartEventType.EngineIgnited &&
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

        internal static List<PartEvent> CheckRcsTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool active, float power,
            HashSet<ulong> activeSet, Dictionary<ulong, float> lastThrottleMap, double ut)
        {
            var events = new List<PartEvent>();
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
                if (delta > 0.01f || delta < -0.01f)
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

            return events;
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
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                float power = active ? ComputeRcsPower(rcs.thrustForces, rcs.thrusterPower) : 0f;
                if (loggedRcsModuleKeys.Add(key))
                {
                    int thrusterCount = rcs.thrusterTransforms != null ? rcs.thrusterTransforms.Count : 0;
                    int forceCount = rcs.thrustForces != null ? rcs.thrustForces.Length : 0;
                    ParsekLog.Verbose("Recorder", $"RCS tracking: '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} thrusters={thrusterCount} forces={forceCount} power={rcs.thrusterPower:F2}");
                }

                var events = CheckRcsTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    active, power,
                    activeRcsKeys, lastRcsThrottle, ut);

                for (int e = 0; e < events.Count; e++)
                {
                    PartEvents.Add(events[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} val={events[e].value:F2}");
                }
            }
        }

        private static bool IsWheelRoboticModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelSteering", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelMotor", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleWheelMotorSteering", StringComparison.Ordinal);
        }

        internal static bool IsRoboticModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleRoboticServoHinge", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticRotationServo", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoRotor", StringComparison.Ordinal) ||
                   IsWheelRoboticModuleName(moduleName);
        }

        internal static List<(Part part, PartModule module, int moduleIndex, string moduleName)> CacheRoboticModules(Vessel v)
        {
            var result = new List<(Part, PartModule, int, string)>();
            if (v == null || v.parts == null) return result;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                int roboticModuleIndex = 0;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module == null) continue;

                    string moduleName = module.moduleName;
                    if (!IsRoboticModuleName(moduleName)) continue;

                    result.Add((p, module, roboticModuleIndex, moduleName));
                    roboticModuleIndex++;
                }
            }

            return result;
        }

        private static BaseField FindModuleField(PartModule module, string fieldName)
        {
            if (module == null || module.Fields == null || string.IsNullOrEmpty(fieldName))
                return null;

            try
            {
                return module.Fields[fieldName];
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("Recorder", $"field-{fieldName}",
                    $"FindModuleField exception for '{fieldName}' on {module.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static bool TryParseFloatValue(object rawValue, out float value)
        {
            value = 0f;
            if (rawValue == null) return false;

            if (rawValue is float f)
            {
                value = f;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (rawValue is double d)
            {
                value = (float)d;
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            if (rawValue is int i)
            {
                value = i;
                return true;
            }

            if (rawValue is uint ui)
            {
                value = ui;
                return true;
            }

            if (rawValue is bool b)
            {
                value = b ? 1f : 0f;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();

            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return !float.IsNaN(value) && !float.IsInfinity(value);

            // Some KSP fields serialize vectors/quaternions as comma-separated values.
            string[] parts = text.Split(',');
            if (parts.Length > 0 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }

            return false;
        }

        private static bool TryParseBoolValue(object rawValue, out bool value)
        {
            value = false;
            if (rawValue == null) return false;

            if (rawValue is bool b)
            {
                value = b;
                return true;
            }

            if (rawValue is int i)
            {
                value = i != 0;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();

            if (bool.TryParse(text, out value)) return true;

            int intValue;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            {
                value = intValue != 0;
                return true;
            }

            return false;
        }

        private static bool TryParseVector3Value(object rawValue, out Vector3 value)
        {
            value = Vector3.zero;
            if (rawValue == null) return false;

            if (rawValue is Vector3 vec)
            {
                value = vec;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 3) return false;

            float x;
            float y;
            float z;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;

            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseQuaternionValue(object rawValue, out Quaternion value)
        {
            value = Quaternion.identity;
            if (rawValue == null) return false;

            if (rawValue is Quaternion quat)
            {
                value = quat;
                return true;
            }

            string text = rawValue.ToString();
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 4) return false;

            float x;
            float y;
            float z;
            float w;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out w)) return false;

            value = new Quaternion(x, y, z, w);
            return true;
        }

        private static bool TryReadModuleFloatField(PartModule module, string fieldName, out float value)
        {
            value = 0f;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseFloatValue(field.GetValue(module), out value);
        }

        private static float NormalizeAnimateHeatScalar(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw))
                return 0f;

            // Common case: ModuleAnimateHeat animTime style 0..1 scalar.
            if (raw >= -0.25f && raw <= 1.25f)
                return Mathf.Clamp01(raw);

            // Percent-like values.
            if (raw >= 0f && raw <= 100f)
                return Mathf.Clamp01(raw / 100f);

            // Absolute-temperature fallback (ambient ~300K, hot ~1500K+).
            if (raw >= 250f && raw <= 2500f)
                return Mathf.Clamp01((raw - 300f) / 1200f);

            return Mathf.Clamp01(raw);
        }

        private static bool TryReadModuleBoolField(PartModule module, string fieldName, out bool value)
        {
            value = false;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseBoolValue(field.GetValue(module), out value);
        }

        private static bool TryReadModuleVector3Field(PartModule module, string fieldName, out Vector3 value)
        {
            value = Vector3.zero;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseVector3Value(field.GetValue(module), out value);
        }

        private static bool TryReadModuleQuaternionField(PartModule module, string fieldName, out Quaternion value)
        {
            value = Quaternion.identity;
            BaseField field = FindModuleField(module, fieldName);
            if (field == null) return false;
            return TryParseQuaternionValue(field.GetValue(module), out value);
        }

        internal static bool TryGetRoboticMovingState(PartModule module, out bool moving)
        {
            moving = false;
            string[] movingFields =
            {
                "servoIsMoving",
                "isMoving",
                "moving",
                "isTraversing",
                "isRotating"
            };

            for (int i = 0; i < movingFields.Length; i++)
            {
                if (TryReadModuleBoolField(module, movingFields[i], out moving))
                    return true;
            }

            return false;
        }

        internal static bool TryGetWheelRoboticPositionValue(
            PartModule module,
            string moduleName,
            out float positionValue,
            out float deadband,
            out string sourceField)
        {
            positionValue = 0f;
            sourceField = null;

            string[] preferredScalarFields;
            if (string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal))
            {
                deadband = 0.0025f;
                preferredScalarFields = new[]
                {
                    "currentSuspensionOffset",
                    "suspensionOffset",
                    "compression",
                    "suspensionCompression",
                    "suspensionTravel"
                };
            }
            else if (string.Equals(moduleName, "ModuleWheelSteering", StringComparison.Ordinal))
            {
                deadband = 0.25f;
                preferredScalarFields = new[]
                {
                    "steeringAngle",
                    "currentSteering",
                    "steerAngle",
                    "steeringInput"
                };
            }
            else
            {
                deadband = 1f;
                preferredScalarFields = new[]
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

            for (int i = 0; i < preferredScalarFields.Length; i++)
            {
                if (TryReadModuleFloatField(module, preferredScalarFields[i], out positionValue))
                {
                    sourceField = preferredScalarFields[i];
                    return true;
                }
            }

            if (string.Equals(moduleName, "ModuleWheelSuspension", StringComparison.Ordinal))
            {
                if (TryReadModuleVector3Field(module, "suspensionPos", out Vector3 suspensionPos))
                {
                    positionValue = suspensionPos.magnitude;
                    sourceField = "suspensionPos";
                    return true;
                }
            }

            if (string.Equals(moduleName, "ModuleWheelMotorSteering", StringComparison.Ordinal) &&
                TryReadModuleFloatField(module, "steeringAngle", out positionValue))
            {
                deadband = 0.25f;
                sourceField = "steeringAngle";
                return true;
            }

            return false;
        }

        internal static bool TryGetRoboticPositionValue(
            PartModule module, string moduleName, out float positionValue,
            out float deadband, out string sourceField)
        {
            positionValue = 0f;
            sourceField = null;
            deadband = string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal)
                ? roboticLinearDeadbandMeters
                : roboticAngularDeadbandDegrees;

            if (IsWheelRoboticModuleName(moduleName))
                return TryGetWheelRoboticPositionValue(
                    module, moduleName, out positionValue, out deadband, out sourceField);

            string[] preferredScalarFields;
            if (string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal))
            {
                preferredScalarFields = new[]
                {
                    "currentPosition",
                    "position",
                    "targetPosition",
                    "traverseVelocity"
                };
            }
            else if (string.Equals(moduleName, "ModuleRoboticServoRotor", StringComparison.Ordinal))
            {
                preferredScalarFields = new[]
                {
                    "currentRPM",
                    "rpm",
                    "rpmLimit"
                };
            }
            else
            {
                preferredScalarFields = new[]
                {
                    "currentAngle",
                    "angle",
                    "targetAngle"
                };
            }

            for (int i = 0; i < preferredScalarFields.Length; i++)
            {
                if (TryReadModuleFloatField(module, preferredScalarFields[i], out positionValue))
                {
                    sourceField = preferredScalarFields[i];
                    return true;
                }
            }

            if (TryReadModuleVector3Field(module, "servoTransformPosition", out Vector3 servoPos))
            {
                positionValue = servoPos.magnitude;
                sourceField = "servoTransformPosition";
                return true;
            }

            if (TryReadModuleQuaternionField(module, "servoTransformRotation", out Quaternion servoRot))
            {
                positionValue = Quaternion.Angle(Quaternion.identity, servoRot);
                sourceField = "servoTransformRotation";
                return true;
            }

            return false;
        }

        private static string DescribeModuleFields(PartModule module)
        {
            if (module == null || module.Fields == null) return "<none>";

            var names = new List<string>();
            for (int i = 0; i < module.Fields.Count; i++)
            {
                BaseField field = module.Fields[i];
                if (field == null || string.IsNullOrEmpty(field.name)) continue;
                names.Add(field.name);
            }

            return names.Count == 0 ? "<none>" : string.Join(", ", names.ToArray());
        }

        internal static List<PartEvent> CheckRoboticTransition(
            ulong key, uint pid, int moduleIndex, string partName,
            bool movingSignal, float positionValue, float deadband, double ut,
            HashSet<ulong> movingSet, Dictionary<ulong, float> lastPositionMap,
            Dictionary<ulong, double> lastSampleMap, double sampleIntervalSeconds)
        {
            var events = new List<PartEvent>();
            bool emittedEvent = false;
            bool wasMoving = movingSet.Contains(key);

            float lastPosition;
            bool hadLastPosition = lastPositionMap.TryGetValue(key, out lastPosition);
            float delta = hadLastPosition ? Mathf.Abs(positionValue - lastPosition) : float.PositiveInfinity;
            bool changedEnough = !hadLastPosition || delta >= deadband;
            bool inferredMoving = hadLastPosition && delta >= Math.Max(deadband * 0.25f, 0.0001f);
            bool movingNow = movingSignal || inferredMoving;

            if (movingNow && !wasMoving)
            {
                movingSet.Add(key);
                lastSampleMap[key] = ut;
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RoboticMotionStarted,
                    partName = partName,
                    value = positionValue,
                    moduleIndex = moduleIndex
                });
                emittedEvent = true;
            }
            else if (!movingNow && wasMoving)
            {
                movingSet.Remove(key);
                lastSampleMap.Remove(key);
                events.Add(new PartEvent
                {
                    ut = ut,
                    partPersistentId = pid,
                    eventType = PartEventType.RoboticMotionStopped,
                    partName = partName,
                    value = positionValue,
                    moduleIndex = moduleIndex
                });
                emittedEvent = true;
            }
            else if (movingNow && wasMoving)
            {
                double lastSampleUT;
                bool sampleDue = !lastSampleMap.TryGetValue(key, out lastSampleUT) ||
                    (ut - lastSampleUT) >= sampleIntervalSeconds;
                if (sampleDue && changedEnough)
                {
                    lastSampleMap[key] = ut;
                    events.Add(new PartEvent
                    {
                        ut = ut,
                        partPersistentId = pid,
                        eventType = PartEventType.RoboticPositionSample,
                        partName = partName,
                        value = positionValue,
                        moduleIndex = moduleIndex
                    });
                    emittedEvent = true;
                }
            }

            if (emittedEvent || !hadLastPosition)
                lastPositionMap[key] = positionValue;

            return events;
        }

        private void CheckRoboticState(Vessel v)
        {
            if (cachedRoboticModules == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < cachedRoboticModules.Count; i++)
            {
                var (part, module, moduleIndex, moduleName) = cachedRoboticModules[i];
                if (part == null || module == null) continue;

                ulong key = EncodeEngineKey(part.persistentId, moduleIndex);

                bool hasMovingSignal = TryGetRoboticMovingState(module, out bool movingSignal);
                bool hasPosition = TryGetRoboticPositionValue(
                    module, moduleName, out float positionValue, out float deadband, out string sourceField);

                if (!hasPosition)
                {
                    if (loggedRoboticModuleKeys.Add(key))
                    {
                        ParsekLog.Verbose("Recorder", $"Robotics: unable to sample '{part.partInfo?.name}' pid={part.persistentId} " +
                            $"midx={moduleIndex} module={moduleName}; fields=[{DescribeModuleFields(module)}]");
                    }
                    continue;
                }

                if (loggedRoboticModuleKeys.Add(key))
                {
                    string movingSource = hasMovingSignal ? "module-flag" : "inferred";
                    ParsekLog.Verbose("Recorder", $"Robotics: tracking '{part.partInfo?.name}' pid={part.persistentId} " +
                        $"midx={moduleIndex} module={moduleName} source={sourceField} " +
                        $"deadband={deadband:F3} sampleHz=4.0 moving={movingSource}");
                }

                var events = CheckRoboticTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    movingSignal, positionValue, deadband, ut,
                    activeRoboticKeys, lastRoboticPosition, lastRoboticSampleUT, roboticSampleIntervalSeconds);

                for (int e = 0; e < events.Count; e++)
                {
                    PartEvents.Add(events[e]);
                    ParsekLog.Verbose("Recorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F3}");
                }
            }
        }

        #endregion

        #region Atmosphere Boundary Detection

        /// <summary>
        /// Pure testable function: determines whether a recording should split at the atmosphere boundary.
        /// Requires both sustained time (hysteresisSeconds) AND distance beyond boundary (hysteresisMeters).
        /// </summary>
        internal static bool ShouldSplitAtAtmosphereBoundary(
            bool wasInAtmo, double altitude, double atmosphereDepth,
            bool pendingCross, double pendingUT, double currentUT,
            double hysteresisSeconds = 3.0, double hysteresisMeters = 1000.0)
        {
            if (atmosphereDepth <= 0) return false; // no atmosphere

            bool nowInAtmo = altitude < atmosphereDepth;
            if (nowInAtmo == wasInAtmo)
                return false; // no boundary crossed

            // Check distance beyond boundary
            double distBeyond = nowInAtmo
                ? (atmosphereDepth - altitude)
                : (altitude - atmosphereDepth);
            if (distBeyond < hysteresisMeters)
                return false; // not far enough past boundary

            // Check sustained time
            if (!pendingCross)
                return false; // first detection — caller should start the timer

            return (currentUT - pendingUT) >= hysteresisSeconds;
        }

        /// <summary>
        /// Called every physics frame to detect atmosphere boundary crossings.
        /// Sets AtmosphereBoundaryCrossed and EnteredAtmosphere when confirmed.
        /// </summary>
        public void CheckAtmosphereBoundary(Vessel v)
        {
            if (!IsRecording || isOnRails) return;
            if (v == null || v.mainBody == null || !v.mainBody.atmosphere) return;

            double altitude = v.altitude;
            double atmoDepth = v.mainBody.atmosphereDepth;
            bool nowInAtmo = altitude < atmoDepth;
            double currentUT = Planetarium.GetUniversalTime();

            if (nowInAtmo == wasInAtmosphere)
            {
                // No boundary — reset pending if we drifted back
                if (atmosphereBoundaryPending)
                    ParsekLog.Verbose("Recorder", $"Atmosphere boundary pending reset — drifted back to same side (inAtmo={nowInAtmo})");
                atmosphereBoundaryPending = false;
                return;
            }

            // Boundary detected — check hysteresis
            if (!atmosphereBoundaryPending)
            {
                // Start the timer
                atmosphereBoundaryPending = true;
                atmosphereBoundaryPendingUT = currentUT;
                ParsekLog.Verbose("Recorder", $"Atmosphere boundary detected — starting hysteresis timer " +
                    $"(body={v.mainBody.name}, {(nowInAtmo ? "entering" : "exiting")}, alt={altitude:F0}m, atmoDepth={atmoDepth:F0}m)");
                return;
            }

            if (ShouldSplitAtAtmosphereBoundary(
                wasInAtmosphere, altitude, atmoDepth,
                atmosphereBoundaryPending, atmosphereBoundaryPendingUT, currentUT))
            {
                // Confirmed crossing — force-sample a boundary point to ensure >= 2 points
                SamplePosition(v);

                AtmosphereBoundaryCrossed = true;
                EnteredAtmosphere = nowInAtmo;
                wasInAtmosphere = nowInAtmo;
                atmosphereBoundaryPending = false;
                ParsekLog.Info("Recorder", $"Atmosphere boundary confirmed: {(nowInAtmo ? "entered" : "exited")} " +
                    $"atmosphere of {v.mainBody.name} at alt {altitude:F0}m " +
                    $"(hysteresis: {(currentUT - atmosphereBoundaryPendingUT):F1}s, {Math.Abs(altitude - atmoDepth):F0}m past boundary)");
            }
        }

        /// <summary>
        /// Reseeds atmosphere state from current vessel. Call on SOI change and off-rails.
        /// </summary>
        private void ReseedAtmosphereState(Vessel v)
        {
            bool oldState = wasInAtmosphere;
            if (v == null || v.mainBody == null)
            {
                wasInAtmosphere = false;
            }
            else
            {
                wasInAtmosphere = v.mainBody.atmosphere && v.altitude < v.mainBody.atmosphereDepth;
            }
            atmosphereBoundaryPending = false;
            AtmosphereBoundaryCrossed = false;
            if (v?.mainBody != null)
            {
                ParsekLog.Verbose("Recorder", $"Atmosphere state reseeded: body={v.mainBody.name}, " +
                    $"inAtmo={wasInAtmosphere} (was {oldState}), " +
                    $"hasAtmo={v.mainBody.atmosphere}, alt={v.altitude:F0}m" +
                    (v.mainBody.atmosphere ? $", atmoDepth={v.mainBody.atmosphereDepth:F0}m" : ""));
            }
        }

        #endregion

        /// <summary>
        /// Captures a quicksave for rewind at recording start. Saves via KSP API
        /// (which writes to root saves dir), then moves to Parsek/Saves/ subdirectory.
        /// </summary>
        private void CaptureRewindSave(Vessel v, bool isPromotion)
        {
            if (isPromotion)
            {
                ParsekLog.Verbose("Recorder", "Rewind save skipped: tree branch/chain promotion");
                return;
            }

            // Clean up orphaned rewind save from a previous aborted recording
            if (!string.IsNullOrEmpty(RewindSaveFileName))
            {
                try
                {
                    string oldPath = RecordingPaths.ResolveSaveScopedPath(
                        RecordingPaths.BuildRewindSaveRelativePath(RewindSaveFileName));
                    if (!string.IsNullOrEmpty(oldPath) && System.IO.File.Exists(oldPath))
                        System.IO.File.Delete(oldPath);
                    ParsekLog.Info("Recorder",
                        $"Deleted orphaned rewind save: {RewindSaveFileName}");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Recorder",
                        $"Failed to delete orphaned rewind save: {ex.Message}");
                }
                RewindSaveFileName = null;
            }

            string shortId = Guid.NewGuid().ToString("N").Substring(0, 6);
            string saveFileName = $"parsek_rw_{shortId}";

            try
            {
                // Save to root saves dir (only KSP API path available)
                string result = GamePersistence.SaveGame(saveFileName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Error("Recorder", "Failed to capture rewind save: SaveGame returned null");
                    return;
                }

                // Move from root to Parsek/Saves/
                string savesDir = System.IO.Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");
                string rootPath = System.IO.Path.Combine(savesDir, saveFileName + ".sfs");

                string rewindDir = RecordingPaths.EnsureRewindSavesDirectory();
                if (string.IsNullOrEmpty(rewindDir))
                {
                    ParsekLog.Error("Recorder", "Failed to capture rewind save: cannot create Parsek/Saves/ directory");
                    try { System.IO.File.Delete(rootPath); } catch { }
                    return;
                }

                string destPath = System.IO.Path.Combine(rewindDir, saveFileName + ".sfs");
                System.IO.File.Move(rootPath, destPath);

                RewindSaveFileName = saveFileName;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Recorder", $"Failed to capture rewind save: {ex.Message}");
                return;
            }

            // Capture reserved resource snapshot
            var reserved = ResourceBudget.ComputeTotalFullCost(
                RecordingStore.CommittedRecordings,
                MilestoneStore.Milestones,
                RecordingStore.CommittedTrees);
            RewindReservedFunds = reserved.reservedFunds;
            RewindReservedScience = reserved.reservedScience;
            RewindReservedRep = (float)reserved.reservedReputation;

            ParsekLog.Info("Recorder",
                $"Captured rewind save at UT {Planetarium.GetUniversalTime()}: vessel \"{v.vesselName}\" in {v.situation} (save: {saveFileName})");
        }

        public void StartRecording(bool isPromotion = false)
        {
            if (Time.timeScale < 0.01f)
            {
                ParsekLog.Warn("Recorder", "Cannot start recording while paused");
                ParsekLog.ScreenMessage("Cannot record while paused", 2f);
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Recorder", "Cannot start recording: no active vessel");
                return;
            }

            Recording.Clear();
            OrbitSegments.Clear();
            PartEvents.Clear();
            ResetPartEventTrackingState(v);

            LogVisualRecordingCoverage(v);

            // Capture pre-launch resource snapshot BEFORE recording starts
            // (skip for promotion — resources belong to the tree root)
            if (!isPromotion)
            {
                PreLaunchFunds = 0;
                PreLaunchScience = 0;
                PreLaunchReputation = 0;
                try
                {
                    if (Funding.Instance != null)
                        PreLaunchFunds = Funding.Instance.Funds;
                    if (ResearchAndDevelopment.Instance != null)
                        PreLaunchScience = ResearchAndDevelopment.Instance.Science;
                    if (Reputation.Instance != null)
                        PreLaunchReputation = Reputation.Instance.reputation;
                }
                catch { }
            }

            // Capture rewind save (quicksave stored in Parsek/Saves/)
            CaptureRewindSave(v, isPromotion);

            IsRecording = true;
            isOnRails = false;
            VesselDestroyedDuringRecording = false;
            CaptureAtStop = null;
            TransitionToBackgroundPending = false;
            RecordingVesselId = v.persistentId;
            RecordingStartedAsEva = v.isEVA;
            lastRecordedUT = -1;
            lastRecordedVelocity = Vector3.zero;

            hasPersistentRotation = AssemblyLoader.loadedAssemblies.Any(
                a => a.name == "PersistentRotation");
            ParsekLog.Info("Recorder", $"PersistentRotation mod detected: {hasPersistentRotation}");

            // Seed atmosphere state
            wasInAtmosphere = v.mainBody != null && v.mainBody.atmosphere
                && v.altitude < v.mainBody.atmosphereDepth;
            atmosphereBoundaryPending = false;
            AtmosphereBoundaryCrossed = false;
            EnteredAtmosphere = false;
            SoiChangePending = false;
            SoiChangeFromBody = null;
            ParsekLog.Verbose("Recorder", $"Boundary detection initialized: body={v.mainBody?.name}, " +
                $"inAtmo={wasInAtmosphere}, hasAtmo={v.mainBody?.atmosphere}, alt={v.altitude:F0}m");

            // Log surface-relative rotation for synthetic recording calibration
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info("Recorder",
                $"srfRelRotation=({v.srfRelRotation.x.ToString("R", ic)},{v.srfRelRotation.y.ToString("R", ic)}," +
                $"{v.srfRelRotation.z.ToString("R", ic)},{v.srfRelRotation.w.ToString("R", ic)}) " +
                $"UT={Planetarium.GetUniversalTime().ToString("R", ic)}");

            // Insert boundary anchor from previous chain segment if present.
            // Must come AFTER the lastRecordedUT reset above so the anchor's
            // UT is preserved (prevents duplicate-UT with the first live sample).
            if (BoundaryAnchor.HasValue)
            {
                var anchor = BoundaryAnchor.Value;
                // Nudge UT backward by a tiny epsilon to maintain strict monotonicity
                // if the anchor UT would equal the first live sample's UT
                double anchorUT = anchor.ut - 0.001;
                anchor.ut = anchorUT;
                Recording.Add(anchor);
                lastRecordedUT = anchorUT;
                lastRecordedVelocity = anchor.velocity;
                BoundaryAnchor = null;
                ParsekLog.Verbose("Recorder", $"Boundary anchor inserted at UT {anchorUT:F3}");
            }
            RefreshBackupSnapshot(v, "record_start", force: true);
            initialGhostVisualSnapshot = lastGoodVesselSnapshot != null
                ? lastGoodVesselSnapshot.CreateCopy()
                : VesselSpawner.TryBackupSnapshot(v);

            // Check if vessel is already on rails (e.g. started recording during time warp)
            if (v.packed)
            {
                // Take one boundary point first
                SamplePosition(v);

                currentOrbitSegment = new OrbitSegment
                {
                    startUT = Planetarium.GetUniversalTime(),
                    inclination = v.orbit.inclination,
                    eccentricity = v.orbit.eccentricity,
                    semiMajorAxis = v.orbit.semiMajorAxis,
                    longitudeOfAscendingNode = v.orbit.LAN,
                    argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                    meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                    epoch = v.orbit.epoch,
                    bodyName = v.mainBody.name
                };

                // Capture orbital-frame rotation (vessel is packed, so rb is null — no angular velocity)
                Vector3d orbVel = v.obt_velocity;
                Vector3d radialOut = (v.CoMD - v.mainBody.position).normalized;
                currentOrbitSegment.orbitalFrameRotation =
                    TrajectoryMath.ComputeOrbitalFrameRotation(v.transform.rotation, orbVel, radialOut);

                isOnRails = true;
                ParsekLog.Info("Recorder",
                    $"Recording started on rails — orbit segment (body={v.mainBody.name}, ofrRot={currentOrbitSegment.orbitalFrameRotation})");
            }

            // Register the Harmony patch to call us each physics frame
            Patches.PhysicsFramePatch.ActiveRecorder = this;

            SubscribePartEvents();

            ParsekLog.Info("Recorder", $"Recording started (physics-frame sampling{(isPromotion ? ", promotion" : "")})");
            if (!isPromotion)
                ParsekLog.ScreenMessage("Recording STARTED", 2f);
        }

        /// <summary>
        /// Resets all part event tracking state and rebuilds module caches for the given vessel.
        /// Extracted from StartRecording for reuse by the promotion path.
        /// </summary>
        private void ResetPartEventTrackingState(Vessel v)
        {
            parachuteStates.Clear();
            jettisonedShrouds.Clear();
            jettisonNameRawCache.Clear();
            parsedJettisonNamesCache.Clear();
            extendedDeployables.Clear();
            lightsOn.Clear();
            blinkingLights.Clear();
            lightBlinkRates.Clear();
            deployedGear.Clear();
            openCargoBays.Clear();
            deployedFairings.Clear();
            deployedLadders.Clear();
            deployedAnimationGroups.Clear();
            deployedAnimateGenericModules.Clear();
            deployedAeroSurfaceModules.Clear();
            deployedControlSurfaceModules.Clear();
            deployedRobotArmScannerModules.Clear();
            hotAnimateHeatModules.Clear();
            loggedLadderClassificationMisses.Clear();
            loggedAnimationGroupClassificationMisses.Clear();
            loggedAnimateGenericClassificationMisses.Clear();
            loggedAeroSurfaceClassificationMisses.Clear();
            loggedControlSurfaceClassificationMisses.Clear();
            loggedRobotArmScannerClassificationMisses.Clear();
            loggedAnimateHeatClassificationMisses.Clear();
            loggedCargoBayDeployIndexIssues.Clear();
            loggedCargoBayAnimationIssues.Clear();
            loggedCargoBayClosedPositionIssues.Clear();
            loggedFairingReadFailures.Clear();
            cachedEngines = CacheEngineModules(v);
            activeEngineKeys = new HashSet<ulong>();
            lastThrottle = new Dictionary<ulong, float>();
            loggedEngineModuleKeys.Clear();
            cachedRcsModules = CacheRcsModules(v);
            activeRcsKeys = new HashSet<ulong>();
            lastRcsThrottle = new Dictionary<ulong, float>();
            loggedRcsModuleKeys.Clear();
            cachedRoboticModules = CacheRoboticModules(v);
            activeRoboticKeys = new HashSet<ulong>();
            lastRoboticPosition = new Dictionary<ulong, float>();
            lastRoboticSampleUT = new Dictionary<ulong, double>();
            loggedRoboticModuleKeys = new HashSet<ulong>();
            ParsekLog.Info("Recorder",
                $"Module caches seeded for vessel pid={v.persistentId}: engines={cachedEngines?.Count ?? 0}, " +
                $"rcs={cachedRcsModules?.Count ?? 0}, robotics={cachedRoboticModules?.Count ?? 0}");

            // Seed already-deployed fairings so we don't emit false events at first poll
            if (v != null && v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null) continue;
                    var fairing = p.FindModuleImplementing<ModuleProceduralFairing>();
                    if (fairing == null) continue;
                    try
                    {
                        if (fairing.GetScalar >= 0.5f)
                            deployedFairings.Add(p.persistentId);
                    }
                    catch { }
                }
            }
        }

        public void StopRecording()
        {
            // Finalize in-progress orbit segment
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                isOnRails = false;

                // Record a boundary point at stop time
                Vessel v = FlightGlobals.ActiveVessel;
                if (v != null) SamplePosition(v);
            }

            // Disconnect from Harmony patch
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();
            IsRecording = false;

            // Sort part events chronologically (mixed event sources may produce non-chronological order)
            PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));

            // Capture persistence artifacts at stop-time so later scene changes
            // don't depend on whatever vessel is currently active.
            CaptureAtStop = new RecordingStore.Recording
            {
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,

                VesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                Points = new List<TrajectoryPoint>(Recording),
                OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                PartEvents = new List<PartEvent>(PartEvents),
                PreLaunchFunds = PreLaunchFunds,
                PreLaunchScience = PreLaunchScience,
                PreLaunchReputation = PreLaunchReputation,
                RewindSaveFileName = RewindSaveFileName,
                RewindReservedFunds = RewindReservedFunds,
                RewindReservedScience = RewindReservedScience,
                RewindReservedRep = RewindReservedRep
            };
            // Clear after first capture — prevents chain children from inheriting root's rewind save
            RewindSaveFileName = null;

            VesselSpawner.SnapshotVessel(
                CaptureAtStop,
                VesselDestroyedDuringRecording,
                destroyedFallbackSnapshot: lastGoodVesselSnapshot);
            CaptureAtStop.GhostVisualSnapshot = initialGhostVisualSnapshot != null
                ? initialGhostVisualSnapshot.CreateCopy()
                : (CaptureAtStop.VesselSnapshot != null ? CaptureAtStop.VesselSnapshot.CreateCopy() : null);

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            ParsekLog.Info("Recorder", $"Recording stopped. {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
            ParsekLog.ScreenMessage($"Recording STOPPED: {Recording.Count} points", 3f);
        }

        /// <summary>
        /// Stops recording silently for a chain boundary (dock/undock/boarding).
        /// Same as StopRecording but without the "STOPPED" screen message.
        /// </summary>
        public void StopRecordingForChainBoundary()
        {
            // Finalize in-progress orbit segment
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                isOnRails = false;

                Vessel v = FlightGlobals.ActiveVessel;
                if (v != null) SamplePosition(v);
            }

            Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();
            IsRecording = false;

            PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));

            CaptureAtStop = new RecordingStore.Recording
            {
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,

                VesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                Points = new List<TrajectoryPoint>(Recording),
                OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                PartEvents = new List<PartEvent>(PartEvents),
                PreLaunchFunds = PreLaunchFunds,
                PreLaunchScience = PreLaunchScience,
                PreLaunchReputation = PreLaunchReputation,
                RewindSaveFileName = RewindSaveFileName,
                RewindReservedFunds = RewindReservedFunds,
                RewindReservedScience = RewindReservedScience,
                RewindReservedRep = RewindReservedRep
            };
            // Clear after first capture
            RewindSaveFileName = null;

            VesselSpawner.SnapshotVessel(
                CaptureAtStop,
                VesselDestroyedDuringRecording,
                destroyedFallbackSnapshot: lastGoodVesselSnapshot);
            CaptureAtStop.GhostVisualSnapshot = initialGhostVisualSnapshot != null
                ? initialGhostVisualSnapshot.CreateCopy()
                : (CaptureAtStop.VesselSnapshot != null ? CaptureAtStop.VesselSnapshot.CreateCopy() : null);

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            ParsekLog.Info("Recorder", $"Recording stopped (chain boundary). {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
        }

        /// <summary>
        /// Re-enables recording after a false-alarm split detection (e.g., joint break that
        /// only produced debris, undock of a non-trackable vessel). Restores the recorder to
        /// the active state it was in before StopRecordingForChainBoundary() was called.
        /// The internal Recording/OrbitSegments/PartEvents lists are still intact since
        /// StopRecordingForChainBoundary only copies them into CaptureAtStop but does not clear them.
        /// </summary>
        public void ResumeAfterFalseAlarm()
        {
            if (IsRecording)
            {
                ParsekLog.Warn("Recorder", "ResumeAfterFalseAlarm called while already recording — ignoring");
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Recorder", "ResumeAfterFalseAlarm: no active vessel — cannot resume");
                return;
            }

            IsRecording = true;

            // Restore rewind save from CaptureAtStop — StopRecordingForChainBoundary
            // cleared RewindSaveFileName after copying it to CaptureAtStop, so if we
            // resume, we need to reclaim it before discarding CaptureAtStop.
            if (CaptureAtStop != null && !string.IsNullOrEmpty(CaptureAtStop.RewindSaveFileName)
                && string.IsNullOrEmpty(RewindSaveFileName))
            {
                RewindSaveFileName = CaptureAtStop.RewindSaveFileName;
                RewindReservedFunds = CaptureAtStop.RewindReservedFunds;
                RewindReservedScience = CaptureAtStop.RewindReservedScience;
                RewindReservedRep = CaptureAtStop.RewindReservedRep;
            }

            CaptureAtStop = null;
            HasPendingJointBreakCheck = false;

            // Re-register the Harmony patch and part event hooks
            Patches.PhysicsFramePatch.ActiveRecorder = this;
            SubscribePartEvents();

            // Update RecordingVesselId in case the vessel PID changed (unlikely but defensive)
            RecordingVesselId = v.persistentId;

            ParsekLog.Info("Recorder", $"Recording resumed after false alarm ({Recording.Count} points preserved)");
        }

        /// <summary>
        /// Called by the Harmony postfix on each physics frame for the active vessel.
        /// </summary>
        public void OnPhysicsFrame(Vessel v)
        {
            if (!IsRecording) return;
            if (isOnRails) return;

            if (v.persistentId != RecordingVesselId)
            {
                // 1. Dock merge guard — must be first. DockMergePending was set by
                //    OnPartCouple; let the existing capture+stop flow handle it below.
                //    Do NOT enter tree decision logic for dock PID changes.
                VesselSwitchDecision decision = DockMergePending
                    ? VesselSwitchDecision.DockMerge
                    : DecideOnVesselSwitch(
                        RecordingVesselId, v.persistentId, v.isEVA, RecordingStartedAsEva,
                        UndockSiblingPid, activeTree: ActiveTree);

                // 2. ContinueOnEva — early return (existing, unchanged)
                if (decision == VesselSwitchDecision.ContinueOnEva)
                {
                    RecordingVesselId = v.persistentId;
                    SamplePosition(v);
                    RefreshBackupSnapshot(v, "eva_switch", force: true);
                    ParsekLog.Verbose("Recorder", $"Recording switched to EVA vessel (pid={v.persistentId})");
                    return;
                }

                // 3. Tree decisions — early return BEFORE CaptureAtStop
                if (decision == VesselSwitchDecision.TransitionToBackground)
                {
                    TransitionToBackground();
                    TransitionToBackgroundPending = true;
                    ParsekLog.Verbose("Recorder", $"Tree: transition to background " +
                        $"(was pid={RecordingVesselId}, now pid={v.persistentId})");
                    return;
                }
                if (decision == VesselSwitchDecision.PromoteFromBackground)
                {
                    // Cannot promote here — vessel may not be loaded yet.
                    // TransitionToBackground the current recorder, let onVesselSwitchComplete handle promotion.
                    TransitionToBackground();
                    TransitionToBackgroundPending = true;
                    ParsekLog.Verbose("Recorder", $"Tree: promote from background " +
                        $"(was pid={RecordingVesselId}, now pid={v.persistentId}) — backgrounding current, promotion deferred");
                    return;
                }

                // 4. Existing CaptureAtStop + IsRecording=false block (unchanged)
                Vessel recordedVessel = FindVesselByPid(RecordingVesselId);

                CaptureAtStop = new RecordingStore.Recording
                {
                    RecordingId = System.Guid.NewGuid().ToString("N"),
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
    
                    VesselName = recordedVessel != null ? recordedVessel.vesselName : v.vesselName,
                    Points = new List<TrajectoryPoint>(Recording),
                    OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                    PartEvents = new List<PartEvent>(PartEvents),
                    PreLaunchFunds = PreLaunchFunds,
                    PreLaunchScience = PreLaunchScience,
                    PreLaunchReputation = PreLaunchReputation,
                    RewindSaveFileName = RewindSaveFileName,
                    RewindReservedFunds = RewindReservedFunds,
                    RewindReservedScience = RewindReservedScience,
                    RewindReservedRep = RewindReservedRep
                };
                // Clear after first capture
                RewindSaveFileName = null;
                VesselSpawner.SnapshotVessel(
                    CaptureAtStop,
                    VesselDestroyedDuringRecording || recordedVessel == null,
                    recordedVessel,
                    lastGoodVesselSnapshot);
                CaptureAtStop.GhostVisualSnapshot = initialGhostVisualSnapshot != null
                    ? initialGhostVisualSnapshot.CreateCopy()
                    : (CaptureAtStop.VesselSnapshot != null ? CaptureAtStop.VesselSnapshot.CreateCopy() : null);

                Patches.PhysicsFramePatch.ActiveRecorder = null;
                UnsubscribePartEvents();
                IsRecording = false;

                // 5. Existing decision dispatch (unchanged)
                if (decision == VesselSwitchDecision.ChainToVessel)
                {
                    ChainToVesselPending = true;
                    ParsekLog.Verbose("Recorder", $"EVA boarded vessel (was pid={RecordingVesselId}, now pid={v.persistentId}) — chain pending");
                    return;
                }

                if (decision == VesselSwitchDecision.DockMerge)
                {
                    DockMergePending = true;
                    ParsekLog.Verbose("Recorder", $"Dock merge detected (was pid={RecordingVesselId}, now pid={v.persistentId}) — dock pending");
                    return;
                }

                if (decision == VesselSwitchDecision.UndockSwitch)
                {
                    UndockSwitchPending = true;
                    ParsekLog.Verbose("Recorder", $"Undock sibling switch (was pid={RecordingVesselId}, now pid={v.persistentId}) — undock switch pending");
                    return;
                }

                ParsekLog.Verbose("Recorder", $"Active vessel changed during recording — auto-stopping " +
                    $"(decision={decision}, was pid={RecordingVesselId}, now pid={v.persistentId}, " +
                    $"nowIsEva={v.isEVA}, startedAsEva={RecordingStartedAsEva})");
                ParsekLog.ScreenMessage("Recording stopped — vessel changed", 3f);
                return;
            }

            // Check atmosphere boundary (before part state polling)
            CheckAtmosphereBoundary(v);

            // Poll parachute, jettison, engine, RCS, and deployable state every physics frame (before adaptive sampling skip)
            CheckParachuteState(v);
            CheckJettisonState(v);
            CheckEngineState(v);
            CheckRcsState(v);
            CheckDeployableState(v);
            CheckLadderState(v);
            CheckAnimationGroupState(v);
            CheckAeroSurfaceState(v);
            CheckControlSurfaceState(v);
            CheckRobotArmScannerState(v);
            CheckAnimateHeatState(v);
            CheckAnimateGenericState(v);
            CheckLightState(v);
            CheckGearState(v);
            CheckCargoBayState(v);
            CheckFairingState(v);
            CheckRoboticState(v);

            // Krakensbane-corrected true velocity
            Vector3 currentVelocity = (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(currentVelocity, lastRecordedVelocity,
                Planetarium.GetUniversalTime(), lastRecordedUT,
                maxSampleInterval, velocityDirThreshold, speedChangeThreshold))
            {
                ParsekLog.VerboseRateLimited("Recorder", "sample-skipped",
                    $"Sample skipped at ut={Planetarium.GetUniversalTime():F2}; waiting for threshold trigger", 2.0);
                return;
            }

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = currentVelocity,
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            Recording.Add(point);
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;

            RefreshBackupSnapshot(v, "periodic");

            if (Recording.Count % 10 == 0)
            {
                ParsekLog.Verbose("Recorder", $"Recorded point #{Recording.Count}: {point}");
            }
        }

        /// <summary>
        /// Force-record a boundary point (used at on-rails/off-rails transitions).
        /// </summary>
        public void SamplePosition(Vessel v)
        {
            if (v == null)
            {
                ParsekLog.VerboseRateLimited("Recorder", "sample-null-vessel",
                    "SamplePosition called with null vessel");
                return;
            }

            Vector3 currentVelocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = currentVelocity,
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            Recording.Add(point);
            lastRecordedUT = point.ut;
            lastRecordedVelocity = point.velocity;
        }

        public void OnVesselGoOnRails(Vessel v)
        {
            if (!IsRecording) return;
            if (v != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Recorder", "on-rails-other-vessel",
                    $"OnVesselGoOnRails: ignoring non-active vessel (pid={v?.persistentId})");
                return;
            }
            if (v.persistentId != RecordingVesselId) return;

            // Record a boundary TrajectoryPoint at current UT (stitching point)
            SamplePosition(v);

            currentOrbitSegment = new OrbitSegment
            {
                startUT = Planetarium.GetUniversalTime(),
                inclination = v.orbit.inclination,
                eccentricity = v.orbit.eccentricity,
                semiMajorAxis = v.orbit.semiMajorAxis,
                longitudeOfAscendingNode = v.orbit.LAN,
                argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                epoch = v.orbit.epoch,
                bodyName = v.mainBody.name
            };

            // Capture orbital-frame-relative rotation
            Vector3d orbVel = v.obt_velocity;
            Vector3d radialOut = (v.CoMD - v.mainBody.position).normalized;
            currentOrbitSegment.orbitalFrameRotation =
                TrajectoryMath.ComputeOrbitalFrameRotation(v.transform.rotation, orbVel, radialOut);

            // Capture angular velocity if PersistentRotation is active and vessel is spinning
            if (hasPersistentRotation && v.rootPart != null && v.rootPart.rb != null)
            {
                Vector3 worldAngVel = v.angularVelocity;
                if (worldAngVel.magnitude > TrajectoryMath.SpinThreshold)
                {
                    currentOrbitSegment.angularVelocity =
                        Quaternion.Inverse(v.transform.rotation) * worldAngVel;
                    ParsekLog.Verbose("Recorder",
                        $"Spinning vessel detected (|angVel|={worldAngVel.magnitude:F4}), recording angular velocity for spin-forward");
                }
            }

            isOnRails = true;
            ParsekLog.Verbose("Recorder",
                $"Vessel went on rails — orbit segment (body={v.mainBody.name}, " +
                $"ofrRot={currentOrbitSegment.orbitalFrameRotation}, " +
                $"angVel={currentOrbitSegment.angularVelocity}, " +
                $"persistentRotation={hasPersistentRotation})");
        }

        public void OnVesselGoOffRails(Vessel v)
        {
            if (!IsRecording || !isOnRails) return;
            if (v != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Recorder", "off-rails-other-vessel",
                    $"OnVesselGoOffRails: ignoring non-active vessel (pid={v?.persistentId})");
                return;
            }
            if (v.persistentId != RecordingVesselId) return;

            // Finalize orbit segment
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);
            isOnRails = false;

            // Record a boundary TrajectoryPoint at current UT.
            // Note: KSP may not perfectly preserve SAS orientation across on/off-rails,
            // so the vessel's actual rotation here can differ slightly from the orbital-frame
            // rotation stored at the on-rails boundary. This causes a sub-degree rotation
            // discontinuity in the ghost at this transition — acceptable.
            SamplePosition(v);

            // Reseed atmosphere state for the current body
            ReseedAtmosphereState(v);

            ParsekLog.Verbose("Recorder", $"Vessel went off rails — orbit segment closed " +
                $"(UT {currentOrbitSegment.startUT:F0}-{currentOrbitSegment.endUT:F0})");
        }

        public void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            if (!IsRecording || !isOnRails) return;
            if (data.host != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Recorder", "soi-other-vessel",
                    $"OnVesselSOIChanged: ignoring non-active vessel (pid={data.host?.persistentId})");
                return;
            }
            if (data.host.persistentId != RecordingVesselId) return;

            // Close current orbit segment in old SOI
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);

            // Start new orbit segment in new SOI
            var v = data.host;
            currentOrbitSegment = new OrbitSegment
            {
                startUT = Planetarium.GetUniversalTime(),
                inclination = v.orbit.inclination,
                eccentricity = v.orbit.eccentricity,
                semiMajorAxis = v.orbit.semiMajorAxis,
                longitudeOfAscendingNode = v.orbit.LAN,
                argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                epoch = v.orbit.epoch,
                bodyName = v.mainBody.name
            };

            // Capture orbital-frame-relative rotation for new SOI
            Vector3d orbVel = v.obt_velocity;
            Vector3d radialOut = (v.CoMD - v.mainBody.position).normalized;
            currentOrbitSegment.orbitalFrameRotation =
                TrajectoryMath.ComputeOrbitalFrameRotation(v.transform.rotation, orbVel, radialOut);

            // Note: vessel is on rails during SOI change — rb is null, so angular velocity
            // is unavailable here. Spin data is only captured at the initial go-on-rails boundary.
            // A spinning vessel crossing an SOI boundary will use orbital-frame rotation (not spin-forward)
            // for the new segment. This is an acceptable v1 limitation.

            // Reseed atmosphere state for the new body
            ReseedAtmosphereState(v);

            // Flag for ParsekFlight to trigger chain split in Update()
            SoiChangePending = true;
            SoiChangeFromBody = data.from.name;

            ParsekLog.Verbose("Recorder",
                $"SOI changed {data.from.name} → {data.to.name} — new segment ofrRot={currentOrbitSegment.orbitalFrameRotation}, " +
                $"angVel={currentOrbitSegment.angularVelocity}");
        }

        public void OnVesselWillDestroy(Vessel v)
        {
            if (!IsRecording) return;
            if (FlightGlobals.ActiveVessel == null)
            {
                ParsekLog.Verbose("Recorder", "OnVesselWillDestroy: no active vessel — skipping");
                return;
            }
            if (v == FlightGlobals.ActiveVessel)
            {
                // Finalize in-progress orbit segment if on rails
                if (isOnRails)
                {
                    currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                    OrbitSegments.Add(currentOrbitSegment);
                    isOnRails = false;
                }

                VesselDestroyedDuringRecording = true;
                RefreshBackupSnapshot(v, "destroy_event", force: true);
                ParsekLog.Warn("Recorder", "Active vessel destroyed during recording");
            }
        }

        /// <summary>
        /// Force-stop recording during scene change (no boundary point needed).
        /// </summary>
        public void ForceStop()
        {
            // Intentionally does not set CaptureAtStop. Forced stops happen during
            // scene transitions where vessel state may already be changing/unreliable;
            // ParsekFlight falls back to scene-change snapshot capture in that path.
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                isOnRails = false;
            }

            Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();
            IsRecording = false;
            PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            // Keep stop-line contract identical to normal StopRecording so live
            // KSP.log validation does not require a manual in-flight stop.
            ParsekLog.Info("Recorder", $"Recording stopped. {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
            ParsekLog.Info("Recorder", "Auto-stopped recording due to scene change");
        }

        /// <summary>
        /// Transitions the current recording to background mode (tree vessel switch).
        /// Captures an orbit segment for the background phase. IsRecording stays true
        /// but the Harmony patch is disconnected so OnPhysicsFrame won't fire.
        /// </summary>
        public void TransitionToBackground()
        {
            if (!IsRecording) return;

            Vessel v = FindVesselByPid(RecordingVesselId);

            // Sample a boundary point if vessel is still accessible
            if (v != null)
                SamplePosition(v);

            // Finalize any in-progress orbit segment (if already on rails during switch)
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                isOnRails = false;
            }

            // Start a new orbit segment for the background phase
            if (v != null && v.orbit != null)
            {
                currentOrbitSegment = new OrbitSegment
                {
                    startUT = Planetarium.GetUniversalTime(),
                    inclination = v.orbit.inclination,
                    eccentricity = v.orbit.eccentricity,
                    semiMajorAxis = v.orbit.semiMajorAxis,
                    longitudeOfAscendingNode = v.orbit.LAN,
                    argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                    meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                    epoch = v.orbit.epoch,
                    bodyName = v.mainBody?.name ?? "Kerbin"
                };
                isOnRails = true;
            }

            // Disconnect from Harmony patch (stop physics-frame sampling)
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();

            // NOTE: IsRecording stays TRUE. The recording is still conceptually active,
            // just in background mode. The Harmony patch is disconnected so OnPhysicsFrame
            // won't fire.
            ParsekLog.Info("Recorder", $"Transitioned to background (pid={RecordingVesselId}, " +
                $"points={Recording.Count}, orbitSegments={OrbitSegments.Count})");
        }

        /// <summary>
        /// Closes any open orbit segment (isOnRails=true) by setting endUT to current UT
        /// and adding it to OrbitSegments. Called before flushing recorder data to a tree
        /// recording so that background orbit segments are not lost.
        /// </summary>
        public void FinalizeOpenOrbitSegment()
        {
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                isOnRails = false;
            }
        }

        internal static Vessel FindVesselByPid(uint pid)
        {
            if (FlightGlobals.Vessels == null) return null;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel vessel = FlightGlobals.Vessels[i];
                if (vessel != null && vessel.persistentId == pid)
                    return vessel;
            }
            return null;
        }

        private void RefreshBackupSnapshot(Vessel vessel, string reason, bool force = false)
        {
            if (vessel == null) return;

            double ut = Planetarium.GetUniversalTime();
            if (!ShouldRefreshSnapshot(lastSnapshotRefreshUT, ut, snapshotRefreshIntervalUT, force))
                return;

            float startMs = Time.realtimeSinceStartup * 1000f;
            var snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            float elapsedMs = Time.realtimeSinceStartup * 1000f - startMs;

            if (snapshot != null)
            {
                lastGoodVesselSnapshot = snapshot;
                lastSnapshotRefreshUT = ut;
            }
            else
            {
                ParsekLog.Warn("Recorder", $"Snapshot backup failed ({reason}): pid={vessel.persistentId}, " +
                    $"loaded={vessel.loaded}, packed={vessel.packed} — using previous snapshot");
            }

            if (elapsedMs >= snapshotPerfLogThresholdMs)
            {
                ParsekLog.Verbose("Recorder", 
                    $"Snapshot backup cost ({reason}): {elapsedMs:F1}ms " +
                    $"pid={vessel.persistentId}, points={Recording.Count}");
            }
        }

        internal static bool ShouldRefreshSnapshot(
            double lastSnapshotRefreshUT, double currentUT, double intervalUT, bool force)
        {
            if (force) return true;
            if (lastSnapshotRefreshUT == double.MinValue) return true;
            return currentUT - lastSnapshotRefreshUT >= intervalUT;
        }

        internal static VesselSwitchDecision DecideOnVesselSwitch(
            uint recordingVesselId, uint currentVesselId, bool currentIsEva,
            bool recordingStartedAsEva, uint undockSiblingPid = 0,
            RecordingTree activeTree = null)
        {
            if (currentVesselId == recordingVesselId)
                return VesselSwitchDecision.None;
            // Player switched to undocked sibling vessel
            if (undockSiblingPid != 0 && currentVesselId == undockSiblingPid)
                return VesselSwitchDecision.UndockSwitch;
            // Avoid mixed-mode tracks (ship trajectory followed by EVA walking).
            // Continue-on-EVA is only valid for recordings that started as EVA.
            if (currentIsEva && recordingStartedAsEva)
                return VesselSwitchDecision.ContinueOnEva;
            // EVA kerbal boarded a vessel — potential chain continuation.
            // ParsekFlight checks activeChainId before actually continuing the chain;
            // if not in a chain, this is treated as a normal stop.
            if (!currentIsEva && recordingStartedAsEva)
                return VesselSwitchDecision.ChainToVessel;

            // Tree mode: switching to a vessel already tracked in the tree
            if (activeTree != null && activeTree.BackgroundMap.ContainsKey(currentVesselId))
                return VesselSwitchDecision.PromoteFromBackground;
            // Tree mode: switching to any other vessel (background the current recording)
            if (activeTree != null)
                return VesselSwitchDecision.TransitionToBackground;

            return VesselSwitchDecision.Stop;
        }
    }
}
