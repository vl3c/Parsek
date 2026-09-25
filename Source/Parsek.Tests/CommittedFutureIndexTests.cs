using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The UT-keyed committed-future index (CommittedFutureIndex.cs): the pure builder's
    /// ledger-type mapping and committed filter, the strict-future boundary, the queries,
    /// the kerbal-retire milestone fallback, and the cache's invalidation keys.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedFutureIndexTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedFutureIndexTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------- builder: mapping ----------------

        private static CommittedFutureIndex BuildKscOnly(params GameAction[] actions)
        {
            return CommittedFutureIndex.Build(actions, null, null, null);
        }

        [Fact]
        public void Build_MapsEveryLedgerTypeToItsKindAndKey()
        {
            var index = BuildKscOnly(
                new GameAction { UT = 10, Type = GameActionType.ScienceSpending, NodeId = "basicRocketry", Cost = 5f },
                new GameAction { UT = 11, Type = GameActionType.FacilityUpgrade, FacilityId = "SpaceCenter/LaunchPad", ToLevel = 2, FacilityCost = 75000f },
                new GameAction { UT = 12, Type = GameActionType.ContractAccept, ContractId = "c1", ContractTitle = "Orbit Kerbin" },
                new GameAction { UT = 13, Type = GameActionType.ContractComplete, ContractId = "c1" },
                new GameAction { UT = 14, Type = GameActionType.ContractFail, ContractId = "c2" },
                new GameAction { UT = 15, Type = GameActionType.ContractCancel, ContractId = "c3" },
                new GameAction { UT = 16, Type = GameActionType.KerbalHire, KerbalName = "Val Kerman", HireCost = 1000f },
                new GameAction { UT = 17, Type = GameActionType.StrategyActivate, StrategyId = "UnpaidResearch", SetupCost = 50f },
                new GameAction { UT = 18, Type = GameActionType.StrategyDeactivate, StrategyId = "UnpaidResearch" },
                new GameAction { UT = 19, Type = GameActionType.FundsSpending, FundsSpendingSource = FundsSpendingSource.Other, DedupKey = "mk1pod.v2", FundsSpent = 1600f });

            Assert.Equal(5f, index.FirstFuture(CommittedFutureKind.TechResearch, "basicRocketry", 0).Amount);
            var facility = index.FirstFuture(CommittedFutureKind.FacilityUpgrade, "SpaceCenter/LaunchPad", 0);
            Assert.Equal(2, facility.FacilityToLevel);
            Assert.Equal(75000f, facility.Amount);
            Assert.Equal("Orbit Kerbin", index.FirstFuture(CommittedFutureKind.ContractAccept, "c1", 0).Title);
            Assert.True(index.HasFuture(CommittedFutureKind.ContractComplete, "c1", 0));
            Assert.True(index.HasFuture(CommittedFutureKind.ContractFail, "c2", 0));
            Assert.True(index.HasFuture(CommittedFutureKind.ContractCancel, "c3", 0));
            Assert.Equal(1000f, index.FirstFuture(CommittedFutureKind.KerbalHire, "Val Kerman", 0).Amount);
            Assert.True(index.HasFuture(CommittedFutureKind.StrategyActivate, "UnpaidResearch", 0));
            Assert.True(index.HasFuture(CommittedFutureKind.StrategyDeactivate, "UnpaidResearch", 0));
            Assert.Equal(1600f, index.FirstFuture(CommittedFutureKind.PartPurchase, "mk1pod.v2", 0).Amount);
            Assert.Equal(10, index.EntryCount);
        }

        [Fact]
        public void Build_SkipsRowsNoStockKindKeysOn()
        {
            var index = BuildKscOnly(
                // A science spend with no node (a strategy setup), a vessel build, a
                // milestone: none of them is a stock-screen item.
                new GameAction { UT = 10, Type = GameActionType.ScienceSpending, NodeId = null, Cost = 5f },
                new GameAction { UT = 11, Type = GameActionType.FundsSpending, FundsSpendingSource = FundsSpendingSource.VesselBuild, DedupKey = "x" },
                new GameAction { UT = 12, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstLaunch" },
                new GameAction { UT = 13, Type = GameActionType.FundsSpending, FundsSpendingSource = FundsSpendingSource.Other, DedupKey = "" });

            Assert.Equal(0, index.EntryCount);
        }

        // ---------------- builder: committed filter ----------------

        [Fact]
        public void Build_KscOriginRowsAreCommitted_TaggedRowsNeedACommittedRecording()
        {
            var committed = new HashSet<string> { "rec-committed" };
            var index = CommittedFutureIndex.Build(
                new[]
                {
                    new GameAction { UT = 10, Type = GameActionType.KerbalHire, KerbalName = "Ksc Kerman" },
                    new GameAction { UT = 10, Type = GameActionType.KerbalHire, KerbalName = "Flight Kerman", RecordingId = "rec-committed" },
                    new GameAction { UT = 10, Type = GameActionType.KerbalHire, KerbalName = "Pending Kerman", RecordingId = "rec-pending" }
                },
                id => committed.Contains(id),
                id => id == "rec-committed" ? "Mun Lander 3" : null,
                null);

            Assert.True(index.HasFuture(CommittedFutureKind.KerbalHire, "Ksc Kerman", 0));
            Assert.Null(index.FirstFuture(CommittedFutureKind.KerbalHire, "Ksc Kerman", 0).RecordingName);
            Assert.Equal("Mun Lander 3", index.FirstFuture(CommittedFutureKind.KerbalHire, "Flight Kerman", 0).RecordingName);
            Assert.False(index.HasFuture(CommittedFutureKind.KerbalHire, "Pending Kerman", 0));
            Assert.Equal(1, index.SkippedUncommittedRows);
        }

        // ---------------- boundary + queries ----------------

        [Fact]
        public void IsFuture_IsStrict_ARowAtTheCurrentUTHasAlreadyHappened()
        {
            Assert.True(CommittedFutureIndex.IsFuture(100.0, 99.999));
            Assert.False(CommittedFutureIndex.IsFuture(100.0, 100.0));
            Assert.False(CommittedFutureIndex.IsFuture(100.0, 100.001));
        }

        [Fact]
        public void Queries_SortByUT_FirstAndLastFutureTrackTheClock()
        {
            var index = BuildKscOnly(
                new GameAction { UT = 300, Type = GameActionType.FacilityUpgrade, FacilityId = "F", ToLevel = 3 },
                new GameAction { UT = 100, Type = GameActionType.FacilityUpgrade, FacilityId = "F", ToLevel = 2 });

            Assert.Equal(new[] { 100.0, 300.0 }, index.AllEntries(CommittedFutureKind.FacilityUpgrade, "F").Select(e => e.UT));
            Assert.Equal(100.0, index.FirstFuture(CommittedFutureKind.FacilityUpgrade, "F", 50).UT);
            Assert.Equal(300.0, index.LastFuture(CommittedFutureKind.FacilityUpgrade, "F", 50).UT);
            Assert.Equal(300.0, index.FirstFuture(CommittedFutureKind.FacilityUpgrade, "F", 100).UT);
            Assert.Single(index.FutureEntries(CommittedFutureKind.FacilityUpgrade, "F", 150));
            Assert.True(index.HasFuture(CommittedFutureKind.FacilityUpgrade, "F", 299));
            Assert.False(index.HasFuture(CommittedFutureKind.FacilityUpgrade, "F", 300));
            Assert.Null(index.LastFuture(CommittedFutureKind.FacilityUpgrade, "F", 300));
            Assert.Null(index.FirstFuture(CommittedFutureKind.FacilityUpgrade, "missing", 0));
        }

        [Fact]
        public void FutureKeys_ListsOnlyKeysWithARowAhead()
        {
            var index = BuildKscOnly(
                new GameAction { UT = 50, Type = GameActionType.ScienceSpending, NodeId = "past" },
                new GameAction { UT = 150, Type = GameActionType.ScienceSpending, NodeId = "ahead" });

            Assert.Equal(new[] { "ahead" }, index.FutureKeys(CommittedFutureKind.TechResearch, 100).ToArray());
            Assert.Empty(index.FutureKeys(CommittedFutureKind.ContractAccept, 100));
        }

        [Fact]
        public void ResolveHoldAssignment_OpenEndedPrefersTheLatestAboardFlight()
        {
            var index = CommittedFutureIndex.Build(
                new[]
                {
                    new GameAction { UT = 10, Type = GameActionType.KerbalAssignment, KerbalName = "Jeb", RecordingId = "r-aboard", KerbalEndStateField = KerbalEndState.Aboard, EndUT = 100f },
                    new GameAction { UT = 20, Type = GameActionType.KerbalAssignment, KerbalName = "Jeb", RecordingId = "r-recovered", KerbalEndStateField = KerbalEndState.Recovered, EndUT = 200f }
                },
                id => true,
                id => id == "r-aboard" ? "Station Hop" : "Mun Lander 3",
                null);

            Assert.Equal("Station Hop", index.ResolveHoldAssignment("Jeb", openEnded: true).RecordingName);
            Assert.Equal("Mun Lander 3", index.ResolveHoldAssignment("Jeb", openEnded: false).RecordingName);
            Assert.Null(index.ResolveHoldAssignment("Bob", openEnded: true));
            Assert.Equal(2, index.AssignmentCount);
        }

        // ---------------- the live cache ----------------

        private static void SeedRecording(string id, string vesselName, MergeState state)
        {
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                MergeState = state
            });
        }

        [Fact]
        public void Current_ReadsTheEffectiveLedger_CommittedMeansInTheEffectiveRecordingSet()
        {
            SeedRecording("rec-immutable", "Mun Lander 3", MergeState.Immutable);
            SeedRecording("rec-provisional", "Relay", MergeState.CommittedProvisional);
            SeedRecording("rec-notcommitted", "Draft", MergeState.NotCommitted);
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "ksc" });
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "immutable", RecordingId = "rec-immutable" });
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "provisional", RecordingId = "rec-provisional" });
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "notcommitted", RecordingId = "rec-notcommitted" });
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "unknown", RecordingId = "rec-live-tree" });

            var keys = CommittedFutureIndexCache.Current.FutureKeys(CommittedFutureKind.TechResearch, 0);

            Assert.Equal(new[] { "immutable", "ksc", "provisional" }, keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal("Mun Lander 3", CommittedFutureIndexCache.Current
                .FirstFuture(CommittedFutureKind.TechResearch, "immutable", 0).RecordingName);
        }

        [Fact]
        public void Current_ExcludesATombstonedRow()
        {
            var row = new GameAction { UT = 500, Type = GameActionType.KerbalHire, KerbalName = "Retired Row Kerman" };
            Ledger.AddAction(row);
            Assert.True(CommittedFutureIndexCache.Current.HasFuture(CommittedFutureKind.KerbalHire, "Retired Row Kerman", 0));

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>
                {
                    new LedgerTombstone { TombstoneId = "t1", ActionId = row.ActionId, RetiringRecordingId = "r" }
                },
                RewindPoints = new List<RewindPoint>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpTombstoneStateVersion();

            Assert.False(CommittedFutureIndexCache.Current.HasFuture(CommittedFutureKind.KerbalHire, "Retired Row Kerman", 0));
        }

        [Fact]
        public void Current_IsCachedUntilTheLedgerChanges_AndLogsOneRebuildLineWithCounters()
        {
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "a" });
            var first = CommittedFutureIndexCache.Current;
            var again = CommittedFutureIndexCache.Current;
            Assert.Same(first, again);
            Assert.Equal(1, CommittedFutureIndexCache.RebuildCount);

            Ledger.AddAction(new GameAction { UT = 600, Type = GameActionType.FacilityUpgrade, FacilityId = "F", ToLevel = 2 });
            var rebuilt = CommittedFutureIndexCache.Current;
            Assert.NotSame(first, rebuilt);
            Assert.Equal(2, CommittedFutureIndexCache.RebuildCount);
            Assert.True(rebuilt.HasFuture(CommittedFutureKind.FacilityUpgrade, "F", 0));

            Assert.Contains(logLines, l => l.Contains("[VERBOSE][CommittedFutureIndex]")
                && l.Contains("Rebuilt committed-future index: entries=2")
                && l.Contains("tech=1 facility=1 accept=0"));
        }

        [Fact]
        public void Invalidate_ForcesARebuild()
        {
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "a" });
            var first = CommittedFutureIndexCache.Current;

            CommittedFutureIndexCache.Invalidate("timeline changed");

            Assert.NotSame(first, CommittedFutureIndexCache.Current);
            Assert.Contains(logLines, l => l.Contains("[CommittedFutureIndex]")
                && l.Contains("Invalidated committed-future index: reason=timeline changed"));
        }

        [Fact]
        public void Current_CannotGoStaleAcrossALedgerOrchestratorReset()
        {
            // LedgerOrchestrator.ResetForTesting nulls OnTimelineDataChanged, so no
            // subscriber can invalidate; the ledger-version key must catch the change.
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.KerbalHire, KerbalName = "Old Kerman" });
            Assert.True(CommittedFutureIndexCache.Current.HasFuture(CommittedFutureKind.KerbalHire, "Old Kerman", 0));

            LedgerOrchestrator.ResetForTesting();
            Assert.Null(LedgerOrchestrator.OnTimelineDataChanged);
            Ledger.AddAction(new GameAction { UT = 500, Type = GameActionType.KerbalHire, KerbalName = "New Kerman" });

            var index = CommittedFutureIndexCache.Current;
            Assert.False(index.HasFuture(CommittedFutureKind.KerbalHire, "Old Kerman", 0));
            Assert.True(index.HasFuture(CommittedFutureKind.KerbalHire, "New Kerman", 0));
        }

        [Fact]
        public void CurrentUT_UsesTheTestProvider()
        {
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 1234.5;
            Assert.Equal(1234.5, CommittedFutureIndexCache.CurrentUT());
        }

        // ---------------- kerbal-retire milestone fallback ----------------

        private static void AddMilestone(GameStateEvent ev, bool committed = true, string recordingId = null)
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = Guid.NewGuid().ToString("N"),
                RecordingId = recordingId,
                Committed = committed,
                // The slice index is irrelevant to the index: a fully "replayed" milestone
                // still contributes, and the UT comparison decides "future".
                LastReplayedEventIndex = 0,
                Events = new List<GameStateEvent> { ev }
            });
        }

        [Fact]
        public void RetireFallback_ReadsCommittedCrewRemovedEvents_FutureByUT()
        {
            AddMilestone(new GameStateEvent { ut = 300, eventType = GameStateEventType.CrewRemoved, key = "Retiree Kerman" });
            AddMilestone(new GameStateEvent { ut = 300, eventType = GameStateEventType.CrewRemoved, key = "Draft Kerman" }, committed: false);
            AddMilestone(new GameStateEvent { ut = 300, eventType = GameStateEventType.CrewRemoved, key = "Hidden Kerman", recordingId = "rec_hidden" });

            var index = CommittedFutureIndexCache.Current;

            var entry = index.FirstFuture(CommittedFutureKind.KerbalRetire, "Retiree Kerman", 100);
            Assert.NotNull(entry);
            Assert.True(entry.FromMilestoneFallback);
            Assert.False(index.HasFuture(CommittedFutureKind.KerbalRetire, "Retiree Kerman", 300));
            Assert.False(index.HasFuture(CommittedFutureKind.KerbalRetire, "Draft Kerman", 100));
            Assert.False(index.HasFuture(CommittedFutureKind.KerbalRetire, "Hidden Kerman", 100));
        }

        [Fact]
        public void RetireFallback_ANewMilestoneRebuildsTheIndex()
        {
            Assert.False(CommittedFutureIndexCache.Current.HasFuture(CommittedFutureKind.KerbalRetire, "Late Kerman", 0));
            AddMilestone(new GameStateEvent { ut = 300, eventType = GameStateEventType.CrewRemoved, key = "Late Kerman" });
            Assert.True(CommittedFutureIndexCache.Current.HasFuture(CommittedFutureKind.KerbalRetire, "Late Kerman", 0));
        }

        [Fact]
        public void CrewRemoved_HasNoLedgerRepresentation()
        {
            // Why the retire kind needs the fallback: the converter drops the event.
            var action = GameStateEventConverter.ConvertEvent(
                new GameStateEvent { ut = 10, eventType = GameStateEventType.CrewRemoved, key = "X" }, null);
            Assert.Null(action);
        }
    }
}
