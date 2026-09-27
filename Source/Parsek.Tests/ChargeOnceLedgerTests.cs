using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A contract fail / cancel penalty and a facility repair cost are charged by the
    /// recalculation walk only when the walk finds the transition real at that point:
    /// the contract still unresolved, the building destroyed. Stock charges each once, on
    /// the transition. Covers the double-charge cases of
    /// docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md section 7.5
    /// (C4 X2 / X3, C6, a world-driven failure) and F3 (a repair of an intact building),
    /// plus the mirror direction: a genuine single fail / cancel / repair still charges.
    /// </summary>
    [Collection("Sequential")]
    public class ChargeOnceLedgerTests : IDisposable
    {
        private const string MainBuilding = "SpaceCenter/LaunchPad/Facility/mainBuilding";
        private const string OtherBuilding = "SpaceCenter/VehicleAssemblyBuilding/Facility/mainBuilding";
        private readonly List<string> logLines = new List<string>();

        public ChargeOnceLedgerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================
        // Helpers
        // ================================================================

        private sealed class Walk
        {
            public ContractsModule Contracts;
            public FundsModule Funds;
            public ReputationModule Rep;
            public FacilitiesModule Facilities;
        }

        private static Walk Register()
        {
            var w = new Walk
            {
                Contracts = new ContractsModule(),
                Funds = new FundsModule(),
                Rep = new ReputationModule(),
                Facilities = new FacilitiesModule()
            };
            // Production tiers (LedgerOrchestrator.Initialize).
            RecalculationEngine.RegisterModule(w.Contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Rep, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Facilities, RecalculationEngine.ModuleTier.Facilities);
            return w;
        }

        // Every cutoff-less recalc patches GetAvailableFunds() into stock (no projection is
        // installed), so a charge-once decision must hold there as well as in the balance.
        private static void AssertFunds(Walk w, double expected)
        {
            Assert.Equal(expected, w.Funds.GetRunningBalance(), 1);
            Assert.Equal(expected, w.Funds.GetAvailableFunds(), 1);
        }

        private static GameAction FundsSeed(float amount = 10000f)
        {
            return new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = amount };
        }

        private static GameAction Accept(double ut, string id, double deadline = 1.0e6)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractAccept,
                ContractId = id,
                DeadlineUT = deadline,
                FundsPenalty = 500f,
                RepPenalty = 5f
            };
        }

        private static GameAction Resolution(GameActionType type, double ut, string id,
            float fundsPenalty, float repPenalty = 0f, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = type,
                ContractId = id,
                FundsPenalty = fundsPenalty,
                RepPenalty = repPenalty,
                RecordingId = recordingId
            };
        }

        private static GameAction Complete(double ut, string id, float fundsReward, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractComplete,
                ContractId = id,
                FundsReward = fundsReward,
                RecordingId = recordingId
            };
        }

        private static GameAction Destruction(double ut, string building, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityDestruction,
                FacilityId = building,
                RecordingId = recordingId
            };
        }

        private static GameAction Repair(double ut, string building, float cost, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityRepair,
                FacilityId = building,
                FacilityCost = cost,
                RecordingId = recordingId
            };
        }

        // ================================================================
        // Contracts: the double-penalty cases (section 7.5)
        // ================================================================

        [Fact]
        public void X3_TwoCancels_SecondChargesNeitherFundsNorReputation()
        {
            var w = Register();
            var first = Resolution(GameActionType.ContractCancel, 150.0, "c-x3", 200f, 4f);
            var second = Resolution(GameActionType.ContractCancel, 300.0, "c-x3", 300f, 6f, "rec-committed");
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-x3"), first, second
            });

            AssertFunds(w, 10000.0 - 200.0);
            Assert.True(first.Effective);
            Assert.True(first.EffectiveRep < 0f);
            Assert.False(second.Effective);
            Assert.Equal(0f, second.EffectiveRep);
            // Reputation patches from the running value (no PrePass total): one penalty.
            Assert.Equal((double)first.EffectiveRep, (double)w.Rep.GetRunningRep(), 3);
        }

        [Theory]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void C6_CompleteNow_ThenCommittedFailOrCancel_ChargesNoPenalty(GameActionType committed)
        {
            var w = Register();
            var complete = Complete(150.0, "c-c6", 3000f, "rec-present");
            var later = Resolution(committed, 300.0, "c-c6", 800f, 8f, "rec-committed");
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-c6"), complete, later
            });

            Assert.True(complete.Effective);
            Assert.False(later.Effective);
            Assert.Equal(0f, later.EffectiveRep);
            AssertFunds(w, 10000.0 + 3000.0);
            Assert.Contains(logLines, l => l.Contains("[Contracts]") && l.Contains("c-c6")
                && l.Contains("effective=false (already completed earlier in the walk)")
                && l.Contains("fundsPenalty=800"));
            // The completion stays the terminal outcome: stock holds it Completed.
            Assert.Equal(ContractTerminalOutcome.Completed, w.Contracts.GetTerminalContractOutcomes()["c-c6"]);
        }

        [Theory]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void WorldDrivenFailNow_ThenCommittedFailOrCancel_ChargesOnce(GameActionType committed)
        {
            var w = Register();
            var worldFail = Resolution(GameActionType.ContractFail, 150.0, "c-w", 500f, 5f, "rec-present");
            var later = Resolution(committed, 300.0, "c-w", 500f, 5f, "rec-committed");
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-w"), worldFail, later
            });

            Assert.True(worldFail.Effective);
            Assert.False(later.Effective);
            Assert.Equal((double)worldFail.EffectiveRep, (double)w.Rep.GetRunningRep(), 3);
            AssertFunds(w, 10000.0 - 500.0);
            Assert.Equal(ContractTerminalOutcome.Failed, w.Contracts.GetTerminalContractOutcomes()["c-w"]);
        }

        [Fact]
        public void WorldDrivenFailNow_ThenCommittedComplete_ChargesTheFailAndZeroesTheCompletion()
        {
            // The X1 path the research doc names: unchanged, the fail is the transition.
            var w = Register();
            var worldFail = Resolution(GameActionType.ContractFail, 150.0, "c-w1", 500f, 5f, "rec-present");
            var complete = Complete(300.0, "c-w1", 3000f, "rec-committed");
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-w1"), worldFail, complete
            });

            Assert.True(worldFail.Effective);
            Assert.False(complete.Effective);
            AssertFunds(w, 10000.0 - 500.0);
        }

        [Fact]
        public void FirstResolutionStaysTheTerminalOutcome()
        {
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-t"),
                Resolution(GameActionType.ContractCancel, 150.0, "c-t", 200f),
                Resolution(GameActionType.ContractFail, 300.0, "c-t", 500f)
            });

            Assert.Equal(ContractTerminalOutcome.Cancelled, w.Contracts.GetTerminalContractOutcomes()["c-t"]);
            Assert.Equal(GameActionType.ContractCancel, w.Contracts.GetTerminalContractActionTypes()["c-t"]);
        }

        // ================================================================
        // Contracts: the mirror direction (genuine single resolutions still charge)
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void SingleFailOrCancel_ChargesFundsAndReputation(GameActionType type)
        {
            var w = Register();
            var resolution = Resolution(type, 150.0, "c-s", 500f, 5f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-s"), resolution
            });

            Assert.True(resolution.Effective);
            Assert.True(resolution.EffectiveRep < 0f);
            AssertFunds(w, 10000.0 - 500.0);
        }

        [Fact]
        public void FailOfAContractAcceptedBeforeTheLedger_StillCharges()
        {
            // No ContractAccept row: the walk has no positive knowledge of a prior
            // resolution, and stock charged the penalty.
            var w = Register();
            var fail = Resolution(GameActionType.ContractFail, 150.0, "c-pre", 500f, 5f);
            RecalculationEngine.Recalculate(new List<GameAction> { FundsSeed(), fail });

            Assert.True(fail.Effective);
            AssertFunds(w, 10000.0 - 500.0);
        }

        [Fact]
        public void RecordedDeadlineExpiryFail_StillCharges_ALaterCancelDoesNot()
        {
            // Stock reports a deadline expiry as a fail; CheckDeadlines expires the
            // contract before the fail row is dispatched, and the row carries the penalty.
            var w = Register();
            var expiryFail = Resolution(GameActionType.ContractFail, 260.0, "c-d", 500f, 5f);
            var laterCancel = Resolution(GameActionType.ContractCancel, 400.0, "c-d", 300f, 3f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-d", deadline: 250.0), expiryFail, laterCancel
            });

            Assert.True(expiryFail.Effective);
            Assert.False(laterCancel.Effective);
            AssertFunds(w, 10000.0 - 500.0);
            Assert.Equal(ContractTerminalOutcome.DeadlineExpired,
                w.Contracts.GetTerminalContractOutcomes()["c-d"]);
        }

        [Fact]
        public void SyntheticDeadlineFail_StillCharges()
        {
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-syn", deadline: 250.0),
                new GameAction { UT = 500.0, Type = GameActionType.FundsEarning, FundsAwarded = 0f }
            });

            AssertFunds(w, 10000.0 - 500.0);
        }

        [Fact]
        public void NoCutoff_TwoCancels_AvailableFundsChargeOnce()
        {
            // Review reproduction: PrePass counted both penalties, so the patched
            // (no-projection) availability read 9500.
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-nc"),
                Resolution(GameActionType.ContractCancel, 200.0, "c-nc", 200f),
                Resolution(GameActionType.ContractCancel, 300.0, "c-nc", 300f)
            });

            Assert.Equal(9800.0, w.Funds.GetAvailableFunds(), 1);
            Assert.Equal(200.0, w.Funds.GetTotalCommittedSpendings(), 1);
        }

        [Fact]
        public void NoCutoff_TwoRepairsOfOneDestruction_AvailableFundsChargeOnce()
        {
            // Review reproduction: the patched availability read 2000.
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Destruction(200.0, MainBuilding),
                Repair(300.0, MainBuilding, 4000f), Repair(400.0, MainBuilding, 4000f)
            });

            Assert.Equal(6000.0, w.Funds.GetAvailableFunds(), 1);
            Assert.Equal(4000.0, w.Funds.GetTotalCommittedSpendings(), 1);
        }

        [Fact]
        public void FailRowsWithNoContractId_AreNotMatchedToEachOther()
        {
            var w = Register();
            var a = Resolution(GameActionType.ContractFail, 150.0, null, 500f);
            var b = Resolution(GameActionType.ContractFail, 300.0, null, 300f);
            RecalculationEngine.Recalculate(new List<GameAction> { FundsSeed(), a, b });

            Assert.True(a.Effective);
            Assert.True(b.Effective);
            AssertFunds(w, 10000.0 - 800.0);
        }

        [Fact]
        public void ReacceptedContract_ChargesEachLifecycleOnce()
        {
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-r"),
                Resolution(GameActionType.ContractCancel, 150.0, "c-r", 200f),
                Accept(200.0, "c-r"),
                Resolution(GameActionType.ContractFail, 300.0, "c-r", 500f)
            });

            AssertFunds(w, 10000.0 - 200.0 - 500.0);
        }

        [Fact]
        public void SameUtCompleteAndFail_FailIsTheTransition()
        {
            // SortActions dispatches the completion first; PrePass makes the same-tick
            // fail authoritative, so the completion is zeroed and the fail still charges.
            var w = Register();
            var complete = Complete(200.0, "c-tie", 3000f);
            var fail = Resolution(GameActionType.ContractFail, 200.0, "c-tie", 500f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-tie"), complete, fail
            });

            Assert.False(complete.Effective);
            Assert.True(fail.Effective);
            AssertFunds(w, 10000.0 - 500.0);
        }

        [Fact]
        public void TombstonedFirstCancel_TheCommittedFailChargesAgain()
        {
            // A Re-Fly tombstone removes a row from the effective ledger before the walk:
            // with the present-day cancel gone, the committed fail is the transition.
            var w = Register();
            var fail = Resolution(GameActionType.ContractFail, 300.0, "c-tomb", 500f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-tomb"), fail
            });

            Assert.True(fail.Effective);
            AssertFunds(w, 10000.0 - 500.0);
        }

        [Fact]
        public void CutoffWalk_AResolvedContractsCommittedFail_IsNotReservedFromAvailableFunds()
        {
            var w = Register();
            var committedFail = Resolution(GameActionType.ContractFail, 300.0, "c-proj", 500f, 0f, "rec-committed");
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-proj"),
                Resolution(GameActionType.ContractCancel, 150.0, "c-proj", 200f),
                committedFail
            }, utCutoff: 200.0);

            Assert.Equal(10000.0 - 200.0, w.Funds.GetRunningBalance(), 1);
            Assert.Equal(10000.0 - 200.0, w.Funds.GetAvailableFunds(), 1);
            Assert.False(committedFail.Effective);
            double delta;
            Assert.False(w.Funds.TryGetProjectionDelta(committedFail, out delta));
        }

        [Fact]
        public void CutoffWalk_AnOpenContractsCommittedFail_IsStillReserved()
        {
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-open"),
                Resolution(GameActionType.ContractFail, 300.0, "c-open", 500f, 0f, "rec-committed")
            }, utCutoff: 200.0);

            Assert.Equal(10000.0, w.Funds.GetRunningBalance(), 1);
            Assert.Equal(10000.0 - 500.0, w.Funds.GetAvailableFunds(), 1);
        }

        [Theory]
        [InlineData(false, false, (int)ContractTerminalOutcome.Completed, null)]
        [InlineData(false, true, (int)ContractTerminalOutcome.Completed, "completed")]
        [InlineData(false, true, (int)ContractTerminalOutcome.DeadlineExpired, null)]
        [InlineData(true, true, (int)ContractTerminalOutcome.DeadlineExpired, "failed at its deadline")]
        [InlineData(true, true, (int)ContractTerminalOutcome.Failed, "failed")]
        [InlineData(true, true, (int)ContractTerminalOutcome.Cancelled, "cancelled")]
        public void ResolvePriorContractResolution_SkipsOnlyOnPositiveKnowledge(
            bool explicitlyResolved, bool hasOutcome, int outcome, string expected)
        {
            Assert.Equal(expected, ContractsModule.ResolvePriorContractResolution(
                explicitlyResolved, hasOutcome, (ContractTerminalOutcome)outcome));
        }

        // ================================================================
        // F3: facility repair charges only when the walk finds the building destroyed
        // ================================================================

        [Fact]
        public void DestructionThenRepair_ChargesOnce()
        {
            var w = Register();
            var repair = Repair(300.0, MainBuilding, 4000f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Destruction(200.0, MainBuilding, "rec-crash"), repair
            });

            Assert.True(repair.Effective);
            AssertFunds(w, 10000.0 - 4000.0);
            Assert.False(w.Facilities.IsFacilityDestroyed(MainBuilding));
        }

        [Fact]
        public void TwoRepairsOfOneDestruction_ChargeOnce_SecondIsLoggedFree()
        {
            // The KSC-REPAIR-AFTER-REWIND shape: a live re-repair at 300 before the
            // committed repair at 400 of the same destruction.
            var w = Register();
            var liveRepair = Repair(300.0, MainBuilding, 4000f);
            var committedRepair = Repair(400.0, MainBuilding, 4000f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Destruction(200.0, MainBuilding, "rec-crash"), liveRepair, committedRepair
            });

            Assert.True(liveRepair.Effective);
            Assert.False(committedRepair.Effective);
            Assert.True(committedRepair.Affordable);
            AssertFunds(w, 10000.0 - 4000.0);
            Assert.Contains(logLines, l => l.Contains("[Funds]")
                && l.Contains("FacilityRepair not charged")
                && l.Contains(MainBuilding)
                && l.Contains("ut=400")
                && l.Contains("cost=4000"));
        }

        [Fact]
        public void RepairOfABuildingWithNoRow_StillCharges()
        {
            // The repair row itself proves stock found the building destroyed; a
            // destruction from before the ledger began leaves no row, and stock charged.
            var w = Register();
            var repair = Repair(300.0, MainBuilding, 4000f);
            RecalculationEngine.Recalculate(new List<GameAction> { FundsSeed(), repair });

            Assert.True(repair.Effective);
            AssertFunds(w, 10000.0 - 4000.0);
        }

        [Fact]
        public void RepairAfterARepairWithNoDestructionBetween_ChargesNothing()
        {
            var w = Register();
            var first = Repair(300.0, MainBuilding, 4000f);
            var second = Repair(400.0, MainBuilding, 4000f);
            RecalculationEngine.Recalculate(new List<GameAction> { FundsSeed(), first, second });

            Assert.True(first.Effective);
            Assert.False(second.Effective);
            AssertFunds(w, 10000.0 - 4000.0);
        }

        [Fact]
        public void TwoDestructionsEachRepaired_ChargeTwice()
        {
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(),
                Destruction(200.0, MainBuilding), Repair(300.0, MainBuilding, 4000f),
                Destruction(400.0, MainBuilding), Repair(500.0, MainBuilding, 4000f)
            });

            AssertFunds(w, 10000.0 - 8000.0);
        }

        [Fact]
        public void RepairState_IsPerBuilding()
        {
            var w = Register();
            var otherRepair = Repair(310.0, OtherBuilding, 1000f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(),
                Destruction(200.0, MainBuilding), Repair(300.0, MainBuilding, 4000f),
                Destruction(250.0, OtherBuilding), otherRepair
            });

            Assert.True(otherRepair.Effective);
            AssertFunds(w, 10000.0 - 5000.0);
        }

        [Fact]
        public void CutoffWalk_AFreeCommittedRepair_IsNotReservedFromAvailableFunds()
        {
            var w = Register();
            var committedRepair = Repair(400.0, MainBuilding, 4000f);
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Destruction(200.0, MainBuilding, "rec-crash"),
                Repair(300.0, MainBuilding, 4000f), committedRepair
            }, utCutoff: 350.0);

            Assert.Equal(10000.0 - 4000.0, w.Funds.GetRunningBalance(), 1);
            Assert.Equal(10000.0 - 4000.0, w.Funds.GetAvailableFunds(), 1);
            double delta;
            Assert.False(w.Funds.TryGetProjectionDelta(committedRepair, out delta));
        }

        [Fact]
        public void CutoffWalk_ACommittedRepairOfADestroyedBuilding_IsStillReserved()
        {
            var w = Register();
            RecalculationEngine.Recalculate(new List<GameAction>
            {
                FundsSeed(), Destruction(200.0, MainBuilding, "rec-crash"),
                Repair(400.0, MainBuilding, 4000f)
            }, utCutoff: 350.0);

            Assert.Equal(10000.0, w.Funds.GetRunningBalance(), 1);
            Assert.Equal(10000.0 - 4000.0, w.Funds.GetAvailableFunds(), 1);
        }

        [Fact]
        public void Recalculate_IsIdempotent_ForTheChargeOnceDecisions()
        {
            var w = Register();
            var actions = new List<GameAction>
            {
                FundsSeed(), Accept(100.0, "c-i"),
                Resolution(GameActionType.ContractCancel, 150.0, "c-i", 200f),
                Resolution(GameActionType.ContractFail, 300.0, "c-i", 500f),
                Destruction(200.0, MainBuilding), Repair(300.0, MainBuilding, 4000f),
                Repair(400.0, MainBuilding, 4000f)
            };
            RecalculationEngine.Recalculate(actions);
            double first = w.Funds.GetRunningBalance();
            RecalculationEngine.Recalculate(actions);

            Assert.Equal(10000.0 - 200.0 - 4000.0, first, 1);
            AssertFunds(w, first);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ShouldChargeFacilityRepair_FreeOnlyWhenTheWalkHasTheBuildingIntact(
            bool? destroyedBefore, bool expected)
        {
            Assert.Equal(expected, FundsModule.ShouldChargeFacilityRepair(destroyedBefore));
        }
    }
}
