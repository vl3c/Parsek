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
    /// KSC building destructions and repairs reach the ledger at the moment they happen
    /// (todo KSC-BUILDING-DESTROY-REPAIR-NEVER-REACH-LEDGER). The stock handlers take a
    /// DestructibleBuilding (a MonoBehaviour that cannot be built headless), so these cells
    /// drive the shared core, <c>GameStateFacilityRecorder.RecordBuildingTransition</c>,
    /// through the recorder's test seam, plus the pure decisions and the repair scope.
    /// </summary>
    [Collection("Sequential")]
    public class KscBuildingLedgerTests : IDisposable
    {
        private const string PadBuilding = "SpaceCenter/LaunchPad/Facility/mainBuilding";
        private const string PadTank = "SpaceCenter/LaunchPad/Facility/ksp_pad_cylTank";

        private readonly List<string> logLines = new List<string>();

        public KscBuildingLedgerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateRecorder.ResetForTesting();
            FacilityRepairCapture.ResetForTesting();
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
            FacilityRepairCapture.ResetForTesting();
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

        // ================================================================
        // Pure decisions
        // ================================================================

        [Fact]
        public void BuildRepairCostShares_SplitsStockTotalOverDestroyedBuildingsOnly()
        {
            var shares = FacilityRepairCapture.BuildRepairCostShares(new[]
            {
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadBuilding, RepairCost = 8000f, IsDestroyed = true },
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadTank, RepairCost = 2000f, IsDestroyed = true },
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = "SpaceCenter/LaunchPad/Facility/intact", RepairCost = 5000f, IsDestroyed = false },
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = "", RepairCost = 100f, IsDestroyed = true },
            }, fundsLossMultiplier: 1.5f, fundsCharged: true);

            Assert.Equal(2, shares.Count);
            Assert.Equal(12000f, shares[PadBuilding]);
            Assert.Equal(3000f, shares[PadTank]);
            // Stock's GetRepairsCost: (8000 + 2000) * 1.5.
            Assert.Equal(15000f, shares.Values.Sum());
        }

        [Fact]
        public void BuildRepairCostShares_NoFundsDeducted_IsEmpty()
        {
            // Science mode / deduceFunds=false: stock charges nothing, so every row costs 0.
            var shares = FacilityRepairCapture.BuildRepairCostShares(new[]
            {
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadBuilding, RepairCost = 8000f, IsDestroyed = true },
            }, fundsLossMultiplier: 1f, fundsCharged: false);

            Assert.Empty(shares);
            Assert.Equal(0f, FacilityRepairCapture.ResolveRepairCost(shares, PadBuilding));
        }

        [Fact]
        public void BuildRepairCostShares_NegativeInputs_NeverProduceARefund()
        {
            var shares = FacilityRepairCapture.BuildRepairCostShares(new[]
            {
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadBuilding, RepairCost = -50f, IsDestroyed = true },
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadTank, RepairCost = 100f, IsDestroyed = true },
            }, fundsLossMultiplier: -2f, fundsCharged: true);

            Assert.Equal(0f, shares[PadBuilding]);
            Assert.Equal(0f, shares[PadTank]);
        }

        [Fact]
        public void ResolveRepairCost_UnknownOrNull_IsZero()
        {
            var shares = new Dictionary<string, float> { { PadBuilding, 10f } };
            Assert.Equal(10f, FacilityRepairCapture.ResolveRepairCost(shares, PadBuilding));
            Assert.Equal(0f, FacilityRepairCapture.ResolveRepairCost(shares, PadTank));
            Assert.Equal(0f, FacilityRepairCapture.ResolveRepairCost(null, PadBuilding));
            Assert.Equal(0f, FacilityRepairCapture.ResolveRepairCost(shares, null));
        }

        [Fact]
        public void SelectResetRepairedBuildings_OnlyDestroyedOnesOnce()
        {
            var ids = FacilityRepairCapture.SelectResetRepairedBuildings(new[]
            {
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadBuilding, IsDestroyed = true },
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadBuilding, IsDestroyed = true },
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = PadTank, IsDestroyed = false },
                new FacilityRepairCapture.BuildingRepairInput { BuildingId = null, IsDestroyed = true },
            });

            Assert.Equal(new[] { PadBuilding }, ids);
            Assert.Empty(FacilityRepairCapture.SelectResetRepairedBuildings(null));
        }

        [Fact]
        public void BuildRepairDetail_IsCultureInvariant_AndRoundTripsThroughTheConverter()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string detail = FacilityRepairCapture.BuildRepairDetail(1234.5f);
                Assert.Equal("cost=1234.5", detail);

                var evt = GameStateFacilityRecorder.CreateBuildingEvent(PadBuilding, true, 77.0, 1234.5f);
                var action = GameStateEventConverter.ConvertEvent(evt, null);
                Assert.Equal(GameActionType.FacilityRepair, action.Type);
                Assert.Equal(1234.5f, action.FacilityCost);
                Assert.Equal(PadBuilding, action.FacilityId);
                Assert.Equal(77.0, action.UT);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void CreateBuildingEvent_DestructionCarriesNoCost()
        {
            var evt = GameStateFacilityRecorder.CreateBuildingEvent(PadBuilding, false, 50.0, 999f);
            Assert.Equal(GameStateEventType.BuildingDestroyed, evt.eventType);
            Assert.Equal(PadBuilding, evt.key);
            Assert.Null(evt.detail);

            var action = GameStateEventConverter.ConvertEvent(evt, null);
            Assert.Equal(GameActionType.FacilityDestruction, action.Type);
            Assert.Equal(50.0, action.UT);
        }

        [Theory]
        [InlineData(false, false, PadBuilding, true)]
        [InlineData(true, false, PadBuilding, false)]
        [InlineData(false, true, PadBuilding, false)]
        [InlineData(false, false, "", false)]
        [InlineData(false, false, null, false)]
        public void ShouldRecordBuildingTransition_SkipsParsekDrivenPatchesAndEmptyIds(
            bool replaying, bool suppressResources, string id, bool expected)
        {
            Assert.Equal(expected,
                GameStateFacilityRecorder.ShouldRecordBuildingTransition(replaying, suppressResources, id));
        }

        [Theory]
        [InlineData("SpaceCenter/LaunchPad/Facility/mainBuilding", true)]
        [InlineData("SpaceCenter/VehicleAssemblyBuilding/Facility/Tank", true)]
        [InlineData("SpaceCenter/LaunchPad", false)]
        [InlineData("LaunchPad", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsDestructibleBuildingId_SplitsBuildingsFromUpgradeableFacilities(string id, bool expected)
        {
            Assert.Equal(expected, FacilityStatePatcher.IsDestructibleBuildingId(id));
        }

        // ================================================================
        // The capture core: event -> ledger
        // ================================================================

        [Fact]
        public void KscCollapse_Untagged_WritesFacilityDestructionAtTheCollapseUt()
        {
            var recorder = new GameStateRecorder();
            Assert.True(recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1500.0, 0f, "test"));

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.BuildingDestroyed);
            Assert.Equal(1500.0, evt.ut);
            Assert.Equal("", evt.recordingId ?? "");

            var row = Ledger.Actions.Single(a => a.Type == GameActionType.FacilityDestruction);
            Assert.Equal(PadBuilding, row.FacilityId);
            Assert.Equal(1500.0, row.UT);
            Assert.Null(row.RecordingId);
            Assert.True(LedgerOrchestrator.Facilities.IsFacilityDestroyed(PadBuilding));
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("Game state: BuildingDestroyed") && l.Contains("source=test"));
        }

        [Fact]
        public void KscRepair_OutsideAScope_WritesFacilityRepairImmediately_AndClearsTheDestroyedState()
        {
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1000.0, 0f, "test");
            recorder.RecordBuildingTransitionForTesting(PadBuilding, true, 2000.0, 0f, "test");

            var repair = Ledger.Actions.Single(a => a.Type == GameActionType.FacilityRepair);
            Assert.Equal(2000.0, repair.UT);
            Assert.Equal(0f, repair.FacilityCost);
            Assert.False(LedgerOrchestrator.Facilities.IsFacilityDestroyed(PadBuilding));
        }

        [Fact]
        public void BuildingTransition_TaggedWithALiveRecording_WaitsForTheCommit()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec-live";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            var recorder = new GameStateRecorder();

            recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1500.0, 0f, "test");

            var evt = GameStateStore.Events.Single(e => e.eventType == GameStateEventType.BuildingDestroyed);
            Assert.Equal("rec-live", evt.recordingId);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityDestruction);

            // ... and the commit-time converter picks it up, tagged, at the collapse UT.
            var converted = GameStateEventConverter.ConvertEvents(
                GameStateStore.Events, "rec-live", 1000.0, 2000.0);
            var row = converted.Single(a => a.Type == GameActionType.FacilityDestruction);
            Assert.Equal("rec-live", row.RecordingId);
            Assert.Equal(1500.0, row.UT);
        }

        [Fact]
        public void BuildingTransition_LiveRecorderWithEmptyTag_IsNotForwarded()
        {
            // Same gate as facility upgrades: a live recorder with no tag is tag drift, not
            // proof the event is ownerless.
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            var recorder = new GameStateRecorder();

            recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1500.0, 0f, "test");

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityDestruction);
        }

        [Fact]
        public void BuildingTransition_DuringRecalcPatch_IsNotRecorded()
        {
            // FacilityStatePatcher.PatchDestructionState calls Demolish()/Repair() inside
            // SuppressionGuard.ResourcesAndReplay; the events it fires are Parsek's own.
            var recorder = new GameStateRecorder();
            using (SuppressionGuard.ResourcesAndReplay())
            {
                Assert.False(recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1500.0, 0f, "test"));
                Assert.False(recorder.RecordBuildingTransitionForTesting(PadBuilding, true, 1600.0, 0f, "test"));
            }

            Assert.DoesNotContain(GameStateStore.Events, e =>
                e.eventType == GameStateEventType.BuildingDestroyed
                || e.eventType == GameStateEventType.BuildingRepaired);
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.FacilityDestruction || a.Type == GameActionType.FacilityRepair);
        }

        // ================================================================
        // The RepairFacility scope: per-building rows, one funds debit
        // ================================================================

        [Fact]
        public void RepairScope_TwoBuildings_BatchReconcilesAgainstTheOneStructureRepairDebit()
        {
            Ledger.SeedInitialFunds(100000.0);
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1000.0, 0f, "test");
            recorder.RecordBuildingTransitionForTesting(PadTank, false, 1000.0, 0f, "test");

            // Stock order inside RepairFacility: the single funds debit first...
            AddStoreEvent(new GameStateEvent
            {
                ut = 2000.0,
                eventType = GameStateEventType.FundsChanged,
                key = "StructureRepair",
                valueBefore = 50000.0,
                valueAfter = 40000.0,
            });
            // ... then one Repairing event per destroyed building.
            FacilityRepairCapture.BeginScope("SpaceCenter/LaunchPad",
                new Dictionary<string, float> { { PadBuilding, 8000f }, { PadTank, 2000f } });
            recorder.RecordBuildingTransitionForTesting(PadBuilding, true, 2000.0,
                FacilityRepairCapture.CostForBuilding(PadBuilding), "test");
            recorder.RecordBuildingTransitionForTesting(PadTank, true, 2000.0,
                FacilityRepairCapture.CostForBuilding(PadTank), "test");

            // Queued, not yet written.
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);

            logLines.Clear();
            FacilityRepairCapture.EndScope("test");

            var repairs = Ledger.Actions.Where(a => a.Type == GameActionType.FacilityRepair).ToList();
            Assert.Equal(2, repairs.Count);
            Assert.Equal(8000f, repairs.Single(a => a.FacilityId == PadBuilding).FacilityCost);
            Assert.Equal(2000f, repairs.Single(a => a.FacilityId == PadTank).FacilityCost);
            Assert.Contains(logLines, l => l.Contains("KSC spending batch recorded")
                && l.Contains("rows=2"));
            // Every row reconciles the full facility sum against the one debit: no WARN.
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation") && l.Contains("[WARN]"));
            Assert.False(LedgerOrchestrator.Facilities.IsFacilityDestroyed(PadBuilding));
            Assert.False(LedgerOrchestrator.Facilities.IsFacilityDestroyed(PadTank));
            // The repair cost is in the funds walk exactly once.
            Assert.Equal(100000.0 - 10000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 3);
        }

        [Fact]
        public void RepairScope_OneRowAtATime_WouldWarn_WhichIsWhyTheBatchExists()
        {
            // Pins the reconciler behaviour the batch works around: a lone row of a two-row
            // repair sees half the expected delta against the whole debit.
            AddStoreEvent(new GameStateEvent
            {
                ut = 2000.0,
                eventType = GameStateEventType.FundsChanged,
                key = "StructureRepair",
                valueBefore = 50000.0,
                valueAfter = 40000.0,
            });
            logLines.Clear();
            LedgerOrchestrator.OnKscSpending(
                GameStateFacilityRecorder.CreateBuildingEvent(PadBuilding, true, 2000.0, 8000f));

            Assert.Contains(logLines, l => l.Contains("KSC reconciliation") && l.Contains("delta mismatch"));
        }

        [Fact]
        public void RepairScope_CostForBuilding_IsZeroOutsideAScope()
        {
            Assert.False(FacilityRepairCapture.IsScopeActive);
            Assert.Equal(0f, FacilityRepairCapture.CostForBuilding(PadBuilding));

            FacilityRepairCapture.BeginScope("f", new Dictionary<string, float> { { PadBuilding, 5f } });
            Assert.True(FacilityRepairCapture.IsScopeActive);
            Assert.Equal(5f, FacilityRepairCapture.CostForBuilding(PadBuilding));
            FacilityRepairCapture.EndScope("test");

            Assert.False(FacilityRepairCapture.IsScopeActive);
            Assert.Equal(0f, FacilityRepairCapture.CostForBuilding(PadBuilding));
        }

        [Fact]
        public void RepairScope_ReenteredWhileOpen_FlushesTheOpenOneFirst()
        {
            var recorder = new GameStateRecorder();
            FacilityRepairCapture.BeginScope("first", new Dictionary<string, float>());
            recorder.RecordBuildingTransitionForTesting(PadBuilding, true, 10.0, 0f, "test");

            FacilityRepairCapture.BeginScope("second", new Dictionary<string, float>());

            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("was still open"));
            FacilityRepairCapture.EndScope("test");
        }

        [Fact]
        public void StructuresReset_RaisesRepairRowsAtZeroCost()
        {
            // Upgrading a destroyed facility resets its buildings with no repair event; the
            // ResetStructures postfix reports them and the subscribed recorder records them.
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 100.0, 0f, "test");
            var received = new List<string>();
            FacilityRepairCapture.StructuresReset += ids => received.AddRange(ids);

            FacilityRepairCapture.ReportStructuresReset(new List<string> { PadBuilding });

            Assert.Equal(new[] { PadBuilding }, received);
        }

        // ================================================================
        // Timeline behaviour: rewind cutoff, load prune, tombstones
        // ================================================================

        [Fact]
        public void Recalc_CutoffBeforeTheRepair_KeepsTheBuildingDestroyed()
        {
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1000.0, 0f, "test");
            recorder.RecordBuildingTransitionForTesting(PadBuilding, true, 3000.0, 4000f, "test");

            // A rewind to UT 2000: the repair is a future action and must not apply.
            LedgerOrchestrator.RecalculateAndPatch(2000.0);
            Assert.True(LedgerOrchestrator.Facilities.IsFacilityDestroyed(PadBuilding));

            // Full timeline: repaired.
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.False(LedgerOrchestrator.Facilities.IsFacilityDestroyed(PadBuilding));
        }

        [Fact]
        public void Reconcile_LoadBeforeTheRepair_PrunesIt_UnlessTheFutureIsPreserved()
        {
            var recorder = new GameStateRecorder();
            recorder.RecordBuildingTransitionForTesting(PadBuilding, false, 1000.0, 0f, "test");
            recorder.RecordBuildingTransitionForTesting(PadBuilding, true, 3000.0, 4000f, "test");

            // A Parsek rewind keeps the future timeline: the repair stays as a future row.
            Ledger.Reconcile(new HashSet<string>(), 2000.0, preserveFutureTimelineActions: true);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);

            // A plain load to before the repair abandons it, like every other KSC spend.
            Ledger.Reconcile(new HashSet<string>(), 2000.0, preserveFutureTimelineActions: false);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FacilityRepair);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FacilityDestruction);
        }

        [Fact]
        public void KscRows_AreNotTombstoneEligible_ButAFlightTaggedCollapseIs()
        {
            var kscRepair = new GameAction { Type = GameActionType.FacilityRepair, RecordingId = null, FacilityId = PadBuilding };
            var flightCollapse = new GameAction { Type = GameActionType.FacilityDestruction, RecordingId = "rec-A", FacilityId = PadBuilding };

            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(kscRepair));
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(flightCollapse));
        }

        [Fact]
        public void FacilityRepair_SerializationRoundTrip_KeepsIdCostAndUt()
        {
            var action = new GameAction
            {
                Type = GameActionType.FacilityRepair,
                UT = 1234.5,
                FacilityId = PadBuilding,
                FacilityCost = 9876.25f,
                Sequence = 3,
            };
            var parent = new ConfigNode("LEDGER");
            action.SerializeInto(parent);
            var back = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));

            Assert.Equal(GameActionType.FacilityRepair, back.Type);
            Assert.Equal(1234.5, back.UT);
            Assert.Equal(PadBuilding, back.FacilityId);
            Assert.Equal(9876.25f, back.FacilityCost);
        }

        [Theory]
        // (targetDestroyed, isIntact, isDestroyed) -> action
        [InlineData(true, true, false, "Demolish")]
        [InlineData(false, false, true, "Repair")]
        [InlineData(true, false, true, "None")]
        [InlineData(false, true, false, "None")]
        // Mid-collapse / mid-repair: stock's animation owns it, never re-patched.
        [InlineData(true, false, false, "Settling")]
        [InlineData(false, false, false, "Settling")]
        public void ResolveDestructionPatch_LeavesAnimatingBuildingsAlone(
            bool targetDestroyed, bool isIntact, bool isDestroyed, string expected)
        {
            Assert.Equal(expected, FacilityStatePatcher.ResolveDestructionPatch(
                targetDestroyed, isIntact, isDestroyed).ToString());
        }

        // ================================================================
        // Harmony targets: a stock rename must red here, not at runtime
        // ================================================================

        [Fact]
        public void RepairScopePatch_TargetsSpaceCenterBuildingRepairFacilityBool()
        {
            Assert.NotEmpty(typeof(FacilityRepairScopePatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo target = typeof(SpaceCenterBuilding).GetMethod(
                "RepairFacility",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(bool) }, modifiers: null);
            Assert.NotNull(target);
            Assert.Equal("deduceFunds", target.GetParameters()[0].Name);
            // The prefix reads these to split stock's GetRepairsCost per building.
            Assert.NotNull(typeof(SpaceCenterBuilding).GetField("destructibles"));
            Assert.NotNull(typeof(DestructibleBuilding).GetField("RepairCost"));
        }

        [Fact]
        public void ResetStructuresPatch_TargetsThePrivateParameterlessMethod()
        {
            Assert.NotEmpty(typeof(FacilityResetStructuresPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo target = typeof(SpaceCenterBuilding).GetMethod(
                "ResetStructures",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            Assert.NotNull(target);
        }

        [Fact]
        public void TheCapturedStockEventsExist()
        {
            Assert.NotNull(typeof(GameEvents).GetField("OnKSCStructureCollapsing"));
            Assert.NotNull(typeof(GameEvents).GetField("OnKSCStructureRepairing"));
        }

        // ================================================================
        // Patcher: building ids are not level lookups
        // ================================================================

        [Fact]
        public void PatchFacilities_BuildingEntry_IsNotCountedAsALevelMiss()
        {
            var module = new FacilitiesModule();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.FacilityDestruction,
                FacilityId = PadBuilding,
            });

            logLines.Clear();
            KspStatePatcher.PatchFacilities(module);

            Assert.DoesNotContain(logLines, l => l.Contains("notFound=1"));
            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]")
                && l.Contains("destructible-building"));
        }
    }
}
