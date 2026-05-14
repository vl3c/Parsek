using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RouteProofSerializationTests : System.IDisposable
    {
        public RouteProofSerializationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        [Fact]
        public void RouteProof_RoundTripsViaTreeMetadata()
        {
            var rec = BuildRecordingWithRouteProof();

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording { RecordingId = "route-proof-tree" };
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.Equal(9001u, loaded.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, loaded.TransferKind);
            Assert.NotNull(loaded.RouteOriginProof);
            Assert.Equal(7007u, loaded.RouteOriginProof.StartDockedOriginVesselPid);
            Assert.Equal(120.0, loaded.RouteOriginProof.StartTransportResources["LiquidFuel"].amount);
            Assert.Equal("evaJetpack", loaded.RouteOriginProof.StartTransportInventory[0].PartName);
            Assert.Equal("STOREDPART", loaded.RouteOriginProof.StartTransportInventory[0].StoredPartSnapshot.name);

            Assert.NotNull(loaded.RouteConnectionWindows);
            Assert.Single(loaded.RouteConnectionWindows);
            RouteConnectionWindow window = loaded.RouteConnectionWindows[0];
            Assert.Equal("window-1", window.WindowId);
            Assert.Equal(100.0, window.DockUT);
            Assert.Equal(180.0, window.UndockUT);
            Assert.True(window.IsComplete);
            Assert.Equal(9001u, window.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, window.TransferKind);
            Assert.Equal(new List<uint> { 11u, 12u }, window.TransportPartPersistentIds);
            Assert.Equal(new List<uint> { 21u, 22u }, window.EndpointPartPersistentIds);
            Assert.Equal(75.0, window.DockTransportResources["LiquidFuel"].amount);
            Assert.Equal(25.0, window.UndockTransportResources["LiquidFuel"].amount);
            Assert.Equal(0.0, window.DockEndpointResources["LiquidFuel"].amount);
            Assert.Equal(50.0, window.UndockEndpointResources["LiquidFuel"].amount);
            Assert.True(window.EndpointAtDock.HasValue);
            Assert.Equal("Mun", window.EndpointAtDock.Value.BodyName);
            Assert.True(window.EndpointAtDock.Value.IsSurface);
            Assert.Equal(4, window.TransferEndpointSituation);

            InventoryPayloadItem payload = window.DockTransportInventory[0];
            Assert.Equal("payload-hash", payload.IdentityHash);
            Assert.Equal("evaJetpack", payload.PartName);
            Assert.Equal("white", payload.VariantName);
            Assert.Equal(2, payload.Quantity);
            Assert.Equal(1, payload.SlotsTaken);
            Assert.Equal(5.0, payload.StoredResources["MonoPropellant"].amount);
            Assert.NotNull(payload.StoredPartSnapshot);
            Assert.Equal("STOREDPART", payload.StoredPartSnapshot.name);
            Assert.Equal("evaJetpack", payload.StoredPartSnapshot.GetValue("partName"));
        }

        [Fact]
        public void RouteProof_RoundTripsViaScenarioMetadata()
        {
            var rec = new Recording
            {
                RecordingId = "route-proof-scenario",
                TransferTargetVesselPid = 42,
                TransferKind = RouteConnectionKind.DockingPort,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "scenario-window",
                        DockUT = 10.0,
                        UndockUT = 20.0,
                        TransferTargetVesselPid = 42,
                        TransferKind = RouteConnectionKind.DockingPort
                    }
                }
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, rec);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadataForTests(node, loaded);

            Assert.Equal(42u, loaded.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.DockingPort, loaded.TransferKind);
            Assert.Single(loaded.RouteConnectionWindows);
            Assert.Equal("scenario-window", loaded.RouteConnectionWindows[0].WindowId);
        }

        [Fact]
        public void RouteProof_MissingMetadataDefaultsToNoProof()
        {
            var node = new ConfigNode("RECORDING");
            var loaded = new Recording();

            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.Equal(0u, loaded.TransferTargetVesselPid);
            Assert.Equal(RouteConnectionKind.None, loaded.TransferKind);
            Assert.Null(loaded.RouteOriginProof);
            Assert.Null(loaded.RouteConnectionWindows);
        }

        [Fact]
        public void RecordingDeepClone_RouteProofIsIndependent()
        {
            var source = BuildRecordingWithRouteProof();

            Recording clone = Recording.DeepClone(source);

            source.RouteOriginProof.StartTransportResources["LiquidFuel"] =
                new ResourceAmount { amount = 1.0, maxAmount = 1.0 };
            source.RouteConnectionWindows[0].TransportPartPersistentIds.Add(99);
            source.RouteConnectionWindows[0].DockTransportInventory[0].StoredPartSnapshot
                .AddValue("mutated", "true");

            Assert.Equal(120.0, clone.RouteOriginProof.StartTransportResources["LiquidFuel"].amount);
            Assert.Equal(new List<uint> { 11u, 12u }, clone.RouteConnectionWindows[0].TransportPartPersistentIds);
            Assert.Null(clone.RouteConnectionWindows[0].DockTransportInventory[0]
                .StoredPartSnapshot.GetValue("mutated"));
        }

        private static Recording BuildRecordingWithRouteProof()
        {
            var storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", "evaJetpack");
            storedPart.AddValue("variant", "white");

            var payload = new InventoryPayloadItem
            {
                IdentityHash = "payload-hash",
                PartName = "evaJetpack",
                VariantName = "white",
                Quantity = 2,
                SlotsTaken = 1,
                StoredResources = new Dictionary<string, ResourceAmount>
                {
                    ["MonoPropellant"] = new ResourceAmount { amount = 5.0, maxAmount = 5.0 }
                },
                StoredPartSnapshot = storedPart
            };

            return new Recording
            {
                RecordingId = "route-proof",
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 7007,
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = new ResourceAmount { amount = 120.0, maxAmount = 120.0 }
                    },
                    EndTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = new ResourceAmount { amount = 20.0, maxAmount = 120.0 }
                    },
                    StartTransportInventory = new List<InventoryPayloadItem> { payload.DeepClone() },
                    EndTransportInventory = new List<InventoryPayloadItem>()
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "window-1",
                        DockUT = 100.0,
                        UndockUT = 180.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { 11, 12 },
                        EndpointPartPersistentIds = new List<uint> { 21, 22 },
                        DockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = new ResourceAmount { amount = 75.0, maxAmount = 100.0 }
                        },
                        UndockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = new ResourceAmount { amount = 25.0, maxAmount = 100.0 }
                        },
                        DockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = new ResourceAmount { amount = 0.0, maxAmount = 200.0 }
                        },
                        UndockEndpointResources = new Dictionary<string, ResourceAmount>
                        {
                            ["LiquidFuel"] = new ResourceAmount { amount = 50.0, maxAmount = 200.0 }
                        },
                        DockTransportInventory = new List<InventoryPayloadItem> { payload.DeepClone() },
                        UndockTransportInventory = new List<InventoryPayloadItem>(),
                        DockEndpointInventory = new List<InventoryPayloadItem>(),
                        UndockEndpointInventory = new List<InventoryPayloadItem> { payload.DeepClone() },
                        EndpointAtDock = new RouteEndpoint
                        {
                            VesselPersistentId = 9001,
                            BodyName = "Mun",
                            Latitude = 1.25,
                            Longitude = 2.5,
                            Altitude = 123.0,
                            IsSurface = true
                        },
                        TransferEndpointSituation = 4
                    }
                }
            };
        }
    }
}
