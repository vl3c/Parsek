using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Shared time-range filter state read by TimelineWindowUI and RecordingsTableUI.
    /// Owned by ParsekUI. Not persisted across sessions.
    /// </summary>
    internal class TimeRangeFilterState
    {
        /// <summary>Inclusive lower bound in UT seconds. Null = unbounded.</summary>
        internal double? MinUT;

        /// <summary>Inclusive upper bound in UT seconds. Null = unbounded.</summary>
        internal double? MaxUT;

        /// <summary>True when any bound is set.</summary>
        internal bool IsActive => MinUT.HasValue || MaxUT.HasValue;

        /// <summary>Name of the active preset ("Last Day", "This Year", etc.), or null for custom/cleared.</summary>
        internal string ActivePresetName;

        internal void Clear()
        {
            MinUT = null;
            MaxUT = null;
            ActivePresetName = null;
        }

        internal void SetRange(double min, double max, string presetName = null)
        {
            MinUT = min;
            MaxUT = max;
            ActivePresetName = presetName;
        }
    }

    /// <summary>
    /// Pure static predicates for time-range filtering.
    /// All methods are testable without Unity.
    /// </summary>
    internal static class TimeRangeFilterLogic
    {
        /// <summary>
        /// Returns true if a single UT value falls within the filter range.
        /// Null bounds are treated as unbounded (always pass).
        /// </summary>
        internal static bool IsUTInRange(double ut, double? minUT, double? maxUT)
        {
            if (minUT.HasValue && ut < minUT.Value) return false;
            if (maxUT.HasValue && ut > maxUT.Value) return false;
            return true;
        }

        /// <summary>
        /// Returns true if a recording's [startUT, endUT] interval overlaps the filter range.
        /// A recording with endUT &lt;= startUT is treated as in-progress (unbounded end),
        /// so it overlaps any range whose min is before the current moment.
        /// </summary>
        internal static bool DoesRecordingOverlapRange(double startUT, double endUT,
            double? filterMinUT, double? filterMaxUT)
        {
            // No filter active — everything passes
            if (!filterMinUT.HasValue && !filterMaxUT.HasValue) return true;

            // In-progress recording (endUT not yet set or invalid): treat end as +∞
            bool inProgress = endUT <= startUT;

            // Standard overlap test: two intervals [a,b] and [c,d] overlap iff a <= d && b >= c
            // With null bounds treated as ±∞
            if (filterMaxUT.HasValue && startUT > filterMaxUT.Value) return false;
            if (!inProgress && filterMinUT.HasValue && endUT < filterMinUT.Value) return false;

            return true;
        }

        /// <summary>
        /// Returns true if any recording in the collection overlaps the filter range.
        /// Used for group/chain visibility — if any descendant overlaps, show the container.
        /// </summary>
        internal static bool DoesAnyRecordingOverlapRange(IReadOnlyList<Recording> recordings,
            IEnumerable<int> indices, double? filterMinUT, double? filterMaxUT)
        {
            // No filter active — everything passes
            if (!filterMinUT.HasValue && !filterMaxUT.HasValue) return true;

            foreach (int idx in indices)
            {
                var rec = recordings[idx];
                if (DoesRecordingOverlapRange(rec.StartUT, rec.EndUT, filterMinUT, filterMaxUT))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if any recording in the list overlaps the filter range.
        /// Overload for direct Recording enumeration (used by group descendant checks).
        /// </summary>
        internal static bool DoesAnyRecordingOverlapRange(IEnumerable<Recording> recordings,
            double? filterMinUT, double? filterMaxUT)
        {
            if (!filterMinUT.HasValue && !filterMaxUT.HasValue) return true;

            foreach (var rec in recordings)
            {
                if (DoesRecordingOverlapRange(rec.StartUT, rec.EndUT, filterMinUT, filterMaxUT))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Computes the slider bounds from the committed recording set.
        /// Returns (minBound, maxBound). If no recordings exist, returns (0, 0).
        /// </summary>
        internal static void ComputeSliderBounds(IReadOnlyList<Recording> committed,
            double currentUT, out double minBound, out double maxBound)
        {
            if (committed == null || committed.Count == 0)
            {
                minBound = 0;
                maxBound = 0;
                return;
            }

            minBound = double.MaxValue;
            maxBound = double.MinValue;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.StartUT < minBound) minBound = rec.StartUT;
                if (rec.EndUT > maxBound) maxBound = rec.EndUT;
            }

            if (currentUT > maxBound) maxBound = currentUT;
            if (minBound > maxBound) minBound = maxBound;
        }

        /// <summary>
        /// Formats a UT value for slider labels, omitting seconds to mask float quantization.
        /// Falls back to raw UT string on error.
        /// </summary>
        internal static string FormatSliderLabel(double ut)
        {
            try
            {
                // KSPUtil.PrintDateCompact includes seconds — trim to just Y/D/H:M
                string full = KSPUtil.PrintDateCompact(ut, true);
                // Format is typically "Y1, D5, 2:14:03" — strip the last :SS
                int lastColon = full.LastIndexOf(':');
                if (lastColon > 0)
                {
                    int prevColon = full.LastIndexOf(':', lastColon - 1);
                    if (prevColon >= 0)
                        return full.Substring(0, lastColon);
                }
                return full;
            }
            catch
            {
                return ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
