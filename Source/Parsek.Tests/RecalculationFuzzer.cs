using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecalculationFuzzerTests : IDisposable
    {
        private const int ExpectedGameActionTypeCount = 23;
        private readonly bool priorSuppressLogging;

        public RecalculationFuzzerTests()
        {
            priorSuppressLogging = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(new ContractsModule(), RecalculationEngine.ModuleTier.FirstTier);
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            RecalculationEngine.ResetSortActionsCallCountForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorSuppressLogging;
        }

        [Fact]
        public void GameActionType_EnumValueCount_Locked()
        {
            Assert.Equal(ExpectedGameActionTypeCount, Enum.GetValues(typeof(GameActionType)).Length);
        }

        [Fact]
        public void DeterministicLedgerFuzzer_PrePassMutationMatchesSecondSortDecision()
        {
            var rng = new Random(0x5EED);
            var seenTypes = new HashSet<GameActionType>();
            for (int iteration = 0; iteration < 10000; iteration++)
            {
                var actions = BuildActions(iteration, rng);
                foreach (var action in actions)
                    seenTypes.Add(action.Type);

                AssertSortMatchesLinq(actions);

                double midTimeline = 500.0;
                double pastEnd = 20000.0;
                double preStart = -1.0;
                double?[] cutoffs = { null, midTimeline, pastEnd, preStart };
                for (int i = 0; i < cutoffs.Length; i++)
                {
                    int expectedSortCalls = ExpectedSortCalls(actions, cutoffs[i]);
                    RecalculationEngine.ResetSortActionsCallCountForTesting();
                    RecalculationEngine.Recalculate(actions, cutoffs[i]);
                    Assert.Equal(expectedSortCalls, RecalculationEngine.SortActionsCallCountForTesting);
                }
            }

            var allTypes = Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>();
            foreach (var type in allTypes)
                Assert.Contains(type, seenTypes);
        }

        private static void AssertSortMatchesLinq(List<GameAction> actions)
        {
            var expected = actions
                .OrderBy(a => a.UT)
                .ThenBy(a => RecalculationEngine.IsEarningType(a.Type) ? 0 : 1)
                .ThenBy(a => a.Sequence)
                .ToList();

            var actual = RecalculationEngine.SortActions(actions);
            Assert.True(expected.SequenceEqual(actual));
        }

        private static int ExpectedSortCalls(List<GameAction> actions, double? cutoff)
        {
            if (!cutoff.HasValue)
                return 1 + (WouldContractsPrePassMutate(SortForPrediction(actions), null) ? 1 : 0);

            var projectionActions = CopyNonNullActions(actions);
            int projectionCalls = 1 + (WouldContractsPrePassMutate(
                SortForPrediction(projectionActions),
                ComputeProjectionHorizon(actions)) ? 1 : 0);

            var effective = new List<GameAction>(actions.Count);
            double cutoffValue = cutoff.Value;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null)
                    continue;
                if (RecalculationEngine.IsSeedType(action.Type) || action.UT <= cutoffValue)
                    effective.Add(action);
            }

            int liveCalls = 1 + (WouldContractsPrePassMutate(
                SortForPrediction(effective),
                cutoffValue) ? 1 : 0);

            return projectionCalls + liveCalls;
        }

        private static bool WouldContractsPrePassMutate(List<GameAction> sorted, double? walkNowUT)
        {
            if (sorted == null || sorted.Count == 0)
                return false;

            var tracked = new Dictionary<string, GameAction>();
            for (int i = 0; i < sorted.Count; i++)
            {
                var action = sorted[i];
                switch (action.Type)
                {
                    case GameActionType.ContractAccept:
                        if (!float.IsNaN(action.DeadlineUT) && action.ContractId != null)
                            tracked[action.ContractId] = action;
                        break;
                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        if (action.ContractId != null)
                            tracked.Remove(action.ContractId);
                        break;
                }
            }

            double nowUT = walkNowUT ?? sorted[sorted.Count - 1].UT;
            foreach (var pair in tracked)
            {
                if (pair.Value.DeadlineUT <= nowUT)
                    return true;
            }

            return false;
        }

        private static List<GameAction> SortForPrediction(List<GameAction> actions)
        {
            return actions
                .OrderBy(a => a.UT)
                .ThenBy(a => RecalculationEngine.IsEarningType(a.Type) ? 0 : 1)
                .ThenBy(a => a.Sequence)
                .ToList();
        }

        private static List<GameAction> CopyNonNullActions(List<GameAction> actions)
        {
            var copy = new List<GameAction>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] != null)
                    copy.Add(actions[i]);
            }

            return copy;
        }

        private static double ComputeProjectionHorizon(List<GameAction> actions)
        {
            double horizon = 0.0;
            bool hasValue = false;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null)
                    continue;

                if (!hasValue || action.UT > horizon)
                {
                    horizon = action.UT;
                    hasValue = true;
                }

                if (action.Type == GameActionType.ContractAccept
                    && !float.IsNaN(action.DeadlineUT)
                    && action.DeadlineUT > horizon)
                {
                    horizon = action.DeadlineUT;
                }
            }

            return horizon;
        }

        private static List<GameAction> BuildActions(int iteration, Random rng)
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 10000f },
                new GameAction { UT = 0.0, Type = GameActionType.ScienceInitial, InitialScience = 50f },
                new GameAction { UT = 0.0, Type = GameActionType.ReputationInitial, InitialReputation = 10f }
            };

            var allTypes = Enum.GetValues(typeof(GameActionType)).Cast<GameActionType>().ToArray();
            for (int i = 0; i < allTypes.Length; i++)
            {
                double ut = 10.0 + rng.Next(0, 1000);
                actions.Add(CreateAction(allTypes[i], ut, i, iteration));
            }

            AddHandSeededScenario(actions, iteration);
            return actions;
        }

        private static void AddHandSeededScenario(List<GameAction> actions, int iteration)
        {
            if ((iteration & 1) == 0)
            {
                actions.Add(new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.ContractAccept,
                    ContractId = "expired-open-" + iteration,
                    DeadlineUT = 150f,
                    FundsPenalty = 250f,
                    RepPenalty = 1f
                });
                actions.Add(new GameAction
                {
                    UT = 200.0,
                    Type = GameActionType.FundsEarning,
                    FundsAwarded = 1f
                });
            }
            else
            {
                string contractId = "resolved-" + iteration;
                actions.Add(new GameAction
                {
                    UT = 100.0,
                    Type = GameActionType.ContractAccept,
                    ContractId = contractId,
                    DeadlineUT = 150f,
                    FundsPenalty = 250f,
                    RepPenalty = 1f
                });
                actions.Add(new GameAction
                {
                    UT = 125.0,
                    Type = GameActionType.ContractComplete,
                    ContractId = contractId,
                    FundsReward = 10f
                });
            }
        }

        private static GameAction CreateAction(GameActionType type, double ut, int sequence, int iteration)
        {
            var action = new GameAction
            {
                UT = ut,
                Type = type,
                Sequence = sequence,
                RecordingId = "rec-" + iteration
            };

            switch (type)
            {
                case GameActionType.ScienceEarning:
                    action.SubjectId = "subject-" + iteration + "-" + sequence;
                    action.ScienceAwarded = 5f;
                    action.SubjectMaxValue = 50f;
                    break;
                case GameActionType.ScienceSpending:
                    action.NodeId = "node-" + iteration;
                    action.Cost = 4f;
                    break;
                case GameActionType.FundsEarning:
                    action.FundsAwarded = 100f;
                    break;
                case GameActionType.FundsSpending:
                    action.FundsSpent = 25f;
                    break;
                case GameActionType.MilestoneAchievement:
                    action.MilestoneId = "milestone-" + iteration + "-" + sequence;
                    action.MilestoneFundsAwarded = 10f;
                    break;
                case GameActionType.ContractAccept:
                    action.ContractId = "generic-contract-" + iteration + "-" + sequence;
                    action.DeadlineUT = float.NaN;
                    action.AdvanceFunds = 5f;
                    action.FundsPenalty = 20f;
                    break;
                case GameActionType.ContractComplete:
                    action.ContractId = "complete-contract-" + iteration + "-" + sequence;
                    action.FundsReward = 15f;
                    action.ScienceReward = 1f;
                    action.RepReward = 1f;
                    break;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    action.ContractId = "terminal-contract-" + iteration + "-" + sequence;
                    action.FundsPenalty = 10f;
                    action.RepPenalty = 1f;
                    break;
                case GameActionType.ReputationEarning:
                    action.NominalRep = 2f;
                    break;
                case GameActionType.ReputationPenalty:
                    action.NominalPenalty = 2f;
                    break;
                case GameActionType.KerbalAssignment:
                    action.KerbalName = "Jebediah Kerman";
                    action.KerbalRole = "Pilot";
                    action.KerbalEndStateField = (iteration % 3 == 0)
                        ? KerbalEndState.Dead
                        : KerbalEndState.Recovered;
                    break;
                case GameActionType.KerbalHire:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                    action.KerbalName = "Kerbal " + iteration;
                    action.KerbalRole = "Engineer";
                    break;
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityDestruction:
                case GameActionType.FacilityRepair:
                    action.FacilityId = "LaunchPad";
                    action.ToLevel = 2;
                    action.FacilityCost = 100f;
                    break;
                case GameActionType.StrategyActivate:
                case GameActionType.StrategyDeactivate:
                    action.StrategyId = "strategy-" + iteration;
                    action.SourceResource = StrategyResource.Funds;
                    action.TargetResource = StrategyResource.Science;
                    action.Commitment = 0.25f;
                    action.SetupCost = 5f;
                    break;
                case GameActionType.FundsInitial:
                    action.InitialFunds = 10000f;
                    break;
                case GameActionType.ScienceInitial:
                    action.InitialScience = 50f;
                    break;
                case GameActionType.ReputationInitial:
                    action.InitialReputation = 10f;
                    break;
            }

            return action;
        }
    }
}
