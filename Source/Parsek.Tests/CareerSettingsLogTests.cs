using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Career settings audit 2026-11-04 section 3.4: each funds / science loss is judged under
    /// the allow-negative-currency setting in force when it was captured, so switching the
    /// setting later never changes a committed past.
    /// </summary>
    [Collection("Sequential")]
    public class CareerSettingsLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private bool live;

        public CareerSettingsLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            Ledger.ResetForTesting();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => live;
        }

        public void Dispose()
        {
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static CareerSettingsLog.Entry E(long from, bool v)
            => new CareerSettingsLog.Entry { FromCaptureSequence = from, Value = v };

        private static FundsModule Walk(params GameAction[] actions)
        {
            var funds = new FundsModule();
            funds.Reset();
            var list = new List<GameAction>(actions);
            funds.PrePass(list);
            foreach (var a in list)
            {
                a.Effective = true;
                funds.ProcessAction(a);
            }
            return funds;
        }

        private static GameAction Seed(double funds) => new GameAction
        {
            ActionId = "seed", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)funds,
        };

        private static GameAction Fail(string id, double ut, long cseq, float penalty) => new GameAction
        {
            ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.ContractFail,
            ContractId = id, FundsPenalty = penalty,
        };

        private static GameAction Earn(string id, double ut, long cseq, float funds) => new GameAction
        {
            ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.FundsEarning, FundsAwarded = funds,
        };

        [Fact]
        public void ValueAt_ReadsTheEntryInForceAtTheCapture_TheFirstBelowIt_AndLiveWhenEmpty()
        {
            var entries = new List<CareerSettingsLog.Entry> { E(5, false), E(20, true), E(30, false) };
            Assert.False(CareerSettingsLog.ValueAt(entries, 0, true));
            Assert.False(CareerSettingsLog.ValueAt(entries, 19, true));
            Assert.True(CareerSettingsLog.ValueAt(entries, 20, false));
            Assert.True(CareerSettingsLog.ValueAt(entries, 29, false));
            Assert.False(CareerSettingsLog.ValueAt(entries, 31, true));
            Assert.True(CareerSettingsLog.ValueAt(new List<CareerSettingsLog.Entry>(), 7, true));
        }

        [Fact]
        public void Note_AppendsOnlyAChange_AndCollapsesAFlipBackWithNoCaptureBetween()
        {
            var entries = new List<CareerSettingsLog.Entry>();
            Assert.True(CareerSettingsLog.Note(entries, false, 1));
            Assert.False(CareerSettingsLog.Note(entries, false, 4));
            Assert.True(CareerSettingsLog.Note(entries, true, 10));
            Assert.Equal(2, entries.Count);
            // Back off before any row was captured: the on entry never governed a row.
            Assert.True(CareerSettingsLog.Note(entries, false, 10));
            Assert.Single(entries);
            Assert.False(entries[0].Value);
            // A change at the first entry's own number never replaces it (it governs the past).
            Assert.True(CareerSettingsLog.Note(entries, true, 1));
            Assert.Equal(2, entries.Count);
            Assert.False(entries[0].Value);
            Assert.False(CareerSettingsLog.ValueAt(entries, 0, true));
            Assert.True(CareerSettingsLog.ValueAt(entries, 1, false));
        }

        [Fact]
        public void SwitchingItOnLater_DoesNotUnforgiveAnOldLoss()
        {
            // Off: a 8000 penalty at 5000 funds stopped at 0. Later the player allows negatives.
            live = false;
            CareerSettingsLog.NoteLive(live, "test");
            var fail = Fail("f", 2000, 1, 8000f);
            CaptureSequence.Next();
            live = true;
            CareerSettingsLog.NoteLive(live, "difficulty-dialog");

            FundsModule funds = Walk(Seed(5000), fail, Earn("e", 9000, 3, 40000f));

            Assert.Equal(40000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(3000.0, funds.GetForgivenBelowZero(), 6);
            Assert.Contains(logLines, l => l.Contains("[CareerSettings]")
                && l.Contains("Allow negative currency is on from capture"));
        }

        [Fact]
        public void FirstToggleWithNoCaptureSinceTheLoad_KeepsThePastsSetting()
        {
            // The load notes "off" from the next capture; the player turns it on before any
            // row is captured. Rows captured before the load keep "off".
            live = false;
            var fail = Fail("f", 2000, 1, 8000f);
            Ledger.AddAction(fail);
            CareerSettingsLog.NoteLive(live, "ledger-load");
            live = true;
            CareerSettingsLog.NoteLive(live, "difficulty-dialog");

            Assert.Equal(2, CareerSettingsLog.AllowNegativeEntries.Count);
            FundsModule funds = Walk(Seed(5000), fail, Earn("e", 9000, 3, 40000f));
            Assert.Equal(3000.0, funds.GetForgivenBelowZero(), 6);

            // Back off before any capture: the "on" entry never governed a row and goes.
            live = false;
            CareerSettingsLog.NoteLive(live, "difficulty-dialog");
            Assert.Single(CareerSettingsLog.AllowNegativeEntries);
        }

        [Fact]
        public void SwitchingItOffLater_DoesNotForgiveAnOldDebt()
        {
            // On: the penalty ran funds to -3000; switching off afterwards is no free money.
            live = true;
            CareerSettingsLog.NoteLive(live, "test");
            var fail = Fail("f", 2000, 1, 8000f);
            CaptureSequence.Next();
            live = false;
            CareerSettingsLog.NoteLive(live, "difficulty-dialog");

            FundsModule funds = Walk(Seed(5000), fail, Earn("e", 9000, 3, 43000f));

            Assert.Equal(40000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(0.0, funds.GetForgivenBelowZero(), 6);
        }

        [Fact]
        public void TheLog_RoundTripsThroughTheLedgerNode()
        {
            live = false;
            CareerSettingsLog.NoteLive(live, "test");
            CaptureSequence.Next();
            live = true;
            CareerSettingsLog.NoteLive(live, "test");
            ConfigNode node = Ledger.BuildSaveNode();
            Assert.Equal(2, node.GetValues(CareerSettingsLog.AllowNegativeKey).Length);

            CareerSettingsLog.ResetForTesting();
            Assert.True(Ledger.LoadFromNode(node));

            Assert.Equal(2, CareerSettingsLog.AllowNegativeEntries.Count);
            Assert.True(CareerSettingsLog.AllowNegativeEntries[1].Value);
        }
    }
}
