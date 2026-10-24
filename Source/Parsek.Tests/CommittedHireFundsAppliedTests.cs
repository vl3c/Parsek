using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CROSSED-HIRE-NEVER-MADE-AFTER-A-SKIPPED-FUNDS-PATCH: a committed hire is made in the
    /// pass whose funds patch applies its row. A crossing that skipped the funds patch leaves
    /// the row counted as not applied (NaN) in the stock pool reference, and the later pass
    /// that commits it makes the hire, crossing or not.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedHireFundsAppliedTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedHireFundsAppliedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Hire(string id, double ut, string name) =>
            new GameAction { ActionId = id, UT = ut, Type = GameActionType.KerbalHire, KerbalName = name, HireCost = 62113f };

        private static GameAction Spend(string id, double ut) =>
            new GameAction
            {
                ActionId = id, UT = ut, Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other, DedupKey = id, FundsSpent = 100f,
            };

        private static StockPoolReferenceState StateCounting(params (string id, double effect)[] rows)
        {
            var state = new StockPoolReferenceState(StockPool.Funds) { Initialized = true, LastWritten = 500000.0 };
            foreach (var r in rows)
                state.Counted[r.id] = new CountedRow { Effect = r.effect, Stepped = 0.0 };
            return state;
        }

        /// <summary>One funds patch as PatchFunds runs it: measure, then commit the walk's effects.</summary>
        private static void CommitFundsPatch(
            StockPoolReferenceState state, IList<GameAction> actions, Dictionary<string, double> effects, double nowUT)
        {
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, actions, effects);
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, nowUT, state.LastWritten, 0.0, false, 0.01);
            StockPoolReference.Commit(state, rows, m, state.LastWritten, 0.0);
        }

        [Fact]
        public void Collect_TakesOnlyHireRowsTheReferenceCountsAsNotApplied()
        {
            var actions = new List<GameAction>
            {
                Hire("h-future", 1000.2, "Verhat Kerman"),
                Hire("h-walked", 200.0, "Bob Kerman"),
                Hire("h-new", 300.0, "Bill Kerman"),
                Spend("s-future", 1000.0),
            };
            var state = StateCounting(("h-future", double.NaN), ("h-walked", -62113.0), ("s-future", double.NaN));

            List<GameAction> rows = LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(state, actions);

            Assert.Single(rows);
            Assert.Equal("h-future", rows[0].ActionId);
        }

        [Fact]
        public void Collect_NoReference_IsEmpty()
        {
            var actions = new List<GameAction> { Hire("h", 1000.2, "Verhat Kerman") };
            var state = StateCounting(("h", double.NaN));
            state.Initialized = false;

            Assert.Empty(LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(state, actions));
            Assert.Empty(LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(null, actions));
        }

        [Fact]
        public void SkippedCrossing_MakesNoHire_TheNextPassThatAppliesTheRowMakesIt()
        {
            var hire = Hire("h", 1000.2, "Verhat Kerman");
            var actions = new List<GameAction> { hire };
            var state = StateCounting(("h", double.NaN));

            // The crossing: the funds patch skipped, so nothing committed.
            var crossing = new KspStatePatcher.ClockCrossing { CrossedRows = 1 };
            crossing.CrossedHireKerbals.Add("Verhat Kerman");
            List<GameAction> before = LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(state, actions);
            Assert.Empty(LedgerOrchestrator.ResolveCommittedHiresToMake(crossing, before, state));

            // A later live pass (no crossing) whose funds patch walks and commits the row.
            before = LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(state, actions);
            Assert.Single(before);
            CommitFundsPatch(state, actions, new Dictionary<string, double> { { "h", -62113.0 } }, 1300.0);
            List<string> names = LedgerOrchestrator.ResolveCommittedHiresToMake(null, before, state);

            Assert.Equal(new[] { "Verhat Kerman" }, names);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Committed hire row(s) applied by this funds patch: count=1")
                && l.Contains("crossing=False"));

            // The pass after that finds nothing left to make.
            before = LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(state, actions);
            Assert.Empty(before);
        }

        [Fact]
        public void PassWhoseRowIsStillAhead_MakesNoHire()
        {
            var hire = Hire("h", 1000.2, "Verhat Kerman");
            var actions = new List<GameAction> { hire };
            var state = StateCounting(("h", double.NaN));

            List<GameAction> before = LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(state, actions);
            // The walk did not dispatch the row (still past its cutoff): it stays NaN.
            CommitFundsPatch(state, actions, new Dictionary<string, double>(), 900.0);

            Assert.Empty(LedgerOrchestrator.ResolveCommittedHiresToMake(null, before, state));
            Assert.True(double.IsNaN(state.Counted["h"].Effect));
        }

        [Fact]
        public void AppliedCrossing_MakesItsHiresOnce()
        {
            var hire = Hire("h", 1000.2, "Verhat Kerman");
            var actions = new List<GameAction> { hire };
            var state = StateCounting(("h", double.NaN));
            var crossing = new KspStatePatcher.ClockCrossing { CrossedRows = 1, FundsApplied = true };
            crossing.CrossedHireKerbals.Add("Verhat Kerman");

            List<GameAction> before = LedgerOrchestrator.CollectHireRowsNotAppliedByFunds(state, actions);
            CommitFundsPatch(state, actions, new Dictionary<string, double> { { "h", -62113.0 } }, 1300.0);

            Assert.Equal(new[] { "Verhat Kerman" },
                LedgerOrchestrator.ResolveCommittedHiresToMake(crossing, before, state));
        }
    }
}
