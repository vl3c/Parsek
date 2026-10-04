using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure Real Spawn Control sorting and row rules shared by the IMGUI window:
    /// the one-word Status with its hover, the Warp button's hover, the time cells' moment
    /// and the first-open height.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnControlPresentationTests : System.IDisposable
    {
        private const double Radius = 250.0;
        private const double MaxRelSpeed = 2.0;

        // A stand-in for KSPUtil.PrintDateCompact: the presentation only passes the UT through.
        private static readonly System.Func<double, string> Date = ut =>
            "D" + ut.ToString("F0", CultureInfo.InvariantCulture);

        public SpawnControlPresentationTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
        }

        public void Dispose()
        {
            ParsekTimeFormat.ResetForTesting();
        }

        private static SpawnCandidateRowPresentation Row(NearbySpawnCandidate c, double now = 100)
            => SpawnControlPresentation.BuildRowPresentation(c, now, Radius, MaxRelSpeed, Date);

        [Fact]
        public void RealSpawnControl_SpeedTrackingReachesPastTheSpawnRadius()
        {
            // The scan samples speed out past the spawn radius so a ghost that closes inside
            // it is listed with its speed already measured; the list keeps too-fast ghosts up
            // to a looser speed bound than the warp gate.
            Assert.True(ParsekFlight.NearbySpawnTrackRadius >= ParsekFlight.NearbySpawnRadius);
            Assert.True(ParsekFlight.MaxListRelativeSpeed >= ParsekFlight.MaxRelativeSpeed);
        }

        // ---------------- sorting ----------------

        private static List<NearbySpawnCandidate> Sort(
            List<NearbySpawnCandidate> list, SpawnControlSortColumn col, bool asc, double now = 100)
            => SpawnControlPresentation.SortCandidates(list, col, asc, now, Radius, MaxRelSpeed);

        [Fact]
        public void SortCandidates_ByNameAscending_UsesCaseInsensitiveOrder()
        {
            var sorted = Sort(new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { vesselName = "charlie" },
                new NearbySpawnCandidate { vesselName = "Alpha" },
                new NearbySpawnCandidate { vesselName = "bravo" }
            }, SpawnControlSortColumn.Name, true);

            Assert.Equal("Alpha", sorted[0].vesselName);
            Assert.Equal("bravo", sorted[1].vesselName);
            Assert.Equal("charlie", sorted[2].vesselName);
        }

        [Fact]
        public void SortCandidates_BySpawnTimeDescending_UsesRequestedDirection()
        {
            var sorted = Sort(new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { vesselName = "A", endUT = 1000 },
                new NearbySpawnCandidate { vesselName = "B", endUT = 3000 },
                new NearbySpawnCandidate { vesselName = "C", endUT = 2000 }
            }, SpawnControlSortColumn.SpawnTime, false);

            Assert.Equal("B", sorted[0].vesselName);
            Assert.Equal("C", sorted[1].vesselName);
            Assert.Equal("A", sorted[2].vesselName);
        }

        [Fact]
        public void SortCandidates_BySpawnTime_LeavingRowSortsByItsDeparture_TheMomentWarpActsOn()
        {
            // Bug: a leaving row's Spawns sort used its far-away spawn (endUT) while its Warp
            // acts on the departure. "Lander" spawns last (endUT 5000) but leaves first (300),
            // so it sorts first, as Warp to Next Spawn would pick it.
            var sorted = Sort(new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { vesselName = "Station", endUT = 1000 },
                new NearbySpawnCandidate
                {
                    vesselName = "Lander", endUT = 5000, willDepart = true, departureUT = 300
                },
                new NearbySpawnCandidate { vesselName = "Probe", endUT = 600 }
            }, SpawnControlSortColumn.SpawnTime, true);

            Assert.Equal(new[] { "Lander", "Probe", "Station" },
                sorted.ConvertAll(c => c.vesselName).ToArray());
            var next = SelectiveSpawnUI.FindNextSpawnCandidate(sorted, 100, Radius, MaxRelSpeed);
            Assert.Equal("Lander", next.Value.vesselName);
        }

        [Fact]
        public void SortCandidates_ByRelativeSpeedAscending_OrdersBySpeed()
        {
            var sorted = Sort(new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { vesselName = "fast", relativeSpeed = 10.0 },
                new NearbySpawnCandidate { vesselName = "still", relativeSpeed = 0.1 },
                new NearbySpawnCandidate { vesselName = "drift", relativeSpeed = 1.5 }
            }, SpawnControlSortColumn.RelativeSpeed, true);

            Assert.Equal("still", sorted[0].vesselName);
            Assert.Equal("drift", sorted[1].vesselName);
            Assert.Equal("fast", sorted[2].vesselName);
        }

        [Fact]
        public void SortCandidates_ByStatusAscending_PutsWarpableRowsFirst_SoonestFirstWithinAStatus()
        {
            var sorted = Sort(new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { vesselName = "passed", endUT = 50, distance = 10, relativeSpeed = 0.1 },
                new NearbySpawnCandidate { vesselName = "fast", endUT = 500, distance = 10, relativeSpeed = 9 },
                new NearbySpawnCandidate { vesselName = "ready-late", endUT = 900, distance = 10, relativeSpeed = 0.1 },
                new NearbySpawnCandidate
                {
                    vesselName = "leaving", endUT = 900, willDepart = true, departureUT = 90,
                    distance = 10, relativeSpeed = 0.1
                },
                new NearbySpawnCandidate { vesselName = "ready-soon", endUT = 200, distance = 10, relativeSpeed = 0.1 },
                new NearbySpawnCandidate
                {
                    vesselName = "leaves", endUT = 900, willDepart = true, departureUT = 400,
                    distance = 10, relativeSpeed = 0.1
                },
            }, SpawnControlSortColumn.Status, true);

            Assert.Equal(
                new[] { "ready-soon", "ready-late", "leaves", "fast", "leaving", "passed" },
                sorted.ConvertAll(c => c.vesselName).ToArray());
        }

        // ---------------- Status words, hovers, Warp ----------------

        [Fact]
        public void Ready_IsTheWord_LiveWarp_HoverNamesTheSpawnDate()
        {
            var row = Row(new NearbySpawnCandidate
            {
                vesselName = "Station", endUT = 500, distance = 100, relativeSpeed = 0.5
            });

            Assert.Equal(SpawnCandidateStatus.Ready, row.Status);
            Assert.Equal("Ready", row.StatusText);
            Assert.Equal("Close and slow enough to spawn; it spawns here on D500", row.StatusHover);
            Assert.Equal("Warp", row.WarpButtonLabel);
            Assert.True(row.WarpButtonEnabled);
            Assert.Equal("Warps to D500, when Station spawns here.", row.WarpButtonHover);
            Assert.Equal(string.Empty, row.WarpButtonDisabledReason);
            Assert.True(row.ConditionsMet);
            Assert.False(row.UsesDepartureWarp);
            Assert.Equal(500, row.EffectiveUT);
            Assert.Equal("D500", row.DateText);
        }

        [Fact]
        public void Leaves_S1_WarpGoesToJustBeforeItLeaves_AndTheHoverSaysItDoesNotSpawnHere()
        {
            var row = Row(new NearbySpawnCandidate
            {
                vesselName = "Mun Lander", endUT = 9000, willDepart = true, departureUT = 220,
                departureKind = DepartureKind.OtherBody, destination = "Mun",
                distance = 100, relativeSpeed = 0.5
            });

            Assert.Equal(SpawnCandidateStatus.Leaves, row.Status);
            Assert.Equal("Leaves", row.StatusText);
            Assert.True(row.WarpButtonEnabled);
            Assert.True(row.UsesDepartureWarp);
            Assert.Equal(
                "Warps to just before Mun Lander leaves orbit on D220; it does not spawn here.",
                row.WarpButtonHover);
            Assert.Equal("Leaves this orbit on D220 for Mun; it does not spawn here",
                row.StatusHover);
            // The two time cells show the departure, not the far-away spawn.
            Assert.Equal(220, row.EffectiveUT);
            Assert.Equal("D220", row.DateText);
        }

        [Fact]
        public void Leaving_DepartureDueNow_IsGreyed_WithTheDestinationInWords()
        {
            var row = Row(new NearbySpawnCandidate
            {
                vesselName = "Tug", endUT = 9000, willDepart = true, departureUT = 100,
                departureKind = DepartureKind.NewOrbit, destination = "Kerbin",
                distance = 100, relativeSpeed = 0.5
            });

            Assert.Equal(SpawnCandidateStatus.Leaving, row.Status);
            Assert.Equal("Leaving", row.StatusText);
            Assert.False(row.WarpButtonEnabled);
            Assert.True(row.ConditionsMet);
            Assert.Equal("Leaving this orbit now for a new orbit; it does not spawn here",
                row.WarpButtonDisabledReason);
            Assert.DoesNotContain("maneuver", row.StatusHover);
        }

        [Fact]
        public void Leaving_WithNoKnownDestination_DropsTheClauseCleanly()
        {
            var row = Row(new NearbySpawnCandidate
            {
                vesselName = "Tug", willDepart = true, departureUT = 100,
                distance = 100, relativeSpeed = 0.5
            });

            Assert.Equal("Leaving this orbit now; it does not spawn here", row.StatusHover);
        }

        [Fact]
        public void Passed_IsGreyed_AndSaysTheSpawnTimeHasPassed()
        {
            var row = Row(new NearbySpawnCandidate
            {
                vesselName = "Probe", endUT = 90, distance = 100, relativeSpeed = 0.5
            });

            Assert.Equal(SpawnCandidateStatus.Passed, row.Status);
            Assert.Equal("Passed", row.StatusText);
            Assert.False(row.WarpButtonEnabled);
            Assert.Equal("Its spawn time, D90, has passed", row.WarpButtonDisabledReason);
            Assert.Equal(string.Empty, row.WarpButtonHover);
        }

        [Fact]
        public void TooFast_IsKeptGreyed_WithTheGateAndTheMeasuredSpeed()
        {
            var row = Row(new NearbySpawnCandidate
            {
                vesselName = "Rover", endUT = 500, distance = 100, relativeSpeed = 8.1
            });

            Assert.Equal(SpawnCandidateStatus.TooFast, row.Status);
            Assert.Equal("Too fast", row.StatusText);
            Assert.False(row.WarpButtonEnabled);
            Assert.False(row.ConditionsMet);
            Assert.Equal("Spawns only below 2 m/s relative speed; it is passing at 8.1 m/s",
                row.WarpButtonDisabledReason);
            Assert.Equal(row.StatusHover, row.WarpButtonDisabledReason);
        }

        [Fact]
        public void TooFast_OutranksADeparture_BecauseSpeedIsWhatThePlayerCanFix()
        {
            var row = Row(new NearbySpawnCandidate
            {
                endUT = 500, willDepart = true, departureUT = 300,
                distance = 100, relativeSpeed = 5.0
            });
            Assert.Equal(SpawnCandidateStatus.TooFast, row.Status);
            Assert.False(row.WarpButtonEnabled);
        }

        [Fact]
        public void TooFast_SpeedNotYetSampled_SaysSo()
        {
            var row = Row(new NearbySpawnCandidate
            {
                endUT = 500, distance = 100, relativeSpeed = double.PositiveInfinity
            });

            Assert.Equal(SpawnCandidateStatus.TooFast, row.Status);
            Assert.False(row.ConditionsMet);
            Assert.False(row.WarpButtonEnabled);
            Assert.Equal("Spawns only below 2 m/s relative speed; its speed is not measured yet",
                row.StatusHover);
        }

        [Fact]
        public void TooFar_IsTheBuildersAnswerForAnUnlistedRow()
        {
            // The list never holds such a row (SelectiveSpawnUI.IsListedCandidate); the
            // builder stays total so a stale scan cannot crash or light a button.
            var row = Row(new NearbySpawnCandidate
            {
                endUT = 500, distance = 470, relativeSpeed = 0.5
            });

            Assert.Equal(SpawnCandidateStatus.TooFar, row.Status);
            Assert.False(row.WarpButtonEnabled);
            Assert.Equal("Spawns only within 250 m; it is 470 m away", row.WarpButtonDisabledReason);
        }

        [Fact]
        public void EveryStatusWord_IsTheModelsAsciiWord()
        {
            var expected = new Dictionary<SpawnCandidateStatus, string>
            {
                { SpawnCandidateStatus.Ready, "Ready" },
                { SpawnCandidateStatus.Leaves, "Leaves" },
                { SpawnCandidateStatus.Leaving, "Leaving" },
                { SpawnCandidateStatus.Passed, "Passed" },
                { SpawnCandidateStatus.TooFast, "Too fast" },
                { SpawnCandidateStatus.TooFar, "Too far" },
            };
            foreach (SpawnCandidateStatus st in System.Enum.GetValues(typeof(SpawnCandidateStatus)))
                Assert.Equal(expected[st], SpawnControlPresentation.StatusText(st));
        }

        [Fact]
        public void NullDateFormatter_PrintsTheRawUt()
        {
            var row = SpawnControlPresentation.BuildRowPresentation(
                new NearbySpawnCandidate { endUT = 500, distance = 1, relativeSpeed = 0 },
                100, Radius, MaxRelSpeed, null);
            Assert.Equal("UT 500", row.DateText);
        }

        // ---------------- cells ----------------

        [Fact]
        public void FormatDistance_HasASpaceBeforeTheUnit()
        {
            Assert.Equal("129 m", SpawnControlPresentation.FormatDistance(129.4, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void FormatRelativeSpeed_NotSampled_ReturnsAsciiDash()
        {
            Assert.Equal("-",
                SpawnControlPresentation.FormatRelativeSpeed(double.PositiveInfinity, CultureInfo.InvariantCulture));
            Assert.Equal("-",
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
        public void FormatRelativeSpeed_HonoursTheCallersCulture()
        {
            Assert.Equal("0,5 m/s",
                SpawnControlPresentation.FormatRelativeSpeed(0.5, new CultureInfo("de-DE")));
        }

        [Fact]
        public void RowHovers_AreInvariantUnderACommaLocale()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var row = Row(new NearbySpawnCandidate
                {
                    endUT = 500, distance = 100, relativeSpeed = 8.1
                });
                Assert.Contains("8.1 m/s", row.StatusHover);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        // ---------------- hover budget ----------------

        // catches: a runtime-built hover clipping in the window's one-line strip (750 px,
        // 102 chars). TooltipEchoBudgetTests cannot see these: they are composed, not
        // literal. Worst realistic inputs: a 20-character craft name, a two-digit-year
        // date, the longest destination clause.
        [Fact]
        public void RuntimeHovers_FitTheSingleLineStrip()
        {
            int budget = TooltipEchoBox.BudgetChars(SpawnControlUI.DefaultWindowWidth, TooltipEchoBox.SingleLine);
            System.Func<double, string> longDate = ut => "Y12, D426, 05:59";
            const string name = "Spawn Control Target";
            var texts = new List<string>();
            var ready = SpawnControlPresentation.BuildRowPresentation(new NearbySpawnCandidate
            {
                vesselName = name, endUT = 500, distance = 10, relativeSpeed = 0.1
            }, 100, Radius, MaxRelSpeed, longDate);
            var leaves = SpawnControlPresentation.BuildRowPresentation(new NearbySpawnCandidate
            {
                vesselName = name, endUT = 900, willDepart = true, departureUT = 500,
                departureKind = DepartureKind.Crash, destination = "Kerbin",
                distance = 10, relativeSpeed = 0.1
            }, 100, Radius, MaxRelSpeed, longDate);
            texts.Add(ready.StatusHover);
            texts.Add(ready.WarpButtonHover);
            texts.Add(leaves.StatusHover);
            texts.Add(leaves.WarpButtonHover);
            texts.Add(SelectiveSpawnUI.FormatNextSpawnTooltip(new NearbySpawnCandidate
            {
                vesselName = name, willDepart = true, departureUT = 100 + 3 * 86400 + 5 * 3600
            }, 100));
            texts.Add(SelectiveSpawnUI.FormatNextSpawnTooltip(new NearbySpawnCandidate
            {
                vesselName = name, endUT = 100 + 3 * 86400 + 5 * 3600
            }, 100));
            foreach (string t in texts)
            {
                Assert.False(string.IsNullOrEmpty(t));
                Assert.True(t.Length <= budget, t.Length + " > " + budget + ": " + t);
            }
        }

        [Fact]
        public void PlayerText_IsAscii()
        {
            // The old window drew a Unicode arrow ("Departing -> Mun") and an em-dash speed.
            var row = Row(new NearbySpawnCandidate
            {
                vesselName = "x", willDepart = true, departureUT = 100,
                departureKind = DepartureKind.OtherBody, destination = "Mun",
                distance = 10, relativeSpeed = 0.1
            });
            foreach (string t in new[]
                     {
                         row.StatusText, row.StatusHover, row.WarpButtonLabel,
                         row.WarpButtonDisabledReason,
                         SpawnControlPresentation.FormatRelativeSpeed(double.NaN, CultureInfo.InvariantCulture),
                     })
                foreach (char c in t)
                    Assert.True(c < 128, "non-ASCII char U+" + ((int)c).ToString("X4") + " in: " + t);
        }

        // ---------------- first-open height ----------------

        [Fact]
        public void FirstOpenHeight_FitsTheRows_FlooredAndCapped()
        {
            float one = SpawnControlPresentation.FirstOpenHeight(1);
            float two = SpawnControlPresentation.FirstOpenHeight(2);
            Assert.Equal(191f, one);
            Assert.Equal(33f, two - one);
            Assert.Equal(one, SpawnControlPresentation.FirstOpenHeight(0));
            Assert.True(one >= SpawnControlUI.MinWindowHeight);
            Assert.Equal(400f, SpawnControlPresentation.FirstOpenHeight(50));
        }
    }
}
