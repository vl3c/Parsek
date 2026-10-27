using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LEDGER-EVENT-IDENTITY step 6 (design 3.15 step 6, 12.6; owner ruling 2026-10-26): a
    /// committed tech the walk can no longer afford is charged into a science debt like a funds
    /// purchase, instead of staying researched for free. The tech stays researched, the running
    /// science goes below 0, later earnings repay it before the pool rises, and the pool write,
    /// the reservation tooltip and step 5's take-back shortfall read the same debt.
    /// </summary>
    [Collection("Sequential")]
    public class ScienceDebtTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ScienceDebtTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class Walk
        {
            internal readonly ScienceModule Science = new ScienceModule();
            internal readonly FundsModule Funds = new FundsModule();
        }

        private static Walk Register()
        {
            var w = new Walk();
            RecalculationEngine.RegisterModule(w.Science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Funds, RecalculationEngine.ModuleTier.SecondTier);
            return w;
        }

        private static List<GameAction> Seeds(float science = 0f)
        {
            return new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 100000f },
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = science },
            };
        }

        private static GameAction Earn(string subject, double ut, long cseq, float award, float cap = 10f)
        {
            return new GameAction
            {
                UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceEarning,
                SubjectId = subject, ScienceAwarded = award, SubjectMaxValue = cap
            };
        }

        private static GameAction Tech(string node, double ut, long cseq, float cost)
        {
            return new GameAction { UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceSpending, NodeId = node, Cost = cost };
        }

        [Theory]
        [InlineData(50.0, 30.0, 0.0)]
        [InlineData(30.0, 30.0, 0.0)]
        [InlineData(10.0, 30.0, 20.0)]
        [InlineData(0.0, 30.0, 30.0)]
        [InlineData(-5.0, 30.0, 30.0)]
        [InlineData(10.0, 0.0, 0.0)]
        public void ResolveDebtShortfall_IsThePartTheRunningValueDoesNotCover(double before, double cost, double expected)
        {
            Assert.Equal(expected, ScienceModule.ResolveDebtShortfall(before, cost), 6);
        }

        // catches: a committed tech the walk cannot pay left researched for free (the walk
        // refusing the deduction) instead of charged into debt.
        [Fact]
        public void UnaffordableTech_IsChargedIntoDebt_AndStaysResearched()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(Earn("crewReport@KerbinSrfLanded", 100, 1, 10f));
            var tech = Tech("survivability", 200, 2, 30f);
            actions.Add(tech);

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(-20.0, w.Science.GetRunningScience(), 3);
            Assert.Equal(20.0, w.Science.GetTechDebtCharged(), 3);
            Assert.True(tech.Effective);
            Assert.True(tech.Affordable);
            Assert.Null(tech.UnaffordableRunningScience);
            Assert.Equal(0.0, w.Science.GetAvailableScience(), 3);
            Assert.Contains(logLines, l => l.Contains("[ScienceModule]")
                && l.Contains("Spending charged into science debt: nodeId=survivability")
                && l.Contains("debt=20"));
            Assert.DoesNotContain(logLines, l => l.Contains("Spending NOT affordable"));
            // Researched: the add-only unlock pass and the two-direction target both keep it.
            Assert.Contains("survivability", KspStatePatcher.BuildCommittedTechUnlockIdsForPatch(actions, 1000));
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var baseline = new GameStateBaseline { ut = 0, researchedTechIds = new List<string> { "start" } };
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                new[] { baseline }, actions, 1000, null, out drops);
            Assert.Contains("survivability", target);
            Assert.Empty(drops);
        }

        // catches: the debt forgiven (a 0 clamp on the cost row) or repaid by anything but
        // later earnings.
        [Fact]
        public void ScienceDebt_IsCarriedAndRepaidByLaterEarnings()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(Earn("a", 100, 1, 10f));
            actions.Add(Tech("survivability", 200, 2, 30f));
            actions.Add(Earn("b", 300, 3, 8f));
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(-12.0, w.Science.GetRunningScience(), 3);

            RecalculationEngine.ClearModules();
            w = Register();
            actions.Add(Earn("c", 400, 4, 10f));
            actions.Add(Earn("d", 500, 5, 9f));
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(7.0, w.Science.GetRunningScience(), 3);
            Assert.Equal(7.0, w.Science.GetAvailableScience(), 3);
        }

        // The owner's case: a rewind moves credit between two flights of one subject. The
        // committed tech was paid by the committed flight's science; after the rewind a new
        // flight earns that subject first and its science is spent, so the committed flight
        // earns only what the cap leaves and the committed tech goes into debt.
        [Fact]
        public void RewindMovesCreditAway_CommittedTechGoesIntoDebt_NeverUnresearched()
        {
            var w = Register();
            var actions = Seeds();
            var committedEarn = Earn("goo@KerbinSrfLanded", 500, 10, 10f);
            var committedTech = Tech("generalRocketry", 600, 11, 10f);
            var newEarn = Earn("goo@KerbinSrfLanded", 200, 20, 10f);
            var newTech = Tech("basicRocketry", 250, 21, 10f);
            actions.Add(committedEarn);
            actions.Add(committedTech);
            actions.Add(newEarn);
            actions.Add(newTech);

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(0f, committedEarn.EffectiveScience);
            Assert.True(committedTech.Effective && committedTech.Affordable);
            Assert.Equal(-10.0, w.Science.GetRunningScience(), 3);
            Assert.Equal(10.0, w.Science.GetTechDebtCharged(), 3);
            var unlocked = KspStatePatcher.BuildCommittedTechUnlockIdsForPatch(actions, 1000);
            Assert.Contains("generalRocketry", unlocked);
            Assert.Contains("basicRocketry", unlocked);
        }

        // A loss with no committed cost ahead stops at 0 as stock stops it; a tech's debt is a
        // cost and is never forgiven that way.
        [Fact]
        public void ScienceDebt_IsNotForgivenByTheZeroClamp()
        {
            var w = Register();
            var actions = Seeds(5f);
            actions.Add(Tech("survivability", 200, 2, 30f));
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(-25.0, w.Science.GetRunningScience(), 3);
            Assert.DoesNotContain(logLines, l => l.Contains("[ScienceModule]") && l.Contains("Zero clamp: ScienceSpending"));
        }

        // catches: the science pool write, the tooltip, or the reservation reading science debt
        // differently from funds debt (a funds purchase the walk cannot pay leaves the same shape).
        [Fact]
        public void TooltipAndWriteTarget_ReadScienceDebtLikeFundsDebt()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(Tech("survivability", 200, 2, 20f));
            actions.Add(new GameAction
            {
                UT = 200, CaptureSequence = 3, Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other, DedupKey = "p", FundsSpent = 100020f
            });
            RecalculationEngine.Recalculate(actions);

            double sciRunning = w.Science.GetProjectionCurrentBalance();
            double fundsRunning = w.Funds.GetProjectionCurrentBalance();
            Assert.Equal(-20.0, sciRunning, 3);
            Assert.Equal(-20.0, fundsRunning, 3);

            string sci = CurrencyReservationOverlay.BuildTooltipFromLedger(
                sciRunning, w.Science.GetAvailableScience(), w.Science.GetProjectionMinBalance(), 0.0, "F1",
                w.Science.GetOutstandingDebt());
            string fnd = CurrencyReservationOverlay.BuildTooltipFromLedger(
                fundsRunning, w.Funds.GetAvailableFunds(), w.Funds.GetProjectionMinBalance(), 0.0, "F1",
                w.Funds.GetOutstandingDebt());
            Assert.Equal("Balance: -20.0", sci);
            Assert.Equal(fnd, sci);

            // The write floors both at 0 and later gains repay the debt first.
            Assert.Equal(0.0, StockPoolReference.ResolveCurrencyWriteTarget(
                w.Science.GetProjectionMinBalance(), sciRunning, 0f, false, 0.0), 6);
            Assert.Equal(0.0, StockPoolReference.ResolveCurrencyWriteTarget(
                w.Funds.GetProjectionMinBalance(), fundsRunning, 0f, false, 0.0), 6);
        }

        // catches: a projection (the Space Center click reservation) that lets the player spend
        // science a committed tech in debt still needs.
        [Fact]
        public void Projection_HoldsNothingSpendableWhileACommittedTechIsInDebt()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(Earn("a", 100, 1, 10f));
            actions.Add(Earn("a", 300, 20, 10f));   // a repeat of a capped subject earns nothing
            actions.Add(Earn("b", 350, 21, 6f));
            actions.Add(Tech("survivability", 500, 2, 15f)); // the committed tech ahead
            RecalculationEngine.Recalculate(actions, 400);
            // Running 16 now, the committed tech needs 15 and nothing earns before it.
            Assert.Equal(16.0, w.Science.GetRunningScience(), 3);
            Assert.Equal(1.0, w.Science.GetAvailableScience(), 3);
        }
    }
}
