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
        TestRunnerReset,
        /// <summary>[ or ], or the W* / stop control (step 3).</summary>
        PlayerStop,
        /// <summary>The return point came (8.5): air or ground ahead, a node, an alarm.</summary>
        ReturnPoint,
        /// <summary>A re-check found the ship no longer safe on rails (8.4).</summary>
        ShipUnsafe,
        /// <summary>The watched ghost came within 250 km of the ship (8.4 hysteresis).</summary>
        GhostNearShip,
        /// <summary>W / Watch on a ghost near the ship: hand back, then near watch (8.4).</summary>
        WatchNearGhost,
        /// <summary>A switch to another vessel was asked for: hand back, then switch (8.5).</summary>
        SwitchRequested,
    }

    /// <summary>How the in-game test runner's between-run reset ends a far watch at once.</summary>
    internal enum FarWatchImmediateEnd
    {
        /// <summary>No drone vessel: end the watch and drop the ids.</summary>
        NoDrone,
        /// <summary>The drone exists but is not active: destroy it, end the watch, drop the ids.</summary>
        DestroyInactiveDrone,
        /// <summary>The drone is active and the ship is loaded: switch to the ship, then destroy it.</summary>
        SwitchToShipThenDestroy,
        /// <summary>The drone is active and no ship can take over: keep the watch and its ids
        /// (a live drone with no identity would be saved and flown as an ordinary vessel).</summary>
        KeepUntilGone,
    }

    /// <summary>What a follow tick does with watch mode's current target (see DecideFollowRekey).</summary>
    internal enum FarWatchFollowRekey
    {
        Keep,
        Rekey,
        WatchGone,
    }

    /// <summary>
    /// Inputs of the far-watch entry refusal (design 8.3 pending windows plus the scene /
    /// vessel preconditions). Plain values so the decision is pure.
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
        internal bool GhostResolvable;
        internal bool GhostOnShipBody;
        /// <summary>Follow mode moves the drone with the ghost; hold mode keeps it beside the ship.</summary>
        internal bool FollowGhost;
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

        /// <summary>
        /// A follow watch's ghost against the watch-mode target, read every follow tick: Keep
        /// while watch mode watches the drone's ghost, Rekey when it watches another recording (a
        /// chain segment transfer or a ghost switch keeps the watch, so the drone follows the new
        /// recording), WatchGone when watch mode watches nothing (an exit that did not reach the
        /// far-watch teardown).
        /// </summary>
        internal static FarWatchFollowRekey DecideFollowRekey(
            bool watching, int watchedIndex, string watchedRecordingId, int ghostIndex, string ghostRecordingId)
        {
            if (!watching || watchedIndex < 0)
                return FarWatchFollowRekey.WatchGone;
            if (watchedIndex == ghostIndex && string.Equals(watchedRecordingId, ghostRecordingId, StringComparison.Ordinal))
                return FarWatchFollowRekey.Keep;
            return FarWatchFollowRekey.Rekey;
        }

        /// <summary>
        /// Stock's flight-ended screen after a ship loss with the drone active (7.6, V2): once,
        /// while the watch is still aborted, a few frames after the loss so the ship is gone from
        /// the vessel list when the screen's save runs.
        /// </summary>
        internal static bool ShouldShowShipLostScreen(FarWatchState state, bool pending, int framesInState, int delayFrames)
        {
            return pending && state == FarWatchState.Aborted && framesInState >= delayFrames;
        }

        /// <summary>The between-run reset's choice (see <see cref="FarWatchImmediateEnd"/>).</summary>
        internal static FarWatchImmediateEnd DecideImmediateEnd(bool droneExists, bool droneActive, bool shipLoaded)
        {
            if (!droneExists)
                return FarWatchImmediateEnd.NoDrone;
            if (!droneActive)
                return FarWatchImmediateEnd.DestroyInactiveDrone;
            return shipLoaded ? FarWatchImmediateEnd.SwitchToShipThenDestroy : FarWatchImmediateEnd.KeepUntilGone;
        }

        /// <summary>
        /// The per-frame camera net runs in every state the drone may exist in, spawning and
        /// teardown included: a watch exit there restores the camera to the active vessel, and a
        /// camera left under a ghost transform must still be caught.
        /// </summary>
        internal static bool CameraNetRuns(FarWatchState state)
        {
            return IsRunning(state);
        }

        /// <summary>True in every state where the drone may exist (everything but Idle).</summary>
        internal static bool IsRunning(FarWatchState state)
        {
            return state != FarWatchState.Idle;
        }

        /// <summary>
        /// The far-watch entry refusal: null when entry is allowed, else the first reason in a
        /// fixed order (scene and vessel first, then the pending windows of 8.3, then the ghost).
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
            if (!i.GhostResolvable) return "ghost-not-resolvable";
            // Step 2 moves the drone on the ship's body only; a hold watch never moves it.
            if (i.FollowGhost && !i.GhostOnShipBody) return "ghost-other-body";
            return null;
        }

        /// <summary>
        /// An entry refusal that names a one-frame pending window of 8.3 (the safety check's
        /// rule 2, hover "busy"), as opposed to a scene, vessel or ghost precondition.
        /// </summary>
        internal static bool IsPendingWindowRefusal(string refusal)
        {
            if (string.IsNullOrEmpty(refusal))
                return false;
            return refusal.StartsWith("pending-", StringComparison.Ordinal)
                || refusal.StartsWith("time-jump-", StringComparison.Ordinal)
                || refusal == "restoring-active-tree";
        }

        /// <summary>
        /// The ship's part count moved while the drone held focus (7.4): a dock, undock,
        /// coupling or part loss. An unknown count (-1) on either side never reads as a change.
        /// </summary>
        internal static bool ShipStructureChanged(int partsAtHandover, int partsNow)
        {
            return partsAtHandover >= 0 && partsNow >= 0 && partsAtHandover != partsNow;
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
