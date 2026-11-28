using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>How Parsek's own walk of a vessel's on-rails path ended (design-far-watch.md 8.1).</summary>
    internal enum FarWatchPathEnding
    {
        /// <summary>A stable final conic: nothing is ahead but that conic.</summary>
        Final,
        /// <summary>A surface impact or an atmosphere's deletion periapsis.</summary>
        Impact,
        /// <summary>The walk stopped on its limits; nothing is known past its end.</summary>
        Truncated,
        /// <summary>The walk was refused at its start (missing body, invalid state).</summary>
        Failed,
    }

    internal enum FarWatchVerdictKind
    {
        Allowed,
        AllowedWithReturn,
        Refused,
    }

    /// <summary>Why a far watch is refused (closed; maps to the hover text and the log).</summary>
    internal enum FarWatchRefusal
    {
        None,
        ReFly,
        Busy,
        PathUnknown,
        NotInAtmosphere,
        NotUnderAcceleration,
        NotWhileMovingOverSurface,
        NotWhileAboutToCrash,
        NotWhileOnALadder,
        NotWhileThrottledUp,
        DockingCapture,
        CrewedShipNearbyInAir,
        ReturnTooClose,
    }

    /// <summary>What the return point is for (the forced-return screen message names it).</summary>
    internal enum FarWatchReturnCause
    {
        None,
        Danger,
        ManeuverNode,
        Alarm,
    }

    /// <summary>A body as the safety check needs it.</summary>
    internal struct FarWatchSafetyBody
    {
        internal string Name;
        internal double Radius;
        /// <summary>0 without air.</summary>
        internal double AtmosphereDepth;
        /// <summary>PQS radiusMax - radius; 0 without PQS.</summary>
        internal double MaxTerrainHeight;
    }

    /// <summary>A vessel's on-rails path as Parsek's own walk saw it.</summary>
    internal sealed class FarWatchSafetyPath
    {
        internal List<FarWatchConic> Segments = new List<FarWatchConic>();
        internal FarWatchPathEnding Ending = FarWatchPathEnding.Failed;
        /// <summary>Impact / Truncated: where the walk stopped.</summary>
        internal double EndUT = double.NaN;
        internal string FailureReason;
    }

    internal sealed class FarWatchSafetyVessel
    {
        internal uint Pid;
        internal Guid Guid;
        internal string Name;
        internal bool LandedOrSplashed;
        internal double SrfSpeed;
        internal bool Crewed;
        internal bool Controllable;
        internal bool DockingCaptureActive;
        internal FarWatchSafetyPath Path;
    }

    internal sealed class FarWatchSafetyInputs
    {
        internal double NowUT;
        internal ClearToSaveStatus ClearToSave = ClearToSaveStatus.CLEAR;
        internal FarWatchSafetyVessel Ship;
        internal List<FarWatchSafetyVessel> Exposed = new List<FarWatchSafetyVessel>();
        /// <summary>The first pending window's name (8.3), or null.</summary>
        internal string PendingWindow;
        internal bool ReFlyActive;
        internal List<double> ShipNodeUTs = new List<double>();
        internal List<double> ShipAlarmUTs = new List<double>();
        internal Dictionary<string, FarWatchSafetyBody> Bodies = new Dictionary<string, FarWatchSafetyBody>(StringComparer.Ordinal);
    }

    internal struct FarWatchSafetyVerdict
    {
        internal FarWatchVerdictKind Kind;
        internal double ReturnUT;
        internal FarWatchReturnCause ReturnCause;
        internal FarWatchRefusal Reason;
        internal double DangerUT;
        internal uint RefusedByPid;
        internal int SpentExposedAllowed;

        internal bool IsAllowed => Kind != FarWatchVerdictKind.Refused;
        internal bool HasReturn => Kind == FarWatchVerdictKind.AllowedWithReturn;
    }

    /// <summary>
    /// The ship-safety check of design-far-watch.md 8.2: pure over plain inputs, one rule table in
    /// order, the first refusal wins. The live gatherer is <see cref="FarWatchPathGatherer"/>.
    /// </summary>
    internal static class FarWatchShipSafety
    {
        internal const double SafeMarginMeters = 1000.0;
        internal const double LandedSlideSpeed = 0.3;
        internal const double RefuseDangerWithinSeconds = 600.0;
        internal const double DangerLeadSeconds = 300.0;
        internal const double NodeLeadSeconds = 180.0;
        internal const double AlarmLeadSeconds = 60.0;
        internal const double MinReturnSeconds = 120.0;

        /// <summary>h_safe(B) = max(atmosphere depth, max terrain height) + 1000 m.</summary>
        internal static double SafeAltitude(FarWatchSafetyBody b)
        {
            return Math.Max(Math.Max(0.0, b.AtmosphereDepth), Math.Max(0.0, b.MaxTerrainHeight)) + SafeMarginMeters;
        }

        /// <summary>
        /// The first UT at or after <paramref name="now"/> on the path where the altitude is under
        /// h_safe of the body that holds it; +inf only for a Final ending whose every conic keeps
        /// its periapsis at or above h_safe. An Impact ends at the latest at its end, a Truncated
        /// walk counts its end as danger, a Failed walk is danger now.
        /// </summary>
        internal static double ComputeDangerUT(FarWatchSafetyPath path, IDictionary<string, FarWatchSafetyBody> bodies, double now)
        {
            if (path == null || path.Ending == FarWatchPathEnding.Failed)
                return now;
            List<FarWatchConic> segs = path.Segments ?? new List<FarWatchConic>();
            for (int i = 0; i < segs.Count; i++)
            {
                FarWatchConic c = segs[i];
                FarWatchSafetyBody body;
                if (c.BodyName == null || bodies == null || !bodies.TryGetValue(c.BodyName, out body))
                    return Math.Max(now, c.StartUT);
                double end = (i == segs.Count - 1 && path.Ending == FarWatchPathEnding.Final)
                    ? double.PositiveInfinity
                    : c.EndUT;
                double from = Math.Max(now, c.StartUT);
                if (from > end)
                    continue;
                double t;
                if (FarWatchConicMath.TryFirstTimeBelowRadius(c, body.Radius + SafeAltitude(body), from, end, out t))
                    return t;
            }
            switch (path.Ending)
            {
                case FarWatchPathEnding.Final:
                    return segs.Count == 0 ? now : double.PositiveInfinity;
                default:
                    return double.IsNaN(path.EndUT) ? now : Math.Max(now, path.EndUT);
            }
        }

        internal static FarWatchSafetyVerdict Evaluate(FarWatchSafetyInputs i)
        {
            var v = new FarWatchSafetyVerdict
            {
                Kind = FarWatchVerdictKind.Refused,
                ReturnUT = double.NaN,
                DangerUT = double.PositiveInfinity,
            };
            if (i == null || i.Ship == null)
            {
                v.Reason = FarWatchRefusal.PathUnknown;
                return v;
            }
            if (i.ReFlyActive) { v.Reason = FarWatchRefusal.ReFly; return v; }
            if (!string.IsNullOrEmpty(i.PendingWindow)) { v.Reason = FarWatchRefusal.Busy; return v; }
            FarWatchSafetyVessel ship = i.Ship;
            if (!ship.LandedOrSplashed && (ship.Path == null || ship.Path.Ending == FarWatchPathEnding.Failed))
            {
                v.Reason = FarWatchRefusal.PathUnknown;
                return v;
            }
            FarWatchRefusal stock = MapClearToSave(i.ClearToSave);
            if (stock != FarWatchRefusal.None) { v.Reason = stock; return v; }
            if (ship.DockingCaptureActive) { v.Reason = FarWatchRefusal.DockingCapture; v.RefusedByPid = ship.Pid; return v; }
            if (i.Exposed != null)
            {
                for (int k = 0; k < i.Exposed.Count; k++)
                {
                    FarWatchSafetyVessel x = i.Exposed[k];
                    if (x != null && x.DockingCaptureActive)
                    {
                        v.Reason = FarWatchRefusal.DockingCapture;
                        v.RefusedByPid = x.Pid;
                        return v;
                    }
                }
            }
            if (ship.LandedOrSplashed && ship.SrfSpeed > LandedSlideSpeed)
            {
                v.Reason = FarWatchRefusal.NotWhileMovingOverSurface;
                return v;
            }
            if (i.Exposed != null)
            {
                for (int k = 0; k < i.Exposed.Count; k++)
                {
                    FarWatchSafetyVessel x = i.Exposed[k];
                    if (x == null || x.LandedOrSplashed)
                        continue;
                    double xd = ComputeDangerUT(x.Path, i.Bodies, i.NowUT);
                    if (double.IsPositiveInfinity(xd))
                        continue;
                    if (x.Crewed || x.Controllable)
                    {
                        v.Reason = FarWatchRefusal.CrewedShipNearbyInAir;
                        v.RefusedByPid = x.Pid;
                        return v;
                    }
                    v.SpentExposedAllowed++;
                }
            }

            double danger = ship.LandedOrSplashed ? double.PositiveInfinity : ComputeDangerUT(ship.Path, i.Bodies, i.NowUT);
            v.DangerUT = danger;
            double returnUT = double.PositiveInfinity;
            FarWatchReturnCause cause = FarWatchReturnCause.None;
            if (!double.IsPositiveInfinity(danger))
            {
                if (danger - i.NowUT < RefuseDangerWithinSeconds)
                {
                    v.Reason = FarWatchRefusal.ReturnTooClose;
                    return v;
                }
                returnUT = danger - DangerLeadSeconds;
                cause = FarWatchReturnCause.Danger;
            }
            double node = FirstAtOrAfter(i.ShipNodeUTs, i.NowUT);
            if (!double.IsNaN(node) && node - NodeLeadSeconds < returnUT)
            {
                returnUT = node - NodeLeadSeconds;
                cause = FarWatchReturnCause.ManeuverNode;
            }
            double alarm = FirstAtOrAfter(i.ShipAlarmUTs, i.NowUT);
            if (!double.IsNaN(alarm) && alarm - AlarmLeadSeconds < returnUT)
            {
                returnUT = alarm - AlarmLeadSeconds;
                cause = FarWatchReturnCause.Alarm;
            }
            if (double.IsPositiveInfinity(returnUT))
            {
                v.Kind = FarWatchVerdictKind.Allowed;
                v.Reason = FarWatchRefusal.None;
                return v;
            }
            if (returnUT - i.NowUT < MinReturnSeconds)
            {
                v.Reason = FarWatchRefusal.ReturnTooClose;
                return v;
            }
            v.Kind = FarWatchVerdictKind.AllowedWithReturn;
            v.Reason = FarWatchRefusal.None;
            v.ReturnUT = returnUT;
            v.ReturnCause = cause;
            return v;
        }

        internal static FarWatchRefusal MapClearToSave(ClearToSaveStatus s)
        {
            switch (s)
            {
                case ClearToSaveStatus.NOT_IN_ATMOSPHERE: return FarWatchRefusal.NotInAtmosphere;
                case ClearToSaveStatus.NOT_UNDER_ACCELERATION: return FarWatchRefusal.NotUnderAcceleration;
                case ClearToSaveStatus.NOT_WHILE_MOVING_OVER_SURFACE: return FarWatchRefusal.NotWhileMovingOverSurface;
                case ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH: return FarWatchRefusal.NotWhileAboutToCrash;
                case ClearToSaveStatus.NOT_WHILE_ON_A_LADDER: return FarWatchRefusal.NotWhileOnALadder;
                case ClearToSaveStatus.NOT_WHILE_THROTTLED_UP: return FarWatchRefusal.NotWhileThrottledUp;
                default: return FarWatchRefusal.None;
            }
        }

        /// <summary>The log token (stock's status name where stock has one).</summary>
        internal static string RefusalToken(FarWatchRefusal r)
        {
            switch (r)
            {
                case FarWatchRefusal.NotInAtmosphere: return "NOT_IN_ATMOSPHERE";
                case FarWatchRefusal.NotUnderAcceleration: return "NOT_UNDER_ACCELERATION";
                case FarWatchRefusal.NotWhileMovingOverSurface: return "NOT_WHILE_MOVING_OVER_SURFACE";
                case FarWatchRefusal.NotWhileAboutToCrash: return "NOT_WHILE_ABOUT_TO_CRASH";
                case FarWatchRefusal.NotWhileOnALadder: return "NOT_WHILE_ON_A_LADDER";
                case FarWatchRefusal.NotWhileThrottledUp: return "NOT_WHILE_THROTTLED_UP";
                default: return r.ToString();
            }
        }

        /// <summary>Stock's localization key for a ClearToSave refusal (#autoLOC_6002346..51), or null.</summary>
        internal static string StockReasonKey(FarWatchRefusal r)
        {
            switch (r)
            {
                case FarWatchRefusal.NotInAtmosphere: return "#autoLOC_6002346";
                case FarWatchRefusal.NotUnderAcceleration: return "#autoLOC_6002347";
                case FarWatchRefusal.NotWhileMovingOverSurface: return "#autoLOC_6002348";
                case FarWatchRefusal.NotWhileAboutToCrash: return "#autoLOC_6002349";
                case FarWatchRefusal.NotWhileOnALadder: return "#autoLOC_6002350";
                case FarWatchRefusal.NotWhileThrottledUp: return "#autoLOC_6002351";
                default: return null;
            }
        }

        internal const string SafetyHoverText =
            "Your ship must be landed, on the pad, or in an orbit clear of the atmosphere to follow this far";
        /// <summary>The hover line on a stock control greyed during a far watch (12.2, ruled).</summary>
        internal const string StopWatchingFirstHoverText = "Stop watching first ([ or ])";
        /// <summary>The hover line on a Parsek control greyed during a far watch (12.2, ruled).</summary>
        internal const string NotWhileWatchingFarHoverText = CameraDroneIdentity.NotWhileWatchingFarText;

        internal const string BusyHoverText = "Your ship is busy for a moment";
        internal const string ReFlyHoverText = "Not available during a Re-Fly";

        /// <summary>
        /// The hover text of a greyed far-watch control (12.2). Stock's own ClearToSave sentence
        /// when <paramref name="stockText"/> is given for a stock refusal; the ruled safety
        /// sentence otherwise.
        /// </summary>
        internal static string HoverText(FarWatchRefusal r, string stockText)
        {
            if (r == FarWatchRefusal.Busy) return BusyHoverText;
            if (r == FarWatchRefusal.ReFly) return ReFlyHoverText;
            if (StockReasonKey(r) != null && !string.IsNullOrEmpty(stockText)) return stockText;
            return SafetyHoverText;
        }

        /// <summary>The section 16.2 admission line (invariant culture; tests read it).</summary>
        internal static string FormatVerdictLine(FarWatchSafetyVerdict v, FarWatchSafetyPath shipPath, int exposedCount, string trigger)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return string.Format(ic,
                "verdict={0} reason={1} path={2} segments={3} dangerUT={4} returnUT={5} returnCause={6} exposed={7} refusedBy={8} spentAllowed={9} trigger={10}",
                v.Kind, RefusalToken(v.Reason),
                shipPath != null ? shipPath.Ending.ToString() : "none",
                shipPath != null && shipPath.Segments != null ? shipPath.Segments.Count : 0,
                double.IsPositiveInfinity(v.DangerUT) ? "inf" : v.DangerUT.ToString("F1", ic),
                double.IsNaN(v.ReturnUT) ? "none" : v.ReturnUT.ToString("F1", ic),
                v.ReturnCause,
                exposedCount,
                v.RefusedByPid == 0 ? "none" : v.RefusedByPid.ToString(ic),
                v.SpentExposedAllowed,
                string.IsNullOrEmpty(trigger) ? "start" : trigger);
        }

        private static double FirstAtOrAfter(List<double> uts, double now)
        {
            if (uts == null) return double.NaN;
            double best = double.NaN;
            for (int k = 0; k < uts.Count; k++)
            {
                double u = uts[k];
                if (double.IsNaN(u) || u < now) continue;
                if (double.IsNaN(best) || u < best) best = u;
            }
            return best;
        }
    }
}
