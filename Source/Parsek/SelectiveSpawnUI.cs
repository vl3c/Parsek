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
        private const string Tag = "SpawnControl";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

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
        /// </summary>
        internal static string FormatTimeDelta(double seconds)
        {
            if (seconds < 0) seconds = 0;

            if (seconds < 60)
                return string.Format(IC, "{0}s", ((int)seconds).ToString(IC));

            if (seconds < 3600)
            {
                int m = (int)(seconds / 60);
                int s = (int)(seconds % 60);
                return string.Format(IC, "{0}m {1}s", m.ToString(IC), s.ToString(IC));
            }

            int h = (int)(seconds / 3600);
            int min = (int)((seconds % 3600) / 60);
            return string.Format(IC, "{0}h {1}m", h.ToString(IC), min.ToString(IC));
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
