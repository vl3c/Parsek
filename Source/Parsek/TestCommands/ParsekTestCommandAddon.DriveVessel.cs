using System.Collections.Generic;
using System.Globalization;
using ModuleWheels;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the automation-only two-phase <c>DriveVessel</c> verb; the
    /// contract is on <see cref="TestCommandDriveVessel"/>. The drive itself is one
    /// <c>Vessel.OnFlyByWire</c> callback; the completion poll ends it once the game clock
    /// covered the requested span.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // The drive in progress. Only one can exist: the two-phase head holds the FIFO.
        private Vessel driveVessel;
        private float driveThrottle;
        private double driveSeconds;
        private double driveStartUT;
        private double driveStartSrfSpeed;
        private double driveStartLat;
        private double driveStartLon;
        private double driveMaxSrfSpeed;
        private double drivePathMeters;
        private bool driveBrakesWereOn;
        // Settle mode: no callback, no control touched; the completion poll samples speed.
        private bool driveSettle;
        private double driveSettleBelow;
        private double driveBelowSinceUT;
        // Dock mode: the throttle is held below the speed cap until stock couples one of the
        // vessel's docking ports (GameEvents.onPartCouple).
        private bool driveUntilDocked;
        private double driveMaxSpeed;
        private bool driveDocked;
        private bool driveCoupleHooked;
        private double driveDockUT;
        private readonly HashSet<uint> drivePortPartIds = new HashSet<uint>();
        private string drivePortPart;
        private string drivePartnerPart;
        private uint drivePartnerPid;
        private string drivePartnerName;

        private void DriveVesselImpl(ParsedCommand cmd)
        {
            string throttleArg = ArgOrNull(cmd, TestCommandDriveVessel.ThrottleKey);
            string settleArg = ArgOrNull(cmd, TestCommandDriveVessel.SettleBelowKey);
            string reason = TestCommandDriveVessel.ValidateSettle(throttleArg, settleArg,
                out bool settle, out double settleBelow);
            if (reason != null)
            {
                RejectDriveVessel(reason, $"{TestCommandDriveVessel.SettleBelowKey}={settleArg ?? string.Empty}");
                return;
            }
            float throttle = 0f;
            if (!settle)
            {
                reason = TestCommandDriveVessel.ValidateThrottle(throttleArg, out throttle);
                if (reason != null)
                {
                    RejectDriveVessel(reason, $"{TestCommandDriveVessel.ThrottleKey}={throttleArg ?? string.Empty}");
                    return;
                }
            }
            string secondsArg = ArgOrNull(cmd, TestCommandDriveVessel.SecondsKey);
            reason = TestCommandDriveVessel.ValidateSeconds(secondsArg, out double seconds);
            if (reason != null)
            {
                RejectDriveVessel(reason, $"{TestCommandDriveVessel.SecondsKey}={secondsArg ?? string.Empty}");
                return;
            }
            string untilDockedArg = ArgOrNull(cmd, TestCommandDriveVessel.UntilDockedKey);
            string maxSpeedArg = ArgOrNull(cmd, TestCommandDriveVessel.MaxSpeedKey);
            reason = TestCommandDriveVessel.ValidateDock(untilDockedArg, maxSpeedArg, settle,
                out bool untilDocked, out double maxSpeed);
            if (reason != null)
            {
                RejectDriveVessel(reason, $"{TestCommandDriveVessel.UntilDockedKey}={untilDockedArg ?? string.Empty} " +
                    $"{TestCommandDriveVessel.MaxSpeedKey}={maxSpeedArg ?? string.Empty}");
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            bool hasVessel = v != null;
            bool unpacked = hasVessel && v.loaded && !v.packed;
            bool onSurface = hasVessel
                && (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED);
            int motors = 0;
            int readyPorts = 0;
            var portIds = new List<uint>();
            if (unpacked)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null) continue;
                    motors += p.FindModulesImplementing<ModuleWheelMotor>().Count;
                    List<ModuleDockingNode> nodes = p.FindModulesImplementing<ModuleDockingNode>();
                    for (int n = 0; n < nodes.Count; n++)
                    {
                        ModuleDockingNode node = nodes[n];
                        if (node == null || node.fsm == null) continue;
                        if (!string.Equals(node.fsm.currentStateName,
                                TestCommandDriveVessel.ReadyPortStateName, System.StringComparison.Ordinal))
                            continue;
                        readyPorts++;
                        portIds.Add(p.flightID);
                    }
                }
            }
            // Every refusal is decided before anything is touched, so a refused call has no
            // side effects.
            reason = TestCommandDriveVessel.DecideGate(hasVessel, unpacked, onSurface, motors, settle,
                untilDocked, readyPorts);
            if (reason != null)
            {
                RejectDriveVessel(reason, hasVessel
                    ? $"vessel={v.vesselName} situation={v.situation} packed={Bool(v.packed)} " +
                      $"loaded={Bool(v.loaded)} wheelMotors={Int(motors)} readyPorts={Int(readyPorts)}"
                    : "no active vessel");
                return;
            }

            if (settle)
            {
                driveSettle = true;
                driveSettleBelow = settleBelow;
                driveVessel = v;
                driveSeconds = seconds;
                driveStartUT = Planetarium.GetUniversalTime();
                driveStartSrfSpeed = v.srfSpeed;
                driveBelowSinceUT = v.srfSpeed < settleBelow ? driveStartUT : -1.0;
                driveBrakesWereOn = false;
                ParsekLog.Info(Tag, TestCommandDriveVessel.FormatSettleStartLine(
                    v.vesselName, v.persistentId, v.situation.ToString(), settleBelow, seconds,
                    v.ActionGroups[KSPActionGroup.Brakes], driveStartUT, driveStartSrfSpeed));
                SetExecResult(PendingVerdict, null, null);
                return;
            }

            // The parking brake would hold the wheels against the motors, so the verb owns
            // releasing it for the drive (restored in RemoveDriveVesselCallback).
            driveBrakesWereOn = v.ActionGroups[KSPActionGroup.Brakes];
            if (driveBrakesWereOn)
                v.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);

            driveVessel = v;
            driveThrottle = throttle;
            driveSeconds = seconds;
            driveStartUT = Planetarium.GetUniversalTime();
            driveStartSrfSpeed = v.srfSpeed;
            driveStartLat = v.latitude;
            driveStartLon = v.longitude;
            driveMaxSrfSpeed = v.srfSpeed;
            drivePathMeters = 0.0;
            driveUntilDocked = untilDocked;
            driveMaxSpeed = maxSpeed;
            driveDocked = false;
            driveDockUT = 0.0;
            drivePortPartIds.Clear();
            drivePortPart = null;
            drivePartnerPart = null;
            drivePartnerPid = 0u;
            drivePartnerName = null;
            v.OnFlyByWire += DriveVesselFlyByWire;

            if (untilDocked)
            {
                for (int i = 0; i < portIds.Count; i++) drivePortPartIds.Add(portIds[i]);
                GameEvents.onPartCouple.Add(OnDriveVesselPartCouple);
                driveCoupleHooked = true;
                ParsekLog.Info(Tag, TestCommandDriveVessel.FormatDockStartLine(
                    v.vesselName, v.persistentId, throttle, seconds, maxSpeed, readyPorts,
                    driveBrakesWereOn, driveStartUT, driveStartSrfSpeed));
                SetExecResult(PendingVerdict, null, null);
                return;
            }

            ParsekLog.Info(Tag, TestCommandDriveVessel.FormatStartLine(
                v.vesselName, v.persistentId, v.situation.ToString(), throttle, seconds, motors,
                driveBrakesWereOn, driveStartUT, driveStartSrfSpeed));
            SetExecResult(PendingVerdict, null, null);
        }

        // Runs inside Vessel.FeedInputFeed every physics frame, after stock copied the
        // player's input into ctrlState, so the motors read this value. It also samples the
        // speed the completion judges the drive by.
        private void DriveVesselFlyByWire(FlightCtrlState s)
        {
            Vessel v = driveVessel;
            if (driveUntilDocked)
                s.wheelThrottle = TestCommandDriveVessel.ResolveDockThrottle(driveThrottle,
                    v != null ? v.srfSpeed : 0.0, driveMaxSpeed, driveDocked);
            else
                s.wheelThrottle = driveThrottle;
            if (v == null)
                return;
            double speed = v.srfSpeed;
            if (speed > driveMaxSrfSpeed)
                driveMaxSrfSpeed = speed;
            drivePathMeters += speed * TimeWarp.fixedDeltaTime;
        }

        // Stock fires onPartCouple inside Part.Couple, before the two vessels merge, so the
        // partner's vessel is still its own. Only a couple naming one of the driven vessel's
        // Ready docking ports (captured at the start) is the dock.
        private void OnDriveVesselPartCouple(GameEvents.FromToAction<Part, Part> data)
        {
            if (!driveUntilDocked || driveDocked) return;
            Part from = data.from;
            Part to = data.to;
            Part own = null;
            Part other = null;
            if (from != null && drivePortPartIds.Contains(from.flightID)) { own = from; other = to; }
            else if (to != null && drivePortPartIds.Contains(to.flightID)) { own = to; other = from; }
            if (own == null) return;
            driveDocked = true;
            driveDockUT = Planetarium.GetUniversalTime();
            drivePortPart = own.partInfo != null ? own.partInfo.name : own.name;
            drivePartnerPart = other != null
                ? (other.partInfo != null ? other.partInfo.name : other.name) : null;
            Vessel ov = other != null ? other.vessel : null;
            drivePartnerPid = ov != null ? ov.persistentId : 0u;
            drivePartnerName = ov != null ? ov.vesselName : null;
            ParsekLog.Info(Tag, "drivevessel dock couple: port='" + drivePortPart
                + "' partnerPart='" + (drivePartnerPart ?? string.Empty)
                + "' partnerPid=" + drivePartnerPid.ToString(CultureInfo.InvariantCulture)
                + " partnerName='" + (drivePartnerName ?? string.Empty)
                + "' ut=" + driveDockUT.ToString("F2", CultureInfo.InvariantCulture));
        }

        private void TryCompleteDockDrive(double now)
        {
            Vessel v = driveVessel;
            bool usable = v != null && v.state != Vessel.State.DEAD && v.loaded && !v.packed;
            double gameElapsed = Planetarium.GetUniversalTime() - driveStartUT;
            double wallElapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandDriveVessel.Verb);
            TestCommandDriveVessel.DriveCompletion decision = TestCommandDriveVessel.DecideDockCompletion(
                driveDocked, usable, gameElapsed, driveSeconds, wallElapsed, budget);
            if (decision == TestCommandDriveVessel.DriveCompletion.StillDriving)
                return;

            string vesselName = v != null ? v.vesselName : string.Empty;
            uint pid = v != null ? v.persistentId : 0u;
            double maxSpeed = driveMaxSrfSpeed;
            double path = drivePathMeters;
            double dockUT = driveDockUT;
            string portPart = drivePortPart;
            string partnerPart = drivePartnerPart;
            uint partnerPid = drivePartnerPid;
            string partnerName = drivePartnerName;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            // ClearTwoPhase removes the callback and the couple hook and restores the brakes.
            ClearTwoPhase();

            Vessel active = FlightGlobals.ActiveVessel;
            uint activePid = active != null ? active.persistentId : 0u;
            string activeName = active != null ? active.vesselName : null;
            int activeParts = active != null && active.parts != null ? active.parts.Count : 0;

            string outcome;
            string error = null;
            switch (decision)
            {
                case TestCommandDriveVessel.DriveCompletion.Docked:
                    outcome = "docked";
                    break;
                case TestCommandDriveVessel.DriveCompletion.DidNotDock:
                    outcome = "did-not-dock";
                    error = TestCommandDriveVessel.DidNotDockReason;
                    break;
                case TestCommandDriveVessel.DriveCompletion.VesselLost:
                    outcome = "vessel-lost";
                    error = TestCommandDriveVessel.VesselLostReason;
                    break;
                default:
                    outcome = "timeout";
                    error = TestCommandDriveVessel.TimeoutReason;
                    TestCommandDiagnostics.Timeout(id, verb, wallElapsed, TestCommandDriveVessel.TimeoutReason);
                    break;
            }
            string endLine = TestCommandDriveVessel.FormatDockEndLine(outcome, vesselName, pid,
                gameElapsed, maxSpeed, path, dockUT, portPart, partnerPart, partnerPid, partnerName,
                activePid, activeName, activeParts);
            if (error == null)
            {
                ParsekLog.Info(Tag, endLine);
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandDriveVessel.BuildDockedPayload(gameElapsed, maxSpeed, path, dockUT,
                        partnerPid, partnerName, activePid, activeName, activeParts),
                    null, dequeueHead: true);
                return;
            }
            ParsekLog.Error(Tag, endLine + " reason=" + error
                + " wallElapsed=" + wallElapsed.ToString("F1", CultureInfo.InvariantCulture) + "s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, error, dequeueHead: true);
        }

        private void TryCompleteDriveVessel(double now)
        {
            if (driveSettle)
            {
                TryCompleteSettleVessel(now);
                return;
            }
            if (driveUntilDocked)
            {
                TryCompleteDockDrive(now);
                return;
            }
            Vessel v = driveVessel;
            bool usable = v != null && v.state != Vessel.State.DEAD && v.loaded && !v.packed;
            double gameElapsed = Planetarium.GetUniversalTime() - driveStartUT;
            double wallElapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandDriveVessel.Verb);
            TestCommandDriveVessel.DriveCompletion decision = TestCommandDriveVessel.DecideCompletion(
                usable, gameElapsed, driveSeconds, driveMaxSrfSpeed, wallElapsed, budget);
            if (decision == TestCommandDriveVessel.DriveCompletion.StillDriving)
                return;

            double endSpeed = usable ? v.srfSpeed : 0.0;
            double displacement = usable && v.mainBody != null
                ? TestCommandDriveVessel.ComputeSurfaceDistanceMeters(
                    driveStartLat, driveStartLon, v.latitude, v.longitude, v.mainBody.Radius)
                : 0.0;
            string situation = v != null ? v.situation.ToString() : string.Empty;
            string vesselName = v != null ? v.vesselName : string.Empty;
            uint pid = v != null ? v.persistentId : 0u;
            double maxSpeed = driveMaxSrfSpeed;
            double path = drivePathMeters;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            // ClearTwoPhase removes the callback and restores the brakes.
            ClearTwoPhase();

            string outcome;
            string error = null;
            switch (decision)
            {
                case TestCommandDriveVessel.DriveCompletion.CompleteOk:
                    outcome = "moved";
                    break;
                case TestCommandDriveVessel.DriveCompletion.DidNotMove:
                    outcome = "did-not-move";
                    error = TestCommandDriveVessel.DidNotMoveReason;
                    break;
                case TestCommandDriveVessel.DriveCompletion.VesselLost:
                    outcome = "vessel-lost";
                    error = TestCommandDriveVessel.VesselLostReason;
                    break;
                default:
                    outcome = "timeout";
                    error = TestCommandDriveVessel.TimeoutReason;
                    TestCommandDiagnostics.Timeout(id, verb, wallElapsed, TestCommandDriveVessel.TimeoutReason);
                    break;
            }
            string endLine = TestCommandDriveVessel.FormatEndLine(outcome, vesselName, pid,
                gameElapsed, driveStartSrfSpeed, endSpeed, maxSpeed, path, displacement, situation);
            if (error == null)
            {
                ParsekLog.Info(Tag, endLine);
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandDriveVessel.BuildCompletePayload(gameElapsed, maxSpeed, path,
                        displacement, situation),
                    null, dequeueHead: true);
                return;
            }
            ParsekLog.Error(Tag, endLine + " reason=" + error
                + " wallElapsed=" + wallElapsed.ToString("F1", CultureInfo.InvariantCulture) + "s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, error, dequeueHead: true);
        }

        private void TryCompleteSettleVessel(double now)
        {
            Vessel v = driveVessel;
            bool usable = v != null && v.state != Vessel.State.DEAD && v.loaded && !v.packed;
            double ut = Planetarium.GetUniversalTime();
            double gameElapsed = ut - driveStartUT;
            double speed = usable ? v.srfSpeed : 0.0;
            if (usable)
            {
                if (speed >= driveSettleBelow)
                    driveBelowSinceUT = -1.0;
                else if (driveBelowSinceUT < 0.0)
                    driveBelowSinceUT = ut;
            }
            double belowFor = driveBelowSinceUT < 0.0 ? -1.0 : ut - driveBelowSinceUT;
            double wallElapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandDriveVessel.Verb);
            TestCommandDriveVessel.DriveCompletion decision = TestCommandDriveVessel.DecideSettleCompletion(
                usable, gameElapsed, driveSeconds, belowFor, wallElapsed, budget);
            if (decision == TestCommandDriveVessel.DriveCompletion.StillDriving)
                return;

            string situation = v != null ? v.situation.ToString() : string.Empty;
            string vesselName = v != null ? v.vesselName : string.Empty;
            uint pid = v != null ? v.persistentId : 0u;
            double startSpeed = driveStartSrfSpeed;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            ClearTwoPhase();

            string outcome;
            string error = null;
            switch (decision)
            {
                case TestCommandDriveVessel.DriveCompletion.Settled:
                    outcome = "settled";
                    break;
                case TestCommandDriveVessel.DriveCompletion.DidNotSettle:
                    outcome = "did-not-settle";
                    error = TestCommandDriveVessel.DidNotSettleReason;
                    break;
                case TestCommandDriveVessel.DriveCompletion.VesselLost:
                    outcome = "vessel-lost";
                    error = TestCommandDriveVessel.VesselLostReason;
                    break;
                default:
                    outcome = "timeout";
                    error = TestCommandDriveVessel.TimeoutReason;
                    TestCommandDiagnostics.Timeout(id, verb, wallElapsed, TestCommandDriveVessel.TimeoutReason);
                    break;
            }
            string endLine = TestCommandDriveVessel.FormatSettleEndLine(outcome, vesselName, pid,
                gameElapsed, startSpeed, speed, situation);
            if (error == null)
            {
                ParsekLog.Info(Tag, endLine);
                EmitExecutedTerminal(id, seq, verb, "OK",
                    TestCommandDriveVessel.BuildSettledPayload(gameElapsed, speed, situation),
                    null, dequeueHead: true);
                return;
            }
            ParsekLog.Error(Tag, endLine + " reason=" + error
                + " wallElapsed=" + wallElapsed.ToString("F1", CultureInfo.InvariantCulture) + "s");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, error, dequeueHead: true);
        }

        // Idempotent: unsubscribes the drive callback and restores the parking brake the
        // drive released. Called from ClearTwoPhase, so no exit path (completion, timeout, a
        // throwing completion) leaves a vessel driving under a stale callback.
        private void RemoveDriveVesselCallback()
        {
            Vessel v = driveVessel;
            driveVessel = null;
            bool settle = driveSettle;
            driveSettle = false;
            bool docked = driveDocked;
            driveUntilDocked = false;
            driveDocked = false;
            drivePortPartIds.Clear();
            if (driveCoupleHooked)
            {
                GameEvents.onPartCouple.Remove(OnDriveVesselPartCouple);
                driveCoupleHooked = false;
            }
            if (v == null || settle)
                return;
            v.OnFlyByWire -= DriveVesselFlyByWire;
            if (driveBrakesWereOn)
            {
                // A dock may have absorbed the driven vessel into its partner; the brakes then
                // go back on the vessel the player now flies.
                Vessel target = v;
                if (docked && (v.state == Vessel.State.DEAD || !v.loaded))
                    target = FlightGlobals.ActiveVessel;
                if (target != null)
                    target.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
            }
            driveBrakesWereOn = false;
        }

        private void RejectDriveVessel(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"drivevessel rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
