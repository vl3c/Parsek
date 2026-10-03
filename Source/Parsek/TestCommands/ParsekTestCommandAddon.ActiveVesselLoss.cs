namespace Parsek.TestCommands
{
    /// <summary>
    /// The shared active-vessel-loss guard on the two-phase completion poll (decision in
    /// the pure <see cref="TestCommandActiveVesselLoss"/>). A watched verb captures the
    /// active vessel when its executor returns PENDING; every completion poll then checks
    /// that vessel BEFORE the verb's own completion runs, and a lost vessel ends the step
    /// with <c>ERROR msg=active-vessel-lost</c> on that poll.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private bool activeVesselWatchArmed;
        private Vessel activeVesselWatch;
        private string activeVesselWatchName;
        private uint activeVesselWatchPid;

        // Called once when a two-phase command goes PENDING (ExecuteHead). Captures nothing
        // for an unwatched verb, outside FLIGHT, or with no active vessel.
        private void ArmActiveVesselWatch(string id, string verb)
        {
            ClearActiveVesselWatch();
            if (!TestCommandActiveVesselLoss.WatchesActiveVessel(verb)) return;
            if (!HighLogic.LoadedSceneIsFlight) return;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null) return;
            activeVesselWatchArmed = true;
            activeVesselWatch = active;
            activeVesselWatchName = active.vesselName;
            activeVesselWatchPid = active.persistentId;
            ParsekLog.Verbose(Tag, $"active-vessel watch armed id={id} cmd={verb} "
                + $"vessel={activeVesselWatchName} pid={activeVesselWatchPid}");
        }

        private void ClearActiveVesselWatch()
        {
            activeVesselWatchArmed = false;
            activeVesselWatch = null;
            activeVesselWatchName = null;
            activeVesselWatchPid = 0u;
        }

        // True when it ended the pending command (the caller returns). The Warn line is
        // written once: the terminal clears the watch with the rest of the two-phase state.
        private bool TryFailPendingOnActiveVesselLoss(double now)
        {
            if (!activeVesselWatchArmed) return false;
            Vessel v = activeVesselWatch;
            bool referenceAlive = v != null;
            bool stateDead = referenceAlive && v.state == Vessel.State.DEAD;
            if (!TestCommandActiveVesselLoss.ShouldFailFast(
                    completionVerb, activeVesselWatchArmed, referenceAlive, stateDead))
                return false;

            double elapsed = now - completionStartedAt;
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            ParsekLog.Warn(Tag, TestCommandActiveVesselLoss.FormatLossLine(
                id, verb, activeVesselWatchName, activeVesselWatchPid, referenceAlive, stateDead, elapsed));
            ClearTwoPhase();
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, TestCommandActiveVesselLoss.Reason,
                dequeueHead: true);
            return true;
        }
    }
}
