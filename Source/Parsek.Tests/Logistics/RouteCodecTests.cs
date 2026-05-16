using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    [Collection("Sequential")]
    public class RouteCodecTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteCodecTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixture helpers
        // -----------------------------------------------------------------

        private static ConfigNode BuildStoredPartSnapshot()
        {
            // STOREDPART with nested RESOURCE + MODULE — a realistic stock
            // shape used to assert verbatim preservation.
            var snapshot = new ConfigNode("STOREDPART");
            snapshot.AddValue("partName", "smallSolarPanel");
            snapshot.AddValue("variant", "white");
            ConfigNode resourceNode = snapshot.AddNode("RESOURCE");
            resourceNode.AddValue("name", "ElectricCharge");
            resourceNode.AddValue("amount", "50");
            resourceNode.AddValue("maxAmount", "50");
            ConfigNode moduleNode = snapshot.AddNode("MODULE");
            moduleNode.AddValue("name", "ModuleDeployableSolarPanel");
            moduleNode.AddValue("deployState", "EXTENDED");
            return snapshot;
        }

        private static InventoryPayloadItem BuildPayloadItem(string hash = "8F17")
        {
            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = "smallSolarPanel",
                VariantName = "white",
                Quantity = 2,
                SlotsTaken = 2,
                StoredResources = new Dictionary<string, ResourceAmount>
                {
                    {
                        "ElectricCharge",
                        new ResourceAmount { amount = 50.0, maxAmount = 50.0 }
                    }
                },
                StoredPartSnapshot = BuildStoredPartSnapshot()
            };
        }

        private static RouteEndpoint BuildKscOrigin()
        {
            return new RouteEndpoint
            {
                BodyName = "Kerbin",
                Latitude = -0.0972,
                Longitude = -74.5577,
                Altitude = 75.2,
                VesselPersistentId = 0,
                IsSurface = true
            };
        }

        private static RouteEndpoint BuildMunStopEndpoint()
        {
            return new RouteEndpoint
            {
                BodyName = "Mun",
                Latitude = 3.2001,
                Longitude = -45.1234,
                Altitude = 612.5,
                VesselPersistentId = 67890,
                IsSurface = true
            };
        }

        private static RouteSourceRef BuildSourceRef(string recId, string treeId, int order)
        {
            return new RouteSourceRef
            {
                RecordingId = recId,
                TreeId = treeId,
                TreeOrder = order,
                RecordingFormatVersion = 13,
                RecordingSchemaGeneration = 2,
                SidecarEpoch = 12,
                StartUT = 42654.0 + order * 1000.0,
                EndUT = 47000.0 + order * 1000.0,
                RouteProofHash = "93C2-" + order
            };
        }

        private static Route BuildFullyPopulatedRoute()
        {
            var stop = new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                SegmentIndexBefore = 1,
                DeliveryOffsetSeconds = 600.0,
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 150.0 },
                    { "Oxidizer", 183.3 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    BuildPayloadItem()
                }
            };

            return new RouteFixtureBuilder()
                .WithId("route-guid-1")
                .WithName("Mun Fuel Run")
                .WithStatus(RouteStatus.InTransit)
                .WithKscOrigin(true, 12500f)
                .WithSchedule(12345.6, 43200.0)
                .WithDispatchWindow(42654.0, 0.0, 258654.0)
                .WithCurrentCycleStartUT(255000.0)
                .WithCurrentSegmentIndex(0)
                .WithPendingDeliveryUT(258654.0 + 600.0, 0)
                .WithCycleCounters(5, 0)
                .WithOrigin(BuildKscOrigin())
                .WithRecordingId("rec-1")
                .WithRecordingId("rec-2")
                .WithSourceRef(BuildSourceRef("rec-1", "tree-1", 0))
                .WithSourceRef(BuildSourceRef("rec-2", "tree-1", 1))
                .WithStop(stop)
                .WithCostManifest(new Dictionary<string, double>
                {
                    { "LiquidFuel", 155.0 },
                    { "Oxidizer", 183.3 }
                })
                .WithInventoryCostManifest(new List<InventoryPayloadItem>
                {
                    BuildPayloadItem()
                })
                .Build();
        }

        // -----------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------

        // catches: any single field dropped from the codec.
        [Fact]
        public void RoundTrip_FullyPopulated()
        {
            Route original = BuildFullyPopulatedRoute();

            var node = new ConfigNode("ROUTE");
            original.SerializeInto(node);

            Route roundTripped = Route.DeserializeFrom(node);
            Assert.NotNull(roundTripped);

            // Identity
            Assert.Equal(original.Id, roundTripped.Id);
            Assert.Equal(original.Name, roundTripped.Name);
            Assert.Equal(original.RecordingIds, roundTripped.RecordingIds);

            // SourceRefs deep-equality (uses our Equals).
            Assert.Equal(original.SourceRefs.Count, roundTripped.SourceRefs.Count);
            for (int i = 0; i < original.SourceRefs.Count; i++)
                Assert.Equal(original.SourceRefs[i], roundTripped.SourceRefs[i]);

            // Endpoints + flags
            AssertEndpointsEqual(original.Origin, roundTripped.Origin);
            Assert.Equal(original.IsKscOrigin, roundTripped.IsKscOrigin);
            Assert.Equal(original.KscDispatchFundsCost, roundTripped.KscDispatchFundsCost);

            // Scheduling
            Assert.Equal(original.TransitDuration, roundTripped.TransitDuration);
            Assert.Equal(original.DispatchInterval, roundTripped.DispatchInterval);
            Assert.Equal(original.DispatchWindowEpochUT, roundTripped.DispatchWindowEpochUT);
            Assert.Equal(original.DispatchWindowPeriod, roundTripped.DispatchWindowPeriod);
            Assert.Equal(original.NextDispatchUT, roundTripped.NextDispatchUT);
            Assert.Equal(original.CurrentCycleStartUT, roundTripped.CurrentCycleStartUT);
            Assert.Equal(original.NextEligibilityCheckUT, roundTripped.NextEligibilityCheckUT);
            Assert.Equal(original.PendingDeliveryUT, roundTripped.PendingDeliveryUT);
            Assert.Equal(original.CurrentSegmentIndex, roundTripped.CurrentSegmentIndex);
            Assert.Equal(original.PendingStopIndex, roundTripped.PendingStopIndex);

            // Linking
            Assert.Equal(original.LinkedRouteId, roundTripped.LinkedRouteId);

            // Status
            Assert.Equal(original.Status, roundTripped.Status);
            Assert.Equal(original.PauseAfterCurrentCycle, roundTripped.PauseAfterCurrentCycle);
            Assert.Equal(original.CompletedCycles, roundTripped.CompletedCycles);
            Assert.Equal(original.SkippedCycles, roundTripped.SkippedCycles);

            // Stops
            Assert.Equal(original.Stops.Count, roundTripped.Stops.Count);
            for (int i = 0; i < original.Stops.Count; i++)
                AssertStopsEqual(original.Stops[i], roundTripped.Stops[i]);

            // Cost manifests
            AssertResourceManifestEqual(original.CostManifest, roundTripped.CostManifest);
            AssertInventoryListEqual(original.InventoryCostManifest, roundTripped.InventoryCostManifest);
        }

        // catches: codec writing noisy empties that bloat saves.
        [Fact]
        public void RoundTrip_Lean_NoSpuriousNodes()
        {
            // Minimum shape: Active status, single stop with NO inventory manifest,
            // all nullable scalars null, no linked route, no cost manifests.
            var leanStop = new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                // InventoryDeliveryManifest intentionally null.
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0
            };

            var route = new RouteFixtureBuilder()
                .WithId("lean-route")
                .WithName("Lean")
                .WithOrigin(BuildKscOrigin())
                .WithStop(leanStop)
                .Build();
            // Explicitly NOT calling WithCurrentCycleStartUT / NextEligibilityCheckUT /
            // PendingDeliveryUT — those stay null.

            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);

            // No optional UT scalars on the wire.
            Assert.False(node.HasValue("currentCycleStartUT"),
                "currentCycleStartUT must be omitted when null");
            Assert.False(node.HasValue("nextEligibilityCheckUT"),
                "nextEligibilityCheckUT must be omitted when null");
            Assert.False(node.HasValue("pendingDeliveryUT"),
                "pendingDeliveryUT must be omitted when null");

            // No empty INVENTORY_DELIVERY_MANIFEST under STOP.
            ConfigNode stopNode = node.GetNode(RouteCodec.StopNode);
            Assert.NotNull(stopNode);
            Assert.False(stopNode.HasNode(RouteCodec.InventoryDeliveryManifestNode),
                "INVENTORY_DELIVERY_MANIFEST must be omitted when empty/null");

            // No top-level cost manifests either.
            Assert.False(node.HasNode(RouteCodec.CostManifestNode),
                "COST_MANIFEST must be omitted when empty/null");
            Assert.False(node.HasNode(RouteCodec.InventoryCostManifestNode),
                "INVENTORY_COST_MANIFEST must be omitted when empty/null");
        }

        // catches: an accidental canonicalization that drifts payload identity hashes.
        [Fact]
        public void StoredPartSnapshot_PreservedVerbatim()
        {
            InventoryPayloadItem item = BuildPayloadItem();
            string before = item.StoredPartSnapshot.ToString();

            var stop = new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                InventoryDeliveryManifest = new List<InventoryPayloadItem> { item },
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0
            };

            var route = new RouteFixtureBuilder()
                .WithId("verbatim-route")
                .WithOrigin(BuildKscOrigin())
                .WithStop(stop)
                .Build();

            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            Route roundTripped = Route.DeserializeFrom(node);

            Assert.NotNull(roundTripped);
            Assert.Single(roundTripped.Stops);
            Assert.NotNull(roundTripped.Stops[0].InventoryDeliveryManifest);
            Assert.Single(roundTripped.Stops[0].InventoryDeliveryManifest);
            ConfigNode after = roundTripped.Stops[0].InventoryDeliveryManifest[0].StoredPartSnapshot;
            Assert.NotNull(after);
            Assert.Equal(before, after.ToString());
        }

        // catches: forward-compat regression / silent enum drop.
        [Fact]
        public void Load_UnknownStatusValue_MapsToActiveAndWarns()
        {
            // Hand-author a minimum-shape route node carrying an unknown status string.
            var node = new ConfigNode("ROUTE");
            node.AddValue("id", "weird-status-route");
            node.AddValue("status", "SomeFutureValue");
            // Required ORIGIN + at least one STOP so the route is otherwise valid.
            ConfigNode origin = node.AddNode(RouteCodec.OriginNode);
            origin.AddValue("bodyName", "Kerbin");
            origin.AddValue("latitude", "0");
            origin.AddValue("longitude", "0");
            origin.AddValue("altitude", "0");
            origin.AddValue("vesselPersistentId", "0");
            origin.AddValue("isSurface", "True");
            ConfigNode stop = node.AddNode(RouteCodec.StopNode);
            ConfigNode endpoint = stop.AddNode(RouteCodec.EndpointNode);
            endpoint.AddValue("bodyName", "Mun");
            endpoint.AddValue("latitude", "0");
            endpoint.AddValue("longitude", "0");
            endpoint.AddValue("altitude", "0");
            endpoint.AddValue("vesselPersistentId", "12345");
            endpoint.AddValue("isSurface", "True");
            stop.AddValue("connectionKind", "DockingPort");
            stop.AddValue("segmentIndexBefore", "0");
            stop.AddValue("deliveryOffsetSeconds", "0");

            Route route = Route.DeserializeFrom(node);

            Assert.NotNull(route);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RouteStore]") &&
                l.Contains("SomeFutureValue") &&
                l.Contains("weird-status-route"));
        }

        // catches: ghost route rows that can never dispatch.
        [Fact]
        public void Load_EmptyStops_ReturnsNullAndWarns()
        {
            var node = new ConfigNode("ROUTE");
            node.AddValue("id", "no-stops-route");
            node.AddValue("status", "Active");
            ConfigNode origin = node.AddNode(RouteCodec.OriginNode);
            origin.AddValue("bodyName", "Kerbin");
            origin.AddValue("latitude", "0");
            origin.AddValue("longitude", "0");
            origin.AddValue("altitude", "0");
            origin.AddValue("vesselPersistentId", "0");
            origin.AddValue("isSurface", "True");
            // No STOP children.

            Route route = Route.DeserializeFrom(node);

            Assert.Null(route);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RouteStore]") &&
                l.Contains("no-stops-route") &&
                l.Contains("STOP"));
        }

        // catches: partially-loaded routes that look valid but skip integrity checks.
        [Fact]
        public void Load_MalformedSourceRef_RoutewideReject()
        {
            // Build a valid route then surgically drop recordingId from the
            // second SOURCE entry — the whole route must be rejected.
            Route valid = BuildFullyPopulatedRoute();
            var node = new ConfigNode("ROUTE");
            valid.SerializeInto(node);

            ConfigNode refsNode = node.GetNode(RouteCodec.SourceRefsNode);
            Assert.NotNull(refsNode);
            ConfigNode[] srcNodes = refsNode.GetNodes(RouteCodec.SourceChildNode);
            Assert.Equal(2, srcNodes.Length);
            srcNodes[1].RemoveValue("recordingId");

            Route reloaded = Route.DeserializeFrom(node);

            Assert.Null(reloaded);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[RouteStore]") &&
                l.Contains("route-guid-1") &&
                l.Contains("SOURCE"));
        }

        // -----------------------------------------------------------------
        // Deep-compare helpers (test-private)
        // -----------------------------------------------------------------

        private static void AssertEndpointsEqual(RouteEndpoint a, RouteEndpoint b)
        {
            Assert.Equal(a.BodyName, b.BodyName);
            Assert.Equal(a.Latitude, b.Latitude);
            Assert.Equal(a.Longitude, b.Longitude);
            Assert.Equal(a.Altitude, b.Altitude);
            Assert.Equal(a.VesselPersistentId, b.VesselPersistentId);
            Assert.Equal(a.IsSurface, b.IsSurface);
        }

        private static void AssertStopsEqual(RouteStop a, RouteStop b)
        {
            AssertEndpointsEqual(a.Endpoint, b.Endpoint);
            Assert.Equal(a.ConnectionKind, b.ConnectionKind);
            Assert.Equal(a.SegmentIndexBefore, b.SegmentIndexBefore);
            Assert.Equal(a.DeliveryOffsetSeconds, b.DeliveryOffsetSeconds);
            AssertResourceManifestEqual(a.DeliveryManifest, b.DeliveryManifest);
            AssertInventoryListEqual(a.InventoryDeliveryManifest, b.InventoryDeliveryManifest);
        }

        private static void AssertResourceManifestEqual(
            Dictionary<string, double> a,
            Dictionary<string, double> b)
        {
            if (a == null && b == null) return;
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a.Count, b.Count);
            foreach (var kvp in a)
            {
                Assert.True(b.ContainsKey(kvp.Key), "missing key: " + kvp.Key);
                Assert.Equal(kvp.Value, b[kvp.Key]);
            }
        }

        private static void AssertInventoryListEqual(
            List<InventoryPayloadItem> a,
            List<InventoryPayloadItem> b)
        {
            if (a == null && b == null) return;
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].IdentityHash, b[i].IdentityHash);
                Assert.Equal(a[i].PartName, b[i].PartName);
                Assert.Equal(a[i].VariantName, b[i].VariantName);
                Assert.Equal(a[i].Quantity, b[i].Quantity);
                Assert.Equal(a[i].SlotsTaken, b[i].SlotsTaken);
                if (a[i].StoredResources != null || b[i].StoredResources != null)
                {
                    Assert.NotNull(a[i].StoredResources);
                    Assert.NotNull(b[i].StoredResources);
                    Assert.Equal(a[i].StoredResources.Count, b[i].StoredResources.Count);
                    foreach (var kvp in a[i].StoredResources)
                    {
                        Assert.True(b[i].StoredResources.ContainsKey(kvp.Key));
                        Assert.Equal(kvp.Value.amount, b[i].StoredResources[kvp.Key].amount);
                        Assert.Equal(kvp.Value.maxAmount, b[i].StoredResources[kvp.Key].maxAmount);
                    }
                }
                if (a[i].StoredPartSnapshot != null || b[i].StoredPartSnapshot != null)
                {
                    Assert.NotNull(a[i].StoredPartSnapshot);
                    Assert.NotNull(b[i].StoredPartSnapshot);
                    Assert.Equal(a[i].StoredPartSnapshot.ToString(), b[i].StoredPartSnapshot.ToString());
                }
            }
        }
    }
}
