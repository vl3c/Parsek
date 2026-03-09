using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Main flight-scene controller for Parsek.
    /// Handles recording, playback, timeline management, and UI.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ParsekFlight : MonoBehaviour
    {
        internal const string MODID = "Parsek_NS";
        internal const string MODNAME = "Parsek";

        #region State

        // Recording (delegated to FlightRecorder)
        private FlightRecorder recorder;
        internal List<TrajectoryPoint> recording => recorder?.Recording ?? noRecording;
        private List<OrbitSegment> orbitSegments => recorder?.OrbitSegments ?? noOrbitSegments;
        private static readonly List<TrajectoryPoint> noRecording = new List<TrajectoryPoint>();
        private static readonly List<OrbitSegment> noOrbitSegments = new List<OrbitSegment>();

        // Manual playback state (preview of current recording)
        private bool isPlaying = false;
        private double playbackStartUT;
        private double recordingStartUT;
        private GameObject ghostObject;
        private List<Material> previewGhostMaterials = new List<Material>();
        private GhostPlaybackState previewGhostState;
        private RecordingStore.Recording previewRecording;
        internal int lastPlaybackIndex = 0;

        // Per-ghost playback state (bundled to prevent dict-sync bugs)
        private class LightPlaybackState
        {
            public bool isOn;
            public bool blinkEnabled;
            public float blinkRateHz = 1f;
        }

        private class GhostPlaybackState
        {
            public GameObject ghost;
            public List<Material> materials;
            public int playbackIndex;
            public int partEventIndex;
            public int loopCycleIndex = -1;
            public Dictionary<uint, List<uint>> partTree;
            public Dictionary<uint, ParachuteGhostInfo> parachuteInfos;
            public Dictionary<uint, JettisonGhostInfo> jettisonInfos;
            public Dictionary<ulong, EngineGhostInfo> engineInfos; // key = EncodeEngineKey(pid, moduleIndex)
            public Dictionary<ulong, RcsGhostInfo> rcsInfos;   // separate from engineInfos — keys can overlap for same part
            public Dictionary<ulong, RoboticGhostInfo> roboticInfos; // key = EncodeEngineKey(pid, moduleIndex)
            public Dictionary<uint, DeployableGhostInfo> deployableInfos;
            public Dictionary<uint, HeatGhostInfo> heatInfos;
            public Dictionary<uint, LightGhostInfo> lightInfos;
            public Dictionary<uint, LightPlaybackState> lightPlaybackStates;
            public Dictionary<uint, FairingGhostInfo> fairingInfos;
            public Dictionary<uint, GameObject> fakeCanopies;
            public ReentryFxInfo reentryFxInfo;
            public Vector3 lastInterpolatedVelocity;
            public string lastInterpolatedBodyName;
            public double lastInterpolatedAltitude;

            public void SetInterpolated(InterpolationResult r)
            {
                lastInterpolatedVelocity = r.velocity;
                lastInterpolatedBodyName = r.bodyName;
                lastInterpolatedAltitude = r.altitude;
            }
        }

        private struct InterpolationResult
        {
            public Vector3 velocity;
            public string bodyName;
            public double altitude;

            public static readonly InterpolationResult Zero = new InterpolationResult
            {
                velocity = Vector3.zero,
                bodyName = null,
                altitude = 0
            };

            public InterpolationResult(Vector3 vel, string body, double alt)
            {
                velocity = vel;
                bodyName = body;
                altitude = alt;
            }
        }

        private Dictionary<int, GhostPlaybackState> ghostStates = new Dictionary<int, GhostPlaybackState>();

        // Auto-record: EVA from pad triggers recording after vessel switch completes
        private bool pendingAutoRecord = false;

        // Active chain being built (persists across segments)
        private string activeChainId;          // null if not building a chain
        private int activeChainNextIndex;      // next segment's ChainIndex
        private string activeChainPrevId;      // previous segment's RecordingId (for ParentRecordingId)
        private string activeChainCrewName;    // EVA crew name for current segment (null if vessel)

        // Pending chain transition
        private bool pendingChainContinuation; // true when a segment ended and next should start
        private bool pendingChainIsBoarding;   // true = boarding (EVA→vessel), false = EVA exit
        private string pendingChainEvaName;    // kerbal name for EVA transitions

        // Boarding confirmation via onCrewBoardVessel
        private uint pendingBoardingTargetPid; // vessel PID from onCrewBoardVessel, 0 = none
        private int boardingConfirmFrames;     // frames since boarding event (auto-clear after 3)

        // Boundary anchor for chain continuation (copied from previous segment's last point)
        private TrajectoryPoint? pendingBoundaryAnchor;

        // Pending dock/undock transitions (set by event handlers, consumed by Update)
        private uint pendingDockMergedPid;          // merged vessel pid, 0 = no pending dock
        private bool pendingDockAsTarget;           // true if our vessel was the dock target (no pid change)
        private int dockConfirmFrames;              // frame counter for confirmation window (auto-clear after 5)
        private uint pendingUndockOtherPid;         // pid of vessel that split off, 0 = no pending undock
        private int undockConfirmFrames;            // frame counter for confirmation window

        // Undock continuation (ghost-only recording for the other vessel)
        private uint undockContinuationPid;         // 0 = not tracking
        private int undockContinuationRecIdx = -1;
        private Vector3 undockContinuationLastVel;
        private double undockContinuationLastUT = -1;

        // Continuation sampling: after a vessel chain segment commits (V→EVA),
        // keeps tracking the original vessel so its trajectory extends beyond the EVA point.
        private uint continuationVesselPid;        // 0 = not tracking
        private int continuationRecordingIdx = -1; // index into CommittedRecordings
        private Vector3 continuationLastVelocity;
        private double continuationLastUT = -1;

        // Continuation adaptive sampling thresholds (read from settings, same as FlightRecorder)
        private static float continuationMaxInterval =>
            ParsekSettings.Current?.maxSampleInterval ?? 3.0f;
        private static float continuationVelDirThreshold =>
            ParsekSettings.Current?.velocityDirThreshold ?? 2.0f;
        private static float continuationSpeedThreshold =>
            (ParsekSettings.Current?.speedChangeThreshold ?? 5.0f) / 100f;

        // Recording tree mode (null when not in tree mode)
        private RecordingTree activeTree;

        // Background recorder for tree mode (null when not in tree mode)
        private BackgroundRecorder backgroundRecorder;

        // Split event detection: deduplication and race-condition guard
        private double lastBranchUT = -1;
        private HashSet<uint> lastBranchVesselPids = new HashSet<uint>();
        private bool pendingSplitInProgress;
        private FlightRecorder pendingSplitRecorder;
        // Vessel PIDs that existed before a joint break (for filtering pre-existing vessels)
        private HashSet<uint> preBreakVesselPids;

        // Merge event detection (tree mode)
        private bool pendingTreeDockMerge;           // true when a tree dock merge is pending
        private uint pendingDockAbsorbedPid;         // PID of absorbed vessel in dock merge
        private bool pendingBoardingTargetInTree;    // true if boarding target is in the tree

        // Docking race condition guard (Task 5 sets, Task 6 checks)
        internal HashSet<uint> dockingInProgress = new HashSet<uint>();

        /// <summary>
        /// Holds captured vessel state between Phase 1 (synchronous, inside OnVesselWillDestroy)
        /// and Phase 3 (deferred, after yield return null). Captured by the coroutine state machine
        /// (heap-allocated, one per deferred check).
        /// </summary>
        internal struct PendingDestruction
        {
            public uint vesselPid;
            public string recordingId;
            public double capturedUT;
            public Vessel.Situations situation;

            // Orbit (captured from vessel.orbit before destruction)
            public bool hasOrbit;
            public double inclination;
            public double eccentricity;
            public double semiMajorAxis;
            public double lan;
            public double argumentOfPeriapsis;
            public double meanAnomalyAtEpoch;
            public double epoch;
            public string bodyName;

            // Surface position
            public bool hasSurface;
            public SurfacePosition surfacePosition;
        }

        // Timeline warp protection — tracks previous frame's UT
        private double lastTimelineUT = -1;

        // Diagnostic logging guards (log once per state transition, not per frame)
        private HashSet<int> loggedGhostEnter = new HashSet<int>();
        private HashSet<int> loggedOrbitSegments = new HashSet<int>();

        // Warp-stop guard: only stop time warp once per recording
        private HashSet<int> warpStoppedForRecording = new HashSet<int>();
        private bool timelineResourceReplayPausedLogged = false;
        private const double DefaultLoopPauseSeconds = 10.0;
        private const double MinLoopDurationSeconds = 0.001;

        // Camera follow (watch mode) — transient, never serialized
        private const string WatchModeLockId = "ParsekWatch";
        private const ControlTypes WatchModeLockMask =
            ControlTypes.STAGING | ControlTypes.THROTTLE |
            ControlTypes.VESSEL_SWITCHING | ControlTypes.EVA_INPUT |
            ControlTypes.CAMERAMODES;
        int watchedRecordingIndex = -1;       // -1 = not watching
        string watchedRecordingId = null;     // stable across index shifts
        Vessel savedCameraVessel = null;
        float savedCameraDistance = 0f;
        float savedCameraPitch = 0f;
        float savedCameraHeading = 0f;
        double watchEndHoldUntilUT = -1;      // non-looped end hold timer

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = false;
        private static ToolbarControl toolbarControl;
        private ParsekUI ui;

        #endregion

        #region Public Accessors (for ParsekUI)

        public bool IsRecording => recorder?.IsRecording ?? false;
        public bool IsPlaying => isPlaying;
        public int TimelineGhostCount => ghostStates.Count;
        public GameObject PreviewGhost => ghostObject;
        public Dictionary<int, GameObject> TimelineGhosts
        {
            get
            {
                var result = new Dictionary<int, GameObject>();
                foreach (var kv in ghostStates)
                    result[kv.Key] = kv.Value.ghost;
                return result;
            }
        }
        public bool HasActiveChain => activeChainId != null;
        public bool HasActiveTree => activeTree != null;

        // Camera follow (watch mode)
        internal bool IsWatchingGhost => watchedRecordingIndex >= 0;
        internal int WatchedRecordingIndex => watchedRecordingIndex;

        #endregion

        #region Unity Lifecycle

        void Start()
        {
            Log("Parsek Flight loaded.");

            GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy);
            GameEvents.onVesselSituationChange.Add(OnVesselSituationChange);
            GameEvents.onCrewOnEva.Add(OnCrewOnEva);
            GameEvents.onCrewBoardVessel.Add(OnCrewBoardVessel);
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Add(OnVesselSOIChanged);
            GameEvents.onPartCouple.Add(OnPartCouple);
            GameEvents.onPartUndock.Add(OnPartUndock);
            GameEvents.onGroundSciencePartDeployed.Add(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Add(OnGroundSciencePartRemoved);
            GameEvents.onVesselChange.Add(OnVesselSwitchComplete);

            ui = new ParsekUI(this);

            // Clean up any orphaned toolbar from rapid scene transitions (e.g. rewind)
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
                toolbarControl = null;
            }

            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                () => { showUI = true; },
                () => { showUI = false; ui.CancelDeleteConfirm(); },
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
                MODID, "parsekButton",
                "Parsek/Textures/parsek_38",
                "Parsek/Textures/parsek_24",
                MODNAME
            );
        }

        void Update()
        {
            // Auto-clear stale boarding confirmation after 3 frames
            if (pendingBoardingTargetPid != 0)
            {
                boardingConfirmFrames++;
                if (boardingConfirmFrames > 3)
                {
                    ParsekLog.Info("Flight", $"Boarding confirmation expired (targetPid={pendingBoardingTargetPid})");
                    pendingBoardingTargetPid = 0;
                    boardingConfirmFrames = 0;
                }
            }

            // Auto-clear stale dock/undock confirmation after 5 frames
            if (pendingDockMergedPid != 0)
            {
                dockConfirmFrames++;
                if (dockConfirmFrames > 5)
                {
                    ParsekLog.Info("Flight", $"Dock confirmation expired (mergedPid={pendingDockMergedPid}, treeDock={pendingTreeDockMerge}, absorbedPid={pendingDockAbsorbedPid})");
                    if (pendingDockAbsorbedPid != 0)
                        dockingInProgress.Remove(pendingDockAbsorbedPid);
                    pendingDockMergedPid = 0;
                    pendingDockAsTarget = false;
                    pendingTreeDockMerge = false;
                    pendingDockAbsorbedPid = 0;
                    dockConfirmFrames = 0;
                }
            }
            if (pendingUndockOtherPid != 0)
            {
                undockConfirmFrames++;
                if (undockConfirmFrames > 5)
                {
                    ParsekLog.Info("Flight", $"Undock confirmation expired (otherPid={pendingUndockOtherPid})");
                    pendingUndockOtherPid = 0;
                    undockConfirmFrames = 0;
                }
            }

            // Tree mode: dock merge -- create merge branch point
            if (activeTree != null && pendingTreeDockMerge && recorder != null
                && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                double mergeUT = Planetarium.GetUniversalTime();
                if (recorder.CaptureAtStop.Points.Count > 0)
                    mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

                // Determine parent recording IDs
                string activeParentId = activeTree.ActiveRecordingId;
                string bgParentId = null;
                // The background partner is whichever vessel (absorbed OR merged) is in BackgroundMap.
                // When we're the TARGET: absorbed vessel is in BackgroundMap.
                // When we're the INITIATOR: merged (surviving) vessel is in BackgroundMap.
                if (pendingDockAbsorbedPid != 0)
                    activeTree.BackgroundMap.TryGetValue(pendingDockAbsorbedPid, out bgParentId);
                if (bgParentId == null && pendingDockMergedPid != 0)
                    activeTree.BackgroundMap.TryGetValue(pendingDockMergedPid, out bgParentId);

                var stoppedRecorder = recorder;
                recorder = null;

                CreateMergeBranch(
                    BranchPointType.Dock,
                    pendingDockMergedPid,
                    activeParentId,
                    bgParentId,
                    mergeUT,
                    stoppedRecorder);

                // Clean up
                if (pendingDockAbsorbedPid != 0)
                    dockingInProgress.Remove(pendingDockAbsorbedPid);

                pendingTreeDockMerge = false;
                pendingDockMergedPid = 0;
                pendingDockAbsorbedPid = 0;
                dockConfirmFrames = 0;
                pendingDockAsTarget = false;

                Log("Tree dock merge completed");
            }

            // Dock: initiator (pid changed, recorder already stopped by OnPhysicsFrame)
            if (recorder != null && recorder.DockMergePending &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                CommitDockUndockSegment(recorder, PartEventType.Docked, pendingDockMergedPid);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    // Pass undock continuation pid so OnPhysicsFrame can detect sibling switch
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after dock (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (docked)", 2f);
                }
                pendingDockMergedPid = 0;
                pendingDockAsTarget = false;
                dockConfirmFrames = 0;
            }

            // Dock: target (pid unchanged, recorder already stopped by OnPartCouple)
            if (pendingDockAsTarget && recorder != null &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                CommitDockUndockSegment(recorder, PartEventType.Docked, pendingDockMergedPid);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after dock as target (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (docked)", 2f);
                }
                pendingDockAsTarget = false;
                pendingDockMergedPid = 0;
                dockConfirmFrames = 0;
            }

            // Undock: player stays on remaining vessel (same pid, recorder stopped by StopRecordingForChainBoundary)
            if (pendingUndockOtherPid != 0 && recorder != null &&
                !recorder.IsRecording && recorder.CaptureAtStop != null &&
                !recorder.UndockSwitchPending)
            {
                CommitDockUndockSegment(recorder, PartEventType.Undocked, 0);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    StartUndockContinuation(pendingUndockOtherPid);
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after undock (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (undocked)", 2f);
                }
                pendingUndockOtherPid = 0;
                undockConfirmFrames = 0;
            }

            // Undock: player switched to undocked vessel (pid changed, recorder stopped by OnPhysicsFrame)
            if (recorder != null && recorder.UndockSwitchPending &&
                !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                uint oldPid = recorder.RecordingVesselId;
                CommitDockUndockSegment(recorder, PartEventType.Undocked, 0);
                recorder = null;

                // Stop existing undock continuation (we're switching roles)
                if (undockContinuationPid != 0)
                    StopUndockContinuation("sibling switch");

                StartRecording();
                if (IsRecording)
                {
                    // The "old" vessel (remaining) becomes continuation
                    StartUndockContinuation(oldPid);
                    recorder.UndockSiblingPid = undockContinuationPid;
                    Log($"Recording continues after undock switch (chain={activeChainId}, idx={activeChainNextIndex})");
                    ScreenMessage("Recording continues (undocked)", 2f);
                }
                pendingUndockOtherPid = 0;
                undockConfirmFrames = 0;
            }

            // Chain: auto-commit previous segment when recording stopped (EVA exit)
            // VesselSnapshot nulled in CommitChainSegment — mid-chain segments are ghost-only.
            if (pendingChainContinuation && !pendingChainIsBoarding &&
                recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                CommitChainSegment(recorder, pendingChainEvaName);
                recorder = null;
                // pendingAutoRecord is still true — will start child EVA recording next
            }

            // Tree mode: board merge -- create merge branch point
            if (activeTree != null && recorder != null && recorder.ChainToVesselPending
                && !recorder.IsRecording && recorder.CaptureAtStop != null
                && pendingBoardingTargetPid != 0
                && FlightGlobals.ActiveVessel != null
                && FlightGlobals.ActiveVessel.persistentId == pendingBoardingTargetPid)
            {
                double mergeUT = Planetarium.GetUniversalTime();
                if (recorder.CaptureAtStop.Points.Count > 0)
                    mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

                uint mergedPid = pendingBoardingTargetPid;

                string activeParentId = activeTree.ActiveRecordingId;
                string bgParentId = null;
                if (pendingBoardingTargetInTree)
                    activeTree.BackgroundMap.TryGetValue(mergedPid, out bgParentId);

                // Fix 5: clear ChainToVesselPending before CreateMergeBranch
                recorder.ChainToVesselPending = false;
                var stoppedRecorder = recorder;
                recorder = null;

                CreateMergeBranch(
                    BranchPointType.Board,
                    mergedPid,
                    activeParentId,
                    bgParentId,
                    mergeUT,
                    stoppedRecorder);

                pendingBoardingTargetPid = 0;
                boardingConfirmFrames = 0;
                pendingBoardingTargetInTree = false;
                activeChainCrewName = null;

                Log("Tree board merge completed");
            }

            // Chain: auto-commit EVA segment when boarding detected (EVA→vessel)
            if (recorder != null && recorder.ChainToVesselPending && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                // Only continue the chain if we're already in one AND the boarding event confirmed
                if (activeChainId != null &&
                    pendingBoardingTargetPid != 0 &&
                    FlightGlobals.ActiveVessel != null &&
                    FlightGlobals.ActiveVessel.persistentId == pendingBoardingTargetPid)
                {
                    Log($"Chain boarding confirmed: EVA→vessel pid={pendingBoardingTargetPid}");
                    pendingBoardingTargetPid = 0;
                    boardingConfirmFrames = 0;

                    CommitChainSegment(recorder, activeChainCrewName);
                    recorder = null;

                    // Start new vessel recording on the boarded vessel
                    activeChainCrewName = null; // vessel segment, not EVA
                    StartRecording();
                    if (IsRecording)
                    {
                        Log($"Chain vessel recording started (chain={activeChainId}, idx={activeChainNextIndex})");
                        ScreenMessage("Recording STARTED (boarded vessel)", 2f);
                    }
                }
                else
                {
                    // Not in a chain or boarding not confirmed — treat as normal stop
                    recorder.ChainToVesselPending = false;
                    pendingBoardingTargetPid = 0;
                    boardingConfirmFrames = 0;
                    Log("ChainToVessel without active chain or boarding confirmation — treating as normal stop");
                    ParsekLog.ScreenMessage("Recording stopped — vessel changed", 3f);
                    // Leave CaptureAtStop intact for normal revert/merge handling
                }
            }

            // Atmosphere boundary: auto-split when crossing atmosphere edge
            if (recorder != null && recorder.IsRecording && recorder.AtmosphereBoundaryCrossed &&
                ParsekSettings.Current?.autoSplitAtAtmosphere != false)
            {
                string phase = recorder.EnteredAtmosphere ? "exo" : "atmo";
                string newPhase = recorder.EnteredAtmosphere ? "atmo" : "exo";
                string bodyName = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                ParsekLog.Info("Flight", $"Atmosphere auto-split triggered: {bodyName} {phase}→{newPhase} " +
                    $"(chain={activeChainId ?? "(new)"}, points={recorder.Recording.Count})");
                recorder.StopRecordingForChainBoundary();
                CommitBoundarySplit(phase, bodyName);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    recorder.UndockSiblingPid = undockContinuationPid;
                    ParsekLog.Info("Flight", $"Recording continues after atmosphere boundary " +
                        $"(chain={activeChainId}, idx={activeChainNextIndex}, {bodyName} {phase}→{newPhase})");
                    ParsekLog.ScreenMessage($"Recording continues ({(newPhase == "atmo" ? "entering" : "exiting")} atmosphere)", 2f);
                }
            }

            // SOI change: auto-split when transitioning between celestial bodies
            if (recorder != null && recorder.IsRecording && recorder.SoiChangePending &&
                ParsekSettings.Current?.autoSplitAtSoi != false)
            {
                string fromBody = recorder.SoiChangeFromBody ?? "Unknown";
                string toBody = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                // Completed segment was at fromBody — tag as "exo" if it has atmosphere, "space" otherwise
                CelestialBody fromCB = FlightGlobals.Bodies?.Find(b => b.name == fromBody);
                string fromPhase = (fromCB != null && fromCB.atmosphere) ? "exo" : "space";
                ParsekLog.Info("Flight", $"SOI auto-split triggered: {fromBody} ({fromPhase}) → {toBody} " +
                    $"(chain={activeChainId ?? "(new)"}, points={recorder.Recording.Count})");
                recorder.StopRecordingForChainBoundary();
                CommitBoundarySplit(fromPhase, fromBody);
                recorder = null;
                StartRecording();
                if (IsRecording)
                {
                    recorder.UndockSiblingPid = undockContinuationPid;
                    ParsekLog.Info("Flight", $"Recording continues after SOI change " +
                        $"({fromBody} → {toBody}, chain={activeChainId}, idx={activeChainNextIndex})");
                    ParsekLog.ScreenMessage($"Recording continues (entering {toBody} SOI)", 2f);
                }
            }

            // Tree mode: flush backgrounded recorder after OnPhysicsFrame detected the switch
            if (activeTree != null && recorder != null && recorder.TransitionToBackgroundPending
                && recorder.IsBackgrounded)
            {
                FlushRecorderToTreeRecording(recorder, activeTree);

                // Add old vessel to BackgroundMap
                string oldRecId = activeTree.ActiveRecordingId;
                RecordingStore.Recording oldRec;
                uint bgPidUpdate = 0;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                    bgPidUpdate = oldRec.VesselPersistentId;
                }
                activeTree.ActiveRecordingId = null;
                recorder = null;
                if (bgPidUpdate != 0) backgroundRecorder?.OnVesselBackgrounded(bgPidUpdate);
                Log($"Tree: recorder flushed to background (recId={oldRecId})");
            }

            // Background recorder: update on-rails background vessels
            if (backgroundRecorder != null)
            {
                backgroundRecorder.UpdateOnRails(Planetarium.GetUniversalTime());
            }

            // Joint break deferred check: if FlightRecorder signaled a joint break,
            // stop recorder and start deferred vessel-creation check
            if (!pendingSplitInProgress && recorder != null && recorder.IsRecording
                && recorder.ConsumePendingJointBreakCheck())
            {
                // Snapshot existing vessel PIDs so the deferred check can identify NEW vessels
                preBreakVesselPids = new HashSet<uint>();
                if (FlightGlobals.Vessels != null)
                {
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v != null) preBreakVesselPids.Add(v.persistentId);
                    }
                }
                recorder.StopRecordingForChainBoundary();
                pendingSplitRecorder = recorder;
                recorder = null;
                pendingSplitInProgress = true;
                Log("Joint break detected — starting deferred vessel split check");
                StartCoroutine(DeferredJointBreakCheck());
            }

            // Skip auto-record and chain handlers while a split is being processed
            if (pendingSplitInProgress)
            {
                // Still run playback, input, and continuation sampling
                UpdateContinuationSampling();
                UpdateUndockContinuationSampling();
                HandleInput();
                if (isPlaying) UpdatePlayback();
                UpdateTimelinePlayback();
                return;
            }

            // Complete deferred auto-record for EVA (vessel switch may take a frame)
            if (pendingAutoRecord && !IsRecording &&
                FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.isEVA)
            {
                StartRecording();

                if (IsRecording)
                {
                    pendingAutoRecord = false;

                    if (pendingChainContinuation)
                    {
                        pendingChainContinuation = false;
                        pendingChainEvaName = null;
                        Log($"Chain EVA recording started (chain={activeChainId}, idx={activeChainNextIndex}, crew={activeChainCrewName})");
                        ScreenMessage("Recording STARTED (EVA chain)", 2f);
                    }
                    else
                    {
                        Log("Auto-record started (EVA from pad)");
                        ScreenMessage("Recording STARTED (auto — EVA from pad)", 2f);
                    }
                }
                // else: StartRecording failed (paused game or no active vessel).
                // Keep pending flags so we retry next frame; fall through to
                // HandleInput/playback so they aren't starved.
            }

            UpdateContinuationSampling();
            UpdateUndockContinuationSampling();

            HandleInput();

            if (isPlaying)
            {
                UpdatePlayback();
            }

            UpdateTimelinePlayback();
        }

        void OnGUI()
        {
            if (MapView.MapIsEnabled)
                ui.DrawMapMarkers();

            if (watchedRecordingIndex >= 0)
                DrawWatchModeOverlay();

            if (showUI)
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    GetInstanceID(),
                    windowRect,
                    ui.DrawWindow,
                    "Parsek",
                    GUILayout.Width(250)
                );
                ui.LogMainWindowPosition(windowRect);
                ui.DrawRecordingsWindowIfOpen(windowRect);
                ui.DrawActionsWindowIfOpen(windowRect);
                ui.DrawSettingsWindowIfOpen(windowRect);
            }
        }

        void OnDestroy()
        {
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
                toolbarControl = null;
            }

            // Unregister events to prevent handler leaks
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChangeRequested);
            GameEvents.onFlightReady.Remove(OnFlightReady);
            GameEvents.onVesselWillDestroy.Remove(OnVesselWillDestroy);
            GameEvents.onVesselSituationChange.Remove(OnVesselSituationChange);
            GameEvents.onCrewOnEva.Remove(OnCrewOnEva);
            GameEvents.onCrewBoardVessel.Remove(OnCrewBoardVessel);
            GameEvents.onVesselGoOnRails.Remove(OnVesselGoOnRails);
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
            GameEvents.onVesselSOIChanged.Remove(OnVesselSOIChanged);
            GameEvents.onPartCouple.Remove(OnPartCouple);
            GameEvents.onPartUndock.Remove(OnPartUndock);
            GameEvents.onGroundSciencePartDeployed.Remove(OnGroundSciencePartDeployed);
            GameEvents.onGroundSciencePartRemoved.Remove(OnGroundSciencePartRemoved);
            GameEvents.onVesselChange.Remove(OnVesselSwitchComplete);

            // Clean up background recorder if active
            if (backgroundRecorder != null)
            {
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Clean up recording if active
            if (IsRecording)
            {
                recorder.ForceStop();
            }
            StopPlayback();
            ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeLockId); // safety net
            DestroyAllTimelineGhosts();

            ui?.Cleanup();
        }

        #endregion

        #region Scene Change Handling

        void OnSceneChangeRequested(GameScenes scene)
        {
            Log($"Scene change requested: {scene}");

            // Exit watch mode on scene change
            if (watchedRecordingIndex >= 0)
                ParsekLog.Info("CameraFollow", "Watch mode cleared on scene change");
            ExitWatchMode();
            InputLockManager.RemoveControlLock(WatchModeLockId); // safety net

            // Finalize continuation sampling before anything else
            if (continuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                StopContinuation("scene change");
            }
            if (undockContinuationPid != 0)
            {
                RefreshUndockContinuationSnapshot();
                StopUndockContinuation("scene change");
            }

            // Clear dock/undock pending state
            pendingDockMergedPid = 0;
            pendingDockAsTarget = false;
            dockConfirmFrames = 0;
            pendingUndockOtherPid = 0;
            undockConfirmFrames = 0;

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingDockAbsorbedPid = 0;
            pendingBoardingTargetInTree = false;
            dockingInProgress.Clear();

            // Clear split event detection state
            lastBranchUT = -1;
            lastBranchVesselPids.Clear();
            pendingSplitInProgress = false;
            pendingSplitRecorder = null;
            preBreakVesselPids = null;

            // Tree mode: finalize and commit tree on scene exit.
            // Background recorder must be finalized BEFORE the tree commit so
            // its data is flushed to the tree recordings.
            if (activeTree != null)
            {
                double commitUT = Planetarium.GetUniversalTime();

                if (scene == GameScenes.FLIGHT)
                {
                    // Revert: preserve snapshots for merge dialog
                    CommitTreeRevert(commitUT);
                }
                else
                {
                    // Scene exit: null snapshots (ghost-only, no spawning outside Flight)
                    CommitTreeSceneExit(commitUT);
                }

                // Clean up tree state
                recorder = null;
                if (backgroundRecorder != null)
                {
                    backgroundRecorder.Shutdown();
                    Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                    backgroundRecorder = null;
                }
                activeTree = null;
            }

            // If we have recording data and are leaving flight, stash it
            if (recorder != null && recorder.Recording.Count > 0)
            {
                bool wasDestroyed = recorder.VesselDestroyedDuringRecording;

                // Stop recording if active
                if (recorder.IsRecording)
                    recorder.ForceStop();

                string vesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel";

                var captured = recorder.CaptureAtStop;
                RecordingStore.StashPending(
                    recorder.Recording,
                    vesselName,
                    recorder.OrbitSegments,
                    recordingId: captured != null ? captured.RecordingId : null,
                    recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                    ghostGeometryVersion: captured != null ? (int?)captured.GhostGeometryVersion : null,
                    partEvents: recorder.PartEvents);

                // Use stop-time atomic capture when available; fallback to scene-change capture.
                // ApplyPersistenceArtifactsFrom copies chain/ParentRecordingId/EvaCrewName from
                // CaptureAtStop on the normal path (StopRecording tagged them there).
                if (captured != null)
                {
                    var pending = RecordingStore.Pending;
                    pending.ApplyPersistenceArtifactsFrom(captured);
                    Log("Applied stop-time snapshot/geometry artifacts to pending recording");
                }
                else
                {
                    // Snapshot vessel for persistence across revert.
                    VesselSpawner.SnapshotVessel(
                        RecordingStore.Pending,
                        wasDestroyed,
                        destroyedFallbackSnapshot: recorder.InitialGhostVisualSnapshot ?? recorder.LastGoodVesselSnapshot);
                    RecordingStore.Pending.GhostVisualSnapshot = recorder.InitialGhostVisualSnapshot != null
                        ? recorder.InitialGhostVisualSnapshot.CreateCopy()
                        : (RecordingStore.Pending.VesselSnapshot != null
                            ? RecordingStore.Pending.VesselSnapshot.CreateCopy()
                            : null);

                    // Fallback path (ForceStop): CaptureAtStop is null, so
                    // ApplyPersistenceArtifactsFrom didn't run. Apply chain
                    // metadata directly from the active fields.
                    if (RecordingStore.HasPending && activeChainId != null)
                    {
                        RecordingStore.Pending.ChainId = activeChainId;
                        RecordingStore.Pending.ChainIndex = activeChainNextIndex;
                        RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
                        RecordingStore.Pending.EvaCrewName = activeChainCrewName;
                    }

                    // Copy rewind fields from recorder (ForceStop bypasses ApplyPersistenceArtifactsFrom)
                    if (RecordingStore.HasPending)
                    {
                        RecordingStore.Pending.RewindSaveFileName = recorder.RewindSaveFileName;
                        RecordingStore.Pending.RewindReservedFunds = recorder.RewindReservedFunds;
                        RecordingStore.Pending.RewindReservedScience = recorder.RewindReservedScience;
                        RecordingStore.Pending.RewindReservedRep = recorder.RewindReservedRep;
                    }

                    // Tag segment phase in fallback path
                    if (RecordingStore.HasPending && string.IsNullOrEmpty(RecordingStore.Pending.SegmentPhase))
                    {
                        var v = FlightGlobals.ActiveVessel;
                        if (v != null && v.mainBody != null)
                        {
                            RecordingStore.Pending.SegmentBodyName = v.mainBody.name;
                            if (v.mainBody.atmosphere)
                                RecordingStore.Pending.SegmentPhase = v.altitude < v.mainBody.atmosphereDepth ? "atmo" : "exo";
                            else
                                RecordingStore.Pending.SegmentPhase = "space";
                            ParsekLog.Verbose("Flight", $"Final segment tagged (ForceStop): " +
                                $"{RecordingStore.Pending.SegmentBodyName} {RecordingStore.Pending.SegmentPhase}");
                        }
                    }
                }

                // Clear chain fields after both paths have consumed them
                activeChainId = null;
                activeChainNextIndex = 0;
                activeChainPrevId = null;
                activeChainCrewName = null;

                // Capture vessel situation + PersistentId on the pending recording.
                // VesselPersistentId: safety net for the fallback path where
                // ApplyPersistenceArtifactsFrom didn't run.
                // SceneExitSituation + TerminalState: enables auto-commit by
                // ParsekScenario on non-revert scene exits.
                if (RecordingStore.HasPending)
                {
                    RecordingStore.Pending.VesselPersistentId = recorder.RecordingVesselId;

                    if (FlightGlobals.ActiveVessel != null)
                    {
                        var sit = FlightGlobals.ActiveVessel.situation;
                        RecordingStore.Pending.SceneExitSituation = (int)sit;
                        RecordingStore.Pending.TerminalStateValue =
                            RecordingTree.DetermineTerminalState((int)sit);
                    }
                }

                recorder = null;
                lastPlaybackIndex = 0;
            }

            // Stop manual playback
            StopPlayback();
            DestroyAllTimelineGhosts();
            GhostVisualBuilder.ClearCanopyCache();
            GhostVisualBuilder.ClearDeployableCache();
            GhostVisualBuilder.ClearFxPrefabCache();
            GhostVisualBuilder.ClearGearCache();
            GhostVisualBuilder.ClearLadderCache();
            GhostVisualBuilder.ClearCargoBayCache();
            GhostVisualBuilder.ClearAnimateHeatCache();
        }

        void OnVesselWillDestroy(Vessel v)
        {
            // Watch mode: null out saved camera vessel if it's being destroyed
            if (watchedRecordingIndex >= 0 && savedCameraVessel != null && v == savedCameraVessel)
                savedCameraVessel = null;

            // Watch mode: if the active vessel is destroyed while watching, exit watch mode.
            // Skip camera restore because ActiveVessel is the dying vessel — KSP will
            // assign a new active vessel and handle the camera itself.
            if (watchedRecordingIndex >= 0 && v == FlightGlobals.ActiveVessel)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Active vessel destroyed while watching ghost #{watchedRecordingIndex} \u2014 skipping camera restore (dying vessel), KSP will reassign");
                ExitWatchMode(skipCameraRestore: true);
            }

            recorder?.OnVesselWillDestroy(v);

            // Also propagate to pending split recorder — joint break may have already
            // moved recorder to pendingSplitRecorder (making recorder null)
            if (pendingSplitRecorder != null && v == FlightGlobals.ActiveVessel
                && !pendingSplitRecorder.VesselDestroyedDuringRecording)
            {
                pendingSplitRecorder.VesselDestroyedDuringRecording = true;
                ParsekLog.Info("Flight", "Vessel destroyed during pending split — flagged for commit");
            }

            // Skip background recorder cleanup for vessels being absorbed by docking.
            // The merge handler (CreateMergeBranch) will clean up via OnVesselRemovedFromBackground.
            if (!dockingInProgress.Contains(v.persistentId))
                backgroundRecorder?.OnBackgroundVesselWillDestroy(v);

            // Deferred destruction check for tree background vessels.
            // Captures vessel state now (Phase 1) and defers the actual destruction confirmation
            // by one frame (Phase 3) to distinguish real destruction from vessel unloading.
            if (activeTree != null && ShouldDeferDestructionCheck(v.persistentId, true,
                dockingInProgress, activeTree.BackgroundMap))
            {
                string recordingId = activeTree.BackgroundMap[v.persistentId];
                var pending = CaptureVesselStateForTerminal(v, recordingId);
                StartCoroutine(DeferredDestructionCheck(pending));
            }

            // Standalone mode: if the active recording vessel is destroyed, schedule a merge dialog.
            // In tree mode, the deferred destruction check handles this already.
            if (activeTree == null && recorder != null && recorder.IsRecording
                && recorder.VesselDestroyedDuringRecording && v == FlightGlobals.ActiveVessel)
            {
                StartCoroutine(ShowPostDestructionMergeDialog());
            }

            // If the continuation vessel is destroyed, mark the recording and stop tracking
            if (continuationVesselPid != 0 && v.persistentId == continuationVesselPid)
            {
                if (continuationRecordingIdx >= 0 &&
                    continuationRecordingIdx < RecordingStore.CommittedRecordings.Count)
                {
                    var rec = RecordingStore.CommittedRecordings[continuationRecordingIdx];
                    rec.VesselDestroyed = true;
                    rec.VesselSnapshot = null;
                    Log($"Continuation vessel destroyed (pid={continuationVesselPid})");
                }
                StopContinuation("vessel destroyed");
            }

            // If the undock continuation vessel is destroyed, stop tracking
            if (undockContinuationPid != 0 && v.persistentId == undockContinuationPid)
            {
                if (undockContinuationRecIdx >= 0 &&
                    undockContinuationRecIdx < RecordingStore.CommittedRecordings.Count)
                {
                    var rec = RecordingStore.CommittedRecordings[undockContinuationRecIdx];
                    rec.VesselDestroyed = true;
                    Log($"Undock continuation vessel destroyed (pid={undockContinuationPid})");
                }
                StopUndockContinuation("vessel destroyed");
            }
        }

        /// <summary>
        /// Coroutine that shows a merge dialog after a vessel is destroyed during standalone recording.
        /// Waits 2 seconds to avoid overlapping with mission results / flight log.
        /// </summary>
        IEnumerator ShowPostDestructionMergeDialog()
        {
            yield return new WaitForSeconds(2f);

            // Guard: only proceed if recorder still exists and vessel was destroyed
            if (recorder == null || !recorder.VesselDestroyedDuringRecording)
                yield break;

            // Guard: only for standalone recordings (tree mode has its own handling)
            if (activeTree != null)
                yield break;

            if (recorder.Recording.Count == 0)
                yield break;

            // Guard: if OnSceneChangeRequested already stashed a pending, don't double-stash
            if (RecordingStore.HasPending)
                yield break;

            Log("Post-destruction: stopping recorder and stashing pending recording");

            // Stop recording
            if (recorder.IsRecording)
                recorder.ForceStop();

            string vesselName = recorder.CaptureAtStop?.VesselName
                ?? (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : "Unknown Vessel");

            var captured = recorder.CaptureAtStop;
            RecordingStore.StashPending(
                recorder.Recording,
                vesselName,
                recorder.OrbitSegments,
                recordingId: captured != null ? captured.RecordingId : null,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                ghostGeometryVersion: captured != null ? (int?)captured.GhostGeometryVersion : null,
                partEvents: recorder.PartEvents);

            if (captured != null)
            {
                RecordingStore.Pending.ApplyPersistenceArtifactsFrom(captured);
            }
            else
            {
                VesselSpawner.SnapshotVessel(
                    RecordingStore.Pending,
                    true, // destroyed
                    destroyedFallbackSnapshot: recorder.InitialGhostVisualSnapshot ?? recorder.LastGoodVesselSnapshot);
                RecordingStore.Pending.GhostVisualSnapshot = recorder.InitialGhostVisualSnapshot != null
                    ? recorder.InitialGhostVisualSnapshot.CreateCopy()
                    : null;

                // Copy rewind fields from recorder (ForceStop bypasses ApplyPersistenceArtifactsFrom)
                RecordingStore.Pending.RewindSaveFileName = recorder.RewindSaveFileName;
                RecordingStore.Pending.RewindReservedFunds = recorder.RewindReservedFunds;
                RecordingStore.Pending.RewindReservedScience = recorder.RewindReservedScience;
                RecordingStore.Pending.RewindReservedRep = recorder.RewindReservedRep;
            }

            // Set terminal state
            if (RecordingStore.HasPending)
            {
                RecordingStore.Pending.VesselPersistentId = recorder.RecordingVesselId;
                RecordingStore.Pending.TerminalStateValue = TerminalState.Destroyed;
            }

            recorder = null;
            lastPlaybackIndex = 0;

            // Show merge dialog
            if (RecordingStore.HasPending)
            {
                Log("Post-destruction: showing merge dialog");
                MergeDialog.Show(RecordingStore.Pending);
            }
        }

        /// <summary>
        /// Primary detection mechanism for vessel switches in tree mode.
        /// Handles both transition (old vessel → background) AND promotion (new vessel → active).
        /// Fires regardless of on-rails state, unlike OnPhysicsFrame.
        /// </summary>
        void OnVesselSwitchComplete(Vessel newVessel)
        {
            // Watch mode: re-target camera to ghost — KSP reparents pivot on vessel switch.
            // Don't early-return: tree vessel-switch logic below must still run so that
            // programmatic switches (e.g. docking absorbs a vessel) properly transition
            // the recorder state and background mapping.
            if (watchedRecordingIndex >= 0)
            {
                GhostPlaybackState ws;
                if (ghostStates.TryGetValue(watchedRecordingIndex, out ws) && ws != null && ws.ghost != null)
                    FlightCamera.fetch.SetTargetTransform(ws.ghost.transform);
                ParsekLog.Verbose("CameraFollow",
                    $"onVesselChange fired while watching \u2014 re-targeting camera to ghost #{watchedRecordingIndex}");
            }

            if (activeTree == null) return;
            if (newVessel == null) return;

            // Fix 1 (CRITICAL): Don't interfere with pending merge processing.
            // Dock-induced vessel switches must not promote before Update() runs the merge handler.
            if (pendingTreeDockMerge) return;

            // Don't promote during pending board merge — the board handler in Update() owns this.
            if (recorder != null && recorder.ChainToVesselPending) return;

            // If the current recorder is still active (OnPhysicsFrame didn't catch the switch,
            // e.g. because isOnRails was true), transition it to background now
            if (recorder != null && recorder.IsRecording && !recorder.IsBackgrounded
                && recorder.RecordingVesselId != newVessel.persistentId)
            {
                recorder.TransitionToBackground();
                FlushRecorderToTreeRecording(recorder, activeTree);

                // Add old vessel to BackgroundMap
                string oldRecId = activeTree.ActiveRecordingId;
                RecordingStore.Recording oldRec;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                }
                activeTree.ActiveRecordingId = null;
                recorder.IsRecording = false;
                uint bgPid1 = recorder.RecordingVesselId;
                recorder = null;
                backgroundRecorder?.OnVesselBackgrounded(bgPid1);
                Log($"Tree: onVesselSwitchComplete transitioned old recorder to background");
            }
            // OnPhysicsFrame already backgrounded the recorder (TransitionToBackgroundPending),
            // but Update() hasn't flushed it yet. Flush now so promotion below can proceed
            // with the old recorder data committed and BackgroundMap up to date.
            else if (recorder != null && recorder.IsBackgrounded && recorder.TransitionToBackgroundPending)
            {
                FlushRecorderToTreeRecording(recorder, activeTree);

                string oldRecId = activeTree.ActiveRecordingId;
                RecordingStore.Recording oldRec;
                uint bgPid2 = 0;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                    bgPid2 = oldRec.VesselPersistentId;
                }
                activeTree.ActiveRecordingId = null;
                recorder = null;
                if (bgPid2 != 0) backgroundRecorder?.OnVesselBackgrounded(bgPid2);
                Log($"Tree: onVesselSwitchComplete flushed backgrounded recorder (recId={oldRecId})");
            }

            // Promote the new vessel if it's in the tree
            uint newPid = newVessel.persistentId;
            string backgroundRecordingId;
            if (activeTree.BackgroundMap.TryGetValue(newPid, out backgroundRecordingId))
            {
                backgroundRecorder?.OnVesselRemovedFromBackground(newPid);
                PromoteRecordingFromBackground(backgroundRecordingId, newVessel);
            }
        }

        /// <summary>
        /// Appends the current recorder's accumulated data (points, orbit segments, part events)
        /// to the tree's Recording object. Sorts part events by UT. Sets IsRecording = false
        /// to prevent dangling recorders. Populates VesselPersistentId on the tree Recording.
        /// </summary>
        void FlushRecorderToTreeRecording(FlightRecorder rec, RecordingTree tree)
        {
            if (rec == null || tree == null) return;

            string recId = tree.ActiveRecordingId;
            if (recId == null)
            {
                ParsekLog.Warn("Flight", "FlushRecorderToTreeRecording: no active recording id in tree");
                rec.IsRecording = false;
                return;
            }

            RecordingStore.Recording treeRec;
            if (!tree.Recordings.TryGetValue(recId, out treeRec))
            {
                ParsekLog.Warn("Flight", $"FlushRecorderToTreeRecording: recording id '{recId}' not found in tree");
                rec.IsRecording = false;
                return;
            }

            // Close any open orbit segment (e.g. background phase) before copying data
            rec.FinalizeOpenOrbitSegment();

            // Append trajectory data
            treeRec.Points.AddRange(rec.Recording);
            treeRec.OrbitSegments.AddRange(rec.OrbitSegments);
            treeRec.PartEvents.AddRange(rec.PartEvents);

            // Sort part events chronologically (mixed event sources may produce non-chronological order)
            treeRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));

            // Populate VesselPersistentId (required for RebuildBackgroundMap after save/load)
            if (treeRec.VesselPersistentId == 0 && rec.RecordingVesselId != 0)
                treeRec.VesselPersistentId = rec.RecordingVesselId;

            // Set IsRecording = false to prevent dangling active state
            rec.IsRecording = false;
            rec.TransitionToBackgroundPending = false;

            ParsekLog.Info("Flight", $"Flushed recorder to tree recording '{recId}': " +
                $"{rec.Recording.Count} points, {rec.OrbitSegments.Count} orbit segments, " +
                $"{rec.PartEvents.Count} part events");
        }

        /// <summary>
        /// Creates a new FlightRecorder for the promoted vessel, starts recording with
        /// isPromotion=true, updates activeTree state.
        /// </summary>
        void PromoteRecordingFromBackground(string backgroundRecordingId, Vessel newVessel)
        {
            if (activeTree == null || newVessel == null) return;

            // Remove from BackgroundMap
            activeTree.BackgroundMap.Remove(newVessel.persistentId);

            // Set as the active recording
            activeTree.ActiveRecordingId = backgroundRecordingId;

            // Create a new recorder and start recording with isPromotion
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            if (pendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = pendingBoundaryAnchor;
                pendingBoundaryAnchor = null;
            }
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"PromoteRecordingFromBackground: StartRecording failed for pid={newVessel.persistentId}");
                recorder = null;
                activeTree.ActiveRecordingId = null;
                // Put it back in the background map
                activeTree.BackgroundMap[newVessel.persistentId] = backgroundRecordingId;
                return;
            }

            ParsekLog.Info("Flight", $"Promoted recording '{backgroundRecordingId}' from background " +
                $"(pid={newVessel.persistentId})");
        }

        #region Split Event Detection (Tree Branching)

        /// <summary>
        /// Checks whether a vessel type alone qualifies as trackable.
        /// SpaceObject (asteroids, comets) are always trackable.
        /// Other types require a module check (see IsTrackableVessel).
        /// </summary>
        internal static bool IsTrackableVesselType(VesselType vesselType)
        {
            return vesselType == VesselType.SpaceObject;
        }

        /// <summary>
        /// Determines whether a vessel is trackable (should get its own tree branch).
        /// Trackable = SpaceObject OR has ModuleCommand (covers crewed pods and probe cores).
        /// Debris, spent boosters, fairings, etc. are NOT trackable.
        /// </summary>
        internal static bool IsTrackableVessel(Vessel v)
        {
            if (v == null) return false;

            // Space objects (asteroids, comets) are always trackable
            if (v.vesselType == VesselType.SpaceObject) return true;

            // Check for command capability (ModuleCommand covers both crewed pods and probe cores)
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p.FindModuleImplementing<ModuleCommand>() != null) return true;
            }
            return false;
        }

        /// <summary>
        /// Pure data-model method: creates the BranchPoint and two child Recording objects
        /// for a vessel split. Testable without Unity.
        /// For EVA branches, evaVesselPid identifies which child is the kerbal (receives
        /// EvaCrewName and ParentRecordingId). If 0, defaults to activeVesselPid.
        /// </summary>
        internal static (BranchPoint bp, RecordingStore.Recording activeChild, RecordingStore.Recording backgroundChild)
            BuildSplitBranchData(
                string parentRecordingId, string treeId, double branchUT,
                BranchPointType branchType, uint activeVesselPid, string activeVesselName,
                uint backgroundVesselPid, string backgroundVesselName,
                string evaCrewName = null, uint evaVesselPid = 0)
        {
            string activeChildId = Guid.NewGuid().ToString("N");
            string backgroundChildId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = branchUT,
                Type = branchType,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string> { activeChildId, backgroundChildId }
            };

            var activeChild = new RecordingStore.Recording
            {
                RecordingId = activeChildId,
                TreeId = treeId,
                VesselPersistentId = activeVesselPid,
                VesselName = activeVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT
            };

            var backgroundChild = new RecordingStore.Recording
            {
                RecordingId = backgroundChildId,
                TreeId = treeId,
                VesselPersistentId = backgroundVesselPid,
                VesselName = backgroundVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = branchUT
            };

            // For EVA: kerbal child gets EvaCrewName and ParentRecordingId.
            // Identify kerbal by PID match (handles both active and background kerbal cases).
            if (branchType == BranchPointType.EVA && !string.IsNullOrEmpty(evaCrewName))
            {
                uint kerbalPid = evaVesselPid != 0 ? evaVesselPid : activeVesselPid;
                if (kerbalPid == activeVesselPid)
                {
                    activeChild.EvaCrewName = evaCrewName;
                    activeChild.ParentRecordingId = backgroundChildId;
                }
                else
                {
                    backgroundChild.EvaCrewName = evaCrewName;
                    backgroundChild.ParentRecordingId = activeChildId;
                }
            }

            return (bp, activeChild, backgroundChild);
        }

        /// <summary>
        /// Pure data-model method: creates the BranchPoint and one child Recording object
        /// for a vessel merge (dock or board). Testable without Unity.
        /// Takes a list of parent recording IDs (1 for foreign vessel merge, 2 for two-tree merge)
        /// and returns a single child recording for the merged vessel.
        /// </summary>
        internal static (BranchPoint bp, RecordingStore.Recording mergedChild)
            BuildMergeBranchData(
                List<string> parentRecordingIds, string treeId, double mergeUT,
                BranchPointType branchType, uint mergedVesselPid, string mergedVesselName)
        {
            string childId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = mergeUT,
                Type = branchType,
                ParentRecordingIds = new List<string>(parentRecordingIds),
                ChildRecordingIds = new List<string> { childId }
            };

            var mergedChild = new RecordingStore.Recording
            {
                RecordingId = childId,
                TreeId = treeId,
                VesselPersistentId = mergedVesselPid,
                VesselName = mergedVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = mergeUT
            };

            return (bp, mergedChild);
        }

        /// <summary>
        /// Creates a tree branch for a vessel split event.
        /// If no tree exists yet, wraps the current recording as the root node.
        /// Creates two child recordings (active + background), wires up BackgroundRecorder,
        /// and starts a new FlightRecorder for the active child.
        /// </summary>
        void CreateSplitBranch(BranchPointType branchType, Vessel activeVessel, Vessel backgroundVessel,
            double branchUT, string evaCrewName = null, uint evaVesselPid = 0)
        {
            if (pendingSplitRecorder == null)
            {
                ParsekLog.Warn("Flight", "CreateSplitBranch: no pending split recorder — aborting");
                return;
            }

            var splitRecorder = pendingSplitRecorder;

            // Determine parent recording ID
            string parentRecordingId;

            // First split: create the tree and wrap current recording as root
            if (activeTree == null)
            {
                string treeId = Guid.NewGuid().ToString("N");
                string rootRecId = Guid.NewGuid().ToString("N");

                activeTree = new RecordingTree
                {
                    Id = treeId,
                    TreeName = splitRecorder.CaptureAtStop?.VesselName
                               ?? activeVessel?.vesselName ?? "Unknown",
                    RootRecordingId = rootRecId,
                    ActiveRecordingId = null // will be set below
                };

                // Capture pre-tree resources from the recorder
                activeTree.PreTreeFunds = splitRecorder.PreLaunchFunds;
                activeTree.PreTreeScience = splitRecorder.PreLaunchScience;
                activeTree.PreTreeReputation = splitRecorder.PreLaunchReputation;

                // Create root recording from captured data
                var rootRec = new RecordingStore.Recording
                {
                    RecordingId = rootRecId,
                    TreeId = treeId,
                    VesselPersistentId = splitRecorder.RecordingVesselId,
                    VesselName = splitRecorder.CaptureAtStop?.VesselName ?? "Unknown",
                    ExplicitEndUT = branchUT
                };

                // Copy captured data into root recording
                if (splitRecorder.CaptureAtStop != null)
                {
                    rootRec.Points = new List<TrajectoryPoint>(splitRecorder.CaptureAtStop.Points);
                    rootRec.OrbitSegments = new List<OrbitSegment>(splitRecorder.CaptureAtStop.OrbitSegments);
                    rootRec.PartEvents = new List<PartEvent>(splitRecorder.CaptureAtStop.PartEvents);
                    rootRec.GhostVisualSnapshot = splitRecorder.CaptureAtStop.GhostVisualSnapshot;
                    rootRec.VesselSnapshot = splitRecorder.CaptureAtStop.VesselSnapshot;
                    if (rootRec.Points.Count > 0)
                        rootRec.ExplicitStartUT = rootRec.Points[0].ut;
                }

                activeTree.Recordings[rootRecId] = rootRec;
                parentRecordingId = rootRecId;

                // Create BackgroundRecorder
                backgroundRecorder = new BackgroundRecorder(activeTree);
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;

                ParsekLog.Info("Flight", $"Tree created: id={treeId}, root={rootRecId}, " +
                    $"vesselPid={splitRecorder.RecordingVesselId}");
            }
            else
            {
                // Subsequent split: use the current active recording as parent
                parentRecordingId = activeTree.ActiveRecordingId;

                if (parentRecordingId == null)
                {
                    ParsekLog.Warn("Flight", "CreateSplitBranch: no active recording in existing tree — aborting");
                    FallbackCommitSplitRecorder(splitRecorder);
                    return;
                }

                // Flush captured data into the existing tree recording
                RecordingStore.Recording parentRec;
                if (activeTree.Recordings.TryGetValue(parentRecordingId, out parentRec))
                {
                    if (splitRecorder.CaptureAtStop != null)
                    {
                        parentRec.Points.AddRange(splitRecorder.CaptureAtStop.Points);
                        parentRec.OrbitSegments.AddRange(splitRecorder.CaptureAtStop.OrbitSegments);
                        parentRec.PartEvents.AddRange(splitRecorder.CaptureAtStop.PartEvents);
                        parentRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
                    }
                    parentRec.ExplicitEndUT = branchUT;
                }
            }

            // Set ChildBranchPointId on parent
            RecordingStore.Recording parentRecording;
            if (activeTree.Recordings.TryGetValue(parentRecordingId, out parentRecording))
            {
                // Will be set after we know the BP id
            }

            // Take snapshots for both child vessels
            ConfigNode activeSnapshot = VesselSpawner.TryBackupSnapshot(activeVessel);
            ConfigNode bgSnapshot = VesselSpawner.TryBackupSnapshot(backgroundVessel);

            // Build the branch data
            var (bp, activeChild, bgChild) = BuildSplitBranchData(
                parentRecordingId, activeTree.Id, branchUT, branchType,
                activeVessel != null ? activeVessel.persistentId : 0,
                activeVessel != null ? activeVessel.vesselName : "Unknown",
                backgroundVessel != null ? backgroundVessel.persistentId : 0,
                backgroundVessel != null ? backgroundVessel.vesselName : "Unknown",
                evaCrewName, evaVesselPid);

            // Set snapshots on child recordings
            activeChild.GhostVisualSnapshot = activeSnapshot;
            activeChild.VesselSnapshot = activeSnapshot != null ? activeSnapshot.CreateCopy() : null;
            bgChild.GhostVisualSnapshot = bgSnapshot;
            bgChild.VesselSnapshot = bgSnapshot != null ? bgSnapshot.CreateCopy() : null;

            // Set ChildBranchPointId on parent recording
            if (parentRecording != null)
                parentRecording.ChildBranchPointId = bp.Id;

            // Add to tree
            activeTree.BranchPoints.Add(bp);
            activeTree.Recordings[activeChild.RecordingId] = activeChild;
            activeTree.Recordings[bgChild.RecordingId] = bgChild;

            // Set active recording
            activeTree.ActiveRecordingId = activeChild.RecordingId;

            // Add background child to BackgroundMap FIRST, then notify BackgroundRecorder
            if (backgroundVessel != null && backgroundVessel.persistentId != 0)
            {
                activeTree.BackgroundMap[backgroundVessel.persistentId] = bgChild.RecordingId;
                backgroundRecorder?.OnVesselBackgrounded(backgroundVessel.persistentId);
            }

            // Stop any existing undock continuation and vessel continuation (tree handles them)
            if (undockContinuationPid != 0)
                StopUndockContinuation("tree branch");
            if (continuationVesselPid != 0)
                StopContinuation("tree branch");

            // Create new FlightRecorder for active child
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);
            recorder.UndockSiblingPid = 0; // tree handles vessel tracking

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"CreateSplitBranch: StartRecording failed for active child");
                recorder = null;
            }

            ParsekLog.Info("Flight", $"Tree branch created: type={branchType}, " +
                $"bp={bp.Id}, activeChild={activeChild.RecordingId} (pid={activeChild.VesselPersistentId}), " +
                $"bgChild={bgChild.RecordingId} (pid={bgChild.VesselPersistentId})" +
                (evaCrewName != null ? $", evaCrew={evaCrewName}" : ""));
        }

        /// <summary>
        /// When we are the dock target (our PID unchanged), finds the PID of the vessel
        /// that was absorbed into us. Scans BackgroundMap for a vessel that is no longer
        /// a separate entity (its Vessel object is gone or merged into ours).
        /// Returns 0 if the partner is not in the tree (foreign vessel).
        /// </summary>
        uint FindAbsorbedDockPartnerPid(uint mergedPid, Vessel mergedVessel)
        {
            if (activeTree == null) return 0;

            foreach (var kvp in activeTree.BackgroundMap)
            {
                uint bgPid = kvp.Key;
                if (bgPid == mergedPid) continue; // skip ourselves

                Vessel bgVessel = FlightRecorder.FindVesselByPid(bgPid);

                // After coupling, the absorbed vessel either:
                // (a) has its Vessel object destroyed (null)
                // (b) has its Vessel reference pointing to the merged vessel (reparented parts)
                // (c) still exists but with 0 parts (about to be destroyed)
                if (bgVessel == null || bgVessel == mergedVessel
                    || bgVessel.parts == null || bgVessel.parts.Count == 0)
                {
                    return bgPid;
                }
            }

            // No background vessel was absorbed -- partner is a foreign vessel
            return 0;
        }

        /// <summary>
        /// Creates a tree merge branch point for a dock or board event.
        /// Ends parent recordings, creates child recording for the merged vessel,
        /// and starts a new FlightRecorder for the child.
        /// Per Orchestrator Fix 3: absorbedVesselPid is NOT a parameter --
        /// background vessel PID is derived from the background parent recording.
        /// </summary>
        void CreateMergeBranch(
            BranchPointType branchType,
            uint mergedVesselPid,
            string activeParentRecordingId,      // from the active recorder (always present)
            string backgroundParentRecordingId,  // from BackgroundMap lookup (null for foreign vessel)
            double mergeUT,
            FlightRecorder stoppedRecorder)
        {
            // 1. Build parent recording ID list
            var parentIds = new List<string>();
            parentIds.Add(activeParentRecordingId);
            if (backgroundParentRecordingId != null)
                parentIds.Add(backgroundParentRecordingId);

            // 2. Flush captured data from stopped recorder into the active parent recording
            RecordingStore.Recording activeParentRec = null;
            if (activeTree.Recordings.TryGetValue(activeParentRecordingId, out activeParentRec))
            {
                if (stoppedRecorder.CaptureAtStop != null)
                {
                    activeParentRec.Points.AddRange(stoppedRecorder.CaptureAtStop.Points);
                    activeParentRec.OrbitSegments.AddRange(stoppedRecorder.CaptureAtStop.OrbitSegments);
                    activeParentRec.PartEvents.AddRange(stoppedRecorder.CaptureAtStop.PartEvents);
                    activeParentRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
                }
                activeParentRec.ExplicitEndUT = mergeUT;
                // Set terminal state based on merge type
                activeParentRec.TerminalStateValue = (branchType == BranchPointType.Dock)
                    ? TerminalState.Docked : TerminalState.Boarded;
            }

            // 3. End background parent recording (if two-parent merge)
            // Per Fix 3: derive background vessel PID from the recording's VesselPersistentId
            if (backgroundParentRecordingId != null)
            {
                RecordingStore.Recording bgParentRec;
                if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec))
                {
                    bgParentRec.ExplicitEndUT = mergeUT;
                    bgParentRec.TerminalStateValue = (branchType == BranchPointType.Dock)
                        ? TerminalState.Docked : TerminalState.Boarded;

                    // Remove background vessel from BackgroundMap and notify BackgroundRecorder
                    uint bgVesselPid = bgParentRec.VesselPersistentId;
                    backgroundRecorder?.OnVesselRemovedFromBackground(bgVesselPid);
                    activeTree.BackgroundMap.Remove(bgVesselPid);
                }
            }

            // 4. Get merged vessel name
            Vessel mergedVessel = FlightRecorder.FindVesselByPid(mergedVesselPid);
            string mergedVesselName = mergedVessel?.vesselName ?? "Merged Vessel";

            // 5. Build merge branch data
            var (bp, mergedChild) = BuildMergeBranchData(
                parentIds, activeTree.Id, mergeUT, branchType,
                mergedVesselPid, mergedVesselName);

            // 6. Take snapshot of merged vessel
            ConfigNode mergedSnapshot = (mergedVessel != null)
                ? VesselSpawner.TryBackupSnapshot(mergedVessel) : null;
            mergedChild.GhostVisualSnapshot = mergedSnapshot;
            mergedChild.VesselSnapshot = mergedSnapshot != null ? mergedSnapshot.CreateCopy() : null;

            // 7. Set ChildBranchPointId on all parent recordings
            if (activeParentRec != null)
                activeParentRec.ChildBranchPointId = bp.Id;
            if (backgroundParentRecordingId != null)
            {
                RecordingStore.Recording bgParentRec2;
                if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec2))
                    bgParentRec2.ChildBranchPointId = bp.Id;
            }

            // 8. Add to tree
            activeTree.BranchPoints.Add(bp);
            activeTree.Recordings[mergedChild.RecordingId] = mergedChild;

            // 9. Set active recording
            activeTree.ActiveRecordingId = mergedChild.RecordingId;

            // 10. Start new FlightRecorder for merged child
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", "CreateMergeBranch: StartRecording failed for merged child");
                recorder = null;
            }

            // 11. Log
            ParsekLog.Info("Flight", $"Tree merge created: type={branchType}, " +
                $"bp={bp.Id}, parents=[{string.Join(",", parentIds)}], " +
                $"child={mergedChild.RecordingId} (pid={mergedVesselPid})");
        }

        /// <summary>
        /// Fallback: if tree creation fails, commit captured data as a standalone recording.
        /// </summary>
        void FallbackCommitSplitRecorder(FlightRecorder splitRec)
        {
            if (splitRec?.CaptureAtStop == null) return;

            var captured = splitRec.CaptureAtStop;
            if (captured.Points.Count >= 2)
            {
                RecordingStore.StashPending(
                    captured.Points, captured.VesselName,
                    orbitSegments: captured.OrbitSegments,
                    partEvents: captured.PartEvents);
                // Copy snapshot/vessel state to the pending recording
                if (RecordingStore.HasPending)
                {
                    var pending = RecordingStore.Pending;
                    pending.VesselSnapshot = captured.VesselSnapshot;
                    pending.GhostVisualSnapshot = captured.GhostVisualSnapshot;
                    pending.VesselDestroyed = captured.VesselDestroyed;
                    pending.VesselSituation = captured.VesselSituation;
                    pending.DistanceFromLaunch = captured.DistanceFromLaunch;
                    pending.MaxDistanceFromLaunch = captured.MaxDistanceFromLaunch;
                    pending.PreLaunchFunds = captured.PreLaunchFunds;
                    pending.PreLaunchScience = captured.PreLaunchScience;
                    pending.PreLaunchReputation = captured.PreLaunchReputation;
                    pending.RewindSaveFileName = captured.RewindSaveFileName;
                    pending.RewindReservedFunds = captured.RewindReservedFunds;
                    pending.RewindReservedScience = captured.RewindReservedScience;
                    pending.RewindReservedRep = captured.RewindReservedRep;
                }
                RecordingStore.CommitPending();
                ParsekLog.Warn("Flight", "Split branch failed — fallback committed recording as standalone");
            }
            else
            {
                ParsekLog.Warn("Flight", "Split branch failed — recording too short to commit, data lost");
            }
        }

        /// <summary>
        /// Resumes recording after a false-alarm split detection (debris undock, joint break
        /// that didn't produce a trackable vessel, deduplication skip). Re-enables the
        /// stopped recorder and restores it as the active recorder.
        /// </summary>
        void ResumeSplitRecorder(FlightRecorder splitRec, string reason)
        {
            if (splitRec == null)
            {
                ParsekLog.Warn("Flight", $"ResumeSplitRecorder: no recorder to resume ({reason})");
                return;
            }

            // Don't resume on a destroyed vessel — fall back to commit
            if (splitRec.VesselDestroyedDuringRecording)
            {
                ParsekLog.Info("Flight", $"ResumeSplitRecorder: vessel was destroyed ({reason}) — committing instead of resuming");
                FallbackCommitSplitRecorder(splitRec);
                return;
            }

            splitRec.ResumeAfterFalseAlarm();

            if (splitRec.IsRecording)
            {
                recorder = splitRec;
                // Restore tree mode if tree is active
                if (activeTree != null)
                    recorder.ActiveTree = activeTree;
                ParsekLog.Info("Flight", $"Recording resumed after false alarm: {reason}");
            }
            else
            {
                // ResumeAfterFalseAlarm failed (no active vessel?) — fall back to commit
                ParsekLog.Warn("Flight", $"ResumeSplitRecorder: resume failed ({reason}) — falling back to commit");
                FallbackCommitSplitRecorder(splitRec);
            }
        }

        /// <summary>
        /// Checks the deduplication guard. Returns true if this branch should proceed.
        /// </summary>
        bool CheckBranchDeduplication(double branchUT, uint newVesselPid)
        {
            if (Math.Abs(branchUT - lastBranchUT) > 0.1)
            {
                lastBranchVesselPids.Clear();
                lastBranchUT = branchUT;
            }
            if (lastBranchVesselPids.Contains(newVesselPid))
            {
                ParsekLog.Verbose("Flight", $"Skipping duplicate branch for pid={newVesselPid} at same UT");
                return false;
            }
            lastBranchVesselPids.Add(newVesselPid);
            return true;
        }

        IEnumerator DeferredUndockBranch(uint newVesselPid)
        {
            yield return null; // defer one frame for KSP to finalize the split

            try
            {
                if (FlightGlobals.ActiveVessel == null || pendingSplitRecorder == null)
                {
                    ParsekLog.Warn("Flight", "DeferredUndockBranch: invalid state after deferral — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();

                // Deduplication check
                if (!CheckBranchDeduplication(branchUT, newVesselPid))
                {
                    ResumeSplitRecorder(pendingSplitRecorder, "undock dedup skip");
                    yield break;
                }

                // Find the new vessel
                Vessel newVessel = FlightRecorder.FindVesselByPid(newVesselPid);
                if (newVessel == null)
                {
                    ParsekLog.Verbose("Flight", $"DeferredUndockBranch: vessel pid={newVesselPid} not found — debris destroyed, resuming recording");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel not found");
                    yield break;
                }

                // Debris filter
                if (!IsTrackableVessel(newVessel))
                {
                    ParsekLog.Verbose("Flight", $"DeferredUndockBranch: vessel pid={newVesselPid} is not trackable (debris) — resuming recording");
                    ResumeSplitRecorder(pendingSplitRecorder, "undocked vessel is debris");
                    yield break;
                }

                // Player stays on remaining vessel (active), undocked vessel goes to background
                Vessel activeVessel = FlightGlobals.ActiveVessel;
                CreateSplitBranch(BranchPointType.Undock, activeVessel, newVessel, branchUT);
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
            }
        }

        IEnumerator DeferredEvaBranch(string kerbalName, GameEvents.FromToAction<Part, Part> evaData)
        {
            yield return null; // defer one frame for vessel switch to complete

            try
            {
                if (FlightGlobals.ActiveVessel == null || pendingSplitRecorder == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranch: invalid state after deferral — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();

                // Get the EVA kerbal vessel and the ship vessel
                Vessel evaVessel = evaData.to?.vessel;
                Vessel shipVessel = evaData.from?.vessel;

                if (evaVessel == null)
                {
                    ParsekLog.Warn("Flight", "DeferredEvaBranch: EVA vessel is null — aborting");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                uint evaPid = evaVessel.persistentId;
                if (!CheckBranchDeduplication(branchUT, evaPid))
                {
                    ResumeSplitRecorder(pendingSplitRecorder, "EVA dedup skip");
                    yield break;
                }

                // Determine which vessel is active by checking FlightGlobals.ActiveVessel
                Vessel currentActive = FlightGlobals.ActiveVessel;
                Vessel activeChild;
                Vessel backgroundChild;

                if (currentActive != null && currentActive.persistentId == evaPid)
                {
                    // KSP switched to the EVA kerbal (expected)
                    activeChild = evaVessel;
                    backgroundChild = shipVessel;
                }
                else if (currentActive != null && shipVessel != null &&
                         currentActive.persistentId == shipVessel.persistentId)
                {
                    // KSP hasn't switched yet — ship is still active
                    activeChild = shipVessel;
                    backgroundChild = evaVessel;
                    // Note: tree vessel switch handling will sort out promotion when KSP switches
                }
                else
                {
                    // Ambiguous — default to EVA kerbal as active (most common case)
                    activeChild = evaVessel;
                    backgroundChild = shipVessel;
                }

                CreateSplitBranch(BranchPointType.EVA, activeChild, backgroundChild, branchUT, kerbalName, evaPid);
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
            }
        }

        IEnumerator DeferredJointBreakCheck()
        {
            yield return null; // defer one frame for vessel split to finalize

            try
            {
                if (FlightGlobals.ActiveVessel == null)
                {
                    ParsekLog.Verbose("Flight", "DeferredJointBreakCheck: no active vessel after deferral");
                    FallbackCommitSplitRecorder(pendingSplitRecorder);
                    yield break;
                }

                // If there's no pendingSplitRecorder, the recorder was re-established already
                if (pendingSplitRecorder == null)
                {
                    ParsekLog.Verbose("Flight", "DeferredJointBreakCheck: no pending split recorder");
                    yield break;
                }

                double branchUT = Planetarium.GetUniversalTime();

                // Scan for NEW vessels created by the joint break.
                // Only consider vessels whose PID was NOT in the pre-break snapshot,
                // to avoid false matches with unrelated vessels (e.g., space stations).
                uint recordedPid = pendingSplitRecorder.RecordingVesselId;
                bool foundTrackable = false;

                if (FlightGlobals.Vessels != null)
                {
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v == null || v.persistentId == recordedPid) continue;

                        // Only consider vessels that didn't exist before the break
                        if (preBreakVesselPids != null && preBreakVesselPids.Contains(v.persistentId))
                            continue;

                        // Skip vessels we're already tracking in the tree
                        if (activeTree != null && activeTree.BackgroundMap.ContainsKey(v.persistentId))
                            continue;

                        // Skip if not trackable
                        if (!IsTrackableVessel(v)) continue;

                        // Skip if already branched at this UT
                        if (!CheckBranchDeduplication(branchUT, v.persistentId))
                            continue;

                        // Found a trackable vessel — create branch
                        Vessel activeVessel = FlightGlobals.ActiveVessel;
                        CreateSplitBranch(BranchPointType.JointBreak, activeVessel, v, branchUT);
                        foundTrackable = true;
                        break; // one branch per coroutine invocation
                    }
                }

                if (!foundTrackable)
                {
                    // No trackable vessel split (debris only) — resume recording
                    ResumeSplitRecorder(pendingSplitRecorder, "joint break produced no trackable vessel");
                }
            }
            finally
            {
                pendingSplitInProgress = false;
                pendingSplitRecorder = null;
                preBreakVesselPids = null;
            }
        }

        #endregion

        #region Terminal Event Detection (Destruction)

        /// <summary>
        /// Pure decision method: determines whether a deferred destruction check should be started.
        /// Returns true if the vessel is in the BackgroundMap, not in dockingInProgress, and a tree exists.
        /// </summary>
        internal static bool ShouldDeferDestructionCheck(
            uint vesselPid,
            bool hasTree,
            HashSet<uint> dockingInProgress,
            Dictionary<uint, string> backgroundMap)
        {
            if (!hasTree) return false;
            if (dockingInProgress.Contains(vesselPid)) return false;
            return backgroundMap.ContainsKey(vesselPid);
        }

        /// <summary>
        /// Pure decision method: determines whether a vessel is truly destroyed (not just unloaded
        /// or absorbed by docking) after the one-frame deferral.
        /// </summary>
        internal static bool IsTrulyDestroyed(
            uint vesselPid,
            HashSet<uint> dockingInProgress,
            bool vesselStillExists)
        {
            if (dockingInProgress.Contains(vesselPid)) return false;
            return !vesselStillExists;
        }

        /// <summary>
        /// Pure static method: applies terminal destruction state to a recording.
        /// Sets TerminalStateValue = Destroyed, ExplicitEndUT, and copies orbital/surface data.
        /// </summary>
        internal static void ApplyTerminalDestruction(
            PendingDestruction pending,
            RecordingStore.Recording rec)
        {
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.ExplicitEndUT = pending.capturedUT;
            ApplyTerminalData(pending, rec);
        }

        /// <summary>
        /// Pure static helper: copies orbital/surface data from a PendingDestruction to a recording.
        /// </summary>
        internal static void ApplyTerminalData(
            PendingDestruction data,
            RecordingStore.Recording rec)
        {
            if (data.hasOrbit)
            {
                rec.TerminalOrbitInclination = data.inclination;
                rec.TerminalOrbitEccentricity = data.eccentricity;
                rec.TerminalOrbitSemiMajorAxis = data.semiMajorAxis;
                rec.TerminalOrbitLAN = data.lan;
                rec.TerminalOrbitArgumentOfPeriapsis = data.argumentOfPeriapsis;
                rec.TerminalOrbitMeanAnomalyAtEpoch = data.meanAnomalyAtEpoch;
                rec.TerminalOrbitEpoch = data.epoch;
                rec.TerminalOrbitBody = data.bodyName;
            }
            if (data.hasSurface)
            {
                rec.TerminalPosition = data.surfacePosition;
            }
        }

        /// <summary>
        /// Phase 1 capture: extracts vessel state before destruction for deferred processing.
        /// Called synchronously from OnVesselWillDestroy while the Vessel object is still valid.
        /// Handles ORBITING/SUB_ORBITAL/ESCAPING (orbit capture), LANDED/SPLASHED/PRELAUNCH
        /// (surface capture), and FLYING (orbit as best-effort approximation).
        /// </summary>
        PendingDestruction CaptureVesselStateForTerminal(Vessel v, string recordingId)
        {
            var pending = new PendingDestruction
            {
                vesselPid = v.persistentId,
                recordingId = recordingId,
                capturedUT = Planetarium.GetUniversalTime(),
                situation = v.situation
            };

            switch (v.situation)
            {
                case Vessel.Situations.ORBITING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                case Vessel.Situations.FLYING:
                    // Capture orbit data (FLYING uses orbit as best-effort approximation)
                    if (v.orbit != null)
                    {
                        pending.hasOrbit = true;
                        pending.inclination = v.orbit.inclination;
                        pending.eccentricity = v.orbit.eccentricity;
                        pending.semiMajorAxis = v.orbit.semiMajorAxis;
                        pending.lan = v.orbit.LAN;
                        pending.argumentOfPeriapsis = v.orbit.argumentOfPeriapsis;
                        pending.meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch;
                        pending.epoch = v.orbit.epoch;
                        pending.bodyName = v.mainBody?.name ?? "Kerbin";
                    }
                    break;

                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                    // Capture surface position (PRELAUNCH treated as LANDED)
                    pending.hasSurface = true;
                    pending.surfacePosition = new SurfacePosition
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
                    break;
            }

            return pending;
        }

        /// <summary>
        /// Deferred destruction check coroutine. After one frame, verifies whether the vessel
        /// was truly destroyed (not just unloaded or absorbed by docking). If confirmed destroyed,
        /// applies terminal state and removes from BackgroundMap.
        /// </summary>
        IEnumerator DeferredDestructionCheck(PendingDestruction pending)
        {
            yield return null; // defer one frame for KSP to finalize the destroy

            // Fix 2: activeTree could become null during scene change
            if (activeTree == null) yield break;

            // Fix 3: another handler (merge, promotion) may have already processed this vessel
            if (!activeTree.BackgroundMap.ContainsKey(pending.vesselPid)) yield break;

            // Use pure decision method to determine if vessel is truly destroyed
            bool vesselStillExists = FlightRecorder.FindVesselByPid(pending.vesselPid) != null;
            if (!IsTrulyDestroyed(pending.vesselPid, dockingInProgress, vesselStillExists))
            {
                if (dockingInProgress.Contains(pending.vesselPid))
                    Log($"DeferredDestructionCheck: pid={pending.vesselPid} now in dockingInProgress — aborting");
                else
                    Log($"DeferredDestructionCheck: pid={pending.vesselPid} still exists — vessel unloaded, not destroyed");
                yield break;
            }

            // Vessel is truly destroyed — apply terminal state
            RecordingStore.Recording rec;
            if (!activeTree.Recordings.TryGetValue(pending.recordingId, out rec))
            {
                ParsekLog.Warn("Flight", $"DeferredDestructionCheck: recording '{pending.recordingId}' not found in tree");
                yield break;
            }

            ApplyTerminalDestruction(pending, rec);
            activeTree.BackgroundMap.Remove(pending.vesselPid);

            if (!string.IsNullOrEmpty(rec.EvaCrewName))
                ParsekLog.Info("Flight", $"Background EVA vessel ended: pid={pending.vesselPid} recId={pending.recordingId}");
            else
                ParsekLog.Warn("Flight", $"Background vessel destroyed: pid={pending.vesselPid} recId={pending.recordingId}");
        }

        #endregion

        void OnVesselSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (IsRecording) return;
            if (data.host != FlightGlobals.ActiveVessel)
            {
                ParsekLog.VerboseRateLimited("Flight", "sit-change-other",
                    $"OnVesselSituationChange: ignoring non-active vessel ({data.from} → {data.to})");
                return;
            }
            if (data.from != Vessel.Situations.PRELAUNCH)
            {
                ParsekLog.VerboseRateLimited("Flight", "sit-change-not-prelaunch",
                    $"OnVesselSituationChange: not from PRELAUNCH ({data.from} → {data.to})");
                return;
            }
            if (ParsekSettings.Current?.autoRecordOnLaunch == false)
            {
                ParsekLog.Verbose("Flight", "OnVesselSituationChange: auto-record disabled in settings");
                return;
            }

            StartRecording();
            Log("Auto-record started (vessel left pad/runway)");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
        {
            if (activeChainId == null && activeTree == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: no active chain or tree — ignoring");
                return;
            }
            if (pendingSplitInProgress) return; // split must complete first
            if (data.to?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: target vessel is null — ignoring");
                return;
            }

            pendingBoardingTargetPid = data.to.vessel.persistentId;
            boardingConfirmFrames = 0;

            // Store whether the boarding target is in the tree
            pendingBoardingTargetInTree = (activeTree != null &&
                activeTree.BackgroundMap.ContainsKey(data.to.vessel.persistentId));

            Log($"onCrewBoardVessel: target vessel pid={pendingBoardingTargetPid}" +
                (activeTree != null ? $", inTree={pendingBoardingTargetInTree}" : ""));
        }

        /// <summary>
        /// Commits the current chain segment and advances chain state.
        /// Sets up boundary anchor for the next segment.
        /// Mid-chain segments have VesselSnapshot nulled (ghost-only) because the recording
        /// ends at EVA, not at the vessel's actual final position.
        /// </summary>
        void CommitChainSegment(FlightRecorder segmentRecorder, string evaCrewName)
        {
            string segmentId = segmentRecorder.CaptureAtStop.RecordingId;
            Log($"Chain: committing segment (id={segmentId}, chainIdx={activeChainNextIndex})");

            RecordingStore.StashPending(
                segmentRecorder.Recording,
                segmentRecorder.CaptureAtStop.VesselName,
                segmentRecorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: segmentRecorder.CaptureAtStop.RecordingFormatVersion,
                ghostGeometryVersion: segmentRecorder.CaptureAtStop.GhostGeometryVersion,
                partEvents: segmentRecorder.PartEvents);

            if (!RecordingStore.HasPending)
            {
                Log("Chain: segment too short to stash — aborting chain continuation");
                pendingChainContinuation = false;
                pendingChainEvaName = null;
                pendingAutoRecord = false;
                return;
            }

            RecordingStore.Pending.ApplyPersistenceArtifactsFrom(segmentRecorder.CaptureAtStop);
            Log($"Chain: segment has VesselSnapshot={RecordingStore.Pending.VesselSnapshot != null}, " +
                $"GhostVisualSnapshot={RecordingStore.Pending.GhostVisualSnapshot != null}");

            // First transition: initialize chain
            if (activeChainId == null)
            {
                activeChainId = System.Guid.NewGuid().ToString("N");
                activeChainNextIndex = 0;
                Log($"Chain: started new chain (id={activeChainId})");
            }

            // Tag segment with chain metadata
            RecordingStore.Pending.ChainId = activeChainId;
            RecordingStore.Pending.ChainIndex = activeChainNextIndex;
            RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
            RecordingStore.Pending.EvaCrewName = evaCrewName;

            RecordingStore.CommitPending();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();

            // Prepare boundary anchor from last point of committed segment
            if (segmentRecorder.Recording.Count > 0)
                pendingBoundaryAnchor = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1];

            // Advance chain state
            activeChainNextIndex++;
            activeChainPrevId = segmentId;

            // Continuation sampling: track the vessel after mid-chain commit
            if (!segmentRecorder.RecordingStartedAsEva)
            {
                // Vessel segment committed (V→EVA): start continuation to extend trajectory
                continuationVesselPid = segmentRecorder.RecordingVesselId;
                continuationRecordingIdx = RecordingStore.CommittedRecordings.Count - 1;
                var lastPoints = RecordingStore.CommittedRecordings[continuationRecordingIdx].Points;
                if (lastPoints.Count > 0)
                {
                    continuationLastVelocity = lastPoints[lastPoints.Count - 1].velocity;
                    continuationLastUT = lastPoints[lastPoints.Count - 1].ut;
                }
                else
                {
                    continuationLastVelocity = Vector3.zero;
                    continuationLastUT = -1;
                }
                Log($"Continuation started: tracking vessel pid={continuationVesselPid} " +
                    $"in recording #{continuationRecordingIdx}");
            }
            else if (continuationVesselPid != 0)
            {
                // EVA segment committed during boarding (EVA→V): stop continuation,
                // null the vessel segment's VesselSnapshot (new vessel segment handles spawning)
                var vesselRec = RecordingStore.CommittedRecordings[continuationRecordingIdx];
                vesselRec.VesselSnapshot = null;
                Log($"Continuation stopped (boarding): nulled VesselSnapshot " +
                    $"on recording #{continuationRecordingIdx}");
                StopContinuation("boarding");
            }
        }

        /// <summary>
        /// Samples the continuation vessel's position each frame (adaptive sampling).
        /// Extends the committed recording's trajectory beyond the EVA point.
        /// </summary>
        void UpdateContinuationSampling()
        {
            if (continuationVesselPid == 0) return;

            // Guard against stale index (e.g. user wiped recordings from UI)
            if (continuationRecordingIdx < 0 ||
                continuationRecordingIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopContinuation("stale index");
                return;
            }

            Vessel v = FlightRecorder.FindVesselByPid(continuationVesselPid);
            if (v == null)
            {
                StopContinuation("vessel null");
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(velocity, continuationLastVelocity,
                ut, continuationLastUT, continuationMaxInterval,
                continuationVelDirThreshold, continuationSpeedThreshold))
                return;

            var rec = RecordingStore.CommittedRecordings[continuationRecordingIdx];

            // Carry forward resource values from the last point (vessel doesn't earn
            // resources while flying autonomously after EVA)
            var lastPoint = rec.Points.Count > 0
                ? rec.Points[rec.Points.Count - 1]
                : default(TrajectoryPoint);

            var point = new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.transform.rotation,
                velocity = velocity,
                bodyName = v.mainBody.name,
                funds = lastPoint.funds,
                science = lastPoint.science,
                reputation = lastPoint.reputation
            };

            rec.Points.Add(point);
            continuationLastUT = ut;
            continuationLastVelocity = velocity;
        }

        void StopContinuation(string reason)
        {
            Log($"Continuation stopped ({reason}): was tracking pid={continuationVesselPid}, " +
                $"recording #{continuationRecordingIdx}");
            continuationVesselPid = 0;
            continuationRecordingIdx = -1;
        }

        /// <summary>
        /// Refreshes the continuation recording's VesselSnapshot before stopping.
        /// If the vessel is loaded, takes a fresh snapshot. If unloaded/null,
        /// updates the existing snapshot's position from the last trajectory point.
        /// </summary>
        void RefreshContinuationSnapshot()
        {
            if (continuationVesselPid == 0 || continuationRecordingIdx < 0) return;
            if (continuationRecordingIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopContinuation("stale index in snapshot refresh");
                return;
            }

            var rec = RecordingStore.CommittedRecordings[continuationRecordingIdx];
            Vessel v = FlightRecorder.FindVesselByPid(continuationVesselPid);

            if (v != null && v.loaded)
            {
                var snapshot = VesselSpawner.TryBackupSnapshot(v);
                if (snapshot != null)
                {
                    rec.VesselSnapshot = snapshot;
                    Log("Continuation: refreshed vessel snapshot from loaded vessel");
                }
            }
            else if (rec.VesselSnapshot != null && rec.Points.Count > 0)
            {
                var last = rec.Points[rec.Points.Count - 1];
                rec.VesselSnapshot.SetValue("lat",
                    last.latitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.VesselSnapshot.SetValue("lon",
                    last.longitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.VesselSnapshot.SetValue("alt",
                    last.altitude.ToString("R", CultureInfo.InvariantCulture), true);
                Log($"Continuation: updated snapshot position from last trajectory point " +
                    $"(lat={last.latitude:F4})");
            }
        }

        void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            // Mid-recording EVA: tree branch (replaces legacy chain continuation)
            if (IsRecording)
            {
                if (pendingSplitInProgress) return; // another split is being processed

                // Only trigger if EVA is from the vessel we're recording
                if (data.from?.vessel == null ||
                    data.from.vessel.persistentId != recorder.RecordingVesselId)
                {
                    return;
                }

                string kerbalName = ExtractEvaKerbalName(data);
                if (string.IsNullOrEmpty(kerbalName))
                {
                    Log("EVA during recording but could not extract kerbal name — ignoring");
                    return;
                }

                // Tree mode: stop recorder synchronously, defer tree branch creation
                recorder.StopRecordingForChainBoundary();
                pendingSplitRecorder = recorder;
                recorder = null;
                pendingSplitInProgress = true;
                Log($"Mid-recording EVA detected: '{kerbalName}' — starting deferred tree branch");
                StartCoroutine(DeferredEvaBranch(kerbalName, data));
                return;
            }

            if (data.from?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewOnEva: source vessel is null — ignoring");
                return;
            }
            if (data.from.vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                ParsekLog.VerboseRateLimited("Flight", "eva-not-prelaunch",
                    $"OnCrewOnEva: vessel not on pad (sit={data.from.vessel.situation}) — ignoring");
                return;
            }
            if (ParsekSettings.Current?.autoRecordOnEva == false)
            {
                ParsekLog.Verbose("Flight", "OnCrewOnEva: auto-record on EVA disabled in settings");
                return;
            }

            // The EVA kerbal may not yet be the active vessel, defer to Update()
            pendingAutoRecord = true;
            Log("EVA from pad detected — pending auto-record");
        }

        internal static string ExtractEvaKerbalName(GameEvents.FromToAction<Part, Part> data)
        {
            if (data.to == null) return null;
            var crew = data.to.protoModuleCrew;
            if (crew != null && crew.Count > 0) return crew[0].name;
            if (data.to.vessel != null) return data.to.vessel.vesselName;
            return null;
        }

        void OnVesselGoOnRails(Vessel v)
        {
            recorder?.OnVesselGoOnRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOnRails(v);
        }

        void OnVesselGoOffRails(Vessel v)
        {
            recorder?.OnVesselGoOffRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOffRails(v);
        }

        void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            recorder?.OnVesselSOIChanged(data);
            backgroundRecorder?.OnBackgroundVesselSOIChanged(data.host, data.from);
        }

        void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
        {
            if (data.to?.vessel == null) return;
            uint mergedPid = data.to.vessel.persistentId;

            // --- TREE MODE: create merge branch instead of chain segment ---
            if (activeTree != null && recorder != null)
            {
                if (pendingSplitInProgress) return; // split must complete first

                if (recorder.IsRecording)
                {
                    bool isTarget = (recorder.RecordingVesselId == mergedPid);
                    uint absorbedPid;

                    if (isTarget)
                    {
                        // We are the TARGET -- find the absorbed partner (Fix 2: pass Vessel)
                        absorbedPid = FindAbsorbedDockPartnerPid(mergedPid, data.to.vessel);
                    }
                    else
                    {
                        // We are the INITIATOR -- we are being absorbed
                        absorbedPid = recorder.RecordingVesselId;
                    }

                    // Stop recorder synchronously
                    recorder.StopRecordingForChainBoundary();

                    // Set pending state for tree merge
                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
                    dockConfirmFrames = 0;

                    // Race condition guard: prevent Task 6 from misclassifying absorption as destruction
                    if (absorbedPid != 0)
                        dockingInProgress.Add(absorbedPid);

                    Log($"onPartCouple (tree): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid}, isTarget={isTarget})");
                    return;
                }
                else if (!recorder.IsRecording && recorder.CaptureAtStop != null)
                {
                    // OnPhysicsFrame already stopped us (initiator, late event)
                    uint absorbedPid = recorder.RecordingVesselId;

                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
                    dockConfirmFrames = 0;

                    // Clear DockMergePending to prevent chain handler
                    recorder.DockMergePending = false;

                    if (absorbedPid != 0)
                        dockingInProgress.Add(absorbedPid);

                    Log($"onPartCouple (tree, retroactive): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid})");
                    return;
                }
                // else: recorder exists but in some other state -- fall through to legacy
            }

            // --- LEGACY (non-tree) chain mode: unchanged ---
            if (recorder != null && recorder.IsRecording)
            {
                // We're actively recording
                if (mergedPid == recorder.RecordingVesselId)
                {
                    // We're the TARGET — no pid change will happen
                    pendingDockAsTarget = true;
                    pendingDockMergedPid = mergedPid;
                    dockConfirmFrames = 0;
                    // Stop recording silently — Update() will commit and restart
                    recorder.StopRecordingForChainBoundary();
                    Log($"onPartCouple: target dock detected (mergedPid={mergedPid})");
                }
                else
                {
                    // We're the INITIATOR — pid will change, OnPhysicsFrame will stop us
                    recorder.DockMergePending = true;
                    pendingDockMergedPid = mergedPid;
                    dockConfirmFrames = 0;
                    Log($"onPartCouple: initiator dock detected (mergedPid={mergedPid})");
                }
            }
            else if (recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
            {
                // OnPhysicsFrame already stopped us (initiator, pid changed before event fired)
                recorder.DockMergePending = true;
                pendingDockMergedPid = mergedPid;
                dockConfirmFrames = 0;
                Log($"onPartCouple: retroactive initiator dock (mergedPid={mergedPid})");
            }
        }

        void OnPartUndock(Part undockedPart)
        {
            if (recorder == null || !recorder.IsRecording) return;
            if (pendingSplitInProgress) return; // another split is already being processed
            if (undockedPart?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnPartUndock: undocked part or vessel is null — ignoring");
                return;
            }

            uint newPid = undockedPart.vessel.persistentId;
            if (newPid == recorder.RecordingVesselId) return; // transient state, ignore

            // Tree mode: create branch instead of chain segment.
            // Stop recorder synchronously to prevent OnPhysicsFrame interference.
            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;
            Log($"onPartUndock: vessel split off (otherPid={newPid}) — starting deferred tree branch");
            StartCoroutine(DeferredUndockBranch(newPid));
        }

        void OnGroundSciencePartDeployed(ModuleGroundSciencePart deployedPart)
        {
            RecordInventoryPlacementEvent(
                deployedPart,
                PartEventType.InventoryPartPlaced,
                "onGroundSciencePartDeployed");
        }

        void OnGroundSciencePartRemoved(ModuleGroundSciencePart removedPart)
        {
            RecordInventoryPlacementEvent(
                removedPart,
                PartEventType.InventoryPartRemoved,
                "onGroundSciencePartRemoved");
        }

        void RecordInventoryPlacementEvent(
            ModuleGroundSciencePart module,
            PartEventType eventType,
            string sourceEvent)
        {
            if (recorder == null || !recorder.IsRecording) return;
            Part p = module?.part;
            if (p == null) return;

            var evt = new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = eventType,
                partName = p.partInfo?.name ?? "unknown",
                moduleIndex = 0
            };
            recorder.PartEvents.Add(evt);

            Log($"Part event captured: {eventType} '{evt.partName}' pid={evt.partPersistentId} via {sourceEvent}");
        }

        /// <summary>
        /// Commits the current segment as a dock/undock chain boundary.
        /// Initializes the chain if needed, tags segment metadata, and advances chain state.
        /// </summary>
        void CommitDockUndockSegment(FlightRecorder segmentRecorder, PartEventType eventType, uint dockPortPid)
        {
            string segmentId = segmentRecorder.CaptureAtStop.RecordingId;
            Log($"Dock/Undock chain: committing segment (id={segmentId}, event={eventType})");

            RecordingStore.StashPending(
                segmentRecorder.Recording,
                segmentRecorder.CaptureAtStop.VesselName,
                segmentRecorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: segmentRecorder.CaptureAtStop.RecordingFormatVersion,
                ghostGeometryVersion: segmentRecorder.CaptureAtStop.GhostGeometryVersion,
                partEvents: segmentRecorder.PartEvents);

            if (!RecordingStore.HasPending)
            {
                Log("Dock/Undock chain: segment too short to stash — aborting");
                pendingDockMergedPid = 0;
                pendingDockAsTarget = false;
                pendingUndockOtherPid = 0;
                return;
            }

            RecordingStore.Pending.ApplyPersistenceArtifactsFrom(segmentRecorder.CaptureAtStop);

            // Add dock/undock part event to the segment
            if (segmentRecorder.Recording.Count > 0)
            {
                double lastUT = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1].ut;
                RecordingStore.Pending.PartEvents.Add(new PartEvent
                {
                    ut = lastUT,
                    partPersistentId = dockPortPid,
                    eventType = eventType,
                    partName = eventType.ToString()
                });
            }

            // First transition: initialize chain
            if (activeChainId == null)
            {
                activeChainId = System.Guid.NewGuid().ToString("N");
                activeChainNextIndex = 0;
                Log($"Dock/Undock chain: started new chain (id={activeChainId})");
            }

            // Tag segment with chain metadata
            RecordingStore.Pending.ChainId = activeChainId;
            RecordingStore.Pending.ChainIndex = activeChainNextIndex;
            RecordingStore.Pending.ChainBranch = 0; // always primary path
            RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
            RecordingStore.Pending.EvaCrewName = null; // vessel segment, not EVA

            // Mid-chain segments are ghost-only (VesselSnapshot nulled)
            RecordingStore.Pending.VesselSnapshot = null;

            RecordingStore.CommitPending();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();

            // Prepare boundary anchor from last point of committed segment
            if (segmentRecorder.Recording.Count > 0)
                pendingBoundaryAnchor = segmentRecorder.Recording[segmentRecorder.Recording.Count - 1];

            // Advance chain state
            activeChainNextIndex++;
            activeChainPrevId = segmentId;
        }

        /// <summary>
        /// Commits the current segment as an atmosphere boundary chain split.
        /// Follows the same pattern as CommitDockUndockSegment.
        /// </summary>
        void CommitBoundarySplit(string completedPhase, string bodyName)
        {
            var captured = recorder.CaptureAtStop;
            string segmentId = captured != null ? captured.RecordingId : null;
            ParsekLog.Info("Flight", $"Boundary split: committing segment " +
                $"(id={segmentId}, phase={completedPhase}, body={bodyName}, " +
                $"points={recorder.Recording.Count}, orbits={recorder.OrbitSegments.Count})");

            RecordingStore.StashPending(
                recorder.Recording,
                captured != null ? captured.VesselName : (FlightGlobals.ActiveVessel?.vesselName ?? "Unknown"),
                recorder.OrbitSegments,
                recordingId: segmentId,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                ghostGeometryVersion: captured != null ? (int?)captured.GhostGeometryVersion : null,
                partEvents: recorder.PartEvents);

            if (!RecordingStore.HasPending)
            {
                ParsekLog.Warn("Flight", "Boundary split: segment too short to stash — aborting " +
                    $"(points={recorder.Recording.Count})");
                return;
            }

            if (captured != null)
                RecordingStore.Pending.ApplyPersistenceArtifactsFrom(captured);

            // Tag segment with phase and body
            RecordingStore.Pending.SegmentPhase = completedPhase;
            RecordingStore.Pending.SegmentBodyName = bodyName;

            // First transition: initialize chain
            if (activeChainId == null)
            {
                activeChainId = System.Guid.NewGuid().ToString("N");
                activeChainNextIndex = 0;
                ParsekLog.Info("Flight", $"Boundary split: started new chain (id={activeChainId})");
            }

            // Tag segment with chain metadata
            RecordingStore.Pending.ChainId = activeChainId;
            RecordingStore.Pending.ChainIndex = activeChainNextIndex;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.ParentRecordingId = activeChainPrevId;
            RecordingStore.Pending.EvaCrewName = activeChainCrewName;

            // Mid-chain segments are ghost-only
            RecordingStore.Pending.VesselSnapshot = null;

            RecordingStore.CommitPending();
            ParsekScenario.ReserveSnapshotCrew();
            ParsekScenario.SwapReservedCrewInFlight();

            // Prepare boundary anchor from last point of committed segment
            if (recorder.Recording.Count > 0)
                pendingBoundaryAnchor = recorder.Recording[recorder.Recording.Count - 1];

            // Advance chain state
            activeChainNextIndex++;
            activeChainPrevId = segmentId;
            ParsekLog.Verbose("Flight", $"Boundary split committed: chain={activeChainId}, " +
                $"nextIdx={activeChainNextIndex}, segment={bodyName} {completedPhase}, " +
                $"totalRecordings={RecordingStore.CommittedRecordings.Count}");
        }

        /// <summary>
        /// Starts ghost-only continuation recording for the "other" vessel after undock.
        /// This vessel gets ChainBranch = 1 so it plays back as a ghost but never spawns.
        /// </summary>
        void StartUndockContinuation(uint otherPid)
        {
            // Stop any existing continuation first
            if (undockContinuationPid != 0)
                StopUndockContinuation("replaced by new undock");

            Vessel otherVessel = FlightRecorder.FindVesselByPid(otherPid);
            if (otherVessel == null)
            {
                Log($"Undock continuation: cannot find vessel pid={otherPid} — skipping");
                return;
            }

            // Take snapshot for ghost visuals
            ConfigNode ghostSnapshot = VesselSpawner.TryBackupSnapshot(otherVessel);

            // Create a committed recording for the continuation
            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = otherVessel.packed
                ? (Vector3)otherVessel.obt_velocity
                : (Vector3)(otherVessel.rb_velocityD + Krakensbane.GetFrameVelocity());

            var seedPoint = new TrajectoryPoint
            {
                ut = ut,
                latitude = otherVessel.latitude,
                longitude = otherVessel.longitude,
                altitude = otherVessel.altitude,
                rotation = otherVessel.transform.rotation,
                velocity = velocity,
                bodyName = otherVessel.mainBody.name
            };

            var contRec = new RecordingStore.Recording
            {
                VesselName = otherVessel.vesselName + " (undock continuation)",
                ChainId = activeChainId,
                ChainIndex = activeChainNextIndex - 1, // same index as the player's new segment
                ChainBranch = 1, // parallel branch — ghost-only, never spawns
                GhostVisualSnapshot = ghostSnapshot,
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = 4
            };
            contRec.Points.Add(seedPoint);

            RecordingStore.CommittedRecordings.Add(contRec);
            undockContinuationRecIdx = RecordingStore.CommittedRecordings.Count - 1;
            undockContinuationPid = otherPid;
            undockContinuationLastVel = velocity;
            undockContinuationLastUT = ut;

            Log($"Undock continuation started: tracking vessel pid={otherPid} " +
                $"in recording #{undockContinuationRecIdx} (chain={activeChainId}, branch=1)");
        }

        /// <summary>
        /// Samples the undock continuation vessel's position each frame (adaptive sampling).
        /// Mirrors UpdateContinuationSampling but for the undocked sibling vessel.
        /// </summary>
        void UpdateUndockContinuationSampling()
        {
            if (undockContinuationPid == 0) return;

            // Guard against stale index
            if (undockContinuationRecIdx < 0 ||
                undockContinuationRecIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopUndockContinuation("stale index");
                return;
            }

            Vessel v = FlightRecorder.FindVesselByPid(undockContinuationPid);
            if (v == null)
            {
                StopUndockContinuation("vessel null");
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            Vector3 velocity = v.packed
                ? (Vector3)v.obt_velocity
                : (Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity());

            if (!TrajectoryMath.ShouldRecordPoint(velocity, undockContinuationLastVel,
                ut, undockContinuationLastUT, continuationMaxInterval,
                continuationVelDirThreshold, continuationSpeedThreshold))
                return;

            var rec = RecordingStore.CommittedRecordings[undockContinuationRecIdx];

            // Carry forward resource values from the last point
            var lastPoint = rec.Points.Count > 0
                ? rec.Points[rec.Points.Count - 1]
                : default(TrajectoryPoint);

            var point = new TrajectoryPoint
            {
                ut = ut,
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.transform.rotation,
                velocity = velocity,
                bodyName = v.mainBody.name,
                funds = lastPoint.funds,
                science = lastPoint.science,
                reputation = lastPoint.reputation
            };

            rec.Points.Add(point);
            undockContinuationLastUT = ut;
            undockContinuationLastVel = velocity;
        }

        void StopUndockContinuation(string reason)
        {
            Log($"Undock continuation stopped ({reason}): was tracking pid={undockContinuationPid}, " +
                $"recording #{undockContinuationRecIdx}");
            undockContinuationPid = 0;
            undockContinuationRecIdx = -1;
            undockContinuationLastUT = -1;
        }

        /// <summary>
        /// Refreshes the undock continuation recording's ghost snapshot before stopping.
        /// </summary>
        void RefreshUndockContinuationSnapshot()
        {
            if (undockContinuationPid == 0 || undockContinuationRecIdx < 0) return;
            if (undockContinuationRecIdx >= RecordingStore.CommittedRecordings.Count)
            {
                StopUndockContinuation("stale index in snapshot refresh");
                return;
            }

            var rec = RecordingStore.CommittedRecordings[undockContinuationRecIdx];
            Vessel v = FlightRecorder.FindVesselByPid(undockContinuationPid);

            if (v != null && v.loaded)
            {
                var snapshot = VesselSpawner.TryBackupSnapshot(v);
                if (snapshot != null)
                {
                    rec.GhostVisualSnapshot = snapshot;
                    Log("Undock continuation: refreshed ghost snapshot from loaded vessel");
                }
            }
            else if (rec.GhostVisualSnapshot != null && rec.Points.Count > 0)
            {
                var last = rec.Points[rec.Points.Count - 1];
                rec.GhostVisualSnapshot.SetValue("lat",
                    last.latitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.GhostVisualSnapshot.SetValue("lon",
                    last.longitude.ToString("R", CultureInfo.InvariantCulture), true);
                rec.GhostVisualSnapshot.SetValue("alt",
                    last.altitude.ToString("R", CultureInfo.InvariantCulture), true);
                Log($"Undock continuation: updated snapshot position from last trajectory point");
            }
        }

        void OnFlightReady()
        {
            Log("Flight ready. Checking for pending recordings...");

            // Shutdown background recorder before clearing tree
            if (backgroundRecorder != null)
            {
                backgroundRecorder.Shutdown();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }

            // Clear tree state on flight ready (revert resets everything)
            activeTree = null;

            // Clear split event detection state
            lastBranchUT = -1;
            lastBranchVesselPids.Clear();
            pendingSplitInProgress = false;
            pendingSplitRecorder = null;
            preBreakVesselPids = null;

            // Clear chain state on flight ready (revert resets everything)
            activeChainId = null;
            activeChainNextIndex = 0;
            activeChainPrevId = null;
            activeChainCrewName = null;
            pendingChainContinuation = false;
            pendingChainIsBoarding = false;
            pendingChainEvaName = null;
            pendingBoardingTargetPid = 0;
            pendingBoundaryAnchor = null;
            continuationVesselPid = 0;
            continuationRecordingIdx = -1;

            // Clear dock/undock state
            pendingDockMergedPid = 0;
            pendingDockAsTarget = false;
            dockConfirmFrames = 0;
            pendingUndockOtherPid = 0;
            undockConfirmFrames = 0;
            undockContinuationPid = 0;
            undockContinuationRecIdx = -1;
            undockContinuationLastUT = -1;

            // Clear merge event detection state
            pendingTreeDockMerge = false;
            pendingDockAbsorbedPid = 0;
            pendingBoardingTargetInTree = false;
            dockingInProgress.Clear();

            // Handle pending tree: show tree merge dialog.
            // On non-revert scene changes, pending trees are auto-committed by ParsekScenario.
            // Reaching here means either a revert or a fallback (auto-commit missed).
            if (RecordingStore.HasPendingTree)
            {
                var pt = RecordingStore.PendingTree;
                ParsekLog.Warn("Flight", $"Pending tree '{pt.TreeName}' reached OnFlightReady — showing tree merge dialog (fallback)");
                MergeDialog.ShowTreeDialog(pt);
            }

            // Handle pending standalone recording.
            // On non-revert scene changes, pending recordings are auto-committed by ParsekScenario.
            // On reverts, the merge dialog is expected here (player chose to revert).
            if (RecordingStore.HasPending)
            {
                var pending = RecordingStore.Pending;

                // Check if this is part of a chain with committed siblings
                bool hasChainSiblings = !string.IsNullOrEmpty(pending.ChainId) &&
                    RecordingStore.GetChainRecordings(pending.ChainId) != null;

                if (hasChainSiblings)
                {
                    // Chain-level merge dialog (covers all segments)
                    Log($"Found pending chain recording (chain={pending.ChainId}, idx={pending.ChainIndex})");
                    MergeDialog.Show(pending);
                }
                else if (!string.IsNullOrEmpty(pending.ParentRecordingId))
                {
                    // Legacy: auto-commit EVA child recordings (skip merge dialog)
                    bool parentFound = false;
                    foreach (var rec in RecordingStore.CommittedRecordings)
                    {
                        if (rec.RecordingId == pending.ParentRecordingId)
                        {
                            parentFound = true;
                            break;
                        }
                    }
                    if (parentFound)
                    {
                        Log($"Auto-committing EVA child recording (parent={pending.ParentRecordingId}, crew={pending.EvaCrewName})");
                        RecordingStore.CommitPending();
                        ParsekScenario.ReserveSnapshotCrew();
                    }
                    else
                    {
                        Log($"EVA child recording has no matching parent — showing merge dialog");
                        MergeDialog.Show(pending);
                    }
                }
                else
                {
                    Log($"Found pending recording from {pending.VesselName} ({pending.Points.Count} points)");
                    MergeDialog.Show(pending);
                }
            }

            // Swap reserved crew out of the active vessel so the player
            // can't record with them again (they belong to deferred-spawn vessels)
            int swapResult = ParsekScenario.SwapReservedCrewInFlight();
            if (swapResult > 0)
                Log($"Crew swap on flight ready: {swapResult} crew swapped out of active vessel");
            else if (ParsekScenario.CrewReplacements.Count > 0)
                Log($"Crew swap on flight ready: 0 swapped ({ParsekScenario.CrewReplacements.Count} reservations exist but no matches on active vessel)");

            var committed = RecordingStore.CommittedRecordings;
            Log($"Timeline has {committed.Count} committed recording(s)");
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                string hasVessel = rec.VesselSnapshot != null ? "vessel" :
                    rec.GhostVisualSnapshot != null ? "ghost" : "ghost-only";
                string orbitInfo = rec.OrbitSegments.Count > 0
                    ? $", {rec.OrbitSegments.Count} orbit seg(s)"
                    : "";
                string chainInfo = !string.IsNullOrEmpty(rec.ChainId)
                    ? $", chain idx={rec.ChainIndex}"
                    : "";
                Log($"  Recording #{i}: \"{rec.VesselName}\" UT {rec.StartUT:F0}-{rec.EndUT:F0}, " +
                    $"{rec.Points.Count} pts{orbitInfo}, {hasVessel}{chainInfo}");
            }
        }

        #endregion

        #region Input Handling

        void HandleInput()
        {
            // Backspace - Exit watch mode (return to vessel)
            if (watchedRecordingIndex >= 0 && Input.GetKeyDown(KeyCode.Backspace))
            {
                ExitWatchMode();
            }

        }

        #endregion

        #region Recording

        public void StartRecording()
        {
            recorder = new FlightRecorder();
            if (pendingBoundaryAnchor.HasValue)
            {
                recorder.BoundaryAnchor = pendingBoundaryAnchor;
                pendingBoundaryAnchor = null;
            }
            // Propagate tree mode to new recorder so DecideOnVesselSwitch uses tree decisions
            if (activeTree != null)
                recorder.ActiveTree = activeTree;
            recorder.StartRecording();
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"StartRecording blocked: {DetermineRecordingBlockReason()}");
                return;
            }

            uint pid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0;
            ParsekLog.Info("Flight", $"StartRecording succeeded: pid={pid}, chainActive={activeChainId != null}, tree={activeTree != null}");
        }

        public void StopRecording()
        {
            recorder?.StopRecording();

            // Tag the final segment with chain metadata if in a chain
            if (recorder?.CaptureAtStop != null && activeChainId != null)
            {
                recorder.CaptureAtStop.ChainId = activeChainId;
                recorder.CaptureAtStop.ChainIndex = activeChainNextIndex;
                recorder.CaptureAtStop.ParentRecordingId = activeChainPrevId;
                recorder.CaptureAtStop.EvaCrewName = activeChainCrewName;
            }

            // Tag final segment phase if untagged
            if (recorder?.CaptureAtStop != null && string.IsNullOrEmpty(recorder.CaptureAtStop.SegmentPhase))
            {
                var v = FlightGlobals.ActiveVessel;
                if (v != null && v.mainBody != null)
                {
                    recorder.CaptureAtStop.SegmentBodyName = v.mainBody.name;
                    if (v.mainBody.atmosphere)
                        recorder.CaptureAtStop.SegmentPhase = v.altitude < v.mainBody.atmosphereDepth ? "atmo" : "exo";
                    else
                        recorder.CaptureAtStop.SegmentPhase = "space";
                    ParsekLog.Verbose("Flight", $"Final segment tagged (StopRecording): " +
                        $"{recorder.CaptureAtStop.SegmentBodyName} {recorder.CaptureAtStop.SegmentPhase}");
                }
            }
        }

        public void ClearRecording()
        {
            recorder = null;
            lastPlaybackIndex = 0;
            GameStateRecorder.PendingScienceSubjects.Clear();
            Log("Recording cleared");
        }

        public void CommitFlight()
        {
            // Guard: no recording data
            if (recorder == null || recorder.Recording.Count < 2)
            {
                ParsekLog.ScreenMessage("No recording to commit", 2f);
                return;
            }

            // Guard: mid-chain recordings can't be committed standalone
            if (activeChainId != null)
            {
                ParsekLog.ScreenMessage("Cannot commit mid-chain \u2014 finish or revert first", 2f);
                Log("CommitFlight blocked: active chain in progress");
                return;
            }

            // Stop recording if still active
            if (recorder.IsRecording)
                StopRecording();

            // Stop any continuation sampling
            if (continuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                StopContinuation("commit flight");
            }
            if (undockContinuationPid != 0)
            {
                RefreshUndockContinuationSnapshot();
                StopUndockContinuation("commit flight");
            }

            string vesselName = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.vesselName
                : "Unknown Vessel";

            var captured = recorder.CaptureAtStop;

            // Stash as pending recording (reuses OnSceneChangeRequested pattern)
            RecordingStore.StashPending(
                recorder.Recording,
                vesselName,
                recorder.OrbitSegments,
                recordingId: captured != null ? captured.RecordingId : null,
                recordingFormatVersion: captured != null ? (int?)captured.RecordingFormatVersion : null,
                ghostGeometryVersion: captured != null ? (int?)captured.GhostGeometryVersion : null,
                partEvents: recorder.PartEvents);

            if (!RecordingStore.HasPending)
            {
                Log("CommitFlight: StashPending rejected recording (too short?)");
                return;
            }

            // Apply snapshot/geometry artifacts from stop-time capture
            if (captured != null)
            {
                RecordingStore.Pending.ApplyPersistenceArtifactsFrom(captured);
                Log("CommitFlight: applied stop-time artifacts");
            }
            else
            {
                // Fallback: capture vessel now
                bool wasDestroyed = recorder.VesselDestroyedDuringRecording;
                VesselSpawner.SnapshotVessel(
                    RecordingStore.Pending,
                    wasDestroyed,
                    destroyedFallbackSnapshot: recorder.InitialGhostVisualSnapshot
                        ?? recorder.LastGoodVesselSnapshot);
                RecordingStore.Pending.GhostVisualSnapshot =
                    recorder.InitialGhostVisualSnapshot != null
                        ? recorder.InitialGhostVisualSnapshot.CreateCopy()
                        : (RecordingStore.Pending.VesselSnapshot != null
                            ? RecordingStore.Pending.VesselSnapshot.CreateCopy()
                            : null);
                Log("CommitFlight: captured vessel snapshot (fallback path)");
            }

            // Mark as non-revert commit:
            // - VesselSpawned=true prevents duplicate spawn while vessel is in-game
            // - SpawnedVesselPersistentId enables duplicate detection on save/load
            // - LastAppliedResourceIndex prevents double-applying deltas
            // All three reset naturally on "Revert to Launch" (launch quicksave has no RECORDING nodes)
            var pending = RecordingStore.Pending;
            pending.VesselSpawned = true;
            pending.SpawnedVesselPersistentId = recorder.RecordingVesselId;
            pending.LastAppliedResourceIndex = pending.Points.Count - 1;

            // Commit to timeline
            RecordingStore.CommitPending();

            // Clear recorder state
            recorder = null;
            lastPlaybackIndex = 0;

            Log($"CommitFlight: committed \"{vesselName}\" to timeline");
            ParsekLog.ScreenMessage("Flight committed to timeline!", 3f);
        }

        /// <summary>
        /// Commits the active recording tree from the Commit Flight button.
        /// Finalizes all recordings, spawns leaf vessels, reserves crew.
        /// The active vessel stays live (VesselSpawned=true).
        /// </summary>
        public void CommitTreeFlight()
        {
            if (activeTree == null)
            {
                ParsekLog.ScreenMessage("No active tree to commit", 2f);
                return;
            }

            double commitUT = Planetarium.GetUniversalTime();
            ParsekLog.Info("Flight", $"CommitTreeFlight: starting tree commit at UT={commitUT:F1}");

            // Stop continuations
            if (continuationVesselPid != 0)
            {
                RefreshContinuationSnapshot();
                StopContinuation("tree commit");
            }
            if (undockContinuationPid != 0)
            {
                RefreshUndockContinuationSnapshot();
                StopUndockContinuation("tree commit");
            }

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

            // Identify the active vessel's recording
            string activeRecId = activeTree.ActiveRecordingId;
            RecordingStore.Recording activeRec = null;
            if (activeRecId != null)
                activeTree.Recordings.TryGetValue(activeRecId, out activeRec);

            // Mark active vessel's recording as already spawned
            if (activeRec != null)
            {
                activeRec.VesselSpawned = true;
                activeRec.SpawnedVesselPersistentId = recorder != null ? recorder.RecordingVesselId :
                    (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u);
                activeRec.LastAppliedResourceIndex = activeRec.Points.Count - 1;
            }

            // Tree resources are already live in the game — mark as applied
            activeTree.ResourcesApplied = true;

            // Mark all tree recordings' resource indices as fully applied
            int markedCount = 0;
            foreach (var rec in activeTree.Recordings.Values)
            {
                if (rec.Points.Count > 0)
                {
                    rec.LastAppliedResourceIndex = rec.Points.Count - 1;
                    markedCount++;
                }
            }
            ParsekLog.Info("Flight",
                $"CommitTreeFlight: ResourcesApplied=true, marked {markedCount}/{activeTree.Recordings.Count} recordings as fully applied");

            // Reserve crew for all leaf vessels (except active vessel)
            var spawnableLeaves = activeTree.GetSpawnableLeaves();
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster != null)
            {
                GameStateRecorder.SuppressCrewEvents = true;
                try
                {
                    foreach (var leaf in spawnableLeaves)
                    {
                        if (leaf.VesselSpawned) continue;
                        if (leaf.VesselSnapshot == null) continue;
                        ParsekScenario.ReserveCrewIn(leaf.VesselSnapshot, false, roster);
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressCrewEvents = false;
                }
            }

            // Commit tree to storage
            RecordingStore.CommitTree(activeTree);

            // Spawn all non-active leaf vessels
            SpawnTreeLeaves(activeTree, activeRecId);

            // Crew swap on active vessel
            int swapped = ParsekScenario.SwapReservedCrewInFlight();
            if (swapped > 0)
                ParsekLog.Info("Flight", $"CommitTreeFlight: swapped {swapped} crew on active vessel");

            int spawnCount = 0;
            foreach (var leaf in spawnableLeaves)
            {
                if (leaf.RecordingId != activeRecId && leaf.VesselSpawned)
                    spawnCount++;
            }

            // Clear state
            var treeName = activeTree.TreeName;
            activeTree = null;
            if (backgroundRecorder != null)
            {
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                backgroundRecorder = null;
            }
            recorder = null;
            lastPlaybackIndex = 0;

            Log($"CommitTreeFlight: committed tree \"{treeName}\" — {spawnCount} vessel(s) spawned");
            if (spawnCount > 0)
                ParsekLog.ScreenMessage($"Tree committed to timeline! {spawnCount} vessel(s) spawned.", 3f);
            else
                ParsekLog.ScreenMessage("Tree committed to timeline!", 3f);
        }

        /// <summary>
        /// Finalizes the active tree for a revert (Flight -> Flight).
        /// Preserves vessel snapshots so the merge dialog can offer spawning.
        /// </summary>
        void CommitTreeRevert(double commitUT)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight", $"CommitTreeRevert: finalizing tree at UT={commitUT:F1}");

            // Finalize all recordings (active + background)
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: false);

            // Stash as pending tree -- snapshots preserved for merge dialog
            RecordingStore.StashPendingTree(activeTree);

            ParsekLog.Info("Flight",
                $"CommitTreeRevert: stashed pending tree '{activeTree.TreeName}' with snapshots preserved");
        }

        /// <summary>
        /// Commits the active tree on scene exit (ghost-only, no spawning).
        /// Finalizes all recordings and stashes the tree as pending.
        /// </summary>
        void CommitTreeSceneExit(double commitUT)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: finalizing tree at UT={commitUT:F1}");

            // Finalize all recordings
            FinalizeTreeRecordings(activeTree, commitUT, isSceneExit: true);

            // Null all vessel snapshots (no spawning on scene exit)
            foreach (var rec in activeTree.Recordings.Values)
            {
                rec.VesselSnapshot = null;
            }

            // Stash as pending tree -- auto-committed ghost-only by ParsekScenario.OnLoad
            RecordingStore.StashPendingTree(activeTree);

            ParsekLog.Info("Flight", $"CommitTreeSceneExit: stashed pending tree '{activeTree.TreeName}'");
        }

        /// <summary>
        /// Finalizes all recordings in the tree: stops active recorder, flushes data,
        /// captures terminal state/orbit/position for all recordings.
        /// </summary>
        void FinalizeTreeRecordings(RecordingTree tree, double commitUT, bool isSceneExit)
        {
            // 1. Stop and flush the active recorder
            if (recorder != null)
            {
                if (recorder.IsRecording)
                {
                    if (recorder.IsBackgrounded)
                        recorder.IsRecording = false;
                    else
                        recorder.ForceStop();
                }
                FlushRecorderToTreeRecording(recorder, tree);
            }

            // 2. Finalize background recorder (close orbit segments, flush data)
            if (backgroundRecorder != null)
                backgroundRecorder.FinalizeAllForCommit(commitUT);

            // 3. Process each recording in the tree
            foreach (var kvp in tree.Recordings)
            {
                var rec = kvp.Value;

                // Set ExplicitStartUT if not already set
                if (double.IsNaN(rec.ExplicitStartUT))
                {
                    if (rec.Points.Count > 0)
                        rec.ExplicitStartUT = rec.Points[0].ut;
                    else if (rec.OrbitSegments.Count > 0)
                        rec.ExplicitStartUT = rec.OrbitSegments[0].startUT;
                }

                // Set ExplicitEndUT on leaf recordings without one
                if (double.IsNaN(rec.ExplicitEndUT))
                {
                    if (rec.Points.Count > 0)
                        rec.ExplicitEndUT = rec.Points[rec.Points.Count - 1].ut;
                    else
                        rec.ExplicitEndUT = commitUT;
                }

                // Determine terminal state for recordings that don't have one yet
                bool isLeaf = rec.ChildBranchPointId == null;
                if (isLeaf && !rec.TerminalStateValue.HasValue)
                {
                    // Try to find the vessel
                    Vessel vessel = rec.VesselPersistentId != 0
                        ? FlightRecorder.FindVesselByPid(rec.VesselPersistentId)
                        : null;

                    if (vessel != null)
                    {
                        rec.TerminalStateValue = RecordingTree.DetermineTerminalState((int)vessel.situation);
                        CaptureTerminalOrbit(rec, vessel);
                        CaptureTerminalPosition(rec, vessel);

                        // Re-snapshot live vessels for Commit Flight path (fresh state)
                        if (!isSceneExit)
                        {
                            ConfigNode freshSnapshot = VesselSpawner.TryBackupSnapshot(vessel);
                            if (freshSnapshot != null)
                            {
                                rec.VesselSnapshot = freshSnapshot;
                                if (rec.GhostVisualSnapshot == null)
                                    rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
                            }
                        }
                    }
                    else
                    {
                        // Vessel not found — mark as destroyed defensively
                        rec.TerminalStateValue = TerminalState.Destroyed;
                        rec.VesselSnapshot = null;
                        ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: vessel pid={rec.VesselPersistentId} " +
                            $"not found for recording '{rec.RecordingId}' — marking Destroyed");
                    }
                }

                // Warn if leaf has no playback data
                if (isLeaf && rec.Points.Count == 0 && rec.OrbitSegments.Count == 0 && !rec.SurfacePos.HasValue)
                    ParsekLog.Warn("Flight", $"FinalizeTreeRecordings: leaf '{rec.RecordingId}' has no playback data");

                Log($"FinalizeTreeRecordings: rec='{rec.RecordingId}' vessel='{rec.VesselName}' " +
                    $"points={rec.Points.Count} orbitSegs={rec.OrbitSegments.Count} " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "none"} " +
                    $"snapshot={rec.VesselSnapshot != null} leaf={isLeaf}");
            }

            // Compute tree-level resource delta
            tree.DeltaFunds = ComputeTreeDeltaFunds(tree);
            tree.DeltaScience = ComputeTreeDeltaScience(tree);
            tree.DeltaReputation = ComputeTreeDeltaReputation(tree);

            ParsekLog.Info("Flight",
                $"FinalizeTreeRecordings: tree '{tree.TreeName}' resource delta: " +
                $"funds={tree.DeltaFunds:+0.0;-0.0}, science={tree.DeltaScience:+0.0;-0.0}, " +
                $"rep={tree.DeltaReputation:+0.0;-0.0}");
        }

        internal static double ComputeTreeDeltaFunds(RecordingTree tree)
        {
            double currentFunds = 0;
            try { if (Funding.Instance != null) currentFunds = Funding.Instance.Funds; } catch { }
            return currentFunds - tree.PreTreeFunds;
        }

        internal static double ComputeTreeDeltaScience(RecordingTree tree)
        {
            double currentScience = 0;
            try { if (ResearchAndDevelopment.Instance != null) currentScience = ResearchAndDevelopment.Instance.Science; } catch { }
            return currentScience - tree.PreTreeScience;
        }

        internal static float ComputeTreeDeltaReputation(RecordingTree tree)
        {
            float currentRep = 0;
            try { if (Reputation.Instance != null) currentRep = Reputation.Instance.reputation; } catch { }
            return currentRep - tree.PreTreeReputation;
        }

        /// <summary>
        /// Spawns all leaf vessels from a committed tree (except the active vessel).
        /// Handles crew reservation (already done by caller), proximity offsets.
        /// </summary>
        void SpawnTreeLeaves(RecordingTree tree, string activeRecordingId)
        {
            var spawnableLeaves = tree.GetSpawnableLeaves();
            if (spawnableLeaves.Count == 0)
            {
                ParsekLog.Info("Flight", "SpawnTreeLeaves: no spawnable leaves");
                return;
            }

            ParsekLog.Info("Flight", $"SpawnTreeLeaves: {spawnableLeaves.Count} spawnable leaf/leaves");

            foreach (var leaf in spawnableLeaves)
            {
                // Skip the active vessel — it's already in-game
                if (leaf.RecordingId == activeRecordingId)
                    continue;

                if (leaf.VesselSnapshot == null)
                {
                    ParsekLog.Warn("Flight", $"SpawnTreeLeaves: leaf '{leaf.RecordingId}' has null snapshot — skipping");
                    continue;
                }

                // Before spawning, recover any timeline-spawned vessel with the same name
                // to prevent overlap collisions (e.g. an older committed recording already
                // spawned the same vessel on the pad).
                RecoverTimelineSpawnedVessel(leaf.VesselName);

                try
                {
                    uint spawnedPid = VesselSpawner.RespawnVessel(leaf.VesselSnapshot);
                    if (spawnedPid != 0)
                    {
                        leaf.VesselSpawned = true;
                        leaf.SpawnedVesselPersistentId = spawnedPid;
                        leaf.LastAppliedResourceIndex = leaf.Points.Count - 1;
                        ResourceBudget.Invalidate();
                        ParsekLog.Info("Flight", $"SpawnTreeLeaves: spawned leaf '{leaf.VesselName}' pid={spawnedPid}");
                    }
                    else
                    {
                        ParsekLog.Warn("Flight", $"SpawnTreeLeaves: failed to spawn leaf '{leaf.VesselName}'");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Flight", $"SpawnTreeLeaves: exception spawning '{leaf.VesselName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Finds and recovers any in-scene vessel that was timeline-spawned from a
        /// committed recording with the given name. Prevents overlap collisions when
        /// a tree commit spawns a vessel on top of an already-spawned timeline vessel.
        /// </summary>
        void RecoverTimelineSpawnedVessel(string vesselName)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!rec.VesselSpawned || rec.SpawnedVesselPersistentId == 0) continue;
                if (rec.VesselName != vesselName) continue;

                // Find the loaded vessel by persistent ID
                Vessel vessel = null;
                for (int v = 0; v < FlightGlobals.Vessels.Count; v++)
                {
                    if (FlightGlobals.Vessels[v].persistentId == rec.SpawnedVesselPersistentId)
                    {
                        vessel = FlightGlobals.Vessels[v];
                        break;
                    }
                }
                if (vessel != null && vessel.loaded)
                {
                    ParsekLog.Info("Flight",
                        $"RecoverTimelineSpawnedVessel: recovering '{vesselName}' pid={rec.SpawnedVesselPersistentId} " +
                        $"(from recording #{i}) to make room for tree leaf spawn");
                    ShipConstruction.RecoverVesselFromFlight(vessel.protoVessel, HighLogic.CurrentGame.flightState, true);
                }

                // Mark recording as no longer having a live vessel
                rec.VesselSpawned = false;
                rec.SpawnedVesselPersistentId = 0;
            }
        }

        /// <summary>
        /// Captures terminal orbit parameters from a vessel's current orbit.
        /// </summary>
        static void CaptureTerminalOrbit(RecordingStore.Recording rec, Vessel vessel)
        {
            if (vessel == null || vessel.orbit == null) return;

            var sit = vessel.situation;
            if (sit == Vessel.Situations.ORBITING || sit == Vessel.Situations.SUB_ORBITAL
                || sit == Vessel.Situations.FLYING || sit == Vessel.Situations.ESCAPING)
            {
                var orb = vessel.orbit;
                rec.TerminalOrbitInclination = orb.inclination;
                rec.TerminalOrbitEccentricity = orb.eccentricity;
                rec.TerminalOrbitSemiMajorAxis = orb.semiMajorAxis;
                rec.TerminalOrbitLAN = orb.LAN;
                rec.TerminalOrbitArgumentOfPeriapsis = orb.argumentOfPeriapsis;
                rec.TerminalOrbitMeanAnomalyAtEpoch = orb.meanAnomalyAtEpoch;
                rec.TerminalOrbitEpoch = orb.epoch;
                rec.TerminalOrbitBody = orb.referenceBody?.name ?? "Kerbin";
            }
        }

        /// <summary>
        /// Captures terminal surface position from a vessel's current state.
        /// </summary>
        static void CaptureTerminalPosition(RecordingStore.Recording rec, Vessel vessel)
        {
            if (vessel == null) return;

            var sit = vessel.situation;
            if (sit == Vessel.Situations.LANDED || sit == Vessel.Situations.SPLASHED
                || sit == Vessel.Situations.PRELAUNCH)
            {
                rec.TerminalPosition = new SurfacePosition
                {
                    body = vessel.mainBody?.name ?? "Kerbin",
                    latitude = vessel.latitude,
                    longitude = vessel.longitude,
                    altitude = vessel.altitude,
                    rotation = vessel.srfRelRotation,
                    situation = sit == Vessel.Situations.SPLASHED
                        ? SurfaceSituation.Splashed
                        : SurfaceSituation.Landed
                };
            }
        }

        #endregion

        #region Manual Playback (preview)

        public void StartPlayback()
        {
            if (recording.Count < 2)
            {
                Log("Not enough recording points for playback!");
                return;
            }

            ExitAllWarpForPlaybackStart("Preview");

            // Build preview ghost from CaptureAtStop snapshot (same as timeline ghosts)
            // so that part events play back identically.
            previewRecording = null;
            previewGhostState = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;

            var capture = recorder?.CaptureAtStop;
            if (capture != null)
            {
                previewRecording = capture;
                List<ParachuteGhostInfo> parachuteInfoList;
                List<JettisonGhostInfo> jettisonInfoList;
                List<EngineGhostInfo> engineInfoList;
                List<DeployableGhostInfo> deployableInfoList;
                List<HeatGhostInfo> heatInfoList;
                List<LightGhostInfo> lightInfoList;
                List<FairingGhostInfo> fairingInfoList;
                List<RcsGhostInfo> rcsInfoList;
                List<RoboticGhostInfo> roboticInfoList;
                ghost = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    previewRecording, "Parsek_Ghost_Preview",
                    out parachuteInfoList, out jettisonInfoList, out engineInfoList,
                    out deployableInfoList, out heatInfoList, out lightInfoList,
                    out fairingInfoList, out rcsInfoList, out roboticInfoList);
                builtFromSnapshot = ghost != null;

                if (builtFromSnapshot)
                {
                    previewGhostState = new GhostPlaybackState
                    {
                        ghost = ghost,
                        playbackIndex = 0,
                        partEventIndex = 0,
                        partTree = GhostVisualBuilder.BuildPartSubtreeMap(
                            GhostVisualBuilder.GetGhostSnapshot(previewRecording))
                    };

                    previewGhostState.materials = new List<Material>();

                    if (parachuteInfoList != null)
                    {
                        previewGhostState.parachuteInfos = new Dictionary<uint, ParachuteGhostInfo>();
                        for (int i = 0; i < parachuteInfoList.Count; i++)
                            previewGhostState.parachuteInfos[parachuteInfoList[i].partPersistentId] = parachuteInfoList[i];
                    }
                    if (jettisonInfoList != null)
                    {
                        previewGhostState.jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
                        for (int i = 0; i < jettisonInfoList.Count; i++)
                            previewGhostState.jettisonInfos[jettisonInfoList[i].partPersistentId] = jettisonInfoList[i];
                    }
                    if (engineInfoList != null)
                    {
                        previewGhostState.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
                        for (int i = 0; i < engineInfoList.Count; i++)
                        {
                            ulong key = FlightRecorder.EncodeEngineKey(
                                engineInfoList[i].partPersistentId, engineInfoList[i].moduleIndex);
                            previewGhostState.engineInfos[key] = engineInfoList[i];
                        }
                    }
                    if (deployableInfoList != null)
                    {
                        previewGhostState.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
                        for (int i = 0; i < deployableInfoList.Count; i++)
                            previewGhostState.deployableInfos[deployableInfoList[i].partPersistentId] = deployableInfoList[i];
                    }
                    if (heatInfoList != null)
                    {
                        previewGhostState.heatInfos = new Dictionary<uint, HeatGhostInfo>();
                        for (int i = 0; i < heatInfoList.Count; i++)
                            previewGhostState.heatInfos[heatInfoList[i].partPersistentId] = heatInfoList[i];
                    }
                    if (lightInfoList != null)
                    {
                        previewGhostState.lightInfos = new Dictionary<uint, LightGhostInfo>();
                        previewGhostState.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();
                        for (int i = 0; i < lightInfoList.Count; i++)
                        {
                            previewGhostState.lightInfos[lightInfoList[i].partPersistentId] = lightInfoList[i];
                            previewGhostState.lightPlaybackStates[lightInfoList[i].partPersistentId] = new LightPlaybackState();
                        }
                    }
                    if (fairingInfoList != null)
                    {
                        previewGhostState.fairingInfos = new Dictionary<uint, FairingGhostInfo>();
                        for (int i = 0; i < fairingInfoList.Count; i++)
                            previewGhostState.fairingInfos[fairingInfoList[i].partPersistentId] = fairingInfoList[i];
                    }
                    if (rcsInfoList != null)
                    {
                        previewGhostState.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
                        for (int i = 0; i < rcsInfoList.Count; i++)
                        {
                            ulong key = FlightRecorder.EncodeEngineKey(
                                rcsInfoList[i].partPersistentId, rcsInfoList[i].moduleIndex);
                            previewGhostState.rcsInfos[key] = rcsInfoList[i];
                        }
                    }
                    if (roboticInfoList != null)
                    {
                        previewGhostState.roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
                        for (int i = 0; i < roboticInfoList.Count; i++)
                        {
                            ulong key = FlightRecorder.EncodeEngineKey(
                                roboticInfoList[i].partPersistentId, roboticInfoList[i].moduleIndex);
                            previewGhostState.roboticInfos[key] = roboticInfoList[i];
                        }
                    }

                    InitializeInventoryPlacementVisibility(previewRecording, previewGhostState);

                    previewGhostState.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                        ghost, previewGhostState.heatInfos, -1, previewRecording.VesselName);

                    Log("Manual preview ghost: built from recording-start snapshot (with part events)");
                }
            }

            if (!builtFromSnapshot)
            {
                Color previewColor = Color.green;
                ghost = CreateGhostSphere("Parsek_Ghost_Preview", previewColor);
                var m = ghost.GetComponent<Renderer>()?.material;
                previewGhostMaterials = m != null ? new List<Material> { m } : new List<Material>();
                previewGhostState = null;
                previewRecording = null;
                Log("Manual preview ghost: using sphere fallback");
            }
            else
            {
                previewGhostMaterials = previewGhostState.materials;
            }

            ghostObject = ghost;

            // Replay from current time (ghost starts at recording start position)
            playbackStartUT = Planetarium.GetUniversalTime();
            recordingStartUT = recording[0].ut;
            lastPlaybackIndex = 0;

            isPlaying = true;

            Log($"Manual playback started. Duration: {recording[recording.Count - 1].ut - recording[0].ut:F1}s");
            ScreenMessage("Preview Playback STARTED", 2f);
        }

        public void StopPlayback()
        {
            if (isPlaying)
                Log("Manual playback stopped");
            isPlaying = false;

            if (previewGhostState != null)
            {
                if (previewGhostState.materials != null)
                {
                    for (int i = 0; i < previewGhostState.materials.Count; i++)
                    {
                        if (previewGhostState.materials[i] != null)
                            Destroy(previewGhostState.materials[i]);
                    }
                }

                if (previewGhostState.engineInfos != null)
                {
                    foreach (var info in previewGhostState.engineInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                Destroy(info.particleSystems[i].gameObject);
                }

                if (previewGhostState.rcsInfos != null)
                {
                    foreach (var info in previewGhostState.rcsInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                Destroy(info.particleSystems[i].gameObject);
                }

                if (previewGhostState.reentryFxInfo != null && previewGhostState.reentryFxInfo.allClonedMaterials != null)
                {
                    for (int i = 0; i < previewGhostState.reentryFxInfo.allClonedMaterials.Count; i++)
                        if (previewGhostState.reentryFxInfo.allClonedMaterials[i] != null)
                            Destroy(previewGhostState.reentryFxInfo.allClonedMaterials[i]);
                }

                DestroyAllFakeCanopies(previewGhostState);
                previewGhostState = null;
            }
            else if (previewGhostMaterials != null)
            {
                // Sphere fallback path — no GhostPlaybackState
                for (int i = 0; i < previewGhostMaterials.Count; i++)
                {
                    if (previewGhostMaterials[i] != null)
                        Destroy(previewGhostMaterials[i]);
                }
            }
            previewGhostMaterials.Clear();
            previewRecording = null;

            if (ghostObject != null)
            {
                Destroy(ghostObject);
                ghostObject = null;
            }

            orbitCache.Clear();
            loggedOrbitSegments.Clear();
        }

        void UpdatePlayback()
        {
            if (ghostObject == null || recording.Count < 2)
            {
                StopPlayback();
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            double elapsedSinceStart = currentUT - playbackStartUT;
            double recordingTime = recordingStartUT + elapsedSinceStart;

            if (recordingTime > recording[recording.Count - 1].ut)
            {
                Log("Manual playback complete — reached end of recording");
                StopPlayback();
                ScreenMessage("Preview playback complete", 2f);
                return;
            }

            InterpolationResult interpResult;
            InterpolateAndPosition(ghostObject, recording, orbitSegments,
                ref lastPlaybackIndex, recordingTime, 10000, out interpResult);

            if (previewGhostState != null && previewRecording != null)
            {
                previewGhostState.SetInterpolated(interpResult);
                ApplyPartEvents(-1, previewRecording, recordingTime, previewGhostState);
                UpdateReentryFx(-1, previewGhostState, previewRecording.VesselName ?? "Preview");
            }
        }

        #endregion

        #region Timeline Auto-Playback

        void UpdateTimelinePlayback()
        {
            var committed = RecordingStore.CommittedRecordings;
            double currentUT = Planetarium.GetUniversalTime();

            // Prevent time warp from skipping ghost playback for vessel spawns
            if (committed.Count > 0 && IsAnyWarpActive() && lastTimelineUT >= 0
                && ParsekSettings.Current?.autoWarpStop != false)
            {
                double timelineStep = System.Math.Max(0, currentUT - lastTimelineUT);
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec.Points.Count < 2 && rec.OrbitSegments.Count == 0 && !rec.SurfacePos.HasValue) continue;
                    if (ShouldLoopPlayback(rec)) continue;
                    if (!rec.PlaybackEnabled) continue;
                    if (rec.VesselSnapshot == null || rec.VesselSpawned || rec.VesselDestroyed ||
                        RecordingStore.IsChainMidSegment(rec)) continue;
                    if (rec.ChildBranchPointId != null) continue;  // non-leaf, never spawns
                    if (rec.TerminalStateValue.HasValue)
                    {
                        var ts = rec.TerminalStateValue.Value;
                        if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
                            || ts == TerminalState.Docked || ts == TerminalState.Boarded)
                            continue;
                    }

                    bool crossedInto = lastTimelineUT < rec.StartUT && currentUT >= rec.StartUT;
                    bool approaching = currentUT < rec.StartUT &&
                                       rec.StartUT - currentUT <= System.Math.Max(1.0, timelineStep);
                    if (crossedInto || approaching)
                    {
                        if (warpStoppedForRecording.Add(i))
                        {
                            ExitAllWarpForPlaybackStart(rec.VesselName);
                            Log($"Stopped warp for recording #{i} ({rec.VesselName}) ghost playback");
                            ScreenMessage($"Time warp stopped — '{rec.VesselName}' playback", 3f);
                        }
                        break;
                    }
                }
            }
            lastTimelineUT = currentUT;

            if (committed.Count == 0) return;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                bool hasPoints = rec.Points.Count >= 2;
                bool hasOrbitData = rec.OrbitSegments.Count > 0;
                bool hasSurfaceData = rec.SurfacePos.HasValue;
                if (!hasPoints && !hasOrbitData && !hasSurfaceData) continue;

                bool inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;
                bool pastEnd = currentUT > rec.EndUT;
                GhostPlaybackState state;
                ghostStates.TryGetValue(i, out state);
                bool ghostActive = state != null && state.ghost != null;

                // Disabled segments: destroy ghost if active, still apply resource deltas
                if (!rec.PlaybackEnabled)
                {
                    if (ghostActive)
                    {
                        DestroyTimelineGhost(i);
                        ParsekLog.Verbose("Flight", $"Ghost #{i} destroyed — segment disabled " +
                            $"({rec.VesselName}, {RecordingStore.GetSegmentPhaseLabel(rec)})");
                    }
                    if (pastEnd && rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                    continue;
                }

                if (ShouldLoopPlayback(rec))
                {
                    UpdateLoopingTimelinePlayback(i, rec, currentUT, state, ghostActive);
                    continue;
                }

                bool isMidChain = RecordingStore.IsChainMidSegment(rec);
                double chainEndUT = isMidChain ? RecordingStore.GetChainEndUT(rec) : rec.EndUT;
                bool pastChainEnd = currentUT > chainEndUT;
                bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed && !rec.TakenControl;

                // Branch > 0 recordings are ghost-only (undock continuations) — never spawn
                if (needsSpawn && rec.ChainBranch > 0)
                {
                    needsSpawn = false;
                    if (loggedGhostEnter.Add(i + 200000))
                        ParsekLog.Verbose("Flight", $"Spawn suppressed for #{i} ({rec.VesselName}): branch > 0 (ghost-only)");
                }

                // Suppress spawning for recordings belonging to a chain currently being built.
                // Without this guard, CommitChainSegment commits the vessel segment mid-flight,
                // and UpdateTimelinePlayback would spawn it immediately (on top of the real vessel).
                if (needsSpawn && activeChainId != null && rec.ChainId == activeChainId)
                {
                    needsSpawn = false;
                    if (loggedGhostEnter.Add(i + 300000))
                        ParsekLog.Verbose("Flight", $"Spawn suppressed for #{i} ({rec.VesselName}): active chain being built");
                }

                // Suppress spawn for looping or fully-disabled chains
                if (needsSpawn && !string.IsNullOrEmpty(rec.ChainId))
                {
                    if (RecordingStore.IsChainLooping(rec.ChainId) ||
                        RecordingStore.IsChainFullyDisabled(rec.ChainId))
                        needsSpawn = false;
                }

                // Non-leaf tree recordings should never spawn (they branched into children)
                if (needsSpawn && rec.ChildBranchPointId != null)
                {
                    needsSpawn = false;
                    if (loggedGhostEnter.Add(i + 400000))
                        ParsekLog.Verbose("Flight", $"Spawn suppressed for #{i} ({rec.VesselName}): non-leaf tree recording");
                }

                // Terminal tree recordings (destroyed/recovered/docked/boarded) should not spawn
                if (needsSpawn && rec.TerminalStateValue.HasValue)
                {
                    var ts = rec.TerminalStateValue.Value;
                    if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
                        || ts == TerminalState.Docked || ts == TerminalState.Boarded)
                    {
                        needsSpawn = false;
                        if (loggedGhostEnter.Add(i + 500000))
                            ParsekLog.Verbose("Flight", $"Spawn suppressed for #{i} ({rec.VesselName}): terminal state {ts}");
                    }
                }

                // One-time chain spawn diagnostics when entering the spawn window
                if (pastChainEnd && !string.IsNullOrEmpty(rec.ChainId) && loggedGhostEnter.Add(i + 100000))
                    Log($"Chain spawn check #{i} \"{rec.VesselName}\": chainId={rec.ChainId}, idx={rec.ChainIndex}, " +
                        $"isMidChain={isMidChain}, hasSnapshot={rec.VesselSnapshot != null}, needsSpawn={needsSpawn}, " +
                        $"chainEndUT={chainEndUT:F1}, currentUT={currentUT:F1}");

                // Guard: if vessel was already spawned (PID recorded), never re-spawn.
                // On revert, SpawnedVesselPersistentId resets to 0 from quicksave so reverts still work.
                // Recovery/destruction sets TerminalState which blocks needsSpawn at the earlier check.
                if (needsSpawn && rec.SpawnedVesselPersistentId != 0)
                {
                    rec.VesselSpawned = true;
                    needsSpawn = false;
                    Log($"Vessel already spawned (pid={rec.SpawnedVesselPersistentId}) — skipping duplicate spawn for recording #{i}");
                }

                // Skip ghost playback entirely for recordings where player took control
                if (rec.TakenControl)
                {
                    if (ghostActive)
                        DestroyTimelineGhost(i);
                    continue;
                }

                if (inRange)
                {
                    if (hasPoints)
                    {
                        // Normal ghost playback (point-based, with optional orbit segments)
                        if (!ghostActive)
                        {
                            SpawnTimelineGhost(i, rec);
                            state = ghostStates[i];
                            if (loggedGhostEnter.Add(i))
                                Log($"Ghost ENTERED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1}");
                        }

                        int playbackIdx = state.playbackIndex;

                        InterpolationResult interpResult;
                        InterpolateAndPosition(state.ghost, rec.Points, rec.OrbitSegments,
                            ref playbackIdx, currentUT, i * 10000, out interpResult);
                        state.SetInterpolated(interpResult);
                        state.playbackIndex = playbackIdx;

                        ApplyPartEvents(i, rec, currentUT, state);
                        UpdateReentryFx(i, state, rec.VesselName);
                        if (rec.TreeId == null)
                            ApplyResourceDeltas(rec, currentUT);
                    }
                    else
                    {
                        // Background-only recording: orbit or surface positioning
                        if (!ghostActive)
                        {
                            SpawnTimelineGhost(i, rec);
                            state = ghostStates[i];
                        }
                        if (state != null && state.ghost != null)
                        {
                            if (hasOrbitData)
                                PositionGhostFromOrbitOnly(state.ghost, rec, currentUT, i * 10000);
                            else if (hasSurfaceData)
                                PositionGhostAtSurface(state.ghost, rec.SurfacePos.Value);
                        }
                        if (loggedGhostEnter.Add(i))
                            ParsekLog.Verbose("Flight", $"Ghost ENTERED range (background): #{i} \"{rec.VesselName}\" " +
                                $"orbit={hasOrbitData} surface={hasSurfaceData}");
                        ApplyPartEvents(i, rec, currentUT, state);
                        // Reentry FX: no InterpolationResult set for background-only path — bodyName stays null,
                        // so UpdateReentryFx will no-op. Intentional: on-rails vessels don't reenter.
                        UpdateReentryFx(i, state, rec.VesselName);
                        if (rec.TreeId == null)
                            ApplyResourceDeltas(rec, currentUT);
                    }
                }
                else if (pastChainEnd && needsSpawn && ghostActive)
                {
                    // Ghost was playing, UT crossed chain EndUT — spawn vessel, despawn ghost
                    Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} — spawning vessel");
                    if (rec.Points.Count > 0)
                        PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);

                    // Watch mode: exit and switch player to spawned vessel
                    if (watchedRecordingIndex == i)
                    {
                        ExitWatchMode();
                        VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                        DestroyTimelineGhost(i);
                        if (rec.SpawnedVesselPersistentId != 0)
                        {
                            ParsekLog.Info("CameraFollow",
                                $"Recording #{i} spawned vessel pid={rec.SpawnedVesselPersistentId} \u2014 switching active vessel");
                            StartCoroutine(DeferredActivateVessel(rec.SpawnedVesselPersistentId));
                        }
                    }
                    else
                    {
                        VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                        DestroyTimelineGhost(i);
                    }
                    if (rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastChainEnd && needsSpawn && !ghostActive)
                {
                    // UT already past chain EndUT on scene load — spawn immediately, no ghost
                    Log($"Ghost SKIPPED (UT already past EndUT): #{i} \"{rec.VesselName}\" at UT {currentUT:F1} " +
                        $"(EndUT={rec.EndUT:F1}) — spawning vessel immediately");
                    VesselSpawner.SpawnOrRecoverIfTooClose(rec, i);
                    if (rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
                else if (pastEnd && ghostActive && isMidChain && !pastChainEnd)
                {
                    // Mid-chain segment past its own EndUT but chain still playing — hold at final pos
                    if (rec.Points.Count > 0)
                        PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);
                    if (pastEnd && rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
                else
                {
                    // Outside time range, no spawn needed — despawn ghost if active
                    if (ghostActive)
                    {
                        // Watch mode: hold camera at last position for 3 seconds before destroying
                        if (watchedRecordingIndex == i)
                        {
                            if (watchEndHoldUntilUT < 0)
                            {
                                watchEndHoldUntilUT = Planetarium.GetUniversalTime() + 3.0;
                                ParsekLog.Info("CameraFollow",
                                    $"Recording #{i} ended \u2014 holding camera at last position until UT {watchEndHoldUntilUT.ToString("F1", CultureInfo.InvariantCulture)}");
                                // Keep ghost alive during hold — skip destroy
                                if (pastEnd && rec.TreeId == null)
                                    ApplyResourceDeltas(rec, currentUT);
                                continue;
                            }
                            else if (Planetarium.GetUniversalTime() < watchEndHoldUntilUT)
                            {
                                // Still holding — keep ghost alive
                                if (pastEnd && rec.TreeId == null)
                                    ApplyResourceDeltas(rec, currentUT);
                                continue;
                            }
                            else
                            {
                                // Hold expired — exit watch mode and destroy ghost
                                ParsekLog.Info("CameraFollow",
                                    $"Hold expired for recording #{i} \u2014 returning to active vessel");
                                ExitWatchMode();
                            }
                        }

                        Log($"Ghost EXITED range: #{i} \"{rec.VesselName}\" at UT {currentUT:F1} — no vessel to spawn");
                        DestroyTimelineGhost(i);
                    }
                    // Catch up resource deltas for recordings we jumped past
                    if (pastEnd && rec.TreeId == null)
                        ApplyResourceDeltas(rec, currentUT);
                }
            }

            // Apply tree-level resource deltas (lump sum when UT passes tree EndUT)
            ApplyTreeResourceDeltas(currentUT);

            // Watch mode: verify ghost still exists
            if (watchedRecordingIndex >= 0)
            {
                GhostPlaybackState ws;
                bool ghostOk = ghostStates.TryGetValue(watchedRecordingIndex, out ws)
                               && ws != null && ws.ghost != null;
                if (!ghostOk)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"Watched ghost #{watchedRecordingIndex} destroyed \u2014 auto-exiting watch mode");
                    ExitWatchMode();
                }
            }
        }

        private bool ShouldLoopPlayback(RecordingStore.Recording rec)
        {
            if (rec == null || !rec.LoopPlayback || rec.Points == null || rec.Points.Count < 2)
                return false;
            return rec.EndUT - rec.StartUT > MinLoopDurationSeconds;
        }

        private double GetLoopPauseSeconds(RecordingStore.Recording rec)
        {
            if (rec == null) return DefaultLoopPauseSeconds;
            if (double.IsNaN(rec.LoopPauseSeconds) || double.IsInfinity(rec.LoopPauseSeconds))
                return DefaultLoopPauseSeconds;
            return Math.Max(0.0, rec.LoopPauseSeconds);
        }

        private bool TryComputeLoopPlaybackUT(
            RecordingStore.Recording rec,
            double currentUT,
            out double loopUT,
            out int cycleIndex,
            out bool inPauseWindow)
        {
            loopUT = rec != null ? rec.StartUT : 0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (rec == null || rec.Points == null || rec.Points.Count < 2) return false;
            if (currentUT < rec.StartUT) return false;

            double duration = rec.EndUT - rec.StartUT;
            if (duration <= MinLoopDurationSeconds) return false;

            double pauseSeconds = GetLoopPauseSeconds(rec);
            double cycleDuration = duration + pauseSeconds;
            if (cycleDuration <= MinLoopDurationSeconds)
                cycleDuration = duration;

            double elapsed = currentUT - rec.StartUT;
            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0) cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (pauseSeconds > 0 && cycleTime > duration)
            {
                inPauseWindow = true;
                loopUT = rec.EndUT;
                return true;
            }

            loopUT = rec.StartUT + Math.Min(cycleTime, duration);
            return true;
        }

        private void UpdateLoopingTimelinePlayback(
            int recIdx,
            RecordingStore.Recording rec,
            double currentUT,
            GhostPlaybackState state,
            bool ghostActive)
        {
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;
            if (!TryComputeLoopPlaybackUT(rec, currentUT, out loopUT, out cycleIndex, out inPauseWindow))
            {
                if (ghostActive)
                    DestroyTimelineGhost(recIdx);
                return;
            }

            // Rebuild once per loop cycle to guarantee clean visual state and event indices.
            bool cycleChanged = !ghostActive || state == null || state.loopCycleIndex != cycleIndex;
            if (cycleChanged && ghostActive)
            {
                ResetReentryFx(state, recIdx);
                DestroyTimelineGhost(recIdx);
                ghostActive = false;
                state = null;
            }

            if (!ghostActive)
            {
                SpawnTimelineGhost(recIdx, rec);
                state = ghostStates[recIdx];
                state.loopCycleIndex = cycleIndex;
                if (loggedGhostEnter.Add(recIdx))
                    Log($"Ghost ENTERED range: #{recIdx} \"{rec.VesselName}\" at UT {currentUT:F1} (loop)");

                // Ghost was rebuilt for new loop cycle — re-target camera
                if (watchedRecordingIndex == recIdx && state.ghost != null)
                    FlightCamera.fetch.SetTargetTransform(state.ghost.transform);
            }

            if (state == null || state.ghost == null)
                return;

            if (inPauseWindow)
            {
                PositionGhostAt(state.ghost, rec.Points[rec.Points.Count - 1]);
                if (state != null && state.reentryFxInfo != null)
                {
                    // Only zero velocity — keep body/altitude from last frame for smooth intensity decay
                    state.lastInterpolatedVelocity = Vector3.zero;
                    UpdateReentryFx(recIdx, state, rec.VesselName);
                }
                return;
            }

            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;
            InterpolateAndPosition(state.ghost, rec.Points, rec.OrbitSegments,
                ref playbackIdx, loopUT, recIdx * 10000, out interpResult);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;

            ApplyPartEvents(recIdx, rec, loopUT, state);
            UpdateReentryFx(recIdx, state, rec.VesselName);
        }

        /// <summary>
        /// Apply resource deltas (funds, science, reputation) for recorded points
        /// that we've passed since the last frame. Computes delta between consecutive
        /// points and adds it to the current career pools.
        /// </summary>
        void ApplyResourceDeltas(RecordingStore.Recording rec, double currentUT)
        {
            if (ShouldPauseTimelineResourceReplay(IsRecording))
            {
                if (!timelineResourceReplayPausedLogged)
                {
                    Log("Timeline resource replay paused while a manual recording is active");
                    timelineResourceReplayPausedLogged = true;
                }
                return;
            }
            if (timelineResourceReplayPausedLogged)
            {
                Log("Timeline resource replay resumed after manual recording stopped");
                timelineResourceReplayPausedLogged = false;
            }

            var points = rec.Points;

            // Find the highest point index whose UT we've passed
            int targetIndex = ComputeTargetResourceIndex(points, rec.LastAppliedResourceIndex, currentUT);

            // Apply deltas for each point we've newly passed
            if (targetIndex > rec.LastAppliedResourceIndex)
            {
                int startIdx = Math.Max(rec.LastAppliedResourceIndex, 0);
                TrajectoryPoint fromPoint = points[startIdx];
                TrajectoryPoint toPoint = points[targetIndex];

                double fundsDelta = toPoint.funds - fromPoint.funds;
                float scienceDelta = toPoint.science - fromPoint.science;
                float repDelta = toPoint.reputation - fromPoint.reputation;

                // Suppress game state recording during replay — these are replayed
                // deltas from a previous recording, not new player actions
                GameStateRecorder.SuppressResourceEvents = true;
                try
                {
                    if (fundsDelta != 0 && Funding.Instance != null)
                    {
                        if (fundsDelta < 0 && Funding.Instance.Funds + fundsDelta < 0)
                            fundsDelta = -Funding.Instance.Funds;
                        Funding.Instance.AddFunds(fundsDelta, TransactionReasons.None);
                        Log($"Timeline resource: funds {fundsDelta:+0.0;-0.0}");
                    }

                    if (scienceDelta != 0 && ResearchAndDevelopment.Instance != null)
                    {
                        if (scienceDelta < 0 && ResearchAndDevelopment.Instance.Science + scienceDelta < 0)
                            scienceDelta = -ResearchAndDevelopment.Instance.Science;
                        ResearchAndDevelopment.Instance.AddScience(scienceDelta, TransactionReasons.None);
                        Log($"Timeline resource: science {scienceDelta:+0.0;-0.0}");
                    }

                    if (repDelta != 0 && Reputation.Instance != null)
                    {
                        if (repDelta < 0 && Reputation.CurrentRep + repDelta < 0)
                            repDelta = -Reputation.CurrentRep;
                        Reputation.Instance.AddReputation(repDelta, TransactionReasons.None);
                        Log($"Timeline resource: reputation {repDelta:+0.0;-0.0}");
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressResourceEvents = false;
                }

                rec.LastAppliedResourceIndex = targetIndex;
                ResourceBudget.Invalidate();

                // Log summary when all resource deltas have been applied
                if (targetIndex == points.Count - 1 && startIdx < targetIndex)
                {
                    Log($"Resource deltas complete for \"{rec.VesselName}\" (recorded totals): " +
                        $"funds={toPoint.funds - points[0].funds:+0.0;-0.0}, " +
                        $"science={toPoint.science - points[0].science:+0.0;-0.0}, " +
                        $"rep={toPoint.reputation - points[0].reputation:+0.0;-0.0}");
                }
            }
        }

        void ApplyTreeResourceDeltas(double currentUT)
        {
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree.ResourcesApplied)
                {
                    ParsekLog.VerboseRateLimited("Flight", "tree-res-skip-applied",
                        $"ApplyTreeResourceDeltas: skipping tree '{tree.TreeName}' — already applied");
                    continue;
                }

                // Compute tree EndUT: max EndUT across all recordings
                double treeEndUT = 0;
                foreach (var rec in tree.Recordings.Values)
                {
                    double recEnd = rec.EndUT;
                    if (recEnd > treeEndUT) treeEndUT = recEnd;
                }

                if (currentUT <= treeEndUT)
                {
                    ParsekLog.VerboseRateLimited("Flight", "tree-res-wait-ut",
                        $"ApplyTreeResourceDeltas: waiting for tree '{tree.TreeName}' — currentUT={currentUT:F1} treeEndUT={treeEndUT:F1}");
                    continue;
                }

                // Apply lump sum delta (sets ResourcesApplied = true internally)
                ApplyTreeLumpSum(tree);
            }
        }

        void ApplyTreeLumpSum(RecordingTree tree)
        {
            if (ShouldPauseTimelineResourceReplay(IsRecording))
                return; // retry next frame

            GameStateRecorder.SuppressResourceEvents = true;
            try
            {
                if (tree.DeltaFunds != 0 && Funding.Instance != null)
                {
                    double delta = tree.DeltaFunds;
                    double originalFunds = delta;
                    if (delta < 0 && Funding.Instance.Funds + delta < 0)
                        delta = -Funding.Instance.Funds;
                    if (delta != originalFunds)
                        Log($"ApplyTreeLumpSum: funds clamped for '{tree.TreeName}': original={originalFunds:+0.0;-0.0} → clamped={delta:+0.0;-0.0} (balance={Funding.Instance.Funds:F0})");
                    Funding.Instance.AddFunds(delta, TransactionReasons.None);
                    Log($"Tree resource: funds {delta:+0.0;-0.0} (tree '{tree.TreeName}')");
                }

                if (tree.DeltaScience != 0 && ResearchAndDevelopment.Instance != null)
                {
                    double delta = tree.DeltaScience;
                    double originalScience = delta;
                    if (delta < 0 && ResearchAndDevelopment.Instance.Science + delta < 0)
                        delta = -ResearchAndDevelopment.Instance.Science;
                    if (delta != originalScience)
                        Log($"ApplyTreeLumpSum: science clamped for '{tree.TreeName}': original={originalScience:+0.0;-0.0} → clamped={delta:+0.0;-0.0} (balance={ResearchAndDevelopment.Instance.Science:F1})");
                    ResearchAndDevelopment.Instance.AddScience((float)delta, TransactionReasons.None);
                    Log($"Tree resource: science {delta:+0.0;-0.0} (tree '{tree.TreeName}')");
                }

                if (tree.DeltaReputation != 0 && Reputation.Instance != null)
                {
                    float delta = tree.DeltaReputation;
                    float originalRep = delta;
                    if (delta < 0 && Reputation.CurrentRep + delta < 0)
                        delta = -Reputation.CurrentRep;
                    if (delta != originalRep)
                        Log($"ApplyTreeLumpSum: reputation clamped for '{tree.TreeName}': original={originalRep:+0.0;-0.0} → clamped={delta:+0.0;-0.0} (balance={Reputation.CurrentRep:F1})");
                    Reputation.Instance.AddReputation(delta, TransactionReasons.None);
                    Log($"Tree resource: reputation {delta:+0.0;-0.0} (tree '{tree.TreeName}')");
                }
            }
            finally
            {
                GameStateRecorder.SuppressResourceEvents = false;
            }

            tree.ResourcesApplied = true;
            ResourceBudget.Invalidate();
            Log($"Tree resource lump sum applied for '{tree.TreeName}'");
        }

        RecordingTree FindCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == treeId) return trees[i];
            }
            Log($"FindCommittedTree: treeId='{treeId}' not found (checked {trees.Count} committed trees)");
            return null;
        }

        void SpawnTimelineGhost(int index, RecordingStore.Recording rec)
        {
            Log($"Spawning timeline ghost #{index} for {rec.VesselName}");

            // Use a slightly different color to distinguish from manual preview
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            List<ParachuteGhostInfo> parachuteInfoList;
            List<JettisonGhostInfo> jettisonInfoList;
            List<EngineGhostInfo> engineInfoList;
            List<DeployableGhostInfo> deployableInfoList;
            List<HeatGhostInfo> heatInfoList;
            List<LightGhostInfo> lightInfoList;
            List<FairingGhostInfo> fairingInfoList;
            List<RcsGhostInfo> rcsInfoList;
            List<RoboticGhostInfo> roboticInfoList;
            GameObject ghost = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                rec, $"Parsek_Timeline_{index}", out parachuteInfoList, out jettisonInfoList,
                out engineInfoList, out deployableInfoList, out heatInfoList, out lightInfoList, out fairingInfoList,
                out rcsInfoList, out roboticInfoList);
            bool builtFromSnapshot = ghost != null;
            if (ghost == null)
            {
                ghost = CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);
                Log($"Timeline ghost #{index}: using sphere fallback");
            }
            else
            {
                bool usedStartSnapshot = rec.GhostVisualSnapshot != null;
                Log(usedStartSnapshot
                    ? $"Timeline ghost #{index}: built from recording-start snapshot"
                    : $"Timeline ghost #{index}: built from vessel snapshot");
            }

            var state = new GhostPlaybackState
            {
                ghost = ghost,
                playbackIndex = 0,
                partEventIndex = 0,
                partTree = GhostVisualBuilder.BuildPartSubtreeMap(GhostVisualBuilder.GetGhostSnapshot(rec))
            };

            if (builtFromSnapshot)
            {
                state.materials = new List<Material>();
            }
            else
            {
                var m = ghost.GetComponent<Renderer>()?.material;
                state.materials = m != null ? new List<Material> { m } : new List<Material>();
            }

            if (parachuteInfoList != null)
            {
                state.parachuteInfos = new Dictionary<uint, ParachuteGhostInfo>();
                for (int i = 0; i < parachuteInfoList.Count; i++)
                    state.parachuteInfos[parachuteInfoList[i].partPersistentId] = parachuteInfoList[i];
            }

            if (jettisonInfoList != null)
            {
                state.jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
                for (int i = 0; i < jettisonInfoList.Count; i++)
                    state.jettisonInfos[jettisonInfoList[i].partPersistentId] = jettisonInfoList[i];
            }

            if (engineInfoList != null)
            {
                state.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
                for (int i = 0; i < engineInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        engineInfoList[i].partPersistentId, engineInfoList[i].moduleIndex);
                    state.engineInfos[key] = engineInfoList[i];
                }
            }

            if (deployableInfoList != null)
            {
                state.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
                for (int i = 0; i < deployableInfoList.Count; i++)
                    state.deployableInfos[deployableInfoList[i].partPersistentId] = deployableInfoList[i];
            }

            if (heatInfoList != null)
            {
                state.heatInfos = new Dictionary<uint, HeatGhostInfo>();
                for (int i = 0; i < heatInfoList.Count; i++)
                    state.heatInfos[heatInfoList[i].partPersistentId] = heatInfoList[i];
            }

            if (lightInfoList != null)
            {
                state.lightInfos = new Dictionary<uint, LightGhostInfo>();
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();
                for (int i = 0; i < lightInfoList.Count; i++)
                {
                    state.lightInfos[lightInfoList[i].partPersistentId] = lightInfoList[i];
                    state.lightPlaybackStates[lightInfoList[i].partPersistentId] = new LightPlaybackState();
                }
            }

            if (fairingInfoList != null)
            {
                state.fairingInfos = new Dictionary<uint, FairingGhostInfo>();
                for (int i = 0; i < fairingInfoList.Count; i++)
                    state.fairingInfos[fairingInfoList[i].partPersistentId] = fairingInfoList[i];
            }

            if (rcsInfoList != null)
            {
                state.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
                for (int i = 0; i < rcsInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        rcsInfoList[i].partPersistentId, rcsInfoList[i].moduleIndex);
                    state.rcsInfos[key] = rcsInfoList[i];
                }
            }

            if (roboticInfoList != null)
            {
                state.roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
                for (int i = 0; i < roboticInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        roboticInfoList[i].partPersistentId, roboticInfoList[i].moduleIndex);
                    state.roboticInfos[key] = roboticInfoList[i];
                }
            }

            InitializeInventoryPlacementVisibility(rec, state);

            state.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                ghost,
                state.heatInfos,
                index,
                rec.VesselName);

            ghostStates[index] = state;
        }

        internal void DestroyTimelineGhost(int index)
        {
            Log($"Despawning timeline ghost #{index}");

            GhostPlaybackState state;
            if (!ghostStates.TryGetValue(index, out state))
                return;

            if (state.materials != null)
            {
                for (int i = 0; i < state.materials.Count; i++)
                {
                    if (state.materials[i] != null)
                        Destroy(state.materials[i]);
                }
            }

            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            Destroy(info.particleSystems[i].gameObject);
            }

            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                    for (int i = 0; i < info.particleSystems.Count; i++)
                        if (info.particleSystems[i] != null)
                            Destroy(info.particleSystems[i].gameObject);
            }

            if (state.reentryFxInfo != null && state.reentryFxInfo.allClonedMaterials != null)
            {
                for (int i = 0; i < state.reentryFxInfo.allClonedMaterials.Count; i++)
                    if (state.reentryFxInfo.allClonedMaterials[i] != null)
                        Destroy(state.reentryFxInfo.allClonedMaterials[i]);
            }

            if (state.ghost != null)
                Destroy(state.ghost);

            DestroyAllFakeCanopies(state);
            ghostStates.Remove(index);
        }

        public void DestroyAllTimelineGhosts()
        {
            // Copy keys to avoid modifying during iteration
            var keys = new List<int>(ghostStates.Keys);
            foreach (int key in keys)
            {
                DestroyTimelineGhost(key);
            }
            orbitCache.Clear();
            loggedGhostEnter.Clear();
            loggedOrbitSegments.Clear();
            warpStoppedForRecording.Clear();
        }

        public bool CanDeleteRecording =>
            !IsRecording && continuationRecordingIdx < 0 && undockContinuationRecIdx < 0;

        public void DeleteRecording(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count)
            {
                ParsekLog.Warn("Flight", $"DeleteRecording ignored: index={index} out of range (count={committed.Count})");
                return;
            }

            if (!CanDeleteRecording)
            {
                ParsekLog.Warn("Flight",
                    $"DeleteRecording blocked: index={index}, isRecording={IsRecording}, " +
                    $"continuationRecordingIdx={continuationRecordingIdx}, undockContinuationRecIdx={undockContinuationRecIdx}");
                return;
            }

            var rec = committed[index];
            Log($"Deleting recording '{rec.VesselName}' at index {index}");

            // Unreserve crew
            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);

            // Destroy ghost if active
            DestroyTimelineGhost(index);

            // Remove from store (handles chain degradation + file deletion)
            RecordingStore.RemoveRecordingAt(index);

            // Update watch mode index if watching a ghost
            if (watchedRecordingIndex >= 0)
            {
                var result = ComputeWatchIndexAfterDelete(
                    watchedRecordingIndex, watchedRecordingId, index,
                    RecordingStore.CommittedRecordings);
                if (result.newIndex < 0)
                {
                    ParsekLog.Warn("CameraFollow",
                        $"Watched recording \"{watchedRecordingId}\" deleted \u2014 auto-exiting watch mode");
                    ExitWatchMode();
                }
                else
                {
                    int oldIdx = watchedRecordingIndex;
                    watchedRecordingIndex = result.newIndex;
                    watchedRecordingId = result.newId;
                    if (oldIdx != result.newIndex)
                        ParsekLog.Info("CameraFollow",
                            $"Recording deleted at #{index} \u2014 watchedRecordingIndex adjusted from {oldIdx} to {result.newIndex}");
                }
            }

            // Rebuild ghost state keys — indices above the removed one shift down by 1
            var oldStates = new Dictionary<int, GhostPlaybackState>(ghostStates);
            ghostStates.Clear();
            foreach (var kvp in oldStates)
            {
                if (kvp.Key < index)
                    ghostStates[kvp.Key] = kvp.Value;
                else if (kvp.Key > index)
                    ghostStates[kvp.Key - 1] = kvp.Value;
                // kvp.Key == index was already removed by DestroyTimelineGhost
            }

            // Clear orbit cache — keys are index-derived (i * 10000 + segIdx),
            // so after reindexing they would map to wrong recordings
            orbitCache.Clear();

            // Clear diagnostic guards since indices shifted
            loggedGhostEnter.Clear();
            loggedOrbitSegments.Clear();
            warpStoppedForRecording.Clear();

            ParsekLog.ScreenMessage($"Recording '{rec.VesselName}' deleted", 2f);
        }

        void ApplyPartEvents(int recIdx, RecordingStore.Recording rec, double currentUT, GhostPlaybackState state)
        {
            if (rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state.ghost == null)
            {
                ParsekLog.VerboseRateLimited("Flight", $"apply-part-events-null-ghost-{recIdx}",
                    $"ApplyPartEvents: ghost is null for recording #{recIdx}");
                return;
            }

            int evtIdx = state.partEventIndex;
            var tree = state.partTree;
            var ghost = state.ghost;

            while (evtIdx < rec.PartEvents.Count && rec.PartEvents[evtIdx].ut <= currentUT)
            {
                var evt = rec.PartEvents[evtIdx];
                switch (evt.eventType)
                {
                    case PartEventType.Decoupled:
                        if (tree != null)
                            HidePartSubtree(ghost, evt.partPersistentId, tree);
                        else
                            HideGhostPart(ghost, evt.partPersistentId);
                        Log($"Part event applied: Decoupled '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.Destroyed:
                        HideGhostPart(ghost, evt.partPersistentId);
                        Log($"Part event applied: Destroyed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ParachuteCut:
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo cutInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out cutInfo))
                            {
                                if (cutInfo.canopyTransform != null)
                                    cutInfo.canopyTransform.localScale = Vector3.zero;
                                if (cutInfo.capTransform != null)
                                    cutInfo.capTransform.gameObject.SetActive(false);
                            }
                        }
                        DestroyFakeCanopy(state, evt.partPersistentId);
                        Log($"Part event: ParachuteCut '{evt.partName}' — canopy hidden, housing remains");
                        break;
                    case PartEventType.ShroudJettisoned:
                        ApplyJettisonPanelState(state, evt, jettisoned: true);
                        Log($"Part event applied: ShroudJettisoned '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ParachuteDestroyed:
                        // Clean up canopy visuals before hiding the part
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo destroyedInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out destroyedInfo))
                            {
                                if (destroyedInfo.canopyTransform != null)
                                    destroyedInfo.canopyTransform.localScale = Vector3.zero;
                            }
                        }
                        DestroyFakeCanopy(state, evt.partPersistentId);
                        HideGhostPart(ghost, evt.partPersistentId);
                        Log($"Part event applied: ParachuteDestroyed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ParachuteSemiDeployed:
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo semiInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out semiInfo) &&
                                semiInfo.canopyTransform != null && semiInfo.semiDeployedSampled)
                            {
                                semiInfo.canopyTransform.localScale = semiInfo.semiDeployedCanopyScale;
                                semiInfo.canopyTransform.localPosition = semiInfo.semiDeployedCanopyPos;
                                semiInfo.canopyTransform.localRotation = semiInfo.semiDeployedCanopyRot;
                                if (semiInfo.capTransform != null)
                                    semiInfo.capTransform.gameObject.SetActive(false);
                                Log($"Part event: ParachuteSemiDeployed '{evt.partName}' — streamer canopy shown");
                            }
                            else
                            {
                                Log($"Part event: ParachuteSemiDeployed '{evt.partName}' — no semi-deployed state sampled, skipping");
                            }
                        }
                        break;
                    case PartEventType.ParachuteDeployed:
                        bool usedRealCanopy = false;

                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo info;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out info) && info.canopyTransform != null)
                            {
                                info.canopyTransform.localScale = info.deployedCanopyScale;
                                info.canopyTransform.localPosition = info.deployedCanopyPos;
                                info.canopyTransform.localRotation = info.deployedCanopyRot;
                                if (info.capTransform != null)
                                    info.capTransform.gameObject.SetActive(false);
                                usedRealCanopy = true;
                                Log($"Part event: ParachuteDeployed '{evt.partName}' — real canopy deployed");
                            }
                        }

                        if (!usedRealCanopy)
                        {
                            var canopy = GhostVisualBuilder.CreateFakeCanopy(ghost, evt.partPersistentId);
                            if (canopy != null)
                            {
                                TrackFakeCanopy(state, evt.partPersistentId, canopy);
                                Log($"Part event: ParachuteDeployed '{evt.partName}' — fake canopy (fallback)");
                            }
                            else
                            {
                                Log($"Part event: ParachuteDeployed '{evt.partName}' — could not create canopy");
                            }
                        }
                        break;
                    case PartEventType.EngineIgnited:
                        SetEngineEmission(state, evt, evt.value);
                        Log($"Part event applied: EngineIgnited '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} throttle={evt.value:F2}");
                        break;
                    case PartEventType.EngineShutdown:
                        SetEngineEmission(state, evt, 0f);
                        Log($"Part event applied: EngineShutdown '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex}");
                        break;
                    case PartEventType.EngineThrottle:
                        SetEngineEmission(state, evt, evt.value);
                        break;
                    case PartEventType.DeployableExtended:
                        ApplyDeployableState(state, evt, deployed: true);
                        Log($"Part event applied: DeployableExtended '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.DeployableRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        Log($"Part event applied: DeployableRetracted '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.ThermalAnimationHot:
                        ApplyHeatState(state, evt, heated: true);
                        Log($"Part event applied: ThermalAnimationHot '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} heat={evt.value:F2}");
                        break;
                    case PartEventType.ThermalAnimationCold:
                        ApplyHeatState(state, evt, heated: false);
                        Log($"Part event applied: ThermalAnimationCold '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} heat={evt.value:F2}");
                        break;
                    case PartEventType.LightOn:
                        ApplyLightPowerEvent(state, evt.partPersistentId, true);
                        Log($"Part event applied: LightOn '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.LightOff:
                        ApplyLightPowerEvent(state, evt.partPersistentId, false);
                        Log($"Part event applied: LightOff '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.LightBlinkEnabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: true, evt.value);
                        Log($"Part event applied: LightBlinkEnabled '{evt.partName}' pid={evt.partPersistentId} rate={evt.value:F2}");
                        break;
                    case PartEventType.LightBlinkDisabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: false, evt.value);
                        Log($"Part event applied: LightBlinkDisabled '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.LightBlinkRate:
                        ApplyLightBlinkRateEvent(state, evt.partPersistentId, evt.value);
                        Log($"Part event applied: LightBlinkRate '{evt.partName}' pid={evt.partPersistentId} rate={evt.value:F2}");
                        break;
                    case PartEventType.GearDeployed:
                        ApplyDeployableState(state, evt, deployed: true);
                        Log($"Part event applied: GearDeployed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.GearRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        Log($"Part event applied: GearRetracted '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.CargoBayOpened:
                        if (!ApplyDeployableState(state, evt, deployed: true))
                            ApplyJettisonPanelState(state, evt, jettisoned: true);
                        Log($"Part event applied: CargoBayOpened '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.CargoBayClosed:
                        if (!ApplyDeployableState(state, evt, deployed: false))
                            ApplyJettisonPanelState(state, evt, jettisoned: false);
                        Log($"Part event applied: CargoBayClosed '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.FairingJettisoned:
                        if (state.fairingInfos != null)
                        {
                            FairingGhostInfo fInfo;
                            if (state.fairingInfos.TryGetValue(evt.partPersistentId, out fInfo)
                                && fInfo.fairingMeshObject != null)
                            {
                                fInfo.fairingMeshObject.SetActive(false);
                                Log($"Part event applied: FairingJettisoned '{evt.partName}' pid={evt.partPersistentId}");
                            }
                        }
                        break;
                    case PartEventType.RCSActivated:
                        SetRcsEmission(state, evt, evt.value);
                        Log($"Part event applied: RCSActivated '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} power={evt.value:F2}");
                        break;
                    case PartEventType.RCSStopped:
                        SetRcsEmission(state, evt, 0f);
                        Log($"Part event applied: RCSStopped '{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex}");
                        break;
                    case PartEventType.RCSThrottle:
                        SetRcsEmission(state, evt, evt.value);
                        break;
                    case PartEventType.RoboticMotionStarted:
                    case PartEventType.RoboticPositionSample:
                    case PartEventType.RoboticMotionStopped:
                        ApplyRoboticEvent(state, evt, currentUT);
                        break;
                    case PartEventType.InventoryPartPlaced:
                        SetGhostPartActive(ghost, evt.partPersistentId, true);
                        Log($"Part event applied: InventoryPartPlaced '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                    case PartEventType.InventoryPartRemoved:
                        SetGhostPartActive(ghost, evt.partPersistentId, false);
                        Log($"Part event applied: InventoryPartRemoved '{evt.partName}' pid={evt.partPersistentId}");
                        break;
                }
                evtIdx++;
            }

            state.partEventIndex = evtIdx;
            UpdateBlinkingLights(state, currentUT);
            UpdateActiveRobotics(state, currentUT);
        }

        static void HideGhostPart(GameObject ghost, uint persistentId)
        {
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(false);
        }

        static void SetGhostPartActive(GameObject ghost, uint persistentId, bool active)
        {
            if (ghost == null) return;
            var t = ghost.transform.Find($"ghost_part_{persistentId}");
            if (t != null) t.gameObject.SetActive(active);
        }

        static void InitializeInventoryPlacementVisibility(
            RecordingStore.Recording rec, GhostPlaybackState state)
        {
            if (rec == null || rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state == null || state.ghost == null) return;

            // If a part's first placement-related event is "placed", start hidden so it
            // visibly appears only when the event fires.
            var initialized = new HashSet<uint>();
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                var evt = rec.PartEvents[i];
                if (initialized.Contains(evt.partPersistentId)) continue;

                if (evt.eventType == PartEventType.InventoryPartPlaced)
                {
                    SetGhostPartActive(state.ghost, evt.partPersistentId, false);
                    initialized.Add(evt.partPersistentId);
                }
                else if (evt.eventType == PartEventType.InventoryPartRemoved)
                {
                    SetGhostPartActive(state.ghost, evt.partPersistentId, true);
                    initialized.Add(evt.partPersistentId);
                }
            }
        }

        void TrackFakeCanopy(GhostPlaybackState state, uint partPid, GameObject canopy)
        {
            if (state.fakeCanopies == null)
                state.fakeCanopies = new Dictionary<uint, GameObject>();
            // Destroy previous canopy for this part if one exists (prevents leak)
            GameObject existing;
            if (state.fakeCanopies.TryGetValue(partPid, out existing) && existing != null)
                DestroyCanopyAndMaterial(existing);
            state.fakeCanopies[partPid] = canopy;
        }

        void DestroyFakeCanopy(GhostPlaybackState state, uint partPid)
        {
            if (state.fakeCanopies == null) return;
            GameObject canopy;
            if (state.fakeCanopies.TryGetValue(partPid, out canopy) && canopy != null)
                DestroyCanopyAndMaterial(canopy);
            state.fakeCanopies.Remove(partPid);
        }

        void DestroyAllFakeCanopies(GhostPlaybackState state)
        {
            if (state.fakeCanopies == null) return;
            foreach (var kv in state.fakeCanopies)
                if (kv.Value != null) DestroyCanopyAndMaterial(kv.Value);
            state.fakeCanopies = null;
        }

        static void DestroyCanopyAndMaterial(GameObject canopy)
        {
            var renderer = canopy.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
                Destroy(renderer.material);
            Destroy(canopy);
        }

        internal static string BuildEngineFxEmissionDiagnostic(
            string partName,
            uint partPersistentId,
            int moduleIndex,
            float power,
            string particleName,
            string parentName,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 worldPosition,
            Vector3 worldForward,
            Vector3 worldUp,
            float emissionRate,
            float startSpeed,
            bool isPlaying)
        {
            string safePartName = string.IsNullOrEmpty(partName) ? "<unknown>" : partName;
            string safeParticleName = string.IsNullOrEmpty(particleName) ? "<unknown>" : particleName;
            string safeParentName = string.IsNullOrEmpty(parentName) ? "<none>" : parentName;
            string localRotationRaw =
                $"({localRotation.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.z.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.w.ToString("F4", CultureInfo.InvariantCulture)})";

            return $"Engine FX emission diag: part='{safePartName}' pid={partPersistentId} midx={moduleIndex} " +
                $"power={power.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"ps='{safeParticleName}' parent='{safeParentName}' " +
                $"localPos={FormatVector3Invariant(localPosition)} localRot={localRotationRaw} " +
                $"worldPos={FormatVector3Invariant(worldPosition)} " +
                $"worldFwd={FormatVector3Invariant(worldForward)} " +
                $"worldUp={FormatVector3Invariant(worldUp)} " +
                $"rate={emissionRate.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"speed={startSpeed.ToString("F2", CultureInfo.InvariantCulture)} playing={isPlaying}";
        }

        private static string FormatVector3Invariant(Vector3 value)
        {
            return $"({value.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.z.ToString("F4", CultureInfo.InvariantCulture)})";
        }

        static void SetEngineEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.engineInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            EngineGhostInfo info;
            if (!state.engineInfos.TryGetValue(key, out info)) return;

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                var emission = ps.emission;
                if (power > 0f)
                {
                    emission.enabled = true;
                    float emRate = info.emissionCurve != null ? info.emissionCurve.Evaluate(power) : power * 100f;
                    emission.rateOverTimeMultiplier = emRate;

                    var main = ps.main;
                    float spd = info.speedCurve != null ? info.speedCurve.Evaluate(power) : power * 10f;
                    main.startSpeedMultiplier = spd;

                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();
                }
                else
                {
                    emission.enabled = false;
                    emission.rateOverTimeMultiplier = 0;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

                if (ParsekLog.IsVerboseEnabled)
                {
                    Transform t = ps.transform;
                    string parentName = t != null && t.parent != null ? t.parent.name : "<none>";
                    var diagMain = ps.main;
                    string diagLine = BuildEngineFxEmissionDiagnostic(
                        evt.partName,
                        evt.partPersistentId,
                        evt.moduleIndex,
                        power,
                        ps.name,
                        parentName,
                        t != null ? t.localPosition : Vector3.zero,
                        t != null ? t.localRotation : Quaternion.identity,
                        t != null ? t.position : Vector3.zero,
                        t != null ? t.forward : Vector3.zero,
                        t != null ? t.up : Vector3.zero,
                        emission.rateOverTimeMultiplier,
                        diagMain.startSpeedMultiplier,
                        ps.isPlaying);
                    string diagKey = $"engine-fx-{evt.partPersistentId}-{evt.moduleIndex}-{ps.GetInstanceID()}";
                    ParsekLog.VerboseRateLimited("Flight", diagKey, diagLine, 0.5);
                }
            }
        }

        static void SetParticleRenderersEnabled(ParticleSystem ps, bool enabled)
        {
            if (ps == null)
                return;

            ParticleSystemRenderer[] renderers = ps.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = enabled;
            }
        }

        static void SetRcsEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.rcsInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            RcsGhostInfo info;
            if (!state.rcsInfos.TryGetValue(key, out info)) return;

            int configuredSystems = 0;
            int enabledRenderers = 0;
            int playingSystems = 0;
            float sampleRate = 0f;
            float sampleSpeed = 0f;
            float sampleSize = 0f;
            float sampleLifetime = 0f;

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                configuredSystems++;
                var emission = ps.emission;
                if (power > 0f)
                {
                    emission.enabled = true;
                    float emRate = ComputeScaledRcsEmissionRate(info.emissionCurve, power, info.emissionScale);
                    emission.rateOverTimeMultiplier = emRate;

                    var main = ps.main;
                    float spd = ComputeScaledRcsSpeed(info.speedCurve, power, info.speedScale);
                    main.startSpeedMultiplier = spd;

                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();

                    if (sampleRate <= 0f)
                    {
                        sampleRate = emRate;
                        sampleSpeed = spd;
                        sampleSize = main.startSizeMultiplier;
                        sampleLifetime = main.startLifetimeMultiplier;
                    }
                }
                else
                {
                    emission.enabled = false;
                    emission.rateOverTimeMultiplier = 0;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

                if (ps.isPlaying) playingSystems++;
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.enabled) enabledRenderers++;
            }

            if (info.emissionScale > 1f)
            {
                ParsekLog.Verbose("Flight", $"RCS showcase diagnostics: part='{evt.partName}' pid={evt.partPersistentId} midx={evt.moduleIndex} " +
                    $"power={power:F2} systems={configuredSystems} playing={playingSystems} renderers={enabledRenderers} " +
                    $"rate={sampleRate:F1} speed={sampleSpeed:F1} size={sampleSize:F2} life={sampleLifetime:F2}");
            }
        }

        internal static float ComputeScaledRcsEmissionRate(
            FloatCurve emissionCurve, float power, float emissionScale)
        {
            if (power <= 0f) return 0f;

            float emRate = emissionCurve != null ? emissionCurve.Evaluate(power) : power * 100f;
            emRate *= emissionScale > 0f ? emissionScale : 1f;
            if (emissionScale > 1f)
                emRate = Math.Max(emRate, 60f);

            return emRate;
        }

        internal static float ComputeScaledRcsSpeed(
            FloatCurve speedCurve, float power, float speedScale)
        {
            if (power <= 0f) return 0f;

            float spd = speedCurve != null ? speedCurve.Evaluate(power) : power * 10f;
            spd *= speedScale > 0f ? speedScale : 1f;
            if (speedScale > 1f)
                spd = Math.Max(spd, 4f);

            return spd;
        }

        internal static float ComputeRotorDeltaDegrees(float rpm, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                return 0f;
            if (float.IsNaN(rpm) || float.IsInfinity(rpm) || Mathf.Abs(rpm) <= 0.0001f)
                return 0f;

            // RPM * 360deg / 60s
            return rpm * 6f * (float)deltaSeconds;
        }

        private static void ApplyRoboticPose(RoboticGhostInfo info, float value)
        {
            if (info == null || info.servoTransform == null)
                return;

            Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                ? info.axisLocal.normalized
                : Vector3.up;

            if (info.visualMode == RoboticVisualMode.Linear)
            {
                info.servoTransform.localPosition = info.stowedPos + (axis * value);
            }
            else if (info.visualMode == RoboticVisualMode.Rotational)
            {
                info.servoTransform.localRotation =
                    info.stowedRot * Quaternion.AngleAxis(value, axis);
            }
        }

        private static void ApplyRoboticEvent(
            GhostPlaybackState state, PartEvent evt, double currentUT)
        {
            if (state == null || state.roboticInfos == null)
                return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            if (!state.roboticInfos.TryGetValue(key, out RoboticGhostInfo info) || info == null)
                return;

            info.currentValue = evt.value;

            if (info.visualMode == RoboticVisualMode.RotorRpm)
            {
                info.active = evt.eventType != PartEventType.RoboticMotionStopped &&
                    Mathf.Abs(evt.value) > 0.0001f;
                info.lastUpdateUT = currentUT;
            }
            else
            {
                ApplyRoboticPose(info, evt.value);
                info.active = evt.eventType != PartEventType.RoboticMotionStopped;
                info.lastUpdateUT = currentUT;
            }
        }

        private void UpdateActiveRobotics(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.roboticInfos == null || state.roboticInfos.Count == 0)
                return;

            foreach (var kv in state.roboticInfos)
            {
                RoboticGhostInfo info = kv.Value;
                if (info == null || info.servoTransform == null)
                    continue;

                if (double.IsNaN(info.lastUpdateUT) || double.IsInfinity(info.lastUpdateUT))
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                double deltaSeconds = currentUT - info.lastUpdateUT;
                if (deltaSeconds <= 0)
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                // Timeline jumps/loop boundaries rebuild ghosts, but guard large UT gaps anyway.
                deltaSeconds = Math.Min(deltaSeconds, 1.0);

                if (info.visualMode == RoboticVisualMode.RotorRpm && info.active)
                {
                    float deltaDegrees = ComputeRotorDeltaDegrees(info.currentValue, deltaSeconds);
                    if (Mathf.Abs(deltaDegrees) > 0.0001f)
                    {
                        Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                            ? info.axisLocal.normalized
                            : Vector3.up;
                        info.servoTransform.localRotation =
                            info.servoTransform.localRotation * Quaternion.AngleAxis(deltaDegrees, axis);
                    }
                }

                info.lastUpdateUT = currentUT;
            }
        }

        static bool ApplyHeatState(GhostPlaybackState state, PartEvent evt, bool heated)
        {
            if (state == null || state.heatInfos == null) return false;

            if (!state.heatInfos.TryGetValue(evt.partPersistentId, out HeatGhostInfo info) || info == null)
                return false;

            bool applied = false;

            if (info.transforms != null)
            {
                for (int i = 0; i < info.transforms.Count; i++)
                {
                    var ts = info.transforms[i];
                    if (ts.t == null) continue;

                    if (heated)
                    {
                        ts.t.localPosition = ts.deployedPos;
                        ts.t.localRotation = ts.deployedRot;
                        ts.t.localScale = ts.deployedScale;
                    }
                    else
                    {
                        ts.t.localPosition = ts.stowedPos;
                        ts.t.localRotation = ts.stowedRot;
                        ts.t.localScale = ts.stowedScale;
                    }
                    applied = true;
                }
            }

            if (info.materialStates != null)
            {
                for (int i = 0; i < info.materialStates.Count; i++)
                {
                    HeatMaterialState materialState = info.materialStates[i];
                    if (materialState.material == null) continue;

                    if (!string.IsNullOrEmpty(materialState.colorProperty))
                    {
                        materialState.material.SetColor(
                            materialState.colorProperty,
                            heated ? materialState.hotColor : materialState.coldColor);
                    }

                    if (!string.IsNullOrEmpty(materialState.emissiveProperty))
                    {
                        materialState.material.SetColor(
                            materialState.emissiveProperty,
                            heated ? materialState.hotEmission : materialState.coldEmission);
                    }

                    applied = true;
                }
            }

            return applied;
        }

        private void DriveReentryToZero(ReentryFxInfo info, int recIdx, string bodyName, double altitude, string vesselName)
        {
            DriveReentryLayers(info, 0f, Vector3.zero, recIdx, bodyName, altitude, 0f, vesselName);
            info.lastIntensity = 0f;
            info.lastVelocity = Vector3.zero;
        }

        private void UpdateReentryFx(int recIdx, GhostPlaybackState state, string vesselName)
        {
            var info = state.reentryFxInfo;
            if (info == null || state.ghost == null) return;

            Vector3 interpolatedVel = state.lastInterpolatedVelocity;
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName))
            {
                DriveReentryToZero(info, recIdx, bodyName, 0.0, vesselName);
                return;
            }

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-nobody",
                    $"ReentryFx: body '{bodyName}' not found — skipping");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName);
                return;
            }

            Vector3 surfaceVel = interpolatedVel - (Vector3)body.getRFrmVel(state.ghost.transform.position);
            float speed = surfaceVel.magnitude;

            if (!body.atmosphere)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-noatmo",
                    $"ReentryFx: body {bodyName} has no atmosphere — skipping");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName);
                return;
            }

            if (altitude >= body.atmosphereDepth)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-aboveatmo",
                    $"ReentryFx: altitude {altitude:F0} above atmosphereDepth {body.atmosphereDepth:F0} — skipping");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName);
                return;
            }

            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure < 0 || double.IsNaN(temperature) || temperature < 0)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-badatmo",
                    $"ReentryFx: GetPressure/GetTemperature returned invalid value for ghost #{recIdx} — density fallback to 0");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName);
                return;
            }

            double density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-baddensity",
                    $"ReentryFx: GetDensity returned invalid value for ghost #{recIdx} — density fallback to 0");
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName);
                return;
            }

            float q = (float)(0.5 * density * speed * speed);
            float rawIntensity = GhostVisualBuilder.ComputeReentryIntensity(speed, q);

            float smoothedIntensity = Mathf.Lerp(info.lastIntensity, rawIntensity,
                1f - Mathf.Exp(-GhostVisualBuilder.ReentrySmoothingRate * Time.deltaTime));
            if (smoothedIntensity < 0.001f && rawIntensity == 0f)
                smoothedIntensity = 0f;

            DriveReentryLayers(info, smoothedIntensity, surfaceVel, recIdx, bodyName, altitude, q, vesselName);
            info.lastIntensity = smoothedIntensity;
            info.lastVelocity = surfaceVel;
        }

        private void DriveReentryLayers(ReentryFxInfo info, float intensity, Vector3 surfaceVel,
            int recIdx, string bodyName, double altitude, float dynamicPressure, string vesselName)
        {
            bool wasActive = info.lastIntensity > 0f;
            bool isActive = intensity > 0f;

            if (isActive && !wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Flight",
                    $"ReentryFx: Activated for ghost #{recIdx} \"{vesselName}\" — intensity={intensity:F2}, q={dynamicPressure:F0} Pa, speed={speed:F0} m/s, alt={altitude:F0} m, body={bodyName}");
            }
            else if (!isActive && wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Flight",
                    $"ReentryFx: Deactivated for ghost #{recIdx} — intensity dropped to 0 (speed={speed:F0} m/s, alt={altitude:F0} m)");
            }

            if (isActive)
            {
                ParsekLog.VerboseRateLimited("Flight", $"ghost-{recIdx}-intensity",
                    $"ReentryFx: ghost #{recIdx} intensity={intensity:F2} speed={surfaceVel.magnitude:F0} alt={altitude:F0}");
            }

            // Layer A: Heat glow (material emission)
            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;

                    if (intensity <= GhostVisualBuilder.ReentryLayerAThreshold)
                    {
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty, ms.coldColor);
                    }
                    else
                    {
                        float glowFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerAThreshold, 1f, intensity);
                        Color targetEmission = Color.Lerp(GhostVisualBuilder.ReentryHotEmissionLow,
                            GhostVisualBuilder.ReentryHotEmissionHigh, glowFraction);
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty,
                                Color.Lerp(ms.coldEmission, ms.coldEmission + targetEmission, glowFraction));
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty,
                                Color.Lerp(ms.coldColor, ms.hotColor, glowFraction));
                    }
                }
            }

            // Layer B: Flame particles
            if (info.flameParticles != null)
            {
                for (int i = 0; i < info.flameParticles.Count; i++)
                {
                    ParticleSystem flame = info.flameParticles[i];
                    if (flame == null) continue;

                    if (intensity > GhostVisualBuilder.ReentryLayerBThreshold)
                    {
                        float flameFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerBThreshold, 1f, intensity);
                        var emission = flame.emission;
                        emission.rateOverTime = flameFraction * GhostVisualBuilder.ReentryFlameMaxEmission;
                        var main = flame.main;
                        main.startSize = Mathf.Lerp(1f, 5f, flameFraction);
                        if (surfaceVel.sqrMagnitude > 1f)
                            flame.transform.rotation = Quaternion.LookRotation(surfaceVel.normalized);
                        if (!flame.isPlaying) flame.Play();
                    }
                    else
                    {
                        var emission = flame.emission;
                        emission.rateOverTime = 0f;
                        if (flame.particleCount == 0 && flame.isPlaying)
                            flame.Stop();
                    }
                }
            }

            // Layer C: Smoke/plasma trail
            if (info.smokeParticles != null)
            {
                for (int i = 0; i < info.smokeParticles.Count; i++)
                {
                    ParticleSystem smoke = info.smokeParticles[i];
                    if (smoke == null) continue;

                    if (intensity > GhostVisualBuilder.ReentryLayerCThreshold)
                    {
                        float smokeFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerCThreshold, 1f, intensity);
                        var emission = smoke.emission;
                        emission.rateOverTime = smokeFraction * GhostVisualBuilder.ReentrySmokeMaxEmission;
                        var main = smoke.main;
                        main.startSize = Mathf.Lerp(2f, 10f, smokeFraction);
                        if (surfaceVel.sqrMagnitude > 1f)
                            smoke.transform.rotation = Quaternion.LookRotation(surfaceVel.normalized);
                        if (!smoke.isPlaying) smoke.Play();
                    }
                    else
                    {
                        var emission = smoke.emission;
                        emission.rateOverTime = 0f;
                        if (smoke.particleCount == 0 && smoke.isPlaying)
                            smoke.Stop();
                    }
                }
            }

            // Layer D: Trail renderer
            if (info.trailRenderer != null)
            {
                if (intensity > GhostVisualBuilder.ReentryLayerDThreshold)
                {
                    float trailFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerDThreshold, 1f, intensity);
                    info.trailRenderer.startWidth = Mathf.Lerp(
                        GhostVisualBuilder.ReentryTrailMinWidth, GhostVisualBuilder.ReentryTrailMaxWidth, trailFraction);
                    info.trailRenderer.endWidth = Mathf.Lerp(
                        GhostVisualBuilder.ReentryTrailEndMinWidth, GhostVisualBuilder.ReentryTrailEndMaxWidth, trailFraction);
                    info.trailRenderer.emitting = true;
                }
                else
                {
                    info.trailRenderer.emitting = false;
                }
            }
        }

        private static void ResetReentryFx(GhostPlaybackState state, int recIdx)
        {
            var info = state.reentryFxInfo;
            if (info == null) return;

            info.lastIntensity = 0f;
            info.lastVelocity = Vector3.zero;

            if (info.trailRenderer != null)
            {
                info.trailRenderer.Clear();
                info.trailRenderer.emitting = false;
            }

            if (info.flameParticles != null)
            {
                for (int i = 0; i < info.flameParticles.Count; i++)
                {
                    if (info.flameParticles[i] != null)
                    {
                        info.flameParticles[i].Clear(true);
                        info.flameParticles[i].Stop();
                    }
                }
            }

            if (info.smokeParticles != null)
            {
                for (int i = 0; i < info.smokeParticles.Count; i++)
                {
                    if (info.smokeParticles[i] != null)
                    {
                        info.smokeParticles[i].Clear(true);
                        info.smokeParticles[i].Stop();
                    }
                }
            }

            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;
                    if (!string.IsNullOrEmpty(ms.emissiveProperty))
                        ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                    if (!string.IsNullOrEmpty(ms.colorProperty))
                        ms.material.SetColor(ms.colorProperty, ms.coldColor);
                }
            }

            ParsekLog.Verbose("Flight", $"ReentryFx: Loop reset for ghost #{recIdx} — cleared trail and particles");
        }

        static bool ApplyDeployableState(GhostPlaybackState state, PartEvent evt, bool deployed)
        {
            if (state.deployableInfos == null) return false;

            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(evt.partPersistentId, out info)) return false;

            bool applied = false;

            for (int i = 0; i < info.transforms.Count; i++)
            {
                var ts = info.transforms[i];
                if (ts.t == null) continue;
                applied = true;
                if (deployed)
                {
                    ts.t.localPosition = ts.deployedPos;
                    ts.t.localRotation = ts.deployedRot;
                    ts.t.localScale = ts.deployedScale;
                }
                else
                {
                    ts.t.localPosition = ts.stowedPos;
                    ts.t.localRotation = ts.stowedRot;
                    ts.t.localScale = ts.stowedScale;
                }
            }

            return applied;
        }

        static bool ApplyJettisonPanelState(GhostPlaybackState state, PartEvent evt, bool jettisoned)
        {
            if (state.jettisonInfos == null) return false;

            JettisonGhostInfo jetInfo;
            if (!state.jettisonInfos.TryGetValue(evt.partPersistentId, out jetInfo) ||
                jetInfo.jettisonTransforms == null ||
                jetInfo.jettisonTransforms.Count == 0)
                return false;

            bool applied = false;
            for (int i = 0; i < jetInfo.jettisonTransforms.Count; i++)
            {
                Transform jettisonTransform = jetInfo.jettisonTransforms[i];
                if (jettisonTransform == null) continue;
                jettisonTransform.gameObject.SetActive(!jettisoned);
                applied = true;
            }

            return applied;
        }

        private static LightPlaybackState GetOrCreateLightPlaybackState(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.lightPlaybackStates == null)
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();

            LightPlaybackState playbackState;
            if (!state.lightPlaybackStates.TryGetValue(partPersistentId, out playbackState))
            {
                playbackState = new LightPlaybackState();
                state.lightPlaybackStates[partPersistentId] = playbackState;
            }

            return playbackState;
        }

        private void ApplyLightPowerEvent(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.isOn = on;
            if (!on)
                SetLightState(state, partPersistentId, false);
            else if (!playbackState.blinkEnabled)
                SetLightState(state, partPersistentId, true);
        }

        private void ApplyLightBlinkModeEvent(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.blinkEnabled = enabled;
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        private void ApplyLightBlinkRateEvent(GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        private void UpdateBlinkingLights(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.lightPlaybackStates == null || state.lightPlaybackStates.Count == 0)
                return;

            foreach (var kv in state.lightPlaybackStates)
            {
                uint partPersistentId = kv.Key;
                LightPlaybackState playbackState = kv.Value;
                if (playbackState == null)
                    continue;

                bool shouldEnable = playbackState.isOn;
                if (shouldEnable && playbackState.blinkEnabled)
                {
                    float rateHz = playbackState.blinkRateHz > 0f ? playbackState.blinkRateHz : 1f;
                    double cycle = currentUT * rateHz;
                    double frac = cycle - Math.Floor(cycle);
                    shouldEnable = frac < 0.5;
                }

                SetLightState(state, partPersistentId, shouldEnable);
            }
        }

        static void SetLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.lightInfos == null) return;

            LightGhostInfo info;
            if (!state.lightInfos.TryGetValue(partPersistentId, out info)) return;

            for (int i = 0; i < info.lights.Count; i++)
            {
                if (info.lights[i] != null)
                    info.lights[i].enabled = on;
            }
        }

        static void HidePartSubtree(GameObject ghost, uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                var t = ghost.transform.Find($"ghost_part_{pid}");
                if (t != null) t.gameObject.SetActive(false);
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                    for (int c = 0; c < children.Count; c++)
                        stack.Push(children[c]);
            }
        }

        bool IsAnyWarpActive()
        {
            // CurrentRateIndex > 0 covers both rails warp and physics warp.
            return IsAnyWarpActive(TimeWarp.CurrentRateIndex, TimeWarp.CurrentRate);
        }

        void ExitAllWarpForPlaybackStart(string context)
        {
            if (!IsAnyWarpActive()) return;
            TimeWarp.SetRate(0, true);
            Log($"Warp reset for playback start ({context})");
        }

        internal static bool ShouldPauseTimelineResourceReplay(bool isRecording)
        {
            return isRecording;
        }

        internal static bool ShouldLoopPlayback(bool recordingLoopPlayback)
        {
            return recordingLoopPlayback;
        }

        internal static int ComputeTargetResourceIndex(
            List<TrajectoryPoint> points, int lastAppliedResourceIndex, double currentUT)
        {
            int targetIndex = lastAppliedResourceIndex;
            for (int j = lastAppliedResourceIndex + 1; j < points.Count; j++)
            {
                if (points[j].ut <= currentUT)
                    targetIndex = j;
                else
                    break;
            }
            return targetIndex;
        }

        internal static bool TryComputeLoopPlaybackUT(
            double currentUT, double startUT, double endUT, double pauseSeconds,
            out double loopUT, out int cycleIndex)
        {
            loopUT = startUT;
            cycleIndex = 0;

            double duration = endUT - startUT;
            if (duration <= 0 || pauseSeconds < 0 || currentUT < startUT)
                return false;

            double cycleDuration = duration + pauseSeconds;
            if (cycleDuration <= 0)
                return false;

            double elapsed = currentUT - startUT;
            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            double phase = elapsed - (cycleIndex * cycleDuration);

            const double epsilon = 1e-6;
            if (phase > duration + epsilon)
                return false;

            if (phase < 0) phase = 0;
            if (phase > duration) phase = duration;

            loopUT = startUT + phase;
            return true;
        }

        internal static bool IsAnyWarpActive(int currentRateIndex, float currentRate)
        {
            return currentRateIndex > 0 || currentRate > 1f;
        }

        #endregion

        #region Camera Follow (Watch Mode)

        // Lazy-initialized styles for the watch mode overlay
        private GUIStyle watchOverlayStyle;
        private GUIStyle watchOverlayHintStyle;

        /// <summary>
        /// Returns true if recording at index has an active ghost (exists and not null).
        /// </summary>
        internal bool HasActiveGhost(int index)
        {
            GhostPlaybackState s;
            return ghostStates.TryGetValue(index, out s) && s != null && s.ghost != null;
        }

        /// <summary>
        /// Returns true if the ghost at index is on the same celestial body as the active vessel.
        /// </summary>
        internal bool IsGhostOnSameBody(int index)
        {
            GhostPlaybackState s;
            if (!ghostStates.TryGetValue(index, out s) || s == null) return false;
            string ghostBody = s.lastInterpolatedBodyName;
            string activeBody = FlightGlobals.ActiveVessel?.mainBody?.name;
            if (string.IsNullOrEmpty(ghostBody) || string.IsNullOrEmpty(activeBody)) return false;
            return ghostBody == activeBody;
        }

        /// <summary>
        /// Draws the on-screen overlay when in watch mode: vessel name and return hint.
        /// Called from OnGUI when watchedRecordingIndex >= 0.
        /// </summary>
        void DrawWatchModeOverlay()
        {
            if (watchOverlayStyle == null)
            {
                watchOverlayStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = Color.white }
                };
                watchOverlayHintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    alignment = TextAnchor.UpperCenter,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.7f) }
                };
            }

            string vesselName = "";
            var committed = RecordingStore.CommittedRecordings;
            if (watchedRecordingIndex >= 0 && watchedRecordingIndex < committed.Count)
                vesselName = committed[watchedRecordingIndex].VesselName;

            float boxW = 300f, boxH = 50f;
            float x = (Screen.width - boxW) / 2f;
            float y = 10f;
            Rect bgRect = new Rect(x, y, boxW, boxH);

            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(bgRect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Label(new Rect(x, y + 5, boxW, 22f), "Watching: " + vesselName, watchOverlayStyle);
            GUI.Label(new Rect(x, y + 27, boxW, 18f), "[Backspace] Return to vessel", watchOverlayHintStyle);
        }

        /// <summary>
        /// Enter watch mode: point the camera at a ghost vessel.
        /// If already watching the same recording, toggles off (exits watch mode).
        /// If watching a different recording, switches to the new one (preserves camera state).
        /// </summary>
        internal void EnterWatchMode(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (index < 0 || index >= committed.Count) return;

            // Toggle off: if already watching this recording, exit
            if (watchedRecordingIndex == index)
            {
                ExitWatchMode();
                return;
            }

            // Ghost must exist
            GhostPlaybackState gs;
            if (!ghostStates.TryGetValue(index, out gs) || gs == null || gs.ghost == null)
                return;

            // Ghost must be on the same body as the active vessel
            string ghostBody = gs.lastInterpolatedBodyName;
            string activeBody = FlightGlobals.ActiveVessel?.mainBody?.name;
            if (string.IsNullOrEmpty(ghostBody) || string.IsNullOrEmpty(activeBody) || ghostBody != activeBody)
                return;

            // Cannot enter watch mode for a taken-control recording
            if (committed[index].TakenControl)
                return;

            // Flight warning: if active vessel is in an unsafe state, show a brief screen message
            var av = FlightGlobals.ActiveVessel;
            if (av != null)
            {
                double pe = av.orbit != null ? av.orbit.PeA : 0;
                double atmoHeight = av.mainBody != null ? av.mainBody.atmosphereDepth : 0;
                if (!IsVesselSituationSafe(av.situation, pe, atmoHeight))
                {
                    ParsekLog.Verbose("CameraFollow", $"Showing flight warning \u2014 active vessel situation: {av.situation}");
                    ScreenMessage("Your vessel continues unattended", 3f);
                }
            }

            // If already watching a different recording, exit first (switch case — preserve camera state)
            bool switching = watchedRecordingIndex >= 0 && watchedRecordingIndex != index;
            if (switching)
            {
                ParsekLog.Info("CameraFollow", $"Switching watch from #{watchedRecordingIndex} to #{index} \"{committed[index].VesselName}\"");
                ExitWatchMode(skipCameraRestore: true);
            }

            watchedRecordingIndex = index;
            watchedRecordingId = committed[index].RecordingId;

            // Save camera state only when entering fresh (not switching between ghosts)
            if (!switching)
            {
                savedCameraVessel = FlightGlobals.ActiveVessel;
                savedCameraDistance = FlightCamera.fetch.Distance;
                savedCameraPitch = FlightCamera.fetch.camPitch;
                savedCameraHeading = FlightCamera.fetch.camHdg;
            }

            // Point camera at ghost
            FlightCamera.fetch.SetTargetTransform(gs.ghost.transform);
            FlightCamera.fetch.SetDistance(50f);  // override [75,400] entry clamp
            ParsekLog.Verbose("CameraFollow",
                $"FlightCamera.SetTargetTransform on ghost #{index} at {gs.ghost.transform.position}, distance={FlightCamera.fetch.Distance.ToString("F1", CultureInfo.InvariantCulture)}");

            // Block inputs that could affect the active vessel
            InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" set: {WatchModeLockMask}");

            // Clear hold timer
            watchEndHoldUntilUT = -1;

            string body = gs.lastInterpolatedBodyName ?? "?";
            string altStr = gs.lastInterpolatedAltitude.ToString("F0", CultureInfo.InvariantCulture);
            ParsekLog.Info("CameraFollow",
                $"Entering watch mode for recording #{index} \"{committed[index].VesselName}\" \u2014 ghost at alt {altStr}m on {body}");
        }

        /// <summary>
        /// Exit watch mode: return camera to the active vessel.
        /// When skipCameraRestore is true (switching between ghosts), the camera is not restored.
        /// </summary>
        internal void ExitWatchMode(bool skipCameraRestore = false)
        {
            if (watchedRecordingIndex < 0) return;

            // Restore camera to the active vessel (unless switching between ghosts)
            if (!skipCameraRestore)
            {
                var committed = RecordingStore.CommittedRecordings;
                string recVesselName = watchedRecordingIndex < committed.Count
                    ? committed[watchedRecordingIndex].VesselName : "?";
                string targetName = savedCameraVessel != null
                    ? savedCameraVessel.vesselName
                    : (FlightGlobals.ActiveVessel?.vesselName ?? "unknown");
                ParsekLog.Info("CameraFollow",
                    $"Exiting watch mode for recording #{watchedRecordingIndex} \"{recVesselName}\" \u2014 returning to {targetName}");

                if (savedCameraVessel != null && savedCameraVessel.gameObject != null)
                {
                    FlightCamera.fetch.SetTargetVessel(savedCameraVessel);
                    FlightCamera.fetch.SetDistance(savedCameraDistance);
                    FlightCamera.fetch.camPitch = savedCameraPitch;
                    FlightCamera.fetch.camHdg = savedCameraHeading;
                    ParsekLog.Verbose("CameraFollow",
                        $"FlightCamera.SetTargetVessel restored to {savedCameraVessel.vesselName}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
                }
                else
                {
                    FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                    ParsekLog.Verbose("CameraFollow",
                        $"FlightCamera.SetTargetVessel restored to {FlightGlobals.ActiveVessel?.vesselName ?? "unknown"}, distance={savedCameraDistance.ToString("F1", CultureInfo.InvariantCulture)}");
                }
            }

            // Remove input locks
            InputLockManager.RemoveControlLock(WatchModeLockId);
            ParsekLog.Verbose("CameraFollow", $"InputLockManager control lock \"{WatchModeLockId}\" removed");

            watchedRecordingIndex = -1;
            watchedRecordingId = null;
            savedCameraVessel = null;
            savedCameraDistance = 0f;
            savedCameraPitch = 0f;
            savedCameraHeading = 0f;
            watchEndHoldUntilUT = -1;
        }

        /// <summary>
        /// Determines whether a vessel's flight situation is safe for unattended flight.
        /// Safe means the vessel will not crash or deorbit while the player watches a ghost.
        /// </summary>
        internal static bool IsVesselSituationSafe(Vessel.Situations situation, double periapsis, double atmosphereAltitude)
        {
            switch (situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                case Vessel.Situations.DOCKED:
                    return true;

                case Vessel.Situations.ORBITING:
                    return periapsis > atmosphereAltitude;

                case Vessel.Situations.FLYING:
                case Vessel.Situations.SUB_ORBITAL:
                case Vessel.Situations.ESCAPING:
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure static helper: computes the new watch index after a recording is deleted.
        /// Returns (newIndex, newId) where newIndex=-1 means watch mode should exit.
        /// </summary>
        internal static (int newIndex, string newId) ComputeWatchIndexAfterDelete(
            int watchedIndex, string watchedId, int deletedIndex,
            List<RecordingStore.Recording> recordings)
        {
            if (deletedIndex == watchedIndex)
                return (-1, null);

            int newIndex = watchedIndex;
            if (deletedIndex < watchedIndex)
                newIndex = watchedIndex - 1;

            // Verify by ID — the recording at the new index should match
            if (newIndex >= 0 && newIndex < recordings.Count &&
                recordings[newIndex].RecordingId == watchedId)
            {
                return (newIndex, watchedId);
            }

            // ID mismatch — scan for correct index
            for (int j = 0; j < recordings.Count; j++)
            {
                if (recordings[j].RecordingId == watchedId)
                    return (j, watchedId);
            }

            // Not found — exit watch mode
            return (-1, null);
        }

        #endregion

        IEnumerator DeferredActivateVessel(uint pid)
        {
            for (int frame = 0; frame < 10; frame++)
            {
                yield return null;
                Vessel v = FlightGlobals.Vessels?.Find(vessel => vessel.persistentId == pid);
                if (v != null && v.loaded)
                {
                    v.IgnoreGForces(240);
                    FlightGlobals.ForceSetActiveVessel(v);
                    Log($"Activated vessel pid={pid}");
                    yield break;
                }
            }
            Log($"WARNING: Could not activate vessel pid={pid} within 10 frames");
        }

        #region Ghost Positioning (shared by manual + timeline playback)

        GameObject CreateGhostSphere(string name, Color color)
        {
            GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ghost.name = name;
            ghost.transform.localScale = Vector3.one * 12f; // 12m diameter

            Collider collider = ghost.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            Renderer renderer = ghost.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    Material mat = new Material(shader);
                    mat.color = color;
                    mat.SetColor("_EmissiveColor", color);
                    renderer.material = mat;
                }
                else
                {
                    Log("Warning: Could not find KSP/Emissive/Diffuse shader, using default");
                }
            }

            return ghost;
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex, double targetUT, out InterpolationResult interpResult)
        {
            if (points == null || points.Count == 0) { interpResult = InterpolationResult.Zero; ghost.SetActive(false); return; }

            int indexBefore = TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                PositionGhostAt(ghost, points[0]);
                interpResult = new InterpolationResult(points[0].velocity, points[0].bodyName, points[0].altitude);
                return;
            }

            TrajectoryPoint before = points[indexBefore];
            TrajectoryPoint after = points[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAt(ghost, before);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            CelestialBody bodyBefore = FlightGlobals.Bodies.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
            {
                Log($"Could not find body: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }
            if (!ghost.activeSelf) ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);

            interpolatedRot = SanitizeQuaternion(interpolatedRot);

            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
            {
                Log("Warning: NaN in interpolated position, using 'before' position");
                interpolatedPos = posBefore;
            }

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = interpolatedRot;
            interpResult = new InterpolationResult(
                Vector3.Lerp(before.velocity, after.velocity, t),
                after.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
        }

        // Keep the old signature for backward compat with tests
        internal int FindWaypointIndex(double targetUT)
        {
            return TrajectoryMath.FindWaypointIndex(recording, ref lastPlaybackIndex, targetUT);
        }

        void PositionGhostAt(GameObject ghost, TrajectoryPoint point)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Flight", "position-ghost-no-body",
                    $"PositionGhostAt: body '{point.bodyName}' not found — ghost position not updated");
                return;
            }

            Vector3d worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);

            ghost.transform.position = worldPos;
            ghost.transform.rotation = SanitizeQuaternion(point.rotation);
        }

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal static OrbitSegment? FindOrbitSegment(List<OrbitSegment> segments, double ut)
        {
            return TrajectoryMath.FindOrbitSegment(segments, ut);
        }

        // Cache to avoid reconstructing Orbit objects every frame
        private Dictionary<int, Orbit> orbitCache = new Dictionary<int, Orbit>();

        void PositionGhostFromOrbit(GameObject ghost, OrbitSegment segment, double ut, int cacheKey)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
            {
                Log($"Could not find body '{segment.bodyName}' for orbit playback");
                return;
            }

            Orbit orbit;
            if (!orbitCache.TryGetValue(cacheKey, out orbit))
            {
                orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                orbitCache[cacheKey] = orbit;
            }

            Vector3d worldPos = orbit.getPositionAtUT(ut);
            ghost.transform.position = worldPos;

            // Orient prograde
            Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);
            if (velocity.sqrMagnitude > 0.001)
                ghost.transform.rotation = Quaternion.LookRotation(velocity);
        }

        /// <summary>
        /// Positions a ghost using only orbit segments (no trajectory points).
        /// Used for background-only recordings (vessels that stayed on rails).
        /// </summary>
        void PositionGhostFromOrbitOnly(GameObject ghost, RecordingStore.Recording rec, double ut, int orbitCacheBase)
        {
            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                if (ut >= seg.startUT && ut <= seg.endUT)
                {
                    int cacheKey = orbitCacheBase + s;
                    if (loggedOrbitSegments.Add(cacheKey))
                        Log($"Orbit-only segment activated: cache={cacheKey}, body={seg.bodyName}, " +
                            $"sma={seg.semiMajorAxis:F0}, UT {seg.startUT:F0}-{seg.endUT:F0}");
                    PositionGhostFromOrbit(ghost, seg, ut, cacheKey);
                    ghost.SetActive(true);
                    return;
                }
            }
            // UT falls in a gap between orbit segments — hide ghost
            ghost.SetActive(false);
        }

        /// <summary>
        /// Positions a ghost at a fixed surface position (body, lat, lon, alt, rotation).
        /// Used for background vessels that are landed/splashed.
        /// </summary>
        void PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == surfPos.body);
            if (body == null)
            {
                ParsekLog.Warn("Flight", $"PositionGhostAtSurface: body '{surfPos.body}' not found");
                ghost.SetActive(false);
                return;
            }
            ghost.transform.position = body.GetWorldSurfacePosition(surfPos.latitude, surfPos.longitude, surfPos.altitude);
            ghost.transform.rotation = body.bodyTransform.rotation * surfPos.rotation;
            ghost.SetActive(true);
            ParsekLog.VerboseRateLimited("Flight", "surface-ghost-positioned",
                $"PositionGhostAtSurface: body={surfPos.body} lat={surfPos.latitude:F4} lon={surfPos.longitude:F4} alt={surfPos.altitude:F1}");
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points,
            List<OrbitSegment> segments, ref int cachedIndex, double targetUT, int orbitCacheBase,
            out InterpolationResult interpResult)
        {
            // Check orbit segments first
            if (segments != null && segments.Count > 0)
            {
                OrbitSegment? seg = FindOrbitSegment(segments, targetUT);
                if (seg.HasValue)
                {
                    // Use segment index as cache key offset
                    int segIdx = segments.IndexOf(seg.Value);
                    int cacheKey = orbitCacheBase + segIdx;
                    if (loggedOrbitSegments.Add(cacheKey))
                        Log($"Orbit segment activated: cache={cacheKey}, body={seg.Value.bodyName}, " +
                            $"sma={seg.Value.semiMajorAxis:F0}, UT {seg.Value.startUT:F0}-{seg.Value.endUT:F0}");
                    PositionGhostFromOrbit(ghost, seg.Value, targetUT, cacheKey);
                    Vector3 vel = Vector3.zero;
                    Orbit orbit;
                    if (orbitCache.TryGetValue(cacheKey, out orbit))
                        vel = orbit.getOrbitalVelocityAtUT(targetUT);
                    CelestialBody segBody = FlightGlobals.Bodies?.Find(b => b.name == seg.Value.bodyName);
                    double segAlt = segBody != null ? segBody.GetAltitude(ghost.transform.position) : 0;
                    interpResult = new InterpolationResult(vel, seg.Value.bodyName, segAlt);
                    return;
                }
            }

            // Fall through to point-based interpolation
            InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT, out interpResult);
        }

        // Delegates to TrajectoryMath — kept for backward compatibility
        internal Quaternion SanitizeQuaternion(Quaternion q)
        {
            return TrajectoryMath.SanitizeQuaternion(q);
        }

        #endregion

        #region Utilities

        private static string DetermineRecordingBlockReason()
        {
            if (Time.timeScale < 0.01f)
                return "game paused";
            if (FlightGlobals.ActiveVessel == null)
                return "no active vessel";
            return "unknown guard in FlightRecorder.StartRecording";
        }

        void Log(string message) => ParsekLog.Verbose("Flight", message);
        void ScreenMessage(string message, float duration) => ParsekLog.ScreenMessage(message, duration);

        #endregion
    }
}
