using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin applier for the two-phase <c>PickUpFlag</c> verb; the contract is on
    /// <see cref="TestCommandPickUpFlag"/>. It picks the nearest real flag to the active EVA
    /// kerbal, and the completion presses stock's own <c>FlagSite.PickUp()</c> (so Parsek's
    /// pick-up capture runs as for a player) and polls until the flag vessel is gone.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // The pick-up in progress. Only one can exist: the two-phase head holds the FIFO.
        private KerbalEVA pickUpFlagKerbal;
        private string pickUpFlagKerbalName;
        private Vessel pickUpFlagVessel;
        private FlagSite pickUpFlagSite;
        private uint pickUpFlagPid;
        private string pickUpFlagName;
        private double pickUpFlagDistance;
        private int pickUpFlagFires;
        private int pickUpFlagPollsSinceFire;

        private void PickUpFlagImpl(ParsedCommand cmd)
        {
            ResetPickUpFlagState();
            Vessel active = FlightGlobals.ActiveVessel;
            KerbalEVA evaCtl = active != null && active.isEVA
                ? active.FindPartModuleImplementing<KerbalEVA>() : null;
            if (evaCtl == null)
            {
                RejectPickUpFlag(TestCommandPickUpFlag.NotEvaReason, active != null
                    ? $"active={active.vesselName} isEva={Bool(active.isEVA)}" : "no active vessel");
                return;
            }

            Vector3d kerbalPos = active.GetWorldPos3D();
            var candidates = new List<FlagPickUpCandidate>();
            var vessels = new List<Vessel>();
            List<Vessel> all = FlightGlobals.Vessels;
            int flagsSeen = 0;
            for (int i = 0; all != null && i < all.Count; i++)
            {
                Vessel v = all[i];
                if (v == null || v.vesselType != VesselType.Flag) continue;
                flagsSeen++;
                candidates.Add(new FlagPickUpCandidate
                {
                    Pid = v.persistentId,
                    Name = v.vesselName,
                    DistanceMeters = (v.GetWorldPos3D() - kerbalPos).magnitude,
                    IsFlag = true,
                    IsGhostMapVessel = GhostMapPresence.IsGhostMapVessel(v.persistentId),
                    Loaded = v.loaded,
                });
                vessels.Add(v);
            }

            int chosen = TestCommandPickUpFlag.ChooseFlag(candidates, out double nearest, out string reason);
            Vessel flag = chosen >= 0 ? vessels[chosen] : null;
            FlagSite site = flag != null ? flag.FindPartModuleImplementing<FlagSite>() : null;
            if (reason == null && site == null)
                reason = TestCommandPickUpFlag.NoFlagReason;
            if (reason != null)
            {
                RejectPickUpFlag(reason, $"kerbal={active.vesselName} flags={Int(flagsSeen)} "
                    + $"nearest={FormatPickUpMetres(nearest)} reach="
                    + TestCommandPickUpFlag.MaxReachMeters.ToString("F2", CultureInfo.InvariantCulture));
                return;
            }

            pickUpFlagKerbal = evaCtl;
            pickUpFlagKerbalName = active.vesselName;
            pickUpFlagVessel = flag;
            pickUpFlagSite = site;
            pickUpFlagPid = flag.persistentId;
            pickUpFlagName = flag.vesselName;
            pickUpFlagDistance = nearest;
            ParsekLog.Info(Tag, TestCommandPickUpFlag.FormatStartLine(
                pickUpFlagKerbalName, pickUpFlagName, pickUpFlagPid, nearest));
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompletePickUpFlag(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandPickUpFlag.Verb);
            Vessel flag = pickUpFlagVessel;
            bool referenceAlive = flag != null;
            bool stateDead = referenceAlive && flag.state == Vessel.State.DEAD;
            bool inList = referenceAlive && FlightGlobals.Vessels != null && FlightGlobals.Vessels.Contains(flag);
            bool gone = TestCommandPickUpFlag.IsFlagGone(referenceAlive, stateDead, inList);
            bool ragdoll = pickUpFlagKerbal != null && pickUpFlagKerbal.isRagdoll;
            bool settled = settleCounter == 0 && !sceneTransitioning;
            if (pickUpFlagFires > 0) pickUpFlagPollsSinceFire++;

            FlagPickUpDecision decision = TestCommandPickUpFlag.Decide(
                gone, pickUpFlagFires, pickUpFlagPollsSinceFire, ragdoll, settled, elapsed, budget);
            switch (decision)
            {
                case FlagPickUpDecision.KeepWaiting:
                    ParsekLog.VerboseRateLimited("TestCommands", "pickupflag-wait",
                        $"pickupflag waiting flagPid={pickUpFlagPid.ToString(CultureInfo.InvariantCulture)} "
                        + $"gone={Bool(gone)} ragdoll={Bool(ragdoll)} fires={Int(pickUpFlagFires)} "
                        + $"settled={Bool(settled)} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                    return;
                case FlagPickUpDecision.FirePickUp:
                    FirePickUpFlag();
                    return;
            }

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string kerbal = pickUpFlagKerbalName;
            string name = pickUpFlagName;
            uint pid = pickUpFlagPid;
            double dist = pickUpFlagDistance;
            int fires = pickUpFlagFires;
            ClearTwoPhase();

            if (decision == FlagPickUpDecision.CompleteOk)
            {
                ParsekLog.Info(Tag, TestCommandPickUpFlag.FormatCompleteLine(kerbal, name, pid, fires));
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandPickUpFlag.BuildOkPayload(name, pid, kerbal, dist, fires), null, dequeueHead: true);
                return;
            }

            string reason = decision == FlagPickUpDecision.FlagLost
                ? TestCommandPickUpFlag.FlagLostReason
                : TestCommandPickUpFlag.TimeoutReason;
            if (decision == FlagPickUpDecision.Timeout)
                TestCommandDiagnostics.Timeout(id, verb, elapsed, reason);
            ParsekLog.Error(Tag, "pickupflag error reason=" + reason
                + $" kerbal='{kerbal ?? string.Empty}' flag='{name ?? string.Empty}' "
                + $"flagPid={pid.ToString(CultureInfo.InvariantCulture)} gone={Bool(gone)} "
                + $"ragdoll={Bool(ragdoll)} fires={Int(fires)} settled={Bool(settled)} "
                + $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, reason, dequeueHead: true);
        }

        // One press of stock's own FlagSite.PickUp (the flag's "Take Down" / "Pick Up" event):
        // it gives the kerbal the flag back and kills the flag vessel inside the call, which is
        // where Parsek's FlagPickUpCapturePatch and OnVesselWillDestroy record the pick-up.
        private void FirePickUpFlag()
        {
            pickUpFlagFires++;
            pickUpFlagPollsSinceFire = 0;
            ParsekLog.Info(Tag, $"pickupflag press flagPid={pickUpFlagPid.ToString(CultureInfo.InvariantCulture)} "
                + $"fire={Int(pickUpFlagFires)}");
            try
            {
                pickUpFlagSite.PickUp();
            }
            catch (Exception ex)
            {
                string id = completionId; long seq = completionSeq; string verb = completionVerb;
                string kerbal = pickUpFlagKerbalName;
                ClearTwoPhase();
                ParsekLog.Error(Tag, "pickupflag error reason=" + TestCommandPickUpFlag.ThrewReason
                    + $" kerbal='{kerbal ?? string.Empty}' {ex.GetType().Name}: {ex.Message}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, TestCommandPickUpFlag.ThrewReason, dequeueHead: true);
            }
        }

        // A pick-up must never leave stale references behind for the next command.
        private void ResetPickUpFlagState()
        {
            pickUpFlagKerbal = null;
            pickUpFlagKerbalName = null;
            pickUpFlagVessel = null;
            pickUpFlagSite = null;
            pickUpFlagPid = 0u;
            pickUpFlagName = null;
            pickUpFlagDistance = 0.0;
            pickUpFlagFires = 0;
            pickUpFlagPollsSinceFire = 0;
        }

        private void RejectPickUpFlag(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"pickupflag rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }

        private static string FormatPickUpMetres(double v)
            => v == double.MaxValue ? "none" : v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
