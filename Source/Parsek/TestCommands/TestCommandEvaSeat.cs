using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one completion poll of a started <c>EvaSeatBoard</c> /
    /// <c>EvaSeatLeave</c> concludes.</summary>
    internal enum EvaSeatCompletionDecision
    {
        /// <summary>The seat move has not fully landed (or has not held long enough): keep
        /// polling.</summary>
        StillWaiting,

        /// <summary>Every conjunct held for <see cref="TestCommandEvaSeat.HeldPollsRequired"/>
        /// consecutive polls: terminal OK.</summary>
        CompleteOk,

        /// <summary>The budget expired first: terminal ERROR (the verb's timeout reason).
        /// Never a false OK on a silent stock refusal.</summary>
        Timeout,
    }

    /// <summary>
    /// Pure half of the two automation-only external command seat verbs: <c>EvaSeatBoard</c>
    /// (the active EVA kerbal takes the nearest free external command seat on a loaded
    /// vessel) and <c>EvaSeatLeave</c> (the kerbal seated on the active vessel leaves the
    /// seat). They exist for the owner ruling of 2026-10-24: a kerbal leaving an external
    /// command seat is an EVA (a vehicle and a kerbal at the end), never an Undock.
    ///
    /// <para><b>Stock ground truth (decompiled KSP 1.12.5).</b> Taking a seat is the seat's
    /// "Board" part action <c>KerbalSeat.BoardSeat()</c>: it checks the seat bounds, calls
    /// <c>KerbalEVA.BoardSeat(KerbalSeat)</c> and, only when that returns true, sets the seat's
    /// <c>occupant</c> (the part <c>KerbalSeat.OnSave</c> writes as <c>occupantID</c>; calling
    /// <c>KerbalEVA.BoardSeat</c> alone leaves it null, so every saved snapshot holds an empty
    /// seat). <c>KerbalEVA.BoardSeat</c> runs the FSM's <c>On_seatBoard</c> event,
    /// whose body moves the kerbal onto the seat pivot, couples the kerbal's part to the seat
    /// part (<c>Part.Couple</c>: the EVA vessel is destroyed and the kerbal part joins the
    /// seat's vessel, which Parsek records as a dock merge) and force-activates the merged
    /// vessel; it returns false (screen message only) when the FSM state refuses the event.
    /// Leaving is <c>KerbalEVA.OnDeboardSeat()</c> (the seat part's "Leave Seat" button): a
    /// tourist is refused with a screen message, anyone else runs <c>On_seatDeboard</c>, whose
    /// body is <c>EjectFromSeat</c> (<c>part.Undock</c>, so stock fires
    /// <c>onVesselsUndocking(vehicle, kerbal)</c>) followed by
    /// <c>ForceSetActiveVessel(kerbal)</c>. <c>KerbalEVA.IsSeated()</c> is the FSM's
    /// seated-command state. A seated kerbal's <c>KerbalEVA</c> lives on the kerbal's part
    /// inside the vehicle, so the leave finds it among the active vessel's parts.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=EvaSeatBoard</c> and <c>cmd=EvaSeatLeave</c>, no args, FLIGHT
    /// only. The board picks the nearest free seat (a <c>KerbalSeat</c> with no occupant) on a
    /// loaded non-EVA vessel within <see cref="DefaultSeatRangeMeters"/>; the seam bounds the
    /// distance itself because <c>BoardSeat</c> has no range check of its own. The leave acts
    /// on the one seated kerbal of the active vessel.</para>
    ///
    /// <para><b>Completion.</b> TWO-PHASE on the default budget. Both hold their conjuncts for
    /// <see cref="HeldPollsRequired"/> consecutive polls before OK: Parsek's dock-merge
    /// confirmation window (a few frames after the couple) has no seam-readable bit, and a
    /// next FIFO command must not land inside it.</para>
    /// </summary>
    internal static class TestCommandEvaSeat
    {
        internal const string BoardVerb = "EvaSeatBoard";
        internal const string LeaveVerb = "EvaSeatLeave";

        /// <summary>The seam's honesty bound (metres, inclusive) from the kerbal to the seat
        /// part, over <c>BoardSeat</c>'s missing distance check. EvaBoard's bound.</summary>
        internal const double DefaultSeatRangeMeters = 10.0;

        /// <summary>Consecutive polls every conjunct must hold before OK. Parsek's dock
        /// confirmation expires after 5 frames, so 10 polls outlast it.</summary>
        internal const int HeldPollsRequired = 10;

        internal const string BoardNotEvaReason = "evaseatboard-not-eva";
        internal const string BoardNoFreeSeatReason = "evaseatboard-no-free-seat";
        internal const string BoardNotNearSeatReason = "evaseatboard-not-near-seat";
        internal const string BoardRefusedReason = "evaseatboard-refused";
        internal const string BoardThrewReason = "evaseatboard-threw";
        internal const string BoardTimeoutReason = "evaseatboard-timeout";

        internal const string LeaveNoActiveVesselReason = "evaseatleave-no-active-vessel";
        internal const string LeaveNotSeatedReason = "evaseatleave-not-seated";
        internal const string LeaveSeatedAmbiguousReason = "evaseatleave-seated-ambiguous";
        internal const string LeaveTouristReason = "evaseatleave-tourist";
        internal const string LeaveThrewReason = "evaseatleave-threw";
        internal const string LeaveTimeoutReason = "evaseatleave-timeout";

        /// <summary>The REJECTED vocabulary of both verbs, in gate order. hlib.EVASEAT_REASONS
        /// mirrors it (EvaSeatSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "evaseatboard-not-eva",
            "evaseatboard-no-free-seat",
            "evaseatboard-not-near-seat",
            "evaseatboard-refused",
            "evaseatleave-no-active-vessel",
            "evaseatleave-not-seated",
            "evaseatleave-seated-ambiguous",
            "evaseatleave-tourist",
        };

        /// <summary>The post-call ERROR vocabulary: the stock call ran and the move did not
        /// land.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "evaseatboard-threw",
            "evaseatboard-timeout",
            "evaseatleave-threw",
            "evaseatleave-timeout",
        };

        /// <summary>
        /// The board gate, in the order the applier meets it: the active vessel is an EVA
        /// kerbal, at least one free seat sits on a loaded non-EVA vessel, and the nearest one
        /// is within <paramref name="rangeMeters"/> (inclusive). Returns null to proceed.
        /// </summary>
        internal static string DecideBoardGate(bool activeIsEva, bool anyFreeSeat,
            double nearestSeatMeters, double rangeMeters)
        {
            if (!activeIsEva) return BoardNotEvaReason;
            if (!anyFreeSeat) return BoardNoFreeSeatReason;
            if (!(nearestSeatMeters <= rangeMeters)) return BoardNotNearSeatReason;
            return null;
        }

        /// <summary>Convenience overload against <see cref="DefaultSeatRangeMeters"/>.</summary>
        internal static string DecideBoardGate(bool activeIsEva, bool anyFreeSeat, double nearestSeatMeters)
            => DecideBoardGate(activeIsEva, anyFreeSeat, nearestSeatMeters, DefaultSeatRangeMeters);

        /// <summary>
        /// The leave gate: an active vessel, exactly one seated kerbal among its parts (two
        /// is ambiguous, never a silent first pick), and that kerbal not a tourist (stock
        /// refuses a tourist with a screen message only). Returns null to proceed.
        /// </summary>
        internal static string DecideLeaveGate(bool hasActiveVessel, int seatedCount, bool seatedIsTourist)
        {
            if (!hasActiveVessel) return LeaveNoActiveVesselReason;
            if (seatedCount <= 0) return LeaveNotSeatedReason;
            if (seatedCount > 1) return LeaveSeatedAmbiguousReason;
            if (seatedIsTourist) return LeaveTouristReason;
            return null;
        }

        /// <summary>
        /// The board conjuncts: the EVA vessel is gone (the couple destroyed it), the kerbal's
        /// FSM is seated, the kerbal's part now belongs to the seat's vessel, that vessel is
        /// the active one, Parsek has no split or board merge pending, and the scene settled.
        /// </summary>
        internal static bool BoardConjunctsHold(bool evaVesselGone, bool seated, bool kerbalOnSeatVessel,
            bool seatVesselActive, bool parsekQuiescent, bool sceneSettled)
            => evaVesselGone && seated && kerbalOnSeatVessel && seatVesselActive
               && parsekQuiescent && sceneSettled;

        /// <summary>
        /// The leave conjuncts: the active vessel is the kerbal's own EVA vessel, its FSM is no
        /// longer seated, the kerbal's part has left the vehicle, Parsek has no split or board
        /// merge pending (the dismount's deferred branch finished), and the scene settled.
        /// </summary>
        internal static bool LeaveConjunctsHold(bool activeIsKerbalEva, bool notSeated, bool leftVehicle,
            bool parsekQuiescent, bool sceneSettled)
            => activeIsKerbalEva && notSeated && leftVehicle && parsekQuiescent && sceneSettled;

        /// <summary>The held-poll counter: one more when the conjuncts hold, back to zero when
        /// they do not.</summary>
        internal static int NextHeldPolls(bool conjunctsHold, int heldPolls)
            => conjunctsHold ? heldPolls + 1 : 0;

        /// <summary>
        /// One completion poll: OK once the conjuncts held <see cref="HeldPollsRequired"/>
        /// consecutive polls; a timeout once <paramref name="elapsed"/> reaches
        /// <paramref name="budget"/>. Positive completion first.
        /// </summary>
        internal static EvaSeatCompletionDecision DecideCompletion(int heldPolls, double elapsed, double budget)
        {
            if (heldPolls >= HeldPollsRequired) return EvaSeatCompletionDecision.CompleteOk;
            if (elapsed >= budget) return EvaSeatCompletionDecision.Timeout;
            return EvaSeatCompletionDecision.StillWaiting;
        }

        /// <summary>The board OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildBoardOkPayload(
            string kerbal, uint vesselPid, string seatPart, uint seatId)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("vesselPid", vesselPid.ToString(ic)),
                new KeyValuePair<string, string>("seatPart", seatPart ?? string.Empty),
                new KeyValuePair<string, string>("seatId", seatId.ToString(ic)),
            };
        }

        /// <summary>The leave OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildLeaveOkPayload(
            string kerbal, uint kerbalPid, uint vehiclePid)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("kerbalPid", kerbalPid.ToString(ic)),
                new KeyValuePair<string, string>("vehiclePid", vehiclePid.ToString(ic)),
            };
        }

        /// <summary>The grep-stable Info line logged when the board call is made.</summary>
        internal static string FormatBoardStartLine(string kerbal, uint targetPid, string seatPart,
            uint seatId, double distanceMeters)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "evaseatboard start kerbal='" + (kerbal ?? string.Empty) + "'"
                + " targetPid=" + targetPid.ToString(ic)
                + " seatPart=" + (seatPart ?? string.Empty)
                + " seatId=" + seatId.ToString(ic)
                + " dist=" + distanceMeters.ToString("F2", ic);
        }

        /// <summary>The grep-stable Info line logged on the board OK.</summary>
        internal static string FormatBoardCompleteLine(string kerbal, uint vesselPid, string seatPart, uint seatId)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "evaseatboard complete kerbal='" + (kerbal ?? string.Empty) + "'"
                + " vesselPid=" + vesselPid.ToString(ic)
                + " seatPart=" + (seatPart ?? string.Empty)
                + " seatId=" + seatId.ToString(ic);
        }

        /// <summary>The grep-stable Info line logged when the leave call is made.</summary>
        internal static string FormatLeaveStartLine(string kerbal, uint vehiclePid, string seatPart)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "evaseatleave start kerbal='" + (kerbal ?? string.Empty) + "'"
                + " vehiclePid=" + vehiclePid.ToString(ic)
                + " seatPart=" + (seatPart ?? string.Empty);
        }

        /// <summary>The grep-stable Info line logged on the leave OK.</summary>
        internal static string FormatLeaveCompleteLine(string kerbal, uint kerbalPid, uint vehiclePid)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "evaseatleave complete kerbal='" + (kerbal ?? string.Empty) + "'"
                + " kerbalPid=" + kerbalPid.ToString(ic)
                + " vehiclePid=" + vehiclePid.ToString(ic);
        }
    }
}
