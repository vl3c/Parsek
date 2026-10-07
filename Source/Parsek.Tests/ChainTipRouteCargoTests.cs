using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHAIN-TIP-SNAPSHOT-CARRIES-UNPAID-ROUTE-CARGO (owner ruling 2026-10-07: subtract from the
    /// snapshot). A route delivers into station 777 before a committed mission docks to it
    /// (dock at UT 1500, undock 1600, the station half is the chain tip and its snapshot was
    /// captured at its end, UT 1700). A rewind to UT 1200 retires and refunds the route rows
    /// after 1200; the tip spawn must not carry the cargo of those crossings.
    /// </summary>
    [Collection("Sequential")]
    public class ChainTipRouteCargoTests : IDisposable
    {
        private const uint StationPid = 777u;
        private const uint TransportPid = 500u;
        private const string StationGuid = "5a7e11002b3c4d5e8f90a1b2c3d4e5f6";
        private const string TransportGuid = "7a7e11002b3c4d5e8f90a1b2c3d4e5f6";
        private const string OtherGuid = "9a7e11002b3c4d5e8f90a1b2c3d4e5f6";
        private const string TreeId = "tree-transport";
        private const double CutoffUT = 1200.0;
        private const double CaptureUT = 1700.0;
        private const uint StationPartA = 7771u;
        private const uint StationPartB = 7772u;
        private const uint TransportPart = 5001u;

        private readonly List<string> logLines = new List<string>();
        private readonly VesselSpawner.ResolveBodyNameByIndexDelegate originalBodyNameResolver;
        private readonly VesselSpawner.ResolveBodyByNameDelegate originalBodyResolver;
        private readonly VesselSpawner.ResolveBodyIndexDelegate originalBodyIndexResolver;
        private string tempDir;

        public ChainTipRouteCargoTests()
        {
            originalBodyNameResolver = VesselSpawner.BodyNameResolverForTesting;
            originalBodyResolver = VesselSpawner.BodyResolverForTesting;
            originalBodyIndexResolver = VesselSpawner.BodyIndexResolverForTesting;

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RetiredRouteCargoStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            VesselSpawner.BodyNameResolverForTesting = originalBodyNameResolver;
            VesselSpawner.BodyResolverForTesting = originalBodyResolver;
            VesselSpawner.BodyIndexResolverForTesting = originalBodyIndexResolver;
            TestBodyRegistry.Reset();
            RetiredRouteCargoStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        #region Pure fixture

        private static SnapshotTank Tank(uint part, string res, double amount, double max)
        {
            return new SnapshotTank { PartPersistentId = part, Resource = res, Amount = amount, MaxAmount = max };
        }

        private const string StationFingerprint = "fp-station";

        private static ChainTipCargoIdentity StationTip(bool withParts = true)
        {
            var tip = new ChainTipCargoIdentity
            {
                TipRecordingId = "station-tip",
                TipTreeId = TreeId,
                CaptureUT = CaptureUT,
                Fingerprint = StationFingerprint,
                EndpointPartIds = withParts ? new HashSet<uint> { StationPartA, StationPartB } : null
            };
            tip.AcceptedRecordingIds.Add("station-tip");
            tip.Vessels.Add(new KeyValuePair<uint, string>(StationPid, StationGuid));
            return tip;
        }

        private static RetiredRouteCargoTipTag StationTag(
            string recordingId = "station-tip", string fingerprint = StationFingerprint,
            string treeId = TreeId, double capture = CaptureUT, string chainId = null)
        {
            return new RetiredRouteCargoTipTag
            {
                TreeId = treeId,
                RecordingId = recordingId,
                ChainId = chainId,
                CaptureUT = capture,
                Fingerprint = fingerprint
            };
        }

        private static ReplayedCrossing Replay(double ut, string routeId = "route-a",
            GameActionType type = GameActionType.RouteCargoDelivered, int stop = 0)
        {
            return new ReplayedCrossing { RouteId = routeId, StopIndex = stop, Type = type, UT = ut };
        }

        private static RetiredRouteCargoRow Row(
            GameActionType type, double ut, string res, double amount,
            string routeId = "route-a", string cycle = null, uint endpointPid = StationPid,
            string endpointGuid = StationGuid, uint actualPid = 0u, double cutoff = CutoffUT,
            RetiredRouteCargoTipTag tag = null, bool untagged = false)
        {
            var row = new RetiredRouteCargoRow
            {
                Type = type,
                UT = ut,
                CutoffUT = cutoff,
                RouteId = routeId,
                CycleId = cycle ?? ("cycle-" + ((int)ut).ToString(CultureInfo.InvariantCulture)),
                StopIndex = 0,
                EndpointPid = endpointPid,
                EndpointGuid = endpointGuid,
                ActualVesselPid = actualPid,
                Resources = new Dictionary<string, double> { { res, amount } }
            };
            if (!untagged)
                row.Tips.Add(tag ?? StationTag());
            return row;
        }

        private static ChainTipCargoAdjustment Compute(
            List<SnapshotTank> tanks, params RetiredRouteCargoRow[] rows)
        {
            return ChainTipRouteCargo.ComputeAdjustment(tanks, rows, StationTip(), new List<ReplayedCrossing>());
        }

        #endregion

        #region Pure adjustment

        [Fact]
        public void Delivery_AfterCutoffBeforeCapture_IsRemovedFromTheTip()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100));

            Assert.Equal(200.0, adj.Amounts[0], 6);
            Assert.True(adj.Changed);
            Assert.Equal(1, adj.RowsApplied);
            ChainTipCargoEntry e = Assert.Single(adj.Entries);
            Assert.Equal("route-a", e.RouteId);
            Assert.Equal("LiquidFuel", e.Resource);
            Assert.Equal(100.0, e.Removed, 6);
            Assert.Equal(0.0, e.Clamped, 6);
        }

        [Fact]
        public void Delivery_LargerThanWhatTheTipHolds_ClampsEachTankAtZeroAndReportsTheRest()
        {
            var tanks = new List<SnapshotTank>
            {
                Tank(StationPartA, "LiquidFuel", 30, 400),
                Tank(StationPartB, "LiquidFuel", 20, 100)
            };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 80));

            Assert.Equal(0.0, adj.Amounts[0], 6);
            Assert.Equal(0.0, adj.Amounts[1], 6);
            ChainTipCargoEntry e = Assert.Single(adj.Entries);
            Assert.Equal(50.0, e.Removed, 6);
            Assert.Equal(30.0, e.Clamped, 6);
        }

        [Fact]
        public void Pickup_IsAddedBackCappedByCapacity()
        {
            var tanks = new List<SnapshotTank>
            {
                Tank(StationPartA, "Ore", 900, 1000),
                Tank(StationPartB, "Ore", 0, 50)
            };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoPickedUp, 1400, "Ore", 200));

            Assert.Equal(1000.0, adj.Amounts[0], 6);
            Assert.Equal(50.0, adj.Amounts[1], 6);
            ChainTipCargoEntry e = Assert.Single(adj.Entries);
            Assert.Equal(150.0, e.Added, 6);
            Assert.Equal(50.0, e.Clamped, 6);
        }

        [Fact]
        public void OriginDebitOfTheStation_ByTheWriterResolvedPid_IsAddedBack()
        {
            // The station is the route's ORIGIN: the debit row names the live vessel it drained.
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 100, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDebited, 1400, "LiquidFuel", 60,
                    endpointPid: 0u, endpointGuid: null, actualPid: StationPid));

            Assert.Equal(160.0, adj.Amounts[0], 6);
            Assert.Equal(60.0, Assert.Single(adj.Entries).Added, 6);
        }

        [Fact]
        public void RowNotAfterItsCutoff_IsUntouched()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1100, "LiquidFuel", 100));

            Assert.Equal(300.0, adj.Amounts[0], 6);
            Assert.False(adj.Changed);
            Assert.Equal(1, adj.SkippedNotAfterCutoff);
        }

        [Fact]
        public void RowAfterTheSnapshotCapture_IsUntouched()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1800, "LiquidFuel", 100));

            Assert.Equal(300.0, adj.Amounts[0], 6);
            Assert.Equal(1, adj.SkippedAfterCapture);
        }

        [Fact]
        public void RowAtTheSnapshotCapture_IsRemoved()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, CaptureUT, "LiquidFuel", 100));

            Assert.Equal(200.0, adj.Amounts[0], 6);
        }

        [Fact]
        public void RowsOfOtherVesselsRoutesOrLaunches_AreUntouched()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                // another route into another vessel
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100,
                    routeId: "route-b", endpointPid: 999u, endpointGuid: OtherGuid),
                // the station's baked pid on a different launch
                Row(GameActionType.RouteCargoDelivered, 1310, "LiquidFuel", 100,
                    routeId: "route-c", endpointGuid: OtherGuid),
                // a pickup the writer resolved on another vessel
                Row(GameActionType.RouteCargoPickedUp, 1320, "LiquidFuel", 100,
                    routeId: "route-d", endpointPid: 0u, endpointGuid: null, actualPid: 999u));

            Assert.Equal(300.0, adj.Amounts[0], 6);
            Assert.Equal(3, adj.SkippedOtherVessel);
            Assert.Empty(adj.Entries);
        }

        [Fact]
        public void UnknownLaunchGuidOnTheEndpoint_MatchesOnPid()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, endpointGuid: null));

            Assert.Equal(200.0, adj.Amounts[0], 6);
        }

        [Fact]
        public void RowTaggedForAnotherSnapshot_IsUntouched()
        {
            // Another tree's tip, a later tip of this tree, and this recording with its snapshot
            // replaced in place: none is the snapshot that carries the crossing.
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100,
                    tag: StationTag(treeId: "tree-other")),
                Row(GameActionType.RouteCargoDelivered, 1310, "LiquidFuel", 100,
                    tag: StationTag(recordingId: "station-cont")),
                Row(GameActionType.RouteCargoDelivered, 1320, "LiquidFuel", 100,
                    tag: StationTag(fingerprint: "fp-replaced")));

            Assert.Equal(300.0, adj.Amounts[0], 6);
            Assert.Equal(3, adj.SkippedOtherSnapshot);
        }

        [Fact]
        public void TagMatchesTip_FollowsTheOptimizerChain_NeverAnotherRecording()
        {
            ChainTipCargoIdentity tip = StationTip();
            tip.TipRecordingId = "station-tip-2";
            tip.ChainId = "chain-1";
            tip.AcceptedRecordingIds.Clear();
            tip.AcceptedRecordingIds.Add("station-tip-2");
            tip.AcceptedRecordingIds.Add("station-tip"); // the split's first half
            tip.TreeRecordingIds = new HashSet<string> { "station-tip", "station-tip-2", "station-cont" };

            // The split moved the tagged snapshot on to the later half.
            Assert.True(ChainTipRouteCargo.TagMatchesTip(StationTag(), tip));
            // A segment of the chain the optimizer merged away.
            Assert.True(ChainTipRouteCargo.TagMatchesTip(
                StationTag(recordingId: "merged-away", chainId: "chain-1"), tip));
            // Another recording of the tree that still exists, even on the same chain id.
            Assert.False(ChainTipRouteCargo.TagMatchesTip(
                StationTag(recordingId: "station-cont", chainId: "chain-1"), tip));
            // The same recording with another snapshot.
            Assert.False(ChainTipRouteCargo.TagMatchesTip(StationTag(fingerprint: "fp-other"), tip));
            // Another tree.
            Assert.False(ChainTipRouteCargo.TagMatchesTip(StationTag(treeId: "tree-other"), tip));
        }

        [Fact]
        public void CrossingTheCurrentTimelinePerformedAgain_IsUntouched()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };
            RetiredRouteCargoRow row = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100);

            ChainTipCargoAdjustment adj = ChainTipRouteCargo.ComputeAdjustment(
                tanks, new[] { row }, StationTip(), new[] { Replay(1300) });

            Assert.Equal(300.0, adj.Amounts[0], 6);
            Assert.Equal(1, adj.SkippedReplayed);
        }

        [Fact]
        public void ReplayUnderAnotherCycleIdAndUT_StandsForTheNearestCrossingOnly()
        {
            // Two carried crossings; the replay (another cycle id, a tick later) pays for the
            // one at 1300, so only the 1400 crossing comes out.
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = ChainTipRouteCargo.ComputeAdjustment(tanks,
                new[]
                {
                    Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, cycle: "cycle-3"),
                    Row(GameActionType.RouteCargoDelivered, 1400, "LiquidFuel", 60, cycle: "cycle-4")
                },
                StationTip(), new[] { Replay(1300.7) });

            Assert.Equal(240.0, adj.Amounts[0], 6);
            Assert.Equal(1, adj.SkippedReplayed);
            Assert.Equal(60.0, Assert.Single(adj.Entries).Removed, 6);
        }

        [Fact]
        public void ReplayOfAnotherRouteStopTypeOrOutsideTheWindow_StandsForNothing()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = ChainTipRouteCargo.ComputeAdjustment(tanks,
                new[] { Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100) },
                StationTip(),
                new[]
                {
                    Replay(1300, routeId: "route-b"),
                    Replay(1300, stop: 1),
                    Replay(1300, type: GameActionType.RouteCargoPickedUp),
                    Replay(1100),          // at or before the cutoff: kept history
                    Replay(1800)           // after the snapshot capture
                });

            Assert.Equal(200.0, adj.Amounts[0], 6);
            Assert.Equal(0, adj.SkippedReplayed);
        }

        [Fact]
        public void ReplayStandsForTheNearestCrossing_NotTheFirstListed()
        {
            // The later crossing is listed first; the replay at 1300.7 still pays for 1300.
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = ChainTipRouteCargo.ComputeAdjustment(tanks,
                new[]
                {
                    Row(GameActionType.RouteCargoDelivered, 1400, "LiquidFuel", 60, cycle: "cycle-4"),
                    Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, cycle: "cycle-3")
                },
                StationTip(), new[] { Replay(1300.7) });

            Assert.Equal(240.0, adj.Amounts[0], 6);
            Assert.Equal(60.0, Assert.Single(adj.Entries).Removed, 6);
        }

        [Fact]
        public void OneReplay_PaysForOneCrossing()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = ChainTipRouteCargo.ComputeAdjustment(tanks,
                new[]
                {
                    Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, cycle: "cycle-3"),
                    Row(GameActionType.RouteCargoDelivered, 1301, "LiquidFuel", 100, cycle: "cycle-4")
                },
                StationTip(), new[] { Replay(1300.5) });

            Assert.Equal(200.0, adj.Amounts[0], 6);
            Assert.Equal(1, adj.SkippedReplayed);
        }

        [Fact]
        public void Removal_TakesTheClaimedVesselsOwnPartsFirst_ThenTheRest()
        {
            // Tank order puts the docked transport's tank first; the station's own part pays first.
            var tanks = new List<SnapshotTank>
            {
                Tank(TransportPart, "LiquidFuel", 200, 200),
                Tank(StationPartA, "LiquidFuel", 60, 400)
            };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100));

            Assert.Equal(0.0, adj.Amounts[1], 6);
            Assert.Equal(160.0, adj.Amounts[0], 6);
        }

        [Fact]
        public void TwoRoutesOneResource_EachReportedOnItsOwn()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, routeId: "route-a"),
                Row(GameActionType.RouteCargoDelivered, 1350, "LiquidFuel", 50, routeId: "route-b"));

            Assert.Equal(150.0, adj.Amounts[0], 6);
            Assert.Equal(2, adj.Entries.Count);
            Assert.Equal(100.0, adj.Entries.Single(x => x.RouteId == "route-a").Removed, 6);
            Assert.Equal(50.0, adj.Entries.Single(x => x.RouteId == "route-b").Removed, 6);
        }

        [Fact]
        public void TagForChainTips_KeepsOnlyRowsATipSnapshotCarries()
        {
            var inWindow = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, untagged: true);
            var afterCapture = Row(GameActionType.RouteCargoDelivered, 1800, "LiquidFuel", 100, untagged: true);
            var otherVessel = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100,
                routeId: "route-b", endpointPid: 999u, endpointGuid: OtherGuid, untagged: true);

            List<RetiredRouteCargoRow> kept = ChainTipRouteCargo.TagForChainTips(
                new[] { inWindow, afterCapture, otherVessel }, new[] { StationTip() }, null);

            RetiredRouteCargoRow only = Assert.Single(kept);
            Assert.Same(inWindow, only);
            RetiredRouteCargoTipTag tag = Assert.Single(only.Tips);
            Assert.Equal("station-tip", tag.RecordingId);
            Assert.Equal(StationFingerprint, tag.Fingerprint);
            Assert.Equal(CaptureUT, tag.CaptureUT);
        }

        [Fact]
        public void TagForChainTips_RowAboveTheSnapshotsWatermark_IsNotTagged()
        {
            // A row created after an earlier retire at 1250 belongs to a timeline that branched
            // after the snapshot was captured, so the snapshot cannot hold it.
            var below = Row(GameActionType.RouteCargoDelivered, 1240, "LiquidFuel", 100, cutoff: 1210, untagged: true);
            var above = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, cutoff: 1210, untagged: true);

            List<RetiredRouteCargoRow> kept = ChainTipRouteCargo.TagForChainTips(
                new[] { below, above }, new[] { StationTip() }, _ => 1250.0);

            Assert.Same(below, Assert.Single(kept));
        }

        #endregion

        #region Stash

        private static GameAction DeliveredAction(string routeId, string cycleId, double ut, string res, double amount)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 3,
                RouteResourceManifest = new Dictionary<string, double> { { res, amount } }
            };
        }

        private static GameAction DispatchedAction(string routeId, string cycleId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 0
            };
        }

        [Fact]
        public void Merge_SameRowAgain_FoldsItsTags_AnotherRowOfTheCycleStaysApart()
        {
            RetiredRouteCargoRow first = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, cycle: "cycle-3");
            RetiredRouteCargoRow again = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100,
                cycle: "cycle-3", tag: StationTag(recordingId: "station-cont", fingerprint: "fp-cont"));
            // Same cycle id, another crossing (cycle ids are rebuilt at every rewind).
            RetiredRouteCargoRow sameIdOtherCrossing = Row(GameActionType.RouteCargoDelivered, 1400, "LiquidFuel", 100,
                cycle: "cycle-3");

            Assert.Equal(1, RetiredRouteCargoStore.Merge(new[] { first }, out int merged0));
            Assert.Equal(0, merged0);
            Assert.Equal(1, RetiredRouteCargoStore.Merge(new[] { again, sameIdOtherCrossing }, out int merged1));
            Assert.Equal(1, merged1);

            Assert.Equal(2, RetiredRouteCargoStore.Rows.Count);
            RetiredRouteCargoRow folded = RetiredRouteCargoStore.Rows.Single(r => r.UT == 1300.0);
            Assert.Equal(new[] { "station-tip", "station-cont" }, folded.Tips.Select(t => t.RecordingId).ToArray());
        }

        [Fact]
        public void BuildRow_TakesTheEndpointFromTheRouteStop_AndSkipsRowsThatMovedNoResource()
        {
            Route route = StationRoute("route-a");
            GameAction delivered = DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100);
            var fundsOnlyDebit = new GameAction
            {
                Type = GameActionType.RouteCargoDebited,
                UT = 1300,
                RouteId = "route-a",
                RouteCycleId = "cycle-1",
                RouteKscFundsCost = 500f
            };

            RetiredRouteCargoRow row = RetiredRouteCargoStore.BuildRow(delivered, CutoffUT, route);

            Assert.NotNull(row);
            Assert.Equal(StationPid, row.EndpointPid);
            Assert.Equal(StationGuid, row.EndpointGuid);
            Assert.Equal(CutoffUT, row.CutoffUT);
            Assert.Equal(100.0, row.Resources["LiquidFuel"], 6);
            Assert.Null(RetiredRouteCargoStore.BuildRow(fundsOnlyDebit, CutoffUT, route));
        }

        [Fact]
        public void Stash_RoundTripsThroughTheLedgerFile_AndAnEmptyStashWritesNoNode()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-chaintipcargo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "ledger.pgld");

            Assert.True(Ledger.SaveToFile(path));
            Assert.DoesNotContain(RetiredRouteCargoStore.NodeName, File.ReadAllText(path));

            RetiredRouteCargoRow row = Row(GameActionType.RouteCargoPickedUp, 1300.25, "Ore", 12.5,
                cycle: "cycle-7", actualPid: StationPid);
            RetiredRouteCargoStore.Merge(new[] { row }, out _);
            RetiredRouteCargoStore.LowerWatermark(StationTag(chainId: "chain-9"), 1250.5, null);
            Assert.True(Ledger.SaveToFile(path));

            RetiredRouteCargoStore.ResetForTesting();
            Assert.True(Ledger.LoadFromFile(path));

            RetiredRouteCargoRow back = Assert.Single(RetiredRouteCargoStore.Rows);
            Assert.Equal(GameActionType.RouteCargoPickedUp, back.Type);
            Assert.Equal(1300.25, back.UT);
            Assert.Equal(CutoffUT, back.CutoffUT);
            Assert.Equal("route-a", back.RouteId);
            Assert.Equal("cycle-7", back.CycleId);
            Assert.Equal(0, back.StopIndex);
            Assert.Equal(StationPid, back.EndpointPid);
            Assert.Equal(StationGuid, back.EndpointGuid);
            Assert.Equal(StationPid, back.ActualVesselPid);
            Assert.Equal(12.5, back.Resources["Ore"]);
            RetiredRouteCargoTipTag tag = Assert.Single(back.Tips);
            Assert.Equal(TreeId, tag.TreeId);
            Assert.Equal("station-tip", tag.RecordingId);
            Assert.Equal(StationFingerprint, tag.Fingerprint);
            Assert.Equal(CaptureUT, tag.CaptureUT);
            RetiredRouteCargoWatermark mark = Assert.Single(RetiredRouteCargoStore.Watermarks);
            Assert.Equal(1250.5, mark.LowestCutoffUT);
            Assert.Equal("chain-9", mark.Snapshot.ChainId);
        }

        [Fact]
        public void LedgerClear_KeepsTheStash()
        {
            RetiredRouteCargoStore.Merge(new[] { Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 1) }, out _);

            Ledger.Clear();

            Assert.Single(RetiredRouteCargoStore.Rows);
        }

        #endregion

        #region Retire sites

        private static Recording MakeRecording(
            string id, uint pid, string guid, double startUT, double endUT,
            TerminalState? terminal, string parentBpId, string childBpId, string name,
            params (uint part, double lf, double lfMax)[] parts)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "ORBITING");
            snapshot.AddValue("type", "Station");
            snapshot.AddValue("name", name);
            snapshot.AddValue("lat", "0");
            snapshot.AddValue("lon", "0");
            snapshot.AddValue("alt", "100000");
            var orbit = snapshot.AddNode("ORBIT");
            orbit.AddValue("SMA", "700000");
            orbit.AddValue("ECC", "0.01");
            orbit.AddValue("INC", "0");
            orbit.AddValue("LPE", "0");
            orbit.AddValue("LAN", "0");
            orbit.AddValue("MNA", "0");
            orbit.AddValue("EPH", "100");
            orbit.AddValue("REF", "0");
            foreach (var p in parts)
            {
                ConfigNode part = snapshot.AddNode("PART");
                part.AddValue("name", "fuelTank");
                part.AddValue("persistentId", p.part.ToString(CultureInfo.InvariantCulture));
                ConfigNode res = part.AddNode("RESOURCE");
                res.AddValue("name", "LiquidFuel");
                res.AddValue("amount", p.lf.ToString("R", CultureInfo.InvariantCulture));
                res.AddValue("maxAmount", p.lfMax.ToString("R", CultureInfo.InvariantCulture));
            }
            return new Recording
            {
                RecordingId = id,
                TreeId = TreeId,
                VesselName = name,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000,
                ParentBranchPointId = parentBpId,
                ChildBranchPointId = childBpId,
                VesselSnapshot = snapshot,
                PlaybackEnabled = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT, bodyName = "Kerbin", altitude = 100000 },
                    new TrajectoryPoint { ut = endUT, bodyName = "Kerbin", altitude = 100000 }
                }
            };
        }

        /// <summary>
        /// Transport (500, part 5001) docks to station 777 at 1500 (station dominant), undocks at
        /// 1600; the station half (777, parts 7771/7772) is the chain tip, ending at 1700.
        /// </summary>
        private static Recording CommitDockUndockTree()
        {
            var transport = MakeRecording("tr-predock", TransportPid, TransportGuid, 1000, 1500,
                TerminalState.Docked, null, "bp-dock", "Transport", (TransportPart, 200, 200));
            var merged = MakeRecording("merged", StationPid, StationGuid, 1500, 1600,
                null, "bp-dock", "bp-undock", "Station",
                (TransportPart, 200, 200), (StationPartA, 300, 400), (StationPartB, 50, 100));
            var stationTip = MakeRecording("station-tip", StationPid, StationGuid, 1600, CaptureUT,
                TerminalState.Orbiting, "bp-undock", null, "Station",
                (StationPartA, 300, 400), (StationPartB, 50, 100));
            var transportHalf = MakeRecording("tr-half", 900u, TransportGuid, 1600, 1800,
                TerminalState.Orbiting, "bp-undock", null, "Transport", (TransportPart, 200, 200));

            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Transport",
                RootRecordingId = transport.RecordingId
            };
            tree.AddOrReplaceRecording(transport);
            tree.AddOrReplaceRecording(merged);
            tree.AddOrReplaceRecording(stationTip);
            tree.AddOrReplaceRecording(transportHalf);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                Type = BranchPointType.Dock,
                UT = 1500,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { transport.RecordingId },
                ChildRecordingIds = new List<string> { merged.RecordingId }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-undock",
                Type = BranchPointType.Undock,
                UT = 1600,
                ParentRecordingIds = new List<string> { merged.RecordingId },
                ChildRecordingIds = new List<string> { stationTip.RecordingId, transportHalf.RecordingId }
            });
            RecordingStore.AddCommittedTreeForTesting(tree);
            return stationTip;
        }

        private static Route StationRoute(string id, uint endpointPid = StationPid, string guid = StationGuid)
        {
            return new Route
            {
                Id = id,
                Name = "route-" + id,
                Status = RouteStatus.Active,
                CreatedUT = 100.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint
                        {
                            VesselPersistentId = endpointPid,
                            LaunchGuid = guid,
                            BodyName = "Kerbin"
                        }
                    }
                }
            };
        }

        private static void SeedRouteHistory()
        {
            RouteStore.AddRoute(StationRoute("route-a"));
            RouteStore.AddRoute(StationRoute("route-b", 999u, OtherGuid));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-0", 1100, "LiquidFuel", 100)); // kept
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100)); // in the tip
            Ledger.AddAction(DeliveredAction("route-a", "cycle-2", 1800, "LiquidFuel", 100)); // after capture
            Ledger.AddAction(DeliveredAction("route-b", "cycle-1", 1400, "LiquidFuel", 100)); // other vessel
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = 1290,
                RouteId = "route-a",
                RouteCycleId = "cycle-1",
                RouteStopIndex = 0
            });
        }

        private static void AssertOnlyTheTipsDeliveryStashed()
        {
            RetiredRouteCargoRow row = Assert.Single(RetiredRouteCargoStore.Rows);
            Assert.Equal("route-a", row.RouteId);
            Assert.Equal("cycle-1", row.CycleId);
            Assert.Equal(1300.0, row.UT);
            Assert.Equal(CutoffUT, row.CutoffUT);
            Assert.Equal(StationPid, row.EndpointPid);
            RetiredRouteCargoTipTag tag = Assert.Single(row.Tips);
            Assert.Equal(TreeId, tag.TreeId);
            Assert.Equal("station-tip", tag.RecordingId);
            Assert.Equal(CaptureUT, tag.CaptureUT);
            Assert.Equal(ChainTipRouteCargo.SnapshotFingerprint(
                RecordingStore.CommittedTrees[0].Recordings["station-tip"].VesselSnapshot), tag.Fingerprint);
            Assert.Equal(100.0, row.Resources["LiquidFuel"], 6);
        }

        [Fact]
        public void GoBackRetire_StashesOnlyTheDeliveriesTheTipSnapshotCarries()
        {
            CommitDockUndockTree();
            SeedRouteHistory();

            int retired = Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            Assert.Equal(4, retired);
            AssertOnlyTheTipsDeliveryStashed();
            Assert.Contains(logLines, l => l.Contains("[ChainTipCargo]")
                && l.Contains("stashed=1"));
        }

        [Fact]
        public void ReFlyRestore_StashesTheSameRows()
        {
            CommitDockUndockTree();
            SeedRouteHistory();

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, CutoffUT);

            AssertOnlyTheTipsDeliveryStashed();
        }

        [Fact]
        public void Retire_WithNoCommittedChain_StashesNothing()
        {
            // A crossing retired before any mission claimed the station is in no snapshot:
            // a tip committed later is captured after the rewind.
            SeedRouteHistory();

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            Assert.Empty(RetiredRouteCargoStore.Rows);
        }

        #endregion

        #region Spawn materialization (site)

        private void InstallKerbin()
        {
            TestBodyRegistry.Install(("Kerbin", 600000.0, 3.5316e12));
            VesselSpawner.BodyNameResolverForTesting = TestBodyRegistry.ResolveBodyNameByIndex;
            VesselSpawner.BodyResolverForTesting = TestBodyRegistry.ResolveBodyByName;
            VesselSpawner.BodyIndexResolverForTesting = TestBodyRegistry.ResolveBodyIndex;
        }

        private static double LiquidFuelOf(ConfigNode vessel, uint partPid)
        {
            foreach (ConfigNode part in vessel.GetNodes("PART"))
            {
                if (part.GetValue("persistentId") != partPid.ToString(CultureInfo.InvariantCulture))
                    continue;
                return double.Parse(part.GetNode("RESOURCE").GetValue("amount"), CultureInfo.InvariantCulture);
            }
            throw new InvalidOperationException("part not found");
        }

        [Fact]
        public void SpawnMaterialization_ChainTip_TakesTheRetiredDeliveryOutOfTheCopyOnly()
        {
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "chain-tip-test");

            Assert.NotNull(copy);
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
            Assert.Equal(50.0, LiquidFuelOf(copy, StationPartB), 6);
            // The committed snapshot keeps what the mission recorded.
            Assert.Equal(300.0, LiquidFuelOf(tip.VesselSnapshot, StationPartA), 6);
            Assert.Contains(logLines, l => l.Contains("[ChainTipCargo]")
                && l.Contains("chain-tip-test")
                && l.Contains("route-a")
                && l.Contains("LiquidFuel")
                && l.Contains("removed=100"));
        }

        /// <summary>
        /// Attaches a switch continuation under the station tip: the station, spawned with the
        /// cargo already taken out (A = 200), was flown on to UT 2000 and committed. It is the
        /// chain's new tip, in the same tree, with its own snapshot.
        /// </summary>
        private static Recording AttachSwitchContinuation(Recording stationTip)
        {
            RecordingTree tree = RecordingStore.CommittedTrees[0];
            var cont = MakeRecording("station-cont", StationPid, StationGuid, CaptureUT, 2000,
                TerminalState.Orbiting, "bp-switch", null, "Station",
                (StationPartA, 200, 400), (StationPartB, 50, 100));
            stationTip.ChildBranchPointId = "bp-switch";
            stationTip.TerminalStateValue = null;
            tree.AddOrReplaceRecording(cont);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-switch",
                Type = BranchPointType.VesselSwitchContinuation,
                UT = CaptureUT,
                ParentRecordingIds = new List<string> { stationTip.RecordingId },
                ChildRecordingIds = new List<string> { cont.RecordingId }
            });
            return cont;
        }

        [Fact]
        public void LaterTipInTheSameTree_SwitchContinuation_GetsNoneOfTheEarlierTipsCargo()
        {
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            ConfigNode first = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "tip-spawn");
            Assert.Equal(200.0, LiquidFuelOf(first, StationPartA), 6);

            Recording cont = AttachSwitchContinuation(tip);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(cont, 2050.0, "continuation-spawn");

            Assert.NotNull(copy);
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void LaterTipInTheSameTree_ReFlyProvisional_GetsNoneOfTheEarlierTipsCargo()
        {
            // A Re-Fly fork of the station half replaces it as the tip, in the same tree, with
            // a snapshot copied from the origin (the inherited copy a fork starts from).
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            RecordingTree tree = RecordingStore.CommittedTrees[0];
            var fork = MakeRecording("rec_refly", StationPid, StationGuid, 1600, 1720,
                TerminalState.Orbiting, "bp-undock", null, "Station");
            fork.VesselSnapshot = tip.VesselSnapshot.CreateCopy();
            tree.Recordings.Remove(tip.RecordingId);
            tree.AddOrReplaceRecording(fork);
            BranchPoint undock = tree.BranchPoints.Single(b => b.Id == "bp-undock");
            undock.ChildRecordingIds[undock.ChildRecordingIds.IndexOf(tip.RecordingId)] = fork.RecordingId;

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(fork, 1750.0, "refly-spawn");

            Assert.NotNull(copy);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void ReplayPaidUnderAnotherCycleId_AfterTheCounterRebuild_IsLeftInTheCopy()
        {
            // Cycles 0 and 1 fire, cycle 2 is blocked (no dispatch row), cycle-3 delivers at
            // 1300; the go-back rewind to 1200 rebuilds the counters from the kept dispatches
            // (max ordinal 1, so 2), and the station standing live at the Space Center takes
            // the 1300 crossing again, paid as cycle-2.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            Route route = StationRoute("route-a");
            RouteStore.AddRoute(route);
            Ledger.AddAction(DispatchedAction("route-a", "cycle-0", 1000));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-0", 1000, "LiquidFuel", 100));
            Ledger.AddAction(DispatchedAction("route-a", "cycle-1", 1100));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1100, "LiquidFuel", 100));
            Ledger.AddAction(DispatchedAction("route-a", "cycle-3", 1300));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-3", 1300, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out List<GameAction> kept);
            RouteRewindClassifier.ReconcileStoreAtRewind(
                new List<Route>(RouteStore.CommittedRoutes), new List<Route>(RouteStore.DormantRoutes),
                CutoffUT, kept, logTag: "Rewind", logPrefix: "test go-back");
            Route reconciled = RouteStore.CommittedRoutes.Single(r => r.Id == "route-a");
            string replayCycle = "cycle-" + (reconciled.CompletedCycles + reconciled.SkippedCycles)
                .ToString(CultureInfo.InvariantCulture);
            Assert.Equal("cycle-2", replayCycle);
            Ledger.AddAction(DispatchedAction("route-a", replayCycle, 1300.6));
            Ledger.AddAction(DeliveredAction("route-a", replayCycle, 1300.6, "LiquidFuel", 100));

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "ksc-end-spawn");

            Assert.NotNull(copy);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void LoadBackInTimeWithNoRouteState_StillLowersTheWatermark()
        {
            // An F9 at the Space Center back to 1000 with the route idle after it: the route
            // reconcile skips, retiring nothing. The route then delivers at 1300 in the new
            // timeline (into the station standing live at the KSC); a go-back rewind to 1250
            // retires that row. The tip snapshot (timeline 1) never held it.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            RouteStore.AddRoute(StationRoute("route-a"));

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.InSessionOther, 1000.0, 1000.0, new List<Route>());
            Assert.Equal(RouteLoadReconcileOutcome.SkippedNothingAfterCutoff, outcome);
            Ledger.AddAction(DeliveredAction("route-a", "cycle-0", 1300, "LiquidFuel", 100));
            Ledger.RetireFutureRouteActionsAtRewind(1250.0, out _);

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "after-f9");

            Assert.NotNull(copy);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void RetireThatRemovedNothing_StillLowersTheWatermark()
        {
            // A go-back rewind to 1200 with no route row after it; the route then delivers at
            // 1300 in the new timeline; a second rewind to 1250 retires that row.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            RouteStore.AddRoute(StationRoute("route-a"));

            Assert.Equal(0, Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-0", 1300, "LiquidFuel", 100));
            Ledger.RetireFutureRouteActionsAtRewind(1250.0, out _);

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "after-empty-retire");

            Assert.NotNull(copy);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void SecondRewind_ReplayFromTheLaterTimeline_IsNotTakenOutTwice()
        {
            // Timeline 2 took the 1300 crossing again into the live pre-claim station (paid as
            // another cycle); a second rewind to 1250 retires that replay. The tip snapshot
            // carries the crossing once, so it is taken out once.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            Ledger.AddAction(DeliveredAction("route-a", "cycle-7", 1300.4, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(1250.0, out _);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "second-rewind");

            Assert.NotNull(copy);
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void OptimizerSplitAfterTheRetire_TheHalfThatGotTheSnapshotIsStillAdjusted()
        {
            // The optimizer splits the station tip after the rewind: the first half keeps the
            // id the rows were tagged with, the second half takes the snapshot under a new id.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            RecordingTree tree = RecordingStore.CommittedTrees[0];
            var tail = MakeRecording("station-tip-2", StationPid, StationGuid, 1650, CaptureUT,
                TerminalState.Orbiting, null, null, "Station");
            tail.VesselSnapshot = tip.VesselSnapshot;
            tip.VesselSnapshot = null;
            tip.ExplicitEndUT = 1650;
            tip.Points[tip.Points.Count - 1] = new TrajectoryPoint { ut = 1650, bodyName = "Kerbin", altitude = 100000 };
            tip.TerminalStateValue = null;
            tip.ChainId = "chain-station";
            tip.ChainIndex = 0;
            tail.ChainId = "chain-station";
            tail.ChainIndex = 1;
            tree.AddOrReplaceRecording(tail);

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tail, 1750.0, "split-tail");

            Assert.NotNull(copy);
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void SnapshotReplacedInPlace_SameRecordingId_GetsNone()
        {
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            foreach (ConfigNode part in tip.VesselSnapshot.GetNodes("PART"))
            {
                if (part.GetValue("persistentId") == StationPartA.ToString(CultureInfo.InvariantCulture))
                    part.GetNode("RESOURCE").SetValue("amount", "250");
            }

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "replaced-in-place");

            Assert.NotNull(copy);
            Assert.Equal(250.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void SpawnMaterialization_RecordingThatIsNotAChainTip_IsLeftAsItIs()
        {
            InstallKerbin();
            CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            Recording transportHalf = RecordingStore.CommittedTrees[0].Recordings["tr-half"];

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(transportHalf, 1850.0, "not-a-tip");

            Assert.NotNull(copy);
            Assert.Equal(200.0, LiquidFuelOf(copy, TransportPart), 6);
        }

        [Fact]
        public void SpawnMaterialization_CrossingBackInTheEffectiveLedger_IsLeftInTheCopy()
        {
            // The pre-claim station stood live and the route delivered cycle-1 again: paid again,
            // and the snapshot's copy of that crossing is the one that survives.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1305, "LiquidFuel", 100));

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "replayed");

            Assert.NotNull(copy);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void ApplyToSpawnCopy_TwoSpawnAttempts_EachCopyAdjustedOnce()
        {
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            ConfigNode first = tip.VesselSnapshot.CreateCopy();
            ChainTipRouteCargo.ApplyToSpawnCopy(first, tip, "attempt-1");
            ConfigNode second = tip.VesselSnapshot.CreateCopy();
            ChainTipRouteCargo.ApplyToSpawnCopy(second, tip, "attempt-2");

            Assert.Equal(200.0, LiquidFuelOf(first, StationPartA), 6);
            Assert.Equal(200.0, LiquidFuelOf(second, StationPartA), 6);
        }

        /// <summary>
        /// Every site that makes a spawn copy of a recording's stored snapshot takes the retired
        /// route cargo out of it: the shared materialization (flight leaf, Tracking Station,
        /// the chain fallbacks), the flight chain tip's own copy and the Space Center's.
        /// </summary>
        [Fact]
        public void EverySpawnCopySite_TakesTheRetiredRouteCargoOut()
        {
            string root = FindRepoRoot();
            AssertMethodCallsApply(Path.Combine(root, "Source", "Parsek", "VesselGhoster.cs"),
                "private uint SpawnChainTipWithResolvedState(", "spawnSnapshot");
            AssertMethodCallsApply(Path.Combine(root, "Source", "Parsek", "ParsekKSC.cs"),
                "ConfigNode spawnSnapshot = rec.VesselSnapshot.CreateCopy();", "spawnSnapshot");
            AssertMethodCallsApply(Path.Combine(root, "Source", "Parsek", "VesselSpawner.cs"),
                "internal static ConfigNode BuildValidatedRespawnSnapshot(\n            Recording rec,\n            double currentUT,\n            string logContext,\n            out string materializationRejectionReason)",
                "snapshot");
        }

        private static void AssertMethodCallsApply(string path, string anchor, string copyName)
        {
            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            int at = text.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(at >= 0, "anchor not found in " + Path.GetFileName(path) + ": " + anchor);
            // The call must follow the anchor within the same method (before the next member).
            int end = text.IndexOf("\n        }\n", at, StringComparison.Ordinal);
            Assert.True(end > at, "method end not found after the anchor in " + Path.GetFileName(path));
            string body = Regex.Replace(text.Substring(at, end - at), @"//[^\n]*", "");
            Assert.True(
                Regex.IsMatch(body, @"ChainTipRouteCargo\.ApplyToSpawnCopy\(\s*" + copyName + @"\s*,"),
                Path.GetFileName(path) + " makes a spawn copy without ChainTipRouteCargo.ApplyToSpawnCopy("
                + copyName + ", ...) after: " + anchor);
        }

        private static string FindRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "Source", "Parsek")))
                    return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new InvalidOperationException("repo root not found");
        }

        #endregion

        #region Ordinary end-of-recording spawns (owner ruling 2026-10-07)

        private const string LeafTreeId = "tree-station";
        private const uint StationRootUid = 4242u;

        /// <summary>
        /// The route-fed station's own recording (for example the station flown on from the
        /// Tracking Station): an ordinary leaf of tree tree-station, no ghost chain anywhere,
        /// ending at <see cref="CaptureUT"/> holding A = 300/400 and B = 50/100.
        /// </summary>
        private static Recording CommitStationLeaf(
            string recordingGuid = StationGuid, uint rootUid = 0u, uint pid = StationPid)
        {
            Recording leaf = MakeRecording("station-leaf", pid, recordingGuid, 1000, CaptureUT,
                TerminalState.Orbiting, null, null, "Station",
                (StationPartA, 300, 400), (StationPartB, 50, 100));
            leaf.TreeId = LeafTreeId;
            if (rootUid != 0u)
            {
                leaf.VesselSnapshot.AddValue("root", "0");
                leaf.VesselSnapshot.GetNodes("PART")[0].AddValue("uid", rootUid.ToString(CultureInfo.InvariantCulture));
            }
            var tree = new RecordingTree { Id = LeafTreeId, TreeName = "Station", RootRecordingId = leaf.RecordingId };
            tree.AddOrReplaceRecording(leaf);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return leaf;
        }

        /// <summary>
        /// Gives a recording the shape the optimizer trims: one coasting (boring) section over
        /// its whole span with an orbit matching its terminal orbit, points every
        /// <paramref name="step"/> seconds, and its last interesting event at
        /// <paramref name="interestingUT"/>.
        /// </summary>
        private static void MakeTrimmable(Recording rec, double interestingUT, double step)
        {
            double start = rec.StartUT;
            double end = rec.EndUT;
            rec.Points.Clear();
            for (double t = start; t <= end + 1e-6; t += step)
                rec.Points.Add(new TrajectoryPoint { ut = t, bodyName = "Kerbin", altitude = 100000 });
            rec.TrackSections.Clear();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = start,
                endUT = end
            });
            rec.OrbitSegments.Clear();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = start,
                endUT = end,
                bodyName = "Kerbin",
                semiMajorAxis = rec.TerminalOrbitSemiMajorAxis
            });
            rec.SegmentEvents.Clear();
            rec.SegmentEvents.Add(new SegmentEvent { ut = interestingUT, type = SegmentEventType.ControllerChange });
        }

        /// <summary>
        /// The orbit segment is the tail-trim's terminal-shape witness only; the headless spawn
        /// materialization cannot propagate an orbit endpoint (FlightGlobals), and the cargo
        /// adjustment does not read it, so it is dropped once the trim has run.
        /// </summary>
        private static void DropOrbitAfterTheTrim(Recording rec)
        {
            rec.OrbitSegments.Clear();
        }

        [Fact]
        public void OrdinaryLeaf_RouteFed_RefundedDeliveryTakenOut_LaterCrossingLeftToTheLiveRun()
        {
            // The station's own recording ends at 1700 holding the 1300 delivery; a rewind to
            // 1200 refunds it, and the replayed crossing is blocked while the station is a
            // ghost. The 1800 crossing came after the capture: the snapshot never held it, so
            // the copy keeps it out of the adjustment and the route delivers it live, once.
            InstallKerbin();
            Recording leaf = CommitStationLeaf();
            RouteStore.AddRoute(StationRoute("route-a"));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-0", 1100, "LiquidFuel", 100));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-2", 1800, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            RetiredRouteCargoRow row = Assert.Single(RetiredRouteCargoStore.Rows);
            Assert.Equal(1300.0, row.UT);
            RetiredRouteCargoTipTag tag = Assert.Single(row.Tips);
            Assert.Equal(LeafTreeId, tag.TreeId);
            Assert.Equal("station-leaf", tag.RecordingId);
            Assert.Equal(CaptureUT, tag.CaptureUT);

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(leaf, 1750.0, "leaf-spawn");

            Assert.NotNull(copy);
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
            Assert.Equal(50.0, LiquidFuelOf(copy, StationPartB), 6);
            Assert.Equal(300.0, LiquidFuelOf(leaf.VesselSnapshot, StationPartA), 6);
            Assert.Contains(logLines, l => l.Contains("[ChainTipCargo]")
                && l.Contains("leaf-spawn")
                && l.Contains("kind=leaf")
                && l.Contains("removed=100"));
        }

        [Fact]
        public void TrimmedTail_CrossingInTheTrimmedTail_IsTakenOutOnce_TheLiveRerunDeliversIt()
        {
            // The optimizer trims the leaf's coasting tail from 1700 to about 1400 and keeps
            // the snapshot captured at 1700, which holds the 1300 and the 1600 deliveries. After
            // a rewind to 1200 the station spawns early, at the trimmed end, and the route then
            // delivers the 1600 crossing again, live: the spawn copy must lack both, or the
            // 1600 cargo arrives twice.
            InstallKerbin();
            Recording leaf = CommitStationLeaf();
            MakeTrimmable(leaf, interestingUT: 1400.0, step: 25.0);
            RecordingStore.AddRecordingWithTreeForTesting(leaf);
            RouteStore.AddRoute(StationRoute("route-a"));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-2", 1600, "LiquidFuel", 100));

            RecordingStore.RunOptimizationPass();
            Assert.True(leaf.EndUT < 1450.0, "the tail was not trimmed: endUT=" + leaf.EndUT.ToString("R", CultureInfo.InvariantCulture));
            double spawnUT = leaf.EndUT;
            DropOrbitAfterTheTrim(leaf);

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            Assert.Equal(new[] { 1300.0, 1600.0 }, RetiredRouteCargoStore.Rows.Select(r => r.UT).OrderBy(u => u).ToArray());
            Assert.All(RetiredRouteCargoStore.Rows, r => Assert.Equal(CaptureUT, Assert.Single(r.Tips).CaptureUT));

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(leaf, spawnUT, "trimmed-leaf-spawn");

            Assert.NotNull(copy);
            Assert.Equal(100.0, LiquidFuelOf(copy, StationPartA), 6);

            // The route performs the 1600 crossing again into the spawned station (paid once,
            // delivered once). Were the spawn to come after that replay, the copy would keep it.
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1600.3, "LiquidFuel", 100));
            ConfigNode afterReplay = VesselSpawner.BuildValidatedRespawnSnapshot(leaf, 1650.0, "after-replay");
            Assert.Equal(200.0, LiquidFuelOf(afterReplay, StationPartA), 6);
        }

        [Fact]
        public void TrimmedChainTip_CrossingInTheTrimmedTail_IsTakenOut()
        {
            // The same capture rule for a chain tip: the station half (1600..1700) is trimmed
            // to about 1630, and its snapshot (captured at 1700) holds the 1650 delivery.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            MakeTrimmable(tip, interestingUT: 1620.0, step: 10.0);
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            SeedRouteHistory();
            Ledger.AddAction(DeliveredAction("route-a", "cycle-3", 1650, "LiquidFuel", 100));

            RecordingStore.RunOptimizationPass();
            Assert.True(tip.EndUT < 1645.0, "the tail was not trimmed: endUT=" + tip.EndUT.ToString("R", CultureInfo.InvariantCulture));
            DropOrbitAfterTheTrim(tip);

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, tip.EndUT, "trimmed-tip-spawn");

            Assert.NotNull(copy);
            Assert.Equal(100.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void OrdinaryLeaf_NotARouteEndpoint_IsLeftAsItIs()
        {
            InstallKerbin();
            Recording other = CommitStationLeaf(pid: 888u);
            RouteStore.AddRoute(StationRoute("route-a"));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(other, 1750.0, "not-an-endpoint");

            Assert.Empty(RetiredRouteCargoStore.Rows);
            Assert.NotNull(copy);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void OrdinaryLeaf_SameCraftAnotherLaunch_IsLeftAsItIs()
        {
            // Same craft-baked pid, another launch guid: not the endpoint's vessel.
            InstallKerbin();
            Recording relaunch = CommitStationLeaf(recordingGuid: OtherGuid);
            RouteStore.AddRoute(StationRoute("route-a"));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(relaunch, 1750.0, "other-launch");

            Assert.Empty(RetiredRouteCargoStore.Rows);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void OrdinaryLeaf_OnlyABarePidInCommon_IsLeftAsItIs()
        {
            // Neither side knows a launch guid and no root part flightID is known: a bare pid
            // is never the identity of an ordinary leaf.
            InstallKerbin();
            Recording leaf = CommitStationLeaf(recordingGuid: null);
            RouteStore.AddRoute(StationRoute("route-a", guid: null));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(leaf, 1750.0, "bare-pid");

            Assert.Empty(RetiredRouteCargoStore.Rows);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void OrdinaryLeaf_RootPartFlightId_IdentifiesTheEndpoint_WhenTheGuidIsUnknown()
        {
            InstallKerbin();
            Recording leaf = CommitStationLeaf(recordingGuid: null, rootUid: StationRootUid);
            Route route = StationRoute("route-a", guid: null);
            route.Stops[0].Endpoint.RootPartUId = StationRootUid;
            RouteStore.AddRoute(route);
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(leaf, 1750.0, "root-part");

            RetiredRouteCargoRow row = Assert.Single(RetiredRouteCargoStore.Rows);
            Assert.Equal(StationRootUid, row.EndpointRootPartUId);
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void OrdinaryLeaf_CrossingTheCurrentTimelinePerformedAgain_IsLeftInTheCopy()
        {
            InstallKerbin();
            Recording leaf = CommitStationLeaf();
            RouteStore.AddRoute(StationRoute("route-a"));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            Ledger.AddAction(DeliveredAction("route-a", "cycle-5", 1301.0, "LiquidFuel", 100));

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(leaf, 1750.0, "leaf-replayed");

            Assert.NotNull(copy);
            Assert.Equal(300.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void OrdinaryLeaf_SecondRewind_ReplayFromTheLaterTimeline_IsNotTakenOutTwice()
        {
            InstallKerbin();
            Recording leaf = CommitStationLeaf();
            RouteStore.AddRoute(StationRoute("route-a"));
            Ledger.AddAction(DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100));
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);
            Ledger.AddAction(DeliveredAction("route-a", "cycle-4", 1300.4, "LiquidFuel", 100));

            Ledger.RetireFutureRouteActionsAtRewind(1250.0, out _);
            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(leaf, 1750.0, "leaf-second-rewind");

            Assert.NotNull(copy);
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
        }

        [Fact]
        public void ChainTipThatIsAlsoTheEndpointsOwnVessel_IsTaggedAndAdjustedOnce()
        {
            // The station half is both the chain tip and a final leaf whose own vessel is the
            // route stop: one tag, one adjustment, one log line.
            InstallKerbin();
            Recording tip = CommitDockUndockTree();
            SeedRouteHistory();
            Ledger.RetireFutureRouteActionsAtRewind(CutoffUT, out _);

            ConfigNode copy = VesselSpawner.BuildValidatedRespawnSnapshot(tip, 1750.0, "tip-once");

            AssertOnlyTheTipsDeliveryStashed();
            Assert.Equal(200.0, LiquidFuelOf(copy, StationPartA), 6);
            Assert.Single(logLines, l => l.Contains("[ChainTipCargo]") && l.Contains("tip-once")
                && l.Contains("removed=100"));
            Assert.Contains(logLines, l => l.Contains("tip-once") && l.Contains("kind=chain-tip"));
        }

        [Theory]
        [InlineData(777u, StationGuid, 0u, 777u, StationGuid, 0u, true)]          // guid known and equal
        [InlineData(777u, "5A7E1100-2B3C-4D5E-8F90-A1B2C3D4E5F6", 0u, 777u, StationGuid, 0u, true)] // normalized
        [InlineData(777u, StationGuid, 9u, 777u, OtherGuid, 9u, false)]           // guids differ: another launch
        [InlineData(777u, null, 9u, 777u, StationGuid, 9u, true)]                 // guid unknown, root part equal
        [InlineData(777u, StationGuid, 9u, 777u, null, 9u, true)]
        [InlineData(777u, null, 9u, 777u, null, 8u, false)]                      // root parts differ
        [InlineData(777u, null, 0u, 777u, null, 9u, false)]                      // bare pid
        [InlineData(777u, null, 9u, 777u, StationGuid, 0u, false)]               // no common evidence
        [InlineData(888u, StationGuid, 9u, 777u, StationGuid, 9u, false)]        // other pid
        [InlineData(0u, StationGuid, 9u, 0u, StationGuid, 9u, false)]            // no pid
        public void IsPositiveEndpointMatch_NeverABarePid(
            uint endpointPid, string endpointGuid, uint endpointRoot,
            uint vesselPid, string vesselGuid, uint vesselRoot, bool expected)
        {
            var vessel = new CargoVesselIdentity { Pid = vesselPid, LaunchGuid = vesselGuid, RootPartUId = vesselRoot };
            Assert.Equal(expected,
                ChainTipRouteCargo.IsPositiveEndpointMatch(endpointPid, endpointGuid, endpointRoot, vessel));
        }

        [Fact]
        public void LeafRowAddressing_UsesThePositiveRule_TheChainTipKeepsItsOwn()
        {
            RetiredRouteCargoRow unknownGuid = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100,
                endpointGuid: null);
            var leaf = new ChainTipCargoIdentity { IsChainTip = false };
            leaf.PositiveVessels.Add(new CargoVesselIdentity { Pid = StationPid, LaunchGuid = StationGuid });

            Assert.False(ChainTipRouteCargo.RowAddressesTip(unknownGuid, leaf));
            Assert.True(ChainTipRouteCargo.RowAddressesTip(unknownGuid, StationTip()));
            Assert.True(ChainTipRouteCargo.RowAddressesTip(
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100), leaf));
        }

        [Fact]
        public void SnapshotCaptureRegistry_KeepsTheLatestCapture_AndRoundTripsWithTheRootPart()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-endspawncargo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string path = Path.Combine(tempDir, "ledger.pgld");

            Assert.True(RetiredRouteCargoStore.NoteSnapshotCapture(LeafTreeId, "station-leaf", "fp-1", 1700.0));
            Assert.True(RetiredRouteCargoStore.NoteSnapshotCapture(LeafTreeId, "station-leaf", "fp-1", 1650.0));
            Assert.False(RetiredRouteCargoStore.NoteSnapshotCapture(LeafTreeId, "station-leaf", null, 1650.0));
            Assert.Equal(1700.0, RetiredRouteCargoStore.CaptureUTOf(LeafTreeId, "fp-1"));
            Assert.True(double.IsNaN(RetiredRouteCargoStore.CaptureUTOf(LeafTreeId, "fp-2")));
            RetiredRouteCargoRow row = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100);
            row.EndpointRootPartUId = StationRootUid;
            RetiredRouteCargoStore.Merge(new[] { row }, out _);

            Assert.True(Ledger.SaveToFile(path));
            RetiredRouteCargoStore.ResetForTesting();
            Assert.True(Ledger.LoadFromFile(path));

            Assert.Equal(1700.0, RetiredRouteCargoStore.CaptureUTOf(LeafTreeId, "fp-1"));
            Assert.Single(RetiredRouteCargoStore.Captures);
            Assert.Equal(StationRootUid, Assert.Single(RetiredRouteCargoStore.Rows).EndpointRootPartUId);
            Assert.True(RetiredRouteCargoStore.HasCaptureAfter(LeafTreeId, 1699.0));
            Assert.False(RetiredRouteCargoStore.HasCaptureAfter(LeafTreeId, 1700.0));
        }

        [Fact]
        public void BuildRow_TakesTheEndpointRootPart()
        {
            Route route = StationRoute("route-a");
            route.Stops[0].Endpoint.RootPartUId = StationRootUid;

            RetiredRouteCargoRow row = RetiredRouteCargoStore.BuildRow(
                DeliveredAction("route-a", "cycle-1", 1300, "LiquidFuel", 100), CutoffUT, route);

            Assert.Equal(StationRootUid, row.EndpointRootPartUId);
        }

        /// <summary>
        /// Every production call that materializes a vessel from a ConfigNode, pinned per file:
        /// a new spawn path must take its copy through <c>ChainTipRouteCargo.ApplyToSpawnCopy</c>
        /// (the shared hook) or be a restore of a vessel that was live a moment ago.
        /// ChainTipStaleVessel (1): restores a removed pre-claim vessel. ParsekKSC (2): the
        /// Space Center end spawn, from its adjusted working copy. VesselGhoster (3): the
        /// chain tip's adjusted copy (2) and the restore after a failed despawn (1).
        /// VesselSpawner (2): the flight / Tracking Station / deferred-queue end spawn and
        /// RespawnValidatedRecording, both from BuildValidatedRespawnSnapshot(Recording).
        /// </summary>
        [Fact]
        public void EveryVesselMaterializer_IsAKnownSpawnCopyOrRestoreSite()
        {
            string root = FindRepoRoot();
            string sourceDir = Path.Combine(root, "Source", "Parsek");
            var expected = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "ChainTipStaleVessel.cs", 1 },
                { "ParsekKSC.cs", 2 },
                { "VesselGhoster.cs", 3 },
                { "VesselSpawner.cs", 2 },
            };
            var call = new Regex(@"\b(RespawnVessel|SpawnAtPosition)\s*\(");
            var declaration = new Regex(@"\bstatic\s+uint\s+(RespawnVessel|SpawnAtPosition)\s*\(");
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            string inGameTests = Path.Combine(sourceDir, "InGameTests");
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.StartsWith(inGameTests, StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = ParsekDialogNamePrefixSourceGateTests.StripComments(
                    File.ReadAllText(file).Replace("\r\n", "\n"));
                int count = call.Matches(text).Count - declaration.Matches(text).Count;
                if (count > 0)
                    found[Path.GetFileName(file)] = count;
            }

            Assert.Equal(
                expected.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray(),
                found.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
        }

        #endregion
    }
}
