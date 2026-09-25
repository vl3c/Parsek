using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Verification cells for the stock-UI reservation overlay program
    /// (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md, section 12).
    /// Each cell pins TODAY's behavior of a claim the design doc makes. Cells whose
    /// name ends in "_DocumentsDefect" assert a known-wrong value on purpose: they
    /// are the regression anchor the later fix PR flips, not an endorsement.
    /// </summary>
    [Collection("Sequential")]
    public class StockUiReservationVerificationTests : System.IDisposable
    {
        private const string LaunchPadId = "SpaceCenter/LaunchPad";
        private readonly List<string> logLines = new List<string>();

        public StockUiReservationVerificationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================
        // F1: facility upgrade block keys on facility id only
        // ================================================================

        private static Milestone MakeFacilityUpgradeMilestone(int lastReplayedIndex)
        {
            return new Milestone
            {
                MilestoneId = "m-facility",
                StartUT = 500,
                EndUT = 1500,
                Committed = true,
                LastReplayedEventIndex = lastReplayedIndex,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 1000,
                        eventType = GameStateEventType.FacilityUpgraded,
                        key = LaunchPadId,
                        // KSP normalized levels: 0.0 = tier 1, 0.5 = tier 2.
                        valueBefore = 0.0,
                        valueAfter = 0.5,
                        detail = "cost=75000"
                    }
                }
            };
        }

        [Fact]
        public void F1_FreshlyCreatedMilestone_IsFullyReplayed_DoesNotBlock()
        {
            // CreateMilestone stamps LastReplayedEventIndex = Events.Count - 1, so on the
            // timeline that recorded the upgrade nothing is in the committed slice.
            MilestoneStore.AddMilestoneForTesting(MakeFacilityUpgradeMilestone(lastReplayedIndex: 0));

            Assert.Empty(MilestoneStore.GetCommittedFacilityUpgrades());
        }

        [Fact]
        public void F1_CommittedOneToTwoUpgrade_BlocksEveryLaterUpgradeOfThatFacility_AfterRewind_DocumentsDefect()
        {
            // The committed 1->2 upgrade at UT 1000 was recorded after the rewind point.
            MilestoneStore.AddMilestoneForTesting(MakeFacilityUpgradeMilestone(lastReplayedIndex: 0));

            // Rewind / revert: HandleRewindOnLoad restores MILESTONE_STATE from the
            // rewind quicksave with resetUnmatched=true. The quicksave predates the
            // milestone, so it has no state row for it and the index drops to -1.
            var rewindScenario = new ConfigNode("SCENARIO");
            MilestoneStore.RestoreMutableState(rewindScenario, resetUnmatched: true);
            Assert.Equal(-1, MilestoneStore.Milestones[0].LastReplayedEventIndex);
            Assert.Contains(LaunchPadId, MilestoneStore.GetCommittedFacilityUpgrades());

            // The clock passes UT 1000 and the ledger replays the upgrade (the facility
            // is now level 2). Nothing advances LastReplayedEventIndex: the only writers
            // are CreateMilestone, RestoreMutableState and the removal decrements. The
            // next save writes -1 and every later ordinary load restores -1.
            var laterSave = new ConfigNode("SCENARIO");
            MilestoneStore.SaveMutableState(laterSave);
            MilestoneStore.RestoreMutableState(laterSave);
            Assert.Equal(-1, MilestoneStore.Milestones[0].LastReplayedEventIndex);

            // DEFECT (todo STOCK-UI-RESERVATION-OVERLAYS-2026-09-25, F1): the slice has
            // no UT and no level. FacilityUpgradePatch.TryBlockFacilityUpgrade reads only
            // facility.id against this set (and SetLevel's prefix only checks
            // lvl > current), so a 2->3 upgrade long after UT 1000 is still refused.
            // The fix (a UT-keyed index over the effective ledger) flips this assert.
            Assert.Contains(LaunchPadId, MilestoneStore.GetCommittedFacilityUpgrades());

            // The refusal dialog cites the replayed 1->2 event as the reason.
            var ev = MilestoneStore.FindCommittedEvent(GameStateEventType.FacilityUpgraded, LaunchPadId);
            Assert.True(ev.HasValue);
            Assert.Equal(1000.0, ev.Value.ut);
            Assert.Equal(0.5, ev.Value.valueAfter);
        }

        // ================================================================
        // X1-X3: Cancel now against a committed resolution
        // ================================================================

        private static GameAction FundsSeed(float amount)
        {
            return new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = amount };
        }

        private static GameAction Accept(double ut, string id, float fundsPenalty)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractAccept,
                ContractId = id,
                AdvanceFunds = 0f,
                DeadlineUT = 1.0e6,
                FundsPenalty = fundsPenalty
            };
        }

        private static GameAction Resolution(GameActionType type, double ut, string id, float fundsPenalty)
        {
            return new GameAction { UT = ut, Type = type, ContractId = id, FundsPenalty = fundsPenalty };
        }

        private static List<GameAction> BuildX1Timeline(bool playerCancelsNow,
            out GameAction complete, out GameAction techUnlock, out GameAction vesselBuild)
        {
            complete = new GameAction
            {
                UT = 200.0,
                Type = GameActionType.ContractComplete,
                ContractId = "c-x1",
                RecordingId = "rec-committed",
                FundsReward = 20000f,
                ScienceReward = 10f
            };
            techUnlock = new GameAction
            {
                UT = 300.0,
                Type = GameActionType.ScienceSpending,
                NodeId = "survivability",
                Cost = 10f
            };
            vesselBuild = new GameAction
            {
                UT = 400.0,
                Type = GameActionType.FundsSpending,
                RecordingId = "rec-later",
                FundsSpent = 25000f,
                FundsSpendingSource = FundsSpendingSource.VesselBuild
            };

            var actions = new List<GameAction>
            {
                FundsSeed(10000f),
                Accept(100.0, "c-x1", fundsPenalty: 500f),
                complete,
                techUnlock,
                vesselBuild
            };
            if (playerCancelsNow)
                actions.Add(Resolution(GameActionType.ContractCancel, 150.0, "c-x1", fundsPenalty: 200f));
            return actions;
        }

        private static FundsModule RegisterContractsScienceFunds(out ScienceModule science)
        {
            var contracts = new ContractsModule();
            science = new ScienceModule();
            var funds = new FundsModule();
            // Production order (LedgerOrchestrator.Initialize): contracts before science in
            // the first tier, funds in the second.
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            return funds;
        }

        [Fact]
        public void X1_Control_CommittedCompletionFundsTheLaterTechAndBuild()
        {
            var funds = RegisterContractsScienceFunds(out var science);
            var actions = BuildX1Timeline(false, out var complete, out var tech, out var build);

            RecalculationEngine.Recalculate(actions);

            Assert.True(complete.Effective);
            Assert.True(tech.Affordable);
            Assert.True(build.Affordable);
            Assert.Equal(5000.0, funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void X1_CancelBeforeCommittedCompletion_ZeroesTheCompletionAndRefusesTheLaterTechAndBuild()
        {
            var funds = RegisterContractsScienceFunds(out var science);
            var actions = BuildX1Timeline(true, out var complete, out var tech, out var build);

            RecalculationEngine.Recalculate(actions);

            // The committed completion loses funds, reputation and science.
            Assert.False(complete.Effective);
            Assert.Contains(logLines, l => l.Contains("[Contracts]")
                && l.Contains("c-x1") && l.Contains("effective=false") && l.Contains("explicitly resolved"));

            // Cascade 1: the committed tech unlock it funded is refused.
            Assert.False(tech.Affordable);
            Assert.True(tech.UnaffordableRunningScience.HasValue);
            Assert.Equal(0.0, tech.UnaffordableRunningScience.Value, 3);
            Assert.Contains(logLines, l => l.Contains("[ScienceModule]")
                && l.Contains("Spending NOT affordable") && l.Contains("survivability"));

            // The tech-tree patch therefore never unlocks the node at its UT; it only
            // reaches the re-lock-refusal map (which protects a node ALREADY researched live).
            var baselines = new List<GameStateBaseline> { new GameStateBaseline { ut = 0.0 } };
            baselines[0].researchedTechIds.Add("start");
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                baselines, actions, 500.0, null, out var drops);
            Assert.DoesNotContain("survivability", target);
            Assert.True(drops.ContainsKey("survivability"));

            // Cascade 2: the committed vessel build it funded goes unaffordable.
            Assert.False(build.Affordable);
            Assert.Equal(10000.0 - 200.0 - 25000.0, funds.GetRunningBalance(), 1);
        }

        [Theory]
        [InlineData(GameActionType.ContractFail, 500f)]
        [InlineData(GameActionType.ContractCancel, 300f)]
        public void X2X3_CancelNowPlusCommittedFailOrCancel_ChargesBothPenalties_DocumentsDefect(
            GameActionType committedType, float committedPenalty)
        {
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(new ContractsModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                FundsSeed(10000f),
                Accept(100.0, "c-x2", fundsPenalty: 500f),
                Resolution(GameActionType.ContractCancel, 150.0, "c-x2", fundsPenalty: 200f),
                Resolution(committedType, 300.0, "c-x2", fundsPenalty: committedPenalty)
            };

            RecalculationEngine.Recalculate(actions);

            // DEFECT (section 7.5): FundsModule.ProcessContractPenalty is unconditional,
            // so the contract the player already cancelled is penalized a second time.
            Assert.Equal(10000.0 - 200.0 - committedPenalty, funds.GetRunningBalance(), 1);
        }

        // ================================================================
        // Loop hold: turning a chain's loop off
        // ================================================================

        private static ConfigNode SnapshotWithCrew(string name)
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddNode("PART").AddValue("crew", name);
            return vessel;
        }

        private static Recording AddLoopingChain(string kerbal, KerbalEndState tipEndState,
            TerminalState tipTerminal)
        {
            var loopSegment = new Recording
            {
                RecordingId = "rec-loop-seg",
                ChainId = "chain-loop",
                ChainIndex = 0,
                LoopPlayback = true
            };
            loopSegment.Points.Add(new TrajectoryPoint { ut = 10.0 });
            loopSegment.Points.Add(new TrajectoryPoint { ut = 142.0 });
            RecordingStore.AddRecordingWithTreeForTesting(loopSegment);

            var tip = new Recording
            {
                RecordingId = "rec-loop-tip",
                ChainId = "chain-loop",
                ChainIndex = 1,
                VesselSnapshot = SnapshotWithCrew(kerbal),
                TerminalStateValue = tipTerminal,
                CrewEndStates = new Dictionary<string, KerbalEndState> { { kerbal, tipEndState } }
            };
            tip.Points.Add(new TrajectoryPoint { ut = 142.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 200.0 });
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            return loopSegment;
        }

        [Fact]
        public void LoopHold_TurningLoopOff_ReleasesARecoveredHoldOnTheNextWalk()
        {
            var loopSegment = AddLoopingChain("Jeb", KerbalEndState.Recovered, TerminalState.Splashed);

            var looping = KerbalsTestHelper.RecalculateFromStore();
            Assert.True(double.IsPositiveInfinity(looping.Reservations["Jeb"].ReservedUntilUT));

            // The Recordings-table toggle writes Recording.LoopPlayback and runs no recalc;
            // KerbalsModule.PrePass re-reads the flag on the next ledger walk.
            loopSegment.LoopPlayback = false;

            var stopped = KerbalsTestHelper.RecalculateFromStore();
            Assert.Equal(200.0, stopped.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void LoopHold_TurningLoopOff_LeavesAnAboardHoldOpenEnded()
        {
            var loopSegment = AddLoopingChain("Val", KerbalEndState.Aboard, TerminalState.Orbiting);

            var looping = KerbalsTestHelper.RecalculateFromStore();
            Assert.True(double.IsPositiveInfinity(looping.Reservations["Val"].ReservedUntilUT));

            loopSegment.LoopPlayback = false;

            // An Aboard hold ends only by a recovery closure, never by the loop toggle.
            var stopped = KerbalsTestHelper.RecalculateFromStore();
            Assert.True(double.IsPositiveInfinity(stopped.Reservations["Val"].ReservedUntilUT));
        }

        // ================================================================
        // P1: part entry-cost purchases
        // ================================================================

        private static GameStateEvent PartPurchase(double ut, float entryCost, bool bypass)
        {
            return GameStateRecorder.CreatePartPurchasedEvent(
                "mk1pod.v2", entryCost, bypass, ut, currentFunds: 50000);
        }

        [Fact]
        public void P1_PartPurchase_BecomesAFundsOnlyRowKeyedByPartName()
        {
            var action = GameStateEventConverter.ConvertEvent(PartPurchase(300.0, 1600f, bypass: false), null);

            // No tech / part identity field: only a funds row whose DedupKey is the part
            // name. Nothing in the walk or KspStatePatcher reads it back as purchased state.
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
            Assert.Equal("mk1pod.v2", action.DedupKey);
            Assert.Equal(1600f, action.FundsSpent);
            Assert.True(string.IsNullOrEmpty(action.NodeId));
        }

        [Fact]
        public void P1_BuyNowPlusCommittedPurchase_ChargesTheEntryCostTwice_DocumentsHole()
        {
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            // Committed KSC purchase at UT 300, then (after a rewind to UT 100) the player
            // buys the same part again at UT 120 because stock still shows it unpurchased.
            var committed = GameStateEventConverter.ConvertEvent(PartPurchase(300.0, 1600f, false), null);
            var boughtNow = GameStateEventConverter.ConvertEvent(PartPurchase(120.0, 1600f, false), null);
            var actions = new List<GameAction> { FundsSeed(10000f), committed, boughtNow };

            RecalculationEngine.Recalculate(actions);

            Assert.True(committed.Affordable);
            Assert.True(boughtNow.Affordable);
            Assert.Equal(10000.0 - 2 * 1600.0, funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void P1_BypassEntryPurchase_PurchaseIsFree_NoDoubleCharge()
        {
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            var committed = GameStateEventConverter.ConvertEvent(PartPurchase(300.0, 1600f, true), null);
            var boughtNow = GameStateEventConverter.ConvertEvent(PartPurchase(120.0, 1600f, true), null);
            var actions = new List<GameAction> { FundsSeed(10000f), committed, boughtNow };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(0f, committed.FundsSpent);
            Assert.Equal(10000.0, funds.GetRunningBalance(), 1);
        }

        // ================================================================
        // S1: strategy activation
        // ================================================================

        [Fact]
        public void S1_SecondActivationOfTheSameStrategy_OverwritesAndChargesSetupAgain_DocumentsHole()
        {
            var strategies = new StrategiesModule();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            GameAction Activate(double ut) => new GameAction
            {
                Type = GameActionType.StrategyActivate,
                UT = ut,
                StrategyId = "UnpaidResearch",
                SourceResource = StrategyResource.Reputation,
                TargetResource = StrategyResource.Science,
                Commitment = 0.1f,
                SetupCost = 1000f
            };

            var actions = new List<GameAction> { FundsSeed(10000f), Activate(300.0), Activate(120.0) };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(1, strategies.GetActiveStrategyCount());
            Assert.Contains(logLines, l => l.Contains("[Strategies]")
                && l.Contains("already active, overwriting previous activation"));
            Assert.Equal(10000.0 - 2 * 1000.0, funds.GetRunningBalance(), 1);
        }
    }
}
