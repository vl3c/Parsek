using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FundsModuleTests : IDisposable
    {
        private readonly FundsModule module;
        private readonly List<string> logLines = new List<string>();

        public FundsModuleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            module = new FundsModule();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers — build fund-affecting actions
        // ================================================================

        private static GameAction MakeSeed(double ut, float initialFunds)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsInitial,
                InitialFunds = initialFunds
            };
        }

        private static GameAction MakeFundsEarning(double ut, float fundsAwarded,
            FundsEarningSource source, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsEarning,
                FundsAwarded = fundsAwarded,
                FundsSource = source,
                RecordingId = recordingId
            };
        }

        private static GameAction MakeFundsSpending(double ut, float fundsSpent,
            FundsSpendingSource source, string recordingId = null, int sequence = 0)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpent = fundsSpent,
                FundsSpendingSource = source,
                RecordingId = recordingId,
                Sequence = sequence
            };
        }

        private static GameAction MakeMilestone(double ut, string milestoneId,
            float fundsAwarded, float repAwarded = 0f, string recordingId = null,
            bool effective = true)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = milestoneId,
                MilestoneFundsAwarded = fundsAwarded,
                MilestoneRepAwarded = repAwarded,
                RecordingId = recordingId,
                Effective = effective
            };
        }

        private static GameAction MakeContractAccept(double ut, string contractId,
            float advance = 0f)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractAccept,
                ContractId = contractId,
                AdvanceFunds = advance
            };
        }

        private static GameAction MakeContractComplete(double ut, string contractId,
            float fundsReward, bool effective = true)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractComplete,
                ContractId = contractId,
                FundsReward = fundsReward,
                TransformedFundsReward = fundsReward,
                Effective = effective
            };
        }

        private static GameAction MakeContractFail(double ut, string contractId,
            float fundsPenalty)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractFail,
                ContractId = contractId,
                FundsPenalty = fundsPenalty
            };
        }

        private static GameAction MakeContractCancel(double ut, string contractId,
            float fundsPenalty)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractCancel,
                ContractId = contractId,
                FundsPenalty = fundsPenalty
            };
        }

        // ================================================================
        // Seed tests
        // ================================================================

        [Fact]
        public void Seed_SetsInitialFundsAndBalance()
        {
            var seed = MakeSeed(0, 25000f);
            module.ProcessAction(seed);

            Assert.Equal(25000.0, module.GetInitialFunds());
            Assert.Equal(25000.0, module.GetRunningBalance());
        }

        [Fact]
        public void Seed_LogsFundsInitial()
        {
            var seed = MakeSeed(0, 25000f);
            module.ProcessAction(seed);

            Assert.Contains(logLines, l => l.Contains("[Funds]") && l.Contains("FundsInitial") && l.Contains("25000"));
        }

        [Fact]
        public void Reset_ClearsAllState()
        {
            var seed = MakeSeed(0, 25000f);
            module.ProcessAction(seed);
            module.ProcessAction(MakeFundsEarning(100, 5000f, FundsEarningSource.Recovery, "rec-A"));

            module.Reset();

            Assert.Equal(0.0, module.GetInitialFunds());
            Assert.Equal(0.0, module.GetRunningBalance());
            Assert.Equal(0.0, module.GetTotalEarnings());
            Assert.Equal(0.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void Reset_LogsReset()
        {
            module.Reset();
            Assert.Contains(logLines, l => l.Contains("[Funds]") && l.Contains("Reset"));
        }

        // ================================================================
        // Earnings tests
        // ================================================================

        [Fact]
        public void FundsEarning_AddsToBalanceAndTotalEarnings()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeFundsEarning(100, 8000f, FundsEarningSource.Recovery, "rec-A"));

            Assert.Equal(33000.0, module.GetRunningBalance());
            Assert.Equal(8000.0, module.GetTotalEarnings());
        }

        [Fact]
        public void FundsEarning_NonEffective_Skipped()
        {
            module.ProcessAction(MakeSeed(0, 25000f));

            var earning = MakeFundsEarning(100, 8000f, FundsEarningSource.Milestone);
            earning.Effective = false;
            module.ProcessAction(earning);

            Assert.Equal(25000.0, module.GetRunningBalance());
            Assert.Equal(0.0, module.GetTotalEarnings());
        }

        [Fact]
        public void FundsEarning_NonEffective_LogsSkip()
        {
            var earning = MakeFundsEarning(100, 8000f, FundsEarningSource.Milestone);
            earning.Effective = false;
            module.ProcessAction(earning);

            Assert.Contains(logLines, l => l.Contains("[Funds]") && l.Contains("skipped") && l.Contains("not effective"));
        }

        [Fact]
        public void MilestoneEarning_Effective_AddsFundsToBalance()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeMilestone(50, "FirstLaunch", 8000f, effective: true));

            Assert.Equal(33000.0, module.GetRunningBalance());
            Assert.Equal(8000.0, module.GetTotalEarnings());
        }

        [Fact]
        public void MilestoneEarning_NotEffective_NoFunds()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeMilestone(50, "FirstLaunch", 8000f, effective: false));

            Assert.Equal(25000.0, module.GetRunningBalance());
            Assert.Equal(0.0, module.GetTotalEarnings());
        }

        [Fact]
        public void MilestoneEarning_ZeroFunds_NoBalanceChange()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeMilestone(50, "FirstLaunch", 0f, effective: true));

            Assert.Equal(25000.0, module.GetRunningBalance());
            Assert.Equal(0.0, module.GetTotalEarnings());
        }

        [Fact]
        public void ContractAccept_AdvanceFundsAdded()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractAccept(100, "contract-1", 5000f));

            Assert.Equal(30000.0, module.GetRunningBalance());
            Assert.Equal(5000.0, module.GetTotalEarnings());
        }

        [Fact]
        public void ContractAccept_ZeroAdvance_NoChange()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractAccept(100, "contract-1", 0f));

            Assert.Equal(25000.0, module.GetRunningBalance());
            Assert.Equal(0.0, module.GetTotalEarnings());
        }

        [Fact]
        public void ContractComplete_Effective_AddsFundsReward()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractComplete(200, "contract-1", 15000f, effective: true));

            Assert.Equal(40000.0, module.GetRunningBalance());
            Assert.Equal(15000.0, module.GetTotalEarnings());
        }

        [Fact]
        public void ContractComplete_NotEffective_NoFunds()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractComplete(200, "contract-1", 15000f, effective: false));

            Assert.Equal(25000.0, module.GetRunningBalance());
            Assert.Equal(0.0, module.GetTotalEarnings());
        }

        [Fact]
        public void ContractComplete_NotEffective_LogsSkip()
        {
            module.ProcessAction(MakeContractComplete(200, "contract-1", 15000f, effective: false));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("skipped") && l.Contains("not effective"));
        }

        // ================================================================
        // Spending tests
        // ================================================================

        [Fact]
        public void FundsSpending_DeductsFromBalance()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild, "rec-A"));

            Assert.Equal(20000.0, module.GetRunningBalance());
        }

        [Fact]
        public void FundsSpending_Affordable_FlagTrue()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            var spending = MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild, "rec-A");
            module.ProcessAction(spending);

            Assert.True(spending.Affordable);
        }

        [Fact]
        public void FundsSpending_NotAffordable_FlagFalse()
        {
            module.ProcessAction(MakeSeed(0, 5000f));
            var spending = MakeFundsSpending(10, 10000f, FundsSpendingSource.VesselBuild, "rec-A");
            module.ProcessAction(spending);

            Assert.False(spending.Affordable);
        }

        [Fact]
        public void FundsSpending_NotAffordable_LogsWarning()
        {
            module.ProcessAction(MakeSeed(0, 5000f));
            var spending = MakeFundsSpending(10, 10000f, FundsSpendingSource.VesselBuild, "rec-A");
            module.ProcessAction(spending);

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("NOT affordable"));
        }

        [Fact]
        public void FundsSpending_ExactBalance_IsAffordable()
        {
            module.ProcessAction(MakeSeed(0, 5000f));
            var spending = MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild);
            module.ProcessAction(spending);

            Assert.True(spending.Affordable);
            Assert.Equal(0.0, module.GetRunningBalance());
        }

        // ================================================================
        // Contract penalties
        // ================================================================

        [Fact]
        public void ContractFail_DeductsPenalty()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractFail(100, "contract-1", 5000f));

            Assert.Equal(20000.0, module.GetRunningBalance());
        }

        [Fact]
        public void ContractCancel_DeductsPenalty()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractCancel(100, "contract-1", 3000f));

            Assert.Equal(22000.0, module.GetRunningBalance());
        }

        [Fact]
        public void ContractFail_ZeroPenalty_NoChange()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractFail(100, "contract-1", 0f));

            Assert.Equal(25000.0, module.GetRunningBalance());
        }

        // ================================================================
        // Pre-pass: ComputeTotalSpendings
        // ================================================================

        [Fact]
        public void ComputeTotalSpendings_SumsAllFundsSpendings()
        {
            var actions = new List<GameAction>
            {
                MakeSeed(0, 25000f),
                MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeFundsSpending(100, 15000f, FundsSpendingSource.FacilityUpgrade),
                MakeFundsEarning(50, 8000f, FundsEarningSource.Milestone)
            };

            module.ComputeTotalSpendings(actions);

            Assert.Equal(20000.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void ComputeTotalSpendings_IncludesContractPenalties()
        {
            var actions = new List<GameAction>
            {
                MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild),
                MakeContractFail(200, "c-1", 3000f),
                MakeContractCancel(300, "c-2", 2000f)
            };

            module.ComputeTotalSpendings(actions);

            Assert.Equal(10000.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void ComputeTotalSpendings_NullList_Zero()
        {
            module.ComputeTotalSpendings(null);
            Assert.Equal(0.0, module.GetTotalCommittedSpendings());
        }

        [Fact]
        public void ComputeTotalSpendings_LogsSummary()
        {
            var actions = new List<GameAction>
            {
                MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild),
                MakeContractFail(200, "c-1", 3000f)
            };

            module.ComputeTotalSpendings(actions);

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("ComputeTotalSpendings") &&
                l.Contains("spendings=1") && l.Contains("penalties=1"));
        }

        // ================================================================
        // Reservation / GetAvailableFunds
        // ================================================================

        [Fact]
        public void GetAvailableFunds_WithNoSpendings_EqualsSeedPlusEarnings()
        {
            module.ComputeTotalSpendings(new List<GameAction>());
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeFundsEarning(100, 10000f, FundsEarningSource.Recovery, "rec-A"));

            Assert.Equal(35000.0, module.GetAvailableFunds());
        }

        [Fact]
        public void GetAvailableFunds_ClampedToZeroWhenNegative()
        {
            var actions = new List<GameAction>
            {
                MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild),
                MakeFundsSpending(100, 30000f, FundsSpendingSource.FacilityUpgrade)
            };

            module.ComputeTotalSpendings(actions);
            module.ProcessAction(MakeSeed(0, 25000f));

            // Available = 25000 (seed) + 0 (earnings) - 35000 (spendings) = -10000 → clamped to 0
            Assert.Equal(0.0, module.GetAvailableFunds());
        }

        // ================================================================
        // Design doc 5.12 — Verified scenario: BasicCareerWithSeed
        // ================================================================

        [Fact]
        public void Scenario_BasicCareerWithSeed()
        {
            // Seed: 25,000.
            // UT=10:  Vessel build -5,000.   Balance: 20,000.
            // UT=50:  Milestone +8,000.      Balance: 28,000.
            // UT=60:  Recovery +3,000.       Balance: 31,000.
            // Available at UT=100: 31,000 - 0 future spendings = 31,000.

            var actions = new List<GameAction>
            {
                MakeSeed(0, 25000f),
                MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeMilestone(50, "FirstOrbit", 8000f, effective: true),
                MakeFundsEarning(60, 3000f, FundsEarningSource.Recovery, "rec-A")
            };

            module.ComputeTotalSpendings(actions);

            foreach (var action in actions)
                module.ProcessAction(action);

            Assert.Equal(31000.0, module.GetRunningBalance());
            Assert.Equal(31000.0, module.GetAvailableFunds());
            Assert.Equal(25000.0, module.GetInitialFunds());
            Assert.Equal(11000.0, module.GetTotalEarnings()); // milestone 8k + recovery 3k
            Assert.Equal(5000.0, module.GetTotalCommittedSpendings());
        }

        // ================================================================
        // Design doc 5.12 — Verified scenario: ReservationBlocksOverspending
        // ================================================================

        [Fact]
        public void Scenario_ReservationBlocksOverspending()
        {
            // Seed: 25,000.
            // UT=10:  Vessel A -5,000.   Balance: 20,000.
            // UT=50:  Earn +8,000.       Balance: 28,000.
            // UT=60:  Earn +3,000.       Balance: 31,000.
            // UT=100: Vessel B -15,000.  Balance: 16,000.
            // UT=200: Earn +20,000.      Balance: 36,000.
            // UT=250: Earn +12,000.      Balance: 48,000.
            //
            // Total spendings: 20,000. Total budget: 73,000.
            //
            // Player rewinds to UT=50:
            //   Earnings up to UT=50: 25,000 + 8,000 = 33,000.
            //   All spendings: 20,000.
            //   Available: 13,000.

            var actions = new List<GameAction>
            {
                MakeSeed(0, 25000f),
                MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeFundsEarning(50, 8000f, FundsEarningSource.Milestone),
                MakeFundsEarning(60, 3000f, FundsEarningSource.Recovery, "rec-A"),
                MakeFundsSpending(100, 15000f, FundsSpendingSource.VesselBuild, "rec-B"),
                MakeFundsEarning(200, 20000f, FundsEarningSource.ContractComplete, "rec-B"),
                MakeFundsEarning(250, 12000f, FundsEarningSource.Recovery, "rec-B")
            };

            module.ComputeTotalSpendings(actions);

            // Walk only up to UT=50 to simulate rewind
            // Process: seed, vessel A, earn 8k
            module.ProcessAction(actions[0]); // seed 25000
            module.ProcessAction(actions[1]); // vessel A -5000
            module.ProcessAction(actions[2]); // earn +8000

            // At UT=50: seed=25000, earnings=8000, total spendings=20000
            // Available = 25000 + 8000 - 20000 = 13000
            Assert.Equal(28000.0, module.GetRunningBalance()); // seed + 8k earning - 5k spending
            Assert.Equal(13000.0, module.GetAvailableFunds()); // reservation includes future spendings

            // Can build 10k vessel? Available=13000 >= 10000 → yes
            Assert.True(module.GetAvailableFunds() >= 10000.0);
            // Can build 20k vessel? Available=13000 < 20000 → no
            Assert.False(module.GetAvailableFunds() >= 20000.0);
        }

        // ================================================================
        // Design doc 5.12 — Verified scenario: FacilityAndHireInterleaved
        // ================================================================

        [Fact]
        public void Scenario_FacilityAndHireInterleaved()
        {
            // Seed: 50,000.
            // UT=50:  Vessel A -20,000.     Balance: 30,000.
            // UT=100: Earn +30,000.         Balance: 60,000.
            // UT=200: Vessel B -15,000.     Balance: 45,000.
            // UT=300: Earn +25,000.         Balance: 70,000.
            // UT=400: Vessel C -30,000.     Balance: 40,000.
            // UT=500: Earn +40,000.         Balance: 80,000.
            // UT=600: Hire kerbal -25,000.  Balance: 55,000.
            // UT=700: Facility -35,000.     Balance: 20,000.
            //
            // Walk: all balances non-negative.
            // Total spendings: 125,000. Total budget: 195,000.
            // Available at UT=0: 50,000 - 125,000 = -75,000 → 0.
            // Available at UT=500: 170,000 - 125,000 = 45,000.

            var actions = new List<GameAction>
            {
                MakeSeed(0, 50000f),
                MakeFundsSpending(50, 20000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeFundsEarning(100, 30000f, FundsEarningSource.ContractComplete, "rec-A"),
                MakeFundsSpending(200, 15000f, FundsSpendingSource.VesselBuild, "rec-B"),
                MakeFundsEarning(300, 25000f, FundsEarningSource.Recovery, "rec-B"),
                MakeFundsSpending(400, 30000f, FundsSpendingSource.VesselBuild, "rec-C"),
                MakeFundsEarning(500, 40000f, FundsEarningSource.ContractComplete, "rec-C"),
                MakeFundsSpending(600, 25000f, FundsSpendingSource.KerbalHire),
                MakeFundsSpending(700, 35000f, FundsSpendingSource.FacilityUpgrade)
            };

            module.ComputeTotalSpendings(actions);

            // Process all actions
            foreach (var a in actions)
                module.ProcessAction(a);

            Assert.Equal(20000.0, module.GetRunningBalance());
            Assert.Equal(125000.0, module.GetTotalCommittedSpendings());
            Assert.Equal(95000.0, module.GetTotalEarnings()); // 30k + 25k + 40k

            // All spendings should be affordable during the walk
            Assert.True(((GameAction)actions[1]).Affordable); // vessel A
            Assert.True(((GameAction)actions[3]).Affordable); // vessel B
            Assert.True(((GameAction)actions[5]).Affordable); // vessel C
            Assert.True(((GameAction)actions[7]).Affordable); // hire
            Assert.True(((GameAction)actions[8]).Affordable); // facility

            // Available at end = seed (50000) + earnings (95000) - all spendings (125000) = 20000
            Assert.Equal(20000.0, module.GetAvailableFunds());
        }

        [Fact]
        public void Scenario_FacilityAndHireInterleaved_AvailableAtUT0()
        {
            // Same timeline, but check available at UT=0 (before any earnings)
            var actions = new List<GameAction>
            {
                MakeSeed(0, 50000f),
                MakeFundsSpending(50, 20000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeFundsEarning(100, 30000f, FundsEarningSource.ContractComplete, "rec-A"),
                MakeFundsSpending(200, 15000f, FundsSpendingSource.VesselBuild, "rec-B"),
                MakeFundsEarning(300, 25000f, FundsEarningSource.Recovery, "rec-B"),
                MakeFundsSpending(400, 30000f, FundsSpendingSource.VesselBuild, "rec-C"),
                MakeFundsEarning(500, 40000f, FundsEarningSource.ContractComplete, "rec-C"),
                MakeFundsSpending(600, 25000f, FundsSpendingSource.KerbalHire),
                MakeFundsSpending(700, 35000f, FundsSpendingSource.FacilityUpgrade)
            };

            module.ComputeTotalSpendings(actions);

            // Only process seed
            module.ProcessAction(actions[0]);

            // Available at UT=0 = seed (50000) + 0 earnings - 125000 spendings = -75000 → 0
            Assert.Equal(0.0, module.GetAvailableFunds());
        }

        [Fact]
        public void Scenario_FacilityAndHireInterleaved_AvailableAtUT500()
        {
            // Same timeline, check available after UT=500 earning
            var actions = new List<GameAction>
            {
                MakeSeed(0, 50000f),
                MakeFundsSpending(50, 20000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeFundsEarning(100, 30000f, FundsEarningSource.ContractComplete, "rec-A"),
                MakeFundsSpending(200, 15000f, FundsSpendingSource.VesselBuild, "rec-B"),
                MakeFundsEarning(300, 25000f, FundsEarningSource.Recovery, "rec-B"),
                MakeFundsSpending(400, 30000f, FundsSpendingSource.VesselBuild, "rec-C"),
                MakeFundsEarning(500, 40000f, FundsEarningSource.ContractComplete, "rec-C"),
                MakeFundsSpending(600, 25000f, FundsSpendingSource.KerbalHire),
                MakeFundsSpending(700, 35000f, FundsSpendingSource.FacilityUpgrade)
            };

            module.ComputeTotalSpendings(actions);

            // Walk up through UT=500 earning (indices 0-6)
            for (int i = 0; i <= 6; i++)
                module.ProcessAction(actions[i]);

            // At UT=500: seed=50000, earnings(30k+25k+40k)=95000, total all spendings=125000
            // Available = 50000 + 95000 - 125000 = 20000
            // But also note that spendings at UT=600 and UT=700 haven't been walked yet,
            // so runningBalance should be 80000 at this point
            Assert.Equal(80000.0, module.GetRunningBalance());
            // Available is based on total committed (all spendings) vs cumulative earnings
            Assert.Equal(20000.0, module.GetAvailableFunds());
        }

        // ================================================================
        // Design doc 5.12 — Verified scenario: NewRecordingExpandsBudget
        // ================================================================

        [Fact]
        public void Scenario_NewRecordingExpandsBudget()
        {
            // Seed: 25,000. Existing timeline:
            //   UT=100: Earn +100,000. UT=500: Vessel -40,000. UT=1500: Facility -50,000.
            //   Total spendings: 90,000. Budget at UT=200: 125,000. Available at UT=200: 35,000.
            //
            // Player rewinds to UT=200, commits rec_B (earns 30k contract, vessel costs 15k):
            //   New spendings total: 105,000. New budget at UT=300: 155,000.
            //   Available at UT=300: 155,000 - 105,000 = 50,000.

            // --- Phase 1: existing timeline ---
            var phase1Actions = new List<GameAction>
            {
                MakeSeed(0, 25000f),
                MakeFundsEarning(100, 100000f, FundsEarningSource.ContractComplete, "rec-A"),
                MakeFundsSpending(500, 40000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeFundsSpending(1500, 50000f, FundsSpendingSource.FacilityUpgrade)
            };

            module.ComputeTotalSpendings(phase1Actions);
            foreach (var a in phase1Actions)
                module.ProcessAction(a);

            // Budget at end = 25000 + 100000 - 40000 - 50000 = 35000
            Assert.Equal(35000.0, module.GetRunningBalance());
            // Available = 25000 + 100000 - 90000 = 35000
            Assert.Equal(35000.0, module.GetAvailableFunds());

            // --- Phase 2: add rec_B at UT=200, full recalculation ---
            module.Reset();

            var phase2Actions = new List<GameAction>
            {
                MakeSeed(0, 25000f),
                MakeFundsEarning(100, 100000f, FundsEarningSource.ContractComplete, "rec-A"),
                MakeFundsSpending(200, 15000f, FundsSpendingSource.VesselBuild, "rec-B"),
                MakeFundsEarning(300, 30000f, FundsEarningSource.ContractComplete, "rec-B"),
                MakeFundsSpending(500, 40000f, FundsSpendingSource.VesselBuild, "rec-A"),
                MakeFundsSpending(1500, 50000f, FundsSpendingSource.FacilityUpgrade)
            };

            module.ComputeTotalSpendings(phase2Actions);
            foreach (var a in phase2Actions)
                module.ProcessAction(a);

            // New total spendings: 15000 + 40000 + 50000 = 105000
            Assert.Equal(105000.0, module.GetTotalCommittedSpendings());
            // New total earnings: 100000 + 30000 = 130000
            Assert.Equal(130000.0, module.GetTotalEarnings());
            // Running balance: 25000 + 100000 - 15000 + 30000 - 40000 - 50000 = 50000
            Assert.Equal(50000.0, module.GetRunningBalance());
            // Available: 25000 + 130000 - 105000 = 50000
            Assert.Equal(50000.0, module.GetAvailableFunds());
        }

        // ================================================================
        // Mixed earning source tests
        // ================================================================

        [Fact]
        public void MixedEarnings_MilestonePlusContractPlusRecovery()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeMilestone(10, "FirstLaunch", 5000f, effective: true));
            module.ProcessAction(MakeContractAccept(20, "c-1", 2000f));
            module.ProcessAction(MakeContractComplete(30, "c-1", 10000f, effective: true));
            module.ProcessAction(MakeFundsEarning(40, 3000f, FundsEarningSource.Recovery, "rec-A"));

            // Balance: 25000 + 5000 + 2000 + 10000 + 3000 = 45000
            Assert.Equal(45000.0, module.GetRunningBalance());
            // Earnings: 5000 + 2000 + 10000 + 3000 = 20000
            Assert.Equal(20000.0, module.GetTotalEarnings());
        }

        [Fact]
        public void ContractFailPenalty_DeductsFromBalance()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractFail(100, "c-1", 5000f));

            Assert.Equal(20000.0, module.GetRunningBalance());
        }

        [Fact]
        public void ContractCancelPenalty_DeductsFromBalance()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            module.ProcessAction(MakeContractCancel(100, "c-1", 3000f));

            Assert.Equal(22000.0, module.GetRunningBalance());
        }

        // ================================================================
        // Null handling
        // ================================================================

        [Fact]
        public void ProcessAction_NullAction_NoThrow()
        {
            module.ProcessAction(null);
            Assert.Equal(0.0, module.GetRunningBalance());
        }

        [Fact]
        public void ProcessAction_UnrelatedType_Ignored()
        {
            var scienceEarning = new GameAction
            {
                UT = 100,
                Type = GameActionType.ScienceEarning,
                ScienceAwarded = 50f
            };

            module.ProcessAction(scienceEarning);

            Assert.Equal(0.0, module.GetRunningBalance());
            Assert.Equal(0.0, module.GetTotalEarnings());
        }

        // ================================================================
        // Log assertion tests
        // ================================================================

        [Fact]
        public void FundsEarning_LogsAmount()
        {
            module.ProcessAction(MakeFundsEarning(100, 8000f, FundsEarningSource.Recovery, "rec-A"));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("FundsEarning") && l.Contains("8000"));
        }

        [Fact]
        public void FundsSpending_Affordable_LogsAmount()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            logLines.Clear();

            module.ProcessAction(MakeFundsSpending(10, 5000f, FundsSpendingSource.VesselBuild));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("FundsSpending") && l.Contains("5000") && l.Contains("affordable=true"));
        }

        [Fact]
        public void FundsSpending_ZeroCost_DoesNotLogVerboseSpend()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            logLines.Clear();

            var spending = MakeFundsSpending(10, 0f, FundsSpendingSource.Other);
            module.ProcessAction(spending);

            Assert.True(spending.Affordable);
            Assert.Equal(25000.0, module.GetRunningBalance());
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Funds]") && l.Contains("FundsSpending:"));
        }

        [Fact]
        public void MilestoneEarning_LogsMilestoneId()
        {
            module.ProcessAction(MakeMilestone(50, "FirstLaunch", 8000f, effective: true));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("Milestone") && l.Contains("FirstLaunch"));
        }

        [Fact]
        public void MilestoneEarning_SameActionRecalculated_RateLimitedToOneLine()
        {
            // Bug #593 (P2 follow-up): the rate-limit key is the stable
            // GameAction.ActionId, not (milestoneId, recordingId). The
            // intended invariant is "recalculating the SAME action collapses
            // its log line"; distinct actions with the same milestoneId/
            // recordingId but different UT or reward must still log on their
            // first walk because they are real, separate effective hits.
            //
            // This test pins the collapsing case: a single action object
            // re-walked 100 times must emit at most one log line.
            var action = MakeMilestone(
                50.0, "RecordsSpeed", 5000f,
                recordingId: "rec-pair-1", effective: true);

            for (int i = 0; i < 100; i++)
            {
                module.ProcessAction(action);
            }

            int speedLines = logLines.Count(l =>
                l.Contains("[Funds]") &&
                l.Contains("Milestone funds:") &&
                l.Contains("RecordsSpeed") &&
                l.Contains("rec-pair-1"));
            Assert.Equal(1, speedLines);
        }

        [Fact]
        public void MilestoneEarning_DistinctActionsSameMilestoneAndRecording_LogSeparately()
        {
            // Bug #593 (P2 follow-up): distinct actions sharing the same
            // (milestoneId, recordingId) but with different UT or reward
            // values MUST log separately on their first walk. Suppressing
            // them would silently drop a real second resource application.
            //
            // Two distinct RecordsSpeed grants for rec-A at UT=50 and UT=80
            // (different ActionId) plus a third grant at UT=80 with a
            // different reward should produce three distinct log lines.
            var grantA = MakeMilestone(50.0, "RecordsSpeed", 5000f,
                recordingId: "rec-A", effective: true);
            var grantB = MakeMilestone(80.0, "RecordsSpeed", 5000f,
                recordingId: "rec-A", effective: true);
            var grantC = MakeMilestone(80.0, "RecordsSpeed", 7500f,
                recordingId: "rec-A", effective: true);

            // Sanity: GameAction.ActionId auto-assigns to a new GUID for each
            // construction, so the three actions are genuinely distinct
            // identities even though milestoneId and recordingId match.
            Assert.NotEqual(grantA.ActionId, grantB.ActionId);
            Assert.NotEqual(grantB.ActionId, grantC.ActionId);
            Assert.NotEqual(grantA.ActionId, grantC.ActionId);

            module.ProcessAction(grantA);
            module.ProcessAction(grantB);
            module.ProcessAction(grantC);

            int speedLines = logLines.Count(l =>
                l.Contains("[Funds]") &&
                l.Contains("Milestone funds:") &&
                l.Contains("RecordsSpeed") &&
                l.Contains("rec-A"));
            Assert.Equal(3, speedLines);
        }

        [Fact]
        public void MilestoneEarning_NullRecordingId_StillKeysOnActionId()
        {
            // Standalone/KSC-path milestones can have a null RecordingId.
            // The earlier (milestoneId, recordingId) key collapsed two
            // distinct null-recording grants into one log line; the
            // ActionId-keyed gate must keep them separate.
            var grant1 = MakeMilestone(50.0, "RecordsAltitude", 4800f,
                recordingId: null, effective: true);
            var grant2 = MakeMilestone(75.0, "RecordsAltitude", 5200f,
                recordingId: null, effective: true);
            Assert.NotEqual(grant1.ActionId, grant2.ActionId);

            module.ProcessAction(grant1);
            module.ProcessAction(grant2);

            int lines = logLines.Count(l =>
                l.Contains("[Funds]") &&
                l.Contains("Milestone funds:") &&
                l.Contains("RecordsAltitude"));
            Assert.Equal(2, lines);
        }

        [Fact]
        public void ContractAccept_LogsAdvance()
        {
            module.ProcessAction(MakeContractAccept(100, "c-1", 5000f));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("ContractAccept") && l.Contains("5000"));
        }

        [Fact]
        public void ContractComplete_LogsReward()
        {
            module.ProcessAction(MakeContractComplete(200, "c-1", 15000f, effective: true));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("ContractComplete") && l.Contains("15000"));
        }

        [Fact]
        public void ContractFail_LogsPenalty()
        {
            module.ProcessAction(MakeContractFail(100, "c-1", 5000f));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("ContractFail") && l.Contains("5000"));
        }

        [Fact]
        public void ContractCancel_LogsPenalty()
        {
            module.ProcessAction(MakeContractCancel(100, "c-1", 3000f));

            Assert.Contains(logLines, l =>
                l.Contains("[Funds]") && l.Contains("ContractCancel") && l.Contains("3000"));
        }

        // ================================================================
        // HasSeed tests
        // ================================================================

        [Fact]
        public void HasSeed_FalseBeforeAnyAction()
        {
            Assert.False(module.HasSeed);
        }

        [Fact]
        public void HasSeed_TrueAfterFundsInitial()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            Assert.True(module.HasSeed);
        }

        [Fact]
        public void HasSeed_FalseAfterReset()
        {
            module.ProcessAction(MakeSeed(0, 25000f));
            Assert.True(module.HasSeed);
            module.Reset();
            Assert.False(module.HasSeed);
        }

        [Fact]
        public void HasSeed_FalseWithOnlyEarnings()
        {
            // Earnings without a seed should not set HasSeed
            module.ProcessAction(MakeFundsEarning(10, 5000f, FundsEarningSource.Recovery, "r1"));
            Assert.False(module.HasSeed);
        }
    }
}
