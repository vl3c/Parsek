using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecalculationEngineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecalculationEngineTests()
        {
            RecalculationEngine.ClearModules();
            RecalculationEngine.ResetSortActionsCallCountForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            RecalculationEngine.ResetSortActionsCallCountForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Test helper — simple module that records all calls
        // ================================================================

        private class TestModule : IResourceModule
        {
            public int ResetCount;
            public int PrePassCount;
            public List<double?> PrePassWalkNowUTs = new List<double?>();
            public List<GameAction> ProcessedActions = new List<GameAction>();

            public void Reset()
            {
                ResetCount++;
            }

            public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
            {
                PrePassCount++;
                PrePassWalkNowUTs.Add(walkNowUT);
                return false;
            }

            public void ProcessAction(GameAction action)
            {
                ProcessedActions.Add(action);
            }

            public int PostWalkCount;
            public void PostWalk()
            {
                PostWalkCount++;
            }
        }

        private sealed class AllocationProbeModule : IResourceModule
        {
            public void Reset() { }
            public bool PrePass(List<GameAction> actions, double? walkNowUT = null) { return false; }
            public void ProcessAction(GameAction action) { }
            public void PostWalk() { }
        }

        private static bool TryGetAllocatedBytesForCurrentThread(out long allocatedBytes)
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (method != null)
            {
                allocatedBytes = (long)method.Invoke(null, null);
                return true;
            }

            allocatedBytes = 0;
            return false;
        }

        // ================================================================
        // SortActions — UT ordering
        // ================================================================

        [Fact]
        public void SortActions_UTAscending()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 300.0, Type = GameActionType.ScienceEarning },
                new GameAction { UT = 100.0, Type = GameActionType.ScienceEarning },
                new GameAction { UT = 200.0, Type = GameActionType.ScienceEarning }
            };

            var sorted = RecalculationEngine.SortActions(actions);

            Assert.Equal(100.0, sorted[0].UT);
            Assert.Equal(200.0, sorted[1].UT);
            Assert.Equal(300.0, sorted[2].UT);
        }

        // ================================================================
        // SortActions — earnings before spendings at same UT
        // ================================================================

        [Fact]
        public void SortActions_EarningsBeforeSpendings()
        {
            var spending = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceSpending,
                NodeId = "survivability"
            };
            var earning = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLanded"
            };

            var actions = new List<GameAction> { spending, earning };
            var sorted = RecalculationEngine.SortActions(actions);

            Assert.Same(earning, sorted[0]);
            Assert.Same(spending, sorted[1]);
        }

        [Fact]
        public void SortActions_FundsEarningBeforeFundsSpending()
        {
            var spending = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 5000f
            };
            var earning = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 10000f
            };

            var actions = new List<GameAction> { spending, earning };
            var sorted = RecalculationEngine.SortActions(actions);

            Assert.Same(earning, sorted[0]);
            Assert.Same(spending, sorted[1]);
        }

        [Fact]
        public void SortActions_MilestoneBeforeFacilityUpgrade()
        {
            var upgrade = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "LaunchPad"
            };
            var milestone = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstLaunch"
            };

            var actions = new List<GameAction> { upgrade, milestone };
            var sorted = RecalculationEngine.SortActions(actions);

            Assert.Same(milestone, sorted[0]);
            Assert.Same(upgrade, sorted[1]);
        }

        // ================================================================
        // SortActions — sequence breaks ties
        // ================================================================

        [Fact]
        public void SortActions_SequenceBreaksTies()
        {
            var second = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceSpending,
                Sequence = 1,
                NodeId = "stability"
            };
            var first = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceSpending,
                Sequence = 0,
                NodeId = "survivability"
            };

            var actions = new List<GameAction> { second, first };
            var sorted = RecalculationEngine.SortActions(actions);

            Assert.Same(first, sorted[0]);
            Assert.Same(second, sorted[1]);
        }

        // ================================================================
        // SortActions — edge cases
        // ================================================================

        [Fact]
        public void SortActions_EmptyList_NoException()
        {
            var sorted = RecalculationEngine.SortActions(new List<GameAction>());

            Assert.NotNull(sorted);
            Assert.Empty(sorted);
        }

        [Fact]
        public void SortActions_NullList_ReturnsEmpty()
        {
            var sorted = RecalculationEngine.SortActions(null);

            Assert.NotNull(sorted);
            Assert.Empty(sorted);
        }

        [Fact]
        public void SortActions_SingleAction_NoChange()
        {
            var action = new GameAction { UT = 17000.0, Type = GameActionType.FundsInitial };
            var sorted = RecalculationEngine.SortActions(new List<GameAction> { action });

            Assert.Single(sorted);
            Assert.Same(action, sorted[0]);
        }

        [Fact]
        public void SortActions_StableSort_PreservesInsertionOrder()
        {
            // Two earnings at the same UT with the same sequence — stable sort preserves input order
            var first = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceEarning,
                Sequence = 0,
                SubjectId = "first"
            };
            var second = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceEarning,
                Sequence = 0,
                SubjectId = "second"
            };

            var actions = new List<GameAction> { first, second };
            var sorted = RecalculationEngine.SortActions(actions);

            Assert.Same(first, sorted[0]);
            Assert.Same(second, sorted[1]);
        }

        [Fact]
        public void SortActions_DoesNotModifyInput()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 300.0, Type = GameActionType.ScienceEarning },
                new GameAction { UT = 100.0, Type = GameActionType.ScienceEarning }
            };

            var originalFirst = actions[0];
            RecalculationEngine.SortActions(actions);

            // Input list should not be reordered
            Assert.Same(originalFirst, actions[0]);
            Assert.Equal(300.0, actions[0].UT);
        }

        // ================================================================
        // SortActions — full three-level sort
        // ================================================================

        [Fact]
        public void SortActions_FullThreeLevelSort()
        {
            // UT=200, spending, seq=1
            var a = new GameAction { UT = 200.0, Type = GameActionType.ScienceSpending, Sequence = 1, NodeId = "A" };
            // UT=100, earning
            var b = new GameAction { UT = 100.0, Type = GameActionType.FundsEarning, SubjectId = "B" };
            // UT=200, earning
            var c = new GameAction { UT = 200.0, Type = GameActionType.ScienceEarning, SubjectId = "C" };
            // UT=200, spending, seq=0
            var d = new GameAction { UT = 200.0, Type = GameActionType.ScienceSpending, Sequence = 0, NodeId = "D" };
            // UT=300, spending
            var e = new GameAction { UT = 300.0, Type = GameActionType.FundsSpending, FundsSpent = 100f };

            var actions = new List<GameAction> { a, b, c, d, e };
            var sorted = RecalculationEngine.SortActions(actions);

            // Expected order:
            // UT=100 earning (b)
            // UT=200 earning (c)
            // UT=200 spending seq=0 (d)
            // UT=200 spending seq=1 (a)
            // UT=300 spending (e)
            Assert.Same(b, sorted[0]);
            Assert.Same(c, sorted[1]);
            Assert.Same(d, sorted[2]);
            Assert.Same(a, sorted[3]);
            Assert.Same(e, sorted[4]);
        }

        // ================================================================
        // IsEarningType
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceEarning)]
        [InlineData(GameActionType.FundsEarning)]
        [InlineData(GameActionType.MilestoneAchievement)]
        [InlineData(GameActionType.ContractComplete)]
        [InlineData(GameActionType.ReputationEarning)]
        [InlineData(GameActionType.KerbalRescue)]
        [InlineData(GameActionType.FundsInitial)]
        public void IsEarningType_AllEarnings(GameActionType type)
        {
            Assert.True(RecalculationEngine.IsEarningType(type));
        }

        [Theory]
        [InlineData(GameActionType.ScienceSpending)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.FacilityUpgrade)]
        [InlineData(GameActionType.FacilityRepair)]
        [InlineData(GameActionType.KerbalHire)]
        [InlineData(GameActionType.StrategyActivate)]
        public void IsEarningType_SpendingsReturnFalse(GameActionType type)
        {
            Assert.False(RecalculationEngine.IsEarningType(type));
        }

        // ================================================================
        // IsSpendingType
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceSpending)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.FacilityUpgrade)]
        [InlineData(GameActionType.FacilityRepair)]
        [InlineData(GameActionType.KerbalHire)]
        [InlineData(GameActionType.StrategyActivate)]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void IsSpendingType_AllSpendings(GameActionType type)
        {
            Assert.True(RecalculationEngine.IsSpendingType(type));
        }

        [Theory]
        [InlineData(GameActionType.ScienceEarning)]
        [InlineData(GameActionType.FundsEarning)]
        [InlineData(GameActionType.MilestoneAchievement)]
        [InlineData(GameActionType.ContractComplete)]
        [InlineData(GameActionType.ReputationEarning)]
        [InlineData(GameActionType.KerbalRescue)]
        [InlineData(GameActionType.FundsInitial)]
        public void IsSpendingType_EarningsReturnFalse(GameActionType type)
        {
            Assert.False(RecalculationEngine.IsSpendingType(type));
        }

        // ================================================================
        // Neutral types — neither earning nor spending
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ContractAccept)]
        [InlineData(GameActionType.ReputationPenalty)]
        [InlineData(GameActionType.KerbalAssignment)]
        [InlineData(GameActionType.KerbalStandIn)]
        [InlineData(GameActionType.FacilityDestruction)]
        [InlineData(GameActionType.StrategyDeactivate)]
        public void NeutralTypes_NeitherEarningNorSpending(GameActionType type)
        {
            Assert.False(RecalculationEngine.IsEarningType(type));
            Assert.False(RecalculationEngine.IsSpendingType(type));
        }

        // ================================================================
        // Recalculate — module lifecycle
        // ================================================================

        [Fact]
        public void PrePassNoInjection_SkipsSecondSort()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 200.0, Type = GameActionType.FundsEarning, FundsAwarded = 25f },
                new GameAction { UT = 100.0, Type = GameActionType.ScienceEarning, ScienceAwarded = 5f }
            };

            RecalculationEngine.ResetSortActionsCallCountForTesting();
            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, RecalculationEngine.SortActionsCallCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") &&
                l.Contains("PrePass stable action list; skipped second sort"));
        }

        [Fact]
        public void PrePassWithInjection_DoesSecondSort()
        {
            var contracts = new ContractsModule();
            var capture = new TestModule();
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(capture, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.ContractAccept,
                    ContractId = "deadline-contract",
                    DeadlineUT = 150f,
                    FundsPenalty = 300f,
                    RepPenalty = 2f
                },
                new GameAction { UT = 200.0, Type = GameActionType.FundsEarning, FundsAwarded = 1f }
            };

            RecalculationEngine.ResetSortActionsCallCountForTesting();
            RecalculationEngine.Recalculate(actions);

            Assert.Equal(2, RecalculationEngine.SortActionsCallCountForTesting);
            Assert.Contains(capture.ProcessedActions, a =>
                a.Type == GameActionType.ContractFail &&
                a.ContractId == "deadline-contract" &&
                Math.Abs(a.UT - 150.0) < 0.001);
        }

        [Fact]
        public void CutoffWalk_AllocationsBounded()
        {
            RecalculationEngine.RegisterModule(new AllocationProbeModule(), RecalculationEngine.ModuleTier.FirstTier);
            var actions = new List<GameAction>();

            bool previousSuppress = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            try
            {
                for (int i = 0; i < 10; i++)
                    RecalculationEngine.Recalculate(actions, 15.0);

                long before;
                if (!TryGetAllocatedBytesForCurrentThread(out before))
                {
                    Console.WriteLine(
                        "CutoffWalk_AllocationsBounded skipped: GC.GetAllocatedBytesForCurrentThread unavailable.");
                    return;
                }

                const int iterations = 100;
                for (int i = 0; i < iterations; i++)
                    RecalculationEngine.Recalculate(actions, 15.0);
                long after;
                Assert.True(TryGetAllocatedBytesForCurrentThread(out after));

                long bytesPerWalk = (after - before) / iterations;
                Assert.True(bytesPerWalk < 4 * 1024,
                    $"Expected cutoff walk allocation below 4 KB, got {bytesPerWalk} bytes per walk.");
            }
            finally
            {
                ParsekLog.SuppressLogging = previousSuppress;
            }
        }

        [Fact]
        public void Recalculate_CallsResetOnAllModules()
        {
            var firstTier = new TestModule();
            var secondTier = new TestModule();
            var strategy = new TestModule();
            var facilities = new TestModule();

            RecalculationEngine.RegisterModule(firstTier, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(secondTier, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(strategy, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.RegisterModule(facilities, RecalculationEngine.ModuleTier.Facilities);

            RecalculationEngine.Recalculate(new List<GameAction>());

            Assert.Equal(1, firstTier.ResetCount);
            Assert.Equal(1, secondTier.ResetCount);
            Assert.Equal(1, strategy.ResetCount);
            Assert.Equal(1, facilities.ResetCount);
        }

        [Fact]
        public void Recalculate_DispatchesToFirstTierModule()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.ScienceEarning },
                new GameAction { UT = 17100.0, Type = GameActionType.ScienceSpending }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, module.ResetCount);
            Assert.Equal(2, module.ProcessedActions.Count);
            Assert.Equal(GameActionType.ScienceEarning, module.ProcessedActions[0].Type);
            Assert.Equal(GameActionType.ScienceSpending, module.ProcessedActions[1].Type);
        }

        [Fact]
        public void Recalculate_DispatchesToSecondTierModule()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.FundsEarning, FundsAwarded = 5000f }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, module.ResetCount);
            Assert.Single(module.ProcessedActions);
            Assert.Equal(GameActionType.FundsEarning, module.ProcessedActions[0].Type);
        }

        [Fact]
        public void Recalculate_DispatchesToStrategyModule()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.Strategy);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.StrategyActivate, StrategyId = "S1" }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, module.ResetCount);
            Assert.Single(module.ProcessedActions);
        }

        [Fact]
        public void Recalculate_DispatchesToFacilitiesModule()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.Facilities);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.FacilityUpgrade, FacilityId = "LaunchPad" }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, module.ResetCount);
            Assert.Single(module.ProcessedActions);
        }

        [Fact]
        public void Recalculate_DispatchesInSortedOrder()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 300.0, Type = GameActionType.ScienceEarning, SubjectId = "third" },
                new GameAction { UT = 100.0, Type = GameActionType.ScienceEarning, SubjectId = "first" },
                new GameAction { UT = 200.0, Type = GameActionType.ScienceEarning, SubjectId = "second" }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(100.0, module.ProcessedActions[0].UT);
            Assert.Equal(200.0, module.ProcessedActions[1].UT);
            Assert.Equal(300.0, module.ProcessedActions[2].UT);
        }

        [Fact]
        public void Recalculate_AllTiersReceiveEveryAction()
        {
            var firstTier = new TestModule();
            var secondTier = new TestModule();
            var strategy = new TestModule();
            var facilities = new TestModule();

            RecalculationEngine.RegisterModule(firstTier, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(secondTier, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(strategy, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.RegisterModule(facilities, RecalculationEngine.ModuleTier.Facilities);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.ScienceEarning },
                new GameAction { UT = 17100.0, Type = GameActionType.FundsSpending }
            };

            RecalculationEngine.Recalculate(actions);

            // Every module receives every action — modules decide internally what to process
            Assert.Equal(2, firstTier.ProcessedActions.Count);
            Assert.Equal(2, secondTier.ProcessedActions.Count);
            Assert.Equal(2, strategy.ProcessedActions.Count);
            Assert.Equal(2, facilities.ProcessedActions.Count);
        }

        [Fact]
        public void Recalculate_MultipleFirstTierModules()
        {
            var moduleA = new TestModule();
            var moduleB = new TestModule();
            RecalculationEngine.RegisterModule(moduleA, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(moduleB, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.ScienceEarning }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, moduleA.ResetCount);
            Assert.Equal(1, moduleB.ResetCount);
            Assert.Single(moduleA.ProcessedActions);
            Assert.Single(moduleB.ProcessedActions);
        }

        // ================================================================
        // Recalculate — empty and null
        // ================================================================

        [Fact]
        public void Recalculate_EmptyActions_NoException()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            RecalculationEngine.Recalculate(new List<GameAction>());

            // Reset is called even with no actions
            Assert.Equal(1, module.ResetCount);
            Assert.Empty(module.ProcessedActions);
        }

        [Fact]
        public void Recalculate_NullActions_LogsWarning()
        {
            RecalculationEngine.Recalculate(null);

            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") && l.Contains("null actions"));
        }

        [Fact]
        public void Recalculate_NoModulesRegistered_NoException()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.ScienceEarning }
            };

            // Should not throw even with no modules registered
            RecalculationEngine.Recalculate(actions);

            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") && l.Contains("Recalculate complete"));
        }

        // ================================================================
        // Logging
        // ================================================================

        [Fact]
        public void Recalculate_LogsSummary()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 17000.0, Type = GameActionType.ScienceEarning },
                new GameAction { UT = 17100.0, Type = GameActionType.FundsEarning }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") &&
                l.Contains("Recalculate complete") &&
                l.Contains("actionsTotal=2"));
        }

        [Fact]
        public void Recalculate_RepeatedNoopSummary_LogsOnceUntilStateChanges()
        {
            var actions = new List<GameAction>();

            RecalculationEngine.Recalculate(actions);
            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, logLines.Count(l =>
                l.Contains("[RecalcEngine]") &&
                l.Contains("Recalculate complete") &&
                l.Contains("actionsTotal=0")));

            actions.Add(new GameAction { UT = 10.0, Type = GameActionType.ScienceEarning });
            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, logLines.Count(l =>
                l.Contains("[RecalcEngine]") &&
                l.Contains("Recalculate complete") &&
                l.Contains("actionsTotal=1")));
        }

        [Fact]
        public void RegisterModule_LogsRegistration()
        {
            var module = new TestModule();
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.FirstTier);

            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") &&
                l.Contains("Registered first-tier module") &&
                l.Contains("TestModule"));
        }

        [Fact]
        public void RegisterModule_NullModule_LogsWarning()
        {
            RecalculationEngine.RegisterModule(null, RecalculationEngine.ModuleTier.FirstTier);

            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") && l.Contains("null module"));
        }

        [Fact]
        public void ClearModules_LogsRemovalCount()
        {
            logLines.Clear();
            var m1 = new TestModule();
            var m2 = new TestModule();
            RecalculationEngine.RegisterModule(m1, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(m2, RecalculationEngine.ModuleTier.SecondTier);

            logLines.Clear();
            RecalculationEngine.ClearModules();

            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") && l.Contains("ClearModules") && l.Contains("removed=2"));
        }

        // ================================================================
        // SortActions — seed types treated as earnings (D19)
        // ================================================================

        [Fact]
        public void SortActions_SeedTypesAreTreatedAsEarnings()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.ScienceSpending, Sequence = 1 },
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial },
                new GameAction { UT = 0, Type = GameActionType.ReputationInitial },
                new GameAction { UT = 0, Type = GameActionType.FundsInitial }
            };

            var sorted = RecalculationEngine.SortActions(actions);

            // Seed types are earnings → sorted before spendings at same UT
            Assert.True(RecalculationEngine.IsEarningType(sorted[0].Type));
            Assert.True(RecalculationEngine.IsEarningType(sorted[1].Type));
            Assert.True(RecalculationEngine.IsEarningType(sorted[2].Type));
            Assert.Equal(GameActionType.ScienceSpending, sorted[3].Type);
        }
    }
}
