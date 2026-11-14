using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-04 (replay audit T2) for once-only milestone rewards: after a
    /// rewind, a world-first or a record step reached first at a lower reward setting and
    /// again (on the other timeline) at a higher one pays the earliest its own reward and the
    /// other only the difference, so the career total is the larger reward, never lower.
    /// </summary>
    [Collection("Sequential")]
    public class RewindTopUpMilestoneTests : IDisposable
    {
        private readonly RecalculationModuleSet modules = new RecalculationModuleSet();

        private readonly List<string> logLines = new List<string>();
        private readonly MilestonesModule milestones = new MilestonesModule();
        private readonly FundsModule funds = new FundsModule();
        private readonly ReputationModule rep = new ReputationModule();
        private readonly ScienceModule science = new ScienceModule();

        public RewindTopUpMilestoneTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            Ledger.ResetForTesting();
            modules.Register(milestones, RecalculationModuleSet.ModuleTier.FirstTier);
            modules.Register(science, RecalculationModuleSet.ModuleTier.FirstTier);
            modules.Register(funds, RecalculationModuleSet.ModuleTier.SecondTier);
            modules.Register(rep, RecalculationModuleSet.ModuleTier.SecondTier);
        }

        public void Dispose()
        {
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static List<GameAction> Seeds() => new List<GameAction>
        {
            new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 100000f },
            new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 0f },
            new GameAction { UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = 0f },
        };

        private static GameAction First(string actionId, string id, double ut, long cseq, float f, float r = 0f, float s = 0f)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
                MilestoneId = id, MilestoneFundsAwarded = f, MilestoneRepAwarded = r, MilestoneScienceAwarded = s,
            };

        private static GameAction Record(string actionId, double ut, long cseq, params RecordThresholdCredit[] band)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
                MilestoneId = "RecordsAltitude",
                MilestoneFundsAwarded = band.Sum(c => c.Funds),
                MilestoneRepAwarded = band.Sum(c => c.Rep),
                MilestoneScienceAwarded = band.Sum(c => c.Science),
                MilestoneRecordThresholds = band.Length,
                MilestoneRecordBand = band.ToList(),
            };

        private double Walk(params GameAction[] rows)
        {
            var actions = Seeds();
            actions.AddRange(rows);
            RecalculationEngine.Recalculate(modules, actions);
            return funds.GetRunningBalance() - 100000.0;
        }

        // ---------------------------------------------------------------- world-firsts

        [Fact]
        public void WorldFirst_CheaperRepeatEarlierAfterARewind_TheCommittedRowPaysTheDifference()
        {
            // Committed Mun flyby at UT 5000 under 150% funds (15000); rewind; at UT 2000 the
            // same world-first under 100% (10000).
            var committed = First("A", "Mun/Flyby", 5000, 1, 15000f, 15f, 3f);
            StockShadowTakeBack.AddBreakForTesting(1, 1000.0, "PlainRewind");
            var earlier = First("C", "Mun/Flyby", 2000, 2, 10000f, 10f, 2f);

            double total = Walk(committed, earlier);

            Assert.True(earlier.Effective);
            Assert.False(earlier.RecordPartlyCredited);
            Assert.True(committed.Effective);
            Assert.True(committed.RecordPartlyCredited);
            Assert.Equal(5000, (double)committed.CreditedMilestoneFunds, 3);
            Assert.Equal(5, (double)committed.CreditedMilestoneRep, 3);
            Assert.Equal(1, (double)committed.CreditedMilestoneScience, 3);
            Assert.False(committed.HasTakeBack);
            Assert.Equal(15000.0, total, 3);
            Assert.Contains(logLines, l => l.Contains("[Milestones]")
                && l.Contains("Milestone 'Mun/Flyby' topped up") && l.Contains("actionId=A"));
            Assert.Equal(TimelineRowHover.ToppedUpText, TimelineRowHover.RecordPartlyCounted(committed));
        }

        [Fact]
        public void WorldFirst_DearerEarlier_TheRepeatStaysAlreadyAchieved()
        {
            var committed = First("A", "Mun/Flyby", 5000, 1, 10000f);
            StockShadowTakeBack.AddBreakForTesting(1, 1000.0, "PlainRewind");
            var earlier = First("C", "Mun/Flyby", 2000, 2, 15000f);

            double total = Walk(committed, earlier);

            Assert.False(committed.Effective);
            Assert.Equal(GameActionNotCountedReason.MilestoneAlreadyAchieved, committed.NotCountedReason);
            Assert.Equal(15000.0, total, 3);
        }

        [Fact]
        public void WorldFirst_NeverRewound_TheRepeatStaysAlreadyAchieved_AsBefore()
        {
            var first = First("A", "Mun/Flyby", 2000, 1, 10000f);
            var repeat = First("B", "Mun/Flyby", 5000, 2, 15000f);

            double total = Walk(first, repeat);

            Assert.False(repeat.Effective);
            Assert.Equal(10000.0, total, 3);
            Assert.DoesNotContain(logLines, l => l.Contains("topped up"));
        }

        [Fact]
        public void WorldFirst_StockForgotTheFirst_TheRepeatTopsUpTheDifference_AndTheRestIsTakenBack()
        {
            // First at UT 100 (10000); rewind to UT 50 makes stock forget it; slider raised; the
            // repeat at UT 120 is paid 15000 by stock. The walk credits 5000 and takes 10000 back.
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            var first = First("A", "Mun/Flyby", 100, 10, 10000f);
            StockShadowTakeBack.AddBreakForTesting(10, 50.0, "PlainRewind");
            var repeat = First("B", "Mun/Flyby", 120, 20, 15000f);

            double total = Walk(first, repeat);

            Assert.True(repeat.Effective);
            Assert.Equal(5000, (double)repeat.CreditedMilestoneFunds, 3);
            Assert.Equal(10000, (double)repeat.TakeBackFunds, 3);
            Assert.Equal(15000.0, total, 3);
        }

        [Fact]
        public void TopUp_Difference_IsPerCurrency_NeverBelowZero()
        {
            var own = new MilestoneTopUp.Paid { Funds = 15000f, Rep = 5f, Science = 2f };
            var paid = new MilestoneTopUp.Paid { Funds = 10000f, Rep = 8f, Science = 2f };
            MilestoneTopUp.Paid d = MilestoneTopUp.Difference(own, paid);
            Assert.Equal(5000f, d.Funds);
            Assert.Equal(0f, d.Rep);
            Assert.Equal(0f, d.Science);
            Assert.True(d.Any);
            Assert.False(MilestoneTopUp.Difference(paid, paid).Any);
        }

        // ---------------------------------------------------------------- record steps

        [Fact]
        public void RecordStep_ReCrossedEarlierAtALowerReward_TheCommittedCrossingPaysTheDifference()
        {
            // Committed: step 7 crossed at UT 5000 paying 2000 (200%). Rewind; the same step at
            // UT 2000 pays 1000 (100%).
            var committed = Record("A", 5000, 1, new RecordThresholdCredit(7, 2000f, 1f, 0f));
            StockShadowTakeBack.AddBreakForTesting(1, 1000.0, "PlainRewind");
            var earlier = Record("C", 2000, 2, new RecordThresholdCredit(7, 1000f, 0.5f, 0f));

            double total = Walk(committed, earlier);

            Assert.True(committed.Effective);
            Assert.True(committed.RecordPartlyCredited);
            Assert.Equal(1000, (double)committed.CreditedMilestoneFunds, 3);
            Assert.Equal(0, committed.RecordThresholdsCredited);
            Assert.Equal(2000.0, total, 3);
            Assert.Contains(logLines, l => l.Contains("toppedUp=[7]"));
        }

        [Fact]
        public void RecordStep_SameReward_TheRepeatStaysZeroed_AsBefore()
        {
            var committed = Record("A", 5000, 1, new RecordThresholdCredit(7, 1000f, 0.5f, 0f));
            StockShadowTakeBack.AddBreakForTesting(1, 1000.0, "PlainRewind");
            var earlier = Record("C", 2000, 2, new RecordThresholdCredit(7, 1000f, 0.5f, 0f));

            double total = Walk(committed, earlier);

            Assert.False(committed.Effective);
            Assert.Equal(GameActionNotCountedReason.RecordThresholdsAlreadyCredited, committed.NotCountedReason);
            Assert.Equal(1000.0, total, 3);
        }

        [Fact]
        public void RecordStep_PaidAsNewTwice_TheTopUpIsNotTakenBack()
        {
            // Stock forgot step 3 (rewind to UT 50) and paid it again at a higher reward.
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            var first = Record("A", 100, 10, new RecordThresholdCredit(3, 1000f, 0f, 0f));
            StockShadowTakeBack.AddBreakForTesting(10, 50.0, "PlainRewind");
            var again = Record("B", 120, 20, new RecordThresholdCredit(3, 1500f, 0f, 0f));

            double total = Walk(first, again);

            Assert.True(again.Effective);
            Assert.Equal(500, (double)again.CreditedMilestoneFunds, 3);
            Assert.Equal(1000, (double)again.TakeBackFunds, 3);
            Assert.Equal(1500.0, total, 3);
        }
    }
}
