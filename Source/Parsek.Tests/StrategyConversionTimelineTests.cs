using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// STRATEGY-CONVERSION-ROWS-OUTLIVE-THEIR-AWARD: the share of a flight's reward a stock
    /// strategy converts (Appreciation Campaign, Leadership Initiative, Patents Licensing and
    /// the other query-family converters) is written live by
    /// <see cref="LedgerOrchestrator.OnStrategyCurrencyConversion"/>, while the reward itself
    /// is a game-state event tagged to the flight and booked at commit. The converted rows
    /// must follow that award through every timeline operation: an F9 into a quicksave taken
    /// before the award, a Re-Fly merge that supersedes the flight, a stock revert, and a
    /// non-rewind discard (which keeps the award, so keeps the converted share too).
    /// </summary>
    [Collection("Sequential")]
    public class StrategyConversionTimelineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public StrategyConversionTimelineTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ResetAll();
            KspStatePatcher.ResetForTesting();
        }

        private static void ResetAll()
        {
            GameStateRecorder.TagResolverForTesting = null;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RecoveryDispatchScope.ResetForTesting();
            FlightRecorder.QuickloadResumeUTProviderForTesting = null;
        }

        // ============================================================
        // Fixtures
        // ============================================================

        /// <summary>
        /// The legs Appreciation Campaign produces on a completed contract: part of the
        /// funds reward diverted out of the pool, paid into reputation.
        /// </summary>
        private static List<StrategyConversionLeg> AppreciationCampaignLegs()
        {
            return new List<StrategyConversionLeg>
            {
                new StrategyConversionLeg { Currency = StrategyConversionCurrency.Funds, Delta = -5000.0 },
                new StrategyConversionLeg { Currency = StrategyConversionCurrency.Reputation, Delta = 2.5 },
            };
        }

        /// <summary>Patents Licensing on a transmission: science converted into funds.</summary>
        private static List<StrategyConversionLeg> PatentsLicensingLegs()
        {
            return new List<StrategyConversionLeg>
            {
                new StrategyConversionLeg { Currency = StrategyConversionCurrency.Science, Delta = -4.0 },
                new StrategyConversionLeg { Currency = StrategyConversionCurrency.Funds, Delta = 1200.0 },
            };
        }

        private static void ConvertWhileRecording(string recordingId, double ut,
            List<StrategyConversionLeg> legs, string reason)
        {
            GameStateRecorder.TagResolverForTesting = () => recordingId ?? "";
            try
            {
                LedgerOrchestrator.OnStrategyCurrencyConversion(ut, legs, reason);
            }
            finally
            {
                GameStateRecorder.TagResolverForTesting = null;
            }
        }

        private static List<GameAction> ConversionRows()
        {
            return Ledger.Actions.Where(LedgerOrchestrator.IsStrategyConversionRow).ToList();
        }

        private static RecordingTree MakeTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording { RecordingId = "root_" + id, TreeId = id, VesselName = id, VesselPersistentId = 111u };
            SetPoints(root, 100.0, 150.0, 200.0);
            tree.AddOrReplaceRecording(root);
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            foreach (double ut in uts)
                rec.Points.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }

        private static void AddEvent(string recordingId, double ut, GameStateEventType type, string key, string detail)
        {
            var e = new GameStateEvent
            {
                ut = ut, eventType = type, key = key, detail = detail, recordingId = recordingId,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static void RunResumePrep(RecordingTree tree, double resumeUT, LoadKind kind)
        {
            var recorder = new FlightRecorder { ActiveTree = tree };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => resumeUT;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree, kind);
            MethodInfo method = typeof(FlightRecorder).GetMethod(
                "PrepareQuickloadResumeStateIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(recorder, null);
        }

        // ============================================================
        // The capture door
        // ============================================================

        [Fact]
        public void Conversion_WhileARecordingIsLive_IsTaggedToThatRecording()
        {
            ConvertWhileRecording("rec_live", 300.0, AppreciationCampaignLegs(), "ContractReward");

            var rows = ConversionRows();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("rec_live", r.RecordingId));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("OnStrategyCurrencyConversion:")
                && l.Contains("recordingId=rec_live"));
            Assert.Equal(2, logLines.Count(l =>
                l.Contains("Strategy conversion recorded:") && l.Contains("recordingId=rec_live")));
        }

        [Fact]
        public void Conversion_AtTheSpaceCenter_StaysUntagged()
        {
            ConvertWhileRecording(null, 300.0, AppreciationCampaignLegs(), "ContractReward");

            var rows = ConversionRows();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.True(string.IsNullOrEmpty(r.RecordingId)));
        }

        // ============================================================
        // F9 into a quicksave taken before the award
        // ============================================================

        [Fact]
        public void F9_BeforeTheAward_RetiresTheConvertedShareWithTheAward()
        {
            // F5 at 200; the flight completes a contract at 300 under Appreciation Campaign;
            // F9. The completion is the abandoned future, so is the share the strategy took.
            var tree = MakeTree("strat_f9");
            string rootId = tree.RootRecordingId;
            AddEvent(rootId, 300.0, GameStateEventType.ContractCompleted, "guid-abandoned", "fundsReward=50000");
            ConvertWhileRecording(rootId, 300.0, AppreciationCampaignLegs(), "ContractReward");
            Assert.Equal(2, ConversionRows().Count);

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "guid-abandoned");
            Assert.Empty(ConversionRows());
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains("strategyConversionRetired=2 "));
        }

        [Fact]
        public void F9_AfterTheAward_KeepsTheConvertedShare()
        {
            var tree = MakeTree("strat_f9_kept");
            string rootId = tree.RootRecordingId;
            AddEvent(rootId, 150.0, GameStateEventType.ContractCompleted, "guid-kept", "fundsReward=50000");
            ConvertWhileRecording(rootId, 150.0, AppreciationCampaignLegs(), "ContractReward");

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            Assert.Contains(GameStateStore.Events, e => e.key == "guid-kept");
            Assert.Equal(2, ConversionRows().Count);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && !l.Contains("strategyConversionRetired=0 "));
        }

        // ============================================================
        // Re-Fly merge
        // ============================================================

        [Fact]
        public void ReFlyMerge_TombstonesTheConvertedShareOfTheSupersededFlight()
        {
            var origin = new Recording { RecordingId = "rec_origin", VesselName = "o", TreeId = "tree_1", MergeState = MergeState.Immutable };
            var tree = new RecordingTree { Id = "tree_1", TreeName = "Test_tree_1" };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            RecordingStore.CommittedTrees.Add(tree);

            var provisional = new Recording
            {
                RecordingId = "rec_provisional", VesselName = "p", TreeId = "tree_1",
                MergeState = MergeState.NotCommitted, TerminalStateValue = TerminalState.Landed,
                SupersedeTargetId = "rec_origin",
            };
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_1", TreeId = "tree_1", ActiveReFlyRecordingId = "rec_provisional",
                    OriginChildRecordingId = "rec_origin", RewindPointId = "rp_1", InvokedUT = 0.0,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();

            var complete = new GameAction
            {
                Type = GameActionType.ContractComplete, RecordingId = "rec_origin", ContractId = "c_1",
                FundsReward = 50000f, RepReward = 5f, UT = 100.0,
            };
            Ledger.AddAction(complete);
            ConvertWhileRecording("rec_origin", 100.0, AppreciationCampaignLegs(), "ContractReward");
            ConvertWhileRecording("rec_origin", 110.0, PatentsLicensingLegs(), "ScienceTransmission");
            var converted = ConversionRows();
            Assert.Equal(4, converted.Count);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == complete.ActionId);
            foreach (var row in converted)
                Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == row.ActionId);
        }

        // ============================================================
        // Stock revert
        // ============================================================

        [Fact]
        public void Revert_PrunesTheRevertedFlightsConversionRows_KeepsCommittedAndKscRows()
        {
            var committed = new Recording { RecordingId = "rec_committed", VesselName = "c" };
            RecordingStore.AddCommittedInternal(committed);

            ConvertWhileRecording("rec_reverted", 300.0, AppreciationCampaignLegs(), "ContractReward");
            ConvertWhileRecording("rec_reverted", 90.0, PatentsLicensingLegs(), "ScienceTransmission");
            ConvertWhileRecording("rec_committed", 300.0, PatentsLicensingLegs(), "Progression");
            ConvertWhileRecording(null, 300.0, AppreciationCampaignLegs(), "ContractReward");
            Assert.Equal(8, ConversionRows().Count);

            logLines.Clear();
            int removed = LedgerOrchestrator.PruneRevertedFlightStrategyConversionRows(100.0, inclusive: false);

            Assert.Equal(2, removed);
            var left = ConversionRows();
            Assert.Equal(6, left.Count);
            Assert.DoesNotContain(left, r => r.RecordingId == "rec_reverted" && r.UT > 100.0);
            Assert.Equal(2, left.Count(r => r.RecordingId == "rec_reverted" && r.UT < 100.0));
            Assert.Equal(2, left.Count(r => r.RecordingId == "rec_committed"));
            Assert.Equal(2, left.Count(r => string.IsNullOrEmpty(r.RecordingId)));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("Revert: pruned 2 strategy conversion row(s)"));
        }

        [Theory]
        [InlineData(100.0, false, false)]
        [InlineData(100.0, true, true)]
        [InlineData(100.5, false, true)]
        public void IsRevertedFlightStrategyConversionRow_Boundary(double ut, bool inclusive, bool expected)
        {
            var row = new GameAction
            {
                UT = ut, Type = GameActionType.FundsSpending, RecordingId = "rec_x",
                FundsSpent = 10f, FundsSpendingSource = FundsSpendingSource.StrategyConverter,
            };
            Assert.Equal(expected,
                LedgerOrchestrator.IsRevertedFlightStrategyConversionRow(row, 100.0, inclusive, id => false));
            Assert.False(
                LedgerOrchestrator.IsRevertedFlightStrategyConversionRow(row, 0.0, inclusive, id => true));
            row.FundsSpendingSource = FundsSpendingSource.VesselBuild;
            Assert.False(
                LedgerOrchestrator.IsRevertedFlightStrategyConversionRow(row, 0.0, inclusive, id => false));
        }

        // ============================================================
        // Non-rewind discard: the award is kept, so is the converted share
        // ============================================================

        [Fact]
        public void NonRewindDiscard_ReHomesTheConvertedShareUntagged()
        {
            AddEvent("rec_discarded", 300.0, GameStateEventType.ContractCompleted, "guid-kept",
                "title=T;fundsReward=50000;repReward=5;sciReward=0");
            ConvertWhileRecording("rec_discarded", 300.0, AppreciationCampaignLegs(), "ContractReward");
            ConvertWhileRecording("rec_other", 300.0, PatentsLicensingLegs(), "ScienceTransmission");

            logLines.Clear();
            LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                new HashSet<string> { "rec_discarded" }, "test-discard");

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ContractComplete && a.ContractId == "guid-kept"
                && string.IsNullOrEmpty(a.RecordingId));
            var rows = ConversionRows();
            Assert.Equal(4, rows.Count);
            Assert.Equal(2, rows.Count(r => string.IsNullOrEmpty(r.RecordingId)));
            Assert.Equal(2, rows.Count(r => r.RecordingId == "rec_other"));
            Assert.Contains(logLines, l => l.Contains("strategyConversionRowsUntagged=2"));
        }

        // ============================================================
        // Commit dedup: a tagged strategy funds yield must not swallow the same
        // recording's own funds earning at the same UT
        // ============================================================

        [Fact]
        public void StrategyFundsYield_KeyDiffersFromTheSameRecordingsFundsEarning()
        {
            var yield = LedgerOrchestrator.BuildStrategyConversionAction(
                300.0,
                new StrategyConversionLeg { Currency = StrategyConversionCurrency.Funds, Delta = 1200.0 },
                "VesselRecovery",
                "rec_a");
            var payout = new GameAction
            {
                UT = 300.0, Type = GameActionType.FundsEarning, RecordingId = "rec_a",
                FundsAwarded = 8000f, FundsSource = FundsEarningSource.Recovery,
            };
            Assert.Equal("rec_a", yield.RecordingId);
            Assert.NotEqual(LedgerOrchestrator.GetActionKey(yield), LedgerOrchestrator.GetActionKey(payout));

            Ledger.AddAction(yield);
            var kept = LedgerOrchestrator.DeduplicateAgainstLedger(new List<GameAction> { payout });
            Assert.Single(kept);
        }

        // ============================================================
        // Recovery scope: a progress reward a recovery completes at the Space Center
        // ============================================================

        [Fact]
        public void ResolveTag_NoLiveRecording_NoRecoveryScope_IsNull()
        {
            GameStateRecorder.TagResolverForTesting = () => "";
            Assert.Null(GameStateRecorder.ResolveStrategyConversionRecordingTag("Progression", 300.0));
        }

        [Fact]
        public void ResolveTag_LiveRecordingWins()
        {
            GameStateRecorder.TagResolverForTesting = () => "rec_live";
            Assert.Equal("rec_live",
                GameStateRecorder.ResolveStrategyConversionRecordingTag("ContractReward", 300.0));
        }
    }
}
