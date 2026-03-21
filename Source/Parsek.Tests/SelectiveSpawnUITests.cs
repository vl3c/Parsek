using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SelectiveSpawnUITests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SelectiveSpawnUITests()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ── GetPendingChainTipCount ──

        [Fact]
        public void GetPendingChainTipCount_NullChains_ReturnsZero()
        {
            Assert.Equal(0, SelectiveSpawnUI.GetPendingChainTipCount(null));
        }

        [Fact]
        public void GetPendingChainTipCount_FiltersTerminated()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, IsTerminated = false } },
                { 2, new GhostChain { OriginalVesselPid = 2, IsTerminated = true } },
                { 3, new GhostChain { OriginalVesselPid = 3, IsTerminated = false } }
            };

            Assert.Equal(2, SelectiveSpawnUI.GetPendingChainTipCount(chains));
        }

        // ── GetPendingChainTips ──

        [Fact]
        public void GetPendingChainTips_NullChains_ReturnsEmpty()
        {
            var result = SelectiveSpawnUI.GetPendingChainTips(null);
            Assert.Empty(result);
        }

        [Fact]
        public void GetPendingChainTips_EmptyChains_ReturnsEmpty()
        {
            var result = SelectiveSpawnUI.GetPendingChainTips(new Dictionary<uint, GhostChain>());
            Assert.Empty(result);
        }

        [Fact]
        public void GetPendingChainTips_FiltersTerminated()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 100, IsTerminated = false } },
                { 2, new GhostChain { OriginalVesselPid = 2, SpawnUT = 200, IsTerminated = true } },
                { 3, new GhostChain { OriginalVesselPid = 3, SpawnUT = 150, IsTerminated = false } }
            };

            var result = SelectiveSpawnUI.GetPendingChainTips(chains);

            Assert.Equal(2, result.Count);
            Assert.Equal((uint)1, result[0].OriginalVesselPid);
            Assert.Equal((uint)3, result[1].OriginalVesselPid);
        }

        [Fact]
        public void GetPendingChainTips_SortedBySpawnUT()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 300 } },
                { 2, new GhostChain { OriginalVesselPid = 2, SpawnUT = 100 } },
                { 3, new GhostChain { OriginalVesselPid = 3, SpawnUT = 200 } }
            };

            var result = SelectiveSpawnUI.GetPendingChainTips(chains);

            Assert.Equal((uint)2, result[0].OriginalVesselPid);
            Assert.Equal((uint)3, result[1].OriginalVesselPid);
            Assert.Equal((uint)1, result[2].OriginalVesselPid);
        }

        // ── ComputeNextSpawnUT ──

        [Fact]
        public void ComputeNextSpawnUT_ReturnsEarliestFuture()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 50 } },
                { 2, new GhostChain { OriginalVesselPid = 2, SpawnUT = 200 } },
                { 3, new GhostChain { OriginalVesselPid = 3, SpawnUT = 150 } }
            };

            double result = SelectiveSpawnUI.ComputeNextSpawnUT(chains, 100);

            Assert.Equal(150, result);
        }

        [Fact]
        public void ComputeNextSpawnUT_NoPendingFuture_ReturnsZero()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 50 } }
            };

            double result = SelectiveSpawnUI.ComputeNextSpawnUT(chains, 100);

            Assert.Equal(0, result);
        }

        [Fact]
        public void ComputeNextSpawnUT_SkipsTerminated()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 150, IsTerminated = true } },
                { 2, new GhostChain { OriginalVesselPid = 2, SpawnUT = 200 } }
            };

            double result = SelectiveSpawnUI.ComputeNextSpawnUT(chains, 100);

            Assert.Equal(200, result);
        }

        // ── FindNextSpawnChain (replaces ShouldEnableWarpToNext) ──

        [Fact]
        public void FindNextSpawnChain_FutureChain_ReturnsChain()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 200 } }
            };

            Assert.NotNull(SelectiveSpawnUI.FindNextSpawnChain(chains, 100));
        }

        [Fact]
        public void FindNextSpawnChain_AllPast_ReturnsNull()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 50 } }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnChain(chains, 100));
        }

        [Fact]
        public void FindNextSpawnChain_EmptyChains_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FindNextSpawnChain(
                new Dictionary<uint, GhostChain>(), 100));
        }

        // ── CanWarpToChain ──

        [Fact]
        public void CanWarpToChain_FutureNonTerminated_True()
        {
            var chain = new GhostChain { OriginalVesselPid = 1, SpawnUT = 200 };
            Assert.True(SelectiveSpawnUI.CanWarpToChain(chain, 100));
        }

        [Fact]
        public void CanWarpToChain_Terminated_False()
        {
            var chain = new GhostChain { OriginalVesselPid = 1, SpawnUT = 200, IsTerminated = true };
            Assert.False(SelectiveSpawnUI.CanWarpToChain(chain, 100));
        }

        [Fact]
        public void CanWarpToChain_PastSpawnUT_False()
        {
            var chain = new GhostChain { OriginalVesselPid = 1, SpawnUT = 50 };
            Assert.False(SelectiveSpawnUI.CanWarpToChain(chain, 100));
        }

        [Fact]
        public void CanWarpToChain_Null_False()
        {
            Assert.False(SelectiveSpawnUI.CanWarpToChain(null, 100));
        }

        // ── FindAlsoSpawnedChains ──

        [Fact]
        public void FindAlsoSpawnedChains_ReturnsIntermediateChains()
        {
            var chainA = new GhostChain { OriginalVesselPid = 1, SpawnUT = 150 };
            var chainB = new GhostChain { OriginalVesselPid = 2, SpawnUT = 300 };
            var chainC = new GhostChain { OriginalVesselPid = 3, SpawnUT = 200 };

            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, chainA }, { 2, chainB }, { 3, chainC }
            };

            // Select chainB (SpawnUT=300) — chainA and chainC are between currentUT=100 and 300
            var result = SelectiveSpawnUI.FindAlsoSpawnedChains(chains, chainB, 100);

            Assert.Equal(2, result.Count);
            Assert.Equal((uint)1, result[0].OriginalVesselPid); // SpawnUT=150 first
            Assert.Equal((uint)3, result[1].OriginalVesselPid); // SpawnUT=200 second
        }

        [Fact]
        public void FindAlsoSpawnedChains_ExcludesSelectedAndTerminated()
        {
            var selected = new GhostChain { OriginalVesselPid = 1, SpawnUT = 300 };
            var terminated = new GhostChain { OriginalVesselPid = 2, SpawnUT = 200, IsTerminated = true };
            var valid = new GhostChain { OriginalVesselPid = 3, SpawnUT = 150 };

            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, selected }, { 2, terminated }, { 3, valid }
            };

            var result = SelectiveSpawnUI.FindAlsoSpawnedChains(chains, selected, 100);

            Assert.Single(result);
            Assert.Equal((uint)3, result[0].OriginalVesselPid);
        }

        [Fact]
        public void FindAlsoSpawnedChains_NoneInRange_ReturnsEmpty()
        {
            var selected = new GhostChain { OriginalVesselPid = 1, SpawnUT = 150 };
            var later = new GhostChain { OriginalVesselPid = 2, SpawnUT = 300 };

            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, selected }, { 2, later }
            };

            var result = SelectiveSpawnUI.FindAlsoSpawnedChains(chains, selected, 100);

            Assert.Empty(result);
        }

        // ── FormatWarpButtonText ──

        [Fact]
        public void FormatWarpButtonText_FutureChain_ShowsDelta()
        {
            var chain = new GhostChain { SpawnUT = 200 };
            string text = SelectiveSpawnUI.FormatWarpButtonText(chain, 100);
            Assert.Contains("Warp", text);
            Assert.Contains("1m 40s", text);
        }

        [Fact]
        public void FormatWarpButtonText_PastChain_SpawnNow()
        {
            var chain = new GhostChain { SpawnUT = 50 };
            string text = SelectiveSpawnUI.FormatWarpButtonText(chain, 100);
            Assert.Equal("Spawn Now", text);
        }

        [Fact]
        public void FormatWarpButtonText_Null_ReturnsWarp()
        {
            Assert.Equal("Warp", SelectiveSpawnUI.FormatWarpButtonText(null, 100));
        }

        // ── FormatTimeDelta ──

        [Fact]
        public void FormatTimeDelta_UnderMinute()
        {
            Assert.Equal("45s", SelectiveSpawnUI.FormatTimeDelta(45));
        }

        [Fact]
        public void FormatTimeDelta_UnderHour()
        {
            Assert.Equal("5m 30s", SelectiveSpawnUI.FormatTimeDelta(330));
        }

        [Fact]
        public void FormatTimeDelta_OverHour()
        {
            Assert.Equal("2h 15m", SelectiveSpawnUI.FormatTimeDelta(8100));
        }

        [Fact]
        public void FormatTimeDelta_Negative_Clamps()
        {
            Assert.Equal("0s", SelectiveSpawnUI.FormatTimeDelta(-10));
        }

        // ── FormatAlsoSpawnsWarning ──

        [Fact]
        public void FormatAlsoSpawnsWarning_Empty_ReturnsNull()
        {
            Assert.Null(SelectiveSpawnUI.FormatAlsoSpawnsWarning(
                new List<GhostChain>(), null));
        }

        [Fact]
        public void FormatAlsoSpawnsWarning_SingleChain()
        {
            var names = new Dictionary<uint, string> { { 1, "Relay Sat" } };
            var also = new List<GhostChain>
            {
                new GhostChain { OriginalVesselPid = 1 }
            };

            string result = SelectiveSpawnUI.FormatAlsoSpawnsWarning(also, names);
            Assert.Equal("Also spawns: Relay Sat", result);
        }

        [Fact]
        public void FormatAlsoSpawnsWarning_MultipleChains()
        {
            var names = new Dictionary<uint, string>
            {
                { 1, "Relay Sat" }, { 2, "Fuel Depot" }
            };
            var also = new List<GhostChain>
            {
                new GhostChain { OriginalVesselPid = 1 },
                new GhostChain { OriginalVesselPid = 2 }
            };

            string result = SelectiveSpawnUI.FormatAlsoSpawnsWarning(also, names);
            Assert.Equal("Also spawns: Relay Sat, Fuel Depot", result);
        }

        [Fact]
        public void FormatAlsoSpawnsWarning_UnknownVessel_FallsBack()
        {
            var also = new List<GhostChain>
            {
                new GhostChain { OriginalVesselPid = 42 }
            };

            string result = SelectiveSpawnUI.FormatAlsoSpawnsWarning(also, null);
            Assert.Equal("Also spawns: Vessel 42", result);
        }

        // ── FormatNextSpawnTooltip ──

        [Fact]
        public void FormatNextSpawnTooltip_HasPending()
        {
            var chain = new GhostChain { OriginalVesselPid = 1, SpawnUT = 200 };
            var names = new Dictionary<uint, string> { { 1, "Station" } };

            string result = SelectiveSpawnUI.FormatNextSpawnTooltip(chain, 100, names);

            Assert.Contains("Station", result);
            Assert.Contains("1m 40s", result);
        }

        [Fact]
        public void FormatNextSpawnTooltip_NoPending()
        {
            string result = SelectiveSpawnUI.FormatNextSpawnTooltip(null, 100, null);
            Assert.Equal("No pending spawns", result);
        }

        // ── FindNextSpawnChain ──

        [Fact]
        public void FindNextSpawnChain_ReturnsEarliestFuture()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 50 } },
                { 2, new GhostChain { OriginalVesselPid = 2, SpawnUT = 200 } },
                { 3, new GhostChain { OriginalVesselPid = 3, SpawnUT = 150 } }
            };

            var result = SelectiveSpawnUI.FindNextSpawnChain(chains, 100);

            Assert.NotNull(result);
            Assert.Equal((uint)3, result.OriginalVesselPid);
        }

        [Fact]
        public void FindNextSpawnChain_NoPending_ReturnsNull()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 50 } }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnChain(chains, 100));
        }

        // ── Boundary: SpawnUT == currentUT ──

        [Fact]
        public void CanWarpToChain_SpawnUTEqualsCurrentUT_False()
        {
            var chain = new GhostChain { OriginalVesselPid = 1, SpawnUT = 100 };
            Assert.False(SelectiveSpawnUI.CanWarpToChain(chain, 100));
        }

        [Fact]
        public void ComputeNextSpawnUT_SpawnUTEqualsCurrentUT_ReturnsZero()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 100 } }
            };

            Assert.Equal(0, SelectiveSpawnUI.ComputeNextSpawnUT(chains, 100));
        }

        [Fact]
        public void FindNextSpawnChain_SpawnUTEqualsCurrentUT_ReturnsNull()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 100 } }
            };

            Assert.Null(SelectiveSpawnUI.FindNextSpawnChain(chains, 100));
        }

        // ── Log assertions ──

        [Fact]
        public void GetPendingChainTips_Logs()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, new GhostChain { OriginalVesselPid = 1, SpawnUT = 200 } }
            };

            SelectiveSpawnUI.GetPendingChainTips(chains);

            Assert.Contains(logLines, l =>
                l.Contains("[SelectiveSpawn]") && l.Contains("1 pending"));
        }

        [Fact]
        public void CanWarpToChain_Logs()
        {
            var chain = new GhostChain { OriginalVesselPid = 1, SpawnUT = 200 };
            SelectiveSpawnUI.CanWarpToChain(chain, 100);

            Assert.Contains(logLines, l =>
                l.Contains("[SelectiveSpawn]") && l.Contains("CanWarpToChain"));
        }

        // ── BuildTipRecordingToChainMap ──

        [Fact]
        public void BuildTipRecordingToChainMap_NullChains_ReturnsEmpty()
        {
            var result = SelectiveSpawnUI.BuildTipRecordingToChainMap(null);
            Assert.Empty(result);
        }

        [Fact]
        public void BuildTipRecordingToChainMap_EmptyChains_ReturnsEmpty()
        {
            var result = SelectiveSpawnUI.BuildTipRecordingToChainMap(
                new Dictionary<uint, GhostChain>());
            Assert.Empty(result);
        }

        [Fact]
        public void BuildTipRecordingToChainMap_MapsNonTerminatedByTipRecordingId()
        {
            var chainA = new GhostChain { OriginalVesselPid = 1, TipRecordingId = "rec-a" };
            var chainB = new GhostChain { OriginalVesselPid = 2, TipRecordingId = "rec-b", IsTerminated = true };
            var chainC = new GhostChain { OriginalVesselPid = 3, TipRecordingId = "rec-c" };

            var chains = new Dictionary<uint, GhostChain>
            {
                { 1, chainA }, { 2, chainB }, { 3, chainC }
            };

            var result = SelectiveSpawnUI.BuildTipRecordingToChainMap(chains);

            Assert.Equal(2, result.Count);
            Assert.Same(chainA, result["rec-a"]);
            Assert.Same(chainC, result["rec-c"]);
            Assert.False(result.ContainsKey("rec-b"));
        }

        [Fact]
        public void BuildTipRecordingToChainMap_SkipsNullTipRecordingId()
        {
            var chain = new GhostChain { OriginalVesselPid = 1, TipRecordingId = null };

            var chains = new Dictionary<uint, GhostChain> { { 1, chain } };

            var result = SelectiveSpawnUI.BuildTipRecordingToChainMap(chains);
            Assert.Empty(result);
        }
    }
}
