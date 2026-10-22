using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Two parts of the committed-value rule built with phase 2 so the stock pool reference
    /// ships alone without lowering a pool in normal play:
    /// <list type="bullet">
    /// <item>Stock's 0 clamp in the walk (plan section 2.3, the no-cost-ahead case): a funds or
    /// science row that is not a cost row and takes the running value below 0 stops at 0, the
    /// rest forgiven, as stock does with <c>AllowNegativeCurrency</c> off.</item>
    /// <item>Open penalties out of the pool (plan section 2.2): the projection's synthetic
    /// deadline fail of an open contract is not held in the reserve; it is told apart from
    /// ledger rows by membership and <c>ContractsModule</c> still injects it.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class StockZeroClampAndOpenPenaltyTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StockZeroClampAndOpenPenaltyTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Seed(double funds) => new GameAction
        {
            ActionId = "seed", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)funds,
        };

        private static GameAction Fail(string id, double ut, float penalty) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.ContractFail, ContractId = id, FundsPenalty = penalty,
        };

        private static GameAction Earn(string id, double ut, float funds) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FundsEarning, FundsAwarded = funds,
        };

        private static GameAction Spend(string id, double ut, float funds) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FundsSpending, FundsSpent = funds,
        };

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

        // ------------------------------------------------------------------
        // The pure rule
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(1000.0, -2000.0, false, false, 2000.0)] // a fail past 0 stops at 0
        [InlineData(1000.0, 400.0, false, false, 0.0)]      // stays above 0
        [InlineData(-2000.0, -2500.0, false, false, 500.0)] // already below 0: forgiven in full, never raised
        [InlineData(1000.0, -2000.0, true, false, 0.0)]     // a cost row is paid in full
        [InlineData(1000.0, -2000.0, false, true, 0.0)]     // AllowNegativeCurrency on
        [InlineData(-100.0, 50.0, false, false, 0.0)]       // a gain is never touched
        public void ForgivenBelowZero_IsStocksClamp(
            double before, double after, bool costRow, bool allowNegative, double expected)
        {
            Assert.Equal(expected,
                StockCurrencyClamp.ForgivenBelowZero(before, after, costRow, allowNegative), 6);
        }

        [Fact]
        public void CostRows_AreThePlansList()
        {
            Assert.True(FundsModule.IsFundsCostRow(GameActionType.FundsSpending));
            Assert.True(FundsModule.IsFundsCostRow(GameActionType.FacilityUpgrade));
            Assert.True(FundsModule.IsFundsCostRow(GameActionType.FacilityRepair));
            Assert.True(FundsModule.IsFundsCostRow(GameActionType.KerbalHire));
            Assert.True(FundsModule.IsFundsCostRow(GameActionType.StrategyActivate));
            Assert.False(FundsModule.IsFundsCostRow(GameActionType.ContractFail));
            Assert.False(FundsModule.IsFundsCostRow(GameActionType.ContractCancel));
            Assert.False(FundsModule.IsFundsCostRow(GameActionType.FundsEarning));

            Assert.True(ScienceModule.IsScienceCostRow(GameActionType.ScienceSpending));
            Assert.True(ScienceModule.IsScienceCostRow(GameActionType.StrategyActivate));
            // The science half of a currency conversion, as its funds half is a FundsSpending.
            Assert.True(ScienceModule.IsScienceCostRow(GameActionType.StrategyScienceDebit));
            Assert.False(ScienceModule.IsScienceCostRow(GameActionType.ScienceEarning));
        }

        // ------------------------------------------------------------------
        // The walk (review case B2)
        // ------------------------------------------------------------------

        [Fact]
        public void B2_FailPastZero_ThenARecovery_KeepsTheRecovery()
        {
            var funds = Walk(Seed(1000), Fail("f", 10, 3000f), Earn("r", 20, 500f));

            Assert.Equal(500.0, funds.GetRunningBalance(), 6);
            Assert.Equal(2000.0, funds.GetForgivenBelowZero(), 6);
            // The no-projection target stays the running balance.
            Assert.Equal(500.0, funds.GetProjectionMinBalance(), 6);
            Assert.Equal(500.0, funds.GetAvailableFunds(), 6);
            Assert.Contains(logLines, l => l.Contains("[Funds]") && l.Contains("Zero clamp: ContractFail id=f")
                && l.Contains("forgiven=2000") && l.Contains("runningBalance=0"));
        }

        [Fact]
        public void ACostRowPastZero_IsPaidInFull_AndALaterFailIsForgivenNotRaised()
        {
            var funds = Walk(Seed(1000), Spend("c", 10, 3000f), Fail("f", 20, 500f));

            Assert.Equal(-2000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(500.0, funds.GetForgivenBelowZero(), 6);
            Assert.Equal(-2000.0, funds.GetProjectionMinBalance(), 6);
            Assert.Equal(0.0, funds.GetAvailableFunds(), 6);
        }

        [Fact]
        public void AllowNegativeCurrency_TheFailRunsBelowZero()
        {
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => true;
            var funds = Walk(Seed(1000), Fail("f", 10, 3000f), Earn("r", 20, 500f));

            Assert.Equal(-1500.0, funds.GetRunningBalance(), 6);
            Assert.Equal(0.0, funds.GetForgivenBelowZero(), 6);
            Assert.DoesNotContain(logLines, l => l.Contains("Zero clamp:"));
        }

        [Fact]
        public void ACancelPenaltyPastZero_IsForgivenToo()
        {
            var cancel = new GameAction
            {
                ActionId = "x", UT = 10, Type = GameActionType.ContractCancel, ContractId = "x", FundsPenalty = 800f,
            };
            var funds = Walk(Seed(300), cancel);
            Assert.Equal(0.0, funds.GetRunningBalance(), 6);
            Assert.Equal(500.0, funds.GetForgivenBelowZero(), 6);
        }

        [Fact]
        public void Science_ExchangeDebitIsACostRow_TheWalkStillRunsBelowZero()
        {
            // An exchange debit can sort ahead of the earnings that funded it (commit-stamped
            // earnings land at the recording's end UT); forgiving it would create science.
            var science = new ScienceModule();
            science.Reset();
            var rows = new List<GameAction>
            {
                new GameAction { ActionId = "seed", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 10f },
                new GameAction { ActionId = "d", UT = 5, Type = GameActionType.StrategyScienceDebit, Cost = 50f },
            };
            science.PrePass(rows);
            foreach (var a in rows) science.ProcessAction(a);

            Assert.Equal(-40.0, science.GetRunningScience(), 6);
        }

        // ------------------------------------------------------------------
        // Open penalties out of the pool reserve (plan 2.2)
        // ------------------------------------------------------------------

        private sealed class Registered
        {
            internal ContractsModule Contracts;
            internal FundsModule Funds;
        }

        private static Registered Register()
        {
            var r = new Registered { Contracts = new ContractsModule(), Funds = new FundsModule() };
            RecalculationEngine.RegisterModule(r.Contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(r.Funds, RecalculationEngine.ModuleTier.SecondTier);
            return r;
        }

        private static List<GameAction> OpenContractLedger(bool withCommittedFail)
        {
            var ledger = new List<GameAction>
            {
                Seed(10000),
                new GameAction
                {
                    ActionId = "acc", UT = 10, Type = GameActionType.ContractAccept, ContractId = "k",
                    DeadlineUT = 1000, FundsPenalty = 3000f,
                },
                // A committed cost ahead, so the cutoff walk projects.
                Spend("cost", 500, 1000f),
            };
            if (withCommittedFail)
                ledger.Add(Fail("k", 600, 3000f));
            return ledger;
        }

        [Fact]
        public void OpenContract_ItsSyntheticDeadlineFail_IsNotHeldInThePool()
        {
            var r = Register();
            RecalculationEngine.Recalculate(OpenContractLedger(withCommittedFail: false), 100.0);

            // The reserve holds the committed cost only: 10000 - 1000. With the synthetic
            // penalty it was 6000.
            Assert.Equal(10000.0, r.Funds.GetRunningBalance(), 6);
            Assert.Equal(9000.0, r.Funds.GetProjectionMinBalance(), 6);
            Assert.Equal(9000.0, r.Funds.GetAvailableFunds(), 6);
            // The projection still injects it (ContractsModule unchanged; its own log is inside the
            // projection's suppressed walk) and the reserve says what it left out.
            Assert.Contains(logLines, l => l.Contains("[RecalcEngine]")
                && l.Contains("Projection reserve for FundsModule")
                && l.Contains("excludedSyntheticRows=1") && l.Contains("excludedSyntheticDelta=-3000"));
        }

        [Fact]
        public void ACommittedFail_ALedgerRow_IsStillReserved()
        {
            var r = Register();
            RecalculationEngine.Recalculate(OpenContractLedger(withCommittedFail: true), 100.0);

            Assert.Equal(6000.0, r.Funds.GetProjectionMinBalance(), 6);
            Assert.Contains(logLines, l => l.Contains("Projection reserve for FundsModule")
                && l.Contains("excludedSyntheticRows=0"));
        }

        [Fact]
        public void CountsInReserve_IsMembership()
        {
            var row = Earn("e", 1, 1f);
            var set = new HashSet<GameAction> { row };
            Assert.True(RecalculationEngine.CountsInReserve(row, set));
            Assert.False(RecalculationEngine.CountsInReserve(Earn("e", 1, 1f), set));
            Assert.True(RecalculationEngine.CountsInReserve(Earn("x", 1, 1f), null));
        }

        [Fact]
        public void TheAffordabilityProbe_LeavesTheSyntheticFailOutToo()
        {
            Register();
            FundsModule probe = RecalculationEngine.ProbeModuleAtCutoff<FundsModule>(
                OpenContractLedger(withCommittedFail: false), 100.0);
            Assert.NotNull(probe);
            Assert.Equal(9000.0, probe.GetProjectionMinBalance(), 6);
        }
    }
}
