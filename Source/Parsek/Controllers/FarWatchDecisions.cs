using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>What a W / Watch request does (design-far-watch.md 8.4, 12.2).</summary>
    internal enum FarWatchWatchRequest
    {
        /// <summary>Today's near watch (ghost within 300 km of the ship, no far watch running).</summary>
        NearWatch,
        /// <summary>A far ghost on the ship's body with the ship safe: the far watch starts directly.</summary>
        FarWatchDirect,
        /// <summary>A far watch runs and the ghost is near the ship: hand back first, then near watch.</summary>
        HandBackThenNearWatch,
        /// <summary>A far watch runs and the ghost is far from the ship: the drone follows it.</summary>
        FollowInFarWatch,
        /// <summary>A far ghost on the ship's body with the ship unsafe: refused with the safety reason.</summary>
        RefusedUnsafe,
        /// <summary>Another body (a later step) or no reading.</summary>
        RefusedOtherBody,
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
        /// Takeover from the near watch (8.4): the watched ghost on the ship's body has reached
        /// 290 km from the ship while no far watch runs. The switch completes before 300 km.
        /// </summary>
        internal static bool ShouldTakeOver(bool watchingGhost, FarWatchState state, bool ghostOnShipBody, double distanceToShip)
        {
            return watchingGhost && state == FarWatchState.Idle && ghostOnShipBody
                && IsFinite(distanceToShip) && distanceToShip >= TakeoverDistanceMeters;
        }

        /// <summary>
        /// Hand back for distance (8.4): the watched ghost is within 250 km of the ship on the
        /// ship's body. A ghost at another body never triggers it.
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

        /// <summary>The W / Watch step (12.2).</summary>
        internal static FarWatchWatchRequest DecideWatchRequest(
            FarWatchState state, bool ghostOnShipBody, double distanceToShip, bool admissionAllowed)
        {
            bool near = IsFinite(distanceToShip) && distanceToShip < NearWatchRangeMeters;
            if (state != FarWatchState.Idle)
            {
                if (ghostOnShipBody && near)
                    return FarWatchWatchRequest.HandBackThenNearWatch;
                return ghostOnShipBody ? FarWatchWatchRequest.FollowInFarWatch : FarWatchWatchRequest.RefusedOtherBody;
            }
            if (!ghostOnShipBody)
                return FarWatchWatchRequest.RefusedOtherBody;
            if (near)
                return FarWatchWatchRequest.NearWatch;
            return admissionAllowed ? FarWatchWatchRequest.FarWatchDirect : FarWatchWatchRequest.RefusedUnsafe;
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
            return "Watch ended at 300 km: " + (string.IsNullOrEmpty(reasonText) ? FarWatchShipSafety.SafetyHoverText : reasonText);
        }

        private static bool IsFinite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }
    }
}
