using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The <c>stock-screen-census-repair</c> fixture (<see cref="StockScreenRepairFixture"/>):
    /// the committed tree equals a fresh build, and the committed save and ledger, read the
    /// way the game reads them, give the Tracking Station the repair block
    /// KSC-REPAIR-AFTER-REWIND-DOUBLE-CHARGE is about (and the census sibling does not), so
    /// a KB-2 flight without the block is a product finding and not a fixture that failed
    /// to stage the state.
    /// </summary>
    [Collection("Sequential")]
    public class StockScreenRepairFixtureTests : IDisposable
    {
        private const double Now = StockScreenCensusFixture.SaveUT;

        public StockScreenRepairFixtureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static string RepoRoot() => SyntheticRecordingTests.ResolveProjectRoot();

        /// <summary>Set PARSEK_WRITE_STOCK_SCREEN_FIXTURE=1 to (re)write the fixture.</summary>
        [Trait("Category", "Manual")]
        [StockScreenCensusFixtureTests.WriteFixtureFact]
        public void WriteRepairFixture()
        {
            string target = StockScreenRepairFixture.TargetDir(RepoRoot());
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            foreach (var kv in StockScreenRepairFixture.BuildFiles(RepoRoot()))
            {
                string path = Path.Combine(target, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, kv.Value);
            }
        }

        [Fact]
        public void Build_IsDeterministic()
        {
            var a = StockScreenRepairFixture.BuildFiles(RepoRoot());
            var b = StockScreenRepairFixture.BuildFiles(RepoRoot());
            Assert.Equal(a.Keys.ToList(), b.Keys.ToList());
            foreach (var key in a.Keys)
                Assert.True(a[key].SequenceEqual(b[key]), key + " differs between two builds");
        }

        /// <summary>Compared after CRLF -> LF normalization on both sides, as the census
        /// sibling is: the flight's sidecars come from the running platform's writer.</summary>
        [Fact]
        public void CommittedFixture_MatchesTheBuilder()
        {
            string target = StockScreenRepairFixture.TargetDir(RepoRoot());
            Assert.True(Directory.Exists(target), "fixture missing: " + target
                + " (write it with PARSEK_WRITE_STOCK_SCREEN_FIXTURE=1 dotnet test --filter WriteRepairFixture)");
            var expected = StockScreenRepairFixture.BuildFiles(RepoRoot());
            var committed = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
                .Select(p => p.Substring(target.Length + 1).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(expected.Keys.ToList(), committed);
            foreach (var kv in expected)
            {
                byte[] onDisk = File.ReadAllBytes(Path.Combine(target, kv.Key));
                Assert.True(Normalize(onDisk).SequenceEqual(Normalize(kv.Value)),
                    kv.Key + " differs from a fresh build - rebuild the fixture, never hand-edit it");
            }
        }

        private static byte[] Normalize(byte[] bytes)
        {
            var result = new List<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') continue;
                result.Add(bytes[i]);
            }
            return result.ToArray();
        }

        // ------------------------------------------------------------------ reading

        private static string Dir(string fixture) => Path.Combine(StockScreenCensusFixture.SavesDir(RepoRoot()), fixture);

        private static ConfigNode Game(string fixture)
        {
            ConfigNode root = ConfigNode.Load(Path.Combine(Dir(fixture), "persistent.sfs"));
            Assert.NotNull(root);
            return root.GetNode("GAME");
        }

        private static ConfigNode Scenario(ConfigNode game, string name)
        {
            foreach (ConfigNode s in game.GetNodes("SCENARIO"))
                if (s.GetValue("name") == name) return s;
            throw new Xunit.Sdk.XunitException("SCENARIO " + name + " missing");
        }

        private static List<GameAction> Ledger(string fixture)
        {
            ConfigNode ledger = ConfigNode.Load(Path.Combine(Dir(fixture), "Parsek", "GameState", "ledger.pgld"));
            return ledger.GetNodes("GAME_ACTION").Select(GameAction.DeserializeFrom).ToList();
        }

        private static HashSet<string> RecordingIds(string fixture)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigNode tree in Scenario(Game(fixture), "ParsekScenario").GetNodes("RECORDING_TREE"))
                foreach (ConfigNode rec in tree.GetNodes("RECORDING"))
                    ids.Add(rec.GetValue("recordingId"));
            return ids;
        }

        private static CommittedFutureIndex Index(string fixture)
        {
            var ids = RecordingIds(fixture);
            return CommittedFutureIndex.Build(Ledger(fixture), id => ids.Contains(id),
                id => id == StockScreenCensusFixture.FlightRecordingId ? StockScreenCensusFixture.FlightName : null,
                null);
        }

        /// <summary>The Tracking Station's live buildings as stock loads them from
        /// <c>ScenarioDestructibles</c>: the two level-0 destructibles KB-1 measured.</summary>
        private static List<FacilityRepairCapture.BuildingRepairInput> TrackingStationBuildings(string fixture)
        {
            ConfigNode destructibles = Scenario(Game(fixture), "ScenarioDestructibles");
            var result = new List<FacilityRepairCapture.BuildingRepairInput>();
            foreach (string id in new[] { StockScreenRepairFixture.IntactSibling, StockScreenRepairFixture.DestroyedBuilding })
            {
                ConfigNode node = destructibles.GetNode(id);
                Assert.NotNull(node);
                result.Add(new FacilityRepairCapture.BuildingRepairInput
                {
                    BuildingId = id,
                    RepairCost = StockScreenRepairFixture.RepairCost,
                    IsDestroyed = node.GetValue("intact") == "False",
                });
            }
            return result;
        }

        private static string Date(double ut) => "UT" + ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        [Fact]
        public void Save_OnlyTheDishIsDown_AndEverythingElseIsTheCensus()
        {
            ConfigNode game = Game(StockScreenRepairFixture.FixtureName);
            Assert.Equal(StockScreenRepairFixture.Title, game.GetValue("Title"));
            Assert.Equal(StockScreenCensusFixture.SaveUT,
                double.Parse(game.GetNode("FLIGHTSTATE").GetValue("UT"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Empty(game.GetNode("FLIGHTSTATE").GetNodes("VESSEL"));

            ConfigNode destructibles = Scenario(game, "ScenarioDestructibles");
            var down = destructibles.nodes.Cast<ConfigNode>().Where(n => n.GetValue("intact") == "False")
                .Select(n => n.name).ToList();
            Assert.Equal(new[] { StockScreenRepairFixture.DestroyedBuilding }, down);
            Assert.Equal("0", Scenario(game, "ScenarioUpgradeableFacilities")
                .GetNode(StockScreenRepairFixture.Facility).GetValue("lvl"));

            // The census's own ledger rows are all still there, plus exactly the two.
            var census = Ledger(StockScreenCensusFixture.FixtureName);
            var repair = Ledger(StockScreenRepairFixture.FixtureName);
            Assert.Equal(census.Count + 2, repair.Count);
            Assert.Equal(census.Select(a => a.ActionId), repair.Take(census.Count).Select(a => a.ActionId));
        }

        [Fact]
        public void Ledger_DestructionIsPast_RepairIsFuture_SameBuilding()
        {
            var rows = StockScreenRepairFixture.BuildLedgerRows();
            GameAction destruction = rows.Single(a => a.Type == GameActionType.FacilityDestruction);
            GameAction repair = rows.Single(a => a.Type == GameActionType.FacilityRepair);
            Assert.True(destruction.UT < Now, "the collapse must lie before the save clock");
            Assert.True(repair.UT > Now, "the committed repair must lie after the save clock");
            Assert.Equal(destruction.FacilityId, repair.FacilityId);
            Assert.True(repair.UT < StockScreenCensusFixture.FacilityUpgradeUT,
                "the repair must precede the census's Tracking Station upgrade (which resets structures)");
            Assert.True(string.IsNullOrEmpty(destruction.RecordingId) && string.IsNullOrEmpty(repair.RecordingId),
                "both rows are KSC-origin");
            Assert.True(CommittedFutureIndex.IsFuture(repair.UT, Now + 3600),
                "an hour of lane at 1x must not reach the committed repair");
        }

        [Fact]
        public void Index_TrackingStationRepairIsBlocked_WithTheExplanation()
        {
            var index = Index(StockScreenRepairFixture.FixtureName);
            Assert.Equal(0, index.SkippedUncommittedRows);
            var buildings = TrackingStationBuildings(StockScreenRepairFixture.FixtureName);
            Assert.True(StockUiReservationPredicates.IsFacilityRepairBlocked(index, buildings, Now));

            var covering = StockUiReservationPredicates.CommittedRepairsCoveringFacility(index, buildings, Now);
            Assert.Single(covering);
            Assert.Equal(StockScreenRepairFixture.DestroyedBuilding, covering[0].Key);
            Assert.Equal(StockScreenRepairFixture.RepairUT, covering[0].UT);

            var d = StockUiDecorationQuery.ForFacilityMenuRepair(index, Now, StockScreenRepairFixture.Facility,
                buildings, replaying: false, formatDate: Date);
            Assert.True(d.Marked && d.Blocked);
            Assert.Equal(StockUiDecorationKind.FacilityRepair, d.Kind);
            Assert.Equal("Repaired on UT80000, blocked by timeline until then.", d.Why);

            // The census Upgrade block on the same menu now names the covering repair first:
            // stock's UpgradeFacility repairs the dish for free (ResetStructures), so the
            // repair holds Upgrade too (FACILITY-UPGRADE-FREE-REPAIR-REWRITES-COMMITTED-REPAIR).
            var upgrade = StockUiDecorationQuery.ForFacilityMenu(index, Now, StockScreenRepairFixture.Facility,
                buildings, false, Date);
            Assert.True(upgrade.Marked && upgrade.Blocked);
            Assert.Equal(StockUiDecorationKind.FacilityUpgrade, upgrade.Kind);
            Assert.Equal("Repaired on UT80000 and upgraded to level 2 on UT90000, blocked by timeline until then.",
                upgrade.Why);
            Assert.Equal(StockScreenRepairFixture.RepairUT, upgrade.UT);
        }

        /// <summary>The lane's reach: past the covering repair the block lifts, and the
        /// census sibling (dish intact, no repair row) never blocks - the negative
        /// control's state.</summary>
        [Fact]
        public void Index_BlockLiftsAfterTheRepair_AndTheCensusSiblingNeverBlocks()
        {
            var index = Index(StockScreenRepairFixture.FixtureName);
            var buildings = TrackingStationBuildings(StockScreenRepairFixture.FixtureName);
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(index, buildings,
                StockScreenRepairFixture.RepairUT + 1));

            var censusBuildings = TrackingStationBuildings(StockScreenCensusFixture.FixtureName);
            Assert.DoesNotContain(censusBuildings, b => b.IsDestroyed);
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(
                Index(StockScreenCensusFixture.FixtureName), censusBuildings, Now));
            // Even with the dish down, the census ledger carries no repair to cover it.
            Assert.False(StockUiReservationPredicates.IsFacilityRepairBlocked(
                Index(StockScreenCensusFixture.FixtureName), buildings, Now));
        }

        [Fact]
        public void Ledger_ReconcileAtColdLoadKeepsBothRows()
        {
            global::Parsek.Ledger.ResetForTesting();
            try
            {
                foreach (var row in Ledger(StockScreenRepairFixture.FixtureName))
                    global::Parsek.Ledger.AddAction(row);
                int before = global::Parsek.Ledger.Actions.Count;
                global::Parsek.Ledger.Reconcile(RecordingIds(StockScreenRepairFixture.FixtureName), 0.0,
                    preserveFutureTimelineActions: true);
                Assert.Equal(before, global::Parsek.Ledger.Actions.Count);
                Assert.Contains(global::Parsek.Ledger.Actions, a => a.Type == GameActionType.FacilityDestruction);
                Assert.Contains(global::Parsek.Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);
            }
            finally
            {
                global::Parsek.Ledger.ResetForTesting();
            }
        }
    }
}
