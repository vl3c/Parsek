using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the two-phase external command seat verbs <c>EvaSeatBoard</c> /
    /// <c>EvaSeatLeave</c>; the contract is on <see cref="TestCommandEvaSeat"/>. It finds the
    /// seat or the seated kerbal, calls stock's own <c>KerbalEVA.BoardSeat</c> /
    /// <c>KerbalEVA.OnDeboardSeat</c>, and the completion polls the live state until the move
    /// landed and held.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // The move in progress. Only one can exist: the two-phase head holds the FIFO.
        private KerbalEVA evaSeatKerbal;
        private string evaSeatKerbalName;
        private Vessel evaSeatEvaVessel;
        private Vessel evaSeatVehicle;
        private uint evaSeatVehiclePid;
        private string evaSeatPartName;
        private uint evaSeatPartId;
        private int evaSeatHeldPolls;

        // =====================================================================================
        // EvaSeatBoard
        // =====================================================================================
        private void EvaSeatBoardImpl(ParsedCommand cmd)
        {
            ResetEvaSeatState();
            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA evaCtl = active != null && active.isEVA
                ? active.FindPartModuleImplementing<KerbalEVA>() : null;

            KerbalSeat bestSeat = null;
            double bestDistance = double.MaxValue;
            int freeSeats = 0;
            if (evaCtl != null)
            {
                Vector3d kerbalPos = active.GetWorldPos3D();
                List<Vessel> loaded = FlightGlobals.VesselsLoaded;
                for (int i = 0; loaded != null && i < loaded.Count; i++)
                {
                    Vessel v = loaded[i];
                    if (v == null || v.isEVA || ReferenceEquals(v, active) || v.parts == null) continue;
                    if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                    for (int p = 0; p < v.parts.Count; p++)
                    {
                        Part part = v.parts[p];
                        if (part == null) continue;
                        KerbalSeat seat = part.FindModuleImplementing<KerbalSeat>();
                        if (seat == null || seat.Occupant != null) continue;
                        freeSeats++;
                        double d = (((Vector3d)part.transform.position) - kerbalPos).magnitude;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            bestSeat = seat;
                        }
                    }
                }
            }

            string reason = TestCommandEvaSeat.DecideBoardGate(evaCtl != null, freeSeats > 0, bestDistance);
            if (reason != null)
            {
                RejectEvaSeat(TestCommandEvaSeat.BoardVerb, reason, active != null
                    ? $"active={active.vesselName} isEva={Bool(active.isEVA)} freeSeats={Int(freeSeats)} "
                      + $"nearest={FormatSeatMetres(bestDistance)}"
                    : "no active vessel");
                return;
            }

            Part seatPart = bestSeat.part;
            Vessel target = seatPart.vessel;
            evaSeatKerbal = evaCtl;
            evaSeatKerbalName = active.vesselName;
            evaSeatEvaVessel = active;
            evaSeatVehicle = target;
            evaSeatVehiclePid = target.persistentId;
            evaSeatPartName = RuntimePartName(seatPart);
            evaSeatPartId = seatPart.flightID;

            ParsekLog.Info(Tag, TestCommandEvaSeat.FormatBoardStartLine(
                evaSeatKerbalName, evaSeatVehiclePid, evaSeatPartName, evaSeatPartId, bestDistance));
            bool accepted;
            try
            {
                // Stock's own seat board: the FSM event moves the kerbal onto the seat pivot,
                // couples the kerbal part to the seat part and activates the merged vessel.
                accepted = evaCtl.BoardSeat(bestSeat);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "evaseatboard error reason=" + TestCommandEvaSeat.BoardThrewReason
                    + $" kerbal='{evaSeatKerbalName}' {ex.GetType().Name}: {ex.Message}");
                ResetEvaSeatState();
                SetExecResult("ERROR", null, TestCommandEvaSeat.BoardThrewReason);
                return;
            }
            if (!accepted)
            {
                // The FSM refused the event from its current state; stock posted its screen
                // message and nothing moved.
                string fsmState = FsmStateName(evaCtl);
                ResetEvaSeatState();
                RejectEvaSeat(TestCommandEvaSeat.BoardVerb, TestCommandEvaSeat.BoardRefusedReason,
                    $"kerbal='{active.vesselName}' fsm={fsmState}");
                return;
            }
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEvaSeatBoard(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandEvaSeat.BoardVerb);
            KerbalEVA kerbal = evaSeatKerbal;
            Vessel kerbalVessel = kerbal != null ? kerbal.vessel : null;
            bool evaGone = evaSeatEvaVessel == null;
            bool seated = kerbal != null && kerbal.IsSeated();
            bool onSeatVessel = kerbalVessel != null && evaSeatVehicle != null
                && ReferenceEquals(kerbalVessel, evaSeatVehicle);
            bool seatVesselActive = evaSeatVehicle != null
                && ReferenceEquals(FlightGlobals.ActiveVessel, evaSeatVehicle);
            bool quiescent = IsParsekSplitAndMergeQuiescent();
            bool settled = settleCounter == 0 && !sceneTransitioning;
            bool hold = TestCommandEvaSeat.BoardConjunctsHold(
                evaGone, seated, onSeatVessel, seatVesselActive, quiescent, settled);
            evaSeatHeldPolls = TestCommandEvaSeat.NextHeldPolls(hold, evaSeatHeldPolls);
            EvaSeatCompletionDecision decision = TestCommandEvaSeat.DecideCompletion(evaSeatHeldPolls, elapsed, budget);
            if (decision == EvaSeatCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string name = evaSeatKerbalName;
            uint vesselPid = evaSeatVehicle != null ? evaSeatVehicle.persistentId : evaSeatVehiclePid;
            string seatPart = evaSeatPartName;
            uint seatId = evaSeatPartId;
            ClearTwoPhase();

            if (decision == EvaSeatCompletionDecision.CompleteOk)
            {
                ParsekLog.Info(Tag, TestCommandEvaSeat.FormatBoardCompleteLine(name, vesselPid, seatPart, seatId));
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandEvaSeat.BuildBoardOkPayload(name, vesselPid, seatPart, seatId), null, dequeueHead: true);
                return;
            }
            TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandEvaSeat.BoardTimeoutReason);
            ParsekLog.Error(Tag, "evaseatboard error reason=" + TestCommandEvaSeat.BoardTimeoutReason
                + $" kerbal='{name ?? string.Empty}' evaGone={Bool(evaGone)} seated={Bool(seated)} "
                + $"onSeatVessel={Bool(onSeatVessel)} seatVesselActive={Bool(seatVesselActive)} "
                + $"quiescent={Bool(quiescent)} settled={Bool(settled)} "
                + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, TestCommandEvaSeat.BoardTimeoutReason, dequeueHead: true);
        }

        // =====================================================================================
        // EvaSeatLeave
        // =====================================================================================
        private void EvaSeatLeaveImpl(ParsedCommand cmd)
        {
            ResetEvaSeatState();
            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA seatedKerbal = null;
            int seatedCount = 0;
            if (active != null && active.parts != null)
            {
                for (int i = 0; i < active.parts.Count; i++)
                {
                    Part part = active.parts[i];
                    if (part == null) continue;
                    KerbalEVA eva = part.FindModuleImplementing<KerbalEVA>();
                    if (eva == null || !eva.IsSeated()) continue;
                    seatedCount++;
                    if (seatedKerbal == null) seatedKerbal = eva;
                }
            }
            ProtoCrewMember crew = seatedKerbal != null && seatedKerbal.part != null
                && seatedKerbal.part.protoModuleCrew != null && seatedKerbal.part.protoModuleCrew.Count > 0
                ? seatedKerbal.part.protoModuleCrew[0] : null;
            bool tourist = crew != null && crew.type == ProtoCrewMember.KerbalType.Tourist;

            string reason = TestCommandEvaSeat.DecideLeaveGate(active != null, seatedCount, tourist);
            if (reason != null)
            {
                RejectEvaSeat(TestCommandEvaSeat.LeaveVerb, reason, active != null
                    ? $"active={active.vesselName} pid={active.persistentId.ToString(CultureInfo.InvariantCulture)} "
                      + $"seated={Int(seatedCount)} tourist={Bool(tourist)}"
                    : "no active vessel");
                return;
            }

            Part seatPart = seatedKerbal.part.parent;
            evaSeatKerbal = seatedKerbal;
            evaSeatKerbalName = crew != null ? crew.name : seatedKerbal.part.partInfo?.title;
            evaSeatVehicle = active;
            evaSeatVehiclePid = active.persistentId;
            evaSeatPartName = seatPart != null ? RuntimePartName(seatPart) : string.Empty;
            evaSeatPartId = seatPart != null ? seatPart.flightID : 0u;

            ParsekLog.Info(Tag, TestCommandEvaSeat.FormatLeaveStartLine(
                evaSeatKerbalName, evaSeatVehiclePid, evaSeatPartName));
            try
            {
                // Stock's own "Leave Seat" button: EjectFromSeat (part.Undock, so stock fires
                // onVesselsUndocking(vehicle, kerbal)) and then ForceSetActiveVessel(kerbal).
                seatedKerbal.OnDeboardSeat();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, "evaseatleave error reason=" + TestCommandEvaSeat.LeaveThrewReason
                    + $" kerbal='{evaSeatKerbalName}' {ex.GetType().Name}: {ex.Message}");
                ResetEvaSeatState();
                SetExecResult("ERROR", null, TestCommandEvaSeat.LeaveThrewReason);
                return;
            }
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteEvaSeatLeave(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandEvaSeat.LeaveVerb);
            KerbalEVA kerbal = evaSeatKerbal;
            Vessel kerbalVessel = kerbal != null ? kerbal.vessel : null;
            bool activeIsKerbalEva = kerbalVessel != null && kerbalVessel.isEVA
                && ReferenceEquals(FlightGlobals.ActiveVessel, kerbalVessel);
            bool notSeated = kerbal != null && !kerbal.IsSeated();
            bool leftVehicle = kerbalVessel != null && !ReferenceEquals(kerbalVessel, evaSeatVehicle);
            bool quiescent = IsParsekSplitAndMergeQuiescent();
            bool settled = settleCounter == 0 && !sceneTransitioning;
            bool hold = TestCommandEvaSeat.LeaveConjunctsHold(
                activeIsKerbalEva, notSeated, leftVehicle, quiescent, settled);
            evaSeatHeldPolls = TestCommandEvaSeat.NextHeldPolls(hold, evaSeatHeldPolls);
            EvaSeatCompletionDecision decision = TestCommandEvaSeat.DecideCompletion(evaSeatHeldPolls, elapsed, budget);
            if (decision == EvaSeatCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string name = evaSeatKerbalName;
            uint kerbalPid = kerbalVessel != null ? kerbalVessel.persistentId : 0u;
            uint vehiclePid = evaSeatVehiclePid;
            ClearTwoPhase();

            if (decision == EvaSeatCompletionDecision.CompleteOk)
            {
                ParsekLog.Info(Tag, TestCommandEvaSeat.FormatLeaveCompleteLine(name, kerbalPid, vehiclePid));
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandEvaSeat.BuildLeaveOkPayload(name, kerbalPid, vehiclePid), null, dequeueHead: true);
                return;
            }
            TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandEvaSeat.LeaveTimeoutReason);
            ParsekLog.Error(Tag, "evaseatleave error reason=" + TestCommandEvaSeat.LeaveTimeoutReason
                + $" kerbal='{name ?? string.Empty}' activeIsKerbalEva={Bool(activeIsKerbalEva)} "
                + $"notSeated={Bool(notSeated)} leftVehicle={Bool(leftVehicle)} "
                + $"quiescent={Bool(quiescent)} settled={Bool(settled)} "
                + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, TestCommandEvaSeat.LeaveTimeoutReason, dequeueHead: true);
        }

        // =====================================================================================
        // Shared
        // =====================================================================================

        // No deferred split / branch and no board merge pending in Parsek. True with no flight
        // controller (nothing records, so nothing can be pending).
        private static bool IsParsekSplitAndMergeQuiescent()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            return flight == null || (!flight.StructuralSplitPending && flight.BoardMergeQuiescent);
        }

        // A seat move must never leave stale references behind for the next command.
        private void ResetEvaSeatState()
        {
            evaSeatKerbal = null;
            evaSeatKerbalName = null;
            evaSeatEvaVessel = null;
            evaSeatVehicle = null;
            evaSeatVehiclePid = 0u;
            evaSeatPartName = null;
            evaSeatPartId = 0u;
            evaSeatHeldPolls = 0;
        }

        private void RejectEvaSeat(string verb, string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"{verb.ToLowerInvariant()} rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }

        private static string FormatSeatMetres(double v)
            => v == double.MaxValue ? "none" : v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
