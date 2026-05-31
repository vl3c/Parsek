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
                .WithCadenceMultiplier(3)
                .WithBackingMissionTreeId("tree-1")
                .WithExcludedIntervalKey("leg-post-undock-survivor")
                .WithExcludedIntervalKey("leg-post-undock-survivor/seg1")
                .WithExcludedIntervalKey("leg-offshoot")
                .WithDockBinding(255600.0, "rec-2")
                .WithLoopAnchorUT(255000.0)
                .WithLastObservedLoopCycleIndex(7)
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
            Assert.Equal(original.CadenceMultiplier, roundTripped.CadenceMultiplier);
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

            // Backing-mission definition (Phase 1)
            Assert.Equal(original.BackingMissionTreeId, roundTripped.BackingMissionTreeId);
            Assert.Equal(original.RecordedDockUT, roundTripped.RecordedDockUT);
            Assert.Equal(original.DockMemberRecordingId, roundTripped.DockMemberRecordingId);
            Assert.Equal(original.LoopAnchorUT, roundTripped.LoopAnchorUT);
            Assert.Equal(original.LastObservedLoopCycleIndex, roundTripped.LastObservedLoopCycleIndex);
            Assert.True(original.IsLoopRoute);
            Assert.True(roundTripped.IsLoopRoute);
            Assert.Equal(
                new SortedSet<string>(original.ExcludedIntervalKeys),
                new SortedSet<string>(roundTripped.ExcludedIntervalKeys));
        }

        // catches: EXCLUDED_INTERVALS node written for an empty set (save bloat)
        // AND an empty-definition route mistakenly rejected on load.
        [Fact]
        public void RoundTrip_EmptyBackingMissionDefinition_NoNodeAndNoReject()
        {
            // A route with NO backing-mission definition (all defaults).
            var leanStop = new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0
            };
            var route = new RouteFixtureBuilder()
                .WithId("no-backing-route")
                .WithName("No Backing")
                .WithOrigin(BuildKscOrigin())
                .WithStop(leanStop)
                .Build();

            // Sanity: nothing populated the backing-mission fields.
            Assert.Empty(route.ExcludedIntervalKeys);
            Assert.False(route.IsLoopRoute);

            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);

            // Empty set writes NO EXCLUDED_INTERVALS node.
            Assert.False(node.HasNode(RouteCodec.ExcludedIntervalsNode),
                "EXCLUDED_INTERVALS must be omitted when the set is empty");
            // No backing-mission scalars that are themselves optional / sparse.
            Assert.False(node.HasValue("backingMissionTreeId"),
                "backingMissionTreeId must be omitted when null");
            Assert.False(node.HasValue("dockMemberRecordingId"),
                "dockMemberRecordingId must be omitted when null");
            Assert.False(node.HasValue("lastObservedLoopCycleIndex"),
                "lastObservedLoopCycleIndex must be omitted when -1");

            // The empty definition does NOT reject the route, and loads an empty set.
            Route roundTripped = Route.DeserializeFrom(node);
            Assert.NotNull(roundTripped);
            Assert.Empty(roundTripped.ExcludedIntervalKeys);
            Assert.Null(roundTripped.BackingMissionTreeId);
            Assert.Null(roundTripped.DockMemberRecordingId);
            Assert.Equal(-1.0, roundTripped.RecordedDockUT);
            Assert.Equal(-1.0, roundTripped.LoopAnchorUT);
            Assert.Equal(-1L, roundTripped.LastObservedLoopCycleIndex);
            Assert.False(roundTripped.IsLoopRoute);
        }

        // catches: a sparse -1 cycle index written to the wire / read as non-default.
        [Fact]
        public void RoundTrip_SparseCycleIndex_DefaultMinusOne()
        {
            var leanStop = new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0
            };
            var route = new RouteFixtureBuilder()
                .WithId("sparse-cycle-route")
                .WithOrigin(BuildKscOrigin())
                .WithStop(leanStop)
                .WithBackingMissionTreeId("tree-7")
                .WithLastObservedLoopCycleIndex(-1)
                .Build();

            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            Assert.False(node.HasValue("lastObservedLoopCycleIndex"),
                "lastObservedLoopCycleIndex must be omitted when -1");

            Route roundTripped = Route.DeserializeFrom(node);
            Assert.NotNull(roundTripped);
            Assert.Equal(-1L, roundTripped.LastObservedLoopCycleIndex);
            // A populated tree id still round-trips and flips IsLoopRoute.
            Assert.Equal("tree-7", roundTripped.BackingMissionTreeId);
            Assert.True(roundTripped.IsLoopRoute);
        }

        // catches: the cadence multiplier (Phase 6) not round-tripping, or the
        // sparse default (N=1) being written to / read off the wire as non-default.
        [Fact]
        public void RoundTrip_CadenceMultiplier_SparseDefaultOne()
        {
            var leanStop = new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0
            };

            // N == 1 (default) writes NO cadenceMultiplier value and loads back 1.
            var defaultRoute = new RouteFixtureBuilder()
                .WithId("cadence-default-route")
                .WithOrigin(BuildKscOrigin())
                .WithStop(leanStop)
                .WithCadenceMultiplier(1)
                .Build();
            var defNode = new ConfigNode("ROUTE");
            defaultRoute.SerializeInto(defNode);
            Assert.False(defNode.HasValue("cadenceMultiplier"),
                "cadenceMultiplier must be omitted when 1 (the floor / default)");
            Route defLoaded = Route.DeserializeFrom(defNode);
            Assert.NotNull(defLoaded);
            Assert.Equal(1, defLoaded.CadenceMultiplier);

            // N > 1 round-trips exactly.
            var raisedRoute = new RouteFixtureBuilder()
                .WithId("cadence-raised-route")
                .WithOrigin(BuildKscOrigin())
                .WithStop(leanStop)
                .WithCadenceMultiplier(4)
                .Build();
            var raisedNode = new ConfigNode("ROUTE");
            raisedRoute.SerializeInto(raisedNode);
            Assert.True(raisedNode.HasValue("cadenceMultiplier"),
                "cadenceMultiplier must be written when > 1");
            Route raisedLoaded = Route.DeserializeFrom(raisedNode);
            Assert.NotNull(raisedLoaded);
            Assert.Equal(4, raisedLoaded.CadenceMultiplier);
        }

        // catches: a hand-edited save with a 0 / negative cadence multiplier
        // landing a sub-floor value instead of being clamped up to 1 on load.
        [Fact]
        public void Load_SubFloorCadenceMultiplier_ClampedToOne()
        {
            var node = new ConfigNode("ROUTE");
            node.AddValue("id", "bad-cadence-route");
            node.AddValue("status", "Active");
            node.AddValue("cadenceMultiplier", "0");
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
            Assert.Equal(1, route.CadenceMultiplier);
        }

        // catches: an old-save node with no backing-mission keys failing to load
        // or loading non-default backing-mission state.
        [Fact]
        public void Load_OldSaveWithoutBackingMission_GracefulDefault()
        {
            // Hand-author a pre-backing-mission ROUTE node (valid origin + stop,
            // no backing-mission keys at all).
            var node = new ConfigNode("ROUTE");
            node.AddValue("id", "old-save-route");
            node.AddValue("status", "Active");
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
            Assert.Null(route.BackingMissionTreeId);
            Assert.Empty(route.ExcludedIntervalKeys);
            Assert.Null(route.DockMemberRecordingId);
            Assert.Equal(-1.0, route.RecordedDockUT);
            Assert.Equal(-1.0, route.LoopAnchorUT);
            Assert.Equal(-1L, route.LastObservedLoopCycleIndex);
            // No cadenceMultiplier key on an old save -> the floor (1).
            Assert.Equal(1, route.CadenceMultiplier);
            Assert.False(route.IsLoopRoute);
        }

        // catches (LST-2): the pre-missing baseline not round-tripping, OR the
        // sparse default (Active) being written to / read off the wire as
        // non-default. A MissingSourceRecording route that remembers a deliberate
        // Paused baseline must keep it across a save/reload so recovery comes back
        // Paused, not Active.
        [Fact]
        public void RoundTrip_PreMissingStatus_SparseAndPaused()
        {
            var leanStop = new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0
            };

            // Default (Active) pre-missing baseline writes NO value and loads Active.
            var defaultRoute = new RouteFixtureBuilder()
                .WithId("premissing-default-route")
                .WithOrigin(BuildKscOrigin())
                .WithStop(leanStop)
                .Build();
            Assert.Equal(RouteStatus.Active, defaultRoute.PreMissingStatus);
            var defNode = new ConfigNode("ROUTE");
            defaultRoute.SerializeInto(defNode);
            Assert.False(defNode.HasValue("preMissingStatus"),
                "preMissingStatus must be omitted when Active (the sentinel default)");
            Route defLoaded = Route.DeserializeFrom(defNode);
            Assert.NotNull(defLoaded);
            Assert.Equal(RouteStatus.Active, defLoaded.PreMissingStatus);

            // A route parked in MissingSourceRecording that remembers a deliberate
            // Paused baseline round-trips both fields exactly.
            var missingRoute = new RouteFixtureBuilder()
                .WithId("premissing-paused-route")
                .WithStatus(RouteStatus.MissingSourceRecording)
                .WithOrigin(BuildKscOrigin())
                .WithStop(leanStop)
                .Build();
            missingRoute.PreMissingStatus = RouteStatus.Paused;
            var missingNode = new ConfigNode("ROUTE");
            missingRoute.SerializeInto(missingNode);
            Assert.True(missingNode.HasValue("preMissingStatus"),
                "preMissingStatus must be written when not Active");
            Assert.Equal("Paused", missingNode.GetValue("preMissingStatus"));
            Route missingLoaded = Route.DeserializeFrom(missingNode);
            Assert.NotNull(missingLoaded);
            Assert.Equal(RouteStatus.MissingSourceRecording, missingLoaded.Status);
            Assert.Equal(RouteStatus.Paused, missingLoaded.PreMissingStatus);
        }

        // catches (LST-2): an old save with no preMissingStatus key loading a
        // non-default baseline instead of the Active sentinel.
        [Fact]
        public void Load_OldSaveWithoutPreMissingStatus_DefaultsActive()
        {
            var node = new ConfigNode("ROUTE");
            node.AddValue("id", "old-no-premissing-route");
            node.AddValue("status", "Paused");
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
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Equal(RouteStatus.Active, route.PreMissingStatus);
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
                l.Contains("[Route]") &&
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
                l.Contains("[Route]") &&
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
                l.Contains("[Route]") &&
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

    /// <summary>
    /// LST-2 recovery-target tests for <see cref="RouteStore.RevalidateSources"/>.
    /// A route flipped to <see cref="RouteStatus.MissingSourceRecording"/> must
    /// remember the status it held BEFORE the flip and return to it (not always
    /// Active) when its sources come back into ERS, so a deliberately Paused
    /// route comes back Paused. Lives in this file (Fix Agent C's owned test set)
    /// rather than RouteStoreValidationTests; mirrors that file's ERS-driving
    /// harness (RecordingStore + installed ParsekScenario -> EffectiveState.ComputeERS).
    /// </summary>
    [Collection("Sequential")]
    public class RoutePreMissingStatusRecoveryTests : System.IDisposable
    {
        private readonly System.Collections.Generic.List<string> logLines =
            new System.Collections.Generic.List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool? priorVerbose;

        public RoutePreMissingStatusRecoveryTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorVerbose = ParsekLog.VerboseOverrideForTesting;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();

            logLines.Clear();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekLog.VerboseOverrideForTesting = priorVerbose;

            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // -----------------------------------------------------------------
        // Fixture helpers (mirror RouteStoreValidationTests)
        // -----------------------------------------------------------------

        private static void InstallScenario()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new System.Collections.Generic.List<RecordingSupersedeRelation>(),
                LedgerTombstones = new System.Collections.Generic.List<LedgerTombstone>(),
                RewindPoints = new System.Collections.Generic.List<RewindPoint>(),
                ActiveReFlySessionMarker = null
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
        }

        private static Recording BuildRouteSourceRecording(string id, int sidecarEpoch = 1)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TreeId = "tree-" + id,
                TreeOrder = 0,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = sidecarEpoch,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 500.0,
                RouteConnectionWindows = new System.Collections.Generic.List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-" + id,
                        DockUT = 150.0,
                        UndockUT = 450.0,
                        TransferTargetVesselPid = 9999u,
                        TransferKind = RouteConnectionKind.DockingPort,
                        DockTransportResources = new System.Collections.Generic.Dictionary<string, ResourceAmount>
                        {
                            { "LiquidFuel", new ResourceAmount { amount = 1000.0, maxAmount = 1000.0 } }
                        }
                    }
                },
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 7777u
                }
            };
        }

        private static RouteSourceRef BuildMatchingSourceRef(Recording rec)
        {
            return new RouteSourceRef
            {
                RecordingId = rec.RecordingId,
                TreeId = rec.TreeId,
                TreeOrder = rec.TreeOrder,
                RecordingFormatVersion = rec.RecordingFormatVersion,
                RecordingSchemaGeneration = rec.RecordingSchemaGeneration,
                SidecarEpoch = rec.SidecarEpoch,
                StartUT = rec.StartUT,
                EndUT = rec.EndUT,
                RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec)
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

        private static RouteStop BuildStop()
        {
            return new RouteStop
            {
                Endpoint = new RouteEndpoint
                {
                    BodyName = "Mun",
                    Latitude = 3.2001,
                    Longitude = -45.1234,
                    Altitude = 612.5,
                    VesselPersistentId = 67890,
                    IsSurface = true
                },
                ConnectionKind = RouteConnectionKind.DockingPort,
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0,
                DeliveryManifest = new System.Collections.Generic.Dictionary<string, double> { { "LiquidFuel", 100.0 } }
            };
        }

        private static Route BuildRoute(string id, RouteStatus status, RouteSourceRef sourceRef)
        {
            return new RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(status)
                .WithOrigin(BuildKscOrigin())
                .WithStop(BuildStop())
                .WithRecordingId(sourceRef.RecordingId)
                .WithSourceRef(sourceRef)
                .Build();
        }

        // -----------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------

        // catches (LST-2 core): a deliberately Paused route whose sources flicker
        // out and back must come back Paused, NOT silently un-paused to Active.
        [Fact]
        public void PausedThroughMissingAndBack_RecoversToPaused()
        {
            var rec = BuildRouteSourceRecording("rec-paused-flicker");
            var sourceRef = BuildMatchingSourceRef(rec);

            // Start the route Paused with its source recording absent from the
            // store (flickered out). First pass: capture Paused + flip to Missing.
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-paused-flicker", RouteStatus.Paused, sourceRef));
            logLines.Clear();

            int firstPass = RouteStore.RevalidateSources("flicker-out");

            Assert.Equal(1, firstPass);
            Assert.True(RouteStore.TryGetRoute("route-paused-flicker", out Route afterMissing));
            Assert.Equal(RouteStatus.MissingSourceRecording, afterMissing.Status);
            Assert.Equal(RouteStatus.Paused, afterMissing.PreMissingStatus);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[Route]")
                && l.Contains("capturing preMissingStatus=Paused"));

            // Source flickers back in. Second pass: recover -> the captured Paused.
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();
            logLines.Clear();

            int secondPass = RouteStore.RevalidateSources("flicker-in");

            Assert.Equal(1, secondPass);
            Assert.True(RouteStore.TryGetRoute("route-paused-flicker", out Route afterRecover));
            Assert.Equal(RouteStatus.Paused, afterRecover.Status);
            // Baseline cleared back to the Active sentinel after recovery.
            Assert.Equal(RouteStatus.Active, afterRecover.PreMissingStatus);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("MissingSourceRecording")
                && l.Contains("Paused")
                && l.Contains("source-restored")
                && l.Contains("preMissing=Paused"));
        }

        // catches: an Active route over-corrected to Paused by the new baseline
        // logic. Active in -> Missing -> Active out (the original contract).
        [Fact]
        public void ActiveThroughMissingAndBack_RecoversToActive()
        {
            var rec = BuildRouteSourceRecording("rec-active-flicker");
            var sourceRef = BuildMatchingSourceRef(rec);

            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-active-flicker", RouteStatus.Active, sourceRef));
            logLines.Clear();

            RouteStore.RevalidateSources("flicker-out");
            Assert.True(RouteStore.TryGetRoute("route-active-flicker", out Route afterMissing));
            Assert.Equal(RouteStatus.MissingSourceRecording, afterMissing.Status);
            // Active baseline == the sentinel default, so nothing is captured.
            Assert.Equal(RouteStatus.Active, afterMissing.PreMissingStatus);

            RecordingStore.AddRecordingWithTreeForTesting(rec);
            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();
            logLines.Clear();

            int secondPass = RouteStore.RevalidateSources("flicker-in");

            Assert.Equal(1, secondPass);
            Assert.True(RouteStore.TryGetRoute("route-active-flicker", out Route afterRecover));
            Assert.Equal(RouteStatus.Active, afterRecover.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Active")
                && l.Contains("source-restored"));
        }

        // catches: SourceChanged being treated as a recovery target. The into-missing
        // capture excludes source-problem statuses, and a load that hand-seeds
        // PreMissingStatus=SourceChanged must still recover to Active (the safe
        // sentinel), never auto-flip back into SourceChanged (design §7.4).
        [Fact]
        public void SourceChangedBaseline_NeverRecoversToSourceChanged()
        {
            var rec = BuildRouteSourceRecording("rec-sc-baseline");
            var sourceRef = BuildMatchingSourceRef(rec);

            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario();

            // A route already Missing on load, with a (defensive) hand-seeded
            // SourceChanged baseline. Recovery must land Active, not SourceChanged.
            var route = BuildRoute("route-sc-baseline", RouteStatus.MissingSourceRecording, sourceRef);
            route.PreMissingStatus = RouteStatus.SourceChanged;
            RouteStore.AddRoute(route);
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("recover");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-sc-baseline", out Route resolved));
            // The production capture path can never seed SourceChanged as a baseline;
            // a hand-edited / corrupt save that does is guarded: recovery falls back to
            // Active (never auto-flips INTO SourceChanged, design §7.4) and warns.
            Assert.Equal(RouteStatus.Active, resolved.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[Route]")
                && l.Contains("invalid preMissingStatus=SourceChanged")
                && l.Contains("falling back to Active"));
        }

        // catches: re-capturing / clobbering the baseline on a repeated Missing pass.
        // A route that stays Missing across two passes must keep its FIRST captured
        // baseline (Paused), not overwrite it with the Missing status itself.
        [Fact]
        public void RepeatedMissingPass_KeepsFirstBaseline()
        {
            var sourceRef = new RouteSourceRef
            {
                RecordingId = "rec-never-there",
                RouteProofHash = "deadbeef00000000"
            };

            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-repeat-missing", RouteStatus.Paused, sourceRef));

            RouteStore.RevalidateSources("pass-1");
            Assert.True(RouteStore.TryGetRoute("route-repeat-missing", out Route afterFirst));
            Assert.Equal(RouteStatus.MissingSourceRecording, afterFirst.Status);
            Assert.Equal(RouteStatus.Paused, afterFirst.PreMissingStatus);

            // Second pass: still missing (recording never added). Baseline unchanged.
            int secondPass = RouteStore.RevalidateSources("pass-2");
            Assert.Equal(0, secondPass);
            Assert.True(RouteStore.TryGetRoute("route-repeat-missing", out Route afterSecond));
            Assert.Equal(RouteStatus.MissingSourceRecording, afterSecond.Status);
            Assert.Equal(RouteStatus.Paused, afterSecond.PreMissingStatus);
        }
    }
}
