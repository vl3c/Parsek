using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Manages continuous recording for background vessels in a recording tree.
    /// Supports two modes per vessel:
    ///   - On-rails: captures OrbitSegment or SurfacePosition snapshots
    ///   - Loaded/physics: full trajectory points, part events, adaptive sampling
    ///
    /// Not a MonoBehaviour. Owned by ParsekFlight, which calls its methods from
    /// Update() and PhysicsFramePatch.Postfix.
    /// </summary>
    internal class BackgroundRecorder
    {
        private RecordingTree tree;

        // Per-vessel tracking state for loaded/physics mode
        private Dictionary<uint, BackgroundVesselState> loadedStates
            = new Dictionary<uint, BackgroundVesselState>();

        // Per-vessel on-rails tracking
        private Dictionary<uint, BackgroundOnRailsState> onRailsStates
            = new Dictionary<uint, BackgroundOnRailsState>();

        // Interval for updating ExplicitEndUT on background recordings
        private const double ExplicitEndUpdateInterval = 30.0;

        // Robotic sampling constants (mirrors FlightRecorder)
        private const double roboticSampleIntervalSeconds = 0.25;
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;

        public BackgroundRecorder(RecordingTree tree)
        {
            this.tree = tree;

            // Initialize on-rails state for each existing background vessel
            foreach (var kvp in tree.BackgroundMap)
            {
                uint vesselPid = kvp.Key;
                string recordingId = kvp.Value;

                onRailsStates[vesselPid] = new BackgroundOnRailsState
                {
                    vesselPid = vesselPid,
                    recordingId = recordingId,
                    hasOpenOrbitSegment = false,
                    isLanded = false,
                    lastExplicitEndUpdate = -1
                };
            }
        }

        #region Inner Classes

        private class BackgroundOnRailsState
        {
            public uint vesselPid;
            public string recordingId;
            public bool hasOpenOrbitSegment;
            public OrbitSegment currentOrbitSegment;
            public bool isLanded;
            public double lastExplicitEndUpdate;
        }

        private class BackgroundVesselState
        {
            public uint vesselPid;
            public string recordingId;

            // Adaptive sampling state
            public double lastRecordedUT = -1;
            public Vector3 lastRecordedVelocity;

            // Part event tracking (mirrors FlightRecorder's instance fields)
            public Dictionary<uint, int> parachuteStates = new Dictionary<uint, int>();
            public HashSet<uint> jettisonedShrouds = new HashSet<uint>();
            public Dictionary<ulong, string> jettisonNameRawCache = new Dictionary<ulong, string>();
            public Dictionary<ulong, string[]> parsedJettisonNamesCache = new Dictionary<ulong, string[]>();
            public HashSet<uint> extendedDeployables = new HashSet<uint>();
            public HashSet<uint> lightsOn = new HashSet<uint>();
            public HashSet<uint> blinkingLights = new HashSet<uint>();
            public Dictionary<uint, float> lightBlinkRates = new Dictionary<uint, float>();
            public HashSet<uint> deployedGear = new HashSet<uint>();
            public HashSet<uint> openCargoBays = new HashSet<uint>();
            public HashSet<uint> deployedFairings = new HashSet<uint>();
            public HashSet<ulong> deployedLadders = new HashSet<ulong>();
            public HashSet<ulong> deployedAnimationGroups = new HashSet<ulong>();
            public HashSet<ulong> deployedAnimateGenericModules = new HashSet<ulong>();
            public HashSet<ulong> deployedAeroSurfaceModules = new HashSet<ulong>();
            public HashSet<ulong> deployedControlSurfaceModules = new HashSet<ulong>();
            public HashSet<ulong> deployedRobotArmScannerModules = new HashSet<ulong>();
            public Dictionary<ulong, HeatLevel> animateHeatLevels = new Dictionary<ulong, HeatLevel>();

            // Engine/RCS/robotic caches
            public List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines;
            public HashSet<ulong> activeEngineKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastThrottle = new Dictionary<ulong, float>();
            public HashSet<ulong> loggedEngineModuleKeys = new HashSet<ulong>();
            public List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules;
            public HashSet<ulong> activeRcsKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastRcsThrottle = new Dictionary<ulong, float>();
            public HashSet<ulong> loggedRcsModuleKeys = new HashSet<ulong>();
            public List<(Part part, PartModule module, int moduleIndex, string moduleName)> cachedRoboticModules;
            public HashSet<ulong> activeRoboticKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastRoboticPosition = new Dictionary<ulong, float>();
            public Dictionary<ulong, double> lastRoboticSampleUT = new Dictionary<ulong, double>();
            public HashSet<ulong> loggedRoboticModuleKeys = new HashSet<ulong>();

            // Diagnostic guards (prevent log spam, one per module type)
            public HashSet<ulong> loggedLadderClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimationGroupClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimateGenericClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAeroSurfaceClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedControlSurfaceClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedRobotArmScannerClassificationMisses = new HashSet<ulong>();
            public HashSet<ulong> loggedAnimateHeatClassificationMisses = new HashSet<ulong>();
            public HashSet<uint> loggedCargoBayDeployIndexIssues = new HashSet<uint>();
            public HashSet<uint> loggedCargoBayAnimationIssues = new HashSet<uint>();
            public HashSet<uint> loggedCargoBayClosedPositionIssues = new HashSet<uint>();
            public HashSet<uint> loggedFairingReadFailures = new HashSet<uint>();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Called from ParsekFlight.Update() once per frame.
        /// Updates ExplicitEndUT for on-rails background vessels.
        /// </summary>
        public void UpdateOnRails(double currentUT)
        {
            if (tree == null) return;

            // Dictionary is not modified during this loop, safe to iterate directly
            foreach (var kvp in onRailsStates)
            {
                uint pid = kvp.Key;
                var state = kvp.Value;

                // Update ExplicitEndUT periodically
                if (currentUT - state.lastExplicitEndUpdate >= ExplicitEndUpdateInterval)
                {
                    RecordingStore.Recording treeRec;
                    if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
                    {
                        treeRec.ExplicitEndUT = currentUT;
                        state.lastExplicitEndUpdate = currentUT;
                    }
                }
            }
        }

        /// <summary>
        /// Called from PhysicsFramePatch.Postfix for each non-active loaded vessel.
        /// Handles loaded/physics mode recording for background vessels.
        /// </summary>
        public void OnBackgroundPhysicsFrame(Vessel bgVessel)
        {
            if (tree == null || bgVessel == null) return;

            uint pid = bgVessel.persistentId;

            // Check if this vessel is in the tree's BackgroundMap
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId)) return;

            // Only process loaded/physics vessels (not packed)
            if (bgVessel.packed) return;

            // Look up the loaded state (created by OnBackgroundVesselGoOffRails)
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(pid, out state)) return;

            // Get the tree recording
            RecordingStore.Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            double ut = Planetarium.GetUniversalTime();

            // Poll part events
            PollPartEvents(bgVessel, state, treeRec, ut);

            // Adaptive sampling
            Vector3 currentVelocity = (Vector3)(bgVessel.rb_velocityD + Krakensbane.GetFrameVelocity());

            float maxSampleInterval = ParsekSettings.Current?.maxSampleInterval ?? 3.0f;
            float velocityDirThreshold = ParsekSettings.Current?.velocityDirThreshold ?? 2.0f;
            float speedChangeThreshold = (ParsekSettings.Current?.speedChangeThreshold ?? 5.0f) / 100f;

            if (!TrajectoryMath.ShouldRecordPoint(currentVelocity, state.lastRecordedVelocity,
                ut, state.lastRecordedUT, maxSampleInterval, velocityDirThreshold, speedChangeThreshold))
            {
                return;
            }

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = ut,
                latitude = bgVessel.latitude,
                longitude = bgVessel.longitude,
                altitude = bgVessel.altitude,
                rotation = bgVessel.srfRelRotation,
                velocity = currentVelocity,
                bodyName = bgVessel.mainBody?.name ?? "Unknown",
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            treeRec.Points.Add(point);
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;

            // Update ExplicitEndUT
            treeRec.ExplicitEndUT = ut;
        }

        /// <summary>
        /// Called when a vessel is added to the background map (transition or branch creation).
        /// </summary>
        public void OnVesselBackgrounded(uint vesselPid)
        {
            if (tree == null) return;

            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Remove any stale state from a prior initialization (e.g. constructor
            // created on-rails state before this method is called for the same vessel).
            // Close any open orbit segment first so data is not lost.
            double ut = Planetarium.GetUniversalTime();
            BackgroundOnRailsState staleRails;
            if (onRailsStates.TryGetValue(vesselPid, out staleRails) && staleRails.hasOpenOrbitSegment)
            {
                CloseOrbitSegment(staleRails, ut);
            }
            onRailsStates.Remove(vesselPid);
            loadedStates.Remove(vesselPid);

            // Look up the vessel
            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v == null)
            {
                // Vessel not found yet; create minimal on-rails state
                onRailsStates[vesselPid] = new BackgroundOnRailsState
                {
                    vesselPid = vesselPid,
                    recordingId = recordingId,
                    hasOpenOrbitSegment = false,
                    isLanded = false,
                    lastExplicitEndUpdate = -1
                };
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (not found): pid={vesselPid} recId={recordingId}");
                return;
            }

            if (v.loaded && !v.packed)
            {
                // Loaded/physics mode
                InitializeLoadedState(v, vesselPid, recordingId);
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (loaded/physics): pid={vesselPid} recId={recordingId}");
            }
            else
            {
                // On-rails mode
                InitializeOnRailsState(v, vesselPid, recordingId);
                ParsekLog.Info("BgRecorder", $"Vessel backgrounded (on-rails): pid={vesselPid} recId={recordingId}");
            }
        }

        /// <summary>
        /// Called when a vessel is removed from the background map (promotion, terminal event).
        /// </summary>
        public void OnVesselRemovedFromBackground(uint vesselPid)
        {
            double ut = Planetarium.GetUniversalTime();

            // Close any open orbit segment
            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(vesselPid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(railsState, ut);
                }
                onRailsStates.Remove(vesselPid);
            }

            // Sample a final boundary point if loaded
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(vesselPid, out loadedState))
            {
                Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                if (v != null)
                {
                    RecordingStore.Recording treeRec;
                    if (tree.Recordings.TryGetValue(loadedState.recordingId, out treeRec))
                    {
                        SampleBoundaryPoint(v, treeRec, ut);
                    }
                }
                loadedStates.Remove(vesselPid);
            }

            ParsekLog.Info("BgRecorder", $"Vessel removed from background: pid={vesselPid}");
        }

        /// <summary>
        /// Called when a background vessel goes on rails.
        /// Transitions from loaded/physics mode to on-rails mode.
        /// </summary>
        public void OnBackgroundVesselGoOnRails(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            double ut = Planetarium.GetUniversalTime();

            // If the vessel was in loaded/physics mode, transition to on-rails
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(pid, out loadedState))
            {
                // Sample a final boundary point
                RecordingStore.Recording treeRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out treeRec))
                {
                    SampleBoundaryPoint(v, treeRec, ut);
                }

                loadedStates.Remove(pid);
                ParsekLog.Info("BgRecorder", $"Mode transition loaded→on-rails: pid={pid}");
            }

            // Initialize on-rails state
            InitializeOnRailsState(v, pid, recordingId);
        }

        /// <summary>
        /// Called when a background vessel goes off rails.
        /// Transitions from on-rails mode to loaded/physics mode.
        /// </summary>
        public void OnBackgroundVesselGoOffRails(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            double ut = Planetarium.GetUniversalTime();

            // Close any open orbit segment
            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(pid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(railsState, ut);
                }
                onRailsStates.Remove(pid);
                ParsekLog.Info("BgRecorder", $"Mode transition on-rails→loaded: pid={pid}");
            }

            // Initialize loaded/physics state if vessel is unpacked
            if (!v.packed)
            {
                InitializeLoadedState(v, pid, recordingId);

                // Sample a boundary point
                RecordingStore.Recording treeRec;
                if (tree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    SampleBoundaryPoint(v, treeRec, ut);
                }
            }
        }

        /// <summary>
        /// Called when a background vessel changes SOI.
        /// Closes the orbit segment in the old SOI and opens one in the new SOI.
        /// </summary>
        public void OnBackgroundVesselSOIChanged(Vessel v, CelestialBody fromBody)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(pid, out recordingId) || recordingId == null) return;

            double ut = Planetarium.GetUniversalTime();

            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(pid, out state))
            {
                // Close the old orbit segment
                if (state.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(state, ut);
                }

                // Open a new orbit segment with the new body's orbit
                if (v.orbit != null)
                {
                    state.currentOrbitSegment = new OrbitSegment
                    {
                        startUT = ut,
                        inclination = v.orbit.inclination,
                        eccentricity = v.orbit.eccentricity,
                        semiMajorAxis = v.orbit.semiMajorAxis,
                        longitudeOfAscendingNode = v.orbit.LAN,
                        argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                        meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                        epoch = v.orbit.epoch,
                        bodyName = v.mainBody?.name ?? "Unknown"
                    };
                    state.hasOpenOrbitSegment = true;
                }

                ParsekLog.Info("BgRecorder", $"SOI change for background vessel: pid={pid} " +
                    $"from={fromBody?.name ?? "?"} to={v.mainBody?.name ?? "?"}");
            }
        }

        /// <summary>
        /// Called when a background vessel is about to be destroyed.
        /// Closes any open orbit segment, cleans up tracking state.
        /// </summary>
        public void OnBackgroundVesselWillDestroy(Vessel v)
        {
            if (v == null || tree == null) return;

            uint pid = v.persistentId;
            if (!tree.BackgroundMap.ContainsKey(pid)) return;
            double ut = Planetarium.GetUniversalTime();

            BackgroundOnRailsState railsState;
            if (onRailsStates.TryGetValue(pid, out railsState))
            {
                if (railsState.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(railsState, ut);
                }
                onRailsStates.Remove(pid);
            }

            if (loadedStates.ContainsKey(pid))
            {
                loadedStates.Remove(pid);
            }

            if (v.vesselType == VesselType.EVA)
                ParsekLog.Info("BgRecorder", $"Background EVA vessel ended: pid={pid}");
            else
                ParsekLog.Warn("BgRecorder", $"Background vessel destroyed: pid={pid}");
        }

        /// <summary>
        /// Called on scene change or tree teardown. Closes all open orbit segments
        /// and cleans up all per-vessel state.
        /// </summary>
        public void Shutdown()
        {
            double ut = Planetarium.GetUniversalTime();

            // Close all open orbit segments
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (state.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(state, ut);
                }
            }

            onRailsStates.Clear();
            loadedStates.Clear();

            ParsekLog.Info("BgRecorder", "Shutdown complete — all background states cleared");
        }

        /// <summary>
        /// Finalizes all background recordings for tree commit.
        /// Closes all open orbit segments, flushes any pending trajectory data,
        /// and sets ExplicitEndUT on all background recordings.
        /// Does NOT clear state — call Shutdown() after this if shutting down.
        /// </summary>
        public void FinalizeAllForCommit(double commitUT)
        {
            // Close all open orbit segments
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (state.hasOpenOrbitSegment)
                {
                    CloseOrbitSegment(state, commitUT);
                }
            }

            // Update ExplicitEndUT on all background recordings
            foreach (var kvp in tree.BackgroundMap)
            {
                RecordingStore.Recording treeRec;
                if (tree.Recordings.TryGetValue(kvp.Value, out treeRec))
                {
                    treeRec.ExplicitEndUT = commitUT;
                }
            }

            ParsekLog.Info("BgRecorder", $"FinalizeAllForCommit complete at UT={commitUT:F1} " +
                $"({onRailsStates.Count} on-rails, {loadedStates.Count} loaded)");
        }

        #endregion

        #region Internal State Management

        private void InitializeOnRailsState(Vessel v, uint vesselPid, string recordingId)
        {
            double ut = Planetarium.GetUniversalTime();

            var state = new BackgroundOnRailsState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
                lastExplicitEndUpdate = ut
            };

            bool isLanded = v.situation == Vessel.Situations.LANDED ||
                            v.situation == Vessel.Situations.SPLASHED;

            if (isLanded)
            {
                state.isLanded = true;
                state.hasOpenOrbitSegment = false;

                // Capture SurfacePosition
                RecordingStore.Recording treeRec;
                if (tree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    treeRec.SurfacePos = new SurfacePosition
                    {
                        body = v.mainBody?.name ?? "Kerbin",
                        latitude = v.latitude,
                        longitude = v.longitude,
                        altitude = v.altitude,
                        rotation = v.srfRelRotation,
                        situation = v.situation == Vessel.Situations.SPLASHED
                            ? SurfaceSituation.Splashed
                            : SurfaceSituation.Landed
                    };
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (landed): pid={vesselPid} " +
                    $"body={v.mainBody?.name} sit={v.situation}");
            }
            else if (v.orbit != null)
            {
                // Open orbit segment
                state.currentOrbitSegment = new OrbitSegment
                {
                    startUT = ut,
                    inclination = v.orbit.inclination,
                    eccentricity = v.orbit.eccentricity,
                    semiMajorAxis = v.orbit.semiMajorAxis,
                    longitudeOfAscendingNode = v.orbit.LAN,
                    argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                    meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                    epoch = v.orbit.epoch,
                    bodyName = v.mainBody?.name ?? "Kerbin"
                };
                state.hasOpenOrbitSegment = true;

                RecordingStore.Recording treeRec;
                if (tree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (orbiting): pid={vesselPid} " +
                    $"body={v.mainBody?.name} sma={v.orbit.semiMajorAxis:F1}");
            }
            else
            {
                // No orbit and not landed — edge case (e.g. vessel on launchpad with no orbit)
                state.hasOpenOrbitSegment = false;

                RecordingStore.Recording treeRec;
                if (tree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (no orbit): pid={vesselPid}");
            }

            onRailsStates[vesselPid] = state;
        }

        private void InitializeLoadedState(Vessel v, uint vesselPid, string recordingId)
        {
            var state = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId
            };

            // Cache engine/RCS/robotic modules
            state.cachedEngines = FlightRecorder.CacheEngineModules(v);
            state.cachedRcsModules = FlightRecorder.CacheRcsModules(v);
            state.cachedRoboticModules = FlightRecorder.CacheRoboticModules(v);

            // Seed already-deployed fairings (mirrors ResetPartEventTrackingState)
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
                            state.deployedFairings.Add(p.persistentId);
                    }
                    catch { }
                }
            }

            ParsekLog.Verbose("BgRecorder", $"Loaded state initialized: pid={vesselPid} " +
                $"engines={state.cachedEngines?.Count ?? 0} rcs={state.cachedRcsModules?.Count ?? 0} " +
                $"robotics={state.cachedRoboticModules?.Count ?? 0}");

            loadedStates[vesselPid] = state;
        }

        private void CloseOrbitSegment(BackgroundOnRailsState state, double ut)
        {
            if (!state.hasOpenOrbitSegment) return;

            state.currentOrbitSegment.endUT = ut;

            RecordingStore.Recording treeRec;
            if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
            {
                treeRec.OrbitSegments.Add(state.currentOrbitSegment);
                treeRec.ExplicitEndUT = ut;
            }

            state.hasOpenOrbitSegment = false;

            ParsekLog.Verbose("BgRecorder", $"Orbit segment closed: pid={state.vesselPid} " +
                $"UT={state.currentOrbitSegment.startUT:F1}-{ut:F1} body={state.currentOrbitSegment.bodyName}");
        }

        private void SampleBoundaryPoint(Vessel v, RecordingStore.Recording treeRec, double ut)
        {
            if (v == null) return;

            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.srfRelRotation,
                velocity = velocity,
                bodyName = v.mainBody?.name ?? "Unknown",
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            treeRec.Points.Add(point);
            treeRec.ExplicitEndUT = ut;
        }

        #endregion

        #region Part Event Polling

        /// <summary>
        /// Polls all part event types for a background vessel in loaded/physics mode.
        /// Duplicates the Layer 2 wrapper methods from FlightRecorder, calling the
        /// same Layer 1 static transition methods.
        /// </summary>
        private void PollPartEvents(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            CheckParachuteState(v, state, treeRec, ut);
            CheckJettisonState(v, state, treeRec, ut);
            CheckEngineState(v, state, treeRec, ut);
            CheckRcsState(v, state, treeRec, ut);
            CheckDeployableState(v, state, treeRec, ut);
            CheckLadderState(v, state, treeRec, ut);
            CheckAnimationGroupState(v, state, treeRec, ut);
            CheckAeroSurfaceState(v, state, treeRec, ut);
            CheckControlSurfaceState(v, state, treeRec, ut);
            CheckRobotArmScannerState(v, state, treeRec, ut);
            CheckAnimateHeatState(v, state, treeRec, ut);
            CheckAnimateGenericState(v, state, treeRec, ut);
            CheckLightState(v, state, treeRec, ut);
            CheckGearState(v, state, treeRec, ut);
            CheckCargoBayState(v, state, treeRec, ut);
            CheckFairingState(v, state, treeRec, ut);
            CheckRoboticState(v, state, treeRec, ut);
        }

        private void CheckParachuteState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var chute = p.FindModuleImplementing<ModuleParachute>();
                if (chute == null) continue;

                int chuteState = chute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED ? 2
                    : chute.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED ? 1
                    : 0;

                var evt = FlightRecorder.CheckParachuteTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", chuteState, state.parachuteStates, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckJettisonState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

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
                    ulong moduleKey = FlightRecorder.EncodeEngineKey(p.persistentId, jettisonModuleIndex);
                    jettisonModuleIndex++;

                    if (jettison.isJettisoned)
                    {
                        isJettisoned = true;
                        break;
                    }

                    string jettisonNames = jettison.jettisonName;
                    if (string.IsNullOrWhiteSpace(jettisonNames))
                        continue;

                    string[] names = GetCachedJettisonNames(state, moduleKey, jettisonNames);
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
                    p.persistentId, p.partInfo?.name ?? "unknown", isJettisoned, state.jettisonedShrouds, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckDeployableState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var deployable = p.FindModuleImplementing<ModuleDeployablePart>();
                if (deployable == null) continue;

                var ds = deployable.deployState;
                if (ds == ModuleDeployablePart.DeployState.BROKEN) continue;
                if (ds == ModuleDeployablePart.DeployState.EXTENDING) continue;
                if (ds == ModuleDeployablePart.DeployState.RETRACTING) continue;

                bool isExtended = ds == ModuleDeployablePart.DeployState.EXTENDED;

                var evt = FlightRecorder.CheckDeployableTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isExtended, state.extendedDeployables, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckLightState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var light = p.FindModuleImplementing<ModuleLight>();
                if (light == null) continue;

                var evt = FlightRecorder.CheckLightTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", light.isOn, state.lightsOn, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }

                var blinkEvents = FlightRecorder.CheckLightBlinkTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    light.isBlinking, light.blinkRate,
                    state.blinkingLights, state.lightBlinkRates, ut);
                for (int e = 0; e < blinkEvents.Count; e++)
                {
                    treeRec.PartEvents.Add(blinkEvents[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {blinkEvents[e].eventType} '{blinkEvents[e].partName}' " +
                        $"pid={blinkEvents[e].partPersistentId} val={blinkEvents[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckGearState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                    if (!isDeployed && !isRetracted) continue;

                    var evt = FlightRecorder.CheckGearTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, state.deployedGear, ut);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                    }
                    break;
                }
            }
        }

        private void CheckCargoBayState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                var cargo = p.FindModuleImplementing<ModuleCargoBay>();
                if (cargo == null) continue;

                int deployIdx = cargo.DeployModuleIndex;
                if (deployIdx < 0 || deployIdx >= p.Modules.Count)
                {
                    if (state.loggedCargoBayDeployIndexIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("BgRecorder", $"CargoBay: invalid DeployModuleIndex for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} deployIdx={deployIdx} modules={p.Modules.Count}");
                    }
                    continue;
                }
                var animModule = p.Modules[deployIdx] as ModuleAnimateGeneric;
                if (animModule == null)
                {
                    if (state.loggedCargoBayAnimationIssues.Add(p.persistentId))
                    {
                        PartModule moduleAtIndex = p.Modules[deployIdx];
                        string moduleName = moduleAtIndex?.moduleName ?? "<null>";
                        ParsekLog.Verbose("BgRecorder", $"CargoBay: DeployModuleIndex did not resolve to ModuleAnimateGeneric for " +
                            $"'{p.partInfo?.name}' pid={p.persistentId} deployIdx={deployIdx} module='{moduleName}'");
                    }
                    continue;
                }

                FlightRecorder.ClassifyCargoBayState(animModule.animTime, cargo.closedPosition,
                    out bool isOpen, out bool isClosed);

                if (!isOpen && !isClosed)
                {
                    if (cargo.closedPosition >= 0.1f && cargo.closedPosition <= 0.9f &&
                        state.loggedCargoBayClosedPositionIssues.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("BgRecorder", $"CargoBay: unsupported closedPosition for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} closedPosition={cargo.closedPosition:F3} animTime={animModule.animTime:F3}");
                    }
                    continue;
                }

                var evt = FlightRecorder.CheckCargoBayTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isOpen, state.openCargoBays, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckFairingState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                    if (state.loggedFairingReadFailures.Add(p.persistentId))
                    {
                        ParsekLog.Verbose("BgRecorder", $"Fairing: unable to read GetScalar for '{p.partInfo?.name}' " +
                            $"pid={p.persistentId} ({ex.GetType().Name}: {ex.Message})");
                    }
                    continue;
                }

                var evt = FlightRecorder.CheckFairingTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, state.deployedFairings, ut);
                if (evt.HasValue)
                {
                    treeRec.PartEvents.Add(evt.Value);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                        $"pid={evt.Value.partPersistentId} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckEngineState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (state.cachedEngines == null) return;

            for (int i = 0; i < state.cachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = state.cachedEngines[i];
                if (part == null || engine == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool ignited = engine.EngineIgnited && engine.isOperational;
                float throttle = engine.currentThrottle;

                var events = FlightRecorder.CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, throttle,
                    state.activeEngineKeys, state.lastThrottle, ut);

                for (int e = 0; e < events.Count; e++)
                {
                    treeRec.PartEvents.Add(events[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F2} (bg vessel {state.vesselPid})");

                    // Engine-ignite jettison fallback (same as FlightRecorder)
                    if (events[e].eventType == PartEventType.EngineIgnited &&
                        part.FindModuleImplementing<ModuleJettison>() != null)
                    {
                        var shroudEvt = FlightRecorder.CheckJettisonTransition(
                            part.persistentId,
                            part.partInfo?.name ?? "unknown",
                            isJettisoned: true,
                            jettisonedSet: state.jettisonedShrouds,
                            ut: ut);
                        if (shroudEvt.HasValue)
                        {
                            treeRec.PartEvents.Add(shroudEvt.Value);
                            ParsekLog.Verbose("BgRecorder", $"Part event: {shroudEvt.Value.eventType} '{shroudEvt.Value.partName}' " +
                                $"pid={shroudEvt.Value.partPersistentId} (engine-ignite fallback, bg vessel {state.vesselPid})");
                        }
                    }
                }
            }
        }

        private void CheckRcsState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (state.cachedRcsModules == null) return;

            for (int i = 0; i < state.cachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = state.cachedRcsModules[i];
                if (part == null || rcs == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                float power = active ? FlightRecorder.ComputeRcsPower(rcs.thrustForces, rcs.thrusterPower) : 0f;

                var events = FlightRecorder.CheckRcsTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    active, power,
                    state.activeRcsKeys, state.lastRcsThrottle, ut);

                for (int e = 0; e < events.Count; e++)
                {
                    treeRec.PartEvents.Add(events[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckLadderState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                        if (state.loggedLadderClassificationMisses.Add(key))
                        {
                            ParsekLog.Verbose("BgRecorder", $"Ladder: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }

                    var evt = FlightRecorder.CheckLadderTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedLadders, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} (ladder, bg vessel {state.vesselPid})");
                    }

                    break; // one RetractableLadder module per ladder part
                }
            }
        }

        private void CheckAnimationGroupState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                        if (state.loggedAnimationGroupClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimationGroup: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAnimationGroups, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animation-group, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAeroSurfaceState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                        if (state.loggedAeroSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AeroSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAeroSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (aero-surface, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckControlSurfaceState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                        if (state.loggedControlSurfaceClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"ControlSurface: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedControlSurfaceModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (control-surface, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckRobotArmScannerState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                        if (state.loggedRobotArmScannerClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"RobotArmScanner: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedRobotArmScannerModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (robot-arm-scanner, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAnimateHeatState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
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
                        if (state.loggedAnimateHeatClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimateHeat: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimateHeatTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        normalizedHeat, state.animateHeatLevels, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (animate-heat, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckAnimateGenericState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                // Avoid duplicate events for modules already tracked via dedicated handlers
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
                    if (!FlightRecorder.TryClassifyAnimateGenericState(animateModule, out isDeployed, out isRetracted))
                    {
                        ulong diagnosticKey = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                        if (state.loggedAnimateGenericClassificationMisses.Add(diagnosticKey))
                        {
                            ParsekLog.Verbose("BgRecorder", $"AnimateGeneric: unable to classify '{p.partInfo?.name}' pid={p.persistentId} midx={m}");
                        }
                        continue;
                    }
                    if (!isDeployed && !isRetracted) continue;

                    ulong key = FlightRecorder.EncodeEngineKey(p.persistentId, m);
                    var evt = FlightRecorder.CheckAnimationGroupTransition(
                        key, p.persistentId, p.partInfo?.name ?? "unknown",
                        isDeployed, state.deployedAnimateGenericModules, ut, m);
                    if (evt.HasValue)
                    {
                        treeRec.PartEvents.Add(evt.Value);
                        ParsekLog.Verbose("BgRecorder", $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId} midx={evt.Value.moduleIndex} (anim-generic, bg vessel {state.vesselPid})");
                    }
                }
            }
        }

        private void CheckRoboticState(Vessel v, BackgroundVesselState state,
            RecordingStore.Recording treeRec, double ut)
        {
            if (state.cachedRoboticModules == null) return;

            for (int i = 0; i < state.cachedRoboticModules.Count; i++)
            {
                var (part, module, moduleIndex, moduleName) = state.cachedRoboticModules[i];
                if (part == null || module == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);

                bool hasPosition = FlightRecorder.TryGetRoboticPositionValue(
                    module, moduleName, out float positionValue, out float deadband, out string sourceField);

                if (!hasPosition) continue;

                bool hasMovingSignal = FlightRecorder.TryGetRoboticMovingState(module, out bool movingSignal);

                var events = FlightRecorder.CheckRoboticTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    movingSignal, positionValue, deadband, ut,
                    state.activeRoboticKeys, state.lastRoboticPosition,
                    state.lastRoboticSampleUT, roboticSampleIntervalSeconds);

                for (int e = 0; e < events.Count; e++)
                {
                    treeRec.PartEvents.Add(events[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {events[e].eventType} '{events[e].partName}' " +
                        $"pid={events[e].partPersistentId} midx={events[e].moduleIndex} " +
                        $"val={events[e].value:F3} (bg vessel {state.vesselPid})");
                }
            }
        }

        #endregion

        #region Jettison Name Cache

        /// <summary>
        /// Per-vessel jettison name cache (mirrors FlightRecorder.GetCachedJettisonNames).
        /// </summary>
        private static string[] GetCachedJettisonNames(BackgroundVesselState state, ulong moduleKey, string rawNames)
        {
            rawNames = rawNames ?? string.Empty;
            string cachedRaw;
            if (state.jettisonNameRawCache.TryGetValue(moduleKey, out cachedRaw) &&
                string.Equals(cachedRaw, rawNames, StringComparison.Ordinal) &&
                state.parsedJettisonNamesCache.TryGetValue(moduleKey, out string[] cachedNames))
            {
                return cachedNames;
            }

            string[] parsedNames = ParseJettisonNames(rawNames);
            state.jettisonNameRawCache[moduleKey] = rawNames;
            state.parsedJettisonNamesCache[moduleKey] = parsedNames;
            return parsedNames;
        }

        // ParseJettisonNames: use FlightRecorder.ParseJettisonNames (internal static)
        private static string[] ParseJettisonNames(string rawNames)
            => FlightRecorder.ParseJettisonNames(rawNames);

        #endregion

        #region Testing Support

        // Expose internal state for testing
        internal int OnRailsStateCount => onRailsStates.Count;
        internal int LoadedStateCount => loadedStates.Count;

        internal bool HasOnRailsState(uint pid) => onRailsStates.ContainsKey(pid);
        internal bool HasLoadedState(uint pid) => loadedStates.ContainsKey(pid);

        internal bool GetOnRailsHasOpenSegment(uint pid)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(pid, out state) && state.hasOpenOrbitSegment;
        }

        internal bool GetOnRailsIsLanded(uint pid)
        {
            BackgroundOnRailsState state;
            return onRailsStates.TryGetValue(pid, out state) && state.isLanded;
        }

        internal double GetOnRailsLastExplicitEndUpdate(uint pid)
        {
            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(pid, out state))
                return state.lastExplicitEndUpdate;
            return -1;
        }

        #endregion
    }
}
