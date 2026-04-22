using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure Real Spawn Control sorting and row-state rules shared by the IMGUI window.
    /// </summary>
    public class SpawnControlPresentationTests
    {
        [Fact]
        public void SortCandidates_ByNameAscending_UsesCaseInsensitiveOrder()
        {
            var sorted = SpawnControlPresentation.SortCandidates(
                new List<NearbySpawnCandidate>
                {
                    new NearbySpawnCandidate { vesselName = "charlie" },
                    new NearbySpawnCandidate { vesselName = "Alpha" },
                    new NearbySpawnCandidate { vesselName = "bravo" }
                },
                SpawnControlSortColumn.Name,
                ascending: true);

            Assert.Equal("Alpha", sorted[0].vesselName);
            Assert.Equal("bravo", sorted[1].vesselName);
            Assert.Equal("charlie", sorted[2].vesselName);
        }

        [Fact]
        public void SortCandidates_BySpawnTimeDescending_UsesRequestedDirection()
        {
            var sorted = SpawnControlPresentation.SortCandidates(
                new List<NearbySpawnCandidate>
                {
                    new NearbySpawnCandidate { vesselName = "A", endUT = 1000 },
                    new NearbySpawnCandidate { vesselName = "B", endUT = 3000 },
                    new NearbySpawnCandidate { vesselName = "C", endUT = 2000 }
                },
                SpawnControlSortColumn.SpawnTime,
                ascending: false);

            Assert.Equal("B", sorted[0].vesselName);
            Assert.Equal("C", sorted[1].vesselName);
            Assert.Equal("A", sorted[2].vesselName);
        }

        [Fact]
        public void BuildRowPresentation_NonDepartingCandidate_ShowsSpawnAction()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    endUT = 500
                },
                currentUT: 100);

            Assert.Equal(string.Empty, row.StateText);
            Assert.Equal(SpawnCandidateStateTone.None, row.StateTone);
            Assert.Equal("FF-Spawn", row.WarpButtonLabel);
            Assert.True(row.WarpButtonEnabled);
            Assert.False(row.UsesDepartureWarp);
        }

        [Fact]
        public void BuildRowPresentation_DepartureInFuture_ShowsCountdownAndEnabledDepartureAction()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    willDepart = true,
                    departureUT = 220,
                    destination = "Mun"
                },
                currentUT: 100);

            Assert.Equal("Departs T-2m 0s", row.StateText);
            Assert.Equal(SpawnCandidateStateTone.UpcomingDeparture, row.StateTone);
            Assert.Equal("FF-Depart", row.WarpButtonLabel);
            Assert.True(row.WarpButtonEnabled);
            Assert.True(row.UsesDepartureWarp);
        }

        [Fact]
        public void BuildRowPresentation_DepartureAlreadyDue_DisablesWarpAndUsesDepartingState()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    willDepart = true,
                    departureUT = 100,
                    destination = null
                },
                currentUT: 100);

            Assert.Equal("Departing \u2192 ?", row.StateText);
            Assert.Equal(SpawnCandidateStateTone.DepartingNow, row.StateTone);
            Assert.Equal("FF-Depart", row.WarpButtonLabel);
            Assert.False(row.WarpButtonEnabled);
            Assert.True(row.UsesDepartureWarp);
        }
    }
}
