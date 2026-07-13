using System.Globalization;

namespace Parsek
{
    public partial class ParsekFlight
    {
        /// <summary>
        /// Thin flight-scene wrapper for the M-C1 <c>TimeJump</c> seam verb: an
        /// epoch-shift jump to an ARBITRARY forward target UT. Mirrors
        /// <see cref="WarpToRecordingEnd"/> (validate <see cref="TimeJumpManager.IsValidJump"/>,
        /// notify the recorder, then <see cref="TimeJumpManager.ExecuteJump"/> with null
        /// chains so the engine playback loop drains the spawn queue), but takes the target
        /// directly instead of a recording's EndUT. ExecuteJump stops warp and epoch-shifts
        /// instantly (frozen relative positions, SMA/ecc/inc unchanged, MNA-at-epoch shifted
        /// by the delta, earlier chain tips auto-spawned) and recalcs the ledger at the
        /// post-jump UT. Returns true when the jump was initiated (a valid forward jump),
        /// false on a rejected non-forward jump (the seam maps that to a REJECTED refusal).
        /// </summary>
        internal bool TimeJumpTo(double targetUT)
        {
            double currentUT = Planetarium.GetUniversalTime();

            if (!TimeJumpManager.IsValidJump(currentUT, targetUT))
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "TimeJumpTo: invalid (non-forward) jump current={0:F1} target={1:F1} - aborted",
                        currentUT, targetUT));
                return false;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "TimeJumpTo: epoch-shift jump to UT={0:F1} (delta={1:F1}s)",
                    targetUT, targetUT - currentUT));

            TimeJumpManager.NotifyRecorder(recorder, currentUT, targetUT);
            // Pass null chains - let the engine playback loop handle spawn naturally,
            // as WarpToRecordingEnd does.
            TimeJumpManager.ExecuteJump(targetUT, null, vesselGhoster);
            return true;
        }
    }
}
