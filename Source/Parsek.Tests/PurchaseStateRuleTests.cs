using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LEDGER-EVENT-IDENTITY step 6 (design 3.15 step 6, owner rulings 2026-10-26): a purchase is
    /// charged when the walk state at its (UT, cseq) does not already own the item (tech, part,
    /// part upgrade, facility at that exact level, kerbal on the roster). The walk owns an item
    /// at a row only through a purchase captured after the row (a rewind separated them); a
    /// legitimate repeat charges again, a hire keeps its captured price, and the reservations,
    /// the reserved floor and the click gates read the same predicate.
    /// </summary>
    [Collection("Sequential")]
    public class PurchaseStateRuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PurchaseStateRuleTests()
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

        // ================================================================
        // Helpers
        // ================================================================

        private sealed class Walk
        {
            internal readonly MilestonesModule Milestones = new MilestonesModule();
            internal readonly ContractsModule Contracts = new ContractsModule();
            internal readonly ScienceModule Science = new ScienceModule();
            internal readonly FundsModule Funds = new FundsModule();
            internal readonly ReputationModule Rep = new ReputationModule();
            internal readonly FacilitiesModule Facilities = new FacilitiesModule();
        }

        // Production tiers (LedgerOrchestrator.Initialize), the subset purchases touch.
        private static Walk Register()
        {
            var w = new Walk();
            RecalculationEngine.RegisterModule(w.Milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Rep, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Facilities, RecalculationEngine.ModuleTier.Facilities);
            return w;
        }

        private static List<GameAction> Seeds(float funds = 100000f, float science = 100f)
        {
            return new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = funds },
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = science },
                new GameAction { UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = 0f },
            };
        }

        private static GameAction Tech(string node, double ut, long cseq, float cost)
        {
            return new GameAction { UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceSpending, NodeId = node, Cost = cost };
        }

        private static GameAction Part(string name, double ut, long cseq, float cost,
            FundsSpendingSource source = FundsSpendingSource.Other)
        {
            return new GameAction
            {
                UT = ut, CaptureSequence = cseq, Type = GameActionType.FundsSpending,
                FundsSpendingSource = source, DedupKey = name, FundsSpent = cost
            };
        }

        private static GameAction Level(string facility, double ut, long cseq, int toLevel, float cost, bool rebuild = false)
        {
            return new GameAction
            {
                UT = ut, CaptureSequence = cseq, Type = GameActionType.FacilityUpgrade,
                FacilityId = facility, ToLevel = toLevel, FacilityCost = cost, FacilityDowngrade = rebuild
            };
        }

        private static GameAction Hire(string name, double ut, long cseq, float cost)
        {
            return new GameAction { UT = ut, CaptureSequence = cseq, Type = GameActionType.KerbalHire, KerbalName = name, HireCost = cost };
        }

        private const string Vab = "SpaceCenter/VehicleAssemblyBuilding";

        // ================================================================
        // Pure rules
        // ================================================================

        // catches: the capture-order half of the rule judging rows without a capture number
        // (an older save), or a holder captured BEFORE the row (stock sold it again later).
        [Theory]
        [InlineData(20L, 10L, true)]
        [InlineData(10L, 20L, false)]
        [InlineData(10L, 10L, false)]
        [InlineData(0L, 10L, false)]
        [InlineData(10L, 0L, false)]
        [InlineData(-3L, 10L, false)]
        [InlineData(20L, -3L, false)]
        public void IsCapturedAfter_Matrix(long holder, long row, bool expected)
        {
            Assert.Equal(expected, PurchaseStateRule.IsCapturedAfter(holder, row));
        }

        [Fact]
        public void TryGetItem_NamesEveryPurchaseKindAndNothingElse()
        {
            PurchaseKind kind;
            string key;
            Assert.True(PurchaseStateRule.TryGetItem(Tech("t", 1, 1, 5f), out kind, out key));
            Assert.Equal(PurchaseKind.Tech, kind);
            Assert.True(PurchaseStateRule.TryGetItem(Part("p", 1, 1, 5f), out kind, out key));
            Assert.Equal(PurchaseKind.Part, kind);
            Assert.True(PurchaseStateRule.TryGetItem(Part("u", 1, 1, 5f, FundsSpendingSource.PartUpgrade), out kind, out key));
            Assert.Equal(PurchaseKind.PartUpgrade, kind);
            Assert.True(PurchaseStateRule.TryGetItem(Level(Vab, 1, 1, 2, 5f), out kind, out key));
            Assert.Equal(PurchaseKind.FacilityLevel, kind);
            Assert.Equal(Vab, key);
            Assert.True(PurchaseStateRule.TryGetItem(Hire("Bill", 1, 1, 5f), out kind, out key));
            Assert.Equal(PurchaseKind.Hire, kind);

            // Not purchases under this rule: a launch, a strategy, a contract accept, a repair.
            Assert.False(PurchaseStateRule.TryGetItem(Part("ship", 1, 1, 5f, FundsSpendingSource.VesselBuild), out kind, out key));
            Assert.False(PurchaseStateRule.TryGetItem(new GameAction { Type = GameActionType.StrategyActivate, StrategyId = "s" }, out kind, out key));
            Assert.False(PurchaseStateRule.TryGetItem(new GameAction { Type = GameActionType.ContractAccept, ContractId = "c" }, out kind, out key));
            Assert.False(PurchaseStateRule.TryGetItem(new GameAction { Type = GameActionType.FacilityRepair, FacilityId = "b" }, out kind, out key));
            // No key, no decision.
            Assert.False(PurchaseStateRule.TryGetItem(Tech(null, 1, 1, 5f), out kind, out key));
            Assert.False(PurchaseStateRule.TryGetItem(Level(Vab, 1, 1, 0, 5f), out kind, out key));
            Assert.False(PurchaseStateRule.TryGetItem(null, out kind, out key));
        }

        [Fact]
        public void TimelineHover_NamesTheReasonInOneSentence()
        {
            Assert.Equal("Not counted: already bought earlier on timeline, so nothing was charged.",
                TimelineRowHover.NotCounted(GameActionNotCountedReason.PurchaseAlreadyOwned));
        }

        // ================================================================
        // Already owned, each kind
        // ================================================================

        // catches: a committed tech (captured first, later UT) charged again after a rewind let
        // the player research the same node earlier on timeline.
        [Fact]
        public void Tech_AlreadyResearchedByALaterCapturedPurchase_IsNotCharged()
        {
            var w = Register();
            var actions = Seeds();
            var committed = Tech("basicRocketry", 500, 10, 5f);
            var again = Tech("basicRocketry", 300, 20, 5f);
            actions.Add(committed);
            actions.Add(again);

            RecalculationEngine.Recalculate(actions);

            Assert.True(again.Effective);
            Assert.False(committed.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committed.NotCountedReason);
            Assert.True(committed.Affordable);
            Assert.Equal(95.0, w.Science.GetRunningScience(), 3);
            double delta;
            Assert.False(ScienceModule.TryGetRowDelta(committed, out delta));
            Assert.Contains(logLines, l => l.Contains("[ScienceModule]")
                && l.Contains("Spending not charged: tech 'basicRocketry' is already researched")
                && l.Contains("captured after this row"));
            // Still researched: the patch's tech set keeps the node through either row.
            Assert.Contains("basicRocketry", KspStatePatcher.BuildCommittedTechUnlockIdsForPatch(actions, 1000));
        }

        [Theory]
        [InlineData(FundsSpendingSource.Other, "part 'mk1pod.v2'")]
        [InlineData(FundsSpendingSource.PartUpgrade, "part upgrade 'mk1pod.v2'")]
        public void PartAndUpgrade_AlreadyUnlockedByALaterCapturedPurchase_AreNotCharged(
            FundsSpendingSource source, string item)
        {
            var w = Register();
            var actions = Seeds();
            var committed = Part("mk1pod.v2", 500, 10, 800f, source);
            var again = Part("mk1pod.v2", 300, 20, 800f, source);
            actions.Add(committed);
            actions.Add(again);

            RecalculationEngine.Recalculate(actions);

            Assert.True(again.Effective);
            Assert.False(committed.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committed.NotCountedReason);
            Assert.Equal(100000.0 - 800.0, w.Funds.GetRunningBalance(), 3);
            double delta;
            Assert.False(FundsModule.TryGetRowDelta(committed, out delta));
            Assert.Contains(logLines, l => l.Contains("[Funds]") && l.Contains("FundsSpending not charged: " + item)
                && l.Contains("is already owned"));
        }

        [Fact]
        public void Facility_ALevelTheWalkHas_IsNotCharged_AHigherLevelIs()
        {
            var w = Register();
            var actions = Seeds(500000f);
            var committedTo2 = Level(Vab, 500, 10, 2, 30000f);
            var committedTo3 = Level(Vab, 600, 11, 3, 60000f);
            var newTo2 = Level(Vab, 300, 20, 2, 30000f);
            actions.Add(committedTo2);
            actions.Add(committedTo3);
            actions.Add(newTo2);

            RecalculationEngine.Recalculate(actions);

            Assert.True(newTo2.Effective);
            Assert.False(committedTo2.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committedTo2.NotCountedReason);
            Assert.True(committedTo3.Effective);
            Assert.Equal(500000.0 - 30000.0 - 60000.0, w.Funds.GetRunningBalance(), 3);
            Assert.Equal(3, w.Facilities.GetFacilityLevel(Vab));
            Assert.Contains(logLines, l => l.Contains("FacilityUpgrade not charged: facility '" + Vab + "' level 2"));
        }

        // catches: an old timeline's upgrade row read as a Rebuild because the walk holds a
        // higher level (review of 21d7ad3, input 1): both committed levels charged again and the
        // facility dropped to L2 between them.
        [Fact]
        public void Facility_BothCommittedLevelsReboughtEarlier_AreNotCharged_AndTheLevelNeverDips()
        {
            var w = Register();
            var actions = Seeds(500000f);
            var committedTo2 = Level(Vab, 100, 10, 2, 30000f);
            var committedTo3 = Level(Vab, 200, 11, 3, 60000f);
            var newTo2 = Level(Vab, 60, 20, 2, 30000f);
            var newTo3 = Level(Vab, 70, 21, 3, 60000f);
            actions.Add(committedTo2);
            actions.Add(committedTo3);
            actions.Add(newTo2);
            actions.Add(newTo3);

            var owned = PurchaseStateRule.CollectAlreadyOwned(actions);
            Assert.Equal(2, owned.Count);
            Assert.Contains(committedTo2, owned);
            Assert.Contains(committedTo3, owned);

            RecalculationEngine.Recalculate(actions);
            Assert.Equal(500000.0 - 90000.0, w.Funds.GetRunningBalance(), 3);
            Assert.True(newTo2.Effective && newTo3.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committedTo2.NotCountedReason);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committedTo3.NotCountedReason);
            Assert.Equal(3, w.Facilities.GetFacilityLevel(Vab));
            Assert.Contains(logLines, l => l.Contains("[Facilities]") && l.Contains("Upgrade not applied")
                && l.Contains("toLevel=2") && l.Contains("level stays 3"));

            // Between the two committed rows the facility is still at L3.
            RecalculationEngine.ClearModules();
            w = Register();
            RecalculationEngine.Recalculate(actions, 150);
            Assert.Equal(3, w.Facilities.GetFacilityLevel(Vab));
        }

        // catches (review of 21d7ad3, input 2): an owned facility row dropped from the index,
        // releasing the upgrade gate; the player's later L3 then turned the owned row into a
        // Rebuild that charged again and took the facility back to L2.
        [Fact]
        public void Facility_AnOwnedCommittedLevel_KeepsTheGate_AndALaterHigherLevelIsNeverUndone()
        {
            var committedTo2 = Level(Vab, 500, 10, 2, 30000f);
            var newTo2 = Level(Vab, 300, 20, 2, 30000f);
            var ledger = Seeds(500000f);
            ledger.Add(committedTo2);
            ledger.Add(newTo2);

            Assert.Contains(committedTo2, PurchaseStateRule.CollectAlreadyOwned(ledger));
            var index = CommittedFutureIndex.Build(ledger, id => true, id => null, null);
            Assert.True(index.HasFuture(CommittedFutureKind.FacilityUpgrade, Vab, 350));
            Assert.Equal(0, index.SkippedAlreadyOwnedRows);

            // Even if a higher level is bought before it (the gate bypassed), the owned row
            // stays owned and changes nothing.
            var w = Register();
            var newTo3 = Level(Vab, 400, 30, 3, 60000f);
            ledger.Add(newTo3);
            RecalculationEngine.Recalculate(ledger);
            Assert.False(committedTo2.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committedTo2.NotCountedReason);
            Assert.Equal(500000.0 - 90000.0, w.Funds.GetRunningBalance(), 3);
            Assert.Equal(3, w.Facilities.GetFacilityLevel(Vab));
        }

        // catches: a Rebuild flag ignored, so a committed Rebuild after a rewind re-bought the
        // levels reads as an owned upgrade (the facility never comes down), and the committed
        // re-upgrade after it reads as owned too.
        [Fact]
        public void Facility_ACommittedRebuildStillApplies_AndTheUpgradeAfterItCharges()
        {
            var w = Register();
            var actions = Seeds(500000f);
            var committedTo2 = Level(Vab, 100, 10, 2, 30000f);
            var committedTo3 = Level(Vab, 200, 11, 3, 60000f);
            var committedRebuild = Level(Vab, 300, 12, 2, 5000f, rebuild: true);
            var committedTo3Again = Level(Vab, 400, 13, 3, 60000f);
            actions.Add(committedTo2);
            actions.Add(committedTo3);
            actions.Add(committedRebuild);
            actions.Add(committedTo3Again);
            actions.Add(Level(Vab, 60, 20, 2, 30000f));
            actions.Add(Level(Vab, 70, 21, 3, 60000f));

            RecalculationEngine.Recalculate(actions);
            Assert.False(committedTo2.Effective);
            Assert.False(committedTo3.Effective);
            Assert.True(committedRebuild.Effective);
            Assert.True(committedTo3Again.Effective);
            Assert.Equal(500000.0 - 30000.0 - 60000.0 - 5000.0 - 60000.0, w.Funds.GetRunningBalance(), 3);
            Assert.Equal(3, w.Facilities.GetFacilityLevel(Vab));

            RecalculationEngine.ClearModules();
            w = Register();
            RecalculationEngine.Recalculate(actions, 350);
            Assert.Equal(2, w.Facilities.GetFacilityLevel(Vab));
        }

        // A Rebuild is owned only at its exact level: the walk already rebuilt the facility to
        // that level through a purchase captured after the committed one.
        [Fact]
        public void Facility_ACommittedRebuildToTheLevelTheWalkIsAt_IsNotCharged()
        {
            var w = Register();
            var actions = Seeds(500000f);
            actions.Add(Level(Vab, 100, 10, 2, 30000f));
            actions.Add(Level(Vab, 150, 11, 3, 60000f));
            var committedRebuild = Level(Vab, 500, 12, 2, 5000f, rebuild: true);
            actions.Add(committedRebuild);
            actions.Add(Level(Vab, 450, 20, 2, 5000f, rebuild: true));

            RecalculationEngine.Recalculate(actions);
            Assert.False(committedRebuild.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committedRebuild.NotCountedReason);
            Assert.Equal(500000.0 - 95000.0, w.Funds.GetRunningBalance(), 3);
            Assert.Equal(2, w.Facilities.GetFacilityLevel(Vab));
        }

        [Fact]
        public void Hire_KerbalAlreadyOnTheRoster_IsNotCharged()
        {
            var w = Register();
            var actions = Seeds();
            var committed = Hire("Bill Kerman", 500, 10, 20000f);
            var again = Hire("Bill Kerman", 300, 20, 20000f);
            actions.Add(committed);
            actions.Add(again);

            RecalculationEngine.Recalculate(actions);

            Assert.True(again.Effective);
            Assert.False(committed.Effective);
            Assert.Equal(GameActionNotCountedReason.PurchaseAlreadyOwned, committed.NotCountedReason);
            Assert.Equal(80000.0, w.Funds.GetRunningBalance(), 3);
            Assert.Contains(logLines, l => l.Contains("KerbalHire not charged: kerbal 'Bill Kerman' is already owned"));
        }

        // catches: the state rule firing on rows with no capture order (an older save): those
        // charge as recorded, so an older career walks exactly as before.
        [Fact]
        public void RowsWithoutCaptureNumbers_AreNeverJudged()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(Part("p", 500, 0, 800f));
            actions.Add(Part("p", 300, 0, 800f));
            actions.Add(Part("q", 500, 0, 100f));
            actions.Add(Part("q", 300, 7, 100f));
            RecalculationEngine.Recalculate(actions);
            Assert.All(actions, a => Assert.True(a.Effective));
            Assert.Equal(100000.0 - 1800.0, w.Funds.GetRunningBalance(), 3);
        }

        // ================================================================
        // Legitimate repeats charge again
        // ================================================================

        // catches: a single-timeline repeat (holder captured BEFORE the row) read as owned. Stock
        // sold the item again, so the walk cannot see the step that gave it up.
        [Fact]
        public void HolderCapturedBeforeTheRow_ChargesAgain()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(Part("p", 100, 1, 800f));
            actions.Add(Part("p", 200, 2, 800f));
            RecalculationEngine.Recalculate(actions);
            Assert.All(actions, a => Assert.True(a.Effective));
            Assert.Equal(100000.0 - 1600.0, w.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void Facility_RebuiltALevelDownThenUpgradedAgain_ChargesEveryStep()
        {
            var w = Register();
            var actions = Seeds(500000f);
            var up = Level(Vab, 100, 1, 2, 30000f);
            var down = Level(Vab, 200, 2, 1, 5000f, rebuild: true);
            var upAgain = Level(Vab, 300, 3, 2, 30000f);
            actions.Add(up);
            actions.Add(down);
            actions.Add(upAgain);

            RecalculationEngine.Recalculate(actions);

            Assert.True(up.Effective && down.Effective && upAgain.Effective);
            Assert.Equal(500000.0 - 65000.0, w.Funds.GetRunningBalance(), 3);
            Assert.Equal(2, w.Facilities.GetFacilityLevel(Vab));
        }

        // catches: a Rebuild that does not end the holding of the higher level, so a committed
        // upgrade after a rewind's rebuild reads as already owned.
        [Fact]
        public void Facility_ARebuildAfterARewindEndsTheHolding_TheCommittedUpgradeCharges()
        {
            var w = Register();
            var actions = Seeds(500000f);
            var committedUp = Level(Vab, 500, 10, 2, 30000f);
            var newUp = Level(Vab, 300, 20, 2, 30000f);
            var newDown = Level(Vab, 400, 21, 1, 5000f, rebuild: true);
            actions.Add(committedUp);
            actions.Add(newUp);
            actions.Add(newDown);

            RecalculationEngine.Recalculate(actions);

            Assert.True(committedUp.Effective);
            Assert.Equal(GameActionNotCountedReason.None, committedUp.NotCountedReason);
            Assert.Equal(500000.0 - 65000.0, w.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void Hire_ReHiredAfterADismissal_ChargesAgain()
        {
            // A dismissal is no ledger row; the re-hire is captured after the first hire.
            var w = Register();
            var actions = Seeds();
            var first = Hire("Bob Kerman", 100, 1, 20000f);
            var again = Hire("Bob Kerman", 400, 5, 26000f);
            actions.Add(first);
            actions.Add(again);
            RecalculationEngine.Recalculate(actions);
            Assert.True(first.Effective && again.Effective);
            Assert.Equal(100000.0 - 46000.0, w.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void Strategy_ActivatedAgain_ChargesItsSetupAgain_EvenAcrossARewindOrder()
        {
            var w = Register();
            var actions = Seeds();
            // Committed activation (captured first, later UT) and an activation a rewind put
            // earlier: strategies keep once per activation, never the purchase state rule.
            actions.Add(new GameAction { UT = 500, CaptureSequence = 10, Type = GameActionType.StrategyActivate, StrategyId = "AggressiveNegotiations", SetupCost = 5000f });
            actions.Add(new GameAction { UT = 550, CaptureSequence = 11, Type = GameActionType.StrategyDeactivate, StrategyId = "AggressiveNegotiations" });
            actions.Add(new GameAction { UT = 300, CaptureSequence = 20, Type = GameActionType.StrategyActivate, StrategyId = "AggressiveNegotiations", SetupCost = 5000f });
            actions.Add(new GameAction { UT = 350, CaptureSequence = 21, Type = GameActionType.StrategyDeactivate, StrategyId = "AggressiveNegotiations" });
            RecalculationEngine.Recalculate(actions);
            Assert.All(actions, a => Assert.True(a.Effective));
            Assert.Equal(90000.0, w.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void Contract_AcceptedAgain_PaysItsAdvanceAgain()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(new GameAction { UT = 100, CaptureSequence = 1, Type = GameActionType.ContractAccept, ContractId = "c1", AdvanceFunds = 1000f });
            actions.Add(new GameAction { UT = 200, CaptureSequence = 2, Type = GameActionType.ContractFail, ContractId = "c1", FundsPenalty = 300f });
            actions.Add(new GameAction { UT = 300, CaptureSequence = 3, Type = GameActionType.ContractAccept, ContractId = "c1", AdvanceFunds = 1000f });
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(100000.0 + 1000.0 - 300.0 + 1000.0, w.Funds.GetRunningBalance(), 3);
            Assert.DoesNotContain(actions, a => a.NotCountedReason == GameActionNotCountedReason.PurchaseAlreadyOwned);
        }

        // ================================================================
        // A hire keeps its captured price
        // ================================================================

        // catches: a committed hire re-priced from the roster size a rewind's earlier new hire
        // changed (owner ruling 2026-10-26: the captured price stands).
        [Fact]
        public void Hire_KeepsItsCapturedPrice_WhenAnEarlierNewHireGrowsTheRoster()
        {
            var w = Register();
            var actions = Seeds();
            // Captured at roster size 4 for 20000; after a rewind a different kerbal is hired
            // earlier, so stock would now price the committed hire at size 5.
            var committed = Hire("Valentina Kerman", 500, 10, 20000f);
            var earlier = Hire("Jebediah Junior", 300, 20, 20000f);
            actions.Add(committed);
            actions.Add(earlier);
            RecalculationEngine.Recalculate(actions);
            Assert.True(committed.Effective && earlier.Effective);
            Assert.Equal(20000f, committed.HireCost);
            Assert.Equal(60000.0, w.Funds.GetRunningBalance(), 3);
        }

        // ================================================================
        // One predicate: index, projection, reserved floor, click probe
        // ================================================================

        private static List<GameAction> OwnedFuturePartLedger(out GameAction committed, out GameAction bought)
        {
            var actions = Seeds();
            bought = Part("mk1pod.v2", 300, 20, 800f);
            committed = Part("mk1pod.v2", 500, 10, 800f);
            actions.Add(committed);
            actions.Add(bought);
            return actions;
        }

        [Fact]
        public void CommittedFutureIndex_SkipsAPurchaseTheWalkDoesNotCharge()
        {
            GameAction committed, bought;
            var actions = OwnedFuturePartLedger(out committed, out bought);
            var owned = PurchaseStateRule.CollectAlreadyOwned(actions);
            Assert.Single(owned);
            Assert.Contains(committed, owned);

            var index = CommittedFutureIndex.Build(actions, id => true, id => null, null);
            Assert.False(index.HasFuture(CommittedFutureKind.PartPurchase, "mk1pod.v2", 350));
            Assert.Equal(1, index.SkippedAlreadyOwnedRows);

            // Control: a different part ahead is still held.
            actions.Add(Part("fuelTank", 600, 11, 500f));
            index = CommittedFutureIndex.Build(actions, id => true, id => null, null);
            Assert.True(index.HasFuture(CommittedFutureKind.PartPurchase, "fuelTank", 350));
            Assert.Contains(logLines, l => l.Contains("[CommittedFutureIndex]") && l.Contains("skipped 1 purchase row(s)"));
        }

        [Fact]
        public void CollectAlreadyOwned_AgreesWithTheWalk()
        {
            var w = Register();
            var actions = Seeds(500000f, 200f);
            actions.Add(Tech("t1", 500, 10, 5f));
            actions.Add(Tech("t1", 300, 20, 5f));
            actions.Add(Level(Vab, 500, 11, 2, 30000f));
            actions.Add(Level(Vab, 300, 21, 2, 30000f));
            actions.Add(Level(Vab, 600, 12, 3, 60000f));
            actions.Add(Hire("Bill", 450, 13, 20000f));
            actions.Add(Hire("Bill", 200, 22, 20000f));
            actions.Add(Part("p", 100, 1, 10f));
            actions.Add(Part("p", 200, 2, 10f));
            var owned = PurchaseStateRule.CollectAlreadyOwned(actions);
            RecalculationEngine.Recalculate(actions);
            foreach (var a in actions)
                Assert.Equal(owned.Contains(a), a.NotCountedReason == GameActionNotCountedReason.PurchaseAlreadyOwned);
            Assert.Equal(3, owned.Count);
        }

        // catches: the reservation (projection) and the reserved floor still holding the cost of
        // a committed purchase the walk does not charge.
        [Fact]
        public void Projection_ReservesNothingForAnOwnedFuturePurchase()
        {
            GameAction committed, bought;
            var w = Register();
            var actions = OwnedFuturePartLedger(out committed, out bought);
            RecalculationEngine.Recalculate(actions, 350);
            Assert.Equal(100000.0 - 800.0, w.Funds.GetAvailableFunds(), 3);

            // Control: a different committed part ahead is reserved.
            RecalculationEngine.ClearModules();
            var c = Register();
            var control = Seeds();
            control.Add(Part("fuelTank", 500, 10, 800f));
            control.Add(Part("mk1pod.v2", 300, 20, 800f));
            RecalculationEngine.Recalculate(control, 350);
            Assert.Equal(100000.0 - 1600.0, c.Funds.GetAvailableFunds(), 3);
        }

        [Fact]
        public void ClickProbe_SeesNoCostAheadForAnOwnedFuturePurchase()
        {
            GameAction committed, bought;
            Register();
            var actions = OwnedFuturePartLedger(out committed, out bought);
            var probe = RecalculationEngine.ProbeClickReservationAtCutoff(actions, 350);
            RecalculationEngine.ClickPoolAhead funds;
            Assert.True(probe.TryGetPool(StockPool.Funds, out funds));
            Assert.False(funds.CostAhead);
            Assert.Equal(0, funds.CostRowsAhead);

            RecalculationEngine.ClearModules();
            Register();
            var control = Seeds();
            control.Add(Part("fuelTank", 500, 10, 800f));
            control.Add(Part("mk1pod.v2", 300, 20, 800f));
            probe = RecalculationEngine.ProbeClickReservationAtCutoff(control, 350);
            Assert.True(probe.TryGetPool(StockPool.Funds, out funds));
            Assert.True(funds.CostAhead);
        }

        // ================================================================
        // Ledger safety: a career that never rewinds walks exactly as before
        // ================================================================

        // catches: the state rule (or science debt) moving any value in a career that never
        // rewound: every purchase charges, every pool ends where stock ended. Randomized like
        // step 4's NeverRewoundCareer generator, with world-record rows under step 4's final
        // threshold rule woven in (new crossings, and re-payments of the held last interval
        // that stock pays on one timeline); the reference re-keys every purchase to a unique
        // item so no rule can ever judge it owned (the walk before step 6).
        [Theory]
        [InlineData(20261026)]
        [InlineData(7)]
        [InlineData(4242)]
        public void NeverRewoundCareer_WalksExactlyAsBefore(int seed)
        {
            var rng = new Random(seed);
            string[] techs = { "basicRocketry", "engineering101", "survivability", "generalRocketry", "stability" };
            string[] parts = { "mk1pod.v2", "fuelTank", "liquidEngine", "parachuteSingle", "probeCoreOcto" };
            string[] upgrades = { "engineTuning", "tankUpgrade" };
            string[] facilities = { Vab, "SpaceCenter/LaunchPad", "SpaceCenter/ResearchAndDevelopment" };
            string[] names = { "Bill", "Bob", "Val", "Jeb", "Ted", "Mal" };
            string[] strategies = { "AggressiveNegotiations", "OutsourcedResearch" };

            double funds = 300000.0, science = 0.0;
            var ownedTech = new HashSet<string>();
            var ownedParts = new HashSet<string>();
            var level = facilities.ToDictionary(f => f, f => 1);
            var roster = new HashSet<string>();
            var active = new HashSet<string>();
            var subjectCredit = new Dictionary<string, int>();
            int openContracts = 0, contractSerial = 0;
            var openIds = new List<string>();
            // Three intervals per record (a short RecordSplit; the book does not depend on the
            // number) so the career reaches re-payments.
            const int recordSplit = 3;
            string[] records = { "RecordsAltitude", "RecordsSpeed" };
            var recordBand = records.ToDictionary(r => r, r => 0);
            int recordRows = 0, repayRows = 0;

            var career = new List<GameAction>();
            double ut = 10.0;
            long cseq = 0;
            for (int i = 0; i < 160; i++)
            {
                ut += 3.0 + rng.Next(0, 30);
                long rowCseq = i < 20 ? 0 : ++cseq;   // the oldest rows carry no capture number
                GameAction row = null;
                switch (rng.Next(11))
                {
                    case 9:
                    {
                        // A world-record award: the next crossing, else (this build's rows
                        // only) a re-payment of the held last interval.
                        string id = records[rng.Next(records.Length)];
                        int interval;
                        bool repay = false;
                        if (recordBand[id] < recordSplit)
                            interval = ++recordBand[id];
                        else if (rowCseq > 0)
                        {
                            interval = recordSplit;
                            repay = true;
                            repayRows++;
                        }
                        else
                            break;
                        float rf = repay ? 300f : 1000f * interval;
                        float rr = 0.5f * interval;
                        float rs = 0.25f * interval;
                        funds += rf;
                        science += rs;
                        recordRows++;
                        row = new GameAction
                        {
                            Type = GameActionType.MilestoneAchievement, MilestoneId = id,
                            RecordingId = "rec-record-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            MilestoneFundsAwarded = rf, MilestoneRepAwarded = rr, MilestoneScienceAwarded = rs,
                            MilestoneRecordThresholds = 1,
                            // An old-save row (no capture number) carries no list.
                            MilestoneRecordBand = rowCseq > 0
                                ? new List<RecordThresholdCredit> { new RecordThresholdCredit(interval, rf, rr, rs, repay) }
                                : null
                        };
                        break;
                    }
                    case 0:
                    case 1:
                    {
                        string subject = "subj-" + rng.Next(6);
                        int credited;
                        subjectCredit.TryGetValue(subject, out credited);
                        int award = Math.Min(1 + rng.Next(8), 20 - credited);
                        if (award <= 0) break;
                        subjectCredit[subject] = credited + award;
                        science += award;
                        row = new GameAction { Type = GameActionType.ScienceEarning, SubjectId = subject, ScienceAwarded = award, SubjectMaxValue = 20f };
                        break;
                    }
                    case 2:
                    {
                        float award = 2000f + rng.Next(0, 8000);
                        funds += award;
                        row = new GameAction { Type = GameActionType.FundsEarning, FundsSource = FundsEarningSource.Recovery, FundsAwarded = award, DedupKey = "rec-" + i };
                        break;
                    }
                    case 3:
                    {
                        string node = techs[rng.Next(techs.Length)];
                        float cost = 5f + rng.Next(0, 10);
                        if (ownedTech.Contains(node) || science < cost) break;
                        ownedTech.Add(node);
                        science -= cost;
                        row = new GameAction { Type = GameActionType.ScienceSpending, NodeId = node, Cost = cost };
                        break;
                    }
                    case 4:
                    {
                        bool upgrade = rng.Next(3) == 0;
                        string item = upgrade ? upgrades[rng.Next(upgrades.Length)] : parts[rng.Next(parts.Length)];
                        string slot = (upgrade ? "u:" : "p:") + item;
                        float cost = 200f + rng.Next(0, 800);
                        if (ownedParts.Contains(slot) || funds < cost) break;
                        ownedParts.Add(slot);
                        funds -= cost;
                        row = new GameAction
                        {
                            Type = GameActionType.FundsSpending, DedupKey = item, FundsSpent = cost,
                            FundsSpendingSource = upgrade ? FundsSpendingSource.PartUpgrade : FundsSpendingSource.Other
                        };
                        break;
                    }
                    case 5:
                    {
                        string f = facilities[rng.Next(facilities.Length)];
                        int from = level[f];
                        bool down = from > 1 && (from == 3 || rng.Next(3) == 0);
                        int to = down ? from - 1 : from + 1;
                        float cost = down ? 5000f : 20000f * from;
                        if (funds < cost) break;
                        funds -= cost;
                        level[f] = to;
                        row = new GameAction { Type = GameActionType.FacilityUpgrade, FacilityId = f, ToLevel = to, FacilityCost = cost, FacilityDowngrade = down };
                        break;
                    }
                    case 6:
                    {
                        string name = names[rng.Next(names.Length)];
                        if (roster.Contains(name))
                        {
                            roster.Remove(name);   // a dismissal: no ledger row
                            break;
                        }
                        float cost = 10000f + 5000f * roster.Count;   // the captured price
                        if (funds < cost) break;
                        funds -= cost;
                        roster.Add(name);
                        row = new GameAction { Type = GameActionType.KerbalHire, KerbalName = name, HireCost = cost };
                        break;
                    }
                    case 7:
                    {
                        string s = strategies[rng.Next(strategies.Length)];
                        if (active.Remove(s))
                        {
                            row = new GameAction { Type = GameActionType.StrategyDeactivate, StrategyId = s };
                            break;
                        }
                        if (funds < 5000f) break;
                        funds -= 5000f;
                        active.Add(s);
                        row = new GameAction { Type = GameActionType.StrategyActivate, StrategyId = s, SetupCost = 5000f };
                        break;
                    }
                    case 8:
                    {
                        if (openContracts >= 2) break;
                        string id = "contract-" + (contractSerial++);
                        openContracts++;
                        openIds.Add(id);
                        funds += 1000f;
                        row = new GameAction { Type = GameActionType.ContractAccept, ContractId = id, AdvanceFunds = 1000f };
                        break;
                    }
                    case 10:
                    {
                        if (openIds.Count == 0) break;
                        string id = openIds[0];
                        openIds.RemoveAt(0);
                        openContracts--;
                        funds += 4000f;
                        row = new GameAction { Type = GameActionType.ContractComplete, ContractId = id, FundsReward = 4000f };
                        break;
                    }
                }
                if (row == null)
                {
                    if (rowCseq > 0) cseq--;
                    continue;
                }
                row.UT = ut;
                row.CaptureSequence = rowCseq;
                career.Add(row);
            }

            Assert.True(career.Count(a => PurchaseStateRule.TryGetItem(a, out _, out _)) > 20,
                "the generator must buy enough to exercise the rule");
            Assert.True(recordRows > 5, "the generator must cross enough record thresholds");
            Assert.True(repayRows > 0, "the generator must reach a record re-payment");
            Assert.Empty(PurchaseStateRule.CollectAlreadyOwned(career));

            // The walk before step 6: every purchase keyed to its own unique item.
            var reference = career.Select((a, n) =>
            {
                var copy = new GameAction
                {
                    Type = a.Type, UT = a.UT, CaptureSequence = a.CaptureSequence,
                    SubjectId = a.SubjectId, ScienceAwarded = a.ScienceAwarded, SubjectMaxValue = a.SubjectMaxValue,
                    FundsSource = a.FundsSource, FundsAwarded = a.FundsAwarded,
                    FundsSpendingSource = a.FundsSpendingSource, FundsSpent = a.FundsSpent, DedupKey = a.DedupKey,
                    NodeId = a.NodeId, Cost = a.Cost,
                    FacilityId = a.FacilityId, ToLevel = a.ToLevel, FacilityCost = a.FacilityCost, FacilityDowngrade = a.FacilityDowngrade,
                    KerbalName = a.KerbalName, HireCost = a.HireCost,
                    StrategyId = a.StrategyId, SetupCost = a.SetupCost,
                    ContractId = a.ContractId, AdvanceFunds = a.AdvanceFunds, FundsReward = a.FundsReward,
                    MilestoneId = a.MilestoneId, RecordingId = a.RecordingId,
                    MilestoneFundsAwarded = a.MilestoneFundsAwarded, MilestoneRepAwarded = a.MilestoneRepAwarded,
                    MilestoneScienceAwarded = a.MilestoneScienceAwarded,
                    MilestoneRecordThresholds = a.MilestoneRecordThresholds,
                    MilestoneRecordBand = a.MilestoneRecordBand
                };
                PurchaseKind kind;
                string key;
                if (PurchaseStateRule.TryGetItem(a, out kind, out key))
                {
                    string unique = "ref-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    copy.NodeId = a.NodeId != null ? unique : null;
                    copy.DedupKey = a.DedupKey != null ? unique : null;
                    copy.KerbalName = a.KerbalName != null ? unique : null;
                    copy.FacilityId = a.FacilityId != null ? unique : null;
                }
                return copy;
            }).ToList();

            var w = Register();
            var actions = Seeds(300000f, 0f);
            actions.AddRange(career);
            RecalculationEngine.Recalculate(actions);
            double walkFunds = w.Funds.GetRunningBalance();
            double walkScience = w.Science.GetRunningScience();
            double walkRep = w.Rep.GetRunningRep();

            Assert.All(career, a => Assert.True(a.Effective, a.Type + " at " + a.UT + " was not counted"));
            Assert.DoesNotContain(career, a => a.NotCountedReason == GameActionNotCountedReason.RecordThresholdsAlreadyCredited);
            Assert.DoesNotContain(career, a => a.NotCountedReason == GameActionNotCountedReason.PurchaseAlreadyOwned);
            Assert.Equal(0.0, w.Science.GetTechDebtCharged(), 6);
            // The career ends where stock ended.
            Assert.Equal(funds, walkFunds, 3);
            Assert.Equal(science, walkScience, 3);

            RecalculationEngine.ClearModules();
            var r = Register();
            var refActions = Seeds(300000f, 0f);
            refActions.AddRange(reference);
            RecalculationEngine.Recalculate(refActions);
            Assert.Equal(r.Funds.GetRunningBalance(), walkFunds, 3);
            Assert.Equal(r.Science.GetRunningScience(), walkScience, 3);
            Assert.Equal((double)r.Rep.GetRunningRep(), walkRep, 3);
            Assert.Equal(r.Funds.GetAvailableFunds(), w.Funds.GetAvailableFunds(), 3);
            Assert.Equal(r.Science.GetAvailableScience(), w.Science.GetAvailableScience(), 3);
        }
    }
}
