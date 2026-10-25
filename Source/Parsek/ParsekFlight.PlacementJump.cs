using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // [ERS-exempt] Reads RecordingStore.CommittedRecordings by index only to find the
    // engine ghost (index-keyed ghostStates) that draws a recording id, the pairing the
    // proximity scan already does; it decides no visibility.
    public partial class ParsekFlight
    {
        /// <summary>
        /// World position of the drawn ghost of <paramref name="recordingId"/> this frame
        /// (its transform), or null when no active ghost draws it. The same read the
        /// proximity scan measures distance with.
        /// </summary>
        internal Vector3d? TryGetDrawnGhostWorldPosition(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) || engine == null)
                return null;
            var committed = RecordingStore.CommittedRecordings;
            foreach (var kvp in ghostStates)
            {
                int i = kvp.Key;
                GhostPlaybackState state = kvp.Value;
                if (state == null || state.ghost == null || !state.ghost.activeSelf)
                    continue;
                if (i < 0 || i >= committed.Count || committed[i] == null)
                    continue;
                if (committed[i].RecordingId == recordingId)
                    return state.ghost.transform.position;
            }
            return null;
        }

        /// <summary>
        /// The Leaves Warp for an orbital player vessel (plan 1.3): the group is moved to the
        /// same spot beside the ghost at the new clock. Returns false when the move was
        /// refused (logged; the scan normally greys the button first) - the jump does not
        /// happen. A refusal is the button's race backstop, so no screen message.
        /// </summary>
        private bool TryWarpToDepartureWithPlacement(
            Recording rec, double currentUT, double targetUT, double departureUT, Vessel active)
        {
            string recId = rec.RecordingId;
            bool started = TimeJumpManager.TryStartLeavesPlacementJump(
                rec, targetUT, departureUT, active, recorder,
                () => TryGetDrawnGhostWorldPosition(recId), out _);
            if (!started)
                return false;
            RefreshBlockedChainTipGhostOrbitsAfterJump();
            ParsekLog.ScreenMessage(
                string.Format(CultureInfo.InvariantCulture,
                    "Warped to departure of \"{0}\" ({1:F0}s)",
                    rec.VesselName, targetUT - currentUT), 5f);
            return true;
        }

        /// <summary>
        /// The Ready Warp for an orbital player vessel (plan Step 2 item 3): the clock jumps to
        /// the recording's end and the group is placed beside the ghost's recorded end state,
        /// where the ghost then spawns. Returns false when the move was refused (logged; the
        /// scan normally greys the button first) - the jump does not happen.
        /// </summary>
        private bool TryWarpToRecordingEndWithPlacement(Recording rec, Vessel active)
        {
            string recId = rec.RecordingId;
            return TimeJumpManager.TryStartReadyPlacementJump(
                rec, active, recorder, () => TryGetDrawnGhostWorldPosition(recId), out _);
        }

        /// <summary>
        /// Scan side of the move gates (plan 1.4): the refusal for a Leaves row, logged only
        /// when it changes for that recording.
        /// </summary>
        private MoveGateVerdict EvaluateLeavesMoveGateForScan(
            Recording rec, double currentUT, double departureUT)
        {
            return LogMoveGateOnChange(rec, TimeJumpManager.EvaluateLeavesMoveGate(
                rec, currentUT, departureUT, FlightGlobals.ActiveVessel));
        }

        /// <summary>
        /// Scan side of the Ready move gates (plan Step 2 item 3), logged only when the
        /// refusal changes for that recording.
        /// </summary>
        private MoveGateVerdict EvaluateReadyMoveGateForScan(Recording rec, double currentUT)
        {
            return LogMoveGateOnChange(rec, TimeJumpManager.EvaluateReadyMoveGate(
                rec, currentUT, FlightGlobals.ActiveVessel));
        }

        private static MoveGateVerdict LogMoveGateOnChange(Recording rec, MoveGateVerdict verdict)
        {
            if (ParsekLog.IsVerboseEnabled)
            {
                string recId = rec.RecordingId ?? "(null)";
                ParsekLog.VerboseOnChange("Flight",
                    "move-gate|" + recId,
                    verdict.Refusal.ToString(),
                    () => "Move gate: ref=" + recId
                        + " refusal=" + verdict.Refusal
                        + (string.IsNullOrEmpty(verdict.Detail) ? string.Empty : " detail=" + verdict.Detail));
            }
            return verdict;
        }
    }
}
