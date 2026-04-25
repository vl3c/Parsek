using System;
using System.Collections.Generic;

namespace Parsek
{
    internal enum SpawnControlSortColumn
    {
        Name,
        Distance,
        RelativeSpeed,
        SpawnTime
    }

    internal enum SpawnCandidateStateTone
    {
        None,
        UpcomingDeparture,
        DepartingNow
    }

    internal struct SpawnCandidateRowPresentation
    {
        internal string StateText;
        internal SpawnCandidateStateTone StateTone;
        internal string WarpButtonLabel;
        internal bool WarpButtonEnabled;
        internal bool UsesDepartureWarp;
        // True when distance and relative-speed are both within the FF gates. Drives the
        // green text on the distance and relative-speed columns. Distinct from
        // WarpButtonEnabled, which additionally requires a future endUT/departureUT.
        internal bool ConditionsMet;
    }

    /// <summary>
    /// Pure presentation rules for the Real Spawn Control window.
    /// Keeps sorting and per-row status decisions out of IMGUI draw code.
    /// </summary>
    internal static class SpawnControlPresentation
    {
        internal static List<NearbySpawnCandidate> SortCandidates(
            IReadOnlyList<NearbySpawnCandidate> candidates,
            SpawnControlSortColumn sortColumn,
            bool ascending)
        {
            var sorted = new List<NearbySpawnCandidate>();
            if (candidates == null)
                return sorted;

            for (int i = 0; i < candidates.Count; i++)
                sorted.Add(candidates[i]);

            sorted.Sort((a, b) => CompareCandidates(a, b, sortColumn, ascending));
            return sorted;
        }

        /// <summary>
        /// Pure: build per-row presentation. The proximity-radius and relative-speed gates
        /// are enforced here (rather than during admission) so the window can show too-fast
        /// craft with a disabled FF button and red speed/distance text.
        /// </summary>
        internal static SpawnCandidateRowPresentation BuildRowPresentation(
            NearbySpawnCandidate candidate,
            double currentUT,
            double proximityRadius,
            double maxRelativeSpeed)
        {
            bool conditionsMet =
                candidate.distance <= proximityRadius &&
                candidate.relativeSpeed <= maxRelativeSpeed;

            if (!candidate.willDepart)
            {
                return new SpawnCandidateRowPresentation
                {
                    StateText = string.Empty,
                    StateTone = SpawnCandidateStateTone.None,
                    WarpButtonLabel = "FF-Spawn",
                    WarpButtonEnabled = conditionsMet && candidate.endUT > currentUT,
                    UsesDepartureWarp = false,
                    ConditionsMet = conditionsMet
                };
            }

            double departureDelta = candidate.departureUT - currentUT;
            string destination = string.IsNullOrEmpty(candidate.destination)
                ? "?"
                : candidate.destination;

            return new SpawnCandidateRowPresentation
            {
                StateText = departureDelta <= 0
                    ? $"Departing → {destination}"
                    : $"Departs {SelectiveSpawnUI.FormatCountdown(departureDelta)}",
                StateTone = departureDelta <= 0
                    ? SpawnCandidateStateTone.DepartingNow
                    : SpawnCandidateStateTone.UpcomingDeparture,
                WarpButtonLabel = "FF-Depart",
                WarpButtonEnabled = conditionsMet && candidate.departureUT > currentUT,
                UsesDepartureWarp = true,
                ConditionsMet = conditionsMet
            };
        }

        /// <summary>
        /// Pure: format relative speed for display. Returns "—" when the speed has not yet
        /// been sampled (PositiveInfinity sentinel set by the proximity scan on first sighting).
        /// Below 10 m/s prints one decimal so the display matches the m/s gate granularity.
        /// </summary>
        internal static string FormatRelativeSpeed(double relativeSpeed, System.IFormatProvider culture)
        {
            if (double.IsInfinity(relativeSpeed) || double.IsNaN(relativeSpeed))
                return "—";
            string fmt = relativeSpeed < 10.0 ? "{0:F1} m/s" : "{0:F0} m/s";
            return string.Format(culture, fmt, relativeSpeed);
        }

        private static int CompareCandidates(
            NearbySpawnCandidate a,
            NearbySpawnCandidate b,
            SpawnControlSortColumn sortColumn,
            bool ascending)
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
                    comparison = a.endUT.CompareTo(b.endUT);
                    break;

                case SpawnControlSortColumn.RelativeSpeed:
                    comparison = a.relativeSpeed.CompareTo(b.relativeSpeed);
                    break;

                default:
                    comparison = a.distance.CompareTo(b.distance);
                    break;
            }

            return ascending ? comparison : -comparison;
        }
    }
}
