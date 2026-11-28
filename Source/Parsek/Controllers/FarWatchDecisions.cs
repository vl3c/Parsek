using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>What a W / Watch request does (design-far-watch.md 8.4, 12.2).</summary>
    internal enum FarWatchWatchRequest
    {
        /// <summary>Today's near watch (ghost within 300 km of the ship, no far watch running).</summary>
        NearWatch,
        /// <summary>A far ghost, or a ghost at another body, with the ship safe: the far watch starts directly.</summary>
        FarWatchDirect,
        /// <summary>A far watch runs and the ghost is near the ship: hand back first, then near watch.</summary>
        HandBackThenNearWatch,
        /// <summary>A far watch runs and the ghost is far from the ship: the drone follows it.</summary>
        FollowInFarWatch,
        /// <summary>A far ghost (any body) with the ship unsafe: refused with the safety reason.</summary>
        RefusedUnsafe,
        /// <summary>A far watch runs and the asked-for ghost has no reading (no state, no body).</summary>
        RefusedNoReading,
        /// <summary>A far watch runs but is not settled in Following (the drone is still moving).</summary>
        RefusedWatchSettling,
        /// <summary>A hold far watch runs (the drone stays beside the ship): no follow switch.</summary>
        RefusedHoldWatch,
    }

    /// <summary>What a player's switch to another vessel does (design-far-watch.md 8.5).</summary>
    internal enum FarWatchSwitchRoute
    {
        /// <summary>No far watch runs: the switch's own gates and stock's switch, as today.</summary>
        RunGates,
        /// <summary>A far watch runs: hand back first, then the same gated switch (no gate runs while the drone has focus).</summary>
        EndWatchThenReplay,
    }

    /// <summary>What an abort does with a pending switch (<see cref="FarWatchDecisions.DecideAbortSwitch"/>).</summary>
    internal enum FarWatchAbortSwitch { None, Replayed, Dropped }

    /// <summary>The switch left over after the hand-back (8.5).</summary>
    internal enum FarWatchPostSwitch
    {
        None,
        /// <summary>The target is gone (destroyed, recovered) or already active: nothing to do.</summary>
        DropTargetGoneOrActive,
        /// <summary>No gated path was handed over: refused, never a bare SetActiveVessel.</summary>
        DropNoGatedPath,
        /// <summary>The switch's own gated path runs with the ship active again.</summary>
        ReplayThroughGates,
    }

    /// <summary>The far-start predicate's answer (<c>FarWatchController.EvaluateFarStart</c>).</summary>
    internal struct FarWatchStartCheck
    {
        /// <summary>Null when the far watch may start; an entry token or unsafe:TOKEN otherwise.</summary>
        internal string Refusal;
        /// <summary>The refusal's hover reason, or null for today's hover.</summary>
        internal string Hover;
        internal bool HasVerdict;
        internal FarWatchSafetyVerdict Verdict;
        internal bool IsAllowed => Refusal == null;
    }

    /// <summary>
    /// The pure far-watch decisions of design-far-watch.md 8.4 / 8.5: the takeover and hand-back
    /// distances, the W step, the return-point check and the overlay clause.
    /// </summary>
    internal static class FarWatchDecisions
    {
        internal const double TakeoverDistanceMeters = 290_000.0;
        internal const double HandbackDistanceMeters = 250_000.0;
        internal const double NearWatchRangeMeters = 300_000.0;

        /// <summary>
        /// Takeover from the near watch (8.4): the watched ghost has reached 290 km from the ship
        /// while no far watch runs. The switch completes before 300 km. Since step 4 the body
        /// does not matter: a near-watched ghost that crosses into another SOI before 290 km is
        /// taken over as soon as its distance qualifies.
        /// </summary>
        internal static bool ShouldTakeOver(bool watchingGhost, FarWatchState state, double distanceToShip)
        {
            return watchingGhost && state == FarWatchState.Idle
                && IsFinite(distanceToShip) && distanceToShip >= TakeoverDistanceMeters;
        }

        /// <summary>
        /// A near watch is today's entry (8.4): the ghost on the ship's body within 300 km. A
        /// ghost at another body is never near, whatever its distance (today's near watch keeps
        /// its same-body guard), so it always needs the far watch.
        /// </summary>
        internal static bool IsWithinNearWatch(bool ghostOnShipBody, double distanceToShip)
        {
            return ghostOnShipBody && IsFinite(distanceToShip) && distanceToShip < NearWatchRangeMeters;
        }

        /// <summary>
        /// Whether a follow start on an idle far watch goes straight to the drone (12.2): any
        /// ghost not within today's near watch. The seam's <c>FarWatch op=start</c> and the
        /// map Watch entry read this one predicate.
        /// </summary>
        internal static bool IsDirectFarStart(bool follow, bool ghostOnShipBody, bool withinVisualRange)
        {
            return follow && !(ghostOnShipBody && withinVisualRange);
        }

        /// <summary>
        /// Hand back for distance (8.4): the watched ghost is within 250 km of the ship on the
        /// ship's body. A ghost at another body never triggers it (its distance to the ship is
        /// across an SOI boundary, and the near watch it would hand to needs the same body).
        /// </summary>
        internal static bool ShouldHandBackForDistance(FarWatchState state, bool ghostOnShipBody, double distanceToShip)
        {
            return state == FarWatchState.Following && ghostOnShipBody
                && IsFinite(distanceToShip) && distanceToShip < HandbackDistanceMeters;
        }

        /// <summary>
        /// Today's 305 km near-watch exit does not apply while a far watch runs: the drone is the
        /// active vessel (or about to be), so the distance it reads is not the ship's.
        /// </summary>
        internal static bool SuppressNearWatchCutoff(FarWatchState state)
        {
            return state != FarWatchState.Idle;
        }

        /// <summary>
        /// The W / Watch step (12.2), one decision for the button's enabled state and for the
        /// click (D1). <paramref name="isWatchedIndex"/> is W on the ghost already watched (today's
        /// toggle off). <paramref name="startAllowed"/> is the full far-start predicate (entry
        /// refusals and the ship's safety, <see cref="CombineStartRefusal"/>). While a far watch
        /// runs only the settled Following state takes a W: a follow switch when it follows a
        /// ghost, a hand-back for a ghost near the ship.
        /// </summary>
        internal static FarWatchWatchRequest DecideWatchRequest(
            FarWatchState state, bool followingGhost, bool ghostResolvable, bool isWatchedIndex,
            bool ghostOnShipBody, double distanceToShip, bool startAllowed)
        {
            if (isWatchedIndex)
                return FarWatchWatchRequest.NearWatch;
            bool finite = IsFinite(distanceToShip);
            bool near = IsWithinNearWatch(ghostOnShipBody, distanceToShip);
            if (state != FarWatchState.Idle)
            {
                if (state != FarWatchState.Following)
                    return FarWatchWatchRequest.RefusedWatchSettling;
                if (!ghostResolvable || !finite)
                    return FarWatchWatchRequest.RefusedNoReading;
                if (near)
                    return FarWatchWatchRequest.HandBackThenNearWatch;
                return followingGhost ? FarWatchWatchRequest.FollowInFarWatch : FarWatchWatchRequest.RefusedHoldWatch;
            }
            if (!ghostResolvable || !finite || near)
                return FarWatchWatchRequest.NearWatch;
            return startAllowed ? FarWatchWatchRequest.FarWatchDirect : FarWatchWatchRequest.RefusedUnsafe;
        }

        /// <summary>Whether a W decision acts (a near watch, a far start, a follow switch or a hand-back).</summary>
        internal static bool IsActionable(FarWatchWatchRequest d)
        {
            return d == FarWatchWatchRequest.NearWatch || d == FarWatchWatchRequest.FarWatchDirect
                || d == FarWatchWatchRequest.FollowInFarWatch || d == FarWatchWatchRequest.HandBackThenNearWatch;
        }

        /// <summary>
        /// The hover reason of a refused W decision: the start refusal's hover for an unsafe or
        /// refused far start, the running watch's reason otherwise; null leaves today's hover.
        /// </summary>
        internal static string WatchRequestHover(FarWatchWatchRequest d, string startRefusalHover)
        {
            switch (d)
            {
                case FarWatchWatchRequest.RefusedUnsafe: return startRefusalHover;
                case FarWatchWatchRequest.RefusedWatchSettling: return SettlingHoverText;
                case FarWatchWatchRequest.RefusedHoldWatch: return FarWatchShipSafety.StopWatchingFirstHoverText;
                default: return null;
            }
        }

        internal const string SettlingHoverText = "The camera is still moving, try again in a moment";
        internal const string EvaHoverText = "Board your ship first to follow this far";
        internal const string NoShipHoverText = "Fly your own ship to follow this far";

        /// <summary>
        /// The one far-start refusal (8.1 to 8.3): the entry refusal first, then the ship's
        /// safety verdict as unsafe:TOKEN. Null when the far watch may start.
        /// </summary>
        internal static string CombineStartRefusal(string entryRefusal, bool verdictAllowed, FarWatchRefusal verdictReason)
        {
            if (entryRefusal != null)
                return entryRefusal;
            return verdictAllowed ? null : "unsafe:" + FarWatchShipSafety.RefusalToken(verdictReason);
        }

        /// <summary>
        /// The hover reason of a far start refused for <paramref name="entryRefusal"/> (or, with
        /// none, for the verdict): a pending window reads busy, a Re-Fly its own line, a kerbal on
        /// EVA or a ghost / drone focus its own sentence, a running drone the far-watch block;
        /// a ghost that cannot be followed (no reading) leaves today's hover (null).
        /// </summary>
        internal static string StartRefusalHover(string entryRefusal, FarWatchRefusal verdictReason, string stockText)
        {
            if (entryRefusal == null)
                return FarWatchShipSafety.HoverText(verdictReason, stockText);
            if (FarWatchTransitions.IsPendingWindowRefusal(entryRefusal))
                return FarWatchShipSafety.BusyHoverText;
            switch (entryRefusal)
            {
                case "refly-active": return FarWatchShipSafety.ReFlyHoverText;
                case "active-is-eva": return EvaHoverText;
                case "already-active": return FarWatchShipSafety.NotWhileWatchingFarHoverText;
                case "not-flight":
                case "no-active-vessel":
                case "active-is-ghost-or-drone": return NoShipHoverText;
                case "ghost-not-resolvable": return null;
                default: return FarWatchShipSafety.SafetyHoverText;
            }
        }

        /// <summary>
        /// A player's switch (map Switch To, alarm Jump) while a far watch runs (8.5, ruled): the
        /// hand-back comes first and the switch's gates (the Re-Fly refusal, the pre-switch
        /// Merge / Discard dialog, the intent arm) run only once the ship is active, so none of
        /// them reads the drone as the active vessel.
        /// </summary>
        internal static FarWatchSwitchRoute DecideOwnedVesselSwitch(bool farWatchRunning)
        {
            return farWatchRunning ? FarWatchSwitchRoute.EndWatchThenReplay : FarWatchSwitchRoute.RunGates;
        }

        /// <summary>
        /// The far-start check over an entry refusal (null = entry allowed) and the safety
        /// admission (8.1 to 8.3): an entry refusal wins and the admission is not run; otherwise
        /// the admission's verdict decides, and a refused verdict's hover is the safety reason.
        /// </summary>
        internal static FarWatchStartCheck ComposeStartCheck(string entry, Func<FarWatchSafetyVerdict> admit,
            Func<FarWatchSafetyVerdict, string> safetyHover)
        {
            var c = new FarWatchStartCheck();
            FarWatchRefusal reason = FarWatchRefusal.None;
            bool allowed = true;
            if (entry == null && admit != null)
            {
                c.Verdict = admit();
                c.HasVerdict = true;
                allowed = c.Verdict.IsAllowed;
                reason = c.Verdict.Reason;
            }
            c.Refusal = CombineStartRefusal(entry, allowed, reason);
            if (c.Refusal != null)
                c.Hover = entry == null
                    ? (safetyHover != null ? safetyHover(c.Verdict) : null)
                    : StartRefusalHover(entry, reason, null);
            return c;
        }

        /// <summary>
        /// What an abort does with a switch the player asked for during the watch: replayed
        /// through the switch gates when the abort resolves at once (the ship kept or got focus
        /// back), dropped when the watch waits in Aborted for a scene exit (no hand-back).
        /// </summary>
        internal static FarWatchAbortSwitch DecideAbortSwitch(bool resolvedAtOnce, bool hasPendingSwitch)
        {
            if (!hasPendingSwitch)
                return FarWatchAbortSwitch.None;
            return resolvedAtOnce ? FarWatchAbortSwitch.Replayed : FarWatchAbortSwitch.Dropped;
        }

        /// <summary>The switch after the hand-back (8.5): replayed through its own gates, never bare.</summary>
        internal static FarWatchPostSwitch DecidePostTeardownSwitch(bool hasTarget, bool targetAlive, bool targetIsActive, bool hasGatedReplay)
        {
            if (!hasTarget)
                return FarWatchPostSwitch.None;
            if (!targetAlive || targetIsActive)
                return FarWatchPostSwitch.DropTargetGoneOrActive;
            return hasGatedReplay ? FarWatchPostSwitch.ReplayThroughGates : FarWatchPostSwitch.DropNoGatedPath;
        }

        /// <summary>
        /// Whether a W decision goes to today's near-watch entry: a near watch (or the toggle off),
        /// and while no far watch runs every refused far start too, where today's entry applies its
        /// own range and body guards. While a far watch runs a refusal does nothing.
        /// </summary>
        internal static bool RoutesToTodaysEntry(FarWatchWatchRequest d, FarWatchState state)
        {
            if (d == FarWatchWatchRequest.NearWatch)
                return true;
            if (d == FarWatchWatchRequest.FarWatchDirect || d == FarWatchWatchRequest.FollowInFarWatch
                || d == FarWatchWatchRequest.HandBackThenNearWatch)
                return false;
            return state == FarWatchState.Idle;
        }

        /// <summary>The return point has come (8.4: compared every physics tick in game time).</summary>
        internal static bool IsReturnDue(double nowUT, double returnUT)
        {
            return !double.IsNaN(returnUT) && nowUT >= returnUT;
        }

        /// <summary>
        /// A recomputation of the ship's verdict is due (8.4): a trigger name, or null. The ship's
        /// body changed (soi), its orbit moved off the cached one (orbit-delta), its node or
        /// alarm list changed (node / alarm), or 600 s of game time passed (drift); never more
        /// than once per real second.
        /// </summary>
        internal static string RecheckTrigger(
            bool bodyChanged, bool orbitChanged, bool nodesChanged, bool alarmsChanged,
            double gameSecondsSinceLast, double realSecondsSinceLast)
        {
            if (realSecondsSinceLast < 1.0)
                return null;
            if (bodyChanged) return "soi";
            if (orbitChanged) return "orbit-delta";
            if (nodesChanged) return "node";
            if (alarmsChanged) return "alarm";
            if (gameSecondsSinceLast >= 600.0) return "drift";
            return null;
        }

        /// <summary>
        /// The drone's inertial velocity carried across an SOI hop of the ghost (6.3 step 2), the
        /// way stock carries a vessel's at an SOI change (<c>Orbit.cs:533</c>:
        /// <c>vel + from.GetFrameVelAtUT(UT) - to.GetFrameVelAtUT(UT)</c>, Zup). The frame
        /// velocities are Zup (<c>CelestialBody.GetFrameVelAtUT</c>); the result is in world axes
        /// like <paramref name="worldObtVelFrom"/>.
        /// </summary>
        internal static Vector3d CarryObtVelocityAcrossBodies(Vector3d worldObtVelFrom, Vector3d fromFrameVelZup, Vector3d toFrameVelZup)
        {
            return worldObtVelFrom + (fromFrameVelZup - toFrameVelZup).xzy;
        }

        /// <summary>The gee window is restarted when the ghost's body changes (an unknown previous body never restarts it).</summary>
        internal static bool ShouldRestartGeeWindow(string previousBody, string body)
        {
            return previousBody != null && !string.Equals(previousBody, body, StringComparison.Ordinal);
        }

        /// <summary>The once-per-change key of the follow body / recording body line.</summary>
        internal static string RecordingBodyMatchKey(string body, string recordingBody)
        {
            return (body ?? "(none)") + "|" + (recordingBody ?? "none");
        }

        /// <summary>m:ss (or h:mm:ss) of game seconds, invariant.</summary>
        internal static string FormatMinutesSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                seconds = 0.0;
            long s = (long)Math.Floor(seconds);
            long h = s / 3600;
            long m = (s % 3600) / 60;
            long sec = s % 60;
            CultureInfo ic = CultureInfo.InvariantCulture;
            return h > 0
                ? string.Format(ic, "{0}:{1:00}:{2:00}", h, m, sec)
                : string.Format(ic, "{0}:{1:00}", m, sec);
        }

        /// <summary>
        /// The far-watch clause of the watch overlay's second line (8.4, ruled): the return time
        /// when there is one, and why warp is held below the requested rate. Empty when neither.
        /// </summary>
        internal static string BuildOverlayClause(double nowUT, double returnUT, string warpLimitCause, string ghostBodyName)
        {
            string ret = double.IsNaN(returnUT) ? null : "return in " + FormatMinutesSeconds(returnUT - nowUT);
            string warp = null;
            if (warpLimitCause == "return")
                warp = "warp limited: return in " + FormatMinutesSeconds(returnUT - nowUT);
            else if (warpLimitCause == "stock" || warpLimitCause == "floor")
                warp = "warp limited: near " + (string.IsNullOrEmpty(ghostBodyName) ? "the ground" : ghostBodyName);
            if (warp != null && warpLimitCause == "return")
                return warp;
            if (ret != null && warp != null)
                return ret + ", " + warp;
            return ret ?? warp ?? string.Empty;
        }

        /// <summary>The forced-return screen message (ruled: one per event).</summary>
        internal static string ForcedReturnMessage(FarWatchReturnCause cause, string shipName)
        {
            string ship = string.IsNullOrEmpty(shipName) ? "your ship" : shipName;
            switch (cause)
            {
                case FarWatchReturnCause.ManeuverNode:
                    return "Back to " + ship + ": its maneuver node is coming up";
                case FarWatchReturnCause.Alarm:
                    return "Back to " + ship + ": an alarm for it is coming up";
                default:
                    return "Back to " + ship + ": its path reaches air or ground soon";
            }
        }

        /// <summary>The snap-back screen message when the far watch was refused (ruled: stock's reason).</summary>
        internal static string SnapBackMessage(string reasonText)
        {
            return string.IsNullOrEmpty(reasonText) ? "Watch ended at 300 km" : "Watch ended at 300 km: " + reasonText;
        }

        private static bool IsFinite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }
    }

    /// <summary>
    /// The takeover attempts of one near watch (8.4): at 290 km and beyond the takeover is tried
    /// at most once per real second while it keeps being refused, whatever refused it (an entry
    /// refusal never reaches the safety check, so the verdict's time cannot pace it), and the
    /// 300 km snap-back message names the last refusal's own reason.
    /// </summary>
    internal sealed class FarWatchTakeoverGate
    {
        internal const double RetrySeconds = 1.0;

        private string refusedRecordingId;
        private double refusedAtRealTime = double.NegativeInfinity;
        private string refusalToken;
        private string refusalHover;

        internal string RefusedRecordingId => refusedRecordingId;
        internal string RefusalToken => refusalToken;

        /// <summary>False within <see cref="RetrySeconds"/> of a refusal of the same watch.</summary>
        internal bool ShouldAttempt(string recordingId, double realNow)
        {
            if (refusedRecordingId == null || refusedRecordingId != recordingId)
                return true;
            return realNow - refusedAtRealTime >= RetrySeconds;
        }

        /// <summary>The first attempt of this watch (the requested line is logged at Info once).</summary>
        internal bool IsFirstAttempt(string recordingId)
        {
            return refusedRecordingId == null || refusedRecordingId != recordingId;
        }

        /// <summary>True when the refusal reason changed (logged at once, otherwise rate-limited).</summary>
        internal bool RecordRefusal(string recordingId, double realNow, string token, string hover)
        {
            bool changed = refusedRecordingId != recordingId || refusalToken != token;
            refusedRecordingId = recordingId;
            refusedAtRealTime = realNow;
            refusalToken = token;
            refusalHover = hover;
            return changed;
        }

        internal void Clear()
        {
            refusedRecordingId = null;
            refusedAtRealTime = double.NegativeInfinity;
            refusalToken = null;
            refusalHover = null;
        }

        /// <summary>
        /// The snap-back message for the watch of <paramref name="recordingId"/> when its takeover
        /// was refused (one per event, ruled), or null; the refusal is consumed.
        /// </summary>
        internal string TakeSnapBackMessage(string recordingId)
        {
            if (refusedRecordingId == null || refusedRecordingId != recordingId)
                return null;
            string msg = FarWatchDecisions.SnapBackMessage(refusalHover);
            Clear();
            return msg;
        }
    }
}
