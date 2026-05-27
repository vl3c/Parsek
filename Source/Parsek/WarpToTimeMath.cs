using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure helpers for the Timeline "Warp to time" control: KSP-calendar date -> UT
    /// conversion, integer field parsing/validation, and the warp-plan decision. All
    /// methods are deterministic and Unity-free so they can be unit-tested directly.
    /// </summary>
    internal static class WarpToTimeMath
    {
        /// <summary>
        /// Minimum |target - now| (seconds) for a warp to do anything. Anything within
        /// this band is treated as "already at target" (no-op). Matches
        /// <see cref="TimeJumpManager.IsValidJump"/> which aborts a forward jump unless
        /// target &gt; current, so a sub-second residual never reaches ExecuteForwardJump.
        /// </summary>
        internal const double AtTargetEpsilonSeconds = 1.0;

        internal enum WarpFieldKind { Year, Day, Hour, Minute }

        internal enum WarpPlanKind { ForwardOnly, RewindThenForward, AtTarget, Unreachable }

        internal readonly struct WarpPlan
        {
            internal WarpPlan(WarpPlanKind kind, bool requiresFlightExit,
                bool landsAtTimelineStart, string reason)
            {
                Kind = kind;
                RequiresFlightExit = requiresFlightExit;
                LandsAtTimelineStart = landsAtTimelineStart;
                Reason = reason ?? "";
            }

            internal WarpPlanKind Kind { get; }
            /// <summary>True when in flight: save the recording and return to KSC first.</summary>
            internal bool RequiresFlightExit { get; }
            /// <summary>RewindThenForward to the earliest launch (target precedes all launches);
            /// no forward jump follows.</summary>
            internal bool LandsAtTimelineStart { get; }
            /// <summary>Reason for AtTarget / Unreachable (disabled-button tooltip).</summary>
            internal string Reason { get; }
        }

        /// <summary>
        /// Converts a KSP-calendar date (Year 1 / Day 1 = game start, UT 0) to UT seconds.
        /// Year/Day are 1-based; values of 0 or below are treated as 1 (game start).
        /// Hour/Minute are added arithmetically (overflow rolls over, e.g. Hour 30 = +1d 6h),
        /// matching the rest of the codebase, which has no exposed hours-per-day constant.
        /// Respects the Kerbin (6h day / 426 d yr) vs Earth (24h / 365 d) calendar setting.
        /// </summary>
        internal static double ComputeTargetUT(int year, int day, int hour, int minute)
        {
            ParsekTimeFormat.GetDayAndYearConstants(out int secsPerDay, out int daysPerYear);
            long secsPerYear = (long)daysPerYear * secsPerDay;

            int y = year < 1 ? 1 : year;
            int d = day < 1 ? 1 : day;
            int h = hour < 0 ? 0 : hour;
            int m = minute < 0 ? 0 : minute;

            return (y - 1) * (double)secsPerYear
                 + (d - 1) * (double)secsPerDay
                 + h * 3600.0
                 + m * 60.0;
        }

        /// <summary>
        /// Inverse of <see cref="ComputeTargetUT"/>: decomposes a UT into the Year / Day / Hour /
        /// Minute fields the warp UI uses (Year/Day 1-based, Hour/Minute 0-based), respecting the
        /// Kerbin vs Earth calendar. Truncates to the whole MINUTE (drops seconds), so
        /// <c>ComputeTargetUT(ComputeComponentsFromUT(ut)) &lt;= ut</c> and is within one minute of it
        /// - lossless to the minute, which is all the warp UI carries. A negative / NaN / infinite ut
        /// clamps to 0 (Year 1, Day 1, 0:00). Pure.
        /// </summary>
        internal static void ComputeComponentsFromUT(
            double ut, out int year, out int day, out int hour, out int minute)
        {
            ParsekTimeFormat.GetDayAndYearConstants(out int secsPerDay, out int daysPerYear);
            long secsPerYear = (long)daysPerYear * secsPerDay;

            if (double.IsNaN(ut) || double.IsInfinity(ut) || ut < 0.0)
                ut = 0.0;

            long total = (long)ut; // floor to the second; the minute division below drops seconds
            year = (int)(total / secsPerYear) + 1;
            long rem = total % secsPerYear;
            day = (int)(rem / secsPerDay) + 1;
            rem %= secsPerDay;
            hour = (int)(rem / 3600L);
            rem %= 3600L;
            minute = (int)(rem / 60L);
        }

        /// <summary>
        /// Parses a draft string into a validated integer field value. Returns false for
        /// non-integer / empty input (caller keeps the prior committed value). Year/Day
        /// floor at 1; Hour/Minute floor at 0.
        /// </summary>
        internal static bool TryParseField(WarpFieldKind kind, string draft, out int value)
        {
            value = (kind == WarpFieldKind.Year || kind == WarpFieldKind.Day) ? 1 : 0;
            if (string.IsNullOrWhiteSpace(draft))
                return false;

            if (!int.TryParse(draft.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                return false;

            switch (kind)
            {
                case WarpFieldKind.Year:
                case WarpFieldKind.Day:
                    value = parsed < 1 ? 1 : parsed;
                    break;
                default:
                    value = parsed < 0 ? 0 : parsed;
                    break;
            }
            return true;
        }

        /// <summary>
        /// Pure rewind-target selection. Given the StartUTs of candidate launches (each owning
        /// a usable rewind save), returns the index to rewind to: the candidate with the
        /// greatest StartUT &lt;= targetUT (nearest prior launch), or, when none qualifies
        /// (target precedes all launches), the candidate with the smallest StartUT (earliest
        /// launch = start of the timeline, with <paramref name="landsAtTimelineStart"/> set).
        /// Returns -1 when there are no candidates.
        /// </summary>
        internal static int SelectRewindTargetIndex(
            IReadOnlyList<double> startUTs, double targetUT, out bool landsAtTimelineStart)
        {
            landsAtTimelineStart = false;
            if (startUTs == null || startUTs.Count == 0)
                return -1;

            int nearestPrior = -1;
            int earliest = -1;
            for (int i = 0; i < startUTs.Count; i++)
            {
                if (earliest < 0 || startUTs[i] < startUTs[earliest])
                    earliest = i;
                if (startUTs[i] <= targetUT
                    && (nearestPrior < 0 || startUTs[i] > startUTs[nearestPrior]))
                    nearestPrior = i;
            }

            if (nearestPrior >= 0)
                return nearestPrior;

            landsAtTimelineStart = true;
            return earliest;
        }

        /// <summary>
        /// Decides what the warp button should do for the given target relative to now.
        /// <paramref name="hasRewindTarget"/> = a rewind-target launch was resolved
        /// (nearest-prior OR earliest fallback). <paramref name="landsAtTimelineStart"/> =
        /// that target is the earliest launch and is itself after targetUT (so no forward
        /// jump will follow). Recording state does NOT gate: the flight flow commits the
        /// recording as part of the exit.
        /// </summary>
        internal static WarpPlan DecideWarpPlan(
            double targetUT, double currentUT, bool inFlight,
            bool hasRewindTarget, bool landsAtTimelineStart)
        {
            if (targetUT > currentUT + AtTargetEpsilonSeconds)
                return new WarpPlan(WarpPlanKind.ForwardOnly, inFlight, false, "");

            if (Math.Abs(targetUT - currentUT) <= AtTargetEpsilonSeconds)
                return new WarpPlan(WarpPlanKind.AtTarget, false, false,
                    "Already at this time");

            // targetUT < currentUT - epsilon
            if (hasRewindTarget)
                return new WarpPlan(WarpPlanKind.RewindThenForward, inFlight,
                    landsAtTimelineStart, "");

            return new WarpPlan(WarpPlanKind.Unreachable, false, false,
                "No launch save to rewind to");
        }
    }
}
