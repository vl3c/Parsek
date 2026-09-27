using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Effective recorder mode at the moment a state snapshot was taken.
    /// Determined from the presence/absence of <see cref="ParsekFlight"/>'s
    /// <c>activeTree</c> and <c>recorder</c> fields. <see cref="None"/> means
    /// no recorder is live and no tree is active — pending slots may still be
    /// populated independently and are reported separately on the snapshot.
    /// </summary>
    internal enum RecorderMode
    {
        None = 0,
        Standalone = 1,
        Tree = 2,
    }

    /// <summary>
    /// Captures every recorder-relevant field needed to emit a single-line
    /// <c>[RecState]</c> diagnostic dump. The struct is a pure data carrier:
    /// no logic, no Unity dependencies beyond <see cref="GameScenes"/>, and
    /// safe to construct from inside unit tests via
    /// <see cref="CaptureFromParts"/>.
    ///
    /// Field order in this struct mirrors the field order in the rendered
    /// log line so the two stay aligned.
    /// </summary>
    internal struct RecorderStateSnapshot
    {
        // --- Mode / identity ---
        public RecorderMode mode;
        public string treeId;
        public string treeName;
        public string activeRecId;
        public string activeVesselName;
        public uint activeVesselPid;

        // --- Recorder live state ---
        public bool recorderExists;
        public bool isRecording;
        public bool isBackgrounded;
        public int bufferedPoints;
        public int bufferedPartEvents;
        public int bufferedOrbitSegments;
        public double lastRecordedUT;        // double.NaN if none

        // --- Tree state (only meaningful when mode == Tree) ---
        public int treeRecordingCount;
        public int treeBackgroundMapCount;

        // --- Pending tree slot (independent of pending standalone slot) ---
        public bool pendingTreePresent;
        public PendingTreeState pendingTreeState;
        public string pendingTreeId;

        // --- Pending standalone slot (independent of pending tree slot) ---
        public bool pendingStandalonePresent;
        public string pendingStandaloneRecId;

        // --- Pending split recorder (breakup / dock / undock race window) ---
        public bool pendingSplitPresent;
        public bool pendingSplitInProgress;

        // --- Chain manager state (chain boundaries are part of the diagnosis surface) ---
        public string chainActiveChainId;
        public int chainNextIndex;
        public bool chainBoundaryAnchorPending;
        public uint chainContinuationPid;
        public uint chainUndockContinuationPid;

        // --- Context ---
        public double currentUT;
        public GameScenes loadedScene;

        /// <summary>
        /// Pure factory: builds a snapshot from the listed inputs without touching
        /// any global state. The instance method <see cref="ParsekFlight.CaptureRecorderState"/>
        /// is a thin wrapper around this; tests call this overload directly with
        /// hand-constructed inputs.
        /// </summary>
        internal static RecorderStateSnapshot CaptureFromParts(
            RecordingTree activeTree,
            FlightRecorder recorder,
            RecordingTree pendingTree,
            PendingTreeState pendingTreeState,
            Recording pendingStandalone,
            FlightRecorder pendingSplitRecorder,
            bool pendingSplitInProgress,
            ChainSegmentManager chain,
            double currentUT,
            GameScenes loadedScene)
        {
            var snap = default(RecorderStateSnapshot);

            // Mode
            if (activeTree != null)
                snap.mode = RecorderMode.Tree;
            else if (recorder != null)
                snap.mode = RecorderMode.Standalone;
            else
                snap.mode = RecorderMode.None;

            // Tree identity (only when in tree mode)
            if (activeTree != null)
            {
                snap.treeId = activeTree.Id;
                snap.treeName = activeTree.TreeName;
                snap.activeRecId = activeTree.ActiveRecordingId;
                snap.treeRecordingCount = activeTree.Recordings != null ? activeTree.Recordings.Count : 0;
                snap.treeBackgroundMapCount = activeTree.BackgroundMap != null ? activeTree.BackgroundMap.Count : 0;
            }

            // Recorder live state
            if (recorder != null)
            {
                snap.recorderExists = true;
                snap.isRecording = recorder.IsRecording;
                snap.isBackgrounded = recorder.IsBackgrounded;
                snap.activeVesselPid = recorder.RecordingVesselId;
                snap.bufferedPoints = recorder.Recording != null ? recorder.Recording.Count : 0;
                snap.bufferedPartEvents = recorder.PartEvents != null ? recorder.PartEvents.Count : 0;
                snap.bufferedOrbitSegments = recorder.OrbitSegments != null ? recorder.OrbitSegments.Count : 0;
                snap.lastRecordedUT = recorder.LastRecordedUT;
            }
            else
            {
                snap.lastRecordedUT = double.NaN;
            }

            // Vessel name: prefer the active recording in the tree, fall back to the
            // CaptureAtStop name on the recorder, then to the empty string.
            if (snap.activeRecId != null && activeTree != null
                && activeTree.Recordings != null
                && activeTree.Recordings.TryGetValue(snap.activeRecId, out var treeRec)
                && treeRec != null)
            {
                snap.activeVesselName = treeRec.VesselName;
                if (snap.activeVesselPid == 0)
                    snap.activeVesselPid = treeRec.VesselPersistentId;
            }
            else if (recorder != null && recorder.CaptureAtStop != null)
            {
                snap.activeVesselName = recorder.CaptureAtStop.VesselName;
                if (snap.activeRecId == null)
                    snap.activeRecId = recorder.CaptureAtStop.RecordingId;
            }

            // Pending tree slot
            if (pendingTree != null)
            {
                snap.pendingTreePresent = true;
                snap.pendingTreeId = pendingTree.Id;
                snap.pendingTreeState = pendingTreeState;
            }

            // Pending standalone slot
            if (pendingStandalone != null)
            {
                snap.pendingStandalonePresent = true;
                snap.pendingStandaloneRecId = pendingStandalone.RecordingId;
            }

            // Pending split recorder
            snap.pendingSplitPresent = pendingSplitRecorder != null;
            snap.pendingSplitInProgress = pendingSplitInProgress;

            // Chain manager
            if (chain != null)
            {
                snap.chainActiveChainId = chain.ActiveChainId;
                snap.chainNextIndex = chain.ActiveChainNextIndex;
                snap.chainBoundaryAnchorPending = chain.PendingBoundaryAnchor.HasValue;
                snap.chainContinuationPid = chain.ContinuationVesselPid;
                snap.chainUndockContinuationPid = chain.UndockContinuationPid;
            }

            // Context
            snap.currentUT = currentUT;
            snap.loadedScene = loadedScene;

            return snap;
        }
    }
}
