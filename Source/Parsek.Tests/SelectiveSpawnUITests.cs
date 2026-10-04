using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SelectiveSpawnUITests : System.IDisposable
    {
        private const double Radius = 250.0;
        private const double MaxRelSpeed = 2.0;
        private readonly List<string> logLines = new List<string>();

        public SelectiveSpawnUITests()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            // Default to Earth time for existing tests
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            SelectiveSpawnUI.KerbinTimeOverrideForTesting = null;
        }

        // ── IsSpawnCandidate ──

        [Fact]
        public void IsSpawnCandidate_AllConditionsMet_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_PastEndUT_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 50, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_NoSpawnNeeded_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: false, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_ChainSuppressed_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: true,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_OutsideRadius_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 300, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_AtExactRadius_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 250, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_EndUTEqualsCurrentUT_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 100, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 1.0, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_AboveMaxRelativeSpeed_False()
        {
            Assert.False(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 2.5, maxRelativeSpeed: 2.0));
        }

        [Fact]
        public void IsSpawnCandidate_AtExactMaxRelativeSpeed_True()
        {
            Assert.True(SelectiveSpawnUI.IsSpawnCandidate(
                endUT: 200, currentUT: 100,
                needsSpawn: true, chainSuppressed: false,
                distance: 200, proximityRadius: 250,
                relativeSpeed: 2.0, maxRelativeSpeed: 2.0));
        }

        // ── ComputeRelativeSpeed ──

        [Fact]
        public void ComputeRelativeSpeed_StationKeeping_ReturnsZero()
        {
            // Same relative offset at both samples: ghost and active vessel moved together.
            var prevActive = new Vector3d(1000, 0, 0);
            var prevGhost = new Vector3d(1100, 0, 0);
            var nowActive = new Vector3d(1500, 200, 0);  // both translated by (500, 200, 0)
            var nowGhost = new Vector3d(1600, 200, 0);
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                nowActive, nowGhost, prevActive, prevGhost,
                dt: 1.5f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(0.0, rel, precision: 6);
        }

        [Fact]
        public void ComputeRelativeSpeed_FrameShift_Cancels()
        {
            // Floating-origin shift: same uniform offset added to all four positions.
            // Relative geometry preserved -> relative speed should still be zero.
            var prevActive = new Vector3d(0, 0, 0);
            var prevGhost = new Vector3d(50, 0, 0);
            var shift = new Vector3d(1e6, -2e6, 5e5);
            var nowActive = prevActive + shift;
            var nowGhost = prevGhost + shift;
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                nowActive, nowGhost, prevActive, prevGhost,
                dt: 1.5f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(0.0, rel, precision: 6);
        }

        [Fact]
        public void ComputeRelativeSpeed_ApproachingAt3MperS_Returns3()
        {
            // Active stationary; ghost moves 4.5 m closer along x over 1.5s -> 3 m/s.
            var prevActive = new Vector3d(0, 0, 0);
            var prevGhost = new Vector3d(100, 0, 0);
            var nowActive = new Vector3d(0, 0, 0);
            var nowGhost = new Vector3d(95.5, 0, 0);
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                nowActive, nowGhost, prevActive, prevGhost,
                dt: 1.5f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(3.0, rel, precision: 6);
        }

        [Fact]
        public void ComputeRelativeSpeed_DtTooShort_ReturnsInfinity()
        {
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                Vector3d.zero, Vector3d.zero, Vector3d.zero, Vector3d.zero,
                dt: 0.1f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(double.PositiveInfinity, rel);
        }

        [Fact]
        public void ComputeRelativeSpeed_DtTooLong_ReturnsInfinity()
        {
            // Stale sample (e.g. after time warp / scene change) — must reject.
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                Vector3d.zero, Vector3d.zero, Vector3d.zero, Vector3d.zero,
                dt: 10f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(double.PositiveInfinity, rel);
        }

        [Fact]
        public void ComputeRelativeSpeed_NegativeDt_ReturnsInfinity()
        {
            double rel = SelectiveSpawnUI.ComputeRelativeSpeed(
                Vector3d.zero, Vector3d.zero, Vector3d.zero, Vector3d.zero,
                dt: -1f, minDt: 0.5f, maxDt: 5.0f);
            Assert.Equal(double.PositiveInfinity, rel);
        }

        // ── FindNextSpawnCandidate ──

        [Fact]
        public void FindNextSpawnCandidate_ReturnsEarliestFuture()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { recordingIndex = 0, vesselName = "A", endUT = 300 },
                new NearbySpawnCandidate { recordingIndex = 1, vesselName = "B", endUT = 150 },
                new NearbySpawnCandidate { recordingIndex = 2, vesselName = "C", endUT = 200 }
            };

            var result = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed);

            Assert.NotNull(result);
            Assert.Equal("B", result.Value.vesselName);
        }

        [Fact]
        public void FindNextSpawnCandidate_AllPast_ReturnsNull()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { endUT = 50 },
                new NearbySpawnCandidate { endUT = 80 }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_Empty_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(
                new List<NearbySpawnCandidate>(), 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_Null_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(null, 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_EndUTEqualsCurrentUT_ReturnsNull()
        {
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate { endUT = 100 }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed));
        }

        [Fact]
        public void FindNextSpawnCandidate_FarButSlow_SkippedInFavorOfInnerCandidate()
        {
            // Slow ghost at 500m sits inside the wider "show in list" envelope but outside the
            // 250m FF radius — the bottom-bar "Warp to Next Spawn" must not pick it.
            // The closer slow ghost (within both gates) wins even though its endUT is later.
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate
                {
                    vesselName = "FarSlow",
                    distance = 500,        // > Radius (250)
                    relativeSpeed = 0.5,   // <= MaxRelSpeed
                    endUT = 200            // earlier
                },
                new NearbySpawnCandidate
                {
                    vesselName = "NearSlow",
                    distance = 100,        // <= Radius
                    relativeSpeed = 0.5,
                    endUT = 400            // later
                }
            };

            var result = SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed);

            Assert.NotNull(result);
            Assert.Equal("NearSlow", result.Value.vesselName);
        }

        [Fact]
        public void FindNextSpawnCandidate_OnlyFarSlowCandidate_ReturnsNull()
        {
            // Lone slow-but-distant ghost: window shows it (red distance text, FF disabled),
            // bottom-bar "Warp to Next Spawn" stays disabled.
            var candidates = new List<NearbySpawnCandidate>
            {
                new NearbySpawnCandidate
                {
                    vesselName = "FarSlow",
                    distance = 500,
                    relativeSpeed = 0.5,
                    endUT = 200
                }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnCandidate(candidates, 100, Radius, MaxRelSpeed));
        }

        // ── FormatNextSpawnTooltip ──

        [Fact]
        public void FormatNextSpawnTooltip_HasCandidate()
        {
            var cand = new NearbySpawnCandidate { vesselName = "Station", endUT = 200 };

            string result = SelectiveSpawnUI.FormatNextSpawnTooltip(cand, 100);

            Assert.Equal("Warps to when Station spawns here, in 1m 40s.", result);
        }

        [Fact]
        public void FormatNextSpawnTooltip_DepartingCandidate_UsesDepartAction()
        {
            var cand = new NearbySpawnCandidate
            {
                vesselName = "Station",
                endUT = 400,
                willDepart = true,
                departureUT = 220
            };

            string result = SelectiveSpawnUI.FormatNextSpawnTooltip(cand, 100);

            // S1: the warp lands just before it leaves, and it does not spawn here.
            Assert.Equal(
                "Warps to just before Station leaves orbit in 2m 0s; it does not spawn here.",
                result);
        }

        [Fact]
        public void FormatNextSpawnTooltip_NullCandidate()
        {
            string result = SelectiveSpawnUI.FormatNextSpawnTooltip(null, 100);
            Assert.Equal("No nearby craft to spawn", result);
        }

        // --- Proximity screen message ---
        // The message must not tell the player to open Real Spawn Control when Basic UI mode
        // has removed its launcher: a 10-second on-screen instruction to open a window with
        // no button is a dead end. The observation itself is still worth showing, so only the
        // call to action drops.

        [Fact]
        public void FormatProximityNotification_Reachable_NamesTheWindow()
        {
            var cand = new NearbySpawnCandidate { vesselName = "Kerbal X" };

            string result = SelectiveSpawnUI.FormatProximityNotification(cand, 100, true);

            Assert.Equal(
                "Nearby craft: Kerbal X. Open the Real Spawn Control window to fast forward and interact.",
                result);
        }

        [Fact]
        public void FormatProximityNotification_Unreachable_DropsTheInstruction()
        {
            var cand = new NearbySpawnCandidate { vesselName = "Kerbal X" };

            string result = SelectiveSpawnUI.FormatProximityNotification(cand, 100, false);

            Assert.Equal("Nearby craft: Kerbal X.", result);
            Assert.DoesNotContain("Spawn Control", result);
        }

        [Fact]
        public void FormatProximityNotification_Departing_KeepsTheDepartureFactsInBothModes()
        {
            var cand = new NearbySpawnCandidate
            {
                vesselName = "Kerbal X",
                willDepart = true,
                departureUT = 220,
                departureKind = DepartureKind.OtherBody,
                destination = "Mun"
            };

            string reachable = SelectiveSpawnUI.FormatProximityNotification(cand, 100, true);
            string basicMode = SelectiveSpawnUI.FormatProximityNotification(cand, 100, false);

            Assert.Equal("Nearby craft: Kerbal X (leaves orbit for Mun in 2m 0s). Open Real Spawn Control.",
                reachable);
            Assert.Equal("Nearby craft: Kerbal X (leaves orbit for Mun in 2m 0s).", basicMode);
            Assert.DoesNotContain("Spawn Control", basicMode);
        }

        [Fact]
        public void FormatProximityNotification_SameBodyOrbitChange_NeverPrintsTheRawWord()
        {
            // Bug: a same-body orbit change read "departs to maneuver", in both modes.
            var cand = new NearbySpawnCandidate
            {
                vesselName = "Tug", willDepart = true, departureUT = 220,
                departureKind = DepartureKind.NewOrbit, destination = "Kerbin"
            };

            string basicMode = SelectiveSpawnUI.FormatProximityNotification(cand, 100, false);

            Assert.Equal("Nearby craft: Tug (leaves orbit for a new orbit in 2m 0s).", basicMode);
            Assert.DoesNotContain("maneuver", basicMode);
        }

        [Fact]
        public void FormatProximityNotification_Landing_DoesNotSayItDepartsToTheBodyItOrbits()
        {
            // Bug: a craft orbiting Kerbin that will land read "departs to Kerbin".
            var cand = new NearbySpawnCandidate
            {
                vesselName = "Pod", willDepart = true, departureUT = 220,
                departureKind = DepartureKind.Landing, destination = "Kerbin"
            };

            string msg = SelectiveSpawnUI.FormatProximityNotification(cand, 100, false);

            Assert.Equal("Nearby craft: Pod (leaves orbit to land on Kerbin in 2m 0s).", msg);
            Assert.DoesNotContain("to Kerbin", msg.Replace("land on Kerbin", ""));
        }

        // --- Departure destination wording ---

        [Theory]
        [InlineData((int)DepartureKind.OtherBody, "Mun", "for Mun")]
        [InlineData((int)DepartureKind.OtherBody, null, "for another body")]
        [InlineData((int)DepartureKind.NewOrbit, "Kerbin", "for a new orbit")]
        [InlineData((int)DepartureKind.Landing, "Kerbin", "to land on Kerbin")]
        [InlineData((int)DepartureKind.Landing, null, "to land")]
        [InlineData((int)DepartureKind.Crash, "Duna", "to come down on Duna")]
        [InlineData((int)DepartureKind.Crash, "", "to come down")]
        [InlineData((int)DepartureKind.None, "Mun", "")]
        public void FormatDepartureDestination_IsPlayerWords(
            int kind, string body, string expected)
        {
            Assert.Equal(expected, SelectiveSpawnUI.FormatDepartureDestination((DepartureKind)kind, body));
        }

        // --- List filter (S2: distance hides, speed greys) ---

        [Theory]
        [InlineData(100.0, 0.5, true)]     // inside both warp gates
        [InlineData(250.0, 0.5, true)]     // exactly on the spawn radius
        [InlineData(250.5, 0.5, false)]    // just past it: cannot spawn, not listed
        [InlineData(470.0, 0.5, false)]    // the old "Too far" band is hidden
        [InlineData(100.0, 8.1, true)]     // too fast: kept, greyed "Too fast"
        [InlineData(100.0, 50.0, true)]    // up to the list speed bound
        [InlineData(100.0, 50.1, false)]   // past it
        [InlineData(100.0, double.PositiveInfinity, false)]
        public void IsListedCandidate_HidesByDistance_KeepsTooFast(
            double distance, double speed, bool listed)
        {
            Assert.Equal(listed, SelectiveSpawnUI.IsListedCandidate(
                distance, speed, ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxListRelativeSpeed));
        }

        [Fact]
        public void IsListedCandidate_UsesTheSpawnRadiusAsTheListBound()
        {
            // The documented spawn radius (250 m) is the list bound: a ghost the warp gate
            // refuses by distance is never listed, whatever its speed.
            Assert.Equal(250.0, ParsekFlight.NearbySpawnRadius);
            Assert.False(SelectiveSpawnUI.IsListedCandidate(
                ParsekFlight.NearbySpawnRadius + 0.01, 0.0,
                ParsekFlight.NearbySpawnRadius, ParsekFlight.MaxListRelativeSpeed));
        }

        [Fact]
        public void EffectiveWarpUT_IsTheDepartureForALeavingCraft_TheSpawnOtherwise()
        {
            Assert.Equal(300, SelectiveSpawnUI.EffectiveWarpUT(new NearbySpawnCandidate
            {
                endUT = 5000, willDepart = true, departureUT = 300
            }));
            Assert.Equal(5000, SelectiveSpawnUI.EffectiveWarpUT(new NearbySpawnCandidate
            {
                endUT = 5000
            }));
        }
    }
}
