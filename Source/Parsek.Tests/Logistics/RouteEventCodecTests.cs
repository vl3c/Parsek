using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The <see cref="RouteEvent"/> codec: every field round-trips through one <c>EVENT</c>
    /// node, every field is sparse, the type is written by name, and a node that cannot be a
    /// route event is refused with a reason instead of throwing.
    /// </summary>
    [Collection("Sequential")]
    public class RouteEventCodecTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteEventCodecTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static ConfigNode Write(RouteEvent e)
        {
            var parent = new ConfigNode("ROOT");
            e.SerializeInto(parent);
            ConfigNode node = parent.GetNode(RouteEvent.NodeName);
            Assert.NotNull(node);
            return node;
        }

        private static RouteEvent RoundTrip(RouteEvent e)
        {
            RouteEvent result = RouteEvent.DeserializeFrom(Write(e), out string reason);
            Assert.Null(reason);
            Assert.NotNull(result);
            return result;
        }

        private static InventoryPayloadItem MakePickupPayload(
            string hash, string partName, string variant, int quantity)
        {
            var storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("variantName", variant);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));
            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = partName,
                VariantName = variant,
                Quantity = quantity,
                SlotsTaken = 1,
                StoredPartSnapshot = storedPart,
            };
        }

        [Fact]
        public void Dispatched_RoundTrips_TypeByName()
        {
            var original = new RouteEvent
            {
                UT = 50000.0,
                Type = RouteEventType.RouteDispatched,
                RouteId = "route-1",
                RouteCycleId = "cyc-42",
                RouteStopIndex = 0,
            };

            ConfigNode node = Write(original);
            Assert.Equal("RouteDispatched", node.GetValue("type"));
            Assert.Equal("50000", node.GetValue("ut"));

            RouteEvent result = RoundTrip(original);
            Assert.Equal(RouteEventType.RouteDispatched, result.Type);
            Assert.Equal(50000.0, result.UT);
            Assert.Equal("route-1", result.RouteId);
            Assert.Equal("cyc-42", result.RouteCycleId);
            Assert.Equal(0, result.RouteStopIndex);
        }

        // The physical origin debit: actuals, requested-on-shortfall and the origin pid.
        [Fact]
        public void CargoDebited_RoundTrips_ActualsRequestedAndOriginPid()
        {
            var original = new RouteEvent
            {
                UT = 50500.0,
                Type = RouteEventType.RouteCargoDebited,
                Sequence = 1,
                RouteId = "route-phys",
                RouteCycleId = "cycle-4",
                RouteResourceManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 40.0 },
                    { "Oxidizer", 48.875 },
                },
                RouteRequestedResourceManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                RouteOriginVesselPid = 3149815921u,
            };

            RouteEvent result = RoundTrip(original);

            Assert.Equal(RouteEventType.RouteCargoDebited, result.Type);
            Assert.Equal(1, result.Sequence);
            Assert.Equal(2, result.RouteResourceManifest.Count);
            Assert.Equal(40.0, result.RouteResourceManifest["LiquidFuel"]);
            Assert.Equal(48.875, result.RouteResourceManifest["Oxidizer"]);
            Assert.Equal(100.0, result.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(3149815921u, result.RouteOriginVesselPid);
        }

        [Fact]
        public void CargoDelivered_RoundTrips_PartialFillRequested()
        {
            var original = new RouteEvent
            {
                UT = 51000.0,
                Type = RouteEventType.RouteCargoDelivered,
                RouteId = "route-3",
                RouteCycleId = "cyc-7",
                RouteStopIndex = 2,
                RouteResourceManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                RouteRequestedResourceManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 150.0 },
                    { "Oxidizer", 183.0 },
                },
            };

            RouteEvent result = RoundTrip(original);

            Assert.Equal(2, result.RouteStopIndex);
            Assert.Equal(100.0, result.RouteResourceManifest["LiquidFuel"]);
            Assert.Equal(150.0, result.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(183.0, result.RouteRequestedResourceManifest["Oxidizer"]);
        }

        [Theory]
        [InlineData(RouteEventType.RoutePaused, "AutoPause:SourceChanged")]
        [InlineData(RouteEventType.RouteResumed, "player-activate")]
        [InlineData(RouteEventType.RouteEndpointLost, "EndpointLost:OrbitalNoFallback")]
        public void LifecycleEvents_RoundTripReason(RouteEventType type, string reason)
        {
            RouteEvent result = RoundTrip(new RouteEvent
            {
                UT = 52000.0,
                Type = type,
                RouteId = "route-4",
                RouteEndpointReason = reason,
            });

            Assert.Equal(type, result.Type);
            Assert.Equal("route-4", result.RouteId);
            Assert.Equal(reason, result.RouteEndpointReason);
            Assert.Null(result.RouteCycleId);
            Assert.Equal(-1, result.RouteStopIndex);
        }

        // The sparse Send Once stamp round-trips when true and writes no key when false.
        [Fact]
        public void Dispatched_SendOnce_RoundTripsAndStaysSparse()
        {
            Assert.True(RoundTrip(new RouteEvent
            {
                UT = 1.0, Type = RouteEventType.RouteDispatched, RouteId = "r", RouteSendOnce = true,
            }).RouteSendOnce);

            var auto = new RouteEvent { UT = 2.0, Type = RouteEventType.RouteDispatched, RouteId = "r" };
            Assert.Null(Write(auto).GetValue("routeSendOnce"));
            Assert.False(RoundTrip(auto).RouteSendOnce);
        }

        // RouteHeld: the kind by NAME, the detail token and the shortfall all round-trip.
        [Fact]
        public void Held_RoundTripsKindDetailAndShortfall()
        {
            var original = new RouteEvent
            {
                UT = 1150.0,
                Type = RouteEventType.RouteHeld,
                RouteId = "route-held",
                RouteCycleId = "cycle-3",
                RouteHoldKind = RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                RouteEndpointReason = "source:42:Depot B:LiquidFuel",
                RouteHoldShortfall = 108.79999999999706,
            };

            ConfigNode node = Write(original);
            Assert.Equal("RouteHeld", node.GetValue("type"));
            Assert.Equal("OriginLacksCargo", node.GetValue("routeHoldKind"));
            Assert.Equal("108.79999999999706", node.GetValue("routeHoldShortfall"));

            RouteEvent result = RoundTrip(original);
            Assert.Equal(original.RouteHoldKind, result.RouteHoldKind);
            Assert.Equal("source:42:Depot B:LiquidFuel", result.RouteEndpointReason);
            Assert.Equal(108.79999999999706, result.RouteHoldShortfall);
            Assert.Equal("cycle-3", result.RouteCycleId);
        }

        // A zero shortfall writes no key; an unknown kind name reads back None with a warn;
        // the shortfall stays InvariantCulture under a comma locale.
        [Fact]
        public void Held_SparseKeys_UnknownKind_AndDeDe()
        {
            var plain = new RouteEvent
            {
                UT = 1.0,
                Type = RouteEventType.RouteHeld,
                RouteId = "r",
                RouteHoldKind = RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull,
            };
            ConfigNode node = Write(plain);
            Assert.Null(node.GetValue("routeHoldShortfall"));
            Assert.Null(node.GetValue("routeEndpointReason"));

            node.SetValue("routeHoldKind", "SomeFutureKind");
            RouteEvent unknown = RouteEvent.DeserializeFrom(node, out _);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, unknown.RouteHoldKind);
            Assert.Contains(logLines, l => l.Contains("[RouteEventLog]")
                && l.Contains("unknown routeHoldKind 'SomeFutureKind'"));

            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var funds = new RouteEvent
                {
                    UT = 2.5, Type = RouteEventType.RouteHeld, RouteId = "r",
                    RouteHoldKind = RouteDispatchEvaluator.EligibilityFailureKind.FundsShort,
                    RouteHoldShortfall = 1234.5,
                    RouteResourceManifest = new Dictionary<string, double> { { "Ore", 0.25 } },
                };
                ConfigNode n2 = Write(funds);
                Assert.Equal("1234.5", n2.GetValue("routeHoldShortfall"));
                Assert.Equal("2.5", n2.GetValue("ut"));
                Assert.Equal("Ore|0.25", n2.GetValue("resource"));
                RouteEvent back = RouteEvent.DeserializeFrom(n2, out _);
                Assert.Equal(1234.5, back.RouteHoldShortfall);
                Assert.Equal(0.25, back.RouteResourceManifest["Ore"]);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        // The pickup event carries resource and inventory manifests and the endpoint pid.
        [Fact]
        public void CargoPickedUp_RoundTrips_ManifestsInventoryAndEndpointPid()
        {
            var original = new RouteEvent
            {
                UT = 56000.0,
                Type = RouteEventType.RouteCargoPickedUp,
                RouteId = "route-pickup",
                RouteCycleId = "cycle-2",
                RouteStopIndex = 0,
                Sequence = 2,
                RouteResourceManifest = new Dictionary<string, double> { { "Ore", 40.0 } },
                RouteRequestedResourceManifest = new Dictionary<string, double> { { "Ore", 100.0 } },
                RouteOriginVesselPid = 3149815921u,
                RouteInventoryManifest = new List<InventoryPayloadItem>
                {
                    MakePickupPayload("ore-container-hash", "smallCargoContainer", "white", 1),
                },
                RouteRequestedInventoryManifest = new List<InventoryPayloadItem>
                {
                    MakePickupPayload("ore-container-hash", "smallCargoContainer", "white", 2),
                },
            };

            RouteEvent result = RoundTrip(original);

            Assert.Equal(RouteEventType.RouteCargoPickedUp, result.Type);
            Assert.Equal(0, result.RouteStopIndex);
            Assert.Equal(2, result.Sequence);
            Assert.Equal(40.0, result.RouteResourceManifest["Ore"]);
            Assert.Equal(100.0, result.RouteRequestedResourceManifest["Ore"]);
            Assert.Equal(3149815921u, result.RouteOriginVesselPid);

            // A loaded item carrying a snapshot has its kind key recomputed from it.
            Assert.Single(result.RouteInventoryManifest);
            InventoryPayloadItem item = result.RouteInventoryManifest[0];
            Assert.Equal(VesselSnapshotOps.ComputeInventoryPayloadKindKey(item.StoredPartSnapshot), item.IdentityHash);
            Assert.Equal("smallCargoContainer", item.PartName);
            Assert.Equal("white", item.VariantName);
            Assert.Equal(1, item.Quantity);
            Assert.Equal("smallCargoContainer", item.StoredPartSnapshot.GetValue("partName"));
            Assert.Equal(2, result.RouteRequestedInventoryManifest[0].Quantity);
        }

        // Every field is sparse: a lean event writes only its type, UT and route id, and
        // reads back with every other field at its default.
        [Fact]
        public void LeanEvent_WritesOnlyTypeUtAndRouteId()
        {
            var lean = new RouteEvent { UT = 60000.0, Type = RouteEventType.RouteCargoDebited, RouteId = "route-lean" };

            ConfigNode node = Write(lean);
            Assert.Equal(3, node.values.Count);
            Assert.Equal(0, node.nodes.Count);
            Assert.Equal("route-lean", node.GetValue("routeId"));

            RouteEvent result = RoundTrip(lean);
            Assert.Null(result.RouteCycleId);
            Assert.Equal(-1, result.RouteStopIndex);
            Assert.Equal(0, result.Sequence);
            Assert.Null(result.RouteResourceManifest);
            Assert.Null(result.RouteRequestedResourceManifest);
            Assert.Null(result.RouteInventoryManifest);
            Assert.Null(result.RouteRequestedInventoryManifest);
            Assert.Equal(0u, result.RouteOriginVesselPid);
            Assert.Null(result.RouteEndpointReason);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, result.RouteHoldKind);
            Assert.Equal(0.0, result.RouteHoldShortfall);
        }

        // A node that cannot be a route event is refused with a reason, never thrown on.
        [Fact]
        public void Deserialize_RefusesUnknownTypeBadUtAndMissingRouteId()
        {
            ConfigNode Node(string type, string ut, string routeId)
            {
                var n = new ConfigNode(RouteEvent.NodeName);
                if (type != null) n.AddValue("type", type);
                if (ut != null) n.AddValue("ut", ut);
                if (routeId != null) n.AddValue("routeId", routeId);
                return n;
            }

            string reason;
            Assert.Null(RouteEvent.DeserializeFrom(null, out reason));
            Assert.Equal("null-node", reason);
            Assert.Null(RouteEvent.DeserializeFrom(Node("RouteFutureKind", "1", "r"), out reason));
            Assert.Equal("unknown-type:RouteFutureKind", reason);
            // The numeric ledger id is not a route event type name.
            Assert.Null(RouteEvent.DeserializeFrom(Node("23", "1", "r"), out reason));
            Assert.Equal("unknown-type:23", reason);
            Assert.Null(RouteEvent.DeserializeFrom(Node(null, "1", "r"), out reason));
            Assert.Equal("unknown-type:(missing)", reason);
            Assert.Null(RouteEvent.DeserializeFrom(Node("RoutePaused", "abc", "r"), out reason));
            Assert.Equal("bad-ut", reason);
            Assert.Null(RouteEvent.DeserializeFrom(Node("RoutePaused", "NaN", "r"), out reason));
            Assert.Equal("bad-ut", reason);
            Assert.Null(RouteEvent.DeserializeFrom(Node("RoutePaused", "1", null), out reason));
            Assert.Equal("no-route-id", reason);
            Assert.NotNull(RouteEvent.DeserializeFrom(Node("RoutePaused", "1", "r"), out reason));
            Assert.Null(reason);
        }

        // A corrupted manifest entry is warn-and-skip for THAT entry: the valid one still loads.
        [Fact]
        public void Deserialize_MalformedManifestEntries_WarnAndSkipWithoutLosingValidOnes()
        {
            var node = new ConfigNode(RouteEvent.NodeName);
            node.AddValue("type", "RouteCargoDebited");
            node.AddValue("ut", "50000");
            node.AddValue("routeId", "r");
            node.AddValue("resource", "LiquidFuel");    // no separator
            node.AddValue("resource", "|5");            // empty name
            node.AddValue("resource", "Oxidizer|abc");  // unparseable amount
            node.AddValue("resource", "Ore|25.5");      // the one valid entry

            RouteEvent loaded = RouteEvent.DeserializeFrom(node, out _);

            Assert.Single(loaded.RouteResourceManifest);
            Assert.Equal(25.5, loaded.RouteResourceManifest["Ore"]);
            Assert.Contains(logLines, l => l.Contains("[RouteEventLog]") && l.Contains("malformed")
                && l.Contains("'resource'") && l.Contains("LiquidFuel"));
            Assert.Contains(logLines, l => l.Contains("[RouteEventLog]") && l.Contains("malformed")
                && l.Contains("'resource'") && l.Contains("|5"));
            Assert.Contains(logLines, l => l.Contains("[RouteEventLog]") && l.Contains("unparseable")
                && l.Contains("'resource'") && l.Contains("Oxidizer") && l.Contains("abc"));
        }

        [Fact]
        public void TryParseType_AcceptsOnlyDefinedNames()
        {
            foreach (RouteEventType t in Enum.GetValues(typeof(RouteEventType)))
            {
                Assert.True(RouteEvent.TryParseType(t.ToString(), out RouteEventType parsed));
                Assert.Equal(t, parsed);
            }
            Assert.False(RouteEvent.TryParseType("RouteRecoveryCredited", out _));
            Assert.False(RouteEvent.TryParseType("26", out _));
            Assert.False(RouteEvent.TryParseType("", out _));
            Assert.False(RouteEvent.TryParseType(null, out _));
        }

        // The route event types keep the ledger's old names and numbers (log lines that
        // print a type read as they always did), and each is a reserved ledger id.
        [Fact]
        public void EventTypes_MirrorTheReservedLedgerIds()
        {
            foreach (RouteEventType t in Enum.GetValues(typeof(RouteEventType)))
            {
                var ledgerType = (GameActionType)(int)t;
                Assert.Equal(t.ToString(), ledgerType.ToString());
                Assert.True(LedgerLoadMigration.IsReservedRouteActionType(ledgerType));
            }
        }
    }
}
