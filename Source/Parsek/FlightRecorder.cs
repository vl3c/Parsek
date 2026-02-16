using System;
using System.Collections.Generic;
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
            UndockSwitch    // Player switched to undocked sibling vessel
        }

        // Recording output
        public List<TrajectoryPoint> Recording { get; } = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
        public List<PartEvent> PartEvents { get; } = new List<PartEvent>();

        // Part event tracking
        private HashSet<uint> deployedParachutes = new HashSet<uint>();
        private HashSet<uint> jettisonedShrouds = new HashSet<uint>();
        private HashSet<uint> extendedDeployables = new HashSet<uint>();
        private HashSet<uint> lightsOn = new HashSet<uint>();
        private HashSet<uint> deployedGear = new HashSet<uint>();
        private HashSet<uint> openCargoBays = new HashSet<uint>();
        private HashSet<uint> deployedFairings = new HashSet<uint>();

        // Engine state tracking (key = EncodeEngineKey(pid, moduleIndex))
        private List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines;
        private HashSet<ulong> activeEngineKeys;
        private Dictionary<ulong, float> lastThrottle;

        // RCS state tracking (separate dicts from engines — keys can overlap for same part)
        private List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules;
        private HashSet<ulong> activeRcsKeys;
        private Dictionary<ulong, float> lastRcsThrottle;
        internal bool partEventsSubscribed;
        public bool IsRecording { get; private set; }
        public uint RecordingVesselId { get; private set; }
        public bool RecordingStartedAsEva { get; private set; }
        public bool VesselDestroyedDuringRecording { get; set; }
        public bool ChainToVesselPending { get; internal set; }
        public bool DockMergePending { get; set; }
        public bool UndockSwitchPending { get; set; }
        // Set by ParsekFlight to enable sibling vessel switch detection in OnPhysicsFrame
        public uint UndockSiblingPid { get; set; }
        public RecordingStore.Recording CaptureAtStop { get; private set; }
        public ConfigNode LastGoodVesselSnapshot => lastGoodVesselSnapshot;
        public ConfigNode InitialGhostVisualSnapshot => initialGhostVisualSnapshot;

        // Adaptive sampling thresholds
        private const float maxSampleInterval = 3.0f;
        private const float velocityDirThreshold = 2.0f;
        private const float speedChangeThreshold = 0.05f;
        private const double snapshotRefreshIntervalUT = 10.0;
        private const float snapshotPerfLogThresholdMs = 25.0f;
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
        private OrbitSegment currentOrbitSegment;

        #region Part Event Subscription

        private void SubscribePartEvents()
        {
            if (partEventsSubscribed) return;
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onPartJointBreak.Add(OnPartJointBreak);
            partEventsSubscribed = true;
        }

        private void UnsubscribePartEvents()
        {
            if (!partEventsSubscribed) return;
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onPartJointBreak.Remove(OnPartJointBreak);
            partEventsSubscribed = false;
        }

        private void OnPartDie(Part p)
        {
            if (!IsRecording) return;
            if (p?.vessel == null) return;
            if (p.vessel.persistentId != RecordingVesselId) return;

            bool hasChute = p.FindModuleImplementing<ModuleParachute>() != null;
            var evtType = ClassifyPartDeath(p.persistentId, hasChute, deployedParachutes);

            PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = evtType,
                partName = p.partInfo?.name ?? "unknown"
            });
            ParsekLog.Log($"Part event: {evtType} '{p.partInfo?.name}' pid={p.persistentId}");
        }

        internal static PartEventType ClassifyPartDeath(
            uint partPersistentId, bool hasParachuteModule, HashSet<uint> deployedSet)
        {
            if (hasParachuteModule && deployedSet.Remove(partPersistentId))
                return PartEventType.ParachuteDestroyed;
            return PartEventType.Destroyed;
        }

        private void OnPartJointBreak(PartJoint joint, float breakForce)
        {
            if (!IsRecording) return;
            if (joint?.Child?.vessel == null) return;
            if (joint.Child.vessel.persistentId != RecordingVesselId) return;

            PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = joint.Child.persistentId,
                eventType = PartEventType.Decoupled,
                partName = joint.Child.partInfo?.name ?? "unknown"
            });
            ParsekLog.Log($"Part event: Decoupled '{joint.Child.partInfo?.name}' pid={joint.Child.persistentId}");
        }

        internal static PartEvent? CheckParachuteTransition(
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
                    eventType = PartEventType.ParachuteDeployed,
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

                bool isDeployed = chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED ||
                                  chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED;

                var evt = CheckParachuteTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedParachutes, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Log($"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
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

        private void CheckJettisonState(Vessel v)
        {
            if (v == null || v.parts == null) return;

            double ut = Planetarium.GetUniversalTime();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var jettison = p.FindModuleImplementing<ModuleJettison>();
                if (jettison == null) continue;

                var evt = CheckJettisonTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", jettison.isJettisoned, jettisonedShrouds, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Log($"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
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
                    ParsekLog.Log($"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
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
                    ParsekLog.Log($"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
                }
            }
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
                        ParsekLog.Log($"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
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
                if (deployIdx < 0 || deployIdx >= p.Modules.Count) continue;
                var animModule = p.Modules[deployIdx] as ModuleAnimateGeneric;
                if (animModule == null) continue;

                ClassifyCargoBayState(animModule.animTime, cargo.closedPosition,
                    out bool isOpen, out bool isClosed);

                if (!isOpen && !isClosed) continue; // mid-transition or non-standard closedPosition

                var evt = CheckCargoBayTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isOpen, openCargoBays, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Log($"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
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
                catch
                {
                    continue;
                }

                var evt = CheckFairingTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedFairings, ut);
                if (evt.HasValue)
                {
                    PartEvents.Add(evt.Value);
                    ParsekLog.Log($"Part event: {evt.Value.eventType} '{evt.Value.partName}' pid={evt.Value.partPersistentId}");
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

                var events = CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, throttle,
                    activeEngineKeys, lastThrottle, ut);

                for (int e = 0; e < events.Count; e++)
                {
                    PartEvents.Add(events[e]);
                    ParsekLog.Log($"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} val={events[e].value:F2}");
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

                var events = CheckRcsTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    active, power,
                    activeRcsKeys, lastRcsThrottle, ut);

                for (int e = 0; e < events.Count; e++)
                {
                    PartEvents.Add(events[e]);
                    ParsekLog.Log($"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} val={events[e].value:F2}");
                }
            }
        }

        #endregion

        public void StartRecording()
        {
            if (Time.timeScale < 0.01f)
            {
                ParsekLog.Log("Cannot start recording while paused");
                ParsekLog.ScreenMessage("Cannot record while paused", 2f);
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Log("No active vessel to record!");
                return;
            }

            Recording.Clear();
            OrbitSegments.Clear();
            PartEvents.Clear();
            deployedParachutes.Clear();
            jettisonedShrouds.Clear();
            extendedDeployables.Clear();
            lightsOn.Clear();
            deployedGear.Clear();
            openCargoBays.Clear();
            deployedFairings.Clear();
            cachedEngines = CacheEngineModules(v);
            activeEngineKeys = new HashSet<ulong>();
            lastThrottle = new Dictionary<ulong, float>();
            cachedRcsModules = CacheRcsModules(v);
            activeRcsKeys = new HashSet<ulong>();
            lastRcsThrottle = new Dictionary<ulong, float>();

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

            IsRecording = true;
            isOnRails = false;
            VesselDestroyedDuringRecording = false;
            CaptureAtStop = null;
            RecordingVesselId = v.persistentId;
            RecordingStartedAsEva = v.isEVA;
            lastRecordedUT = -1;
            lastRecordedVelocity = Vector3.zero;

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
                ParsekLog.Log($"Boundary anchor inserted at UT {anchorUT:F3}");
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
                isOnRails = true;
                ParsekLog.Log($"Recording started on rails — capturing orbit (body={v.mainBody.name})");
            }

            // Register the Harmony patch to call us each physics frame
            Patches.PhysicsFramePatch.ActiveRecorder = this;

            SubscribePartEvents();

            ParsekLog.Log("Recording started (physics-frame sampling)");
            ParsekLog.ScreenMessage("Recording STARTED", 2f);
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
                GhostGeometryVersion = RecordingStore.CurrentGhostGeometryVersion,
                VesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                Points = new List<TrajectoryPoint>(Recording),
                OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                PartEvents = new List<PartEvent>(PartEvents)
            };
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

            ParsekLog.Log($"Recording stopped. {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
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
                GhostGeometryVersion = RecordingStore.CurrentGhostGeometryVersion,
                VesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                Points = new List<TrajectoryPoint>(Recording),
                OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                PartEvents = new List<PartEvent>(PartEvents)
            };
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

            ParsekLog.Log($"Recording stopped (chain boundary). {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
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
                VesselSwitchDecision decision = DecideOnVesselSwitch(
                    RecordingVesselId, v.persistentId, v.isEVA, RecordingStartedAsEva,
                    UndockSiblingPid);

                // Keep recording across switch to EVA. This preserves player intent for
                // launch -> EVA sequences until multi-track replay lands.
                if (decision == VesselSwitchDecision.ContinueOnEva)
                {
                    RecordingVesselId = v.persistentId;
                    SamplePosition(v);
                    RefreshBackupSnapshot(v, "eva_switch", force: true);
                    ParsekLog.Log($"Recording switched to EVA vessel (pid={v.persistentId})");
                    return;
                }

                Vessel recordedVessel = FindVesselByPid(RecordingVesselId);

                CaptureAtStop = new RecordingStore.Recording
                {
                    RecordingId = System.Guid.NewGuid().ToString("N"),
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                    GhostGeometryVersion = RecordingStore.CurrentGhostGeometryVersion,
                    VesselName = recordedVessel != null ? recordedVessel.vesselName : v.vesselName,
                    Points = new List<TrajectoryPoint>(Recording),
                    OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                    PartEvents = new List<PartEvent>(PartEvents)
                };
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

                if (decision == VesselSwitchDecision.ChainToVessel)
                {
                    ChainToVesselPending = true;
                    ParsekLog.Log($"EVA boarded vessel (was pid={RecordingVesselId}, now pid={v.persistentId}) — chain pending");
                    return;
                }

                if (decision == VesselSwitchDecision.DockMerge)
                {
                    DockMergePending = true;
                    ParsekLog.Log($"Dock merge detected (was pid={RecordingVesselId}, now pid={v.persistentId}) — dock pending");
                    return;
                }

                if (decision == VesselSwitchDecision.UndockSwitch)
                {
                    UndockSwitchPending = true;
                    ParsekLog.Log($"Undock sibling switch (was pid={RecordingVesselId}, now pid={v.persistentId}) — undock switch pending");
                    return;
                }

                ParsekLog.Log($"Active vessel changed during recording — auto-stopping " +
                    $"(decision={decision}, was pid={RecordingVesselId}, now pid={v.persistentId}, " +
                    $"nowIsEva={v.isEVA}, startedAsEva={RecordingStartedAsEva})");
                ParsekLog.ScreenMessage("Recording stopped — vessel changed", 3f);
                return;
            }

            // Poll parachute, jettison, engine, RCS, and deployable state every physics frame (before adaptive sampling skip)
            CheckParachuteState(v);
            CheckJettisonState(v);
            CheckEngineState(v);
            CheckRcsState(v);
            CheckDeployableState(v);
            CheckLightState(v);
            CheckGearState(v);
            CheckCargoBayState(v);
            CheckFairingState(v);

            // Krakensbane-corrected true velocity
            Vector3 currentVelocity = (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(currentVelocity, lastRecordedVelocity,
                Planetarium.GetUniversalTime(), lastRecordedUT,
                maxSampleInterval, velocityDirThreshold, speedChangeThreshold))
                return;

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.transform.rotation,
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
                ParsekLog.Log($"Recorded point #{Recording.Count}: {point}");
            }
        }

        /// <summary>
        /// Force-record a boundary point (used at on-rails/off-rails transitions).
        /// </summary>
        public void SamplePosition(Vessel v)
        {
            if (v == null) return;

            Vector3 currentVelocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.transform.rotation,
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
            if (v != FlightGlobals.ActiveVessel) return;
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

            isOnRails = true;
            ParsekLog.Log($"Vessel went on rails — capturing orbit segment (body={v.mainBody.name})");
        }

        public void OnVesselGoOffRails(Vessel v)
        {
            if (!IsRecording || !isOnRails) return;
            if (v != FlightGlobals.ActiveVessel) return;
            if (v.persistentId != RecordingVesselId) return;

            // Finalize orbit segment
            currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
            OrbitSegments.Add(currentOrbitSegment);
            isOnRails = false;

            // Record a boundary TrajectoryPoint at current UT
            SamplePosition(v);

            ParsekLog.Log($"Vessel went off rails — orbit segment closed " +
                $"(UT {currentOrbitSegment.startUT:F0}-{currentOrbitSegment.endUT:F0})");
        }

        public void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            if (!IsRecording || !isOnRails) return;
            if (data.host != FlightGlobals.ActiveVessel) return;
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

            ParsekLog.Log($"SOI changed during orbit recording: {data.from.name} → {data.to.name}");
        }

        public void OnVesselWillDestroy(Vessel v)
        {
            if (!IsRecording) return;
            if (FlightGlobals.ActiveVessel == null) return;
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
                ParsekLog.Log("Active vessel destroyed during recording!");
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
            ParsekLog.Log("Auto-stopped recording due to scene change");
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
                ParsekLog.Log($"Snapshot backup FAILED ({reason}): pid={vessel.persistentId}, " +
                    $"loaded={vessel.loaded}, packed={vessel.packed} — using previous snapshot");
            }

            if (elapsedMs >= snapshotPerfLogThresholdMs)
            {
                ParsekLog.Log(
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
            bool recordingStartedAsEva, uint undockSiblingPid = 0)
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
            return VesselSwitchDecision.Stop;
        }
    }
}
