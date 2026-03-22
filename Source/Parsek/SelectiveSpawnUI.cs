using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Lightweight data for a nearby ghost craft eligible for real spawn.
    /// Built by the proximity scan in ParsekFlight, consumed by the Real Spawn Control window.
    /// </summary>
    internal struct NearbySpawnCandidate
    {
        public int recordingIndex;
        public string vesselName;
        public double endUT;
        public double distance;
        public string recordingId;
    }

    /// <summary>
    /// Pure static methods for the Real Spawn Control UI: determining which nearby
    /// ghost craft can be warped to for real-vessel interaction, and formatting UI text.
    ///
    /// The player approaches a ghost vessel and opens the Real Spawn Control window
    /// to fast-forward to the moment the ghost becomes a real craft. The window lists
    /// all nearby ghost craft sorted by distance, with per-craft warp buttons.
    /// </summary>
    internal static class SelectiveSpawnUI
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly System.Text.StringBuilder SharedSB = new System.Text.StringBuilder(64);

        // Kerbin time: 6-hour days (21600s), 426-day years
        // Earth time: 24-hour days (86400s), 365-day years
        private const int KerbinDaySec = 21600;
        private const int KerbinYearDays = 426;
        private const int EarthDaySec = 86400;
        private const int EarthYearDays = 365;

        /// <summary>
        /// Test hook: when non-null, overrides GameSettings.KERBIN_TIME for unit tests.
        /// </summary>
        internal static bool? KerbinTimeOverrideForTesting;

        internal static bool UseKerbinTime =>
            KerbinTimeOverrideForTesting ?? GameSettings.KERBIN_TIME;

        internal static void GetDayAndYearConstants(out int daySec, out int yearDays)
        {
            if (UseKerbinTime)
            {
                daySec = KerbinDaySec;
                yearDays = KerbinYearDays;
            }
            else
            {
                daySec = EarthDaySec;
                yearDays = EarthYearDays;
            }
        }

        /// <summary>
        /// Pure: determine whether a ghost qualifies as a spawn candidate.
        /// True when endUT is in the future, spawn is needed, not suppressed, and within range.
        /// </summary>
        internal static bool IsSpawnCandidate(
            double endUT, double currentUT,
            bool needsSpawn, bool chainSuppressed,
            double distance, double proximityRadius)
        {
            return endUT > currentUT
                && needsSpawn
                && !chainSuppressed
                && distance <= proximityRadius;
        }

        /// <summary>
        /// Pure: find the candidate with the earliest endUT in the future.
        /// Returns null if no candidates qualify.
        /// </summary>
        internal static NearbySpawnCandidate? FindNextSpawnCandidate(
            List<NearbySpawnCandidate> candidates, double currentUT)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            NearbySpawnCandidate? best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].endUT > currentUT)
                {
                    if (best == null || candidates[i].endUT < best.Value.endUT)
                        best = candidates[i];
                }
            }
            return best;
        }

        /// <summary>
        /// Pure: format a time delta as human-readable string.
        /// Under 60s: "{s}s". Under 3600s: "{m}m {s}s". Otherwise: "{h}h {m}m".
        /// Uses Kerbin or Earth time based on GameSettings.KERBIN_TIME for day/year boundaries.
        /// (Currently only shows up to hours, so day length doesn't affect output.)
        /// </summary>
        internal static string FormatTimeDelta(double seconds)
        {
            if (seconds < 0) seconds = 0;

            if (seconds < 60)
                return string.Format(IC, "{0}s", ((long)seconds).ToString(IC));

            if (seconds < 3600)
            {
                long m = (long)(seconds / 60);
                long s = (long)(seconds % 60);
                return string.Format(IC, "{0}m {1}s", m.ToString(IC), s.ToString(IC));
            }

            long h = (long)(seconds / 3600);
            long min = (long)((seconds % 3600) / 60);
            return string.Format(IC, "{0}h {1}m", h.ToString(IC), min.ToString(IC));
        }

        /// <summary>
        /// Pure: format a countdown string "T-Xd Xh Xm Xs" from a time delta.
        /// Hides zero leading components (no years if 0, no days if 0, etc.).
        /// Uses Kerbin time (6h days, 426-day years) or Earth time (24h days, 365-day years)
        /// based on GameSettings.KERBIN_TIME.
        /// Returns "T+..." for negative deltas (event in the past).
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

            var parts = SharedSB;
            parts.Clear();
            parts.Append(prefix);
            bool started = false;

            if (years > 0)
            {
                parts.Append(years.ToString(IC));
                parts.Append("y ");
                started = true;
            }
            if (started || days > 0)
            {
                parts.Append(days.ToString(IC));
                parts.Append("d ");
                started = true;
            }
            if (started || hours > 0)
            {
                parts.Append(hours.ToString(IC));
                parts.Append("h ");
                started = true;
            }
            if (started || minutes > 0)
            {
                parts.Append(minutes.ToString(IC));
                parts.Append("m ");
            }
            parts.Append(seconds.ToString(IC));
            parts.Append('s');

            return parts.ToString();
        }

        /// <summary>
        /// Pure: format the tooltip for the "Warp to Next Spawn" button.
        /// </summary>
        internal static string FormatNextSpawnTooltip(
            NearbySpawnCandidate? candidate, double currentUT)
        {
            if (candidate == null)
                return "No nearby craft to spawn";

            double delta = candidate.Value.endUT - currentUT;
            return string.Format(IC,
                "Warp to {0} (spawns in {1})",
                candidate.Value.vesselName, FormatTimeDelta(delta));
        }
    }
}
