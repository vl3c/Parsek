using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Centralized time formatting that respects KSP's calendar settings.
    /// Kerbin calendar: 6-hour days, 426-day years.
    /// Earth calendar: 24-hour days, 365-day years.
    /// All time display across the mod should use these methods to ensure
    /// consistency with the player's GameSettings.KERBIN_TIME choice.
    /// </summary>
    internal static class ParsekTimeFormat
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Test hook: overrides GameSettings.KERBIN_TIME for unit tests.</summary>
        internal static bool? KerbinTimeOverrideForTesting;

        internal static bool UseKerbinTime =>
            KerbinTimeOverrideForTesting ?? GameSettings.KERBIN_TIME;

        internal static int SecsPerDay => UseKerbinTime ? 21600 : 86400;
        internal static int DaysPerYear => UseKerbinTime ? 426 : 365;
        internal static int SecsPerYear => DaysPerYear * SecsPerDay;

        internal static void GetDayAndYearConstants(out int daySec, out int yearDays)
        {
            if (UseKerbinTime)
            {
                daySec = 21600;
                yearDays = 426;
            }
            else
            {
                daySec = 86400;
                yearDays = 365;
            }
        }

        /// <summary>
        /// Compact duration: top 2 units. "5s", "2m 23s", "2h 0m", "3d 2h", "1y 42d".
        /// </summary>
        internal static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            int total = (int)seconds;
            if (total < 60) return total.ToString(IC) + "s";
            if (total < 3600) return (total / 60).ToString(IC) + "m " + (total % 60).ToString(IC) + "s";

            int secsPerDay = SecsPerDay;
            int secsPerYear = SecsPerYear;

            if (total < secsPerDay)
                return (total / 3600).ToString(IC) + "h " + ((total % 3600) / 60).ToString(IC) + "m";

            if (total < secsPerYear)
            {
                int days = total / secsPerDay;
                int hours = (total % secsPerDay) / 3600;
                return hours > 0
                    ? days.ToString(IC) + "d " + hours.ToString(IC) + "h"
                    : days.ToString(IC) + "d";
            }

            int years = total / secsPerYear;
            int remDays = (total % secsPerYear) / secsPerDay;
            return remDays > 0
                ? years.ToString(IC) + "y " + remDays.ToString(IC) + "d"
                : years.ToString(IC) + "y";
        }

        /// <summary>
        /// Full duration: all non-zero components, comma-separated. "1y, 2d, 3h, 4m, 5s".
        /// Returns empty string for zero or negative.
        /// </summary>
        internal static string FormatDurationFull(double seconds)
        {
            if (seconds <= 0) return "";

            GetDayAndYearConstants(out int daySec, out int yearDays);
            int hoursPerDay = daySec / 3600;

            long totalSeconds = (long)seconds;
            long s = totalSeconds % 60;
            long totalMinutes = totalSeconds / 60;
            long m = totalMinutes % 60;
            long totalHours = totalMinutes / 60;
            long h = totalHours % hoursPerDay;
            long totalDays = totalHours / hoursPerDay;
            long d = totalDays % yearDays;
            long y = totalDays / yearDays;

            var parts = new System.Collections.Generic.List<string>(5);
            if (y > 0) parts.Add(y.ToString(IC) + "y");
            if (d > 0) parts.Add(d.ToString(IC) + "d");
            if (h > 0) parts.Add(h.ToString(IC) + "h");
            if (m > 0) parts.Add(m.ToString(IC) + "m");
            if (s > 0) parts.Add(s.ToString(IC) + "s");

            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "";
        }

        /// <summary>
        /// Countdown: "T-2d 3h 15m 5s" with T+/T- prefix.
        /// Shows all components from first non-zero downward.
        /// </summary>
        internal static string FormatCountdown(double deltaSeconds)
        {
            string prefix = "T-";
            if (deltaSeconds < 0)
            {
                prefix = "T+";
                deltaSeconds = -deltaSeconds;
            }

            GetDayAndYearConstants(out int daySec, out int yearDays);
            long yearSec = (long)yearDays * daySec;

            long totalSec = (long)deltaSeconds;
            long years = totalSec / yearSec;
            totalSec %= yearSec;
            long days = totalSec / daySec;
            totalSec %= daySec;
            long hours = totalSec / 3600;
            totalSec %= 3600;
            long minutes = totalSec / 60;
            long seconds = totalSec % 60;

            var sb = new System.Text.StringBuilder(32);
            sb.Append(prefix);
            bool started = false;

            if (years > 0)
            {
                sb.Append(years.ToString(IC)).Append("y ");
                started = true;
            }
            if (started || days > 0)
            {
                sb.Append(days.ToString(IC)).Append("d ");
                started = true;
            }
            if (started || hours > 0)
            {
                sb.Append(hours.ToString(IC)).Append("h ");
                started = true;
            }
            if (started || minutes > 0)
            {
                sb.Append(minutes.ToString(IC)).Append("m ");
            }
            sb.Append(seconds.ToString(IC)).Append('s');

            return sb.ToString();
        }

        /// <summary>Reset test overrides. Call in test Dispose.</summary>
        internal static void ResetForTesting()
        {
            KerbinTimeOverrideForTesting = null;
        }
    }
}
