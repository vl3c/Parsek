using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-DISCARD-AFTER-REWIND-LOSES-COMMITTED-FUTURE (lane RW-5, run 2026-10-28_1056): a
    /// Discard Re-fly after a Rewind-to-Launch lost the committed future (+2932 recovery, +37500
    /// contract advance) for good. The Re-Fly start walks the whole ledger (cutoff
    /// <c>double.MaxValue</c>), lifting the committed future into the live pool, and the flight
    /// start's persistent.sfs write saves that pool's stock pool reference. The Discard Re-fly
    /// writes the rewind point's game (its pools) to persistent.sfs around the session's PARSEK
    /// node, so the reference no longer described the pools of its own file, and the first
    /// measured patch read the lifted future as a loss: <c>PatchFunds: 150632 -> 110200
    /// (delta=-40432)</c>, filed as an adjustment row the later crossing could not undo.
    ///
    /// <para>Each cell drives the real walk (<see cref="RecalculationEngine.Recalculate"/>), the
    /// real stock pool reference (<see cref="StockPoolReference.Measure"/> / <c>Commit</c>, its
    /// <c>SaveTo</c> / <c>LoadFrom</c> through the ConfigNodes the saves carry) and the real
    /// persistent.sfs splice (<see cref="RevertInterceptor.SpliceLiveParsekScenarioNode"/>), the
    /// way <c>KspStatePatcher.PatchFunds</c> / <c>PatchScience</c> compose them.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyDiscardPoolReferenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<GameAction> ledger = new List<GameAction>();
        private readonly FundsModule funds = new FundsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly ReputationModule reputation = new ReputationModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private StockPoolMeasurement lastMeasurement;

        // RW-5's shape: the pool at the rewind point, the committed future, and the clocks.
        private const double PoolAtRewindPoint = 150632.0;
        private const float HRecovery = 2932f;
        private const float FAdvance = 37500f;
        private const double CommittedFuture = 40432.0;
        private const double LaunchUT = 100.0;
        private const double RewindPointUT = 150.0;
        private const double HRecoveryUT = 200.0;
        private const double FAcceptUT = 250.0;
        private const double CrossingUT = 400.0;

        public ReFlyDiscardPoolReferenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Helpers (the patcher's composition, as in StockPoolReferenceTests)
        // ------------------------------------------------------------------

        private HashSet<string> IdsInLedger() => new HashSet<string>(
            ledger.Where(a => a != null && !string.IsNullOrEmpty(a.ActionId)).Select(a => a.ActionId),
            StringComparer.Ordinal);

        private void File(StockPoolMeasurement m, StockPool pool, double now)
        {
            if (m.Adjustment == 0.0)
                return;
            GameAction row = StockPoolReference.BuildAdjustmentRow(pool, m.Adjustment, now);
            ledger.Add(row);
            m.AdjustmentRow = row;
        }

        private double PatchFunds(double now, double live, bool authoritative = false)
        {
            RecalculationEngine.Recalculate(ledger.ToList(), now);
            var state = StockPoolReference.Funds;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, ledger, funds.GetRowEffects());
            double min = funds.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.01, min, IdsInLedger(), clampAtZero: true);
            File(m, StockPool.Funds, now);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(min, funds.GetRunningBalance(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private double PatchScience(double now, double live, bool authoritative = false)
        {
            RecalculationEngine.Recalculate(ledger.ToList(), now);
            var state = StockPoolReference.Science;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Science, ledger, science.GetRowEffects());
            double min = science.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.001, min, IdsInLedger(), clampAtZero: true);
            File(m, StockPool.Science, now);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(min, science.GetRunningScience(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private double Adjusted(StockPool pool) => StockPoolReference.SumAdjustments(pool, ledger);

        /// <summary>A ParsekScenario node as a live OnSave writes it: one tree plus the reference.</summary>
        private static ConfigNode SaveParsekNode(string treeId)
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", "ParsekScenario");
            node.AddNode("RECORDING_TREE").AddValue("id", treeId);
            StockPoolReference.SaveTo(node);
            return node;
        }

        /// <summary>
        /// The Discard Re-fly persistent.sfs write: the rewind point game's own PARSEK node with
        /// the session's last live-saved one spliced in, then the scene start's OnLoad installing
        /// the reference from that file.
        /// </summary>
        private ConfigNode DiscardReFlyWriteAndLoad(ConfigNode rewindPointNode, ConfigNode persistentNode)
        {
            ConfigNode written = rewindPointNode.CreateCopy();
            Assert.Null(RevertInterceptor.SpliceLiveParsekScenarioNode(written, persistentNode));
            StockPoolReference.LoadFrom(written, "DiscardReFly");
            return written;
        }

        /// <summary>
        /// RW-5 up to the Re-Fly attempt: a committed pad hop H (recovery) and a Space Center
        /// contract accept F, a Rewind-to-Launch that leaves both ahead of the clock, flight G
        /// authoring a rewind point, and the Re-Fly start's full-ledger walk. Returns the rewind
        /// point's PARSEK node and the one the flight start wrote to persistent.sfs.
        /// </summary>
        private void FlyUpToTheReFlyAttempt(
            out ConfigNode rewindPointNode, out ConfigNode persistentNode, bool rewindFirst = true)
        {
            ledger.Add(new GameAction
            {
                ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)PoolAtRewindPoint,
            });
            Assert.Equal(PoolAtRewindPoint, PatchFunds(10, PoolAtRewindPoint), 6);

            // The launch save the Rewind-to-Launch goes back to.
            Assert.Equal(PoolAtRewindPoint, PatchFunds(LaunchUT, PoolAtRewindPoint), 6);
            ConfigNode launchSave = SaveParsekNode("tree-H");

            // H recovers (+2932) and F is accepted (+37500): stock applied both.
            ledger.Add(new GameAction
            {
                ActionId = "H-recovery", UT = HRecoveryUT, Type = GameActionType.FundsEarning,
                FundsAwarded = HRecovery,
            });
            ledger.Add(new GameAction
            {
                ActionId = "F-accept", UT = FAcceptUT, Type = GameActionType.ContractAccept,
                ContractId = "F", AdvanceFunds = FAdvance,
            });
            double afterBoth = PoolAtRewindPoint + CommittedFuture;
            Assert.Equal(afterBoth, PatchFunds(FAcceptUT + 10, afterBoth), 6);
            Assert.False(lastMeasurement.LeakKept);

            double reFlyStartClock = FAcceptUT + 20;
            if (rewindFirst)
            {
                // Rewind-to-Launch: the launch save's pool and reference, an authoritative walk at
                // its clock. H and F are committed future now.
                StockPoolReference.LoadFrom(launchSave, "PlainRewind");
                Assert.Equal(PoolAtRewindPoint, PatchFunds(LaunchUT, PoolAtRewindPoint, authoritative: true), 6);
                reFlyStartClock = RewindPointUT + 5;
            }

            // Flight G decouples: the rewind point quicksave (a live save in flight).
            double rpPool = rewindFirst ? PoolAtRewindPoint : afterBoth;
            rewindPointNode = SaveParsekNode("tree-at-rewind-point");

            // Re-Fly start: the rewind point quicksave loads (its pool and reference), and the
            // invoke walk runs with cutoff double.MaxValue (RewindInvoker), lifting every
            // committed row into the pool.
            StockPoolReference.LoadFrom(rewindPointNode, "ReFlyStart");
            double reFlyPool = PatchFunds(double.MaxValue, rpPool, authoritative: true);
            Assert.Equal(PoolAtRewindPoint + CommittedFuture, reFlyPool, 6);

            // FlightDriver.PostInit rewrites persistent.sfs with the session's live save.
            persistentNode = SaveParsekNode("tree-at-rewind-point");
            persistentNode.AddNode("RECORDING_TREE").AddValue("id", "tree-committed-later");
            Assert.Equal(PoolAtRewindPoint + CommittedFuture,
                double.Parse(persistentNode.GetNode(StockPoolReference.NodeName)
                    .GetNode(StockPoolReference.FundsNodeName).GetValue(StockPoolReference.LastWrittenKey),
                    System.Globalization.CultureInfo.InvariantCulture), 6);
            Assert.True(reFlyStartClock > 0);
        }

        // ------------------------------------------------------------------
        // The headline: RW-5's sequence
        // ------------------------------------------------------------------

        // The first patch after the Discard Re-fly load measured (the in-session load did, and a
        // cold load of the written file or a pool's late first patch still does) against the
        // reference of the file it loaded: the rewind point's own, after the fix.
        [Fact]
        public void DiscardReFlyAfterRewind_MeasuredFirstPatch_KeepsTheCommittedFuture()
        {
            FlyUpToTheReFlyAttempt(out ConfigNode rewindPointNode, out ConfigNode persistentNode);

            // Discard Re-fly: the rewind point's game (stock funds 150632) written around the
            // session's PARSEK node; the tree committed later still rides along.
            ConfigNode written = DiscardReFlyWriteAndLoad(rewindPointNode, persistentNode);
            Assert.Equal(2, written.GetNodes("RECORDING_TREE").Length);

            // RW-5 before the fix: write=110200, adjustment=-40432.
            double write = PatchFunds(RewindPointUT, PoolAtRewindPoint);
            Assert.Equal(PoolAtRewindPoint, write, 6);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);
            Assert.False(lastMeasurement.LeakKept, "leak=" + lastMeasurement.Leak);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);

            // The clock reaches H and F: the crossing pays them once, and the career ends where
            // it stood before the Re-Fly plus the committed future (not 150632).
            double crossed = PatchFunds(CrossingUT, write);
            Assert.Equal(PoolAtRewindPoint + CommittedFuture, crossed, 6);
            Assert.False(lastMeasurement.LeakKept);
        }

        // The Discard Re-fly load's first patch walks as a Re-Fly discard (authoritative), as the
        // merge dialog's discard does: also right, and it re-baselines the reference.
        [Fact]
        public void DiscardReFlyAfterRewind_AuthoritativeFirstPatch_KeepsTheCommittedFuture()
        {
            FlyUpToTheReFlyAttempt(out ConfigNode rewindPointNode, out ConfigNode persistentNode);
            DiscardReFlyWriteAndLoad(rewindPointNode, persistentNode);

            double write = PatchFunds(RewindPointUT, PoolAtRewindPoint, authoritative: true);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);
            Assert.Equal(PoolAtRewindPoint, write, 6);
            Assert.Equal(PoolAtRewindPoint + CommittedFuture, PatchFunds(CrossingUT, write), 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
        }

        // The defect's mechanism, kept as a cell: the session's reference (written after the
        // Re-Fly start's full-ledger walk) measured against the rewind point's pool reads the
        // lifted committed future as a 40432 loss, exactly RW-5's PatchFunds line.
        [Fact]
        public void SessionReference_AgainstTheRewindPointPool_ReadsTheCommittedFutureAsALoss()
        {
            FlyUpToTheReFlyAttempt(out ConfigNode _, out ConfigNode persistentNode);

            StockPoolReference.LoadFrom(persistentNode, "DiscardReFly");
            Assert.Equal(PoolAtRewindPoint + CommittedFuture, StockPoolReference.Funds.LastWritten, 6);
            double write = PatchFunds(RewindPointUT, PoolAtRewindPoint);

            Assert.True(lastMeasurement.LeakKept);
            Assert.Equal(-CommittedFuture, lastMeasurement.Adjustment, 6);
            Assert.Equal(PoolAtRewindPoint - CommittedFuture, write, 6);
            Assert.Equal(PoolAtRewindPoint, PatchFunds(CrossingUT, write), 6);
        }

        [Fact]
        public void DiscardReFlyAfterRewind_SciencePool_KeepsTheCommittedFuture()
        {
            ledger.Add(new GameAction { ActionId = "seedS", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 100f });
            Assert.Equal(100.0, PatchScience(LaunchUT, 100), 6);
            ConfigNode launchSave = SaveParsekNode("tree-H");
            ledger.Add(new GameAction
            {
                ActionId = "H-science", UT = HRecoveryUT, Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLandedLaunchPad", ScienceAwarded = 25f, SubjectMaxValue = 50f,
            });
            Assert.Equal(125.0, PatchScience(HRecoveryUT + 10, 125), 6);

            StockPoolReference.LoadFrom(launchSave, "PlainRewind");
            Assert.Equal(100.0, PatchScience(LaunchUT, 100, authoritative: true), 6);
            ConfigNode rewindPointNode = SaveParsekNode("tree-at-rewind-point");

            StockPoolReference.LoadFrom(rewindPointNode, "ReFlyStart");
            Assert.Equal(125.0, PatchScience(double.MaxValue, 100, authoritative: true), 6);
            ConfigNode persistentNode = SaveParsekNode("tree-at-rewind-point");

            DiscardReFlyWriteAndLoad(rewindPointNode, persistentNode);
            double write = PatchScience(RewindPointUT, 100);
            Assert.False(lastMeasurement.LeakKept, "leak=" + lastMeasurement.Leak);
            Assert.Equal(100.0, write, 6);
            Assert.Equal(125.0, PatchScience(CrossingUT, write), 6);
            Assert.Equal(0.0, Adjusted(StockPool.Science), 6);
        }

        // ------------------------------------------------------------------
        // (b) a Discard Re-fly with no committed future is unchanged
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DiscardReFlyWithoutCommittedFuture_SessionOrRewindPointReference_SameOutcome(bool authoritative)
        {
            ledger.Add(new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 5000f });
            Assert.Equal(5000.0, PatchFunds(10, 5000), 6);
            ledger.Add(new GameAction { ActionId = "rollout", UT = 100, Type = GameActionType.FundsSpending, FundsSpent = 800f });
            Assert.Equal(4200.0, PatchFunds(110, 4200), 6);
            ConfigNode rewindPointNode = SaveParsekNode("tree-at-rewind-point");

            StockPoolReference.LoadFrom(rewindPointNode, "ReFlyStart");
            Assert.Equal(4200.0, PatchFunds(double.MaxValue, 4200, authoritative: true), 6);
            ConfigNode persistentNode = SaveParsekNode("tree-at-rewind-point");

            // Before the fix: the session's reference.
            StockPoolReference.LoadFrom(persistentNode, "DiscardReFly");
            double before = PatchFunds(RewindPointUT, 4200, authoritative);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);

            // After the fix: the rewind point's own reference.
            DiscardReFlyWriteAndLoad(rewindPointNode, persistentNode);
            double after = PatchFunds(RewindPointUT, 4200, authoritative);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);

            Assert.Equal(4200.0, before, 6);
            Assert.Equal(before, after, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
        }

        // The loss needed no rewind: any committed row past the rewind point (here the flight's
        // own other stage recovered after the separation) is lifted by the Re-Fly start walk.
        [Fact]
        public void DiscardReFlyWithoutRewind_ARowPastTheRewindPoint_IsKept()
        {
            ledger.Add(new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 5000f });
            Assert.Equal(5000.0, PatchFunds(RewindPointUT, 5000), 6);
            ConfigNode rewindPointNode = SaveParsekNode("tree-at-rewind-point");

            // G merges: its booster recovered after the split (+1200, stock applied).
            ledger.Add(new GameAction
            {
                ActionId = "booster-recovery", UT = RewindPointUT + 100, Type = GameActionType.FundsEarning,
                FundsAwarded = 1200f,
            });
            Assert.Equal(6200.0, PatchFunds(RewindPointUT + 110, 6200), 6);

            StockPoolReference.LoadFrom(rewindPointNode, "ReFlyStart");
            Assert.Equal(6200.0, PatchFunds(double.MaxValue, 5000, authoritative: true), 6);
            ConfigNode persistentNode = SaveParsekNode("tree-at-rewind-point");

            DiscardReFlyWriteAndLoad(rewindPointNode, persistentNode);
            double write = PatchFunds(RewindPointUT, 5000);
            Assert.False(lastMeasurement.LeakKept, "leak=" + lastMeasurement.Leak);
            Assert.Equal(5000.0, write, 6);
            Assert.Equal(6200.0, PatchFunds(CrossingUT, write), 6);
        }

        // ------------------------------------------------------------------
        // Why the load's first patch is authoritative too
        // ------------------------------------------------------------------

        // Stock paid something no row models in flight before the separation (+50); the
        // session's next patch after the merge filed it as an adjustment row at that later
        // clock. Against the rewind point's reference a MEASURED first patch files it again at
        // the rewind point's clock, and the crossing then pays it twice; the authoritative patch
        // leaves it to the crossing alone.
        [Theory]
        [InlineData(false, 1100.0)]
        [InlineData(true, 1050.0)]
        public void DiscardReFly_AnUntrackedGainBeforeTheSplit_IsPaidOnceOnlyByAnAuthoritativePatch(
            bool authoritative, double expectedAfterCrossing)
        {
            ledger.Add(new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f });
            Assert.Equal(1000.0, PatchFunds(LaunchUT, 1000), 6);
            // The rewind point quicksave: pool 1050, reference still at the launch patch.
            ConfigNode rewindPointNode = SaveParsekNode("tree-at-rewind-point");

            Assert.Equal(1050.0, PatchFunds(300, 1050), 6);
            Assert.True(lastMeasurement.LeakKept);
            Assert.Equal(50.0, Adjusted(StockPool.Funds), 6);

            StockPoolReference.LoadFrom(rewindPointNode, "ReFlyStart");
            Assert.Equal(1050.0, PatchFunds(double.MaxValue, 1050, authoritative: true), 6);
            ConfigNode persistentNode = SaveParsekNode("tree-at-rewind-point");

            DiscardReFlyWriteAndLoad(rewindPointNode, persistentNode);
            double write = PatchFunds(RewindPointUT, 1050, authoritative);
            Assert.Equal(expectedAfterCrossing, PatchFunds(CrossingUT, write), 6);
        }

        // ------------------------------------------------------------------
        // (a) the mirror: a Re-Fly MERGE after the same lift
        // ------------------------------------------------------------------

        // No load replaces the pools: the merge's post-marker-clear walk (journal still active,
        // so authoritative) cuts off at the current clock, takes the lifted future back out and
        // re-baselines; the crossing pays it once. The attempt's own earning is kept.
        [Fact]
        public void ReFlyMergeAfterRewind_EndsWithTheCommittedFutureAndTheAttempt()
        {
            FlyUpToTheReFlyAttempt(out ConfigNode _, out ConfigNode _);
            double lifted = PoolAtRewindPoint + CommittedFuture;

            // The attempt: stock pays a 500 world-first in flight (patches deferred); the merge
            // files its row and walks at the current clock.
            ledger.Add(new GameAction
            {
                ActionId = "attempt-world-first", UT = RewindPointUT + 10, Type = GameActionType.FundsEarning,
                FundsAwarded = 500f,
            });
            double merged = PatchFunds(RewindPointUT + 20, lifted + 500, authoritative: true);
            Assert.Equal(PoolAtRewindPoint + 500, merged, 6);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);

            Assert.Equal(PoolAtRewindPoint + CommittedFuture + 500, PatchFunds(CrossingUT, merged), 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
        }

        // ------------------------------------------------------------------
        // The splice and the wiring
        // ------------------------------------------------------------------

        [Fact]
        public void Splice_KeepsTheRewindPointsPoolReference_AndCarriesTheRest()
        {
            var target = new ConfigNode("SCENARIO");
            target.AddValue("name", "ParsekScenario");
            target.AddNode("RECORDING_TREE").AddValue("id", "tree-at-rewind-point");
            target.AddNode(StockPoolReference.NodeName).AddNode(StockPoolReference.FundsNodeName)
                .AddValue(StockPoolReference.LastWrittenKey, "150632");
            var live = new ConfigNode("SCENARIO");
            live.AddValue("name", "ParsekScenario");
            live.AddNode("RECORDING_TREE").AddValue("id", "tree-at-rewind-point");
            live.AddNode("RECORDING_TREE").AddValue("id", "tree-committed-later");
            live.AddNode(StockPoolReference.NodeName).AddNode(StockPoolReference.FundsNodeName)
                .AddValue(StockPoolReference.LastWrittenKey, "191064");

            Assert.Null(RevertInterceptor.SpliceLiveParsekScenarioNode(target, live));

            Assert.Equal(2, target.GetNodes("RECORDING_TREE").Length);
            ConfigNode[] references = target.GetNodes(StockPoolReference.NodeName);
            Assert.Single(references);
            Assert.Equal("150632", references[0].GetNode(StockPoolReference.FundsNodeName)
                .GetValue(StockPoolReference.LastWrittenKey));
            // The session's node is not changed by the splice.
            Assert.Equal("191064", live.GetNode(StockPoolReference.NodeName)
                .GetNode(StockPoolReference.FundsNodeName).GetValue(StockPoolReference.LastWrittenKey));
            Assert.Contains(logLines, l => l.Contains("[RewindSave]")
                && l.Contains("keeping the rewind point's own STOCK_POOL_REFERENCE")
                && l.Contains("rewindPoint=present session=present"));
        }

        [Fact]
        public void Splice_ARewindPointWithoutAReference_DropsTheSessionsOne()
        {
            var target = new ConfigNode("SCENARIO");
            target.AddValue("name", "ParsekScenario");
            var live = new ConfigNode("SCENARIO");
            live.AddValue("name", "ParsekScenario");
            live.AddNode(StockPoolReference.NodeName).AddNode(StockPoolReference.FundsNodeName)
                .AddValue(StockPoolReference.LastWrittenKey, "191064");

            Assert.Null(RevertInterceptor.SpliceLiveParsekScenarioNode(target, live));

            Assert.False(target.HasNode(StockPoolReference.NodeName));
            StockPoolReference.LoadFrom(target, "DiscardReFly");
            Assert.False(StockPoolReference.Funds.Initialized);
            Assert.Contains(logLines, l => l.Contains("[RewindSave]") && l.Contains("rewindPoint=absent")
                && l.Contains("each pool starts afresh at its first patch"));
        }

        [Fact]
        public void DiscardReFlyLoad_FirstWalkAndItsRepatch_WalkAsAReFlyDiscard()
        {
            string path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "Source", "Parsek", "ParsekScenario.cs");
            string src = string.Join(" ", System.IO.File.ReadAllText(path)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

            int onLoad = src.IndexOf("public override void OnLoad(ConfigNode node)", StringComparison.Ordinal);
            Assert.True(onLoad >= 0);
            int firstWalk = src.IndexOf("bool inSessionLedgerUsedCutoff = RecalculateLedgerForInSessionLoad(", onLoad,
                StringComparison.Ordinal);
            Assert.True(firstWalk > onLoad, "the in-session load's first ledger walk was not found");
            int end = src.IndexOf(";", firstWalk, StringComparison.Ordinal);
            Assert.Contains("reFlyDiscard: refinedLoadKind == LoadKind.DiscardReFly",
                src.Substring(firstWalk, end - firstWalk));

            int repatch = src.IndexOf("StartCoroutine(RepatchAfterInSessionLoadWhenCurrenciesLoaded(", firstWalk,
                StringComparison.Ordinal);
            Assert.True(repatch > firstWalk);
            int repatchEnd = src.IndexOf(";", repatch, StringComparison.Ordinal);
            Assert.Contains("refinedLoadKind == LoadKind.DiscardReFly", src.Substring(repatch, repatchEnd - repatch));

            int body = src.IndexOf("private IEnumerator RepatchAfterInSessionLoadWhenCurrenciesLoaded(", StringComparison.Ordinal);
            int bodyEnd = src.IndexOf("private IEnumerator SeedRecorderResourceBaselinesWhenReady(", body, StringComparison.Ordinal);
            string repatchBody = src.Substring(body, bodyEnd - body);
            Assert.Contains("RecalculateAndPatchForCurrentTimelineUT( cutoffUT, \"post-rewind-load\", reFlyDiscard: true)",
                repatchBody);
            Assert.Contains("LedgerOrchestrator.RecalculateAndPatch(reFlyDiscard: reFlyDiscard)", repatchBody);
        }
    }
}
