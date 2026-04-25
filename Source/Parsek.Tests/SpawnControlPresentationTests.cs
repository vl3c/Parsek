using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure Real Spawn Control sorting and row-state rules shared by the IMGUI window.
    /// </summary>
    public class SpawnControlPresentationTests
    {
        private const double Radius = 250.0;
        private const double MaxRelSpeed = 2.0;

        [Fact]
        public void RealSpawnControl_ListBounds_ExceedFFGate()
        {
            // The outer "show in list" bounds must always be at least as permissive as the
            // inner FF-enable gates; otherwise the two-tier logic collapses and ghosts that
            // fail the FF gate would not have a chance to appear in the list at all.
            Assert.True(Parsek.ParsekFlight.NearbySpawnListRadius >= Parsek.ParsekFlight.NearbySpawnRadius,
                "List radius must be >= FF radius");
            Assert.True(Parsek.ParsekFlight.MaxListRelativeSpeed >= Parsek.ParsekFlight.MaxRelativeSpeed,
                "List rel-speed cap must be >= FF rel-speed cap");
        }

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
        public void SortCandidates_ByRelativeSpeedAscending_OrdersBySpeed()
        {
            var sorted = SpawnControlPresentation.SortCandidates(
                new List<NearbySpawnCandidate>
                {
                    new NearbySpawnCandidate { vesselName = "fast", relativeSpeed = 10.0 },
                    new NearbySpawnCandidate { vesselName = "still", relativeSpeed = 0.1 },
                    new NearbySpawnCandidate { vesselName = "drift", relativeSpeed = 1.5 }
                },
                SpawnControlSortColumn.RelativeSpeed,
                ascending: true);

            Assert.Equal("still", sorted[0].vesselName);
            Assert.Equal("drift", sorted[1].vesselName);
            Assert.Equal("fast", sorted[2].vesselName);
        }

        [Fact]
        public void BuildRowPresentation_NonDepartingCandidate_ShowsSpawnAction()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    endUT = 500,
                    distance = 100,
                    relativeSpeed = 0.5
                },
                currentUT: 100,
                proximityRadius: Radius,
                maxRelativeSpeed: MaxRelSpeed);

            Assert.Equal(string.Empty, row.StateText);
            Assert.Equal(SpawnCandidateStateTone.None, row.StateTone);
            Assert.Equal("FF-Spawn", row.WarpButtonLabel);
            Assert.True(row.WarpButtonEnabled);
            Assert.True(row.ConditionsMet);
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
                    destination = "Mun",
                    distance = 100,
                    relativeSpeed = 0.5
                },
                currentUT: 100,
                proximityRadius: Radius,
                maxRelativeSpeed: MaxRelSpeed);

            Assert.Equal("Departs T-2m 0s", row.StateText);
            Assert.Equal(SpawnCandidateStateTone.UpcomingDeparture, row.StateTone);
            Assert.Equal("FF-Depart", row.WarpButtonLabel);
            Assert.True(row.WarpButtonEnabled);
            Assert.True(row.ConditionsMet);
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
                    destination = null,
                    distance = 100,
                    relativeSpeed = 0.5
                },
                currentUT: 100,
                proximityRadius: Radius,
                maxRelativeSpeed: MaxRelSpeed);

            Assert.Equal("Departing → ?", row.StateText);
            Assert.Equal(SpawnCandidateStateTone.DepartingNow, row.StateTone);
            Assert.Equal("FF-Depart", row.WarpButtonLabel);
            Assert.False(row.WarpButtonEnabled);
            // Physical preconditions still pass — only the time gate fails.
            Assert.True(row.ConditionsMet);
            Assert.True(row.UsesDepartureWarp);
        }

        [Fact]
        public void BuildRowPresentation_RelativeSpeedAboveGate_DisablesWarpAndClearsConditionsMet()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    endUT = 500,
                    distance = 100,
                    relativeSpeed = 5.0  // above MaxRelSpeed
                },
                currentUT: 100,
                proximityRadius: Radius,
                maxRelativeSpeed: MaxRelSpeed);

            Assert.False(row.ConditionsMet);
            Assert.False(row.WarpButtonEnabled);
            Assert.Equal("FF-Spawn", row.WarpButtonLabel);
        }

        [Fact]
        public void BuildRowPresentation_DistanceAboveRadius_DisablesWarpAndClearsConditionsMet()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    endUT = 500,
                    distance = Radius + 1,
                    relativeSpeed = 0.5
                },
                currentUT: 100,
                proximityRadius: Radius,
                maxRelativeSpeed: MaxRelSpeed);

            Assert.False(row.ConditionsMet);
            Assert.False(row.WarpButtonEnabled);
        }

        [Fact]
        public void BuildRowPresentation_RelativeSpeedNotYetSampled_DisablesWarp()
        {
            SpawnCandidateRowPresentation row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate
                {
                    endUT = 500,
                    distance = 100,
                    relativeSpeed = double.PositiveInfinity
                },
                currentUT: 100,
                proximityRadius: Radius,
                maxRelativeSpeed: MaxRelSpeed);

            Assert.False(row.ConditionsMet);
            Assert.False(row.WarpButtonEnabled);
        }

        [Fact]
        public void FormatRelativeSpeed_NotSampled_ReturnsDash()
        {
            Assert.Equal("—",
                SpawnControlPresentation.FormatRelativeSpeed(double.PositiveInfinity, CultureInfo.InvariantCulture));
            Assert.Equal("—",
                SpawnControlPresentation.FormatRelativeSpeed(double.NaN, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void FormatRelativeSpeed_BelowTen_PrintsOneDecimal()
        {
            Assert.Equal("0.5 m/s",
                SpawnControlPresentation.FormatRelativeSpeed(0.5, CultureInfo.InvariantCulture));
            Assert.Equal("9.9 m/s",
                SpawnControlPresentation.FormatRelativeSpeed(9.9, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void FormatRelativeSpeed_TenOrAbove_PrintsInteger()
        {
            Assert.Equal("12 m/s",
                SpawnControlPresentation.FormatRelativeSpeed(12.4, CultureInfo.InvariantCulture));
            Assert.Equal("100 m/s",
                SpawnControlPresentation.FormatRelativeSpeed(100.0, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void FormatRelativeSpeed_UsesInvariantCulture_NotCommaLocale()
        {
            CultureInfo deDE = new CultureInfo("de-DE");
            // de-DE uses comma as decimal separator; we want a dot, so the explicit IC arg drives.
            Assert.Equal("0.5 m/s",
                SpawnControlPresentation.FormatRelativeSpeed(0.5, CultureInfo.InvariantCulture));
            // When the caller hands de-DE, we honor it (callers always pass IC, but verify pass-through).
            Assert.Equal("0,5 m/s",
                SpawnControlPresentation.FormatRelativeSpeed(0.5, deDE));
        }
    }
}
