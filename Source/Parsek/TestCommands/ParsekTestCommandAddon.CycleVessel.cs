using Parsek.Patches;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the automation-only <c>CycleVessel</c> verb; the contract is
    /// on <see cref="TestCommandCycleVessel"/>. SINGLE-PHASE: the press runs the cycle Prefix's
    /// own body (<see cref="VesselCycleReFlySwitchPatch.RunCycle"/>) inside the call, and a
    /// switch between loaded vessels sets the active vessel synchronously.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void CycleVesselImpl(ParsedCommand cmd)
        {
            string rawDirection = ArgOrNull(cmd, TestCommandCycleVessel.DirectionKey);
            string rawForceGhost = ArgOrNull(cmd, TestCommandCycleVessel.ForceGhostKey);
            string reason = TestCommandCycleVessel.ParseArgs(rawDirection, rawForceGhost,
                out bool previous, out bool forceGhost);
            if (reason != null)
            {
                RejectCycleVessel(reason,
                    $"direction={rawDirection ?? string.Empty} forceGhost={rawForceGhost ?? string.Empty}");
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
            {
                RejectCycleVessel(TestCommandCycleVessel.NoActiveVesselReason, "no active vessel");
                return;
            }

            uint before = active.persistentId;
            ParsekLog.Info(Tag, $"cyclevessel press direction={(previous ? "prev" : "next")} " +
                $"active={before} vessel={active.vesselName} forceGhost={Bool(forceGhost)}");

            // The Prefix's own sequence after its key read: the game-mode gate, then the body.
            var report = new VesselCycleReFlySwitchPatch.CycleReport();
            string outcome;
            if (ParsekGameModeGate.CheckInert("CycleVessel seam"))
                outcome = "inert";
            else
            {
                VesselCycleReFlySwitchPatch.RunCycle(previous, report);
                outcome = TestCommandCycleVessel.OutcomeToken(report.Outcome);
            }

            uint afterPress = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u;

            string forceGhostResult = TestCommandCycleVessel.ForceGhostSkipped;
            uint ghostPid = 0u;
            if (forceGhost)
            {
                ghostPid = TestCommandCycleVessel.FirstGhostCandidate(report.CandidatePids, report.CandidateIsGhost);
                Vessel ghost = ghostPid != 0u ? FindLiveVesselByPid(ghostPid) : null;
                if (ghost == null)
                {
                    forceGhostResult = TestCommandCycleVessel.ForceGhostNoGhost;
                    ParsekLog.Info(Tag, $"cyclevessel forceGhost: no ghost map marker among the press's " +
                        $"candidates (candidates={Int(report.CandidatePids.Count)}) - backstop not probed");
                }
                else
                {
                    bool returned = FlightGlobals.ForceSetActiveVessel(ghost);
                    uint afterForce = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u;
                    forceGhostResult = TestCommandCycleVessel.DecideForceGhostResult(
                        returned, afterPress, afterForce, ghostPid);
                    ParsekLog.Info(Tag, $"cyclevessel forceGhost: ForceSetActiveVessel(ghost pid={ghostPid} " +
                        $"vessel={ghost.vesselName}) returned={Bool(returned)} activeBefore={afterPress} " +
                        $"activeAfter={afterForce} result={forceGhostResult}");
                }
            }

            uint after = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u;
            var payload = TestCommandCycleVessel.BuildOkPayload(previous, outcome, report.Detail,
                before, after, report, forceGhostResult, ghostPid);
            ParsekLog.Info(Tag, TestCommandCycleVessel.FormatResultLine(payload));
            SetExecResult("OK", payload, null);
        }

        private void RejectCycleVessel(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"cyclevessel rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
