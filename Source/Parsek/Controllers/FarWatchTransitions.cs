using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>The far-watch states of design-far-watch.md 4.1.</summary>
    internal enum FarWatchState
    {
        Idle,
        Spawning,
        HandoverOut,
        Following,
        HandoverBack,
        Teardown,
        Aborted,
    }

    /// <summary>The events that move the far-watch state machine (design 4.2).</summary>
    internal enum FarWatchTrigger
    {
        StartAdmitted,
        DroneLoaded,
        SpawnFailed,
        SwitchedToDrone,
        SwitchOutFailed,
        EndRequested,
        DroneLost,
        ShipLost,
        HandbackSwitched,
        ShipNeverLoaded,
        TeardownComplete,
        AbortToTeardown,
        AbortResolved,
        SceneExit,
    }

    /// <summary>
    /// Why a far watch ended (design 7.5: one closed enum, logged on every transition).
    /// </summary>
    internal enum FarWatchEndReason
    {
        None,
        SeamStop,
        WatchExit,
        SceneExit,
        DroneLost,
        ShipLost,
        SpawnTimeout,
        BuildFailed,
        SwitchFailed,
        ShipNeverLoaded,
        InvalidState,
        RecorderNotBackgrounded,
        GhostLeftBody,
        GhostGone,
        CameraLost,
        ControllerDestroyed,
    }

    /// <summary>
    /// Inputs of the far-watch entry refusal (design 8.3 pending windows plus the V1 ruling
    /// and the scene / vessel preconditions). Plain values so the decision is pure.
    /// </summary>
    internal struct FarWatchEntryInputs
    {
        internal bool InFlightScene;
        internal bool AlreadyActive;
        internal bool HasActiveVessel;
        internal bool ActiveVesselIsEva;
        internal bool ActiveVesselIsGhostOrDrone;
        internal bool PendingSplitInProgress;
        internal bool PendingSplitRecorder;
        internal bool DeferredJointBreakCheckInFlight;
        internal bool CrashCoalescerPendingBreakup;
        internal bool PendingTreeDockMerge;
        internal bool PendingAutoRecord;
        internal bool ChainToVesselPending;
        internal bool TransitionToBackgroundPending;
        internal bool TimeJumpPlacementArmed;
        internal bool TimeJumpPlacementWatched;
        internal bool RestoringActiveTree;
        internal bool ReFlyActive;
        internal bool SupplyRunProving;
        internal bool GhostResolvable;
        internal bool GhostOnShipBody;
    }

    /// <summary>
    /// The pure far-watch decisions: the transition table of design 4.2 as data, the entry
    /// refusal and the transition log line.
    /// </summary>
    internal static class FarWatchTransitions
    {
        /// <summary>
        /// The next state for (state, trigger), or false for an illegal pair. The scene exit is
        /// legal from every non-Idle state (no hand-back, design 7.5).
        /// </summary>
        internal static bool TryTransition(FarWatchState from, FarWatchTrigger trigger, out FarWatchState to)
        {
            to = from;
            if (trigger == FarWatchTrigger.SceneExit)
            {
                if (from == FarWatchState.Idle)
                    return false;
                to = FarWatchState.Idle;
                return true;
            }
            switch (from)
            {
                case FarWatchState.Idle:
                    if (trigger == FarWatchTrigger.StartAdmitted) { to = FarWatchState.Spawning; return true; }
                    return false;
                case FarWatchState.Spawning:
                    if (trigger == FarWatchTrigger.DroneLoaded) { to = FarWatchState.HandoverOut; return true; }
                    if (trigger == FarWatchTrigger.SpawnFailed
                        || trigger == FarWatchTrigger.DroneLost
                        || trigger == FarWatchTrigger.ShipLost
                        || trigger == FarWatchTrigger.EndRequested) { to = FarWatchState.Aborted; return true; }
                    return false;
                case FarWatchState.HandoverOut:
                    if (trigger == FarWatchTrigger.SwitchedToDrone) { to = FarWatchState.Following; return true; }
                    if (trigger == FarWatchTrigger.SwitchOutFailed
                        || trigger == FarWatchTrigger.DroneLost
                        || trigger == FarWatchTrigger.ShipLost) { to = FarWatchState.Aborted; return true; }
                    if (trigger == FarWatchTrigger.EndRequested) { to = FarWatchState.HandoverBack; return true; }
                    return false;
                case FarWatchState.Following:
                    if (trigger == FarWatchTrigger.EndRequested) { to = FarWatchState.HandoverBack; return true; }
                    if (trigger == FarWatchTrigger.DroneLost || trigger == FarWatchTrigger.ShipLost)
                    { to = FarWatchState.Aborted; return true; }
                    return false;
                case FarWatchState.HandoverBack:
                    if (trigger == FarWatchTrigger.HandbackSwitched) { to = FarWatchState.Teardown; return true; }
                    if (trigger == FarWatchTrigger.ShipNeverLoaded
                        || trigger == FarWatchTrigger.DroneLost
                        || trigger == FarWatchTrigger.ShipLost) { to = FarWatchState.Aborted; return true; }
                    return false;
                case FarWatchState.Teardown:
                    if (trigger == FarWatchTrigger.TeardownComplete) { to = FarWatchState.Idle; return true; }
                    return false;
                case FarWatchState.Aborted:
                    if (trigger == FarWatchTrigger.AbortToTeardown) { to = FarWatchState.Teardown; return true; }
                    if (trigger == FarWatchTrigger.AbortResolved) { to = FarWatchState.Idle; return true; }
                    return false;
            }
            return false;
        }

        /// <summary>True in every state where the drone may exist (everything but Idle).</summary>
        internal static bool IsRunning(FarWatchState state)
        {
            return state != FarWatchState.Idle;
        }

        /// <summary>
        /// The far-watch entry refusal: null when entry is allowed, else the first reason in a
        /// fixed order (scene and vessel first, then the pending windows of 8.3, then V1).
        /// </summary>
        internal static string EvaluateEntryRefusal(FarWatchEntryInputs i)
        {
            if (!i.InFlightScene) return "not-flight";
            if (i.AlreadyActive) return "already-active";
            if (!i.HasActiveVessel) return "no-active-vessel";
            if (i.ActiveVesselIsGhostOrDrone) return "active-is-ghost-or-drone";
            if (i.ActiveVesselIsEva) return "active-is-eva";
            if (i.PendingSplitInProgress) return "pending-split";
            if (i.PendingSplitRecorder) return "pending-split-recorder";
            if (i.DeferredJointBreakCheckInFlight) return "pending-joint-break-check";
            if (i.CrashCoalescerPendingBreakup) return "pending-breakup";
            if (i.PendingTreeDockMerge) return "pending-dock-merge";
            if (i.PendingAutoRecord) return "pending-auto-record";
            if (i.ChainToVesselPending) return "pending-board-merge";
            if (i.TransitionToBackgroundPending) return "pending-background-transition";
            if (i.TimeJumpPlacementArmed) return "time-jump-placement-armed";
            if (i.TimeJumpPlacementWatched) return "time-jump-placement-watched";
            if (i.RestoringActiveTree) return "restoring-active-tree";
            if (i.ReFlyActive) return "refly-active";
            if (i.SupplyRunProving) return "supply-run-proving";
            if (!i.GhostResolvable) return "ghost-not-resolvable";
            if (!i.GhostOnShipBody) return "ghost-other-body";
            return null;
        }

        /// <summary>The section 16.2 transition line (invariant culture; tests read it).</summary>
        internal static string FormatTransitionLine(
            FarWatchState from, FarWatchState to, FarWatchEndReason reason, string ghostRecordingId,
            string shipName, uint shipPid, uint dronePid, double ut)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "FarWatch transition from={0} to={1} reason={2} ghostRec={3} ship={4} shipPid={5} dronePid={6} ut={7}",
                from, to, reason, string.IsNullOrEmpty(ghostRecordingId) ? "(none)" : ghostRecordingId,
                string.IsNullOrEmpty(shipName) ? "(none)" : shipName, shipPid, dronePid,
                ut.ToString("F2", CultureInfo.InvariantCulture));
        }

        /// <summary>The section 16.2 per-tick summary line (shared rate-limit key).</summary>
        internal static string FormatTickSummary(
            int ticks, int writes, int skips, int floorClamps, double maxDeviationMeters, int holdRenewals)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "ticks={0} writes={1} skips={2} floorClamps={3} maxDevM={4} holdRenewals={5}",
                ticks, writes, skips, floorClamps,
                maxDeviationMeters.ToString("F1", CultureInfo.InvariantCulture), holdRenewals);
        }
    }
}
