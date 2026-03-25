using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Owns chain segment state: the active chain identity, pending transitions,
    /// boundary anchors, and continuation tracking fields. Extracted from ParsekFlight
    /// to isolate chain state into a single owner.
    ///
    /// Phase 1: State isolation + pure stop methods. Complex orchestration methods
    /// (CommitChainSegment, HandleDockUndockCommitRestart, etc.) remain on ParsekFlight
    /// and access chain state via this manager.
    /// </summary>
    internal class ChainSegmentManager
    {
        // Core chain identity
        internal string ActiveChainId;          // null if not building a chain
        internal int ActiveChainNextIndex;      // next segment's ChainIndex
        internal string ActiveChainPrevId;      // previous segment's RecordingId
        internal string ActiveChainCrewName;    // EVA crew name for current segment (null if vessel)

        // Pending chain transition
        internal bool PendingContinuation;      // true when a segment ended and next should start
        internal bool PendingIsBoarding;        // true = boarding (EVA→vessel), false = EVA exit
        internal string PendingEvaName;         // kerbal name for EVA transitions

        // Boundary anchor for chain continuation (copied from previous segment's last point)
        internal TrajectoryPoint? PendingBoundaryAnchor;

        // Continuation sampling: after a vessel chain segment commits (V→EVA),
        // keeps tracking the original vessel so its trajectory extends beyond the EVA point.
        internal uint ContinuationVesselPid;        // 0 = not tracking
        internal int ContinuationRecordingIdx = -1; // index into CommittedRecordings
        internal Vector3 ContinuationLastVelocity;
        internal double ContinuationLastUT = -1;

        // Undock continuation (ghost-only recording for the other vessel)
        internal uint UndockContinuationPid;         // 0 = not tracking
        internal int UndockContinuationRecIdx = -1;
        internal Vector3 UndockContinuationLastVel;
        internal double UndockContinuationLastUT = -1;

        /// <summary>Whether a chain is currently being built.</summary>
        internal bool HasActiveChain => ActiveChainId != null;

        internal ChainSegmentManager()
        {
            ParsekLog.Info("Chain", "ChainSegmentManager created");
        }

        /// <summary>
        /// Clears all chain state. Called from ResetFlightReadyState on flight ready/revert.
        /// </summary>
        internal void ClearAll()
        {
            ActiveChainId = null;
            ActiveChainNextIndex = 0;
            ActiveChainPrevId = null;
            ActiveChainCrewName = null;
            PendingContinuation = false;
            PendingIsBoarding = false;
            PendingEvaName = null;
            PendingBoundaryAnchor = null;
            ContinuationVesselPid = 0;
            ContinuationRecordingIdx = -1;
            ContinuationLastVelocity = Vector3.zero;
            ContinuationLastUT = -1;
            UndockContinuationPid = 0;
            UndockContinuationRecIdx = -1;
            UndockContinuationLastVel = Vector3.zero;
            UndockContinuationLastUT = -1;
            ParsekLog.Verbose("Chain", "ClearAll: all chain state reset");
        }

        /// <summary>
        /// Stops vessel continuation tracking. Clears PID and recording index.
        /// </summary>
        internal void StopContinuation(string reason)
        {
            ParsekLog.Verbose("Chain",
                $"Continuation stopped ({reason}): was tracking pid={ContinuationVesselPid}, " +
                $"recording #{ContinuationRecordingIdx}");
            ContinuationVesselPid = 0;
            ContinuationRecordingIdx = -1;
        }

        /// <summary>
        /// Stops undock continuation tracking. Clears PID, recording index, and last UT.
        /// </summary>
        internal void StopUndockContinuation(string reason)
        {
            ParsekLog.Verbose("Chain",
                $"Undock continuation stopped ({reason}): was tracking pid={UndockContinuationPid}, " +
                $"recording #{UndockContinuationRecIdx}");
            UndockContinuationPid = 0;
            UndockContinuationRecIdx = -1;
            UndockContinuationLastUT = -1;
        }
    }
}
