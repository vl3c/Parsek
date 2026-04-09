using System;
using System.Collections.Generic;
using System.Globalization;
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

        // GameEvent subscriptions for discrete events (onPartDie, onPartJointBreak)
        private bool partEventsSubscribed;

        // Interval for updating ExplicitEndUT on background recordings
        private const double ExplicitEndUpdateInterval = 30.0;

        // Debris TTL: stop recording debris after this many seconds
        internal const double DebrisTTLSeconds = 60.0;

        // Per-vessel debris TTL tracking (vesselPid -> UT at which to stop recording)
        // double.NaN = no TTL (controlled vessel, keep recording indefinitely)
        private Dictionary<uint, double> debrisTTLExpiry
            = new Dictionary<uint, double>();

        // Pending background split checks: after OnBackgroundPartJointBreak,
        // we defer one frame to let KSP finalize the vessel split, then check.
        // Maps parent vessel PID -> (branchUT, recordingId)
        private Dictionary<uint, (double branchUT, string recordingId)> pendingBackgroundSplitChecks
            = new Dictionary<uint, (double, string)>();

        // Pre-break snapshot of all vessel PIDs in FlightGlobals, per parent vessel.
        // Used to identify NEW vessels that appeared from the split.
        private Dictionary<uint, HashSet<uint>> preBreakVesselPidSnapshots
            = new Dictionary<uint, HashSet<uint>>();

        // Reusable buffer for transition-check methods (avoids per-frame List<PartEvent> allocations)
        private readonly List<PartEvent> reusableEventBuffer = new List<PartEvent>();

        // Robotic sampling constants (mirrors FlightRecorder)
        private const double roboticSampleIntervalSeconds = 0.25;
        private const float roboticAngularDeadbandDegrees = 0.5f;
        private const float roboticLinearDeadbandMeters = 0.01f;

        // Injectable vessel finder for CheckpointAllVessels (null = use FlightRecorder.FindVesselByPid)
        private System.Func<uint, Vessel> vesselFinderOverride;

        // Injectable distance override for testing (null = use FlightGlobals.ActiveVessel)
        private System.Func<Vessel, double> distanceOverrideForTesting;

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

            // Proximity-based sample interval tracking
            public double currentSampleInterval = ProximityRateSelector.OutOfRangeInterval;
            public double lastSampleUT = -1;

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
            public Dictionary<ulong, int> rcsActiveFrameCount = new Dictionary<ulong, int>();
            public HashSet<ulong> loggedRcsModuleKeys = new HashSet<ulong>();
            public List<(Part part, PartModule module, int moduleIndex, string moduleName)> cachedRoboticModules;
            public HashSet<ulong> activeRoboticKeys = new HashSet<ulong>();
            public Dictionary<ulong, float> lastRoboticPosition = new Dictionary<ulong, float>();
            public Dictionary<ulong, double> lastRoboticSampleUT = new Dictionary<ulong, double>();
            public HashSet<ulong> loggedRoboticModuleKeys = new HashSet<ulong>();

            // Environment tracking (TrackSection management)
            public EnvironmentHysteresis environmentHysteresis;
            public TrackSection currentTrackSection;
            public bool trackSectionActive;
            public List<TrackSection> trackSections = new List<TrackSection>();

            // Part destruction/decoupling tracking
            public HashSet<uint> decoupledPartIds = new HashSet<uint>();

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

        #region GameEvent Subscriptions

        /// <summary>
        /// Subscribes to GameEvents for discrete part events (onPartDie, onPartJointBreak)
        /// that cannot be captured by per-frame polling. These events fire globally for all
        /// vessels; the handlers check if the part belongs to a background vessel.
        /// </summary>
        internal void SubscribePartEvents()
        {
            if (partEventsSubscribed) return;
            GameEvents.onPartDie.Add(OnBackgroundPartDie);
            GameEvents.onPartJointBreak.Add(OnBackgroundPartJointBreak);
            partEventsSubscribed = true;
            ParsekLog.Verbose("BgRecorder", "Subscribed to onPartDie and onPartJointBreak for background vessels");
        }

        internal void UnsubscribePartEvents()
        {
            if (!partEventsSubscribed) return;
            GameEvents.onPartDie.Remove(OnBackgroundPartDie);
            GameEvents.onPartJointBreak.Remove(OnBackgroundPartJointBreak);
            partEventsSubscribed = false;
            ParsekLog.Verbose("BgRecorder", "Unsubscribed from onPartDie and onPartJointBreak");
        }

        /// <summary>
        /// Handles part death for background vessels. Looks up the dying part's vessel
        /// in the tree's BackgroundMap; if found and in loaded state, emits a Destroyed
        /// or ParachuteDestroyed event.
        /// </summary>
        internal void OnBackgroundPartDie(Part p)
        {
            if (tree == null) return;
            if (p?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("BgRecorder", "bg-part-die-null",
                    "OnBackgroundPartDie: part or vessel is null");
                return;
            }

            uint vesselPid = p.vessel.persistentId;

            // Only handle background vessels (not the active/focused vessel)
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Must have loaded state (part events only meaningful for loaded vessels)
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state)) return;

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            bool hasChute = p.FindModuleImplementing<ModuleParachute>() != null;
            var evtType = FlightRecorder.ClassifyPartDeath(
                p.persistentId, hasChute, state.parachuteStates);

            treeRec.PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = evtType,
                partName = p.partInfo?.name ?? "unknown"
            });
            treeRec.MarkFilesDirty();

            ParsekLog.Verbose("BgRecorder",
                $"Part death on background vessel: {evtType} '{p.partInfo?.name}' " +
                $"pid={p.persistentId} vesselPid={vesselPid}");
        }

        /// <summary>
        /// Handles part joint breaks for background vessels. Looks up the child part's
        /// vessel in the tree's BackgroundMap; if found and in loaded state, emits a
        /// Decoupled event (with structural-joint and dedup guards).
        /// </summary>
        internal void OnBackgroundPartJointBreak(PartJoint joint, float breakForce)
        {
            if (tree == null) return;
            if (joint?.Child?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("BgRecorder", "bg-joint-break-null",
                    "OnBackgroundPartJointBreak: joint, child, or vessel is null");
                return;
            }

            uint vesselPid = joint.Child.vessel.persistentId;

            // Only handle background vessels
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state)) return;

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            // Skip non-structural joint breaks (same logic as FlightRecorder)
            var childAttachJoint = joint.Child.attachJoint;
            if (!FlightRecorder.IsStructuralJointBreak(joint == childAttachJoint, childAttachJoint != null))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"bg-joint-break-nonstructural-{joint.Child.persistentId}",
                    $"OnBackgroundPartJointBreak: non-structural joint break on " +
                    $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                    $"vesselPid={vesselPid} (breakForce={breakForce:F1}), skipping");
                return;
            }

            // Skip duplicate decoupled events for the same part
            if (state.decoupledPartIds.Contains(joint.Child.persistentId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"bg-joint-break-dup-{joint.Child.persistentId}",
                    $"OnBackgroundPartJointBreak: duplicate for already-decoupled " +
                    $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                    $"vesselPid={vesselPid}, skipping");
                return;
            }
            state.decoupledPartIds.Add(joint.Child.persistentId);

            treeRec.PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = joint.Child.persistentId,
                eventType = PartEventType.Decoupled,
                partName = joint.Child.partInfo?.name ?? "unknown"
            });
            treeRec.MarkFilesDirty();

            ParsekLog.Verbose("BgRecorder",
                $"Part joint break on background vessel: Decoupled " +
                $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                $"vesselPid={vesselPid} breakForce={breakForce:F1}");

            // Schedule a deferred split check for this background vessel.
            // KSP needs one frame to finalize vessel splits after joint breaks.
            if (!pendingBackgroundSplitChecks.ContainsKey(vesselPid))
            {
                double branchUT = Planetarium.GetUniversalTime();

                // Snapshot all current vessel PIDs so we can identify NEW ones next frame
                var snapshot = new HashSet<uint>();
                if (FlightGlobals.Vessels != null)
                {
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v == null) continue;
                        if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                        snapshot.Add(v.persistentId);
                    }
                }
                preBreakVesselPidSnapshots[vesselPid] = snapshot;
                pendingBackgroundSplitChecks[vesselPid] = (branchUT, recordingId);

                ParsekLog.Info("BgRecorder",
                    $"Scheduled deferred split check for background vessel: " +
                    $"parentPid={vesselPid} branchUT={branchUT:F1} " +
                    $"preBreakVesselCount={snapshot.Count}");
            }
        }

        #endregion

        #region Background Vessel Split Detection

        /// <summary>
        /// Called from Update() to process any pending background split checks.
        /// Must be called one frame after the joint break event to allow KSP to
        /// finalize the vessel split.
        /// </summary>
        internal void ProcessPendingSplitChecks()
        {
            if (pendingBackgroundSplitChecks.Count == 0) return;

            // Copy keys to avoid modifying dict during iteration
            var pending = new List<KeyValuePair<uint, (double branchUT, string recordingId)>>(
                pendingBackgroundSplitChecks);
            pendingBackgroundSplitChecks.Clear();

            for (int i = 0; i < pending.Count; i++)
            {
                uint parentPid = pending[i].Key;
                double branchUT = pending[i].Value.branchUT;
                string parentRecordingId = pending[i].Value.recordingId;

                HashSet<uint> preBreakPids;
                preBreakVesselPidSnapshots.TryGetValue(parentPid, out preBreakPids);
                preBreakVesselPidSnapshots.Remove(parentPid);

                HandleBackgroundVesselSplit(parentPid, branchUT, parentRecordingId, preBreakPids);
            }
        }

        /// <summary>
        /// Called when a background vessel may have split. Checks if new vessels appeared
        /// from the split, creates BranchPoint + child recordings for each new vessel.
        /// Spent stages and debris get a TTL (stop recording after 30s or on destruction
        /// or when leaving physics bubble).
        /// </summary>
        internal void HandleBackgroundVesselSplit(uint parentPid, double branchUT,
            string parentRecordingId, HashSet<uint> preBreakPids)
        {
            if (tree == null) return;

            Recording parentRec;
            if (!tree.Recordings.TryGetValue(parentRecordingId, out parentRec))
            {
                ParsekLog.Warn("BgRecorder",
                    $"HandleBackgroundVesselSplit: parent recording not found: " +
                    $"parentPid={parentPid} recId={parentRecordingId}");
                return;
            }

            // Find new vessel PIDs that appeared since the pre-break snapshot
            var newVesselInfos = new List<(uint pid, string name, bool hasController)>();
            if (FlightGlobals.Vessels != null)
            {
                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    Vessel v = FlightGlobals.Vessels[i];
                    if (v == null) continue;
                    if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                    if (v.persistentId == parentPid) continue;

                    // Only consider vessels that didn't exist before the break
                    if (preBreakPids != null && preBreakPids.Contains(v.persistentId))
                        continue;

                    // Skip vessels we're already tracking in the tree
                    if (tree.BackgroundMap.ContainsKey(v.persistentId))
                        continue;

                    bool hasController = ParsekFlight.IsTrackableVessel(v);
                    newVesselInfos.Add((v.persistentId, v.vesselName ?? "Unknown", hasController));
                }
            }

            if (newVesselInfos.Count == 0)
            {
                ParsekLog.Verbose("BgRecorder",
                    $"HandleBackgroundVesselSplit: no new vessels from parentPid={parentPid} — " +
                    $"joint break did not cause vessel split");
                return;
            }

            // A split occurred. Build the branch structure.
            ParsekLog.Info("BgRecorder",
                $"Background vessel split detected: parentPid={parentPid} " +
                $"childCount={newVesselInfos.Count} branchUT={branchUT:F1}");

            // Determine BranchPoint type: JointBreak (structural separation)
            var branchType = BranchPointType.JointBreak;

            // Build a BranchPoint and child recordings using our pure static method
            var result = BuildBackgroundSplitBranchData(
                parentRecordingId, tree.Id, branchUT, branchType,
                parentPid, newVesselInfos);

            var bp = result.bp;
            var childRecordings = result.childRecordings;

            // Close parent recording: set ChildBranchPointId, close orbit segment/trajectory
            CloseParentRecording(parentRec, parentPid, bp.Id, branchUT);

            // Add BranchPoint to tree
            tree.BranchPoints.Add(bp);

            // Create a continuation recording for the parent vessel itself
            // (it keeps existing with potentially fewer parts)
            string parentContRecId = System.Guid.NewGuid().ToString("N");
            var parentContRec = new Recording
            {
                RecordingId = parentContRecId,
                TreeId = tree.Id,
                VesselPersistentId = parentPid,
                VesselName = parentRec.VesselName,
                ParentBranchPointId = bp.Id,
                ExplicitStartUT = branchUT,
                IsDebris = parentRec.IsDebris
            };
            bp.ChildRecordingIds.Insert(0, parentContRecId);
            tree.Recordings[parentContRecId] = parentContRec;
            tree.BackgroundMap[parentPid] = parentContRecId;

            // Re-initialize tracking state for the parent continuation
            OnVesselBackgrounded(parentPid);

            // Set TTL for parent continuation if it's debris
            if (parentRec.IsDebris)
            {
                debrisTTLExpiry[parentPid] = branchUT + DebrisTTLSeconds;
                ParsekLog.Info("BgRecorder",
                    $"Parent continuation is debris, TTL set: parentPid={parentPid} " +
                    $"expiry={branchUT + DebrisTTLSeconds:F1}");
            }

            // Add each child recording to tree and BackgroundMap
            RegisterChildRecordingsFromSplit(childRecordings, newVesselInfos, branchUT);

            ParsekLog.Info("BgRecorder",
                $"Background split branch complete: bp={bp.Id} type={branchType} " +
                $"parentRecId={parentRecordingId} children={bp.ChildRecordingIds.Count} " +
                $"(1 parent continuation + {newVesselInfos.Count} new vessels)");
        }

        /// <summary>
        /// Closes the parent recording at a branch point: sets ChildBranchPointId, ExplicitEndUT,
        /// closes any open orbit segment, samples a final boundary point, and removes from tracking dicts.
        /// </summary>
        private void CloseParentRecording(Recording parentRec, uint parentPid, string branchPointId, double branchUT)
        {
            parentRec.ChildBranchPointId = branchPointId;
            parentRec.ExplicitEndUT = branchUT;

            // Close parent's orbit segment if open
            BackgroundOnRailsState parentRails;
            if (onRailsStates.TryGetValue(parentPid, out parentRails) && parentRails.hasOpenOrbitSegment)
            {
                CloseOrbitSegment(parentRails, branchUT);
            }

            // Sample a final boundary point for the parent if loaded
            BackgroundVesselState parentLoaded;
            if (loadedStates.TryGetValue(parentPid, out parentLoaded))
            {
                Vessel parentVessel = FlightRecorder.FindVesselByPid(parentPid);
                if (parentVessel != null)
                {
                    SampleBoundaryPoint(parentVessel, parentRec, branchUT);
                }
            }

            // Remove parent from BackgroundMap (it has branched, no longer a live recording)
            tree.BackgroundMap.Remove(parentPid);
            onRailsStates.Remove(parentPid);
            loadedStates.Remove(parentPid);
            debrisTTLExpiry.Remove(parentPid);
        }

        /// <summary>
        /// Registers child recordings from a vessel split: captures snapshots, adds to tree
        /// and BackgroundMap, initializes tracking state, and sets TTL for debris children.
        /// </summary>
        private void RegisterChildRecordingsFromSplit(
            List<Recording> childRecordings,
            List<(uint pid, string name, bool hasController)> newVesselInfos,
            double branchUT)
        {
            // Capture vessel snapshots so ghosts can be built during playback
            // (without snapshots, GhostVisualBuilder returns null → no ghost appears).
            for (int i = 0; i < childRecordings.Count; i++)
            {
                var child = childRecordings[i];

                // Capture snapshot for ghost building — the vessel is still loaded at this point
                Vessel childVessel = FlightRecorder.FindVesselByPid(child.VesselPersistentId);
                if (childVessel != null)
                {
                    child.VesselSnapshot = VesselSpawner.TryBackupSnapshot(childVessel);
                    child.GhostVisualSnapshot = child.VesselSnapshot != null
                        ? child.VesselSnapshot.CreateCopy() : null;
                    ParsekLog.Verbose("BgRecorder",
                        $"Captured snapshot for child vessel: pid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' hasSnapshot={child.VesselSnapshot != null}");
                }
                else
                {
                    ParsekLog.Warn("BgRecorder",
                        $"Could not capture snapshot for child vessel: pid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}' — vessel not found, ghost will not render");
                }

                tree.Recordings[child.RecordingId] = child;
                tree.BackgroundMap[child.VesselPersistentId] = child.RecordingId;

                // Initialize tracking state for new child vessel
                OnVesselBackgrounded(child.VesselPersistentId);

                bool hasController = newVesselInfos[i].hasController;
                if (!hasController)
                {
                    // Debris child: set TTL
                    child.IsDebris = true;
                    debrisTTLExpiry[child.VesselPersistentId] = branchUT + DebrisTTLSeconds;
                    ParsekLog.Info("BgRecorder",
                        $"Child recording created (debris, TTL={DebrisTTLSeconds:F0}s): " +
                        $"recId={child.RecordingId} vesselPid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}'");
                }
                else
                {
                    // Controlled child: no TTL
                    ParsekLog.Info("BgRecorder",
                        $"Child recording created (controlled, no TTL): " +
                        $"recId={child.RecordingId} vesselPid={child.VesselPersistentId} " +
                        $"name='{child.VesselName}'");
                }
            }
        }

        /// <summary>
        /// Pure static method: creates a BranchPoint and child Recording objects for a
        /// background vessel split. Testable without Unity.
        /// The parent vessel gets a continuation recording (added by the caller);
        /// this method creates recordings for the NEW child vessels only.
        /// </summary>
        internal static (BranchPoint bp, List<Recording> childRecordings)
            BuildBackgroundSplitBranchData(
                string parentRecordingId, string treeId, double branchUT,
                BranchPointType branchType, uint parentVesselPid,
                List<(uint pid, string name, bool hasController)> newVesselInfos)
        {
            string bpId = System.Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = branchUT,
                Type = branchType,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string>(),
                SplitCause = "DECOUPLE"
            };

            var childRecordings = new List<Recording>(newVesselInfos.Count);
            for (int i = 0; i < newVesselInfos.Count; i++)
            {
                string childId = System.Guid.NewGuid().ToString("N");
                bp.ChildRecordingIds.Add(childId);

                var child = new Recording
                {
                    RecordingId = childId,
                    TreeId = treeId,
                    VesselPersistentId = newVesselInfos[i].pid,
                    VesselName = newVesselInfos[i].name,
                    ParentBranchPointId = bpId,
                    ExplicitStartUT = branchUT,
                    IsDebris = !newVesselInfos[i].hasController
                };
                childRecordings.Add(child);
            }

            return (bp, childRecordings);
        }

        /// <summary>
        /// Checks debris TTL expiry for all tracked debris vessels.
        /// Called once per frame from Update(). When a debris vessel's TTL has expired,
        /// its recording is finalized and tracking state is cleaned up.
        /// Also checks for destroyed/out-of-bubble vessels.
        /// </summary>
        internal void CheckDebrisTTL(double currentUT)
        {
            if (debrisTTLExpiry.Count == 0) return;

            // Collect expired entries (can't modify dict during iteration)
            List<uint> expired = null;

            foreach (var kvp in debrisTTLExpiry)
            {
                uint vesselPid = kvp.Key;
                double expiry = kvp.Value;

                if (double.IsNaN(expiry)) continue; // no TTL

                // Check if vessel is destroyed or out of bubble
                Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                if (v == null)
                {
                    // Vessel gone (destroyed or despawned)
                    if (expired == null) expired = new List<uint>();
                    expired.Add(vesselPid);

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel destroyed/despawned, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }

                // Check if TTL expired
                if (currentUT >= expiry)
                {
                    if (expired == null) expired = new List<uint>();
                    expired.Add(vesselPid);

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL expired, ending recording: " +
                        $"vesselPid={vesselPid} expiry={expiry:F1} currentUT={currentUT:F1}");
                    continue;
                }

                // Check if vessel went out of physics bubble (packed + on rails)
                if (!v.loaded)
                {
                    if (expired == null) expired = new List<uint>();
                    expired.Add(vesselPid);

                    ParsekLog.Info("BgRecorder",
                        $"Debris TTL: vessel left physics bubble, ending recording: " +
                        $"vesselPid={vesselPid}");
                    continue;
                }
            }

            if (expired == null) return;

            for (int i = 0; i < expired.Count; i++)
            {
                EndDebrisRecording(expired[i], currentUT);
            }
        }

        /// <summary>
        /// Sets the debris TTL expiry for an externally-created debris recording.
        /// Called by ParsekFlight when promoting a standalone recording to a tree
        /// and adding debris child recordings.
        /// </summary>
        public void SetDebrisExpiry(uint vesselPid, double expiryUT)
        {
            debrisTTLExpiry[vesselPid] = expiryUT;
            ParsekLog.Info("BgRecorder",
                "Debris expiry set: pid=" + vesselPid +
                ", expiryUT=" + expiryUT.ToString("F1", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Ends a debris recording: finalizes the recording with a terminal state,
        /// cleans up tracking state, removes from BackgroundMap.
        /// </summary>
        private void EndDebrisRecording(uint vesselPid, double endUT)
        {
            debrisTTLExpiry.Remove(vesselPid);

            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            Recording rec;
            if (tree.Recordings.TryGetValue(recordingId, out rec))
            {
                rec.ExplicitEndUT = endUT;

                // Determine terminal state from vessel situation
                Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                if (v == null)
                {
                    rec.TerminalStateValue = TerminalState.Destroyed;
                }
                else
                {
                    rec.TerminalStateValue = RecordingTree.DetermineTerminalState((int)v.situation, v);
                    ParsekFlight.CaptureTerminalOrbit(rec, v);
                }
            }

            // Clean up tracking state
            OnVesselRemovedFromBackground(vesselPid);
            tree.BackgroundMap.Remove(vesselPid);

            ParsekLog.Info("BgRecorder",
                $"Debris recording ended: vesselPid={vesselPid} recId={recordingId} " +
                $"terminal={rec?.TerminalStateValue?.ToString() ?? "null"}");
        }

        /// <summary>
        /// Pure static method: determines whether a debris vessel's TTL has expired,
        /// or whether it should stop recording for another reason.
        /// Returns the reason string if should stop, null if should continue.
        /// </summary>
        internal static string ShouldStopDebrisRecording(
            double currentUT, double ttlExpiry, bool vesselExists, bool vesselLoaded)
        {
            if (!vesselExists)
                return "destroyed";

            if (!double.IsNaN(ttlExpiry) && currentUT >= ttlExpiry)
                return "ttl_expired";

            if (!vesselLoaded)
                return "out_of_bubble";

            return null;
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
                    Recording treeRec;
                    if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
                    {
                        treeRec.ExplicitEndUT = currentUT;
                        state.lastExplicitEndUpdate = currentUT;
                        ParsekLog.VerboseRateLimited("BgRecorder", $"onRailsEndUT.{pid}",
                            $"On-rails ExplicitEndUT updated: pid={pid} UT={currentUT:F1}", 30.0);
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
            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            double ut = Planetarium.GetUniversalTime();

            // Poll part events (always, regardless of sampling rate)
            PollPartEvents(bgVessel, state, treeRec, ut);

            // Check for environment transitions (always, regardless of sampling rate)
            if (state.environmentHysteresis != null)
            {
                bool hasAtmo = bgVessel.mainBody != null && bgVessel.mainBody.atmosphere;
                double atmoDepth = hasAtmo ? bgVessel.mainBody.atmosphereDepth : 0;
                double approachAlt = (!hasAtmo && bgVessel.mainBody != null)
                    ? FlightRecorder.ComputeApproachAltitude(bgVessel.mainBody) : 0;
                var rawEnv = ClassifyBackgroundEnvironment(
                    hasAtmo, bgVessel.altitude, atmoDepth,
                    (int)bgVessel.situation, bgVessel.srfSpeed, state.cachedEngines, approachAlt);
                if (state.environmentHysteresis.Update(rawEnv, ut))
                {
                    var newEnv = state.environmentHysteresis.CurrentEnvironment;
                    ParsekLog.Info("BgRecorder",
                        $"Environment transition: pid={pid} -> {newEnv} " +
                        $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
                    CloseBackgroundTrackSection(state, ut);
                    StartBackgroundTrackSection(state, newEnv, ReferenceFrame.Absolute, ut);
                }
            }

            // Compute distance to focused vessel and determine proximity-based sample interval
            double distance = ComputeDistanceToFocusedVessel(bgVessel);
            double proximityInterval = ProximityRateSelector.GetSampleInterval(distance);

            // Log when sample rate changes for this vessel
            if (proximityInterval != state.currentSampleInterval)
            {
                float newHz = ProximityRateSelector.GetSampleRateHz(distance);
                string distStr = distance.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                string hzStr = newHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                ParsekLog.Info("BgRecorder",
                    $"Sample rate changed: pid={pid} dist={distStr}m " +
                    $"interval={FormatInterval(proximityInterval)} ({hzStr} Hz)");
                state.currentSampleInterval = proximityInterval;
            }

            // Skip trajectory sampling if out of range
            if (proximityInterval >= ProximityRateSelector.OutOfRangeInterval)
            {
                return;
            }

            // Gate sampling by proximity-based interval
            if (state.lastSampleUT >= 0 && (ut - state.lastSampleUT) < proximityInterval)
            {
                return;
            }

            // Adaptive sampling (velocity-based within the proximity-gated window)
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
            treeRec.MarkFilesDirty();
            state.lastRecordedUT = point.ut;
            state.lastRecordedVelocity = point.velocity;
            state.lastSampleUT = ut;

            // Dual-write: also add to current TrackSection's frames list
            if (state.trackSectionActive && state.currentTrackSection.frames != null)
            {
                state.currentTrackSection.frames.Add(point);
            }

            // Update ExplicitEndUT
            treeRec.ExplicitEndUT = ut;

            ParsekLog.VerboseRateLimited("BgRecorder", $"bgPhysics.{pid}",
                $"Background point sampled: pid={pid} pts={treeRec.Points.Count} " +
                $"alt={bgVessel.altitude:F0} dist={distance:F0}m interval={FormatInterval(proximityInterval)}", 5.0);
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

            // Close TrackSection and flush if loaded
            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(vesselPid, out loadedState))
            {
                CloseBackgroundTrackSection(loadedState, ut);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(loadedState, flushRec);

                    // Sample a final boundary point
                    Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
                    if (v != null)
                    {
                        SampleBoundaryPoint(v, flushRec, ut);
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
                // Close the active ABSOLUTE/Background TrackSection
                CloseBackgroundTrackSection(loadedState, ut);

                // Open an ORBITAL_CHECKPOINT/Checkpoint section for the on-rails phase
                StartCheckpointTrackSection(loadedState, ut);

                // Flush accumulated TrackSections to the recording
                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(loadedState, flushRec);

                    // Sample a final boundary point
                    SampleBoundaryPoint(v, flushRec, ut);
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
                Recording treeRec;
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
                    state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
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

            BackgroundVesselState loadedState;
            if (loadedStates.TryGetValue(pid, out loadedState))
            {
                CloseBackgroundTrackSection(loadedState, ut);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(loadedState.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(loadedState, flushRec);
                    // Bug #280: persist the flushed data to disk IMMEDIATELY at
                    // finalization time. Relying on OnSave to see FilesDirty and write
                    // the file has proven fragile — the 2026-04-09 playtest showed
                    // destroyed debris recordings consistently losing their 22-frame
                    // TrackSection data across the scene reload because either the
                    // dirty flag or the save path dropped them. Writing here bypasses
                    // OnSave entirely for the destroy path and guarantees the file
                    // exists on disk before any scene transition.
                    PersistFinalizedRecording(flushRec, $"OnBackgroundVesselWillDestroy pid={pid}");
                }
                loadedStates.Remove(pid);
            }

            if (v.vesselType == VesselType.EVA)
                ParsekLog.Info("BgRecorder", $"Background EVA vessel ended: pid={pid}");
            else
                ParsekLog.Warn("BgRecorder", $"Background vessel destroyed: pid={pid}");
        }

        /// <summary>
        /// Writes a background recording's .prec sidecar to disk immediately after a
        /// finalization flush, bypassing OnSave's FilesDirty-driven path. Used by
        /// destroy/shutdown/split sites where the in-memory data must survive any
        /// scene reload that might happen before the next OnSave.
        ///
        /// Background: the 2026-04-09 playtest revealed ~16 debris recordings losing
        /// all their trajectory data on F9 quickload. BackgroundRecorder flushed the
        /// TrackSections to the Recording in memory and called <c>MarkFilesDirty</c>,
        /// but by OnSave time the files were never written — so on reload, the
        /// missing files produced 0-point recordings, and the subsequent CommitTree
        /// pass wrote those empty recordings over the in-memory data. Persisting at
        /// flush time closes the window between "data exists in memory" and "data
        /// exists on disk" (bug #280).
        /// </summary>
        internal static void PersistFinalizedRecording(Recording rec, string context)
        {
            if (rec == null) return;
            if (RecordingStore.SaveRecordingFiles(rec))
            {
                ParsekLog.Verbose("BgRecorder",
                    $"PersistFinalizedRecording: wrote sidecar for " +
                    $"recId={rec.RecordingId} ({context})");
            }
            else
            {
                ParsekLog.Warn("BgRecorder",
                    $"PersistFinalizedRecording: failed to write sidecar for " +
                    $"recId={rec.RecordingId} ({context})");
            }
        }

        /// <summary>
        /// Called on scene change or tree teardown. Closes all open orbit segments
        /// and cleans up all per-vessel state.
        /// </summary>
        public void Shutdown()
        {
            UnsubscribePartEvents();

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

            // Close and flush TrackSections for all loaded vessels
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
                CloseBackgroundTrackSection(state, ut);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(state, flushRec);
                    // Bug #280: persist flushed data immediately (see PersistFinalizedRecording docs).
                    PersistFinalizedRecording(flushRec, $"Shutdown pid={state.vesselPid}");
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

            // Close and flush TrackSections for all loaded vessels, emit terminal engine/RCS/robotic events (bug #108)
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
                CloseBackgroundTrackSection(state, commitUT);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(state, flushRec);

                    var terminalEvents = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                        state.activeEngineKeys, state.activeRcsKeys, state.activeRoboticKeys,
                        state.lastRoboticPosition, commitUT, "BgRecorder");
                    if (terminalEvents.Count > 0)
                    {
                        flushRec.PartEvents.AddRange(terminalEvents);
                        // FlushTrackSectionsToRecording only marks dirty when
                        // state.trackSections.Count > 0 — a background vessel
                        // finalized with no accumulated sections (e.g. one that
                        // just entered loaded state) would otherwise leave
                        // flushRec.FilesDirty false after this terminal emit.
                        flushRec.MarkFilesDirty();
                    }
                }
            }

            // Update ExplicitEndUT on all background recordings
            foreach (var kvp in tree.BackgroundMap)
            {
                Recording treeRec;
                if (tree.Recordings.TryGetValue(kvp.Value, out treeRec))
                {
                    treeRec.ExplicitEndUT = commitUT;
                }
            }

            ParsekLog.Info("BgRecorder", $"FinalizeAllForCommit complete at UT={commitUT:F1} " +
                $"({onRailsStates.Count} on-rails, {loadedStates.Count} loaded)");
        }

        /// <summary>
        /// Captures an orbital checkpoint for all on-rails background vessels at the given UT.
        /// For each vessel with an open orbit segment, closes the current segment and opens a
        /// fresh one with the vessel's current orbital elements. This creates clean reference
        /// points for orbital propagation during playback.
        /// Called at time warp boundaries and scene changes.
        /// </summary>
        internal void CheckpointAllVessels(double ut)
        {
            CheckpointAllVessels(ut, vesselFinderOverride);
        }

        /// <summary>
        /// Core checkpoint logic with injectable vessel finder for testability.
        /// If vesselFinder is null, uses FlightRecorder.FindVesselByPid.
        /// </summary>
        internal void CheckpointAllVessels(double ut, System.Func<uint, Vessel> vesselFinder)
        {
            if (tree == null) return;

            int checkpointed = 0;
            int skippedNoVessel = 0;
            int skippedNotOrbital = 0;

            foreach (var kvp in onRailsStates)
            {
                uint vesselPid = kvp.Key;
                var state = kvp.Value;

                // Only checkpoint vessels with open orbit segments (on-rails, orbiting)
                if (!state.hasOpenOrbitSegment)
                {
                    // Landed/splashed or no-orbit vessels don't need orbital checkpoints
                    skippedNotOrbital++;
                    continue;
                }

                // Find the live vessel to get current orbital elements
                Vessel v = vesselFinder != null
                    ? vesselFinder(vesselPid)
                    : FlightRecorder.FindVesselByPid(vesselPid);

                // Close the current orbit segment at checkpoint UT regardless of vessel availability.
                // The recorded orbital data up to this point is preserved even if the vessel
                // can't be found (e.g., destroyed between frames).
                CloseOrbitSegment(state, ut);

                if (v == null || v.orbit == null)
                {
                    skippedNoVessel++;
                    ParsekLog.Verbose("BgRecorder",
                        $"CheckpointAllVessels: closed segment for pid={vesselPid} but " +
                        $"vessel/orbit not found — no new segment opened");
                    continue;
                }

                // Open a fresh orbit segment with current orbital elements
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
                state.hasOpenOrbitSegment = true;
                checkpointed++;
            }

            ParsekLog.Verbose("BgRecorder",
                $"CheckpointAllVessels at UT={ut:F2}: checkpointed={checkpointed}, " +
                $"skippedNotOrbital={skippedNotOrbital}, skippedNoVessel={skippedNoVessel}");
        }

        /// <summary>
        /// Pure static method for determining whether an orbital checkpoint should be
        /// captured for a given vessel situation.
        /// </summary>
        internal static bool IsOrbitalCheckpointSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.ORBITING
                || situation == Vessel.Situations.SUB_ORBITAL
                || situation == Vessel.Situations.ESCAPING;
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
                Recording treeRec;
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
            else if (v.mainBody != null &&
                     FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(
                         v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
            {
                // In atmosphere — Keplerian orbit ignores drag, skip orbit segment
                state.hasOpenOrbitSegment = false;

                Recording treeRec;
                if (tree.Recordings.TryGetValue(recordingId, out treeRec))
                {
                    treeRec.ExplicitEndUT = ut;
                }

                ParsekLog.Verbose("BgRecorder", $"On-rails state initialized (atmosphere, skip orbit): pid={vesselPid} " +
                    $"body={v.mainBody?.name} alt={v.altitude:F0} atmoDepth={v.mainBody.atmosphereDepth:F0}");
            }
            else if (v.orbit != null)
            {
                // Open orbit segment
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Kerbin");
                state.hasOpenOrbitSegment = true;

                Recording treeRec;
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

                Recording treeRec;
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

            // Seed all tracking sets with current part state (mirrors FlightRecorder.SeedExistingPartStates)
            SeedBackgroundPartStates(v, state);

            // Emit seed events ONLY if the recording has no part events yet.
            // If a prior active recorder (or earlier background load) already seeded events,
            // re-emitting at the current UT would poison FindLastInterestingUT for boring-tail
            // trimming (bug A / #263 sibling — rover recording not trimmed because of stale
            // DeployableExtended events at UT 2068).
            Recording treeRecForSeed;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRecForSeed))
            {
                // Tree recording not found — unexpected state. The caller supplied a recordingId
                // that doesn't exist in the tree's Recordings dict, which means a lifecycle bug
                // somewhere upstream (tree corrupted, recording deleted without updating the
                // background map, race between StopRecording and InitializeLoadedState, etc.).
                // Log loud so we notice; seeding is skipped because there's no target anyway.
                ParsekLog.Warn("BgRecorder",
                    $"InitializeLoadedState: tree recording '{recordingId}' not found for " +
                    $"pid={vesselPid} — skipping seed events (upstream lifecycle bug)");
            }
            else if (treeRecForSeed.PartEvents.Count > 0)
            {
                ParsekLog.Verbose("BgRecorder",
                    $"InitializeLoadedState: skipping seed events for pid={vesselPid} recId={recordingId} " +
                    $"— recording already has {treeRecForSeed.PartEvents.Count} part event(s)");
            }
            else
            {
                var partNamesByPid = new Dictionary<uint, string>();
                if (v.parts != null)
                {
                    for (int i = 0; i < v.parts.Count; i++)
                    {
                        Part p = v.parts[i];
                        if (p != null && !partNamesByPid.ContainsKey(p.persistentId))
                            partNamesByPid[p.persistentId] = p.partInfo?.name ?? "unknown";
                    }
                }
                double seedUT = Planetarium.GetUniversalTime();
                var seedSets = BuildPartTrackingSetsFromState(state);
                var seedEvents = PartStateSeeder.EmitSeedEvents(seedSets, partNamesByPid, seedUT, "BgRecorder");
                if (seedEvents.Count > 0)
                {
                    treeRecForSeed.PartEvents.AddRange(seedEvents);
                    // If the very next event is an OnSave (e.g., the player
                    // F5s immediately after a background vessel enters loaded
                    // physics), the seed events would otherwise be lost because
                    // nothing else marks dirty until the next poll emits an event.
                    treeRecForSeed.MarkFilesDirty();
                }
            }

            // Initialize environment tracking and open first TrackSection
            double ut = Planetarium.GetUniversalTime();
            bool hasAtmo = v.mainBody != null && v.mainBody.atmosphere;
            double atmoDepth = hasAtmo ? v.mainBody.atmosphereDepth : 0;
            double approachAlt = (!hasAtmo && v.mainBody != null)
                ? FlightRecorder.ComputeApproachAltitude(v.mainBody) : 0;
            var initialEnv = ClassifyBackgroundEnvironment(
                hasAtmo, v.altitude, atmoDepth,
                (int)v.situation, v.srfSpeed, state.cachedEngines, approachAlt);
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Absolute, ut);

            ParsekLog.Verbose("BgRecorder", $"Loaded state initialized: pid={vesselPid} " +
                $"engines={state.cachedEngines?.Count ?? 0} rcs={state.cachedRcsModules?.Count ?? 0} " +
                $"robotics={state.cachedRoboticModules?.Count ?? 0} initialEnv={initialEnv}");

            loadedStates[vesselPid] = state;
        }

        /// <summary>
        /// Builds a PartTrackingSets struct from a BackgroundVesselState's tracking fields.
        /// Used by both InitializeLoadedState (seed events) and SeedBackgroundPartStates (seed state).
        /// </summary>
        private static PartTrackingSets BuildPartTrackingSetsFromState(BackgroundVesselState state)
        {
            return new PartTrackingSets
            {
                deployedFairings = state.deployedFairings,
                jettisonedShrouds = state.jettisonedShrouds,
                parachuteStates = state.parachuteStates,
                extendedDeployables = state.extendedDeployables,
                lightsOn = state.lightsOn,
                blinkingLights = state.blinkingLights,
                lightBlinkRates = state.lightBlinkRates,
                deployedGear = state.deployedGear,
                openCargoBays = state.openCargoBays,
                deployedLadders = state.deployedLadders,
                deployedAnimationGroups = state.deployedAnimationGroups,
                deployedAnimateGenericModules = state.deployedAnimateGenericModules,
                deployedAeroSurfaceModules = state.deployedAeroSurfaceModules,
                deployedControlSurfaceModules = state.deployedControlSurfaceModules,
                deployedRobotArmScannerModules = state.deployedRobotArmScannerModules,
                animateHeatLevels = state.animateHeatLevels,
                activeEngineKeys = state.activeEngineKeys,
                lastThrottle = state.lastThrottle,
                activeRcsKeys = state.activeRcsKeys,
                lastRcsThrottle = state.lastRcsThrottle,
            };
        }

        /// <summary>
        /// Pre-populates a BackgroundVesselState's tracking sets with the current
        /// state of every part on the vessel. Delegates to shared PartStateSeeder.
        /// </summary>
        private static void SeedBackgroundPartStates(Vessel v, BackgroundVesselState state)
        {
            PartStateSeeder.SeedPartStates(v,
                BuildPartTrackingSetsFromState(state),
                state.cachedEngines, state.cachedRcsModules,
                seedColorChangerLights: false, logTag: "BgRecorder");
        }

        /// <summary>
        /// Creates an OrbitSegment from a vessel's current orbital elements.
        /// Used by OnBackgroundVesselSOIChanged, CheckpointAllVessels, and InitializeOnRailsState.
        /// </summary>
        private static OrbitSegment CreateOrbitSegmentFromVessel(Vessel v, double startUT, string defaultBodyName)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                inclination = v.orbit.inclination,
                eccentricity = v.orbit.eccentricity,
                semiMajorAxis = v.orbit.semiMajorAxis,
                longitudeOfAscendingNode = v.orbit.LAN,
                argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
                epoch = v.orbit.epoch,
                bodyName = v.mainBody?.name ?? defaultBodyName
            };
        }

        private void CloseOrbitSegment(BackgroundOnRailsState state, double ut)
        {
            if (!state.hasOpenOrbitSegment) return;

            state.currentOrbitSegment.endUT = ut;

            Recording treeRec;
            if (tree.Recordings.TryGetValue(state.recordingId, out treeRec))
            {
                treeRec.OrbitSegments.Add(state.currentOrbitSegment);
                treeRec.MarkFilesDirty();
                treeRec.ExplicitEndUT = ut;
            }

            state.hasOpenOrbitSegment = false;

            ParsekLog.Verbose("BgRecorder", $"Orbit segment closed: pid={state.vesselPid} " +
                $"UT={state.currentOrbitSegment.startUT:F1}-{ut:F1} body={state.currentOrbitSegment.bodyName}");
        }

        private void SampleBoundaryPoint(Vessel v, Recording treeRec, double ut)
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
            treeRec.MarkFilesDirty();
            treeRec.ExplicitEndUT = ut;

            ParsekLog.Verbose("BgRecorder", $"Boundary point sampled: pid={v.persistentId} " +
                $"UT={ut:F1} alt={v.altitude:F0} pts={treeRec.Points.Count}");
        }

        /// <summary>
        /// Computes the distance in meters between a background vessel and the focused vessel.
        /// Returns double.MaxValue if there is no focused vessel (defensive).
        /// Uses an injectable override for testing; falls back to FlightGlobals.ActiveVessel.
        /// </summary>
        internal double ComputeDistanceToFocusedVessel(Vessel bgVessel)
        {
            if (distanceOverrideForTesting != null)
                return distanceOverrideForTesting(bgVessel);

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null) return double.MaxValue;
            return Vector3d.Distance(active.transform.position, bgVessel.transform.position);
        }

        /// <summary>
        /// Formats a sample interval for log messages.
        /// </summary>
        private static string FormatInterval(double interval)
        {
            if (interval >= ProximityRateSelector.OutOfRangeInterval || double.IsInfinity(interval))
                return "none";
            return interval.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        #endregion

        #region Background Environment Classification & TrackSection Management

        /// <summary>
        /// Classifies the environment for a background vessel using its cached engine list.
        /// Similar to FlightRecorder.ClassifyCurrentEnvironment but works with
        /// BackgroundVesselState's cached engines instead of FlightRecorder instance fields.
        /// Pure enough to be internal static for testability.
        /// </summary>
        internal static SegmentEnvironment ClassifyBackgroundEnvironment(
            bool hasAtmosphere, double altitude, double atmosphereDepth,
            int situation, double srfSpeed,
            List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines,
            double approachAltitude = 0)
        {
            bool hasActiveThrust = false;
            if (cachedEngines != null)
            {
                for (int i = 0; i < cachedEngines.Count; i++)
                {
                    if (cachedEngines[i].engine != null &&
                        cachedEngines[i].engine.EngineIgnited &&
                        cachedEngines[i].engine.finalThrust > 0)
                    {
                        hasActiveThrust = true;
                        break;
                    }
                }
            }

            return EnvironmentDetector.Classify(
                hasAtmosphere, altitude, atmosphereDepth,
                situation, srfSpeed, hasActiveThrust, approachAltitude);
        }

        /// <summary>
        /// Opens a new TrackSection for a background vessel.
        /// Sets source = Background.
        /// </summary>
        private void StartBackgroundTrackSection(
            BackgroundVesselState state, SegmentEnvironment env, ReferenceFrame refFrame, double ut)
        {
            state.currentTrackSection = new TrackSection
            {
                environment = env,
                referenceFrame = refFrame,
                startUT = ut,
                source = TrackSectionSource.Background,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };
            state.trackSectionActive = true;
            ParsekLog.Info("BgRecorder",
                $"TrackSection started: env={env} ref={refFrame} source=Background " +
                $"pid={state.vesselPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Opens an OrbitalCheckpoint TrackSection for a background vessel transitioning to on-rails.
        /// Sets source = Checkpoint.
        /// </summary>
        private void StartCheckpointTrackSection(BackgroundVesselState state, double ut)
        {
            state.currentTrackSection = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = ut,
                source = TrackSectionSource.Checkpoint,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };
            state.trackSectionActive = true;
            ParsekLog.Info("BgRecorder",
                $"TrackSection started: env=ExoBallistic ref=OrbitalCheckpoint source=Checkpoint " +
                $"pid={state.vesselPid} at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Closes the current TrackSection for a background vessel and appends it
        /// to the per-vessel trackSections list.
        /// </summary>
        private void CloseBackgroundTrackSection(BackgroundVesselState state, double ut)
        {
            if (!state.trackSectionActive) return;

            state.currentTrackSection.endUT = ut;

            // Compute approximate sample rate
            if (state.currentTrackSection.frames != null && state.currentTrackSection.frames.Count > 1)
            {
                double duration = state.currentTrackSection.endUT - state.currentTrackSection.startUT;
                if (duration > 0)
                    state.currentTrackSection.sampleRateHz =
                        (float)(state.currentTrackSection.frames.Count / duration);
            }

            state.trackSections.Add(state.currentTrackSection);
            state.trackSectionActive = false;

            int frameCount = state.currentTrackSection.frames?.Count ?? 0;
            int checkpointCount = state.currentTrackSection.checkpoints?.Count ?? 0;
            double sectionDuration = ut - state.currentTrackSection.startUT;
            ParsekLog.Info("BgRecorder",
                $"TrackSection closed: env={state.currentTrackSection.environment} " +
                $"ref={state.currentTrackSection.referenceFrame} " +
                $"frames={frameCount} checkpoints={checkpointCount} " +
                $"duration={sectionDuration.ToString("F2", CultureInfo.InvariantCulture)}s " +
                $"pid={state.vesselPid}");
        }

        /// <summary>
        /// Copies all accumulated TrackSections from a BackgroundVesselState to the
        /// recording's TrackSections list.
        /// </summary>
        private static void FlushTrackSectionsToRecording(BackgroundVesselState state, Recording treeRec)
        {
            if (state.trackSections.Count == 0) return;

            for (int i = 0; i < state.trackSections.Count; i++)
            {
                treeRec.TrackSections.Add(state.trackSections[i]);
            }
            treeRec.MarkFilesDirty();

            ParsekLog.Info("BgRecorder",
                $"Flushed {state.trackSections.Count} TrackSections to recording: " +
                $"pid={state.vesselPid} recId={state.recordingId}");

            // Clear after flush to prevent duplicate sections if flushed again
            // (e.g., FinalizeAllForCommit followed by Shutdown)
            state.trackSections.Clear();
        }

        #endregion

        #region Part Event Polling

        /// <summary>
        /// Polls all part event types for a background vessel in loaded/physics mode.
        /// Duplicates the Layer 2 wrapper methods from FlightRecorder, calling the
        /// same Layer 1 static transition methods.
        ///
        /// Part Event Coverage Audit (BackgroundRecorder vs FlightRecorder):
        ///
        ///   POLLED (per-physics-frame, in PollPartEvents):
        ///     [x] CheckParachuteState        - ParachuteDeployed / ParachuteCut / ParachuteSemiDeployed
        ///     [x] CheckJettisonState          - ShroudJettisoned
        ///     [x] CheckEngineState            - EngineIgnited / EngineShutdown / EngineThrottle
        ///     [x] CheckRcsState               - RCSActivated / RCSStopped / RCSThrottle
        ///     [x] CheckDeployableState        - DeployableExtended / DeployableRetracted
        ///     [x] CheckLadderState            - DeployableExtended / DeployableRetracted (ladders)
        ///     [x] CheckAnimationGroupState    - DeployableExtended / DeployableRetracted (anim groups)
        ///     [x] CheckAeroSurfaceState       - DeployableExtended / DeployableRetracted (aero surfaces)
        ///     [x] CheckControlSurfaceState    - DeployableExtended / DeployableRetracted (control surfaces)
        ///     [x] CheckRobotArmScannerState   - DeployableExtended / DeployableRetracted (robot arms)
        ///     [x] CheckAnimateHeatState       - ThermalAnimationHot / ThermalAnimationMedium / ThermalAnimationCold
        ///     [x] CheckAnimateGenericState    - DeployableExtended / DeployableRetracted (generic animations)
        ///     [x] CheckLightState             - LightOn / LightOff / LightBlinkEnabled / LightBlinkDisabled / LightBlinkRate
        ///     [x] CheckGearState              - GearDeployed / GearRetracted
        ///     [x] CheckCargoBayState          - CargoBayOpened / CargoBayClosed
        ///     [x] CheckFairingState           - FairingJettisoned
        ///     [x] CheckRoboticState           - RoboticMotionStarted / RoboticPositionSample / RoboticMotionStopped
        ///
        ///   GAME-EVENT DRIVEN (via SubscribePartEvents):
        ///     [x] onPartDie                   - Destroyed / ParachuteDestroyed (OnBackgroundPartDie)
        ///     [x] onPartJointBreak            - Decoupled (OnBackgroundPartJointBreak)
        ///
        ///   NOT APPLICABLE to background vessels (handled elsewhere):
        ///     [-] Docked / Undocked           - branch management in ParsekFlight
        ///     [-] InventoryPartPlaced/Removed  - EVA-only, active vessel only
        /// </summary>
        private void PollPartEvents(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
        {
            // Count delta before/after pattern: all CheckXState methods only
            // mutate treeRec.PartEvents (no Points/OrbitSegments/etc changes),
            // so a single post-polling dirty mark guarded on count-delta covers
            // all 17 child checks without 19 inline MarkFilesDirty calls.
            // If no events were emitted this poll, the mark is skipped — keeps
            // the frequent per-physics-frame poll cheap.
            int prePartEventCount = treeRec.PartEvents.Count;

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

            if (treeRec.PartEvents.Count > prePartEventCount)
                treeRec.MarkFilesDirty();
        }

        private void CheckParachuteState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                bool hasJettisonModule = false;
                bool isJettisoned = false;
                int jettisonModuleIndex = 0;
                bool skipTransformFallback = p.FindModuleImplementing<ModulePartVariants>() != null;
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

                    if (skipTransformFallback)
                        continue;

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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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

                reusableEventBuffer.Clear();
                FlightRecorder.CheckLightBlinkTransition(
                    p.persistentId, p.partInfo?.name ?? "unknown",
                    light.isBlinking, light.blinkRate,
                    state.blinkingLights, state.lightBlinkRates, ut, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckGearState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
        {
            if (state.cachedEngines == null) return;

            for (int i = 0; i < state.cachedEngines.Count; i++)
            {
                var (part, engine, moduleIndex) = state.cachedEngines[i];
                if (part == null || engine == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool ignited = engine.EngineIgnited && engine.isOperational;
                float throttle = engine.currentThrottle;

                reusableEventBuffer.Clear();
                FlightRecorder.CheckEngineTransition(
                    key, part.persistentId, moduleIndex,
                    part.partInfo?.name ?? "unknown",
                    ignited, throttle,
                    state.activeEngineKeys, state.lastThrottle, ut, reusableEventBuffer);

                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} " +
                        $"val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");

                    // Engine-ignite jettison fallback (same as FlightRecorder)
                    if (reusableEventBuffer[e].eventType == PartEventType.EngineIgnited &&
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
            Recording treeRec, double ut)
        {
            if (state.cachedRcsModules == null) return;

            for (int i = 0; i < state.cachedRcsModules.Count; i++)
            {
                var (part, rcs, moduleIndex) = state.cachedRcsModules[i];
                if (part == null || rcs == null) continue;

                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                uint pid = part.persistentId;
                string partName = part.partInfo?.name ?? "unknown";

                reusableEventBuffer.Clear();
                FlightRecorder.ProcessRcsDebounce(
                    key, pid, moduleIndex, partName,
                    active, rcs.thrustForces, rcs.thrusterPower, ut,
                    state.rcsActiveFrameCount, state.activeRcsKeys, state.lastRcsThrottle, reusableEventBuffer);
                for (int e = 0; e < reusableEventBuffer.Count; e++)
                {
                    treeRec.PartEvents.Add(reusableEventBuffer[e]);
                    ParsekLog.Verbose("BgRecorder", $"Part event: {reusableEventBuffer[e].eventType} '{reusableEventBuffer[e].partName}' " +
                        $"pid={reusableEventBuffer[e].partPersistentId} midx={reusableEventBuffer[e].moduleIndex} " +
                        $"val={reusableEventBuffer[e].value:F2} (bg vessel {state.vesselPid})");
                }
            }
        }

        private void CheckLadderState(Vessel v, BackgroundVesselState state,
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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
            Recording treeRec, double ut)
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

        internal bool IsPartEventsSubscribed => partEventsSubscribed;

        /// <summary>
        /// For testing: injects a loaded state for a vessel PID so that
        /// OnBackgroundPartDie / OnBackgroundPartJointBreak can find it
        /// without needing a real KSP Vessel.
        /// </summary>
        internal void InjectLoadedStateForTesting(uint vesselPid, string recordingId)
        {
            loadedStates[vesselPid] = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
            };
        }

        /// <summary>
        /// For testing: gets the decoupledPartIds set for a given vessel PID.
        /// Returns null if no loaded state exists.
        /// </summary>
        internal HashSet<uint> GetDecoupledPartIdsForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.decoupledPartIds;
            return null;
        }

        /// <summary>
        /// For testing: injects an open orbit segment into the on-rails state for a vessel PID.
        /// The vessel must already have an on-rails state (created by constructor or OnVesselBackgrounded).
        /// </summary>
        internal void InjectOpenOrbitSegmentForTesting(uint vesselPid, OrbitSegment segment)
        {
            BackgroundOnRailsState state;
            if (onRailsStates.TryGetValue(vesselPid, out state))
            {
                state.currentOrbitSegment = segment;
                state.hasOpenOrbitSegment = true;
            }
        }

        /// <summary>
        /// For testing: overrides the vessel finder used by CheckpointAllVessels.
        /// Set to null to restore default behavior (FlightRecorder.FindVesselByPid).
        /// </summary>
        internal void SetVesselFinderForTesting(System.Func<uint, Vessel> finder)
        {
            vesselFinderOverride = finder;
        }

        /// <summary>
        /// For testing: overrides the distance-to-focused-vessel computation.
        /// Set to null to restore default behavior (FlightGlobals.ActiveVessel distance).
        /// </summary>
        internal void SetDistanceOverrideForTesting(System.Func<Vessel, double> distanceFunc)
        {
            distanceOverrideForTesting = distanceFunc;
        }

        /// <summary>
        /// For testing: gets the current proximity sample interval for a loaded vessel.
        /// Returns double.MaxValue if no loaded state exists.
        /// </summary>
        internal double GetCurrentSampleIntervalForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.currentSampleInterval;
            return double.MaxValue;
        }

        /// <summary>
        /// For testing: gets the last sample UT for a loaded vessel.
        /// Returns -1 if no loaded state exists.
        /// </summary>
        internal double GetLastSampleUTForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.lastSampleUT;
            return -1;
        }

        /// <summary>
        /// For testing: gets the debris TTL expiry for a vessel PID.
        /// Returns double.NaN if not tracked.
        /// </summary>
        internal double GetDebrisTTLExpiryForTesting(uint vesselPid)
        {
            double expiry;
            if (debrisTTLExpiry.TryGetValue(vesselPid, out expiry))
                return expiry;
            return double.NaN;
        }

        /// <summary>
        /// For testing: injects a debris TTL expiry for a vessel PID.
        /// </summary>
        internal void InjectDebrisTTLForTesting(uint vesselPid, double expiry)
        {
            debrisTTLExpiry[vesselPid] = expiry;
        }

        /// <summary>
        /// For testing: gets the count of pending background split checks.
        /// </summary>
        internal int PendingSplitCheckCount => pendingBackgroundSplitChecks.Count;

        /// <summary>
        /// For testing: gets the count of debris TTL entries.
        /// </summary>
        internal int DebrisTTLCount => debrisTTLExpiry.Count;

        /// <summary>
        /// For testing: gets the accumulated TrackSections for a loaded vessel.
        /// Returns null if no loaded state exists.
        /// </summary>
        internal List<TrackSection> GetTrackSectionsForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.trackSections;
            return null;
        }

        /// <summary>
        /// For testing: checks if a loaded vessel has an active TrackSection.
        /// Returns false if no loaded state exists.
        /// </summary>
        internal bool GetTrackSectionActiveForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.trackSectionActive;
            return false;
        }

        /// <summary>
        /// For testing: gets the current TrackSection for a loaded vessel.
        /// Returns null/default if no loaded state exists.
        /// </summary>
        internal TrackSection? GetCurrentTrackSectionForTesting(uint vesselPid)
        {
            BackgroundVesselState state;
            if (loadedStates.TryGetValue(vesselPid, out state))
                return state.currentTrackSection;
            return null;
        }

        /// <summary>
        /// For testing: injects a loaded state with environment tracking initialized.
        /// Creates the state, sets up EnvironmentHysteresis, and opens the first TrackSection.
        /// </summary>
        internal void InjectLoadedStateWithEnvironmentForTesting(
            uint vesselPid, string recordingId, SegmentEnvironment initialEnv, double ut)
        {
            var state = new BackgroundVesselState
            {
                vesselPid = vesselPid,
                recordingId = recordingId,
            };
            state.environmentHysteresis = new EnvironmentHysteresis(initialEnv);
            StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Absolute, ut);
            loadedStates[vesselPid] = state;
        }

        #endregion
    }
}
