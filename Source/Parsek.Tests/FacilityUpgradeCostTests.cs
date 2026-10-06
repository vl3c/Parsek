using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KSCACTION-FACILITY-UPGRADE-LEDGER-COST-ZERO: every KSC facility upgrade reached the
    /// ledger at cost 0, because the FacilityUpgraded event carried no <c>cost=</c>. The
    /// UpgradeFacility scope (<see cref="FacilityUpgradeCapture"/>) now reads stock's
    /// StructureConstruction debit and stamps it on the event; a load-time repair gives old
    /// cost-0 rows the debit their saved events still prove. The stock handler takes an
    /// UpgradeableFacility (a MonoBehaviour) and reads Planetarium, so these cells drive the
    /// recorder core through its test seam, plus the pure decisions and the scope.
    /// </summary>
    [Collection("Sequential")]
    public class FacilityUpgradeCostTests : IDisposable
    {
        private const string Admin = "SpaceCenter/Administration";
        private const string Tracking = "SpaceCenter/TrackingStation";
        private const string AdminBuilding = "SpaceCenter/Administration/Facility/mainBuilding";
        private const double UpgradeUt = 473.0;

        private readonly List<string> logLines = new List<string>();

        public FacilityUpgradeCostTests()
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
            // No flight: untagged, no live recorder - the KSC shape.
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

        // A headless recalc warns about Planetarium (CommittedFutureIndex.CurrentUT); only the
        // capture's own and the KSC reconciler's warnings speak to this fix.
        private List<string> UpgradeOrReconcileWarns() =>
            logLines.Where(l => l.Contains("[WARN]")
                && (l.Contains("KSC reconciliation") || l.Contains("FacilityUpgrade"))).ToList();

        private static GameStateEvent StructureConstructionDebit(
            double ut, double before, double after, string tag = "") =>
            new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = "StructureConstruction",
                recordingId = tag,
                valueBefore = before,
                valueAfter = after,
            };

        private static GameAction ZeroCostUpgradeRow(double ut, string facility = Admin, string recordingId = null) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = facility,
                ToLevel = 2,
                FacilityCost = 0f,
                RecordingId = recordingId,
            };

        /// <summary>Stock's order inside UpgradeFacility: scope open, the debit, then SetLevel.</summary>
        private static void OpenScopeAndDebit(string facility, double fundsBefore, double fundsAfter)
        {
            FacilityUpgradeCapture.BeginScope(facility, fundsCharged: true, fundsAtOpen: fundsBefore);
            FacilityUpgradeCapture.ObserveFundsChange(
                TransactionReasons.StructureConstruction, fundsBefore, fundsAfter);
        }

        // ================================================================
        // Pure decisions
        // ================================================================

        [Fact]
        public void ResolveUpgradeCost_CareerDebitObserved_IsTheDebit()
        {
            string source;
            Assert.Equal(150000f, FacilityUpgradeCapture.ResolveUpgradeCost(true, 1, -150000.0, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceObservedDebit, source);
        }

        [Fact]
        public void ResolveUpgradeCost_NoFundsCharged_IsZero_EvenWithAnObservedChange()
        {
            // Science / Sandbox (Funding.Instance null) or deduceFunds=false: stock charges nothing.
            string source;
            Assert.Equal(0f, FacilityUpgradeCapture.ResolveUpgradeCost(false, 1, -150000.0, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceNoFundsCharged, source);
        }

        [Theory]
        [InlineData(0, -150000.0, "no-debit-observed")]
        [InlineData(1, double.NaN, "unmeasured-debit")]
        [InlineData(1, 0.0, "non-debit")]
        [InlineData(1, 500.0, "non-debit")]
        public void ResolveUpgradeCost_UnreadableDebit_IsZero_WithTheReason(
            int firings, double delta, string expectedSource)
        {
            string source;
            Assert.Equal(0f, FacilityUpgradeCapture.ResolveUpgradeCost(true, firings, delta, out source));
            Assert.Equal(expectedSource, source);
        }

        [Fact]
        public void ResolveObservedFundsDelta_UsesTheRecorderBaseline_AndTheOpenPoolOnlyForAnUnseededFirstFiring()
        {
            Assert.Equal(-150000.0, FacilityUpgradeCapture.ResolveObservedFundsDelta(500000.0, 350000.0, 999.0, true));
            Assert.Equal(-150000.0, FacilityUpgradeCapture.ResolveObservedFundsDelta(double.NaN, 350000.0, 500000.0, true));
            Assert.True(double.IsNaN(FacilityUpgradeCapture.ResolveObservedFundsDelta(double.NaN, 350000.0, 500000.0, false)));
            Assert.True(double.IsNaN(FacilityUpgradeCapture.ResolveObservedFundsDelta(double.NaN, 350000.0, double.NaN, true)));
        }

        [Fact]
        public void BuildUpgradeDetail_IsCultureInvariant_AndReachesFacilityCostThroughTheConverter()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string detail = FacilityUpgradeCapture.BuildUpgradeDetail(148500.5f);
                Assert.Equal("cost=148500.5", detail);

                var evt = new GameStateEvent
                {
                    ut = UpgradeUt,
                    eventType = GameStateEventType.FacilityUpgraded,
                    key = Admin,
                    detail = detail,
                    valueBefore = 0f,
                    valueAfter = 0.5f,
                };
                var action = GameStateEventConverter.ConvertEvent(evt, null);
                Assert.Equal(GameActionType.FacilityUpgrade, action.Type);
                Assert.Equal(148500.5f, action.FacilityCost);
                Assert.Equal(2, action.ToLevel);
                Assert.Equal(Admin, action.FacilityId);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ================================================================
        // The scope
        // ================================================================

        [Fact]
        public void BlockedUpgrade_OpensNoScope_AndSkipsTheOriginal()
        {
            Assert.False(FacilityUpgradeCapture.AllowAndOpenScope(true, Admin, true, 500000.0));
            Assert.False(FacilityUpgradeCapture.IsScopeActive);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("FacilityUpgrade scope not opened") && l.Contains(Admin));

            // A level change with no scope reads cost 0 (nothing stock debited is known).
            string source;
            Assert.Equal(0f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceNoScope, source);
        }

        [Fact]
        public void AllowedUpgrade_OpensTheScope_AndRunsTheOriginal()
        {
            Assert.True(FacilityUpgradeCapture.AllowAndOpenScope(false, Admin, true, 500000.0));
            Assert.True(FacilityUpgradeCapture.IsScopeActive);
            Assert.Contains(logLines, l => l.Contains("FacilityUpgrade scope open")
                && l.Contains("fundsCharged=True") && l.Contains("fundsAtOpen=500000"));
            FacilityUpgradeCapture.EndScope("test");
            Assert.False(FacilityUpgradeCapture.IsScopeActive);
        }

        [Fact]
        public void Scope_ReadsOnlyTheStructureConstructionDebit_Once()
        {
            FacilityUpgradeCapture.BeginScope(Admin, true, 500000.0);
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureRepair, 500000.0, 490000.0);
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureConstruction, 490000.0, 340000.0);

            string source;
            Assert.Equal(150000f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceObservedDebit, source);

            // After the read, later StructureConstruction moves (a recalc patch, say) are ignored
            // and a second read is refused.
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureConstruction, 340000.0, 1.0);
            Assert.Equal(0f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceAlreadyConsumed, source);
            FacilityUpgradeCapture.EndScope("test");
        }

        [Fact]
        public void Scope_StrategyDiscount_RecordsTheNetDebit()
        {
            // Aggressive Negotiations (CurrencyOperation on StructureConstruction): stock's
            // Funding.OnCurrenciesModified fires FundsChanged(StructureConstruction) with the
            // discounted pool, then AddFunds fires it again with no further move. The ledger
            // wants the net debit - StrategyConversionCapture records no funds leg for this
            // event-derived reason.
            FacilityUpgradeCapture.BeginScope(Admin, true, 500000.0);
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureConstruction, 500000.0, 351500.0);
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureConstruction, 351500.0, 351500.0);

            string source;
            Assert.Equal(148500f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            FacilityUpgradeCapture.EndScope("test");
        }

        [Fact]
        public void Scope_UnseededRecorderBaseline_FallsBackToThePoolTheScopeOpenedWith()
        {
            FacilityUpgradeCapture.BeginScope(Admin, true, 500000.0);
            FacilityUpgradeCapture.ObserveFundsChange(TransactionReasons.StructureConstruction, double.NaN, 350000.0);

            string source;
            Assert.Equal(150000f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            FacilityUpgradeCapture.EndScope("test");
        }

        [Fact]
        public void Scope_FundsChargedButNoDebitSeen_ReadsZero_AndWarns()
        {
            FacilityUpgradeCapture.BeginScope(Admin, true, 500000.0);
            string source;
            Assert.Equal(0f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceNoDebitObserved, source);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("debit could not be read"));
            FacilityUpgradeCapture.EndScope("test");
        }

        [Fact]
        public void Scope_AnotherFacilitysLevelChange_DoesNotTakeTheDebit()
        {
            OpenScopeAndDebit(Admin, 500000.0, 350000.0);
            string source;
            Assert.Equal(0f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Tracking, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceFacilityMismatch, source);
            Assert.Equal(150000f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            FacilityUpgradeCapture.EndScope("test");
        }

        [Fact]
        public void Scope_DebitNoEventRead_WarnsAtClose()
        {
            OpenScopeAndDebit(Admin, 500000.0, 350000.0);
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("no FacilityUpgraded event read it") && l.Contains(Admin));
            Assert.Contains(logLines, l => l.Contains("FacilityUpgrade scope close")
                && l.Contains("reason=upgrade-returned") && l.Contains("costRead=False"));
        }

        [Fact]
        public void Scope_CannotAfford_ClosesQuietly()
        {
            // Stock returns before AddFunds when the player cannot afford the upgrade.
            FacilityUpgradeCapture.BeginScope(Admin, true, 100.0);
            logLines.Clear();
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]"));
            Assert.Contains(logLines, l => l.Contains("FacilityUpgrade scope close") && l.Contains("firings=0"));
        }

        [Fact]
        public void Scope_ReenteredWhileOpen_ClosesTheOpenOneFirst()
        {
            FacilityUpgradeCapture.BeginScope(Admin, true, 1.0);
            FacilityUpgradeCapture.BeginScope(Tracking, true, 1.0);

            Assert.True(FacilityUpgradeCapture.IsScopeActive);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("was still open"));
            FacilityUpgradeCapture.EndScope("test");
        }

        [Fact]
        public void RecorderFundsHandler_FeedsTheScope_WithItsOwnBaseline()
        {
            // The real OnFundsChanged handler (private, reached by reflection). Under resource
            // suppression it returns before reading Planetarium (absent headless); the scope
            // observation runs ahead of that early return.
            var recorder = new GameStateRecorder();
            var lastFunds = typeof(GameStateRecorder).GetField("lastFunds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var onFundsChanged = typeof(GameStateRecorder).GetMethod("OnFundsChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(lastFunds);
            Assert.NotNull(onFundsChanged);
            lastFunds.SetValue(recorder, 500000.0);

            FacilityUpgradeCapture.BeginScope(Admin, true, 999999.0);
            GameStateRecorder.SuppressResourceEvents = true;
            onFundsChanged.Invoke(recorder, new object[] { 490000.0, TransactionReasons.VesselRollout });
            onFundsChanged.Invoke(recorder, new object[] { 340000.0, TransactionReasons.StructureConstruction });
            GameStateRecorder.SuppressResourceEvents = false;

            string source;
            Assert.Equal(150000f, FacilityUpgradeCapture.ConsumeCostForUpgrade(Admin, out source));
            Assert.Equal(FacilityUpgradeCapture.SourceObservedDebit, source);
            FacilityUpgradeCapture.EndScope("test");
        }

        // ================================================================
        // Recorder -> event -> ledger
        // ================================================================

        [Fact]
        public void KscUpgrade_InsideTheScope_ChargesTheDebit_AndReconcilesWithoutAWarn()
        {
            Ledger.SeedInitialFunds(500000.0);
            var recorder = new GameStateRecorder();

            // Stock order: the debit (recorded as FundsChanged by the funds handler)...
            AddStoreEvent(StructureConstructionDebit(UpgradeUt, 500000.0, 350000.0));
            OpenScopeAndDebit(Admin, 500000.0, 350000.0);
            logLines.Clear();
            // ... then SetLevel fires OnKSCFacilityUpgrading.
            Assert.True(recorder.RecordFacilityLevelChangeForTesting(Admin, 0f, 0.5f, UpgradeUt));
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.FacilityUpgraded);
            Assert.Equal("cost=150000", evt.detail);
            var row = Ledger.Actions.Single(a => a.Type == GameActionType.FacilityUpgrade);
            Assert.Equal(150000f, row.FacilityCost);
            Assert.Equal(2, row.ToLevel);
            Assert.Equal(UpgradeUt, row.UT);

            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("Game state: FacilityUpgraded") && l.Contains("(event-driven)")
                && l.Contains("cost=150000") && l.Contains("costSource=observed-debit"));
            // The row pairs with the StructureConstruction debit: no reconcile WARN, no scope WARN.
            Assert.Empty(UpgradeOrReconcileWarns());
            // The funds walk charges it exactly once.
            Assert.Equal(350000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            Assert.Contains(logLines, l => l.Contains("FacilityUpgrade: -150000")
                && l.Contains("facilityId=" + Admin));
        }

        [Fact]
        public void CostZeroRow_TheOldShape_WalksToThePreUpgradePool()
        {
            // Pins the bug's shape: the same debit with a cost-0 row walks to the PRE-upgrade
            // pool (the reconciler skips a zero expectation, so only the funds guard saw it).
            Ledger.SeedInitialFunds(500000.0);
            AddStoreEvent(StructureConstructionDebit(UpgradeUt, 500000.0, 350000.0));
            LedgerOrchestrator.OnKscSpending(new GameStateEvent
            {
                ut = UpgradeUt,
                eventType = GameStateEventType.FacilityUpgraded,
                key = Admin,
                valueBefore = 0f,
                valueAfter = 0.5f,
            });

            Assert.Equal(0f, Ledger.Actions.Single(a => a.Type == GameActionType.FacilityUpgrade).FacilityCost);
            Assert.Equal(500000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void KscUpgrade_ScienceOrSandbox_WritesAnExplicitZero()
        {
            var recorder = new GameStateRecorder();
            FacilityUpgradeCapture.BeginScope(Admin, fundsCharged: false, fundsAtOpen: double.NaN);
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0f, 0.5f, UpgradeUt);
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.FacilityUpgraded);
            Assert.Equal("cost=0", evt.detail);
            Assert.Equal(0f, Ledger.Actions.Single(a => a.Type == GameActionType.FacilityUpgrade).FacilityCost);
            Assert.Contains(logLines, l => l.Contains("costSource=no-funds-charged"));
            Assert.Empty(UpgradeOrReconcileWarns());
        }

        [Fact]
        public void TaggedUpgrade_WaitsForTheCommit_AndTheCommitRowCarriesTheCost()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            var recorder = new GameStateRecorder();

            OpenScopeAndDebit(Admin, 500000.0, 350000.0);
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0f, 0.5f, 1500.0);
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityUpgrade);
            var converted = GameStateEventConverter.ConvertEvents(GameStateStore.Events, "rec-live", 1000.0, 2000.0);
            var row = converted.Single(a => a.Type == GameActionType.FacilityUpgrade);
            Assert.Equal("rec-live", row.RecordingId);
            Assert.Equal(150000f, row.FacilityCost);
        }

        [Fact]
        public void Downgrade_CarriesNoCost_AndWritesNoRow()
        {
            var recorder = new GameStateRecorder();
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0.5f, 0f, UpgradeUt);

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.FacilityDowngraded);
            Assert.Null(evt.detail);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityUpgrade);
        }

        [Fact]
        public void UpgradeOfADestroyedFacility_WritesTheFreeRepairWithTheUpgrade_InOneBatch()
        {
            Ledger.SeedInitialFunds(500000.0);
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, false, 100.0, 0f, "test");

            AddStoreEvent(StructureConstructionDebit(UpgradeUt, 500000.0, 350000.0));
            OpenScopeAndDebit(Admin, 500000.0, 350000.0);
            // ResetStructures runs between the debit and SetLevel: its free repair is queued...
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, true, UpgradeUt, 0f, "facility-level-reset");
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);
            Assert.True(LedgerOrchestrator.Facilities.IsFacilityDestroyed(AdminBuilding));

            // ... and written with the upgrade row, so no recalc runs on the debit alone.
            logLines.Clear();
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0f, 0.5f, UpgradeUt);
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            Assert.Contains(logLines, l => l.Contains("KSC spending batch recorded")
                && l.Contains("reason=facility-upgrade") && l.Contains("rows=2"));
            Assert.Equal(0f, Ledger.Actions.Single(a => a.Type == GameActionType.FacilityRepair).FacilityCost);
            Assert.Equal(150000f, Ledger.Actions.Single(a => a.Type == GameActionType.FacilityUpgrade).FacilityCost);
            Assert.False(LedgerOrchestrator.Facilities.IsFacilityDestroyed(AdminBuilding));
            Assert.Equal(350000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            Assert.Empty(UpgradeOrReconcileWarns());
        }

        [Fact]
        public void TaggedUpgradeOfADestroyedFacility_LeavesTheFreeRepairForTheCommit()
        {
            // The batch above holds only repairs the forward gate already let through: a repair
            // owned by a live recording is written at commit, never inside the upgrade batch.
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, false, 100.0, 0f, "test");

            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            OpenScopeAndDebit(Admin, 500000.0, 350000.0);
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, true, 1500.0, 0f, "facility-level-reset");
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0f, 0.5f, 1500.0);
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityUpgrade);
            var converted = GameStateEventConverter.ConvertEvents(GameStateStore.Events, "rec-live", 1000.0, 2000.0);
            Assert.Single(converted, a => a.Type == GameActionType.FacilityRepair);
            Assert.Equal(150000f, converted.Single(a => a.Type == GameActionType.FacilityUpgrade).FacilityCost);
        }

        [Fact]
        public void QueuedFreeRepair_WithNoUpgradeEvent_IsWrittenWhenTheScopeCloses()
        {
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, false, 100.0, 0f, "test");
            FacilityUpgradeCapture.BeginScope(Admin, false, double.NaN);
            recorder.RecordBuildingTransitionForTesting(AdminBuilding, true, UpgradeUt, 0f, "facility-level-reset");
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);

            FacilityUpgradeCapture.EndScope("upgrade-threw");

            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);
            Assert.Contains(logLines, l => l.Contains("FacilityUpgrade scope close")
                && l.Contains("reason=upgrade-threw") && l.Contains("rowsToWrite=1"));
        }

        [Fact]
        public void FundsModuleWalk_ChargesTheUpgradeCost_AndRespectsARewindCutoff()
        {
            Ledger.SeedInitialFunds(500000.0);
            var recorder = new GameStateRecorder();
            AddStoreEvent(StructureConstructionDebit(UpgradeUt, 500000.0, 350000.0));
            OpenScopeAndDebit(Admin, 500000.0, 350000.0);
            recorder.RecordFacilityLevelChangeForTesting(Admin, 0f, 0.5f, UpgradeUt);
            FacilityUpgradeCapture.EndScope("upgrade-returned");

            // A rewind cutoff before the upgrade does not charge it; the full walk does.
            LedgerOrchestrator.RecalculateAndPatch(UpgradeUt - 10.0);
            Assert.Equal(500000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(350000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
        }

        // ================================================================
        // Load-time repair of rows written at cost 0
        // ================================================================

        [Fact]
        public void Repair_ZeroCostRowWithItsSavedDebit_TakesTheDebit()
        {
            var row = ZeroCostUpgradeRow(UpgradeUt);
            var events = new List<GameStateEvent>
            {
                new GameStateEvent { ut = UpgradeUt, eventType = GameStateEventType.FacilityUpgraded, key = Admin, valueAfter = 0.5f },
                StructureConstructionDebit(UpgradeUt, 465808.0, 315808.0),
            };

            var result = LedgerLoadMigration.RepairZeroCostFacilityUpgradeActionsOnLoad(events, new List<GameAction> { row });

            Assert.Equal(150000f, row.FacilityCost);
            Assert.Equal(1, result.ZeroCostRows);
            Assert.Equal(1, result.Repaired);
            Assert.Contains("repaired=1", result.Format());
        }

        [Fact]
        public void Repair_TaggedRow_PairsOnlyWithItsOwnRecordingsDebit()
        {
            var row = ZeroCostUpgradeRow(UpgradeUt, recordingId: "rec-A");
            var events = new List<GameStateEvent>
            {
                StructureConstructionDebit(UpgradeUt, 500000.0, 350000.0, tag: ""),
            };
            float cost;
            string refusal;
            Assert.False(LedgerLoadMigration.TryResolveLegacyFacilityUpgradeCost(
                row, events, new List<GameAction> { row }, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyUpgradeRefusalNoDebitEvent, refusal);

            events.Add(StructureConstructionDebit(UpgradeUt, 500000.0, 380000.0, tag: "rec-A"));
            Assert.True(LedgerLoadMigration.TryResolveLegacyFacilityUpgradeCost(
                row, events, new List<GameAction> { row }, out cost, out refusal));
            Assert.Equal(120000f, cost);
        }

        [Fact]
        public void Repair_RefusesEveryRowItCannotProve_AndCountsWhy()
        {
            // a: no debit in the window (pruned, or outside 0.1 s)
            var noEvent = ZeroCostUpgradeRow(1000.0);
            // b: the debit is a credit
            var credit = ZeroCostUpgradeRow(2000.0);
            // c: a downgrade (the other StructureConstruction debit) shares the window
            var downgrade = ZeroCostUpgradeRow(3000.0);
            // d: two upgrade rows share one (coalesced) debit
            var twinA = ZeroCostUpgradeRow(4000.0, Admin);
            var twinB = ZeroCostUpgradeRow(4000.05, Tracking);
            // e: the upgrade event carries an explicit cost= (a captured zero)
            var explicitZero = ZeroCostUpgradeRow(5000.0);
            // f: an already-costed row is not a candidate at all
            var costed = ZeroCostUpgradeRow(6000.0);
            costed.FacilityCost = 75000f;

            var events = new List<GameStateEvent>
            {
                StructureConstructionDebit(1000.2, 500000.0, 350000.0),
                StructureConstructionDebit(2000.0, 350000.0, 360000.0),
                StructureConstructionDebit(3000.0, 360000.0, 260000.0),
                new GameStateEvent { ut = 3000.0, eventType = GameStateEventType.FacilityDowngraded, key = Tracking, valueBefore = 0.5f },
                StructureConstructionDebit(4000.0, 260000.0, 100000.0),
                new GameStateEvent { ut = 5000.0, eventType = GameStateEventType.FacilityUpgraded, key = Admin, detail = "cost=0", valueAfter = 0.5f },
                StructureConstructionDebit(5000.0, 100000.0, 90000.0),
                StructureConstructionDebit(6000.0, 90000.0, 15000.0),
            };
            var rows = new List<GameAction> { noEvent, credit, downgrade, twinA, twinB, explicitZero, costed };

            var result = LedgerLoadMigration.RepairZeroCostFacilityUpgradeActionsOnLoad(events, rows);

            Assert.Equal(6, result.ZeroCostRows);
            Assert.Equal(0, result.Repaired);
            Assert.Equal(1, result.NoDebitEvent);
            Assert.Equal(1, result.NonDebit);
            Assert.Equal(1, result.DowngradeInWindow);
            Assert.Equal(2, result.AmbiguousRows);
            Assert.Equal(1, result.ExplicitZero);
            Assert.All(rows.Where(r => !ReferenceEquals(r, costed)), r => Assert.Equal(0f, r.FacilityCost));
            Assert.Equal(75000f, costed.FacilityCost);
        }

        [Fact]
        public void Repair_TwoDebitEventsInTheWindow_IsAmbiguous()
        {
            var row = ZeroCostUpgradeRow(UpgradeUt);
            // Two same-key events inside the window only survive coalescing when another
            // event sits between them; either way one row cannot claim both.
            var events = new List<GameStateEvent>
            {
                StructureConstructionDebit(UpgradeUt, 500000.0, 400000.0),
                StructureConstructionDebit(UpgradeUt + 0.05, 400000.0, 350000.0),
            };
            float cost;
            string refusal;
            Assert.False(LedgerLoadMigration.TryResolveLegacyFacilityUpgradeCost(
                row, events, new List<GameAction> { row }, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyUpgradeRefusalAmbiguousEvents, refusal);
            Assert.Equal(0f, cost);
        }

        [Fact]
        public void Repair_OnLoad_LogsOneSummaryLine_AndTheWalkThenChargesTheRow()
        {
            Ledger.SeedInitialFunds(465808.0);
            Ledger.AddAction(ZeroCostUpgradeRow(UpgradeUt));
            AddStoreEvent(StructureConstructionDebit(UpgradeUt, 236416.75, 86416.75));

            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(465808.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);

            logLines.Clear();
            Assert.Equal(1, LedgerOrchestrator.RepairZeroCostFacilityUpgradeActionsOnLoad(
                GameStateStore.Events, Ledger.Actions));
            Assert.Single(logLines, l => l.Contains("[INFO]")
                && l.Contains("repaired cost-0 facility-upgrade rows")
                && l.Contains("zeroCostRows=1 repaired=1"));

            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(465808.0 - 150000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);

            // Idempotent: a second load finds no cost-0 row and stays silent.
            logLines.Clear();
            Assert.Equal(0, LedgerOrchestrator.RepairZeroCostFacilityUpgradeActionsOnLoad(
                GameStateStore.Events, Ledger.Actions));
            Assert.DoesNotContain(logLines, l => l.Contains("facility-upgrade rows"));
        }

        [Fact]
        public void Repair_OnLoad_UnprovableRow_IsKept_AndLoggedVerbose()
        {
            Ledger.AddAction(ZeroCostUpgradeRow(UpgradeUt));

            Assert.Equal(0, LedgerOrchestrator.RepairZeroCostFacilityUpgradeActionsOnLoad(
                GameStateStore.Events, Ledger.Actions));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE]")
                && l.Contains("kept as stored") && l.Contains("noDebitEvent=1"));
        }

        // ================================================================
        // Cost-table estimate of rows no saved debit proves (owner ruling 2026-10-07)
        // ================================================================

        private const double CareerStartUt = 0.0;
        private const double StartingFunds = 500000.0;

        // Stock-shaped three-tier table: level 1 is free, then the two upgrade prices.
        private static readonly float[] AdminLevelCosts = { 0f, 150000f, 500000f };

        private static LedgerLoadMigration.FacilityCostTableProbe LoadedCareerTable(
            float multiplier = 1f, Func<string, float[]> levelCosts = null) =>
            new LedgerLoadMigration.FacilityCostTableProbe
            {
                GameAvailable = true,
                IsCareer = true,
                MultiplierReadable = true,
                FundsLossMultiplier = multiplier,
                FacilityDataLoaded = true,
                LevelCosts = levelCosts ?? (id => id == Admin || id == Tracking ? AdminLevelCosts : null),
            };

        private static GameAction UpgradeRowTo(double ut, int toLevel, string facility = Admin)
        {
            var row = ZeroCostUpgradeRow(ut, facility);
            row.ToLevel = toLevel;
            return row;
        }

        [Fact]
        public void Estimate_RowCost_IsTheTargetLevelsCostTimesTodaysMultiplier()
        {
            float cost;
            string refusal;
            Assert.True(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 2), AdminLevelCosts, 1.25f, out cost, out refusal));
            Assert.Equal(187500f, cost);
            Assert.Null(refusal);

            Assert.True(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 3), AdminLevelCosts, 1f, out cost, out refusal));
            Assert.Equal(500000f, cost);
        }

        [Fact]
        public void Estimate_RefusesARowTheTableCannotPrice()
        {
            float cost;
            string refusal;

            // The ledger's 1-based tier comes from stock's 0 / 0.5 / 1 normalized levels, so
            // only a three-tier facility maps back onto a table index.
            Assert.False(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 2), new[] { 0f, 1f, 2f, 3f }, 1f, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyEstimateRefusalLevelMismatch, refusal);
            Assert.False(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 1), AdminLevelCosts, 1f, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyEstimateRefusalLevelMismatch, refusal);
            Assert.False(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 4), AdminLevelCosts, 1f, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyEstimateRefusalLevelMismatch, refusal);

            // A free level, or a zero multiplier, prices the upgrade at 0: the row is right.
            Assert.False(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 2), new[] { 0f, 0f, 10f }, 1f, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyEstimateRefusalZeroCost, refusal);
            Assert.False(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 2), AdminLevelCosts, 0f, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyEstimateRefusalZeroCost, refusal);

            Assert.False(LedgerLoadMigration.TryEstimateLegacyFacilityUpgradeCost(
                UpgradeRowTo(UpgradeUt, 2), null, 1f, out cost, out refusal));
            Assert.Equal(LedgerLoadMigration.LegacyEstimateRefusalUnknownFacility, refusal);
            Assert.Equal(0f, cost);
        }

        [Fact]
        public void ClassifyFundsSeedCaptureMoment_OnlyASeedTakenFromTheCareerStartBaselineIsDated()
        {
            Assert.Equal(LedgerLoadMigration.FundsSeedCaptureMoment.NoSeed,
                LedgerLoadMigration.ClassifyFundsSeedCaptureMoment(
                    hasSeed: false, seedFunds: 0f, hasInitialBaseline: true, initialBaselineFunds: StartingFunds));
            Assert.Equal(LedgerLoadMigration.FundsSeedCaptureMoment.CareerStart,
                LedgerLoadMigration.ClassifyFundsSeedCaptureMoment(
                    hasSeed: true, seedFunds: (float)StartingFunds, hasInitialBaseline: true,
                    initialBaselineFunds: StartingFunds));
            // A seed read off the live pool records no moment: it may already hold the debits.
            Assert.Equal(LedgerLoadMigration.FundsSeedCaptureMoment.Unknown,
                LedgerLoadMigration.ClassifyFundsSeedCaptureMoment(
                    hasSeed: true, seedFunds: 350000f, hasInitialBaseline: true,
                    initialBaselineFunds: StartingFunds));
            Assert.Equal(LedgerLoadMigration.FundsSeedCaptureMoment.Unknown,
                LedgerLoadMigration.ClassifyFundsSeedCaptureMoment(
                    hasSeed: true, seedFunds: (float)StartingFunds, hasInitialBaseline: false,
                    initialBaselineFunds: 0.0));
            // A zero career start never seeds from the baseline (DecideInitialFundsSeed).
            Assert.Equal(LedgerLoadMigration.FundsSeedCaptureMoment.Unknown,
                LedgerLoadMigration.ClassifyFundsSeedCaptureMoment(
                    hasSeed: true, seedFunds: 0f, hasInitialBaseline: true, initialBaselineFunds: 0.0));
        }

        [Fact]
        public void EstimatePass_CareerStartSeed_PricesEveryUnprovenRow_AndHonoursTheSavedEvidence()
        {
            var unproven = UpgradeRowTo(1000.0, 2);
            var unprovenTier3 = UpgradeRowTo(1500.0, 3, Tracking);
            var explicitZero = UpgradeRowTo(2000.0, 2);
            var credit = UpgradeRowTo(3000.0, 2);
            var unknownFacility = UpgradeRowTo(4000.0, 2, "SpaceCenter/ModdedHangar");
            var beforeSeed = UpgradeRowTo(CareerStartUt, 2);
            var costed = UpgradeRowTo(5000.0, 2);
            costed.FacilityCost = 75000f;
            var rows = new List<GameAction>
                { unproven, unprovenTier3, explicitZero, credit, unknownFacility, beforeSeed, costed };
            var events = new List<GameStateEvent>
            {
                new GameStateEvent { ut = 2000.0, eventType = GameStateEventType.FacilityUpgraded, key = Admin, detail = "cost=0", valueAfter = 0.5f },
                StructureConstructionDebit(3000.0, 350000.0, 360000.0),
            };

            var result = LedgerLoadMigration.EstimateLegacyFacilityUpgradeCosts(
                rows, events, LoadedCareerTable(multiplier: 1f),
                LedgerLoadMigration.FundsSeedCaptureMoment.CareerStart, CareerStartUt);

            Assert.True(result.Complete);
            Assert.Equal(6, result.Candidates);
            Assert.Equal(2, result.Estimated);
            Assert.Equal(2, result.EvidenceZero);
            Assert.Equal(1, result.UnknownFacility);
            Assert.Equal(1, result.BeforeSeed);
            Assert.Equal(150000f, unproven.FacilityCost);
            Assert.Equal(500000f, unprovenTier3.FacilityCost);
            Assert.Equal(0f, explicitZero.FacilityCost);
            Assert.Equal(0f, credit.FacilityCost);
            Assert.Equal(0f, unknownFacility.FacilityCost);
            Assert.Equal(0f, beforeSeed.FacilityCost);
            Assert.Equal(75000f, costed.FacilityCost);
            string formatted = result.Format();
            Assert.Contains("candidates=6 estimated=2", formatted);
            Assert.Contains(unproven.ActionId + " " + Admin + "->Lv2 estimated=150000", formatted);
            Assert.Contains(unprovenTier3.ActionId + " " + Tracking + "->Lv3 estimated=500000", formatted);
        }

        [Fact]
        public void EstimatePass_DefersWithoutTouchingARow_UntilEverythingItNeedsIsReadable()
        {
            var row = UpgradeRowTo(UpgradeUt, 2);
            var rows = new List<GameAction> { row };
            var events = new List<GameStateEvent>();
            var career = LedgerLoadMigration.FundsSeedCaptureMoment.CareerStart;

            var noGame = LoadedCareerTable();
            noGame.GameAvailable = false;
            var noTable = LoadedCareerTable();
            noTable.FacilityDataLoaded = false;
            var noMultiplier = LoadedCareerTable();
            noMultiplier.MultiplierReadable = false;

            var cases = new[]
            {
                (probe: noGame, seed: career, reason: LedgerLoadMigration.LegacyEstimateDeferNoGame),
                (probe: LoadedCareerTable(), seed: LedgerLoadMigration.FundsSeedCaptureMoment.NoSeed,
                    reason: LedgerLoadMigration.LegacyEstimateDeferNoFundsSeed),
                (probe: noMultiplier, seed: career, reason: LedgerLoadMigration.LegacyEstimateDeferNoMultiplier),
                (probe: noTable, seed: career, reason: LedgerLoadMigration.LegacyEstimateDeferNoFacilityData),
            };
            foreach (var c in cases)
            {
                var result = LedgerLoadMigration.EstimateLegacyFacilityUpgradeCosts(
                    rows, events, c.probe, c.seed, CareerStartUt);
                Assert.False(result.Complete, c.reason);
                Assert.Equal(c.reason, result.DeferReason);
                Assert.Equal(0f, row.FacilityCost);
            }
        }

        [Fact]
        public void EstimatePass_SeedFromTheLivePool_SkipsEveryRow_SoNothingIsChargedTwice()
        {
            var row = UpgradeRowTo(UpgradeUt, 2);

            var result = LedgerLoadMigration.EstimateLegacyFacilityUpgradeCosts(
                new List<GameAction> { row }, new List<GameStateEvent>(), LoadedCareerTable(),
                LedgerLoadMigration.FundsSeedCaptureMoment.Unknown, double.NaN);

            Assert.True(result.Complete);
            Assert.Equal(1, result.SeedCaptureUnknown);
            Assert.Equal(0, result.Estimated);
            Assert.Equal(0f, row.FacilityCost);
            Assert.Contains("seedCaptureUnknown=1", result.Format());
        }

        [Fact]
        public void EstimatePass_ScienceOrSandbox_LeavesEveryRowAtZero()
        {
            var row = UpgradeRowTo(UpgradeUt, 2);
            var probe = LoadedCareerTable();
            probe.IsCareer = false;

            var result = LedgerLoadMigration.EstimateLegacyFacilityUpgradeCosts(
                new List<GameAction> { row }, new List<GameStateEvent>(), probe,
                LedgerLoadMigration.FundsSeedCaptureMoment.CareerStart, CareerStartUt);

            Assert.True(result.Complete);
            Assert.Equal(1, result.NotCareer);
            Assert.Equal(0f, row.FacilityCost);
        }

        [Fact]
        public void EstimatePass_IsIdempotent_AnEstimatedRowIsNoLongerACandidate()
        {
            var row = UpgradeRowTo(UpgradeUt, 2);
            var rows = new List<GameAction> { row };

            var first = LedgerLoadMigration.EstimateLegacyFacilityUpgradeCosts(
                rows, new List<GameStateEvent>(), LoadedCareerTable(),
                LedgerLoadMigration.FundsSeedCaptureMoment.CareerStart, CareerStartUt);
            var second = LedgerLoadMigration.EstimateLegacyFacilityUpgradeCosts(
                rows, new List<GameStateEvent>(), LoadedCareerTable(multiplier: 2f),
                LedgerLoadMigration.FundsSeedCaptureMoment.CareerStart, CareerStartUt);

            Assert.Equal(1, first.Estimated);
            Assert.True(second.Complete);
            Assert.Equal(0, second.Candidates);
            Assert.Equal(150000f, row.FacilityCost);
        }

        [Fact]
        public void Recalc_CareerStartSeed_EstimatesOnceTheTableIsReadable_ThenChargesTheRow()
        {
            // A career started with Parsek: the funds seed is the career-start baseline, so it
            // predates every upgrade row and cannot already hold the debit.
            GameStateStore.AddBaseline(new GameStateBaseline { ut = CareerStartUt, funds = StartingFunds });
            var row = UpgradeRowTo(UpgradeUt, 2);
            Ledger.AddAction(row);
            var probe = LoadedCareerTable();
            probe.FacilityDataLoaded = false;
            LedgerOrchestrator.FacilityCostTableProbeForTesting = () => probe;

            // The Tracking Station / a cold load: no facility objects, so the estimate waits.
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(0f, row.FacilityCost);
            Assert.Equal(StartingFunds, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE]")
                && l.Contains("Legacy facility-upgrade cost estimate: deferred")
                && l.Contains(LedgerLoadMigration.LegacyEstimateDeferNoFacilityData));

            // The Space Center: the table is readable, the estimate runs inside this recalc.
            probe.FacilityDataLoaded = true;
            logLines.Clear();
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(150000f, row.FacilityCost);
            Assert.Equal(StartingFunds - 150000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            Assert.Single(logLines, l => l.Contains("[INFO]")
                && l.Contains("Legacy facility-upgrade cost estimate:")
                && l.Contains("candidates=1 estimated=1")
                && l.Contains(row.ActionId + " " + Admin + "->Lv2 estimated=150000"));

            // Once per load: later recalcs neither rescan nor log.
            logLines.Clear();
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.DoesNotContain(logLines, l => l.Contains("Legacy facility-upgrade cost estimate"));
            Assert.Equal(StartingFunds - 150000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void Recalc_SeedFromTheLivePool_KeepsTheRowAtZero_AndSaysWhy()
        {
            // A mid-career install: the seed is whatever the pool held when Parsek first read it,
            // which can already include the upgrade's debit.
            Ledger.SeedInitialFunds(350000.0);
            var row = UpgradeRowTo(UpgradeUt, 2);
            Ledger.AddAction(row);
            LedgerOrchestrator.FacilityCostTableProbeForTesting = () => LoadedCareerTable();

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(0f, row.FacilityCost);
            Assert.Equal(350000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
            Assert.Single(logLines, l => l.Contains("[INFO]")
                && l.Contains("Legacy facility-upgrade cost estimate:")
                && l.Contains("seedCaptureUnknown=1") && l.Contains("estimated=0"));
        }

        // ================================================================
        // Harmony target: a stock rename must red here, not at runtime
        // ================================================================

        [Fact]
        public void SpendPatch_TargetsUpgradeFacilityBool_WithABlockingPrefixAndAFinalizer()
        {
            Assert.NotEmpty(typeof(FacilityUpgradeSpendPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo target = typeof(SpaceCenterBuilding).GetMethod(
                "UpgradeFacility",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(bool) }, modifiers: null);
            Assert.NotNull(target);
            // Harmony binds the prefix's deduceFunds argument by this name.
            Assert.Equal("deduceFunds", target.GetParameters()[0].Name);

            var prefix = typeof(FacilityUpgradeSpendPatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.Equal(typeof(bool), prefix.ReturnType);
            Assert.Equal(new[] { "__instance", "deduceFunds" }, prefix.GetParameters().Select(p => p.Name).ToArray());
            var finalizer = typeof(FacilityUpgradeSpendPatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(finalizer);
            Assert.Equal(typeof(Exception), finalizer.ReturnType);

            // The capture reads the debit off this event and this reason.
            Assert.NotNull(typeof(GameEvents).GetField("OnFundsChanged"));
            Assert.True(Enum.IsDefined(typeof(TransactionReasons), TransactionReasons.StructureConstruction));
        }
    }
}
