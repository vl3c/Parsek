using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The <c>stock-screen-census</c> fixture (<see cref="StockScreenCensusFixture"/>): the
    /// committed tree equals a fresh build, and the committed ledger, read the way the game
    /// reads it, gives every stock screen the census photographs the mark or block it is
    /// staged for. Headless proof that a census capture without its annotation is an
    /// overlay finding and not a fixture that failed to reserve anything.
    /// </summary>
    [Collection("Sequential")]
    public class StockScreenCensusFixtureTests : IDisposable
    {
        private const double Now = StockScreenCensusFixture.SaveUT;

        public StockScreenCensusFixtureTests()
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
        internal sealed class WriteFixtureFactAttribute : FactAttribute
        {
            public WriteFixtureFactAttribute()
            {
                if (Environment.GetEnvironmentVariable("PARSEK_WRITE_STOCK_SCREEN_FIXTURE") != "1")
                    Skip = "set PARSEK_WRITE_STOCK_SCREEN_FIXTURE=1 to rewrite harness/fixtures/saves/"
                           + StockScreenCensusFixture.FixtureName;
            }
        }

        [Trait("Category", "Manual")]
        [WriteFixtureFact]
        public void WriteFixture()
        {
            string target = StockScreenCensusFixture.TargetDir(RepoRoot());
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            foreach (var kv in StockScreenCensusFixture.BuildFiles(RepoRoot()))
            {
                string path = Path.Combine(target, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, kv.Value);
            }
        }

        [Fact]
        public void Build_IsDeterministic()
        {
            var a = StockScreenCensusFixture.BuildFiles(RepoRoot());
            var b = StockScreenCensusFixture.BuildFiles(RepoRoot());
            Assert.Equal(a.Keys.ToList(), b.Keys.ToList());
            foreach (var key in a.Keys)
                Assert.True(a[key].SequenceEqual(b[key]), key + " differs between two builds");
        }

        /// <summary>
        /// The committed fixture is exactly what the builder produces from the committed base.
        /// Compared after CRLF -> LF normalization on BOTH sides, because the flight's
        /// sidecars are written by the running platform's own writer and CI builds on Linux.
        /// </summary>
        [Fact]
        public void CommittedFixture_MatchesTheBuilder()
        {
            string target = StockScreenCensusFixture.TargetDir(RepoRoot());
            Assert.True(Directory.Exists(target), "fixture missing: " + target
                + " (write it with PARSEK_WRITE_STOCK_SCREEN_FIXTURE=1 dotnet test --filter WriteFixture)");
            var expected = StockScreenCensusFixture.BuildFiles(RepoRoot());
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

        // ------------------------------------------------------------------ the save

        private static ConfigNode LoadCommitted(string rel)
        {
            string path = Path.Combine(StockScreenCensusFixture.TargetDir(RepoRoot()), rel);
            Assert.True(File.Exists(path), "missing " + path);
            ConfigNode node = ConfigNode.Load(path);
            Assert.NotNull(node);
            return node;
        }

        private static ConfigNode Game() => LoadCommitted("persistent.sfs").GetNode("GAME");

        private static ConfigNode Scenario(ConfigNode game, string name)
        {
            foreach (ConfigNode s in game.GetNodes("SCENARIO"))
                if (s.GetValue("name") == name) return s;
            throw new Xunit.Sdk.XunitException("SCENARIO " + name + " missing");
            return null;
        }

        [Fact]
        public void Save_CarriesTheStockStateTheAnnotationsNeed()
        {
            ConfigNode game = Game();
            Assert.Equal("CAREER", game.GetValue("Mode"));
            Assert.Equal(StockScreenCensusFixture.Title, game.GetValue("Title"));
            Assert.Equal("False", game.GetNode("PARAMETERS").GetNode("DIFFICULTY")
                .GetValue("BypassEntryPurchaseAfterResearch"));
            Assert.Equal(StockScreenCensusFixture.SaveUT,
                double.Parse(game.GetNode("FLIGHTSTATE").GetValue("UT"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Empty(game.GetNode("FLIGHTSTATE").GetNodes("VESSEL"));

            ConfigNode start = Scenario(game, "ResearchAndDevelopment").GetNodes("Tech")
                .Single(t => t.GetValue("id") == "start");
            Assert.DoesNotContain(StockScreenCensusFixture.PurchasedLaterPart, start.GetValues("part"));
            Assert.Contains("mk1pod.v2", start.GetValues("part"));

            Assert.Equal((StockScreenCensusFixture.BaseFunds - StockScreenCensusFixture.ActiveStrategySetupCost)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Scenario(game, "Funding").GetValue("funds"));

            ConfigNode[] strategies = Scenario(game, "StrategySystem").GetNode("STRATEGIES").GetNodes("STRATEGY");
            Assert.Single(strategies);
            Assert.Equal(StockScreenCensusFixture.ActiveStrategy, strategies[0].GetValue("name"));

            var contracts = Scenario(game, "ContractSystem").GetNode("CONTRACTS").GetNodes("CONTRACT");
            var active = contracts.Where(c => c.GetValue("state") == "Active").ToList();
            Assert.Single(active);
            Assert.Equal(StockScreenCensusFixture.ActiveContractGuid, active[0].GetValue("guid"));
            Assert.Equal("Offered", contracts.Single(c => c.GetValue("guid") ==
                StockScreenCensusFixture.AcceptedLaterContractGuid).GetValue("state"));
            Assert.Equal("Offered", contracts.Single(c => c.GetValue("guid") ==
                StockScreenCensusFixture.SlotBlockedContractGuid).GetValue("state"));

            var roster = game.GetNode("ROSTER").GetNodes("KERBAL");
            Assert.Equal("Applicant", roster.Single(k => k.GetValue("name") ==
                StockScreenCensusFixture.HiredLaterApplicant).GetValue("type"));
            ConfigNode reserved = roster.Single(k => k.GetValue("name") == StockScreenCensusFixture.ReservedKerbal);
            Assert.Equal("Crew", reserved.GetValue("type"));
            Assert.Equal("Available", reserved.GetValue("state"));

            // Mission Control and Administration at level 1: two contract slots, one strategy.
            ConfigNode facilities = Scenario(game, "ScenarioUpgradeableFacilities");
            Assert.Equal("0", facilities.GetNode("SpaceCenter/MissionControl").GetValue("lvl"));
            Assert.Equal("0", facilities.GetNode("SpaceCenter/TrackingStation").GetValue("lvl"));
        }

        [Fact]
        public void Save_CarriesTheCommittedFlightTreeAndItsSidecars()
        {
            ConfigNode parsek = Scenario(Game(), "ParsekScenario");
            ConfigNode treeNode = parsek.GetNodes("RECORDING_TREE")
                .Single(t => t.GetValue("rootRecordingId") == StockScreenCensusFixture.FlightRecordingId);
            RecordingTree tree = RecordingTree.Load(treeNode);
            Recording rec = tree.Recordings[StockScreenCensusFixture.FlightRecordingId];
            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.Equal(StockScreenCensusFixture.FlightName, rec.VesselName);
            Assert.True(rec.StartUT > Now, "the committed flight must lie ahead of the save clock");
            Assert.Equal(StockScreenCensusFixture.FlightEndUT, rec.EndUT);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[StockScreenCensusFixture.ReservedKerbal]);
            Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration, rec.RecordingSchemaGeneration);

            string recDir = Path.Combine(StockScreenCensusFixture.TargetDir(RepoRoot()), "Parsek", "Recordings");
            var loaded = new Recording { RecordingId = StockScreenCensusFixture.FlightRecordingId };
            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loaded,
                Path.Combine(recDir, StockScreenCensusFixture.FlightRecordingId + ".prec"),
                Path.Combine(recDir, StockScreenCensusFixture.FlightRecordingId + "_vessel.craft"),
                Path.Combine(recDir, StockScreenCensusFixture.FlightRecordingId + "_ghost.craft")));
            Assert.Equal(5, loaded.Points.Count);
            Assert.NotNull(loaded.VesselSnapshot);
        }

        /// <summary>
        /// The kerbal assignment row equals what the load-time migration would derive for the
        /// flight, so the first load keeps it rather than re-deriving it.
        /// </summary>
        [Fact]
        public void Ledger_KerbalAssignmentRowIsTheOneTheMigrationDerives()
        {
            GameAction row = StockScreenCensusFixture.BuildLedgerRows()
                .Single(a => a.Type == GameActionType.KerbalAssignment);
            Recording rec = StockScreenCensusFixture.BuildFlightTree().Recordings[StockScreenCensusFixture.FlightRecordingId];
            Assert.Equal(rec.StartUT, row.UT);
            Assert.Equal((float)rec.StartUT, row.StartUT);
            Assert.Equal((float)rec.EndUT, row.EndUT);
            Assert.Equal(1, row.Sequence);
            Assert.Equal(KerbalEndState.Recovered, row.KerbalEndStateField);
        }

        // ------------------------------------------------------------------ the index

        private static List<GameAction> CommittedLedger()
        {
            ConfigNode ledger = LoadCommitted("Parsek/GameState/ledger.pgld");
            return ledger.GetNodes("GAME_ACTION").Select(GameAction.DeserializeFrom).ToList();
        }

        private static HashSet<string> CommittedRecordingIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigNode tree in Scenario(Game(), "ParsekScenario").GetNodes("RECORDING_TREE"))
                foreach (ConfigNode rec in tree.GetNodes("RECORDING"))
                    ids.Add(rec.GetValue("recordingId"));
            return ids;
        }

        private static CommittedFutureIndex BuildIndex()
        {
            var ids = CommittedRecordingIds();
            return CommittedFutureIndex.Build(
                CommittedLedger(),
                id => ids.Contains(id),
                id => id == StockScreenCensusFixture.FlightRecordingId ? StockScreenCensusFixture.FlightName : null,
                null);
        }

        private static string Date(double ut) => "UT" + ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        [Fact]
        public void Index_EveryRowIsCommittedAndEveryFutureRowIsAhead()
        {
            var index = BuildIndex();
            Assert.Equal(0, index.SkippedUncommittedRows);
            foreach (var row in CommittedLedger())
                if (!string.IsNullOrEmpty(row.RecordingId))
                    Assert.Contains(row.RecordingId, CommittedRecordingIds());
            Assert.True(StockScreenCensusFixture.TechResearchUT > Now);
            Assert.True(CommittedFutureIndex.IsFuture(StockScreenCensusFixture.TechResearchUT, Now + 3600),
                "an hour of lane at 1x must not reach the earliest committed row");
        }

        [Fact]
        public void Index_RnD_MarksTheResearchedLaterNode()
        {
            var index = BuildIndex();
            var d = StockUiDecorationQuery.ForRnD(index, Now,
                new[] { StockScreenCensusFixture.ResearchedLaterTech, "engineering101", "start" }, Date);
            Assert.Equal(1, d.Count(x => x.Marked && x.Blocked));
            Assert.Equal(StockUiDecorationKind.TechResearch,
                d.Single(x => x.Id == StockScreenCensusFixture.ResearchedLaterTech).Kind);
        }

        [Fact]
        public void Index_MissionControl_AcceptMark_SlotBlock_AndActiveResolution()
        {
            var index = BuildIndex();
            var slots = ContractSlotReservation.Forecast(index,
                new[]
                {
                    new ContractSlotHolder(StockScreenCensusFixture.ActiveContractGuid,
                        StockScreenCensusFixture.ActiveContractAcceptUT,
                        StockScreenCensusFixture.ActiveContractDeadlineUT)
                },
                limitNow: 2, currentUT: Now, slotsForLevel: level => level <= 1 ? 2 : 7);
            Assert.Equal(1, slots.ActiveNow);
            Assert.Equal(2, slots.PeakCommitted);
            Assert.Equal(0, slots.FreeSlotsForNewAcceptNow);
            Assert.True(slots.BlocksNewAcceptNow);

            var rows = new[]
            {
                new StockUiItem(StockScreenCensusFixture.AcceptedLaterContractGuid, StockUiDecorationQuery.MissionControlAvailableTab),
                new StockUiItem(StockScreenCensusFixture.SlotBlockedContractGuid, StockUiDecorationQuery.MissionControlAvailableTab),
                new StockUiItem(StockScreenCensusFixture.ActiveContractGuid, StockUiDecorationQuery.MissionControlActiveTab),
            };
            var d = StockUiDecorationQuery.ForMissionControl(index, Now, rows, Date, slots,
                key => Now + 9201600);
            Assert.Equal(StockUiDecorationKind.ContractAccept, d[0].Kind);
            Assert.True(d[0].Marked && d[0].Blocked);
            Assert.Equal(StockUiDecorationKind.ContractSlot, d[1].Kind);
            Assert.True(!d[1].Marked && d[1].Blocked);
            Assert.Equal(StockUiDecorationKind.ContractResolution, d[2].Kind);
            Assert.True(d[2].Marked && d[2].Blocked);
            Assert.StartsWith("Completes on ", d[2].Why);
            Assert.Contains(StockScreenCensusFixture.FlightName, d[2].Why);
        }

        [Fact]
        public void Index_AstronautComplex_FutureHireAndHeldKerbal()
        {
            var index = BuildIndex();
            Assert.True(StockUiReservationPredicates.IsKerbalHireBlocked(index,
                StockScreenCensusFixture.HiredLaterApplicant, Now));
            var holds = index.AssignmentsOf(StockScreenCensusFixture.ReservedKerbal);
            Assert.Single(holds);
            Assert.Equal(KerbalEndState.Recovered, holds[0].EndState);
            Assert.Equal(StockScreenCensusFixture.FlightName, holds[0].RecordingName);
            Assert.True(holds[0].EndUT > Now, "the Recovered hold runs to the flight's end, after now");
        }

        [Fact]
        public void Index_FacilityMenu_TrackingStationUpgradeBlocked()
        {
            var index = BuildIndex();
            var d = StockUiDecorationQuery.ForFacilityMenu(index, Now,
                StockScreenCensusFixture.UpgradedLaterFacility, false, Date);
            Assert.True(d.Marked && d.Blocked);
            Assert.Equal(StockUiDecorationKind.FacilityUpgrade, d.Kind);
            Assert.False(StockUiDecorationQuery.ForFacilityMenu(index, Now,
                "SpaceCenter/VehicleAssemblyBuilding", false, Date).Blocked);
        }

        [Fact]
        public void Index_PartPurchase_BlockedWithBypassOffOnly()
        {
            var index = BuildIndex();
            Assert.True(StockUiReservationPredicates.IsPartPurchaseBlocked(index,
                StockScreenCensusFixture.PurchasedLaterPart, Now, bypassEntryPurchase: false, purchasedInStock: false));
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index,
                StockScreenCensusFixture.PurchasedLaterPart, Now, bypassEntryPurchase: true, purchasedInStock: false));
        }

        [Fact]
        public void Index_Administration_ActivationAndDeactivationBlocked()
        {
            var index = BuildIndex();
            var activation = StrategyReservationPredicates.EvaluateActivation(index,
                StockScreenCensusFixture.ActivatedLaterStrategy, Now,
                new[] { StockScreenCensusFixture.ActiveStrategy }, currentLimit: 1);
            Assert.True(activation.Blocked);
            var next = StrategyReservationPredicates.FirstFutureRow(index, StockScreenCensusFixture.ActiveStrategy, Now);
            Assert.NotNull(next);
            Assert.Equal(CommittedFutureKind.StrategyDeactivate, next.Kind);
            Assert.True(StrategyReservationPredicates.IsDeactivationBlocked(index, StockScreenCensusFixture.ActiveStrategy, Now));
        }

        [Fact]
        public void Ledger_ReconcileAtColdLoadKeepsEveryFutureRow()
        {
            // A cold SPACECENTER load reconciles with preserveFutureTimelineActions=true
            // (the scene is cutoff-supported and the clock is not ready yet), and a row of a
            // loaded recording is valid; nothing this fixture adds may be pruned there.
            Ledger.ResetForTesting();
            try
            {
                foreach (var row in CommittedLedger())
                    Ledger.AddAction(row);
                int before = Ledger.Actions.Count;
                Ledger.Reconcile(CommittedRecordingIds(), 0.0, preserveFutureTimelineActions: true);
                Assert.Equal(before, Ledger.Actions.Count);
            }
            finally
            {
                Ledger.ResetForTesting();
            }
        }
    }
}
