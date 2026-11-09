using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of <c>LedgerOrchestrator.AccumulateEmittedEarningsRow</c>, the per-row
    /// body <c>ComputeEarningsWindowEmittedDeltas</c> was split into (wave 2 W7b). The whole
    /// walk is pinned by <see cref="EarningsWindowEmittedDeltasCharacterizationTests"/>.
    /// Pure: no log, no static state.
    /// </summary>
    public class EmittedEarningsRowTests
    {
        private static readonly List<GameStateEvent> NoEvents = new List<GameStateEvent>();

        private static LedgerOrchestrator.EarningsWindowEmittedDeltas Fresh() =>
            new LedgerOrchestrator.EarningsWindowEmittedDeltas
            {
                EffectiveScienceActions = new List<GameAction>()
            };

        private static LedgerOrchestrator.EarningsWindowEmittedDeltas One(GameAction row,
            IReadOnlyList<GameStateEvent> events = null)
        {
            var d = Fresh();
            LedgerOrchestrator.AccumulateEmittedEarningsRow(ref d, row, events ?? NoEvents, 100.0, 200.0, "rec-a");
            return d;
        }

        [Fact]
        public void FundsEarning_CountsOnlyWhenEffective()
        {
            var row = new GameAction { Type = GameActionType.FundsEarning, FundsAwarded = 5f, RecordingId = "rec-a" };
            Assert.Equal(5.0, One(row).EmittedFundsDelta);
            row.Effective = false;
            Assert.Equal(0.0, One(row).EmittedFundsDelta);
        }

        [Fact]
        public void Spendings_Subtract_RegardlessOfEffective()
        {
            var fs = new GameAction { Type = GameActionType.FundsSpending, FundsSpent = 3f, Effective = false };
            Assert.Equal(-3.0, One(fs).EmittedFundsDelta);
            var ss = new GameAction { Type = GameActionType.ScienceSpending, Cost = 2f, Effective = false };
            Assert.Equal(-2.0, One(ss).EmittedSciDelta);
            var sd = new GameAction { Type = GameActionType.StrategyScienceDebit, Cost = 1.5f };
            Assert.Equal(-1.5, One(sd).EmittedSciDelta);
        }

        [Fact]
        public void ContractComplete_AddsAllThreeTransformedLegs()
        {
            var row = new GameAction
            {
                Type = GameActionType.ContractComplete,
                TransformedFundsReward = 10f,
                EffectiveRep = 2f,
                TransformedScienceReward = 4f,
            };
            var d = One(row);
            Assert.Equal(10.0, d.EmittedFundsDelta);
            Assert.Equal(2.0, d.EmittedRepDelta);
            Assert.Equal(4.0, d.EmittedSciDelta);
        }

        [Fact]
        public void Counters_ForAcceptUpgradeRepair()
        {
            var accept = One(new GameAction { Type = GameActionType.ContractAccept, AdvanceFunds = 7f });
            Assert.Equal(1, accept.ContractAcceptCount);
            Assert.Equal(7.0, accept.EmittedFundsDelta);

            var upgrade = One(new GameAction { Type = GameActionType.FacilityUpgrade, FacilityCost = 9f, Effective = false });
            Assert.Equal(1, upgrade.FacilityUpgradeCount);
            Assert.Equal(-9.0, upgrade.EmittedFundsDelta);

            var freeRepair = One(new GameAction { Type = GameActionType.FacilityRepair, FacilityCost = 9f, Effective = false });
            Assert.Equal(0, freeRepair.FacilityRepairCount);
            Assert.Equal(0.0, freeRepair.EmittedFundsDelta);
        }

        [Fact]
        public void ScienceEarning_ReadsEffectiveScience_AndListsTheRow()
        {
            var row = new GameAction { Type = GameActionType.ScienceEarning, ScienceAwarded = 8f, EffectiveScience = 0f };
            var d = One(row);
            Assert.Equal(0.0, d.EmittedSciDelta);
            Assert.Same(row, Assert.Single(d.EffectiveScienceActions));
        }

        [Fact]
        public void KerbalDeathPenalty_PairedWithItsEvent_IsSkippedAndCounted()
        {
            var row = new GameAction
            {
                Type = GameActionType.ReputationPenalty,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath,
                RecordingId = "rec-a",
                NominalPenalty = 10f,
                EffectiveRep = -10f,
            };
            var paired = new List<GameStateEvent>
            {
                new GameStateEvent
                {
                    ut = 150.0,
                    eventType = GameStateEventType.ReputationChanged,
                    key = KerbalDeathRepPenalty.VesselLossEventKey,
                    valueBefore = 100.0,
                    valueAfter = 90.0,
                    recordingId = "rec-a",
                }
            };
            var d = One(row, paired);
            Assert.Equal(1, d.KerbalDeathPairedSkipped);
            Assert.Equal(0.0, d.EmittedRepDelta);

            var unpaired = One(row);
            Assert.Equal(0, unpaired.KerbalDeathPairedSkipped);
            Assert.Equal(-10.0, unpaired.EmittedRepDelta);
        }

        [Fact]
        public void InsideSeedRow_CountsTheEstimate_NotTheZeroedEffectiveRep()
        {
            var row = new GameAction
            {
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f,
                EffectiveRep = 0f,
                InsideReputationSeed = true,
            };
            var d = One(row);
            Assert.Equal(1, d.InsideSeedRepRowsCounted);
            Assert.True(d.EmittedRepDelta > 0.0);
        }

        [Fact]
        public void Accumulates_AcrossCalls_AndIgnoresUnlistedTypes()
        {
            var d = Fresh();
            LedgerOrchestrator.AccumulateEmittedEarningsRow(ref d,
                new GameAction { Type = GameActionType.FundsEarning, FundsAwarded = 1f }, NoEvents, 0, 1, null);
            LedgerOrchestrator.AccumulateEmittedEarningsRow(ref d,
                new GameAction { Type = GameActionType.FundsEarning, FundsAwarded = 2f }, NoEvents, 0, 1, null);
            LedgerOrchestrator.AccumulateEmittedEarningsRow(ref d,
                new GameAction { Type = GameActionType.FundsInitial, InitialFunds = 1000f }, NoEvents, 0, 1, null);
            Assert.Equal(3.0, d.EmittedFundsDelta);
            Assert.Equal(0.0, d.EmittedRepDelta);
            Assert.Equal(0.0, d.EmittedSciDelta);
        }
    }
}
