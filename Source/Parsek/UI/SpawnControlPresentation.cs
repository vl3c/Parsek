using System;
using System.Collections.Generic;

namespace Parsek
{
    internal enum SpawnControlSortColumn
    {
        Name,
        Distance,
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

        internal static SpawnCandidateRowPresentation BuildRowPresentation(
            NearbySpawnCandidate candidate,
            double currentUT)
        {
            if (!candidate.willDepart)
            {
                return new SpawnCandidateRowPresentation
                {
                    StateText = string.Empty,
                    StateTone = SpawnCandidateStateTone.None,
                    WarpButtonLabel = "FF-Spawn",
                    WarpButtonEnabled = candidate.endUT > currentUT,
                    UsesDepartureWarp = false
                };
            }

            double departureDelta = candidate.departureUT - currentUT;
            string destination = string.IsNullOrEmpty(candidate.destination)
                ? "?"
                : candidate.destination;

            return new SpawnCandidateRowPresentation
            {
                StateText = departureDelta <= 0
                    ? $"Departing \u2192 {destination}"
                    : $"Departs {SelectiveSpawnUI.FormatCountdown(departureDelta)}",
                StateTone = departureDelta <= 0
                    ? SpawnCandidateStateTone.DepartingNow
                    : SpawnCandidateStateTone.UpcomingDeparture,
                WarpButtonLabel = "FF-Depart",
                WarpButtonEnabled = candidate.departureUT > currentUT,
                UsesDepartureWarp = true
            };
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

                default:
                    comparison = a.distance.CompareTo(b.distance);
                    break;
            }

            return ascending ? comparison : -comparison;
        }
    }
}
