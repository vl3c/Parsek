using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The <c>stock-screen-census-strategy</c> fixture (<see cref="StockScreenStrategyFixture"/>):
    /// the committed tree equals a fresh build, and the committed save and ledger, read the
    /// way the game reads them, leave the Administration slot free while the committed
    /// timeline still activates <see cref="StockScreenCensusFixture.ActivatedLaterStrategy"/>
    /// later, so the activation is refused by Parsek's future-activation block and by nothing
    /// stock checks. A KB-5 flight that lets the press through is then a product finding and
    /// not a fixture that failed to stage the state.
    /// </summary>
    [Collection("Sequential")]
    public class StockScreenStrategyFixtureTests : IDisposable
    {
        private const double Now = StockScreenCensusFixture.SaveUT;

        public StockScreenStrategyFixtureTests()
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
        public void WriteStrategyFixture()
        {
            string target = StockScreenStrategyFixture.TargetDir(RepoRoot());
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            foreach (var kv in StockScreenStrategyFixture.BuildFiles(RepoRoot()))
            {
                string path = Path.Combine(target, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, kv.Value);
            }
        }

        [Fact]
        public void Build_IsDeterministic()
        {
            var a = StockScreenStrategyFixture.BuildFiles(RepoRoot());
            var b = StockScreenStrategyFixture.BuildFiles(RepoRoot());
            Assert.Equal(a.Keys.ToList(), b.Keys.ToList());
            foreach (var key in a.Keys)
                Assert.True(a[key].SequenceEqual(b[key]), key + " differs between two builds");
        }

        /// <summary>Compared after CRLF -> LF normalization on both sides, as the census
        /// siblings are: the flight's sidecars come from the running platform's writer.</summary>
        [Fact]
        public void CommittedFixture_MatchesTheBuilder()
        {
            string target = StockScreenStrategyFixture.TargetDir(RepoRoot());
            Assert.True(Directory.Exists(target), "fixture missing: " + target
                + " (write it with PARSEK_WRITE_STOCK_SCREEN_FIXTURE=1 dotnet test --filter WriteStrategyFixture)");
            var expected = StockScreenStrategyFixture.BuildFiles(RepoRoot());
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

        private static string Date(double ut) => "UT" + ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        [Fact]
        public void Save_NoActiveStrategy_BaseFunds_EverythingElseIsTheCensus()
        {
            ConfigNode game = Game(StockScreenStrategyFixture.FixtureName);
            Assert.Equal(StockScreenStrategyFixture.Title, game.GetValue("Title"));
            Assert.Equal(StockScreenCensusFixture.SaveUT,
                double.Parse(game.GetNode("FLIGHTSTATE").GetValue("UT"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Empty(game.GetNode("FLIGHTSTATE").GetNodes("VESSEL"));
            Assert.Empty(Scenario(game, "StrategySystem").GetNode("STRATEGIES").GetNodes("STRATEGY"));
            Assert.Equal(StockScreenCensusFixture.BaseFunds, double.Parse(Scenario(game, "Funding").GetValue("funds"),
                System.Globalization.CultureInfo.InvariantCulture));

            // The census's rows in order, minus exactly its two active-strategy rows.
            var census = Ledger(StockScreenCensusFixture.FixtureName);
            var strategy = Ledger(StockScreenStrategyFixture.FixtureName);
            var kept = census.Where(a => a.StrategyId != StockScreenCensusFixture.ActiveStrategy).ToList();
            Assert.Equal(census.Count - 2, strategy.Count);
            Assert.Equal(kept.Select(a => a.ActionId), strategy.Select(a => a.ActionId));
            Assert.DoesNotContain(strategy, a => a.StrategyId == StockScreenCensusFixture.ActiveStrategy);
            Assert.Contains(strategy, a => a.Type == GameActionType.StrategyActivate
                && a.StrategyId == StockScreenCensusFixture.ActivatedLaterStrategy
                && a.UT == StockScreenCensusFixture.StrategyActivateUT);

            // The recordings are the census's.
            Assert.Equal(RecordingIds(StockScreenCensusFixture.FixtureName),
                RecordingIds(StockScreenStrategyFixture.FixtureName));
        }

        /// <summary>The slot is free (stock's own check passes at level 1) and the committed
        /// activation is still ahead, so Parsek's refusal is the future-activation block,
        /// with the explanation the press's dialog must carry.</summary>
        [Fact]
        public void Index_ActivationRefusedAsAFutureActivation_WithAFreeSlot()
        {
            var index = Index(StockScreenStrategyFixture.FixtureName);
            Assert.Equal(0, index.SkippedUncommittedRows);
            var decision = StrategyReservationPredicates.EvaluateActivation(index,
                StockScreenCensusFixture.ActivatedLaterStrategy, Now,
                new string[0], currentLimit: 1);
            Assert.True(decision.Blocked);
            Assert.Equal(StrategyActivationBlockKind.FutureActivation, decision.Kind);
            Assert.Equal(StockScreenCensusFixture.StrategyActivateUT, decision.Entry.UT);

            var text = StrategyReservationPredicates.ExplainActivation(decision, id => id, Date);
            Assert.StartsWith("Activated on UT160000 on your committed timeline.", text.Body);
            Assert.Contains(ReservationExplanation.TimelineRule, text.Body);

            // Past the committed activation the block lifts.
            Assert.False(StrategyReservationPredicates.EvaluateActivation(index,
                StockScreenCensusFixture.ActivatedLaterStrategy, StockScreenCensusFixture.StrategyActivateUT + 1,
                new string[0], currentLimit: 1).Blocked);

            // Any other strategy activated now would stay active through the committed
            // activation and take its slot: KB-5's second press.
            var slot = StrategyReservationPredicates.EvaluateActivation(index,
                StockScreenCensusFixture.ActiveStrategy, Now, new string[0], currentLimit: 1);
            Assert.True(slot.Blocked);
            Assert.Equal(StrategyActivationBlockKind.SlotNeeded, slot.Kind);
            Assert.Equal(StockScreenCensusFixture.StrategyActivateUT, slot.Entry.UT);
            Assert.StartsWith("A committed activation of 'Outsourced R&D' on UT160000 needs this slot.",
                StrategyReservationPredicates.ExplainActivation(slot, id => "Outsourced R&D", Date).Body);

            // No strategy is active, so no deactivation block exists on this fixture.
            Assert.False(StrategyReservationPredicates.IsDeactivationBlocked(index,
                StockScreenCensusFixture.ActiveStrategy, Now));
        }

        [Fact]
        public void Ledger_ReconcileAtColdLoadKeepsTheCommittedActivation()
        {
            global::Parsek.Ledger.ResetForTesting();
            try
            {
                foreach (var row in Ledger(StockScreenStrategyFixture.FixtureName))
                    global::Parsek.Ledger.AddAction(row);
                int before = global::Parsek.Ledger.Actions.Count;
                global::Parsek.Ledger.Reconcile(RecordingIds(StockScreenStrategyFixture.FixtureName), 0.0,
                    preserveFutureTimelineActions: true);
                Assert.Equal(before, global::Parsek.Ledger.Actions.Count);
                Assert.Contains(global::Parsek.Ledger.Actions, a => a.Type == GameActionType.StrategyActivate
                    && a.StrategyId == StockScreenCensusFixture.ActivatedLaterStrategy);
            }
            finally
            {
                global::Parsek.Ledger.ResetForTesting();
            }
        }
    }
}
