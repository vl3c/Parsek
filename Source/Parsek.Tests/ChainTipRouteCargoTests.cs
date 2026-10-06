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

        private static ChainTipCargoIdentity StationTip(bool withParts = true)
        {
            var tip = new ChainTipCargoIdentity
            {
                TipRecordingId = "station-tip",
                TipTreeId = TreeId,
                CaptureUT = CaptureUT,
                EndpointPartIds = withParts ? new HashSet<uint> { StationPartA, StationPartB } : null
            };
            tip.Vessels.Add(new KeyValuePair<uint, string>(StationPid, StationGuid));
            return tip;
        }

        private static RetiredRouteCargoRow Row(
            GameActionType type, double ut, string res, double amount,
            string routeId = "route-a", string cycle = null, uint endpointPid = StationPid,
            string endpointGuid = StationGuid, uint actualPid = 0u, double cutoff = CutoffUT,
            string treeId = TreeId)
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
            if (treeId != null)
                row.TipTreeIds.Add(treeId);
            return row;
        }

        private static ChainTipCargoAdjustment Compute(
            List<SnapshotTank> tanks, params RetiredRouteCargoRow[] rows)
        {
            return ChainTipRouteCargo.ComputeAdjustment(tanks, rows, StationTip(), new HashSet<string>());
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
        public void RowTaggedForAnotherTreesTip_IsUntouched()
        {
            // A crossing retired before this tip's mission was committed is not in its snapshot.
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };

            ChainTipCargoAdjustment adj = Compute(tanks,
                Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, treeId: "tree-other"));

            Assert.Equal(300.0, adj.Amounts[0], 6);
            Assert.Equal(1, adj.SkippedOtherTree);
        }

        [Fact]
        public void CrossingTheCurrentTimelinePerformedAgain_IsUntouched()
        {
            var tanks = new List<SnapshotTank> { Tank(StationPartA, "LiquidFuel", 300, 400) };
            RetiredRouteCargoRow row = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100);

            ChainTipCargoAdjustment adj = ChainTipRouteCargo.ComputeAdjustment(
                tanks, new[] { row }, StationTip(), new HashSet<string> { row.CrossingKey });

            Assert.Equal(300.0, adj.Amounts[0], 6);
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
        public void TagForChainTips_KeepsOnlyRowsATipCarries()
        {
            var inWindow = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, treeId: null);
            var afterCapture = Row(GameActionType.RouteCargoDelivered, 1800, "LiquidFuel", 100, treeId: null);
            var otherVessel = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100,
                routeId: "route-b", endpointPid: 999u, endpointGuid: OtherGuid, treeId: null);

            List<RetiredRouteCargoRow> kept = ChainTipRouteCargo.TagForChainTips(
                new[] { inWindow, afterCapture, otherVessel }, new[] { StationTip() });

            RetiredRouteCargoRow only = Assert.Single(kept);
            Assert.Same(inWindow, only);
            Assert.Equal(new List<string> { TreeId }, only.TipTreeIds);
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

        [Fact]
        public void Merge_SameCrossingRetiredAgain_ReplacesTheEntry()
        {
            RetiredRouteCargoRow first = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 100, cycle: "cycle-3");
            RetiredRouteCargoRow again = Row(GameActionType.RouteCargoDelivered, 1300, "LiquidFuel", 90,
                cycle: "cycle-3", treeId: "tree-later");
            RetiredRouteCargoRow other = Row(GameActionType.RouteCargoDelivered, 1400, "LiquidFuel", 100, cycle: "cycle-4");

            Assert.Equal(1, RetiredRouteCargoStore.Merge(new[] { first }, out int replaced0));
            Assert.Equal(0, replaced0);
            Assert.Equal(1, RetiredRouteCargoStore.Merge(new[] { again, other }, out int replaced1));
            Assert.Equal(1, replaced1);

            Assert.Equal(2, RetiredRouteCargoStore.Rows.Count);
            RetiredRouteCargoRow merged = RetiredRouteCargoStore.Rows.Single(r => r.CycleId == "cycle-3");
            Assert.Equal(90.0, merged.Resources["LiquidFuel"], 6);
            // The first entry's tree still carries the crossing in its snapshot.
            Assert.Equal(new[] { "tree-later", TreeId }, merged.TipTreeIds.ToArray());
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
            Assert.Equal(new List<string> { TreeId }, back.TipTreeIds);
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
            Assert.Equal(new List<string> { TreeId }, row.TipTreeIds);
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
    }
}
