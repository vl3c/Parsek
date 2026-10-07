using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FACILITY-DOWNGRADE-DEBIT-NOT-LEDGERED. Stock's KSC facility menu, opened with Left Ctrl
    /// held on an out-of-service facility above level 1, shows a "Rebuild lvl N" button that
    /// calls <c>SpaceCenterBuilding.DowngradeFacility(Funding.Instance != null)</c>: a
    /// StructureConstruction debit of 0.667 x the lower level's cost, a free
    /// <c>ResetStructures</c>, then <c>SetLevel(level - 1)</c>. The FacilityDowngraded event
    /// was informational, so the ledger kept the higher level (the next recalc's facility patch
    /// raised the facility back) and ran high by the debit. The downgrade now opens the same
    /// capture scope an upgrade does and books a <see cref="GameActionType.FacilityUpgrade"/>
    /// level-change row to the lower tier, marked <see cref="GameAction.FacilityDowngrade"/>,
    /// carrying the observed debit.
    /// </summary>
    [Collection("Sequential")]
    public class FacilityDowngradeCostTests : IDisposable
    {
        private const string Admin = "SpaceCenter/Administration";
        private const string AdminBuilding = "SpaceCenter/Administration/Facility/mainBuilding";
        private const double UpgradeUt = 473.0;
        private const double DowngradeUt = 900.0;

        private readonly List<string> logLines = new List<string>();

        public FacilityDowngradeCostTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateRecorder.ResetForTesting();
            FacilityRepairCapture.ResetForTesting();
            FacilityUpgradeCapture.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
        }

        public void Dispose()
        {
            FacilityUpgradeCapture.ResetForTesting();
            FacilityRepairCapture.ResetForTesting();
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void AddStoreEvent(GameStateEvent e) => GameStateStore.AddEvent(ref e);

        private List<string> FacilityOrReconcileWarns() =>
            logLines.Where(l => l.Contains("[WARN]")
                && (l.Contains("KSC reconciliation") || l.Contains("FacilityUpgrade")
                    || l.Contains("FacilityDowngrade"))).ToList();

        private static GameStateEvent StructureConstructionDebit(double ut, double before, double after) =>
            new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = "StructureConstruction",
                recordingId = "",
                valueBefore = before,
                valueAfter = after,
            };

        /// <summary>A KSC upgrade of the Administration to tier 2 for 150000, through the real capture.</summary>
        private static void UpgradeAdminToTierTwo(GameStateRecorder recorder)
        {
            AddStoreEvent(StructureConstructionDebit(UpgradeUt, 500000.0, 350000.0));
            FacilityUpgradeCapture.BeginScope(Admin, fundsCharged: true, fundsAtOpen: 500000.0);
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureConstruction, 500000.0, 350000.0);
            Assert.True(recorder.RecordFacilityLevelChangeForTesting(Admin, 0f, 0.5f, UpgradeUt));
            FacilityUpgradeCapture.EndScope("upgrade-returned");
        }

        /// <summary>Stock's order inside DowngradeFacility(true): scope open, the debit, then SetLevel.</summary>
        private static void OpenDowngradeScopeAndDebit(string facility, double before, double after)
        {
            FacilityUpgradeCapture.BeginScope(facility, fundsCharged: true, fundsAtOpen: before, downgrade: true);
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureConstruction, before, after);
        }

        // MUTATION NOTE: dropping the scoped-downgrade branch in
        // GameStateFacilityRecorder.RecordEventDrivenLevelChange (or the converter's
        // FacilityDowngraded row) reds this cell: no row, the walk keeps tier 2, and the
        // ledger stays 100050 above the pool.
        [Fact]
        public void KscDowngrade_InsideTheScope_ChargesTheDebit_AndTheWalkLowersTheLevel()
        {
            Ledger.SeedInitialFunds(500000.0);
            var recorder = new GameStateRecorder();
            UpgradeAdminToTierTwo(recorder);
            Assert.Equal(2, LedgerOrchestrator.Facilities.GetFacilityLevel(Admin));

            AddStoreEvent(StructureConstructionDebit(DowngradeUt, 350000.0, 249950.0));
            OpenDowngradeScopeAndDebit(Admin, 350000.0, 249950.0);
            logLines.Clear();
            Assert.True(recorder.RecordFacilityLevelChangeForTesting(Admin, 0.5f, 0f, DowngradeUt));
            FacilityUpgradeCapture.EndScope("downgrade-returned");

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.FacilityDowngraded);
            Assert.Equal("cost=100050", evt.detail);
            var row = Ledger.Actions.Single(a => a.Type == GameActionType.FacilityUpgrade && a.FacilityDowngrade);
            Assert.Equal(100050f, row.FacilityCost);
            Assert.Equal(1, row.ToLevel);
            Assert.Equal(DowngradeUt, row.UT);
            Assert.Null(row.RecordingId);

            Assert.Equal(1, LedgerOrchestrator.Facilities.GetFacilityLevel(Admin));
            Assert.Equal(249950.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("Game state: FacilityDowngraded") && l.Contains("(event-driven)")
                && l.Contains("cost=100050") && l.Contains("costSource=observed-debit"));
            // The row pairs with its StructureConstruction debit: no reconcile WARN, no scope WARN.
            Assert.Empty(FacilityOrReconcileWarns());
        }

        [Fact]
        public void KscDowngrade_ScienceMode_RecordsTheLowerLevelAtCostZero()
        {
            var recorder = new GameStateRecorder();
            FacilityUpgradeCapture.BeginScope(Admin, fundsCharged: false, fundsAtOpen: double.NaN, downgrade: true);
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0.5f, 0f, DowngradeUt);
            FacilityUpgradeCapture.EndScope("downgrade-returned");

            Assert.Equal("cost=0", GameStateStore.Events.Single(
                e => e.eventType == GameStateEventType.FacilityDowngraded).detail);
            var row = Ledger.Actions.Single(a => a.Type == GameActionType.FacilityUpgrade);
            Assert.True(row.FacilityDowngrade);
            Assert.Equal(0f, row.FacilityCost);
            Assert.Equal(1, row.ToLevel);
            Assert.Contains(logLines, l => l.Contains("costSource=no-funds-charged"));
            Assert.Empty(FacilityOrReconcileWarns());
        }

        // A level drop no DowngradeFacility call made (the scene-change poll, another mod,
        // an upgrade scope of the same facility) stays informational, as before.
        [Fact]
        public void DowngradeOutsideADowngradeScope_CarriesNoCost_AndWritesNoRow()
        {
            var recorder = new GameStateRecorder();
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0.5f, 0f, DowngradeUt);
            FacilityUpgradeCapture.BeginScope(Admin, fundsCharged: false, fundsAtOpen: double.NaN);
            recorder.RecordFacilityLevelChangeForTesting(Admin, 1f, 0.5f, DowngradeUt + 1.0);
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            Assert.All(GameStateStore.Events.Where(e => e.eventType == GameStateEventType.FacilityDowngraded),
                e => Assert.Null(e.detail));
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityUpgrade);
            Assert.Null(GameStateEventConverter.ConvertEvent(
                GameStateStore.Events.First(e => e.eventType == GameStateEventType.FacilityDowngraded), null));
        }

        [Fact]
        public void TaggedDowngrade_WaitsForTheCommit_AndTheCommitRowCarriesTheCost()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            var recorder = new GameStateRecorder();

            OpenDowngradeScopeAndDebit(Admin, 350000.0, 249950.0);
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0.5f, 0f, 1500.0);
            FacilityUpgradeCapture.EndScope("downgrade-returned");

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityUpgrade);
            var converted = GameStateEventConverter.ConvertEvents(GameStateStore.Events, "rec-live", 1000.0, 2000.0);
            var row = converted.Single(a => a.Type == GameActionType.FacilityUpgrade);
            Assert.True(row.FacilityDowngrade);
            Assert.Equal("rec-live", row.RecordingId);
            Assert.Equal(100050f, row.FacilityCost);
            Assert.Equal(1, row.ToLevel);
        }

        // Stock's Rebuild button exists only on an out-of-service facility, so its
        // ResetStructures always repairs: the free repairs are written with the downgrade row.
        [Fact]
        public void DowngradeOfADestroyedFacility_WritesTheFreeRepairWithTheDowngrade_InOneBatch()
        {
            Ledger.SeedInitialFunds(500000.0);
            var recorder = new GameStateRecorder();
            UpgradeAdminToTierTwo(recorder);
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, false, 800.0, 0f, "test");
            Assert.True(LedgerOrchestrator.Facilities.IsFacilityDestroyed(AdminBuilding));

            AddStoreEvent(StructureConstructionDebit(DowngradeUt, 350000.0, 249950.0));
            OpenDowngradeScopeAndDebit(Admin, 350000.0, 249950.0);
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, true, DowngradeUt, 0f, "facility-level-reset");
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);

            logLines.Clear();
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0.5f, 0f, DowngradeUt);
            FacilityUpgradeCapture.EndScope("downgrade-returned");

            Assert.Contains(logLines, l => l.Contains("KSC spending batch recorded")
                && l.Contains("reason=facility-downgrade") && l.Contains("rows=2"));
            Assert.False(LedgerOrchestrator.Facilities.IsFacilityDestroyed(AdminBuilding));
            Assert.Equal(1, LedgerOrchestrator.Facilities.GetFacilityLevel(Admin));
            Assert.Equal(249950.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            Assert.Empty(FacilityOrReconcileWarns());
        }

        [Fact]
        public void DowngradeScope_DebitNoEventRead_WarnsAtClose_InItsOwnWords()
        {
            OpenDowngradeScopeAndDebit(Admin, 350000.0, 249950.0);
            FacilityUpgradeCapture.EndScope("downgrade-returned");

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("no FacilityDowngraded event read it") && l.Contains(Admin));
            Assert.Contains(logLines, l => l.Contains("FacilityDowngrade scope close")
                && l.Contains("reason=downgrade-returned") && l.Contains("costRead=False"));
        }

        [Fact]
        public void DowngradeMarker_RoundTripsThroughTheLedgerCodec_AndIsSparse()
        {
            var parent = new ConfigNode("ROOT");
            new GameAction
            {
                UT = DowngradeUt, Type = GameActionType.FacilityUpgrade, FacilityId = Admin,
                ToLevel = 1, FacilityCost = 100050f, FacilityDowngrade = true,
            }.SerializeInto(parent);
            new GameAction
            {
                UT = UpgradeUt, Type = GameActionType.FacilityUpgrade, FacilityId = Admin,
                ToLevel = 2, FacilityCost = 150000f,
            }.SerializeInto(parent);

            var nodes = parent.GetNodes("GAME_ACTION");
            Assert.Equal("True", nodes[0].GetValue("facilityDowngrade"));
            Assert.Null(nodes[1].GetValue("facilityDowngrade"));
            var down = GameAction.DeserializeFrom(nodes[0]);
            var up = GameAction.DeserializeFrom(nodes[1]);
            Assert.True(down.FacilityDowngrade);
            Assert.Equal(1, down.ToLevel);
            Assert.Equal(100050f, down.FacilityCost);
            Assert.False(up.FacilityDowngrade);
        }

        [Fact]
        public void DowngradeRow_ReadsAsADowngrade_AndReservesNothing()
        {
            var down = new GameAction
            {
                UT = DowngradeUt, Type = GameActionType.FacilityUpgrade, FacilityId = Admin,
                ToLevel = 1, FacilityCost = 100050f, FacilityDowngrade = true,
            };
            var up = new GameAction
            {
                UT = UpgradeUt, Type = GameActionType.FacilityUpgrade, FacilityId = Admin,
                ToLevel = 2, FacilityCost = 150000f,
            };

            string downText = GameActionDisplay.GetDescription(down, Game.Modes.CAREER);
            Assert.StartsWith("Downgrade ", downText);
            Assert.Contains("Lv.1", downText);
            Assert.StartsWith("Upgrade ", GameActionDisplay.GetDescription(up, Game.Modes.CAREER));

            // The stock-screen reservation reads "Upgraded to level N": a committed
            // downgrade is not an upgrade the player must wait for.
            CommittedFutureKind kind;
            string key;
            Assert.False(CommittedFutureIndex.TryClassify(down, out kind, out key));
            Assert.True(CommittedFutureIndex.TryClassify(up, out kind, out key));
        }

        [Fact]
        public void LegacyUpgradeCostPasses_LeaveADowngradeRowAlone()
        {
            var down = new GameAction
            {
                UT = DowngradeUt, Type = GameActionType.FacilityUpgrade, FacilityId = Admin,
                ToLevel = 1, FacilityCost = 0f, FacilityDowngrade = true,
            };
            var rows = new List<GameAction> { down };

            var repair = LedgerLoadMigration.RepairZeroCostFacilityUpgradeActionsOnLoad(
                new List<GameStateEvent> { StructureConstructionDebit(DowngradeUt, 350000.0, 249950.0) }, rows);
            Assert.Equal(0, repair.ZeroCostRows);

            var estimate = LedgerLoadMigration.EstimateLegacyFacilityUpgradeCosts(
                rows, new List<GameStateEvent>(), default(LedgerLoadMigration.FacilityCostTableProbe),
                default(LedgerLoadMigration.FundsSeedCaptureMoment), 0.0);
            Assert.Equal(0, estimate.Candidates);
            Assert.Equal(0f, down.FacilityCost);
        }

        // ================================================================
        // Harmony target: a stock rename must red here, not at runtime
        // ================================================================

        [Fact]
        public void DowngradeSpendPatch_TargetsDowngradeFacilityBool_WithAPrefixAndAFinalizer()
        {
            Assert.NotEmpty(typeof(FacilityDowngradeSpendPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo target = typeof(SpaceCenterBuilding).GetMethod(
                "DowngradeFacility",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(bool) }, modifiers: null);
            Assert.NotNull(target);
            Assert.Equal("deduceFunds", target.GetParameters()[0].Name);

            var prefix = typeof(FacilityDowngradeSpendPatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.Equal(new[] { "__instance", "deduceFunds" }, prefix.GetParameters().Select(p => p.Name).ToArray());
            var finalizer = typeof(FacilityDowngradeSpendPatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(finalizer);
            Assert.Equal(typeof(Exception), finalizer.ReturnType);
        }
    }
}
