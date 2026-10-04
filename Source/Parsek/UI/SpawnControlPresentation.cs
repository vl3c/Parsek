using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The candidate table's sort keys, one per sortable header: Craft, Dist, Speed, Spawns
    /// and Status. The Spawn date column shows the same moment as Spawns, so it is not a
    /// second sortable header.
    /// </summary>
    internal enum SpawnControlSortColumn
    {
        Name,
        Distance,
        RelativeSpeed,
        SpawnTime,
        Status
    }

    /// <summary>
    /// A row's one-word Status. Declaration order is the Status sort order: the rows a
    /// player can warp to first, then the ones that may still become warpable, then the
    /// ones that cannot.
    /// </summary>
    internal enum SpawnCandidateStatus
    {
        /// <summary>Inside both spawn gates, spawns here later. "Ready", green.</summary>
        Ready,
        /// <summary>Inside both gates, but leaves its orbit before it would spawn.
        /// "Leaves", the countdown amber.</summary>
        Leaves,
        /// <summary>Close enough but passing faster than the spawn gate. "Too fast".</summary>
        TooFast,
        /// <summary>Its departure is due now. "Leaving", orange.</summary>
        Leaving,
        /// <summary>Its spawn time is behind the clock. "Passed".</summary>
        Passed,
        /// <summary>Farther than the spawn radius. The list never holds such a row
        /// (<see cref="SelectiveSpawnUI.IsListedCandidate"/>); the builder still answers
        /// for one so it is total.</summary>
        TooFar
    }

    internal struct SpawnCandidateRowPresentation
    {
        internal SpawnCandidateStatus Status;

        /// <summary>The Status cell's one word.</summary>
        internal string StatusText;

        /// <summary>The Status cell's hover: why the row reads that word. A bare clause with
        /// no trailing period, the house why-disabled voice, because a greyed row's Warp
        /// button carries the same text.</summary>
        internal string StatusHover;

        /// <summary>The moment the row's Warp acts on (<see cref="SelectiveSpawnUI.EffectiveWarpUT"/>);
        /// the Spawns countdown and the Spawn date cells both show it.</summary>
        internal double EffectiveUT;

        /// <summary>The Spawn date cell: <see cref="EffectiveUT"/> as a calendar date.</summary>
        internal string DateText;

        internal string WarpButtonLabel;
        internal bool WarpButtonEnabled;

        /// <summary>What the live Warp button does, as its hover. Empty while the button
        /// is greyed (then <see cref="WarpButtonDisabledReason"/> speaks instead).</summary>
        internal string WarpButtonHover;

        /// <summary>
        /// Why the Warp button is greyed (the row's Status hover); empty when the button is
        /// live. Carried to the hover strip by <c>DisabledHoverEcho</c>.
        /// </summary>
        internal string WarpButtonDisabledReason;
        internal bool UsesDepartureWarp;

        /// <summary>True when distance and relative speed are both inside the spawn gates.
        /// Distinct from <see cref="WarpButtonEnabled"/>, which also needs the warp moment
        /// to be ahead of the clock.</summary>
        internal bool ConditionsMet;
    }

    /// <summary>
    /// Pure presentation rules for the Real Spawn Control window.
    /// Keeps sorting and per-row status decisions out of IMGUI draw code.
    /// </summary>
    internal static class SpawnControlPresentation
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The row button's label: one word, for both warp targets. What it
        /// jumps to is its hover.</summary>
        internal const string WarpButtonText = "Warp";

        internal const string StatusReadyText = "Ready";
        internal const string StatusLeavesText = "Leaves";
        internal const string StatusLeavingText = "Leaving";
        internal const string StatusPassedText = "Passed";
        internal const string StatusTooFastText = "Too fast";
        internal const string StatusTooFarText = "Too far";

        /// <summary>The Speed cell before a second position sample exists.</summary>
        internal const string NoSpeedText = "-";

        /// <summary>
        /// The window's first-open height for <paramref name="rowCount"/> rows: the chrome
        /// (title bar, header row, hover strip, button row) plus one row height per
        /// candidate, floored at the window's minimum and capped so a long list opens
        /// scrolling rather than off screen. Measured off the GUI tree dump of a one-row
        /// window (header row 31 px, one row 29 px plus its 4 px gap).
        /// </summary>
        internal static float FirstOpenHeight(int rowCount)
        {
            const float ChromePx = 158f;
            const float RowPx = 33f;
            const float MaxPx = 400f;
            int rows = Math.Max(1, rowCount);
            float h = ChromePx + RowPx * rows;
            if (h < SpawnControlUI.MinWindowHeight) h = SpawnControlUI.MinWindowHeight;
            if (h > MaxPx) h = MaxPx;
            return h;
        }

        internal static List<NearbySpawnCandidate> SortCandidates(
            IReadOnlyList<NearbySpawnCandidate> candidates,
            SpawnControlSortColumn sortColumn,
            bool ascending,
            double currentUT,
            double proximityRadius,
            double maxRelativeSpeed)
        {
            var sorted = new List<NearbySpawnCandidate>();
            if (candidates == null)
                return sorted;

            for (int i = 0; i < candidates.Count; i++)
                sorted.Add(candidates[i]);

            sorted.Sort((a, b) => CompareCandidates(
                a, b, sortColumn, ascending, currentUT, proximityRadius, maxRelativeSpeed));
            return sorted;
        }

        /// <summary>
        /// Pure: the Status of one candidate. The two proximity gates come before the
        /// clock, because they are the ones the player can still act on; a departure comes
        /// before a spawn, because a craft that leaves first never spawns here.
        /// </summary>
        internal static SpawnCandidateStatus ClassifyStatus(
            NearbySpawnCandidate candidate,
            double currentUT,
            double proximityRadius,
            double maxRelativeSpeed)
        {
            if (candidate.distance > proximityRadius)
                return SpawnCandidateStatus.TooFar;
            if (!(candidate.relativeSpeed <= maxRelativeSpeed))
                return SpawnCandidateStatus.TooFast;
            if (candidate.willDepart)
                return candidate.departureUT > currentUT
                    ? SpawnCandidateStatus.Leaves
                    : SpawnCandidateStatus.Leaving;
            return candidate.endUT > currentUT
                ? SpawnCandidateStatus.Ready
                : SpawnCandidateStatus.Passed;
        }

        internal static string StatusText(SpawnCandidateStatus status)
        {
            switch (status)
            {
                case SpawnCandidateStatus.Ready: return StatusReadyText;
                case SpawnCandidateStatus.Leaves: return StatusLeavesText;
                case SpawnCandidateStatus.Leaving: return StatusLeavingText;
                case SpawnCandidateStatus.Passed: return StatusPassedText;
                case SpawnCandidateStatus.TooFast: return StatusTooFastText;
                default: return StatusTooFarText;
            }
        }

        /// <summary>
        /// Pure: build per-row presentation. <paramref name="formatDate"/> turns a UT into
        /// the house calendar date (the window passes <c>KSPUtil.PrintDateCompact</c>); a
        /// null formatter prints the raw UT, for headless callers.
        /// </summary>
        internal static SpawnCandidateRowPresentation BuildRowPresentation(
            NearbySpawnCandidate candidate,
            double currentUT,
            double proximityRadius,
            double maxRelativeSpeed,
            Func<double, string> formatDate)
        {
            SpawnCandidateStatus status = ClassifyStatus(
                candidate, currentUT, proximityRadius, maxRelativeSpeed);
            double effectiveUT = SelectiveSpawnUI.EffectiveWarpUT(candidate);
            string date = FormatDate(effectiveUT, formatDate);
            string name = candidate.vesselName ?? string.Empty;
            string destination = SelectiveSpawnUI.FormatDepartureDestination(
                candidate.departureKind, candidate.destination);
            // " for Mun", or nothing when the departure has no known destination.
            string destinationClause = string.IsNullOrEmpty(destination)
                ? string.Empty
                : " " + destination;

            string statusHover;
            string warpHover = string.Empty;
            bool enabled;
            switch (status)
            {
                case SpawnCandidateStatus.Ready:
                    statusHover = string.Format(IC,
                        "Close and slow enough to spawn; it spawns here on {0}", date);
                    warpHover = string.Format(IC,
                        "Warps to {0}, when {1} spawns here.", date, name);
                    enabled = true;
                    break;
                case SpawnCandidateStatus.Leaves:
                    statusHover = string.Format(IC,
                        "Leaves this orbit on {0}{1}; it does not spawn here",
                        date, destinationClause);
                    warpHover = FormatLeavesWarpHover(name, date);
                    enabled = true;
                    break;
                case SpawnCandidateStatus.Leaving:
                    statusHover = string.Format(IC,
                        "Leaving this orbit now{0}; it does not spawn here", destinationClause);
                    enabled = false;
                    break;
                case SpawnCandidateStatus.Passed:
                    statusHover = string.Format(IC,
                        "Its spawn time, {0}, has passed", date);
                    enabled = false;
                    break;
                case SpawnCandidateStatus.TooFast:
                    statusHover = TooFastReason(candidate.relativeSpeed, maxRelativeSpeed);
                    enabled = false;
                    break;
                default:
                    statusHover = TooFarReason(candidate.distance, proximityRadius);
                    enabled = false;
                    break;
            }

            return new SpawnCandidateRowPresentation
            {
                Status = status,
                StatusText = StatusText(status),
                StatusHover = statusHover,
                EffectiveUT = effectiveUT,
                DateText = date,
                WarpButtonLabel = WarpButtonText,
                WarpButtonEnabled = enabled,
                WarpButtonHover = warpHover,
                WarpButtonDisabledReason = enabled ? string.Empty : statusHover,
                UsesDepartureWarp = candidate.willDepart,
                ConditionsMet = candidate.distance <= proximityRadius
                    && candidate.relativeSpeed <= maxRelativeSpeed
            };
        }

        /// <summary>
        /// Pure: the Warp hover of a craft that leaves its orbit before it would spawn. The
        /// button jumps to just before the departure, and the craft does not spawn here.
        /// </summary>
        internal static string FormatLeavesWarpHover(string vesselName, string date)
        {
            return string.Format(IC,
                "Warps to just before {0} leaves orbit on {1}; it does not spawn here.",
                vesselName, date);
        }

        /// <summary>Pure: the Too fast reason, naming the gate and the measured speed.</summary>
        internal static string TooFastReason(double relativeSpeed, double maxRelativeSpeed)
        {
            if (double.IsInfinity(relativeSpeed) || double.IsNaN(relativeSpeed))
                return string.Format(IC,
                    "Spawns only below {0:0.#} m/s relative speed; its speed is not measured yet",
                    maxRelativeSpeed);
            return string.Format(IC,
                "Spawns only below {0:0.#} m/s relative speed; it is passing at {1}",
                maxRelativeSpeed, FormatRelativeSpeed(relativeSpeed, IC));
        }

        /// <summary>Pure: the Too far reason, naming the radius and the distance.</summary>
        internal static string TooFarReason(double distance, double proximityRadius)
        {
            return string.Format(IC,
                "Spawns only within {0:F0} m; it is {1:F0} m away",
                proximityRadius, distance);
        }

        /// <summary>
        /// Pure: format distance for display, a space before the unit like the Speed cell
        /// ("129 m").
        /// </summary>
        internal static string FormatDistance(double distance, IFormatProvider culture)
        {
            return string.Format(culture, "{0:F0} m", distance);
        }

        /// <summary>
        /// Pure: format relative speed for display. Returns <see cref="NoSpeedText"/> when
        /// the speed has not yet been sampled (PositiveInfinity sentinel set by the
        /// proximity scan on first sighting). Below 10 m/s prints one decimal so the
        /// display matches the m/s gate granularity.
        /// </summary>
        internal static string FormatRelativeSpeed(double relativeSpeed, IFormatProvider culture)
        {
            if (double.IsInfinity(relativeSpeed) || double.IsNaN(relativeSpeed))
                return NoSpeedText;
            string fmt = relativeSpeed < 10.0 ? "{0:F1} m/s" : "{0:F0} m/s";
            return string.Format(culture, fmt, relativeSpeed);
        }

        private static string FormatDate(double ut, Func<double, string> formatDate)
        {
            if (formatDate != null)
            {
                string text = formatDate(ut);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            return string.Format(IC, "UT {0:F0}", ut);
        }

        private static int CompareCandidates(
            NearbySpawnCandidate a,
            NearbySpawnCandidate b,
            SpawnControlSortColumn sortColumn,
            bool ascending,
            double currentUT,
            double proximityRadius,
            double maxRelativeSpeed)
        {
            int comparison;
            switch (sortColumn)
            {
                case SpawnControlSortColumn.Name:
                    comparison = string.Compare(
                        a.vesselName,
                        b.vesselName,
                        StringComparison.OrdinalIgnoreCase);
                    break;

                case SpawnControlSortColumn.SpawnTime:
                    comparison = SelectiveSpawnUI.EffectiveWarpUT(a)
                        .CompareTo(SelectiveSpawnUI.EffectiveWarpUT(b));
                    break;

                case SpawnControlSortColumn.RelativeSpeed:
                    comparison = a.relativeSpeed.CompareTo(b.relativeSpeed);
                    break;

                case SpawnControlSortColumn.Status:
                    comparison = ((int)ClassifyStatus(a, currentUT, proximityRadius, maxRelativeSpeed))
                        .CompareTo((int)ClassifyStatus(b, currentUT, proximityRadius, maxRelativeSpeed));
                    // Within one Status, soonest first, so the top row is the next to act.
                    if (comparison == 0)
                        comparison = SelectiveSpawnUI.EffectiveWarpUT(a)
                            .CompareTo(SelectiveSpawnUI.EffectiveWarpUT(b));
                    break;

                default:
                    comparison = a.distance.CompareTo(b.distance);
                    break;
            }

            return ascending ? comparison : -comparison;
        }
    }
}
